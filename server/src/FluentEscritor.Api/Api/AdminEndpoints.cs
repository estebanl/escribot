using System.Text.Json;
using System.Text.Json.Nodes;
using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Common;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Api;

/// <summary>
/// The admin page's data and the complimentary Pro toggle. To anyone but an admin these
/// routes do not exist: they answer 404 before reading anything, the request body included.
/// </summary>
internal static class AdminEndpoints
{
    private const int AccountLimit = 100;
    private const int ErrorLimit = 25;
    private const int MaxSearchLength = 254;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/admin").AddEndpointFilter(async (context, next) =>
            context.HttpContext.GetSession() is { IsAdmin: true }
                ? await next(context)
                : ApiResults.Error(StatusCodes.Status404NotFound, "Not found."));

        admin.MapGet("/overview", GetOverviewAsync).OnFailure("Failed to load the admin data.");
        admin.MapPatch("/users/{id}", SetPlanAsync).OnFailure("Failed to update the user.");
        return api;
    }

    /// <summary>Everything the admin page shows: users, sign-ups, subscribers, platform AI cost by plan and recent errors.</summary>
    private static async Task<IResult> GetOverviewAsync(
        string? q,
        IAdminRepository admin,
        IErrorEventRepository errors,
        AuthSettings auth,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        // Month to date (UTC), as the quotas count.
        var since = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var search = q?.Trim() is { Length: > 0 } text ? text[..Math.Min(text.Length, MaxSearchLength)] : null;

        var stats = await admin.GetAccountStatsAsync(cancellationToken);
        var statuses = await admin.GetSubscriptionStatusCountsAsync(cancellationToken);
        var spend = await admin.GetAiUsageTotalsAsync(since, cancellationToken);
        var essays = await admin.CountAiCallsAsync(since, UsageService.EvaluationFeatures, cancellationToken);
        var errorsLastDay = await errors.CountSinceAsync(now.AddDays(-1), cancellationToken);
        var accounts = await admin.ListAccountsAsync(search, AccountLimit, cancellationToken);
        var usage = (await admin.GetAiUsageByUserAsync([.. accounts.Select(a => a.Id)], since, UsageService.EvaluationFeatures, cancellationToken))
            .ToDictionary(u => u.UserId);
        var recentErrors = await errors.ListRecentAsync(ErrorLimit, cancellationToken);

        int Count(string status) => statuses.GetValueOrDefault(status);
        var platform = spend.Where(s => s.KeySource == "platform").ToList();
        double CostFor(string plan) => platform.Where(s => s.Plan == plan).Sum(s => s.CostUsd);

        var byStatus = new JsonObject();
        foreach (var (status, count) in statuses.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            byStatus[status] = count;
        }

        return Results.Json(new JsonObject
        {
            ["since"] = Wire.Time(since),
            ["users"] = new JsonObject
            {
                ["total"] = stats.Total,
                ["verified"] = stats.Verified,
                ["createdLast7Days"] = stats.CreatedLast7Days,
                ["createdLast30Days"] = stats.CreatedLast30Days,
            },
            ["subscriptions"] = new JsonObject
            {
                ["paying"] = Count("active") + Count("trialing"),
                ["pastDue"] = Count("past_due"),
                ["canceled"] = Count("canceled"),
                ["byStatus"] = byStatus,
            },
            ["aiCost"] = new JsonObject
            {
                // Only the platform's own keys cost the deployment anything.
                ["platformUsd"] = platform.Sum(s => s.CostUsd),
                ["freeUsd"] = CostFor(Plans.Free),
                ["proUsd"] = CostFor(Plans.Pro),
                ["ownerUsd"] = CostFor(Plans.Owner),
            },
            ["essaysGraded"] = essays,
            ["errorsLast24Hours"] = errorsLastDay,
            ["accounts"] = new JsonArray([.. accounts.Select(a => (JsonNode)Account(a, usage.GetValueOrDefault(a.Id), auth))]),
            ["accountLimit"] = AccountLimit,
            ["recentErrors"] = new JsonArray([.. recentErrors.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.Id,
                ["source"] = e.Source,
                ["message"] = e.Message,
                ["stack"] = e.Stack,
                ["path"] = e.Path,
                ["digest"] = e.Digest,
                ["createdAt"] = Wire.Time(e.CreatedAt),
            })]),
        });
    }

    private static JsonObject Account(AdminAccountRow row, UserAiUsage? usage, AuthSettings auth)
    {
        var isOwner = auth.IsOwner(new UserAccount { Id = row.Id, Email = row.Email, EmailVerified = row.EmailVerified });
        return new JsonObject
        {
            ["id"] = row.Id,
            ["name"] = row.Name,
            ["email"] = row.Email,
            ["emailVerified"] = row.EmailVerified,
            ["createdAt"] = Wire.Time(row.CreatedAt),
            ["plan"] = PlanResolver.Resolve(row.StoredPlan, isOwner, Plans.IsProStatus(row.SubscriptionStatus)),
            ["subscriptionStatus"] = row.SubscriptionStatus,
            ["complimentaryPro"] = row.StoredPlan == Plans.Pro,
            ["essays"] = usage?.CountedCalls ?? 0,
            ["platformCostUsd"] = usage?.PlatformCostUsd ?? 0,
        };
    }

    /// <summary>Grants or removes complimentary Pro (the plan in the user's settings). A paid subscription is unaffected.</summary>
    private static async Task<IResult> SetPlanAsync(
        string id,
        HttpContext context,
        IUserAccountRepository accounts,
        ISettingsRepository settings,
        CancellationToken cancellationToken)
    {
        // Read here rather than bound, so a non-admin never learns how the body is checked.
        string? plan;
        try
        {
            plan = new Body(await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken)).String("plan");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            plan = null;
        }

        if (plan is not (Plans.Pro or Plans.Free))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "plan must be 'pro' or 'free'.");
        }

        if (await accounts.GetAsync(id, cancellationToken) is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "User not found.");
        }

        await settings.UpdateAsync(id, existing => existing with { Plan = plan }, cancellationToken);
        return Results.Json(new JsonObject { ["ok"] = true, ["plan"] = plan });
    }
}
