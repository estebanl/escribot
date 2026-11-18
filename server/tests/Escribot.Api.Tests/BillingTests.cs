using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Escribot.Api.Billing;
using Escribot.Api.Common;
using Escribot.Api.Data.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Escribot.Api.Tests;

/// <summary>Records the Stripe calls the app makes and answers them without a network.</summary>
public sealed class FakeStripeGateway : IStripeGateway
{
    private int _customers;

    public List<string> CreatedCustomers { get; } = [];

    public List<CheckoutRequest> Checkouts { get; } = [];

    public List<string> DeletedCustomers { get; } = [];

    public int PriceLookups { get; private set; }

    public Exception? Failure { get; set; }

    public Task<string> CreateCustomerAsync(string userId, string email, string name, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var id = $"cus_test_{Interlocked.Increment(ref _customers)}";
        lock (CreatedCustomers)
        {
            CreatedCustomers.Add(id);
        }

        return Task.FromResult(id);
    }

    public Task<string> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Checkouts.Add(request);
        return Task.FromResult($"https://checkout.stripe.test/{request.CustomerId}");
    }

    public Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult($"https://billing.stripe.test/{customerId}?return={returnUrl}");
    }

    public Task<PriceDisplay?> GetPriceAsync(string priceId, string interval, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        PriceLookups++;
        return Task.FromResult<PriceDisplay?>(new PriceDisplay(interval == "year" ? 9000 : 900, "usd", interval));
    }

    public Task<bool> DeleteCustomerAsync(string customerId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        DeletedCustomers.Add(customerId);
        return Task.FromResult(true);
    }

    private void ThrowIfFailing()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

public class BillingTests
{
    private const string WebhookSecret = "whsec_test_secret_for_signing";

    private static ApiFactory CreateFactory(FakeStripeGateway stripe, bool billing = true, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["STRIPE_SECRET_KEY"] = billing ? "sk_test_fake" : string.Empty,
            ["STRIPE_PRICE_PRO_MONTHLY"] = billing ? "price_month" : string.Empty,
            ["STRIPE_PRICE_PRO_YEARLY"] = billing ? "price_year" : string.Empty,
            ["STRIPE_WEBHOOK_SECRET"] = billing ? WebhookSecret : string.Empty,
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return new ApiFactory
        {
            Settings = settings,
            ConfigureServices = services =>
            {
                services.RemoveAll<IStripeGateway>();
                services.AddSingleton<IStripeGateway>(stripe);
            },
        };
    }

    internal static async Task<(HttpClient Browser, string UserId)> SignUpAsync(ApiFactory factory, string label)
    {
        var browser = factory.CreateBrowser();
        var email = $"{label}-{Guid.NewGuid():N}@example.test";
        using var res = await AuthEndpointTests.SignUpAsync(browser, email, $"pw-{TestSecrets.NewSecret()}", $"User {label}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var userId = (await AuthEndpointTests.Json(res)).GetProperty("user").GetProperty("id").GetString()!;
        return (browser, userId);
    }

    // ---- Webhook ----

    private static string SubscriptionEvent(
        string type,
        string customerId,
        string status,
        long created,
        string subscriptionId = "sub_1",
        string? userIdMetadata = null,
        bool cancelAtPeriodEnd = false,
        long periodEnd = 1_900_000_000)
    {
        var metadata = new JsonObject();
        if (userIdMetadata is not null)
        {
            metadata["userId"] = userIdMetadata;
        }

        return new JsonObject
        {
            ["id"] = $"evt_{Guid.NewGuid():N}",
            ["object"] = "event",
            ["api_version"] = Stripe.StripeConfiguration.ApiVersion,
            ["created"] = created,
            ["type"] = type,
            ["livemode"] = false,
            ["data"] = new JsonObject
            {
                ["object"] = new JsonObject
                {
                    ["id"] = subscriptionId,
                    ["object"] = "subscription",
                    ["customer"] = customerId,
                    ["status"] = status,
                    ["cancel_at_period_end"] = cancelAtPeriodEnd,
                    ["cancel_at"] = null,
                    ["metadata"] = metadata,
                    ["items"] = new JsonObject
                    {
                        ["object"] = "list",
                        ["data"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "si_1",
                            ["object"] = "subscription_item",
                            ["current_period_end"] = periodEnd,
                            ["price"] = new JsonObject { ["id"] = "price_month", ["object"] = "price" },
                        }),
                    },
                },
            },
        }.ToJsonString();
    }

