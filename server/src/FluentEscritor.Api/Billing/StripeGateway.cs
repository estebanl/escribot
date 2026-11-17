using Stripe;

namespace FluentEscritor.Api.Billing;

/// <summary>A Pro price as configured in Stripe, for the pricing page.</summary>
/// <param name="UnitAmount">In minor units (cents).</param>
public sealed record PriceDisplay(long UnitAmount, string Currency, string Interval);

public sealed record CheckoutRequest(string CustomerId, string UserId, string PriceId, string SuccessUrl, string CancelUrl);

/// <summary>
/// The Stripe API calls the app makes. Everything else about billing is decided in this
/// app, so tests replace only this.
/// </summary>
public interface IStripeGateway
{
    /// <summary>Creates the user's customer. Repeated calls for the same user return the same customer.</summary>
    Task<string> CreateCustomerAsync(string userId, string email, string name, CancellationToken cancellationToken);

    /// <summary>Starts a Checkout session and returns its URL.</summary>
    Task<string> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>Opens the customer portal and returns its URL.</summary>
    Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken cancellationToken);

    /// <summary>Null when the price has no fixed amount.</summary>
    Task<PriceDisplay?> GetPriceAsync(string priceId, string interval, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the customer, which cancels their subscriptions at once and removes saved
    /// cards. Invoices stay in Stripe. Returns false when the customer was already gone.
    /// </summary>
    Task<bool> DeleteCustomerAsync(string customerId, CancellationToken cancellationToken);
}

internal sealed class StripeGateway(StripeSettings settings, IHttpClientFactory httpClients) : IStripeGateway
{
    public const string HttpClientName = "Stripe";

    private static readonly AppInfo App = new() { Name = "FluentEscritor" };

    public async Task<string> CreateCustomerAsync(string userId, string email, string name, CancellationToken cancellationToken)
    {
        var customer = await new CustomerService(Client()).CreateAsync(
            new CustomerCreateOptions { Email = email, Name = name, Metadata = new() { ["userId"] = userId } },
            // Two quick clicks must not create two customers for the same user.
            new RequestOptions { IdempotencyKey = $"customer-create-{userId}" },
            cancellationToken);
        return customer.Id;
    }

    public async Task<string> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var session = await new Stripe.Checkout.SessionService(Client()).CreateAsync(
            new Stripe.Checkout.SessionCreateOptions
            {
                Mode = "subscription",
                Customer = request.CustomerId,
                ClientReferenceId = request.UserId,
                LineItems = [new() { Price = request.PriceId, Quantity = 1 }],
                SubscriptionData = new() { Metadata = new() { ["userId"] = request.UserId } },
                AllowPromotionCodes = true,
                SuccessUrl = request.SuccessUrl,
                CancelUrl = request.CancelUrl,
            },
            cancellationToken: cancellationToken);
        return session.Url ?? throw new InvalidOperationException("Stripe did not return a checkout URL.");
    }

    public async Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken cancellationToken)
    {
        var session = await new Stripe.BillingPortal.SessionService(Client()).CreateAsync(
            new Stripe.BillingPortal.SessionCreateOptions { Customer = customerId, ReturnUrl = returnUrl },
            cancellationToken: cancellationToken);
        return session.Url;
    }

    public async Task<PriceDisplay?> GetPriceAsync(string priceId, string interval, CancellationToken cancellationToken)
    {
        var price = await new PriceService(Client()).GetAsync(priceId, cancellationToken: cancellationToken);
        return price.UnitAmount is { } amount ? new PriceDisplay(amount, price.Currency, interval) : null;
    }

    public async Task<bool> DeleteCustomerAsync(string customerId, CancellationToken cancellationToken)
    {
        try
        {
            await new CustomerService(Client()).DeleteAsync(customerId, cancellationToken: cancellationToken);
            return true;
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            return false;
        }
    }

    /// <summary>Created per call around a pooled HttpClient, so this gateway can be a singleton.</summary>
    private StripeClient Client()
    {
        var key = settings.SecretKey ?? throw new InvalidOperationException("Billing is not configured (STRIPE_SECRET_KEY is missing).");
        var http = new SystemNetHttpClient(httpClients.CreateClient(HttpClientName), maxNetworkRetries: 2, appInfo: App);
        return new StripeClient(new StripeClientOptions { ApiKey = key, HttpClient = http, ApiBase = settings.ApiBase });
    }
}
