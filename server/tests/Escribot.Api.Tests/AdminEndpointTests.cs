using System.Net;
using System.Net.Http.Json;
using System.Text;
using Escribot.Api.Common;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Tests;

public class AdminEndpointTests
{
    private const string AdminEmail = "admin@example.test";

    private static ApiFactory CreateFactory() => new()
    {
        Settings = new() { ["ADMIN_EMAILS"] = AdminEmail, ["OWNER_EMAIL"] = "owner@example.test" },
    };

    /// <summary>Signs up with <paramref name="email"/> and marks the address verified, as opening the emailed link would.</summary>
    private static async Task<(HttpClient Browser, string UserId)> SignUpVerifiedAsync(ApiFactory factory, string email)
    {
        var browser = factory.CreateBrowser();
        using var res = await AuthEndpointTests.SignUpAsync(browser, email, $"pw-{TestSecrets.NewSecret()}", email.Split('@')[0]);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var userId = (await AuthEndpointTests.Json(res)).GetProperty("user").GetProperty("id").GetString()!;
        factory.Data.Accounts[userId] = factory.Data.Accounts[userId] with { EmailVerified = true };
        return (browser, userId);
    }

    private static HttpRequestMessage Patch(string userId, string body) =>
        new(HttpMethod.Patch, $"/api/admin/users/{userId}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Admin_routes_do_not_exist_for_non_admins()
    {
        await using var factory = CreateFactory();
        var (user, userId) = await SignUpVerifiedAsync(factory, $"user-{Guid.NewGuid():N}@example.test");

        // An unverified admin address is not an admin yet.
        var unverified = factory.CreateBrowser();
        (await AuthEndpointTests.SignUpAsync(unverified, AdminEmail, $"pw-{TestSecrets.NewSecret()}", "Admin")).Dispose();

        foreach (var browser in new[] { user, unverified })
        {
            using var overview = await browser.GetAsync("/api/admin/overview");
            Assert.Equal(HttpStatusCode.NotFound, overview.StatusCode);
            Assert.Equal("Not found.", (await AuthEndpointTests.Json(overview)).GetProperty("error").GetString());

            // The body is not even read: a valid one, an invalid one and garbage look the same.
            foreach (var body in new[] { """{"plan":"pro"}""", """{"plan":"gold"}""", "not json" })
            {
                using var patch = await browser.SendAsync(Patch(userId, body));
                Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
            }
        }

        Assert.NotEqual(Plans.Pro, factory.Data.Settings.GetValueOrDefault(userId)?.Plan);

        // Signed out, every /api route answers 401 alike.
        using var anon = await factory.CreateBrowser().GetAsync("/api/admin/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }

    [Fact]
    public async Task An_admin_grants_and_removes_complimentary_pro()
    {
        await using var factory = CreateFactory();
        var (admin, _) = await SignUpVerifiedAsync(factory, AdminEmail);
        var (_, userId) = await SignUpVerifiedAsync(factory, $"user-{Guid.NewGuid():N}@example.test");
        var plans = factory.Services.GetService(typeof(PlanResolver)) as PlanResolver;

        using var grant = await admin.SendAsync(Patch(userId, """{"plan":"pro"}"""));
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        Assert.Equal("pro", (await AuthEndpointTests.Json(grant)).GetProperty("plan").GetString());
        Assert.Equal(Plans.Pro, await plans!.GetPlanAsync(factory.Data.Accounts[userId]));

        using var remove = await admin.SendAsync(Patch(userId, """{"plan":"free"}"""));
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.Equal(Plans.Free, await plans.GetPlanAsync(factory.Data.Accounts[userId]));

        // A paid subscription is not touched by the toggle.
        factory.Data.Subscriptions[userId] = new Subscription { UserId = userId, StripeCustomerId = "cus_1", StripeSubscriptionId = "sub_1", Status = "active" };
        Assert.Equal(Plans.Pro, await plans.GetPlanAsync(factory.Data.Accounts[userId]));

        using var invalid = await admin.SendAsync(Patch(userId, """{"plan":"owner"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var garbage = await admin.SendAsync(Patch(userId, "not json"));
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);

        using var missing = await admin.SendAsync(Patch("no-such-user", """{"plan":"pro"}"""));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("User not found.", (await AuthEndpointTests.Json(missing)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_overview_has_users_sign_ups_subscribers_ai_cost_and_errors()
    {
        await using var factory = CreateFactory();
        factory.Clock.Now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var (admin, adminId) = await SignUpVerifiedAsync(factory, AdminEmail);
        var (_, paidId) = await SignUpVerifiedAsync(factory, $"paid-{Guid.NewGuid():N}@example.test");
        var (_, compId) = await SignUpVerifiedAsync(factory, $"comp-{Guid.NewGuid():N}@example.test");
        var (_, ownerId) = await SignUpVerifiedAsync(factory, "owner@example.test");

        factory.Data.Subscriptions[paidId] = new Subscription { UserId = paidId, StripeCustomerId = "cus_1", StripeSubscriptionId = "sub_1", Status = "past_due" };
        factory.Data.Settings[compId] = factory.Data.Settings[compId] with { Plan = Plans.Pro };
        factory.Data.AiUsage[paidId] =
        [
            TestData.Usage("u1", "rubric_score", plan: "pro", cost: 0.5) with { CreatedAt = factory.Clock.Now.AddDays(-1) },
            TestData.Usage("u2", "outline_check", plan: "pro", cost: 0.25) with { CreatedAt = factory.Clock.Now.AddDays(-1) },
            TestData.Usage("u3", "rubric_score", keySource: "user", plan: "pro", cost: 9) with { CreatedAt = factory.Clock.Now.AddDays(-1) },
            // Last month: not counted.
            TestData.Usage("u4", "rubric_score", plan: "pro", cost: 100) with { CreatedAt = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero) },
        ];
        factory.Data.AiUsage[compId] = [TestData.Usage("u5", "rubric_score_fallback", plan: "free", cost: 0.125) with { CreatedAt = factory.Clock.Now }];
        factory.Data.Errors.Add(new ErrorEvent { Id = "e1", Source = "server", Message = "boom", Path = "POST /api/x", CreatedAt = factory.Clock.Now.AddHours(-2) });
        factory.Data.Errors.Add(new ErrorEvent { Id = "e0", Source = "client", Message = "old", CreatedAt = factory.Clock.Now.AddDays(-3) });

        using var response = await admin.GetAsync("/api/admin/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await AuthEndpointTests.Json(response);

        Assert.Equal("2026-11-01T00:00:00.000Z", json.GetProperty("since").GetString());
        Assert.Equal(4, json.GetProperty("users").GetProperty("total").GetInt32());
        Assert.Equal(4, json.GetProperty("users").GetProperty("verified").GetInt32());
        Assert.Equal(4, json.GetProperty("users").GetProperty("createdLast7Days").GetInt32());

        var subscriptions = json.GetProperty("subscriptions");
        Assert.Equal(0, subscriptions.GetProperty("paying").GetInt32());
        Assert.Equal(1, subscriptions.GetProperty("pastDue").GetInt32());

        var cost = json.GetProperty("aiCost");
        Assert.Equal(0.875, cost.GetProperty("platformUsd").GetDouble(), 6);
        Assert.Equal(0.75, cost.GetProperty("proUsd").GetDouble(), 6);
        Assert.Equal(0.125, cost.GetProperty("freeUsd").GetDouble(), 6);
        Assert.Equal(3, json.GetProperty("essaysGraded").GetInt32());
        Assert.Equal(1, json.GetProperty("errorsLast24Hours").GetInt32());

        var accounts = json.GetProperty("accounts").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        Assert.Equal(Plans.Pro, accounts[paidId].GetProperty("plan").GetString());
        Assert.Equal("past_due", accounts[paidId].GetProperty("subscriptionStatus").GetString());
        Assert.Equal(2, accounts[paidId].GetProperty("essays").GetInt32());
        Assert.Equal(0.75, accounts[paidId].GetProperty("platformCostUsd").GetDouble(), 6);
        Assert.Equal(Plans.Pro, accounts[compId].GetProperty("plan").GetString());
        Assert.True(accounts[compId].GetProperty("complimentaryPro").GetBoolean());
        Assert.Equal(Plans.Owner, accounts[ownerId].GetProperty("plan").GetString());
        Assert.Equal(Plans.Free, accounts[adminId].GetProperty("plan").GetString());

        var errors = json.GetProperty("recentErrors").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(["e1", "e0"], errors);

        // The search narrows the list by email.
        using var searched = await admin.GetAsync("/api/admin/overview?q=PAID-");
        var found = (await AuthEndpointTests.Json(searched)).GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToList();
        Assert.Equal([paidId], found);
    }
}
