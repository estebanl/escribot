using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Escribot.Api.Auth;
using Escribot.Api.Billing;
using Escribot.Api.Common;
using Escribot.Api.Configuration;
using Escribot.Api.Data;
using Microsoft.Extensions.Options;

namespace Escribot.Api.Api;

/// <summary>
/// Stripe Checkout, the customer portal, the pricing page's prices and the Stripe webhook.
/// </summary>
internal static partial class BillingEndpoints
{
    private const string NotAvailable = "Billing is not available yet.";

    /// <summary>Far more than any subscription event; Stripe caps payloads well below this.</summary>
    private const int MaxWebhookBytes = 512 * 1024;

    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/billing/checkout", CheckoutAsync);
        api.MapPost("/billing/portal", PortalAsync);
        api.MapGet("/billing/prices", PricesAsync);
        api.MapPost("/stripe/webhook", WebhookAsync);
        return api;
    }

    /// <summary>Starts a Stripe Checkout session for the Pro plan and returns its URL.</summary>
    private static async Task<IResult> CheckoutAsync(
        HttpContext context,
        StripeSettings stripe,
        IRateLimiter limiter,
        PlanResolver plans,
        ISubscriptionRepository subscriptions,
        SubscriptionSync sync,
        IStripeGateway gateway,
        IOptions<AppOptions> options,
        ErrorReporter reporter,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        if (!stripe.Enabled)
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, NotAvailable);
        }

        var rate = await limiter.CheckAsync($"billing:{account.Id}", 10, TimeSpan.FromMinutes(10), cancellationToken);
        if (!rate.Allowed)
        {
            context.Response.Headers.RetryAfter = rate.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return ApiResults.Error(StatusCodes.Status429TooManyRequests, "Too many attempts. Please wait a few minutes and try again.");
        }

        // An unreadable or missing body means the monthly price, as before.
        var interval = await ReadIntervalAsync(context.Request, cancellationToken);
        if (stripe.ProPriceId(interval) is not { } priceId)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "That billing period is not offered.");
        }

        try
        {
            if (await plans.GetPlanAsync(account, cancellationToken) == Plans.Owner)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, "The owner account already has unlimited access.");
            }

            if (Plans.HasProAccess(await subscriptions.GetAsync(account.Id, cancellationToken)))
            {
                return ApiResults.Error(StatusCodes.Status409Conflict, "You already have Pro. Use Manage billing to change or cancel it.", "already_subscribed");
            }

            var customerId = await sync.GetOrCreateCustomerAsync(account, cancellationToken);
            var origin = AppOrigin(context.Request, options.Value);
            var url = await gateway.CreateCheckoutSessionAsync(
                new CheckoutRequest(customerId, account.Id, priceId, $"{origin}/settings?billing=success", $"{origin}/pricing?billing=canceled"),
                cancellationToken);
            return Results.Json(new JsonObject { ["url"] = url });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await reporter.ReportServerErrorAsync(ex, "POST /api/billing/checkout", account.Id);
            return ApiResults.Error(StatusCodes.Status502BadGateway, "Could not start checkout. Please try again.");
        }
    }

    /// <summary>Opens the Stripe customer portal: change plan, update the card, cancel, invoices.</summary>
    private static async Task<IResult> PortalAsync(
        HttpContext context,
        StripeSettings stripe,
        ISubscriptionRepository subscriptions,
        IStripeGateway gateway,
        IOptions<AppOptions> options,
        ErrorReporter reporter,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        if (!stripe.Enabled)
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, NotAvailable);
        }

        var subscription = await subscriptions.GetAsync(account.Id, cancellationToken);
        if (string.IsNullOrEmpty(subscription?.StripeSubscriptionId))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "There is no subscription to manage yet.");
        }

        try
        {
            var url = await gateway.CreatePortalSessionAsync(subscription.StripeCustomerId, $"{AppOrigin(context.Request, options.Value)}/settings", cancellationToken);
            return Results.Json(new JsonObject { ["url"] = url });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await reporter.ReportServerErrorAsync(ex, "POST /api/billing/portal", account.Id);
            return ApiResults.Error(StatusCodes.Status502BadGateway, "Could not open billing management. Please try again.");
        }
    }

    /// <summary>
    /// What the pricing page shows, also to signed-out visitors: whether billing is on, the
    /// Pro prices, and for a signed-in viewer their plan and whether they have a subscription.
    /// </summary>
    private static async Task<IResult> PricesAsync(
        HttpContext context,
        StripeSettings stripe,
        PriceCatalog catalog,
        PlanResolver plans,
        ISubscriptionRepository subscriptions,
        CancellationToken cancellationToken)
    {
        var prices = new JsonObject();
        foreach (var (interval, price) in await catalog.GetProPricesAsync(cancellationToken))
        {
            prices[interval] = new JsonObject
            {
                ["unitAmount"] = price.UnitAmount,
                ["currency"] = price.Currency,
                ["interval"] = price.Interval,
            };
        }

        var account = context.GetSession()?.Account;
        var subscription = account is null ? null : await subscriptions.GetAsync(account.Id, cancellationToken);

        // Public, but the viewer part differs per person.
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Json(new JsonObject
        {
            ["billingEnabled"] = stripe.Enabled,
            ["prices"] = prices,
            ["viewer"] = account is null ? "anonymous" : await plans.GetPlanAsync(account, cancellationToken),
            ["hasSubscription"] = !string.IsNullOrEmpty(subscription?.StripeSubscriptionId),
        });
    }

    /// <summary>
    /// Public: Stripe proves itself with the signature header. Subscribe the endpoint to the
    /// <c>customer.subscription.*</c> events in <see cref="SubscriptionSync.SubscriptionEvents"/>.
    /// A 5xx makes Stripe retry the delivery with backoff; a 4xx does not.
    /// </summary>
    private static async Task<IResult> WebhookAsync(
        HttpContext context,
        StripeSettings stripe,
        StripeWebhookReader reader,
        SubscriptionSync sync,
        ErrorReporter reporter,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (!stripe.WebhookConfigured)
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "Webhook not configured.");
        }

        // The signature covers the exact bytes Stripe sent, so the raw body is read.
        if (await ReadBodyAsync(context.Request, cancellationToken) is not { } payload)
        {
            return ApiResults.Error(StatusCodes.Status413PayloadTooLarge, "Payload too large.");
        }

        if (reader.Read(payload, context.Request.Headers["Stripe-Signature"].ToString()) is not { } stripeEvent)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Invalid signature.");
        }

        if (!SubscriptionSync.SubscriptionEvents.Contains(stripeEvent.Type) || stripeEvent.Data?.Object is not Stripe.Subscription subscription)
        {
            return Results.Json(new JsonObject { ["received"] = true, ["handled"] = false });
        }

        try
        {
            var created = new DateTimeOffset(DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var result = await sync.ApplyAsync(SubscriptionSnapshot.From(subscription), created, cancellationToken);
            if (result == SubscriptionApplyResult.UnknownCustomer)
            {
                LogUnknownCustomer(loggers.CreateLogger(typeof(BillingEndpoints)), stripeEvent.Id);
            }

            return Results.Json(new JsonObject { ["received"] = true, ["result"] = SubscriptionSync.WireName(result) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await reporter.ReportServerErrorAsync(ex, "POST /api/stripe/webhook");
            return ApiResults.Error(StatusCodes.Status500InternalServerError, "Processing failed.");
        }
    }

    /// <summary>The address Stripe sends people back to: the configured public URL, else this request's own.</summary>
    internal static string AppOrigin(HttpRequest request, AppOptions options) =>
        (options.PublicUrl?.GetLeftPart(UriPartial.Path) ?? $"{request.Scheme}://{request.Host}").TrimEnd('/');

    private static async Task<string> ReadIntervalAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var json = await request.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return new Body(json).String("interval") == StripeSettings.Year ? StripeSettings.Year : StripeSettings.Month;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return StripeSettings.Month;
        }
    }

    /// <summary>The body as text, or null when it is larger than <see cref="MaxWebhookBytes"/>.</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxWebhookBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxWebhookBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stripe event {EventId} is for a customer with no matching user.")]
    private static partial void LogUnknownCustomer(ILogger logger, string eventId);
}
