using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Common;

/// <param name="EvaluationsPerMonth">Graded essays per calendar month (UTC). Null is unlimited.</param>
/// <param name="AssistsPerMonth">Outline checks, toolkit runs, drills and prompt generation per month.</param>
/// <param name="CanChooseModels">Whether the provider and model chosen in Settings are honoured on platform keys.</param>
/// <param name="StorageBytes">Total size of uploaded chart images. Null is unlimited.</param>
public sealed record PlanLimits(string Label, int? EvaluationsPerMonth, int? AssistsPerMonth, bool CanChooseModels, long? StorageBytes);

/// <summary>
/// Plans decide how much of the platform's AI budget and storage an account may use.
/// Ported from <c>src/lib/plans.ts</c>.
/// </summary>
public static class Plans
{
    public const string Free = "free";
    public const string Pro = "pro";
    public const string Owner = "owner";

    private const long Megabyte = 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, PlanLimits> Limits = new Dictionary<string, PlanLimits>
    {
        [Free] = new("Free", 5, 60, false, 25 * Megabyte),
        [Pro] = new("Pro", 100, 1500, true, 250 * Megabyte),
        // The deployment owner pays for the platform keys.
        [Owner] = new("Owner", null, null, true, null),
    };

    /// <summary>
    /// Statuses that grant Pro. <c>past_due</c> keeps access while Stripe retries the card
    /// (Smart Retries); Stripe moves the subscription to <c>canceled</c> or <c>unpaid</c> if
    /// every retry fails, which removes access.
    /// </summary>
    public static bool IsProStatus(string? status) => status is "active" or "trialing" or "past_due";

    public static bool HasProAccess(Subscription? subscription) => IsProStatus(subscription?.Status);

    /// <summary>
    /// Paid plans need the Stripe key, the monthly Pro price and the webhook secret.
    /// Without the webhook, checkout would take payment but never record the subscription.
    /// </summary>
    public static bool IsBillingEnabled(IConfiguration configuration) =>
        new[] { "STRIPE_SECRET_KEY", "STRIPE_PRICE_PRO_MONTHLY", "STRIPE_WEBHOOK_SECRET" }
            .All(name => !string.IsNullOrWhiteSpace(configuration[name]));
}

/// <summary>Works out which plan applies to a user right now: owner, paid or complimentary Pro, or Free.</summary>
public sealed class PlanResolver(ISettingsRepository settings, ISubscriptionRepository subscriptions, AuthSettings auth)
{
    public async Task<string> GetPlanAsync(UserAccount account, CancellationToken cancellationToken = default)
    {
        // The owner plan needs a verified OWNER_EMAIL, so it cannot be claimed by signing
        // up with someone else's address.
        if (auth.IsOwner(account))
        {
            return Plans.Owner;
        }

        var stored = await settings.GetOrCreateAsync(account.Id, cancellationToken);
        var subscription = await subscriptions.GetAsync(account.Id, cancellationToken);
        return Resolve(stored.Plan, isOwner: false, Plans.HasProAccess(subscription));
    }

    /// <param name="storedPlan">The manual override in the user's settings; "pro" is complimentary Pro.</param>
    public static string Resolve(string? storedPlan, bool isOwner, bool hasPaidPro) =>
        isOwner ? Plans.Owner : hasPaidPro || storedPlan == Plans.Pro ? Plans.Pro : Plans.Free;
}
