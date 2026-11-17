using System.Globalization;
using System.Text.Json.Nodes;
using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Common;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Ai;

/// <summary>
/// Enforces per-user AI rate limits, Free-plan verification &amp; daily cap, and monthly quotas.
/// Ported from <c>guardAiRequest</c> in <c>src/lib/ai/access.ts</c>.
/// </summary>
public sealed class AiAccessGuard(
    IRateLimiter rateLimiter,
    AiConfigResolver configResolver,
    IAiUsageRepository usageRepo,
    IFreeAiDailyCounter freeDailyCounter,
    AuthSettings authSettings,
    IConfiguration configuration,
    IHostEnvironment environment,
    TimeProvider time)
{
    public const int AiRequestsPerMinute = 20;

    public async Task<IResult?> GuardAsync(
        UserAccount account,
        AiRequestKind kind,
        int units = 1,
        CancellationToken cancellationToken = default)
    {
        var rate = await rateLimiter.CheckAsync($"ai:{account.Id}", AiRequestsPerMinute, TimeSpan.FromMinutes(1), cancellationToken);
        if (!rate.Allowed)
        {
            return TooManyRequests(
                "You are sending AI requests too quickly. Please wait a moment and try again.",
                "rate_limited",
                rate.RetryAfterSeconds);
        }

        var cfg = await configResolver.ResolveAsync(account, cancellationToken);
        // Own key: the user pays. No key at all: features fall back to local heuristics.
        if (cfg.KeySource != "platform")
        {
            return null;
        }

        var billingEnabled = IsBillingEnabled();

        if (cfg.Plan == Plans.Free)
        {
            var trusted = !environment.IsProduction() || account.EmailVerified || authSettings.IsInvited(account.Email);
            if (!trusted)
            {
                return Results.Json(
                    new JsonObject
                    {
                        ["error"] = "Please verify your email address to use AI feedback on the Free plan, or add your own API key in Settings.",
                        ["code"] = "verification_required",
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (GetFreeDailyCap() is { } dailyCap)
            {
                var usedToday = await freeDailyCounter.GetTodayAsync(cancellationToken);
                if (usedToday + units > dailyCap)
                {
                    return TooManyRequests(
                        "Free AI feedback is at capacity for today. Please try again tomorrow, upgrade to Pro, or add your own API key in Settings.",
                        "free_capacity_reached",
                        upgradeUrl: billingEnabled ? "/pricing" : null);
                }
            }
        }

        var limits = Plans.Limits[cfg.Plan];
        var limit = kind == AiRequestKind.Evaluation ? limits.EvaluationsPerMonth : limits.AssistsPerMonth;
        if (limit is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var nextMonth = monthStart.AddMonths(1);
        var features = kind == AiRequestKind.Evaluation
            ? AiCompletionService.EvaluationFeatures
            : AiCompletionService.AssistFeatures;

        var used = await usageRepo.CountAsync(account.Id, monthStart, features, "platform", cancellationToken);
        if (used + units <= limit.Value)
        {
            return null;
        }

        var resetDate = nextMonth.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var what = kind == AiRequestKind.Evaluation ? "graded essays" : "AI coaching requests";
        var remaining = Math.Max(0, limit.Value - used);
        var detail = remaining > 0 && units > 1
            ? $"This submission needs {units} but only {remaining} of your {limit.Value} monthly {what} remain."
            : $"You have used all {limit.Value} {what} included in the {limits.Label} plan this month.";

        var canUpgrade = cfg.Plan == Plans.Free && billingEnabled;
        var pro = Plans.Limits[Plans.Pro];
        var nextStep = canUpgrade
            ? $"To keep going now, upgrade to Pro ({(kind == AiRequestKind.Evaluation ? pro.EvaluationsPerMonth : pro.AssistsPerMonth)} {what} a month) on the Pricing page, or add your own API key in Settings."
            : "To keep going now, add your own API key in Settings.";

        return TooManyRequests(
            $"{detail} Your allowance resets on {resetDate}. {nextStep}",
            "quota_exceeded",
            upgradeUrl: canUpgrade ? "/pricing" : null);
    }

    private int? GetFreeDailyCap()
    {
        var raw = configuration["FREE_PLAN_DAILY_AI_CAP"]?.Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private bool IsBillingEnabled() =>
        new[] { "STRIPE_SECRET_KEY", "STRIPE_PRICE_PRO_MONTHLY", "STRIPE_WEBHOOK_SECRET" }
            .All(name => !string.IsNullOrWhiteSpace(configuration[name]));

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
