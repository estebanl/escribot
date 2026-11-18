namespace Escribot.Api.Billing;

/// <summary>
/// Stripe configuration, read once at startup from <c>STRIPE_SECRET_KEY</c>,
/// <c>STRIPE_PRICE_PRO_MONTHLY</c>, <c>STRIPE_PRICE_PRO_YEARLY</c> and <c>STRIPE_WEBHOOK_SECRET</c>.
/// </summary>
public sealed class StripeSettings
{
    public const string Month = "month";
    public const string Year = "year";

    public StripeSettings(IConfiguration configuration, IHostEnvironment environment)
    {
        SecretKey = Value(configuration, "STRIPE_SECRET_KEY");
        MonthlyPriceId = Value(configuration, "STRIPE_PRICE_PRO_MONTHLY");
        YearlyPriceId = Value(configuration, "STRIPE_PRICE_PRO_YEARLY");
        WebhookSecret = Value(configuration, "STRIPE_WEBHOOK_SECRET");

        // For local testing against stripe-mock or a fake server; never honoured in production.
        ApiBase = environment.IsProduction() ? null : Value(configuration, "STRIPE_API_BASE");
    }

    public string? SecretKey { get; }

    public string? MonthlyPriceId { get; }

    public string? YearlyPriceId { get; }

    public string? WebhookSecret { get; }

    public string? ApiBase { get; }

    /// <summary>
    /// Billing is on only when the API key, the monthly Pro price and the webhook secret are
    /// all set. Without the webhook, checkout would take payment but never record the subscription.
    /// </summary>
    public bool Enabled => SecretKey is not null && MonthlyPriceId is not null && WebhookSecret is not null;

    /// <summary>The webhook can verify and apply events: it needs the signing secret and the API key.</summary>
    public bool WebhookConfigured => SecretKey is not null && WebhookSecret is not null;

    /// <summary>The Pro price for <paramref name="interval"/> ("month" or "year"), or null when it is not offered.</summary>
    public string? ProPriceId(string interval) => interval switch
    {
        Month => MonthlyPriceId,
        Year => YearlyPriceId,
        _ => null,
    };

    private static string? Value(IConfiguration configuration, string name) =>
        configuration[name]?.Trim() is { Length: > 0 } value ? value : null;
}
