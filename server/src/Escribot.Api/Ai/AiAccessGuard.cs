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
/// Tracks in-flight platform AI quota units per user/month and per day (Free daily cap), so
/// concurrent requests cannot race past monthly quotas or the Free-plan daily cap.
/// </summary>
/// <remarks>
/// A request reserves its units first, then reads stored usage, then checks. The lock only
/// guards the in-memory counts and is never held across I/O. Each reservation remembers how
/// many units were already in flight when it was taken, and checks against stored usage plus
/// those. Of any two admitted requests, the later one therefore counts the earlier one, either
/// while it is in flight or once it has recorded its usage, so admissions never exceed the
/// limit. A request that finishes between another's reserve and read is counted twice, which
/// can only deny, never over-admit. The counts live in this process: this holds while the API
/// runs as a single replica (<c>maxReplicas: 1</c> in <c>infra/api/api-containerapp.bicep</c>).
/// </remarks>
public sealed class AiQuotaTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<MonthlyKey, int> _inFlightMonthly = [];
    private readonly Dictionary<string, int> _inFlightFreeDaily = [];

    internal Reservation Reserve(MonthlyKey? monthlyKey, string? freeDailyKey, int units)
    {
        lock (_lock)
        {
            var monthlyAhead = monthlyKey is { } mk ? Add(_inFlightMonthly, mk, units) : 0;
            var dailyAhead = freeDailyKey is not null ? Add(_inFlightFreeDaily, freeDailyKey, units) : 0;
            return new Reservation(this, monthlyKey, freeDailyKey, units, monthlyAhead, dailyAhead);
        }
    }

    private void Release(MonthlyKey? monthlyKey, string? freeDailyKey, int units)
    {
        lock (_lock)
        {
            if (monthlyKey is { } mk)
            {
                Subtract(_inFlightMonthly, mk, units);
            }

            if (freeDailyKey is not null)
            {
                Subtract(_inFlightFreeDaily, freeDailyKey, units);
            }
        }
    }

    /// <summary>Adds <paramref name="units"/> and answers what was in flight before.</summary>
    private static int Add<TKey>(Dictionary<TKey, int> counts, TKey key, int units)
        where TKey : notnull
    {
        var ahead = counts.GetValueOrDefault(key);
        counts[key] = ahead + units;
        return ahead;
    }

    private static void Subtract<TKey>(Dictionary<TKey, int> counts, TKey key, int units)
        where TKey : notnull
    {
        if (!counts.TryGetValue(key, out var current))
        {
            return;
        }

        var next = current - units;
        if (next <= 0)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = next;
        }
    }

    internal readonly record struct MonthlyKey(string UserId, AiRequestKind Kind, string MonthKey);

    internal sealed class Reservation(
        AiQuotaTracker tracker,
        MonthlyKey? monthlyKey,
        string? freeDailyKey,
        int units,
        int monthlyAhead,
        int dailyAhead) : IDisposable
    {
        private int _released;

        /// <summary>Units of this user's monthly quota already in flight when this was reserved.</summary>
        public int MonthlyAhead { get; } = monthlyAhead;

        /// <summary>Units of the Free daily cap already in flight when this was reserved.</summary>
        public int DailyAhead { get; } = dailyAhead;

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
    AiQuotaTracker quotaTracker,
    PlanCatalog planCatalog)
{
    public const int AiRequestsPerMinute = 20;

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
        var limits = planCatalog[cfg.Plan];
        var limit = kind == AiRequestKind.Evaluation ? limits.EvaluationsPerMonth : limits.AssistsPerMonth;
        if (dailyCap is null && limit is null)
        {
            return new AiGuardOutcome(null, cfg);
        }

        var now = time.GetUtcNow();
        var dayKey = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var monthlyKey = limit is null
            ? (AiQuotaTracker.MonthlyKey?)null
            : new AiQuotaTracker.MonthlyKey(account.Id, kind, $"{now.Year:D4}-{now.Month:D2}");

        // Reserve before reading stored usage; see AiQuotaTracker for why this cannot over-admit.
        var reservation = quotaTracker.Reserve(monthlyKey, dailyCap is null ? null : dayKey, units);
        var admitted = false;
        try
        {
            if (dailyCap is { } cap)
            {
                var usedToday = await freeDailyCounter.GetTodayAsync(cancellationToken);
                if (usedToday + reservation.DailyAhead + units > cap)
                {
                    return new AiGuardOutcome(
                        TooManyRequests(
                            "Free AI feedback is at capacity for today. Please try again tomorrow, upgrade to Pro, or add your own API key in Settings.",
                            "free_capacity_reached",
                            upgradeUrl: billingEnabled ? "/pricing" : null),
                        null);
                }
            }

            if (limit is { } monthlyLimit)
            {
                var features = kind == AiRequestKind.Evaluation
                    ? UsageService.EvaluationFeatures
                    : UsageService.AssistFeatures;

                var used = await usageRepo.CountAsync(account.Id, monthStart, features, "platform", cancellationToken);
                var effectiveUsed = used + reservation.MonthlyAhead;
                if (effectiveUsed + units > monthlyLimit)
                {
                    var resetDate = monthStart.AddMonths(1).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var what = kind == AiRequestKind.Evaluation ? "graded essays" : "AI coaching requests";
                    var remaining = Math.Max(0, monthlyLimit - effectiveUsed);
                    var detail = remaining > 0 && units > 1
                        ? $"This submission needs {units} but only {remaining} of your {monthlyLimit} monthly {what} remain."
                        : $"You have used all {monthlyLimit} {what} included in the {limits.Label} plan this month.";

                    var canUpgrade = cfg.Plan == Plans.Free && billingEnabled;
                    var pro = planCatalog[Plans.Pro];
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
            }

            admitted = true;
            return new AiGuardOutcome(null, cfg, reservation);
        }
        finally
        {
            if (!admitted)
            {
                reservation.Dispose();
            }
        }
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
