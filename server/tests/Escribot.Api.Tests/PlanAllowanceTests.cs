using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Escribot.Api.Ai;
using Escribot.Api.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Escribot.Api.Tests;

/// <summary>
/// The monthly AI allowances come from deployment settings, are validated strictly, and
/// cannot be changed or skipped by anything a request carries.
/// </summary>
public class PlanAllowanceTests
{
    private static PlanCatalog Catalog(Dictionary<string, string?>? values = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build());

    [Fact]
    public void Defaults_are_3_and_30_for_free_100_and_1500_for_pro_and_unlimited_for_owner()
    {
        var catalog = Catalog();
        Assert.Equal(3, catalog[Plans.Free].EvaluationsPerMonth);
        Assert.Equal(30, catalog[Plans.Free].AssistsPerMonth);
        Assert.Equal(100, catalog[Plans.Pro].EvaluationsPerMonth);
        Assert.Equal(1500, catalog[Plans.Pro].AssistsPerMonth);
        Assert.Null(catalog[Plans.Owner].EvaluationsPerMonth);
        Assert.Null(catalog[Plans.Owner].AssistsPerMonth);
    }

    [Fact]
    public void Settings_override_the_monthly_allowances_and_nothing_else()
    {
        var catalog = Catalog(new()
        {
            ["FREE_PLAN_EVALUATIONS_PER_MONTH"] = " 7 ",
            ["FREE_PLAN_ASSISTS_PER_MONTH"] = "0",
            ["PRO_PLAN_EVALUATIONS_PER_MONTH"] = "250",
            ["PRO_PLAN_ASSISTS_PER_MONTH"] = "",
        });

        Assert.Equal(7, catalog[Plans.Free].EvaluationsPerMonth);
        Assert.Equal(0, catalog[Plans.Free].AssistsPerMonth);
        Assert.Equal(250, catalog[Plans.Pro].EvaluationsPerMonth);
        Assert.Equal(1500, catalog[Plans.Pro].AssistsPerMonth); // blank keeps the default
        Assert.Equal(Plans.Defaults[Plans.Free] with { EvaluationsPerMonth = 7, AssistsPerMonth = 0 }, catalog[Plans.Free]);
        Assert.Equal(Plans.Defaults[Plans.Owner], catalog[Plans.Owner]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("1,000")]
    [InlineData("0x10")]
    [InlineData("unlimited")]
    [InlineData("null")]
    [InlineData("１２")] // full-width digits
    [InlineData("1000001")]
    [InlineData("99999999999")]
    public void A_bad_value_is_refused_rather_than_ignored_or_treated_as_unlimited(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Catalog(new() { ["FREE_PLAN_EVALUATIONS_PER_MONTH"] = value }));
        Assert.Contains("FREE_PLAN_EVALUATIONS_PER_MONTH", ex.Message);
    }

    [Fact]
    public void An_unknown_plan_gets_the_free_limits()
    {
        var catalog = Catalog(new() { ["FREE_PLAN_EVALUATIONS_PER_MONTH"] = "1" });
        Assert.Equal(catalog[Plans.Free], catalog["enterprise"]);
    }

    [Fact]
    public async Task The_public_config_shows_the_allowances_in_force()
    {
        await using var factory = new ApiFactory
        {
            Settings = new() { ["FREE_PLAN_EVALUATIONS_PER_MONTH"] = "4", ["PRO_PLAN_ASSISTS_PER_MONTH"] = "900" },
        };
        using var res = await factory.CreateBrowser().GetAsync("/api/config");
        var plans = (await AuthEndpointTests.Json(res)).GetProperty("plans");

        Assert.Equal(4, plans.GetProperty("free").GetProperty("evaluationsPerMonth").GetInt32());
        Assert.Equal(30, plans.GetProperty("free").GetProperty("assistsPerMonth").GetInt32());
        Assert.Equal(100, plans.GetProperty("pro").GetProperty("evaluationsPerMonth").GetInt32());
        Assert.Equal(900, plans.GetProperty("pro").GetProperty("assistsPerMonth").GetInt32());
        Assert.Equal(JsonValueKind.Null, plans.GetProperty("owner").GetProperty("evaluationsPerMonth").ValueKind);
    }

