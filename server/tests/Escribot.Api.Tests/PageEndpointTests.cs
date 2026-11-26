using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Escribot.Api.Tests;

/// <summary>The data of the browser app's pages: the viewer, the public config and the page endpoints.</summary>
public class PageEndpointTests
{
    private static async Task<(HttpClient Browser, string UserId)> SignUpAsync(ApiFactory factory, string? name = "Ana María López")
    {
        var browser = factory.CreateBrowser();
        using var response = await AuthEndpointTests.SignUpAsync(browser, $"page-{Guid.NewGuid():N}@example.test", $"pw-{TestSecrets.NewSecret()}", name);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (browser, (await AuthEndpointTests.Json(response)).GetProperty("user").GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient browser, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        return await AuthEndpointTests.Json(response);
    }

    private static T Service<T>(ApiFactory factory)
        where T : notnull => factory.Services.GetRequiredService<T>();

    private static async Task<Prompt> SavePromptAsync(ApiFactory factory, string userId, string id, string taskType = "task1", string title = "Synthetic chart")
    {
        var prompt = TestData.Prompt(id) with { UserId = userId, TaskType = taskType, Title = title };
        await Service<IPromptRepository>(factory).SaveAsync(userId, prompt);
        return prompt;
    }

    [Fact]
    public async Task The_viewer_is_null_when_signed_out_and_the_person_with_their_plan_when_signed_in()
    {
        await using var factory = new ApiFactory();
        var anonymous = await GetJsonAsync(factory.CreateBrowser(), "/api/viewer");
        Assert.Equal(JsonValueKind.Null, anonymous.GetProperty("user").ValueKind);

        var (browser, userId) = await SignUpAsync(factory);
        using var response = await browser.GetAsync("/api/viewer");
        Assert.True(response.Headers.CacheControl is { NoStore: true, Private: true });
        var user = (await AuthEndpointTests.Json(response)).GetProperty("user");
        Assert.Equal(
            ["id", "name", "email", "emailVerified", "hasPassword", "plan", "canUpgrade", "isAdmin", "onboarded"],
            [.. user.EnumerateObject().Select(p => p.Name)]);
        Assert.Equal(userId, user.GetProperty("id").GetString());
        Assert.Equal("Ana María López", user.GetProperty("name").GetString());
        Assert.True(user.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("free", user.GetProperty("plan").GetString());
        // Billing is not configured in these tests, so there is nothing to upgrade to.
        Assert.False(user.GetProperty("canUpgrade").GetBoolean());
        Assert.False(user.GetProperty("isAdmin").GetBoolean());
        Assert.False(user.GetProperty("onboarded").GetBoolean());

        (await browser.PutAsJsonAsync("/api/settings", new { completeOnboarding = true })).Dispose();
        Assert.True((await GetJsonAsync(browser, "/api/viewer")).GetProperty("user").GetProperty("onboarded").GetBoolean());
    }

    [Fact]
    public async Task The_public_config_has_the_site_operator_details()
    {
        await using (var unset = new ApiFactory())
        {
            var site = (await GetJsonAsync(unset.CreateBrowser(), "/api/config")).GetProperty("site");
            Assert.Equal("the operator of Escribot", site.GetProperty("operator").GetString());
            Assert.Equal(JsonValueKind.Null, site.GetProperty("supportEmail").ValueKind);
            Assert.Equal(JsonValueKind.Null, site.GetProperty("jurisdiction").ValueKind);
        }

        await using var factory = new ApiFactory
        {
            Settings = new() { ["SITE_OPERATOR"] = " Example Operator ", ["SUPPORT_EMAIL"] = "help@example.test", ["SITE_JURISDICTION"] = "Spain" },
        };
        var configured = (await GetJsonAsync(factory.CreateBrowser(), "/api/config")).GetProperty("site");
        Assert.Equal("Example Operator", configured.GetProperty("operator").GetString());
        Assert.Equal("help@example.test", configured.GetProperty("supportEmail").GetString());
        Assert.Equal("Spain", configured.GetProperty("jurisdiction").GetString());
    }

    [Fact]
    public async Task Page_data_needs_a_session()
    {
        await using var factory = new ApiFactory();
        var anonymous = factory.CreateBrowser();
        foreach (var path in new[] { "/api/pages/studio", "/api/pages/write/sess-1", "/api/pages/analytics", "/api/pages/settings", "/api/pages/drills" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_studio_lists_prompts_recent_sessions_and_the_settings_summary()
    {
        await using var factory = new ApiFactory();
        var (browser, userId) = await SignUpAsync(factory);
        (await browser.PutAsJsonAsync("/api/settings", new { currentBand = 6, targetBand = 7.5, examDate = "2026-12-24", nativeLanguage = "pt" })).Dispose();
        await SavePromptAsync(factory, userId, "custom-a", title: "Chart A");
        var writing = Service<IWritingRepository>(factory);
        await writing.SaveSessionAsync(userId, TestData.Session("sess-a") with { Task1PromptId = "custom-a" });
        await writing.SaveDraftAsync(userId, TestData.Draft("draft-1", "sess-a") with { PromptId = "custom-a", Version = 1 });
        await writing.SaveDraftAsync(userId, TestData.Draft("draft-2", "sess-a", 5) with { PromptId = "custom-a", Version = 2 });
        await writing.SaveEvaluationAsync(userId, TestData.Evaluation("eval-1", "sess-a", "draft-1"));

        var studio = await GetJsonAsync(browser, "/api/pages/studio");
        Assert.Equal("custom-a", Assert.Single(studio.GetProperty("prompts").EnumerateArray()).GetProperty("id").GetString());

        var session = Assert.Single(studio.GetProperty("recentSessions").EnumerateArray());
        Assert.Equal("sess-a", session.GetProperty("id").GetString());
        // How many sessions use each uploaded prompt, for the edit window.
        Assert.Equal(1, studio.GetProperty("promptUsage").GetProperty("custom-a").GetInt32());
        Assert.Equal("Chart A", session.GetProperty("task1Title").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("task2Title").ValueKind);
        // Only graded drafts count: version 2 has no evaluation yet.
        Assert.Equal(1, session.GetProperty("latestDraftVersion").GetInt32());

        var summary = studio.GetProperty("settingsSummary");
        Assert.Equal(6, summary.GetProperty("currentBand").GetDouble());
        Assert.Equal(7.5, summary.GetProperty("targetBand").GetDouble());
        Assert.Equal("2026-12-24", summary.GetProperty("examDate").GetString());
        Assert.Equal("pt", summary.GetProperty("nativeLanguage").GetString());
        Assert.False(summary.GetProperty("activeProviderReady").GetBoolean());
    }

    [Fact]
    public async Task A_coach_session_restores_the_saved_draft_and_the_previous_priorities()
    {
        await using var factory = new ApiFactory();
        var (browser, userId) = await SignUpAsync(factory);
        await SavePromptAsync(factory, userId, "custom-t2", taskType: "task2");
        var writing = Service<IWritingRepository>(factory);
        await writing.SaveSessionAsync(userId, TestData.Session("sess-c") with { Task2PromptId = "custom-t2" });
        await writing.SaveDraftAsync(userId, TestData.Draft("draft-1", "sess-c") with { PromptId = "custom-t2", Version = 1, OutlineFeedback = TestData.Json("""{"verdict":"ok"}""") });
        await writing.SaveEvaluationAsync(userId, TestData.Evaluation("eval-1", "sess-c", "draft-1") with
        {
            OverallBand = 6,
            TopPriorities = TestData.Json("""["Use a clear thesis", " ", 3, "Vary linking words"]"""),
        });

        var page = await GetJsonAsync(browser, "/api/pages/write/sess-c");
        Assert.Equal("coach", page.GetProperty("mode").GetString());
        var coach = page.GetProperty("coach");
        Assert.Equal("custom-t2", coach.GetProperty("prompt").GetProperty("id").GetString());

        var draft = coach.GetProperty("initialDraft");
        Assert.Equal("Synthetic essay text for a test.", draft.GetProperty("essayText").GetString());
        Assert.Equal("synthetic", draft.GetProperty("outline").GetProperty("thesis").GetString());
        Assert.Equal("ok", draft.GetProperty("outlineFeedback").GetProperty("verdict").GetString());

        var focus = coach.GetProperty("previousFocus");
        Assert.Equal(6, focus.GetProperty("overallBand").GetDouble());
        Assert.Equal(["Use a clear thesis", "Vary linking words"], [.. focus.GetProperty("topPriorities").EnumerateArray().Select(p => p.GetString()!)]);
        Assert.Equal(1, focus.GetProperty("fromVersion").GetInt32());
        Assert.True(focus.GetProperty("isRedraft").GetBoolean());
    }

    [Fact]
    public async Task A_simulator_session_restores_each_task_and_another_users_session_is_not_found()
    {
        await using var factory = new ApiFactory();
        var (browser, userId) = await SignUpAsync(factory);
        await SavePromptAsync(factory, userId, "custom-t1");
        await SavePromptAsync(factory, userId, "custom-t2", taskType: "task2");
        var writing = Service<IWritingRepository>(factory);
        await writing.SaveSessionAsync(userId, TestData.Session("sess-s") with { Mode = "simulator_full_60m", Task1PromptId = "custom-t1", Task2PromptId = "custom-t2" });
        await writing.SaveDraftAsync(userId, TestData.Draft("draft-1", "sess-s") with
        {
            PromptId = "custom-t1",
            TaskType = "task1",
            PromptHighlights = TestData.Json("""["rose sharply"]"""),
            ScratchNotes = "synthetic notes",
        });

        var simulator = (await GetJsonAsync(browser, "/api/pages/write/sess-s")).GetProperty("simulator");
        Assert.Equal("custom-t1", simulator.GetProperty("task1Prompt").GetProperty("id").GetString());
        Assert.Equal("custom-t2", simulator.GetProperty("task2Prompt").GetProperty("id").GetString());
        var task1 = simulator.GetProperty("initialDrafts").GetProperty("task1");
        Assert.Equal("rose sharply", Assert.Single(task1.GetProperty("promptHighlights").EnumerateArray()).GetString());
        Assert.Equal("synthetic notes", task1.GetProperty("scratchNotes").GetString());
        Assert.Equal(JsonValueKind.Null, simulator.GetProperty("initialDrafts").GetProperty("task2").ValueKind);

        var (other, _) = await SignUpAsync(factory);
        await GetJsonAsync(other, "/api/pages/write/sess-s", HttpStatusCode.NotFound);
        await GetJsonAsync(browser, "/api/pages/write/sess-missing", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Analytics_add_up_graded_work_and_this_months_spend()
    {
        await using var factory = new ApiFactory();
        var (browser, userId) = await SignUpAsync(factory);
        await SavePromptAsync(factory, userId, "custom-t1", title: "Chart");
        var writing = Service<IWritingRepository>(factory);
        await writing.SaveSessionAsync(userId, TestData.Session("sess-a") with { Task1PromptId = "custom-t1" });
        await writing.SaveDraftAsync(userId, TestData.Draft("draft-1", "sess-a") with { PromptId = "custom-t1", TaskType = "task1", WordCount = 180 });
        await writing.SaveDraftAsync(userId, TestData.Draft("draft-ungraded", "sess-a", 1) with { PromptId = "custom-t1", TaskType = "task1", WordCount = 999 });
        var now = factory.Clock.Now;
        await writing.SaveEvaluationAsync(userId, TestData.Evaluation("eval-1", "sess-a", "draft-1") with { OverallBand = 6.5, TaTrBand = 6, CcBand = 7, LrBand = 6.5, GraBand = 6, CreatedAt = now });
        var usage = Service<IAiUsageRepository>(factory);
        await usage.RecordAsync(userId, TestData.Usage("u-1", "rubric_score", cost: 0.25) with { CreatedAt = now });
        await usage.RecordAsync(userId, TestData.Usage("u-2", "toolkit", cost: 0.05) with { CreatedAt = now });
        // Last month's calls are not this month's spend.
        await usage.RecordAsync(userId, TestData.Usage("u-old", "toolkit", cost: 9) with { CreatedAt = now.AddDays(-40) });

        var analytics = await GetJsonAsync(browser, "/api/pages/analytics");
        Assert.Equal(1, analytics.GetProperty("totalEssaysGraded").GetInt32());
        Assert.Equal(180, analytics.GetProperty("totalWordsWritten").GetInt32());
        // TA_TR and GRA tie for lowest; the first in the rubric's order wins.
        Assert.Equal("TA_TR", analytics.GetProperty("weakestCriterion").GetString());
        var point = Assert.Single(analytics.GetProperty("trendPoints").EnumerateArray());
        Assert.Equal("T1 v1", point.GetProperty("label").GetString());
        var row = Assert.Single(analytics.GetProperty("sessionRows").EnumerateArray());
        Assert.Equal("Chart", row.GetProperty("title").GetString());
        Assert.Equal(1, row.GetProperty("draftCount").GetInt32());
        Assert.Equal(0.3, analytics.GetProperty("monthlySpendUsd").GetDouble(), 6);
        Assert.Equal(2, analytics.GetProperty("usageCallsCount").GetInt32());
        Assert.Equal(0.25, analytics.GetProperty("featureSpend").GetProperty("rubric_score").GetDouble(), 6);
    }

    [Fact]
    public async Task Settings_and_drills_pages_get_what_they_show()
    {
        await using var factory = new ApiFactory();
        var (browser, userId) = await SignUpAsync(factory);
        (await browser.PutAsJsonAsync("/api/settings", new { currentBand = 7.5 })).Dispose();

        Assert.Equal(7.5, (await GetJsonAsync(browser, "/api/pages/drills")).GetProperty("currentBand").GetDouble());
        Assert.False((await GetJsonAsync(browser, "/api/pages/settings")).GetProperty("hasSubscription").GetBoolean());

        factory.Data.Subscriptions[userId] = new Subscription { UserId = userId, StripeCustomerId = "cus_synthetic", StripeSubscriptionId = "sub_synthetic", Status = "active" };
        Assert.True((await GetJsonAsync(browser, "/api/pages/settings")).GetProperty("hasSubscription").GetBoolean());
    }
}
