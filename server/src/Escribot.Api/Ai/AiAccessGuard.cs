using System.Globalization;
using System.Text.Json.Nodes;
using Escribot.Api.Api;
using Escribot.Api.Auth;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Ai;

public sealed record AiGuardOutcome(IResult? Blocked, ResolvedAiConfig? Config, IDisposable? Reservation = null) : IDisposable
{
    public void Dispose() => Reservation?.Dispose();
}

/// <summary>
/// Tracks in-flight platform AI quota units per user/month and per day (Free daily cap),
/// serializing check-and-reserve against release so concurrent requests cannot race past
/// monthly quotas or the Free-plan daily cap.
/// </summary>
public sealed class AiQuotaTracker
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string UserId, AiRequestKind Kind, string MonthKey), int> _inFlightMonthly = [];
    private readonly Dictionary<string, int> _inFlightFreeDaily = [];

    internal async Task<T> RunUnderGateAsync<T>(
        Func<QuotaGate, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action(new QuotaGate(this));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Release(
        (string UserId, AiRequestKind Kind, string MonthKey)? monthlyKey,
        string? freeDailyKey,
        int units)
    {
        _gate.Wait();
        try
        {
            if (monthlyKey is { } mk && _inFlightMonthly.TryGetValue(mk, out var m))
            {
                var next = m - units;
                if (next <= 0)
                {
                    _inFlightMonthly.Remove(mk);
                }
                else
                {
                    _inFlightMonthly[mk] = next;
                }
            }

            if (freeDailyKey is not null && _inFlightFreeDaily.TryGetValue(freeDailyKey, out var d))
            {
                var next = d - units;
                if (next <= 0)
                {
                    _inFlightFreeDaily.Remove(freeDailyKey);
                }
                else
                {
                    _inFlightFreeDaily[freeDailyKey] = next;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal readonly struct QuotaGate(AiQuotaTracker tracker)
    {
        public int GetInFlightFreeDaily(string dayKey) =>
            tracker._inFlightFreeDaily.GetValueOrDefault(dayKey);

        public int GetInFlightMonthly(string userId, AiRequestKind kind, string monthKey) =>
            tracker._inFlightMonthly.GetValueOrDefault((userId, kind, monthKey));

        public IDisposable Reserve(
            (string UserId, AiRequestKind Kind, string MonthKey)? monthlyKey,
            string? freeDailyKey,
            int units)
        {
            if (monthlyKey is { } mk)
            {
                tracker._inFlightMonthly[mk] = tracker._inFlightMonthly.GetValueOrDefault(mk) + units;
            }

            if (freeDailyKey is not null)
            {
                tracker._inFlightFreeDaily[freeDailyKey] = tracker._inFlightFreeDaily.GetValueOrDefault(freeDailyKey) + units;
            }

            return new Reservation(tracker, monthlyKey, freeDailyKey, units);
        }
    }

    private sealed class Reservation(
        AiQuotaTracker tracker,
        (string UserId, AiRequestKind Kind, string MonthKey)? monthlyKey,
        string? freeDailyKey,
        int units) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                tracker.Release(monthlyKey, freeDailyKey, units);
            }
        }
    }
}

/// <summary>
/// Enforces per-user AI rate limits, Free-plan verification &amp; daily cap, and monthly quotas.
/// </summary>
public sealed class AiAccessGuard(
    IRateLimiter rateLimiter,
    AiConfigResolver configResolver,
    IAiUsageRepository usageRepo,
    IFreeAiDailyCounter freeDailyCounter,
    AuthSettings authSettings,
    IConfiguration configuration,
    IHostEnvironment environment,
    TimeProvider time,
    AiQuotaTracker? quotaTracker = null)
{
    public const int AiRequestsPerMinute = 20;

    private readonly AiQuotaTracker _quotaTracker = quotaTracker ?? new AiQuotaTracker();

    public async Task<AiGuardOutcome> GuardWithConfigAsync(
        UserAccount account,
        AiRequestKind kind,
        int units = 1,
        CancellationToken cancellationToken = default)
    {
        var rate = await rateLimiter.CheckAsync($"ai:{account.Id}", AiRequestsPerMinute, TimeSpan.FromMinutes(1), cancellationToken);
        if (!rate.Allowed)
        {
            return new AiGuardOutcome(
                TooManyRequests(
                    "You are sending AI requests too quickly. Please wait a moment and try again.",
                    "rate_limited",
                    rate.RetryAfterSeconds),
                null);
        }

        var cfg = await configResolver.ResolveAsync(account, cancellationToken);
        // Own key: the user pays. No key at all: features fall back to local heuristics.
        if (cfg.KeySource != "platform")
        {
            return new AiGuardOutcome(null, cfg);
        }

        var billingEnabled = Plans.IsBillingEnabled(configuration);

        if (cfg.Plan == Plans.Free && !UsageService.IsTrustedForFreeUse(account, authSettings, environment))
        {
            return new AiGuardOutcome(
                Results.Json(
                    new JsonObject
                    {
                        ["error"] = "Please verify your email address to use AI feedback on the Free plan, or add your own API key in Settings.",
                        ["code"] = "verification_required",
                    },
                    statusCode: StatusCodes.Status403Forbidden),
                null);
        }

        var dailyCap = cfg.Plan == Plans.Free ? GetFreeDailyCap() : null;
        var limits = Plans.Limits[cfg.Plan];
        var limit = kind == AiRequestKind.Evaluation ? limits.EvaluationsPerMonth : limits.AssistsPerMonth;
        if (dailyCap is null && limit is null)
        {
            return new AiGuardOutcome(null, cfg);
        }

        return await _quotaTracker.RunUnderGateAsync(async gate =>
        {
            var now = time.GetUtcNow();
            var dayKey = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (dailyCap is { } cap)
            {
                var usedToday = await freeDailyCounter.GetTodayAsync(cancellationToken);
                var inFlightToday = gate.GetInFlightFreeDaily(dayKey);
                if (usedToday + inFlightToday + units > cap)
                {
                    return new AiGuardOutcome(
                        TooManyRequests(
                            "Free AI feedback is at capacity for today. Please try again tomorrow, upgrade to Pro, or add your own API key in Settings.",
                            "free_capacity_reached",
                            upgradeUrl: billingEnabled ? "/pricing" : null),
                        null);
                }
            }

            (string UserId, AiRequestKind Kind, string MonthKey)? monthlyKey = null;
            if (limit is { } monthlyLimit)
            {
                var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
                var nextMonth = monthStart.AddMonths(1);
                var monthKey = $"{now.Year:D4}-{now.Month:D2}";
                var features = kind == AiRequestKind.Evaluation
                    ? UsageService.EvaluationFeatures
                    : UsageService.AssistFeatures;

                var used = await usageRepo.CountAsync(account.Id, monthStart, features, "platform", cancellationToken);
                var effectiveUsed = used + gate.GetInFlightMonthly(account.Id, kind, monthKey);
                if (effectiveUsed + units > monthlyLimit)
                {
                    var resetDate = nextMonth.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var what = kind == AiRequestKind.Evaluation ? "graded essays" : "AI coaching requests";
                    var remaining = Math.Max(0, monthlyLimit - effectiveUsed);
                    var detail = remaining > 0 && units > 1
                        ? $"This submission needs {units} but only {remaining} of your {monthlyLimit} monthly {what} remain."
                        : $"You have used all {monthlyLimit} {what} included in the {limits.Label} plan this month.";

                    var canUpgrade = cfg.Plan == Plans.Free && billingEnabled;
                    var pro = Plans.Limits[Plans.Pro];
                    var nextStep = canUpgrade
                        ? $"To keep going now, upgrade to Pro ({(kind == AiRequestKind.Evaluation ? pro.EvaluationsPerMonth : pro.AssistsPerMonth)} {what} a month) on the Pricing page, or add your own API key in Settings."
                        : "To keep going now, add your own API key in Settings.";

                    return new AiGuardOutcome(
                        TooManyRequests(
                            $"{detail} Your allowance resets on {resetDate}. {nextStep}",
                            "quota_exceeded",
                            upgradeUrl: canUpgrade ? "/pricing" : null),
                        null);
                }

                monthlyKey = (account.Id, kind, monthKey);
            }

            var freeDailyKey = dailyCap is not null ? dayKey : null;
            var reservation = gate.Reserve(monthlyKey, freeDailyKey, units);
            return new AiGuardOutcome(null, cfg, reservation);
        }, cancellationToken);
    }

    public async Task<IResult?> GuardAsync(
        UserAccount account,
        AiRequestKind kind,
        int units = 1,
        CancellationToken cancellationToken = default)
    {
        using var outcome = await GuardWithConfigAsync(account, kind, units, cancellationToken);
        return outcome.Blocked;
    }

    private int? GetFreeDailyCap()
    {
        var raw = configuration["FREE_PLAN_DAILY_AI_CAP"]?.Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static IResult TooManyRequests(string error, string code, int? retryAfterSeconds = null, string? upgradeUrl = null)
    {
        var payload = new JsonObject
        {
            ["error"] = error,
            ["code"] = code,
        };
        if (upgradeUrl is not null)
        {
            payload["upgradeUrl"] = upgradeUrl;
        }

        if (retryAfterSeconds is { } seconds)
        {
            return new RetryAfterJsonResult(payload, seconds);
        }

        return Results.Json(payload, statusCode: StatusCodes.Status429TooManyRequests);
    }

    private sealed class RetryAfterJsonResult(JsonObject payload, int retryAfterSeconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return httpContext.Response.WriteAsJsonAsync(payload, httpContext.RequestAborted);
        }
    }
}
