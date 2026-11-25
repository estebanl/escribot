using System.Globalization;
using Escribot.Api.Auth;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Common;

/// <param name="EvaluationsPerMonth">Graded essays per calendar month (UTC). Null is unlimited.</param>
/// <param name="AssistsPerMonth">Outline checks, toolkit runs, drills and prompt generation per month.</param>
/// <param name="CanChooseModels">Whether the provider and model chosen in Settings are honoured on platform keys.</param>
/// <param name="CanTransformSvg">Whether uploaded Task 1 images can be AI-transformed into clean SVGs, and SVG images uploaded at all.</param>
/// <param name="StorageBytes">Total size of uploaded chart images. Null is unlimited.</param>
public sealed record PlanLimits(
    string Label,
    int? EvaluationsPerMonth,
    int? AssistsPerMonth,
    bool CanChooseModels,
    bool CanTransformSvg,
    long? StorageBytes);

/// <summary>
/// Plans decide how much of the platform's AI budget and storage an account may use.
/// </summary>
public static class Plans
{
    public const string Free = "free";
    public const string Pro = "pro";
    public const string Owner = "owner";

    private const long Megabyte = 1024 * 1024;

    /// <summary>
    /// The built-in limits. The monthly AI allowances of Free and Pro can be overridden by
    /// deployment settings, so read them through <see cref="PlanCatalog"/>; only the
    /// feature flags and storage here are fixed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, PlanLimits> Defaults = new Dictionary<string, PlanLimits>
    {
        [Free] = new("Free", 3, 30, false, false, 25 * Megabyte),
        [Pro] = new("Pro", 100, 1500, true, true, 250 * Megabyte),
        // The deployment owner pays for the platform keys.
        [Owner] = new("Owner", null, null, true, true, null),
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

/// <summary>
/// The limits in force for each plan: <see cref="Plans.Defaults"/> with the monthly AI
/// allowances of Free and Pro taken from deployment settings where they are set.
/// </summary>
/// <remarks>
/// Only server configuration (environment variables, app settings, Key Vault) is read, never
/// anything a request carries, and the values are fixed when the app starts. A setting must
/// be a whole number from 0 to <see cref="MaxMonthlyAllowance"/>; anything else stops the app
/// from starting rather than silently falling back, so a typo can never turn into
/// "unlimited". The Owner plan has no settings and stays unlimited.
/// </remarks>
public sealed class PlanCatalog
{
    /// <summary>The largest monthly allowance a setting may give.</summary>
    public const int MaxMonthlyAllowance = 1_000_000;

    /// <summary>The settings that override each plan's monthly allowances.</summary>
    public static readonly IReadOnlyList<(string Plan, string Evaluations, string Assists)> Settings =
    [
        (Plans.Free, "FREE_PLAN_EVALUATIONS_PER_MONTH", "FREE_PLAN_ASSISTS_PER_MONTH"),
        (Plans.Pro, "PRO_PLAN_EVALUATIONS_PER_MONTH", "PRO_PLAN_ASSISTS_PER_MONTH"),
    ];

    private readonly Dictionary<string, PlanLimits> _limits;

    public PlanCatalog(IConfiguration configuration)
    {
        _limits = new Dictionary<string, PlanLimits>(Plans.Defaults);
        foreach (var (plan, evaluations, assists) in Settings)
        {
            var defaults = Plans.Defaults[plan];
            _limits[plan] = defaults with
            {
                EvaluationsPerMonth = Read(configuration, evaluations, defaults.EvaluationsPerMonth),
                AssistsPerMonth = Read(configuration, assists, defaults.AssistsPerMonth),
            };
        }
    }

    /// <summary>The limits of a plan. An unknown plan gets Free's, the most restrictive.</summary>
    public PlanLimits Get(string plan) => _limits.TryGetValue(plan, out var limits) ? limits : _limits[Plans.Free];

    public PlanLimits this[string plan] => Get(plan);

    private static int? Read(IConfiguration configuration, string name, int? fallback)
    {
        var raw = configuration[name]?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        // Digits only: no sign, decimals, exponents or thousands separators.
        if (raw.All(char.IsAsciiDigit)
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value <= MaxMonthlyAllowance)
        {
            return value;
        }

        throw new InvalidOperationException(
            $"{name} must be a whole number from 0 to {MaxMonthlyAllowance.ToString("N0", CultureInfo.InvariantCulture)}, or unset for the default ({fallback}).");
    }
}