    [Fact]
    public async Task The_configured_allowance_is_enforced_and_a_request_cannot_raise_it()
    {
        var provider = new FakeChatClientFactory((_, userPrompt) => userPrompt.Contains("taTrBand", StringComparison.Ordinal)
            ? new FakeChatResult(
                """{"taTrBand":7.0,"ccBand":7.0,"lrBand":7.0,"graBand":7.0,"examinerSummary":"Good.","criteria":{"TA_TR":{"band":7.0},"CC":{"band":7.0},"LR":{"band":7.0},"GRA":{"band":7.0}},"topPriorities":[]}""",
                "m")
            : new FakeChatResult("""{"inlineAnnotations":[],"targetBandRewrite":"Improved.","rewriteKeyChanges":[],"vaultItems":[]}""", "m"));

        await using var factory = new ApiFactory
        {
            Settings = new() { ["OPENROUTER_API_KEY"] = "or-test-key", ["FREE_PLAN_EVALUATIONS_PER_MONTH"] = "1" },
            ConfigureServices = services =>
            {
                services.RemoveAll<IAiChatClientFactory>();
                services.AddSingleton<IAiChatClientFactory>(provider);
            },
        };

        var browser = factory.CreateBrowser();
        using (var signUp = await AuthEndpointTests.SignUpAsync(browser, $"allow-{Guid.NewGuid():N}@example.test", $"pw-{TestSecrets.NewSecret()}", "Allowance"))
        {
            Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);
        }

        using var promptRes = await browser.PostAsJsonAsync("/api/prompts", new { taskType = "task2", promptText = "Discuss tech.", title = "Tech" });
        var promptId = (await AuthEndpointTests.Json(promptRes)).GetProperty("prompt").GetProperty("id").GetString()!;
        using var sessionRes = await browser.PostAsJsonAsync("/api/sessions", new { mode = "coach", task2PromptId = promptId });
        var sessionId = (await AuthEndpointTests.Json(sessionRes)).GetProperty("session").GetProperty("id").GetString()!;

        // Each request tries to raise its allowance with a query string, a header and body fields
        // named like the setting or the plan. None of them is read.
        async Task<HttpResponseMessage> EvaluateAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ai/evaluate?FREE_PLAN_EVALUATIONS_PER_MONTH=100&plan=owner")
            {
                Content = JsonContent.Create(new
                {
                    sessionId,
                    plan = "owner",
                    FREE_PLAN_EVALUATIONS_PER_MONTH = 100,
                    evaluationsPerMonth = 100,
                    submissions = new[] { new { promptId, taskType = "task2", essayText = "Technology has transformed education across the globe in recent decades." } },
                }),
            };
            request.Headers.Add("FREE_PLAN_EVALUATIONS_PER_MONTH", "100");
            request.Headers.Add("X-Plan", "owner");
            var response = await browser.SendAsync(request);
            await response.Content.ReadAsStringAsync();
            return response;
        }

        using (var first = await EvaluateAsync())
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await EvaluateAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var body = await AuthEndpointTests.Json(second);
        Assert.Equal("quota_exceeded", body.GetProperty("code").GetString());
        Assert.Contains("You have used all 1 ", body.GetProperty("error").GetString());

        // The Settings page reports the same limit the guard enforced.
        using var settingsRes = await browser.GetAsync("/api/settings");
        var usage = (await AuthEndpointTests.Json(settingsRes)).GetProperty("usage");
        Assert.Equal("""{"used":1,"limit":1}""", usage.GetProperty("evaluations").GetRawText());
    }
}