    private static string Sign(string payload, long timestamp, string secret = WebhookSecret)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }

    private static async Task<HttpResponseMessage> PostWebhookAsync(ApiFactory factory, string payload, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("Stripe-Signature", signature);
        }

        return await factory.CreateBrowser().SendAsync(request);
    }

    private static async Task<string?> DeliverAsync(ApiFactory factory, string payload)
    {
        using var response = await PostWebhookAsync(factory, payload, Sign(payload, factory.Clock.GetUtcNow().ToUnixTimeSeconds()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await AuthEndpointTests.Json(response);
        return json.TryGetProperty("result", out var result) ? result.GetString() : null;
    }

    private static string WithCustomer(ApiFactory factory, string userId, string customerId = "cus_known")
    {
        factory.Data.Subscriptions[userId] = new Subscription { UserId = userId, StripeCustomerId = customerId };
        return customerId;
    }

    [Fact]
    public async Task Webhook_rejects_a_missing_wrong_tampered_or_old_signature()
    {
        await using var factory = CreateFactory(new FakeStripeGateway());
        var (_, userId) = await SignUpAsync(factory, "sig");
        var customer = WithCustomer(factory, userId);
        var payload = SubscriptionEvent("customer.subscription.created", customer, "active", 100);
        var now = factory.Clock.GetUtcNow().ToUnixTimeSeconds();

        foreach (var signature in new string?[]
        {
            null,
            string.Empty,
            "not a signature",
            Sign(payload, now, "whsec_someone_else"),
            Sign(payload.Replace("\"active\"", "\"trialing\""), now),
            Sign(payload, now - StripeWebhookReader.ToleranceSeconds - 60),
        })
        {
            using var response = await PostWebhookAsync(factory, payload, signature);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Invalid signature.", (await AuthEndpointTests.Json(response)).GetProperty("error").GetString());
        }

        // Nothing was applied.
        Assert.Null(factory.Data.Subscriptions[userId].Status);
    }

    [Fact]
    public async Task Webhook_is_unavailable_without_its_secret()
    {
        await using var factory = CreateFactory(new FakeStripeGateway(), billing: false);
        using var response = await PostWebhookAsync(factory, "{}", "t=1,v1=00");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("active", Plans.Pro)]
    [InlineData("trialing", Plans.Pro)]
    [InlineData("past_due", Plans.Pro)]
    [InlineData("incomplete", Plans.Free)]
    [InlineData("incomplete_expired", Plans.Free)]
    [InlineData("unpaid", Plans.Free)]
    [InlineData("canceled", Plans.Free)]
    [InlineData("paused", Plans.Free)]
    public async Task Each_subscription_status_grants_or_withholds_pro(string status, string expectedPlan)
    {
        await using var factory = CreateFactory(new FakeStripeGateway());
        var (_, userId) = await SignUpAsync(factory, "status");
        var customer = WithCustomer(factory, userId);

        Assert.Equal("applied", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.updated", customer, status, 100, cancelAtPeriodEnd: true)));

        var stored = factory.Data.Subscriptions[userId];
        Assert.Equal(status, stored.Status);
        Assert.Equal("sub_1", stored.StripeSubscriptionId);
        Assert.Equal("price_month", stored.PriceId);
        Assert.True(stored.CancelAtPeriodEnd);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), stored.CurrentPeriodEnd);
        Assert.Equal(100, stored.LastEventAt);

        var plans = factory.Services.GetRequiredService<PlanResolver>();
        Assert.Equal(expectedPlan, await plans.GetPlanAsync(factory.Data.Accounts[userId]));
    }

    [Fact]
    public async Task Each_of_the_five_subscription_events_is_applied()
    {
        await using var factory = CreateFactory(new FakeStripeGateway());
        var (_, userId) = await SignUpAsync(factory, "events");
        var customer = WithCustomer(factory, userId);

        var created = 100;
        foreach (var type in SubscriptionSync.SubscriptionEvents)
        {
            Assert.Equal("applied", await DeliverAsync(factory, SubscriptionEvent(type, customer, "active", created++)));
        }

        Assert.Equal(created - 1, factory.Data.Subscriptions[userId].LastEventAt);
    }

    [Fact]
    public async Task Other_events_are_acknowledged_but_not_handled()
    {
        await using var factory = CreateFactory(new FakeStripeGateway());
        var payload = SubscriptionEvent("customer.subscription.created", "cus_x", "active", 100).Replace("customer.subscription.created", "invoice.paid");
        using var response = await PostWebhookAsync(factory, payload, Sign(payload, factory.Clock.GetUtcNow().ToUnixTimeSeconds()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await AuthEndpointTests.Json(response)).GetProperty("handled").GetBoolean());
    }

    [Fact]
    public async Task An_event_older_than_the_last_one_applied_is_skipped()
    {
        await using var factory = CreateFactory(new FakeStripeGateway());
        var (_, userId) = await SignUpAsync(factory, "stale");
        var customer = WithCustomer(factory, userId);

        Assert.Equal("applied", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.updated", customer, "active", 200)));
        Assert.Equal("stale", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.updated", customer, "canceled", 150)));
        Assert.Equal("active", factory.Data.Subscriptions[userId].Status);
        Assert.Equal(200, factory.Data.Subscriptions[userId].LastEventAt);

        // Events created in the same second are applied in arrival order.
        Assert.Equal("applied", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.deleted", customer, "canceled", 200)));
        Assert.Equal("canceled", factory.Data.Subscriptions[userId].Status);
    }

    [Fact]
    public async Task The_end_of_an_old_subscription_does_not_downgrade_a_new_one()
    {
        await using var factory = CreateFactory(new FakeStripeGateway());
        var (_, userId) = await SignUpAsync(factory, "resub");
        var customer = WithCustomer(factory, userId);

        // sub_old was cancelled; the user then subscribed again with sub_new.
        Assert.Equal("applied", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.deleted", customer, "canceled", 100, "sub_old")));
        Assert.Equal("applied", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.created", customer, "active", 200, "sub_new")));

        // A late update about the old subscription is ignored, at any time.
        Assert.Equal("ignored", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.updated", customer, "canceled", 300, "sub_old")));
        Assert.Equal("sub_new", factory.Data.Subscriptions[userId].StripeSubscriptionId);
        Assert.Equal("active", factory.Data.Subscriptions[userId].Status);
    }

    [Fact]
    public async Task Users_are_found_by_customer_id_or_else_by_the_user_id_in_metadata()
    {
        await using var factory = CreateFactory(new FakeStripeGateway());
        var (_, userId) = await SignUpAsync(factory, "lookup");

        // Nobody has this customer, and the metadata names nobody.
        Assert.Equal("unknown_customer", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.created", "cus_dashboard", "active", 100)));
        Assert.Equal("unknown_customer", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.created", "cus_dashboard", "active", 100, userIdMetadata: "no-such-user")));
        Assert.Empty(factory.Data.Subscriptions);

        // A customer created in the dashboard, tagged with an existing user.
        Assert.Equal("applied", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.created", "cus_dashboard", "active", 100, userIdMetadata: userId)));
        Assert.Equal("cus_dashboard", factory.Data.Subscriptions[userId].StripeCustomerId);
        Assert.Equal("active", factory.Data.Subscriptions[userId].Status);

        // That user keeps the customer they have: another customer naming them matches nobody.
        Assert.Equal("unknown_customer", await DeliverAsync(factory, SubscriptionEvent("customer.subscription.created", "cus_other", "canceled", 200, "sub_2", userIdMetadata: userId)));
        Assert.Equal("active", factory.Data.Subscriptions[userId].Status);
    }

    [Fact]
    public void Snapshot_takes_the_latest_period_end_and_cancel_at_as_cancelling()
    {
        var subscription = new Stripe.Subscription
        {
            Id = "sub_1",
            CustomerId = "cus_1",
            Status = "active",
            CancelAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Items = new Stripe.StripeList<Stripe.SubscriptionItem>
            {
                Data =
                [
                    new() { CurrentPeriodEnd = new DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc), Price = new() { Id = "price_a" } },
                    new() { CurrentPeriodEnd = new DateTime(2029, 6, 1, 0, 0, 0, DateTimeKind.Utc), Price = new() { Id = "price_b" } },
                ],
            },
        };

        var snapshot = SubscriptionSnapshot.From(subscription);
        Assert.Equal("price_a", snapshot.PriceId);
        Assert.Equal(new DateTimeOffset(2029, 6, 1, 0, 0, 0, TimeSpan.Zero), snapshot.CurrentPeriodEnd);
        Assert.True(snapshot.CancelAtPeriodEnd);
        Assert.Null(snapshot.UserIdMetadata);
    }

    // ---- Checkout and portal ----

    [Fact]
    public async Task Billing_routes_require_a_session()
    {
        await using var factory = CreateFactory(new FakeStripeGateway());
        var anon = factory.CreateBrowser();
        foreach (var path in new[] { "/api/billing/checkout", "/api/billing/portal" })
        {
            using var response = await anon.PostAsJsonAsync(path, new { });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Checkout_and_portal_stay_closed_until_billing_is_configured()
    {
        var stripe = new FakeStripeGateway();
        await using var factory = CreateFactory(stripe, billing: false);
        var (browser, _) = await SignUpAsync(factory, "closed");

        foreach (var path in new[] { "/api/billing/checkout", "/api/billing/portal" })
        {
            using var response = await browser.PostAsJsonAsync(path, new { });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        Assert.Empty(stripe.CreatedCustomers);
    }

    [Fact]
    public async Task Checkout_creates_one_customer_per_user_and_returns_the_session_url()
    {
        var stripe = new FakeStripeGateway();
        await using var factory = CreateFactory(stripe, extra: new() { ["App:PublicUrl"] = "https://app.example.test/" });
        var (browser, userId) = await SignUpAsync(factory, "checkout");

        using var first = await browser.PostAsJsonAsync("/api/billing/checkout", new { interval = "month" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("https://checkout.stripe.test/cus_test_1", (await AuthEndpointTests.Json(first)).GetProperty("url").GetString());

        using var second = await browser.PostAsJsonAsync("/api/billing/checkout", new { interval = "year" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Single(stripe.CreatedCustomers);
        Assert.Equal("cus_test_1", factory.Data.Subscriptions[userId].StripeCustomerId);
        Assert.Equal(new[] { "price_month", "price_year" }, stripe.Checkouts.Select(c => c.PriceId));
        Assert.All(stripe.Checkouts, c => Assert.Equal(userId, c.UserId));
        Assert.Equal("https://app.example.test/settings?billing=success", stripe.Checkouts[0].SuccessUrl);
        Assert.Equal("https://app.example.test/pricing?billing=canceled", stripe.Checkouts[0].CancelUrl);
    }

    [Fact]
    public async Task Checkout_refuses_a_user_who_already_has_pro_or_an_interval_not_offered()
    {
        var stripe = new FakeStripeGateway();
        await using var factory = CreateFactory(stripe, extra: new() { ["STRIPE_PRICE_PRO_YEARLY"] = string.Empty });
        var (browser, userId) = await SignUpAsync(factory, "already");

        using var yearly = await browser.PostAsJsonAsync("/api/billing/checkout", new { interval = "year" });
        Assert.Equal(HttpStatusCode.BadRequest, yearly.StatusCode);

        factory.Data.Subscriptions[userId] = new Subscription { UserId = userId, StripeCustomerId = "cus_1", StripeSubscriptionId = "sub_1", Status = "past_due" };
        using var again = await browser.PostAsJsonAsync("/api/billing/checkout", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_subscribed", (await AuthEndpointTests.Json(again)).GetProperty("code").GetString());
        Assert.Empty(stripe.Checkouts);
    }

    [Fact]
    public async Task Checkout_failures_at_stripe_are_reported_as_502()
    {
        var stripe = new FakeStripeGateway { Failure = new HttpRequestException("stripe down") };
        await using var factory = CreateFactory(stripe);
        var (browser, _) = await SignUpAsync(factory, "down");

        using var response = await browser.PostAsJsonAsync("/api/billing/checkout", new { });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(factory.Data.Errors, e => e.Path == "POST /api/billing/checkout");
    }

    [Fact]
    public async Task Portal_needs_a_subscription_and_opens_for_its_customer()
    {
        await using var factory = CreateFactory(new FakeStripeGateway());
        var (browser, userId) = await SignUpAsync(factory, "portal");

        using var none = await browser.PostAsync("/api/billing/portal", null);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        // A customer without a subscription yet (checkout abandoned) has nothing to manage either.
        WithCustomer(factory, userId, "cus_portal");
        using var abandoned = await browser.PostAsync("/api/billing/portal", null);
        Assert.Equal(HttpStatusCode.NotFound, abandoned.StatusCode);

        factory.Data.Subscriptions[userId] = factory.Data.Subscriptions[userId] with { StripeSubscriptionId = "sub_1", Status = "active" };
        using var opened = await browser.PostAsync("/api/billing/portal", null);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.StartsWith("https://billing.stripe.test/cus_portal?return=https://localhost/settings", (await AuthEndpointTests.Json(opened)).GetProperty("url").GetString());
    }

    // ---- Prices ----

    [Fact]
    public async Task Prices_are_public_and_cached_for_an_hour()
    {
        var stripe = new FakeStripeGateway();
        await using var factory = CreateFactory(stripe);
        var anon = factory.CreateBrowser();

        using var first = await anon.GetAsync("/api/billing/prices");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var json = await AuthEndpointTests.Json(first);
        Assert.True(json.GetProperty("billingEnabled").GetBoolean());
        Assert.Equal("anonymous", json.GetProperty("viewer").GetString());
        Assert.Equal(900, json.GetProperty("prices").GetProperty("month").GetProperty("unitAmount").GetInt64());
        Assert.Equal("usd", json.GetProperty("prices").GetProperty("year").GetProperty("currency").GetString());

        (await anon.GetAsync("/api/billing/prices")).Dispose();
        Assert.Equal(2, stripe.PriceLookups);

        factory.Clock.Now += PriceCatalog.CacheFor;
        (await anon.GetAsync("/api/billing/prices")).Dispose();
        Assert.Equal(4, stripe.PriceLookups);

        var (browser, _) = await SignUpAsync(factory, "viewer");
        using var signedIn = await browser.GetAsync("/api/billing/prices");
        Assert.Equal(Plans.Free, (await AuthEndpointTests.Json(signedIn)).GetProperty("viewer").GetString());
    }

    [Fact]
    public async Task Prices_are_empty_when_billing_is_off()
    {
        var stripe = new FakeStripeGateway();
        await using var factory = CreateFactory(stripe, billing: false);
        using var response = await factory.CreateBrowser().GetAsync("/api/billing/prices");
        var json = await AuthEndpointTests.Json(response);
        Assert.False(json.GetProperty("billingEnabled").GetBoolean());
        Assert.Empty(json.GetProperty("prices").EnumerateObject());
        Assert.Equal(0, stripe.PriceLookups);
    }

    // ---- Account deletion ----

    private static async Task<HttpResponseMessage> DeleteAccountAsync(HttpClient browser, string password) =>
        await browser.PostAsJsonAsync("/api/auth/delete-user", new { password });

    [Fact]
    public async Task Deleting_an_account_deletes_its_stripe_customer_first()
    {
        var stripe = new FakeStripeGateway();
        await using var factory = CreateFactory(stripe);
        var browser = factory.CreateBrowser();
        var password = $"pw-{TestSecrets.NewSecret()}";
        using var signUp = await AuthEndpointTests.SignUpAsync(browser, $"delete-{Guid.NewGuid():N}@example.test", password, "Deleter");
        var userId = (await AuthEndpointTests.Json(signUp)).GetProperty("user").GetProperty("id").GetString()!;
        WithCustomer(factory, userId, "cus_delete");

        // Stripe is down: nothing is deleted, and the user is told to try again.
        stripe.Failure = new HttpRequestException("stripe down");
        using var failed = await DeleteAccountAsync(browser, password);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal("BILLING_UNAVAILABLE", (await AuthEndpointTests.Json(failed)).GetProperty("code").GetString());
        Assert.True(factory.Data.Accounts.ContainsKey(userId));
        Assert.True(factory.Data.Subscriptions.ContainsKey(userId));

        stripe.Failure = null;
        using var deleted = await DeleteAccountAsync(browser, password);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(new[] { "cus_delete" }, stripe.DeletedCustomers);
        Assert.False(factory.Data.Accounts.ContainsKey(userId));
        Assert.False(factory.Data.Subscriptions.ContainsKey(userId));
    }

    [Fact]
    public async Task Deleting_an_account_without_billing_does_not_call_stripe()
    {
        var stripe = new FakeStripeGateway { Failure = new InvalidOperationException("must not be called") };
        await using var factory = CreateFactory(stripe, billing: false);
        var browser = factory.CreateBrowser();
        var password = $"pw-{TestSecrets.NewSecret()}";
        (await AuthEndpointTests.SignUpAsync(browser, $"plain-{Guid.NewGuid():N}@example.test", password, "Plain")).Dispose();

        using var deleted = await DeleteAccountAsync(browser, password);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }

    [Fact]
    public void Settings_enable_billing_only_with_key_monthly_price_and_webhook_secret()
    {
        static StripeSettings Settings(string? key, string? month, string? hook) => new(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["STRIPE_SECRET_KEY"] = key,
                ["STRIPE_PRICE_PRO_MONTHLY"] = month,
                ["STRIPE_WEBHOOK_SECRET"] = hook,
                ["STRIPE_API_BASE"] = "http://localhost:12111",
            }).Build(),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" });

        Assert.True(Settings("sk", "price", "whsec").Enabled);
        Assert.False(Settings(" ", "price", "whsec").Enabled);
        Assert.False(Settings("sk", null, "whsec").Enabled);
        Assert.False(Settings("sk", "price", "").Enabled);
        Assert.True(Settings("sk", null, "whsec").WebhookConfigured);
        Assert.Null(Settings("sk", "price", "whsec").ApiBase);
        Assert.Null(Settings("sk", "price", "whsec").ProPriceId("week"));
    }
}
