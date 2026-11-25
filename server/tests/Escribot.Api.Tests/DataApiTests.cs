using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;
using Escribot.Api.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Escribot.Api.Tests;

/// <summary>
/// The data endpoints, called over HTTP on an API that runs on the Cosmos DB and Blob
/// Storage emulators. The isolation checks follow scripts/test-isolation.mjs.
/// All content is synthetic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AppHostCollection.Name)]
public class DataApiTests(AppHostFixture fixture)
{
    // A 1x1 pixel PNG.
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    private const string Png = $"data:image/png;base64,{PngBase64}";

    private readonly string _password = $"pw-{TestSecrets.NewSecret()}";

    private T Service<T>()
        where T : notnull => fixture.Api.Services.GetRequiredService<T>();

    private async Task<(HttpClient Browser, string UserId, string Email)> SignUpAsync()
    {
        var email = $"data-{Guid.NewGuid():N}@example.test";
        var browser = fixture.Api.CreateBrowser();
        using var response = await AuthEndpointTests.SignUpAsync(browser, email, _password, "Data Tester");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (browser, (await AuthEndpointTests.Json(response)).GetProperty("user").GetProperty("id").GetString()!, email);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient browser, string path)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await AuthEndpointTests.Json(response);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient browser, string path, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await browser.PostAsJsonAsync(path, body);
        Assert.Equal(expected, response.StatusCode);
        return await AuthEndpointTests.Json(response);
    }

    private static string[] Names(JsonElement value) => [.. value.EnumerateObject().Select(p => p.Name)];

    // ---- Prompts and images ----

    [Fact]
    public async Task Prompts_are_the_curated_bank_plus_the_users_own_in_the_old_row_shape()
    {
        var (browser, userId, _) = await SignUpAsync();
        var created = (await PostJsonAsync(browser, "/api/prompts", new { taskType = "task2", promptText = "  Some people think synthetic tests are useful. Discuss.  " })).GetProperty("prompt");

        Assert.Equal(
            ["id", "userId", "taskType", "category", "topic", "title", "promptText", "chartSpecJson", "imageDataUrl", "extractedVisualSpec", "source", "createdAt"],
            Names(created));
        Assert.StartsWith("custom-", created.GetProperty("id").GetString());
        Assert.Equal(userId, created.GetProperty("userId").GetString());
        Assert.Equal("opinion", created.GetProperty("category").GetString());
        Assert.Equal("Custom Practice", created.GetProperty("topic").GetString());
        Assert.Equal("Custom Task 2 Essay", created.GetProperty("title").GetString());
        Assert.Equal("Some people think synthetic tests are useful. Discuss.", created.GetProperty("promptText").GetString());
        Assert.Equal("custom", created.GetProperty("source").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", created.GetProperty("createdAt").GetString());

        var all = (await GetJsonAsync(browser, "/api/prompts")).GetProperty("prompts").EnumerateArray().ToList();
        var curated = all.Where(p => p.GetProperty("source").GetString() == "curated").ToList();
        Assert.Equal(CuratedPromptSeed.LoadEmbedded().Count, curated.Count);
        Assert.All(curated, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("userId").ValueKind));
        // A chart specification travels as JSON text, as it did when it was a text column.
        var withChart = curated.First(p => p.GetProperty("chartSpecJson").ValueKind == JsonValueKind.String);
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(withChart.GetProperty("chartSpecJson").GetString()!).RootElement.ValueKind);
        Assert.Contains(all, p => p.GetProperty("id").GetString() == created.GetProperty("id").GetString());

        var task2 = (await GetJsonAsync(browser, "/api/prompts?taskType=task2")).GetProperty("prompts").EnumerateArray().ToList();
        Assert.NotEmpty(task2);
        Assert.All(task2, p => Assert.Equal("task2", p.GetProperty("taskType").GetString()));
    }

    [Theory]
    [InlineData("""{ "promptText": "No task type." }""")]
    [InlineData("""{ "taskType": "task3", "promptText": "Unknown task type." }""")]
    [InlineData("""{ "taskType": "task1", "promptText": "   " }""")]
    [InlineData("""{ "taskType": "task1", "promptText": 5 }""")]
    [InlineData("[]")]
    public async Task A_prompt_needs_a_task_type_and_instructions(string body)
    {
        var (browser, _, _) = await SignUpAsync();

        using var response = await browser.PostAsync("/api/prompts", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Task type and prompt instructions are required.", (await AuthEndpointTests.Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_uploaded_image_is_stored_in_blob_storage_and_served_only_to_its_owner()
    {
        var (ana, anaId, _) = await SignUpAsync();
        var (ben, _, _) = await SignUpAsync();

        var prompt = (await PostJsonAsync(ana, "/api/prompts", new { taskType = "task1", promptText = "Describe the chart.", imageDataUrl = Png })).GetProperty("prompt");
        var imageUrl = prompt.GetProperty("imageDataUrl").GetString()!;
        Assert.StartsWith("/api/images/", imageUrl);

        using var own = await ana.GetAsync(imageUrl);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("image/png", own.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Convert.FromBase64String(PngBase64), await own.Content.ReadAsByteArrayAsync());
        Assert.True(own.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromDays(1), own.Headers.CacheControl.MaxAge);
        Assert.Contains(own.Headers.CacheControl.Extensions, e => e.Name == "immutable");
        Assert.Equal("nosniff", own.Headers.GetValues("X-Content-Type-Options").Single());

        using var others = await ben.GetAsync(imageUrl);
        Assert.Equal(HttpStatusCode.NotFound, others.StatusCode);
        using var signedOut = await fixture.Api.CreateBrowser().GetAsync(imageUrl);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        using var missing = await ana.GetAsync("/api/images/not-an-image");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // The bytes are in the blob store under the owner's folder, not in the database.
        var metadata = Assert.Single(await Service<IImageRepository>().ListAsync(anaId));
        Assert.StartsWith($"users/{anaId}/images/", metadata.BlobName);
        Assert.EndsWith(".png", metadata.BlobName);
        Assert.NotNull(await Service<IFileStore>().ReadAsync(metadata.BlobName, default));

        // Deleting the account removes the file as well.
        using var deleted = await ana.PostAsJsonAsync("/api/auth/delete-user", new { password = _password });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Null(await Service<IFileStore>().ReadAsync(metadata.BlobName, default));
    }

    [Fact]
    public async Task Uploads_are_limited_by_type_size_and_the_plans_allowance()
    {
        var (browser, userId, _) = await SignUpAsync();

        var wrongType = await PostJsonAsync(browser, "/api/prompts", new { taskType = "task1", promptText = "x", imageDataUrl = "data:image/bmp;base64,Qk0=" }, HttpStatusCode.BadRequest);
        Assert.Equal("Only PNG, JPEG, WebP or GIF images can be uploaded.", wrongType.GetProperty("error").GetString());

        // SVGs are a Pro feature.
        const string emptySvg = "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=";
        var freeSvg = await PostJsonAsync(browser, "/api/prompts", new { taskType = "task1", promptText = "x", imageDataUrl = emptySvg }, HttpStatusCode.Forbidden);
        Assert.Equal("upgrade_required", freeSvg.GetProperty("code").GetString());

        var oversizedBytes = new byte[(2 * 1024 * 1024) + 1];
        ((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]).CopyTo(oversizedBytes);
        var tooBig = $"data:image/png;base64,{Convert.ToBase64String(oversizedBytes)}";
        var oversize = await PostJsonAsync(browser, "/api/prompts", new { taskType = "task1", promptText = "x", imageDataUrl = tooBig }, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal("Images must be 2 MB or smaller.", oversize.GetProperty("error").GetString());

        // A Free account that has already used its 25 MB.
        await Service<IImageRepository>().SaveAsync(userId, TestData.Image("full", 25L * 1024 * 1024));
        var overAllowance = await PostJsonAsync(browser, "/api/prompts", new { taskType = "task1", promptText = "x", imageDataUrl = Png }, HttpStatusCode.RequestEntityTooLarge);
        Assert.StartsWith("You have used your 25 MB of image storage.", overAllowance.GetProperty("error").GetString());

        // Complimentary Pro has room again.
        await Service<ISettingsRepository>().UpdateAsync(userId, s => s with { Plan = "pro" });
        await PostJsonAsync(browser, "/api/prompts", new { taskType = "task1", promptText = "x", imageDataUrl = Png });

        // Pro takes SVGs, but only valid ones, and names them among the accepted types.
        var proWrongType = await PostJsonAsync(browser, "/api/prompts", new { taskType = "task1", promptText = "x", imageDataUrl = "data:image/bmp;base64,Qk0=" }, HttpStatusCode.BadRequest);
        Assert.Equal("Only PNG, JPEG, WebP, GIF or SVG images can be uploaded.", proWrongType.GetProperty("error").GetString());
        var invalidSvg = await PostJsonAsync(browser, "/api/prompts", new { taskType = "task1", promptText = "x", imageDataUrl = emptySvg }, HttpStatusCode.BadRequest);
        Assert.Equal("The SVG image is not valid.", invalidSvg.GetProperty("error").GetString());

        // Nothing was saved for the refused uploads, and a Task 2 prompt takes no image at all.
        var task2 = (await PostJsonAsync(browser, "/api/prompts", new { taskType = "task2", promptText = "x", imageDataUrl = Png })).GetProperty("prompt");
        Assert.Equal(JsonValueKind.Null, task2.GetProperty("imageDataUrl").ValueKind);
        Assert.Equal(2, (await Service<IImageRepository>().ListAsync(userId)).Count);
    }

    [Fact]
    public async Task An_image_stored_for_a_save_that_failed_is_removed_again()
    {
        var (_, userId, _) = await SignUpAsync();
        var store = Service<Api.ImageStore>();
        var url = await store.StoreAsync(userId, "free", Png);
        var metadata = Assert.Single(await Service<IImageRepository>().ListAsync(userId));

        await store.DiscardAsync(userId, url);

        Assert.Empty(await Service<IImageRepository>().ListAsync(userId));
        Assert.Null(await Service<IFileStore>().ReadAsync(metadata.BlobName, default));
        Assert.Equal(0, await Service<IImageRepository>().GetTotalBytesAsync(userId));
        // Anything that is not one of the user's stored images is left alone.
        await store.DiscardAsync(userId, "https://example.test/elsewhere.png");
        await store.DiscardAsync(userId, null);
    }

    // ---- Sessions ----

    [Fact]
    public async Task A_session_is_created_listed_and_fetched_with_its_drafts_and_evaluations()
    {
        var (browser, userId, _) = await SignUpAsync();
        var prompt = (await PostJsonAsync(browser, "/api/prompts", new { taskType = "task2", promptText = "Discuss both views." })).GetProperty("prompt");
        var promptId = prompt.GetProperty("id").GetString()!;

        var session = (await PostJsonAsync(browser, "/api/sessions", new { mode = "simulator_t2", task2PromptId = promptId })).GetProperty("session");
        Assert.Equal(
            ["id", "userId", "mode", "status", "task1PromptId", "task2PromptId", "startedAt", "completedAt", "totalSecondsSpent", "weightedBand"],
            Names(session));
        var sessionId = session.GetProperty("id").GetString()!;
        Assert.StartsWith("sess-", sessionId);
        Assert.Equal("in_progress", session.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("task1PromptId").ValueKind);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("weightedBand").ValueKind);

        // Graded work, as the AI endpoints will save it.
        var writing = Service<IWritingRepository>();
        await writing.SaveDraftAsync(userId, TestData.Draft("d2", sessionId, minutes: 2) with { PromptId = promptId, Version = 2, WordCount = 260 });
        await writing.SaveDraftAsync(userId, TestData.Draft("d1", sessionId, minutes: 1) with { PromptId = promptId, Version = 1, WordCount = 250 });
        await writing.SaveEvaluationAsync(userId, TestData.Evaluation("e1", sessionId, "d1", minutes: 3) with { OverallBand = 6, TaTrBand = 6, CcBand = 5.5, LrBand = 6.5, GraBand = 6 });
        await writing.SaveEvaluationAsync(userId, TestData.Evaluation("e2", sessionId, "d2", minutes: 4) with { OverallBand = 7, TaTrBand = 7, CcBand = 6, LrBand = 7, GraBand = 7.5 });

        var fetched = (await GetJsonAsync(browser, $"/api/sessions/{sessionId}")).GetProperty("session");
        Assert.Equal(promptId, fetched.GetProperty("task2Prompt").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, fetched.GetProperty("task1Prompt").ValueKind);
        var drafts = fetched.GetProperty("drafts").EnumerateArray().ToList();
        Assert.Equal(["d1", "d2"], drafts.Select(d => d.GetProperty("id").GetString()));
        Assert.Equal(
            ["id", "userId", "sessionId", "promptId", "taskType", "version", "outlineJson", "outlineFeedbackJson", "essayText", "wordCount", "secondsSpent", "promptHighlightsJson", "scratchNotes", "createdAt", "prompt", "evaluation"],
            Names(drafts[0]));
        Assert.Equal("""{"thesis":"synthetic"}""", drafts[0].GetProperty("outlineJson").GetString());
        Assert.Equal(promptId, drafts[0].GetProperty("prompt").GetProperty("id").GetString());
        var evaluation = drafts[0].GetProperty("evaluation");
        Assert.Equal(
            ["id", "userId", "draftId", "overallBand", "taTrBand", "ccBand", "lrBand", "graBand", "examinerSummary", "criterionFeedbackJson", "inlineAnnotationsJson", "targetBandRewrite", "rewriteKeyChangesJson", "topPrioritiesJson", "providerUsed", "modelUsed", "costUsd", "createdAt"],
            Names(evaluation));
        Assert.Equal("""{"TA_TR":{"band":6.5}}""", evaluation.GetProperty("criterionFeedbackJson").GetString());
        Assert.Equal("[]", evaluation.GetProperty("inlineAnnotationsJson").GetString());

        var list = await GetJsonAsync(browser, "/api/sessions");
        var listed = Assert.Single(list.GetProperty("sessions").EnumerateArray());
        Assert.Equal(sessionId, listed.GetProperty("id").GetString());
        Assert.Equal(2, listed.GetProperty("drafts").GetArrayLength());
        Assert.Equal("e2", listed.GetProperty("drafts")[0].GetProperty("evaluation").GetProperty("id").GetString());

        var analytics = list.GetProperty("analytics");
        Assert.Equal(2, analytics.GetProperty("totalEssaysGraded").GetInt32());
        Assert.Equal(510, analytics.GetProperty("totalWordsWritten").GetInt32());
        Assert.Equal("""{"overall":6.5,"TA_TR":6.5,"CC":5.75,"LR":6.75,"GRA":6.75}""", analytics.GetProperty("averages").GetRawText());
        Assert.Equal("CC", analytics.GetProperty("weakestCriterion").GetString());
        Assert.Equal(
            """[{"index":1,"date":"2026-03-01","label":"T2 v1","overall":6,"taTr":6,"cc":5.5,"lr":6.5,"gra":6},{"index":2,"date":"2026-03-01","label":"T2 v2","overall":7,"taTr":7,"cc":6,"lr":7,"gra":7.5}]""",
            analytics.GetProperty("trendPoints").GetRawText());
    }

    [Fact]
    public async Task A_new_account_has_no_sessions_and_empty_analytics()
    {
        var (browser, _, _) = await SignUpAsync();

        var list = await GetJsonAsync(browser, "/api/sessions");

        Assert.Equal(
            """{"sessions":[],"analytics":{"totalEssaysGraded":0,"totalWordsWritten":0,"averages":{"overall":0,"TA_TR":0,"CC":0,"LR":0,"GRA":0},"weakestCriterion":"CC","trendPoints":[]}}""",
            list.GetRawText());
    }

    [Fact]
    public async Task A_full_exam_session_picks_a_prompt_for_each_task()
    {
        var (browser, _, _) = await SignUpAsync();

        var session = (await PostJsonAsync(browser, "/api/sessions", new { mode = "simulator_full_60m" })).GetProperty("session");
        var fetched = (await GetJsonAsync(browser, $"/api/sessions/{session.GetProperty("id").GetString()}")).GetProperty("session");

        Assert.Equal("task1", fetched.GetProperty("task1Prompt").GetProperty("taskType").GetString());
        Assert.Equal("task2", fetched.GetProperty("task2Prompt").GetProperty("taskType").GetString());

        var coach = (await PostJsonAsync(browser, "/api/sessions", new { })).GetProperty("session");
        Assert.Equal("coach", coach.GetProperty("mode").GetString());
        await PostJsonAsync(browser, "/api/sessions", new { mode = "made_up" }, HttpStatusCode.BadRequest);
    }

    // ---- Vault and drills ----

    [Fact]
    public async Task Vault_items_are_added_reviewed_and_deleted()
    {
        var (browser, userId, _) = await SignUpAsync();
        var starter = (await GetJsonAsync(browser, "/api/vault")).GetProperty("items");
        Assert.Equal(4, starter.GetArrayLength());

        var item = (await PostJsonAsync(browser, "/api/vault", new { originalText = " big problem ", upgradedText = "pressing concern", itemType = "collocation" })).GetProperty("item");
        Assert.Equal(
            ["id", "userId", "sourceDraftId", "itemType", "criterion", "originalText", "upgradedText", "explanation", "exampleSentence", "masteryLevel", "reviewCount", "nextReviewAt", "createdAt"],
            Names(item));
        var id = item.GetProperty("id").GetString()!;
        Assert.Equal("big problem", item.GetProperty("originalText").GetString());
        Assert.Equal("collocation", item.GetProperty("itemType").GetString());
        Assert.Equal("LR", item.GetProperty("criterion").GetString());
        Assert.Equal("Saved manually to personal Vault.", item.GetProperty("explanation").GetString());
        Assert.Equal("pressing concern", item.GetProperty("exampleSentence").GetString());
        Assert.Equal(0, item.GetProperty("masteryLevel").GetInt32());

        await PostJsonAsync(browser, "/api/vault", new { originalText = "only one side" }, HttpStatusCode.BadRequest);

        using var patch = await browser.PatchAsJsonAsync("/api/vault", new { id, masteryLevel = 2.4 });
        var patched = await AuthEndpointTests.Json(patch);
        Assert.Equal(["ok", "masteryLevel", "nextReviewAt"], Names(patched));
        Assert.Equal(2, patched.GetProperty("masteryLevel").GetInt32());
        var stored = await Service<IVaultRepository>().GetAsync(userId, id);
        Assert.Equal(1, stored!.ReviewCount);
        Assert.InRange(stored.NextReviewAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromDays(2.9), TimeSpan.FromDays(3.1));

        using var tooHigh = await browser.PatchAsJsonAsync("/api/vault", new { id, masteryLevel = 99 });
        Assert.Equal(3, (await AuthEndpointTests.Json(tooHigh)).GetProperty("masteryLevel").GetInt32());
        using var noLevel = await browser.PatchAsJsonAsync("/api/vault", new { id, masteryLevel = "2" });
        Assert.Equal(HttpStatusCode.BadRequest, noLevel.StatusCode);
        using var unknown = await browser.PatchAsJsonAsync("/api/vault", new { id = "vault-nobody", masteryLevel = 1 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var noId = await browser.DeleteAsync("/api/vault");
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
        using var deleted = await browser.DeleteAsync($"/api/vault?id={id}");
        Assert.Equal("""{"ok":true}""", await deleted.Content.ReadAsStringAsync());
        Assert.Equal(4, (await GetJsonAsync(browser, "/api/vault")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Drills_list_the_twenty_most_recent()
    {
        var (browser, userId, _) = await SignUpAsync();
        var drills = Service<IDrillRepository>();
        for (var i = 0; i < 22; i++)
        {
            await drills.SaveAsync(userId, TestData.Drill($"drill-{i:00}") with { CreatedAt = TestData.Start.AddMinutes(i) });
        }

        var listed = (await GetJsonAsync(browser, "/api/drills")).GetProperty("drills").EnumerateArray().ToList();

        Assert.Equal(20, listed.Count);
        Assert.Equal("drill-21", listed[0].GetProperty("id").GetString());
        Assert.Equal(
            ["id", "userId", "drillType", "targetCriterion", "promptPayloadJson", "userResponse", "scoreJson", "createdAt", "completedAt"],
            Names(listed[0]));
        Assert.Equal("""{"sentence":"synthetic"}""", listed[0].GetProperty("promptPayloadJson").GetString());
    }

    // ---- Settings ----

    [Fact]
    public async Task Settings_start_with_defaults_and_report_usage_and_billing()
    {
        var (browser, _, _) = await SignUpAsync();

        var response = await GetJsonAsync(browser, "/api/settings");

        Assert.Equal(["settings", "providers", "usage", "billing"], Names(response));
        Assert.Equal(
            """{"currentBand":6.5,"targetBand":7.5,"examDate":"","nativeLanguage":"es","l1CoachingEnabled":true,"models":{"grader":{"provider":"openrouter","model":"anthropic/claude-sonnet-5.5"},"fast":{"provider":"openrouter","model":"deepseek/deepseek-v4.1-flash-1017"},"vision":{"provider":"openrouter","model":"deepseek/deepseek-v4.1-flash-1017"}}}""",
            response.GetProperty("settings").GetRawText());
        var providers = response.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal(["openrouter", "deepseek", "gemini", "openai", "anthropic"], providers.Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(
            """{"id":"openai","name":"OpenAI","description":"GPT models over the OpenAI API.","configured":false,"hasStoredKey":false,"maskedStoredKey":null,"hasEnvKey":false,"models":["gpt-6.2-sol","gpt-6-luna","gpt-6.1-terra","gpt-6.2-astra"],"suggestedModels":["gpt-6.2-sol","gpt-6-luna","gpt-6.1-terra","gpt-6.2-astra"]}""",
            providers[3].GetRawText());
        Assert.Equal(6, providers[0].GetProperty("suggestedModels").GetArrayLength());

        var usage = response.GetProperty("usage");
        Assert.Equal(["plan", "planLabel", "keySource", "canChooseModels", "evaluations", "assists", "verificationRequired", "resetsAt"], Names(usage));
        Assert.Equal("free", usage.GetProperty("plan").GetString());
        Assert.Equal("none", usage.GetProperty("keySource").GetString());
        Assert.Equal("""{"used":0,"limit":5}""", usage.GetProperty("evaluations").GetRawText());
        Assert.Equal("""{"used":0,"limit":60}""", usage.GetProperty("assists").GetRawText());
        var nextMonth = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        Assert.Equal(nextMonth, usage.GetProperty("resetsAt").GetDateTimeOffset());

        Assert.Equal(
            """{"enabled":false,"hasSubscription":false,"status":null,"currentPeriodEnd":null,"cancelAtPeriodEnd":false}""",
            response.GetProperty("billing").GetRawText());
    }

    [Fact]
    public async Task Saved_api_keys_are_encrypted_at_rest_and_only_ever_shown_masked()
    {
        var (browser, userId, _) = await SignUpAsync();
        var key = $"sk-test-{TestSecrets.NewSecret()}";

        using var saved = await browser.PutAsJsonAsync("/api/settings", new
        {
            currentBand = 6,
            targetBand = 8,
            examDate = "2027-01-15",
            nativeLanguage = "pt",
            l1CoachingEnabled = false,
            activeProvider = "openai",
            graderModel = "gpt-custom",
            fastModel = "anthropic/wrong-kind",
            apiKeysToUpdate = new { openai = $"  {key}  ", gemini = "" },
            completeOnboarding = true,
        });
        Assert.Equal("""{"ok":true}""", await saved.Content.ReadAsStringAsync());

        var stored = await Service<ISettingsRepository>().GetOrCreateAsync(userId);
        var encrypted = Assert.Single(stored.EncryptedApiKeys);
        Assert.Equal("openai", encrypted.Key);
        Assert.DoesNotContain(key, encrypted.Value);
        Assert.NotNull(stored.OnboardedAt);

        using var raw = await browser.GetAsync("/api/settings");
        var text = await raw.Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, text);
        Assert.DoesNotContain(encrypted.Value, text);
        var response = JsonDocument.Parse(text).RootElement;
        // A legacy save (one provider, three model names) sets every role to that provider.
        Assert.Equal(
            """{"currentBand":6,"targetBand":8,"examDate":"2027-01-15","nativeLanguage":"pt","l1CoachingEnabled":false,"models":{"grader":{"provider":"openai","model":"gpt-custom"},"fast":{"provider":"openai","model":"gpt-6-luna"},"vision":{"provider":"openai","model":"gpt-6.1-terra"}}}""",
            response.GetProperty("settings").GetRawText());
        var status = response.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "openai");
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.True(status.GetProperty("hasStoredKey").GetBoolean());
        Assert.Equal($"{key[..5]}••••••{key[^4..]}", status.GetProperty("maskedStoredKey").GetString());
        Assert.Equal("user", response.GetProperty("usage").GetProperty("keySource").GetString());

        // The export names the provider and nothing more.
        using var export = await browser.GetAsync("/api/account/export");
        var exported = await export.Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, exported);
        Assert.DoesNotContain(encrypted.Value, exported);
        Assert.Contains("\"savedApiKeyProviders\": [\n      \"openai\"\n    ]", exported);

        // A partial save keeps the rest, and a key is removed with null or "__CLEAR__".
        using var cleared = await browser.PutAsJsonAsync("/api/settings", new { activeProvider = "openai", apiKeysToUpdate = new { openai = "__CLEAR__" } });
        var after = await Service<ISettingsRepository>().GetOrCreateAsync(userId);
        Assert.Empty(after.EncryptedApiKeys);
        Assert.Equal(8, after.TargetBand);
        Assert.Equal("pt", after.NativeLanguage);
        Assert.Equal("2027-01-15", after.ExamDate);
        Assert.Equal(stored.OnboardedAt, after.OnboardedAt);
    }

    [Theory]
    [InlineData("""{ "currentBand": 12 }""")]
    [InlineData("""{ "targetBand": -1 }""")]
    [InlineData("""{ "nativeLanguage": "tlh" }""")]
    [InlineData("""{ "activeProvider": "someone-elses-server" }""")]
    [InlineData("""{ "apiKeysToUpdate": { "not-a-provider": "sk-test" } }""")]
    [InlineData("""{ "models": { "grader": { "provider": "someone-elses-server", "model": "m" } } }""")]
    [InlineData("""{ "models": { "fast": { "provider": "openai", "model": "anthropic/wrong-kind" } } }""")]
    [InlineData("""{ "models": { "vision": { "provider": "openrouter" } } }""")]
    public async Task Settings_that_make_no_sense_are_refused(string body)
    {
        var (browser, _, _) = await SignUpAsync();

        using var response = await browser.PutAsync("/api/settings", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Providers_are_added_edited_and_removed_and_roles_follow_them()
    {
        var (browser, userId, _) = await SignUpAsync();
        var (other, otherId, _) = await SignUpAsync();
        var key = $"sk-test-{TestSecrets.NewSecret()}";

        // Adding a provider needs a key; without a model list it starts with its suggested models.
        using var noKey = await browser.PutAsJsonAsync("/api/settings/providers/deepseek", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        using var added = await browser.PutAsJsonAsync("/api/settings/providers/deepseek", new { apiKey = key });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var addedText = await added.Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, addedText);
        var provider = JsonDocument.Parse(addedText).RootElement;
        Assert.True(provider.GetProperty("configured").GetBoolean());
        Assert.Equal(["deepseek-v4-pro", "deepseek-flash"], provider.GetProperty("models").EnumerateArray().Select(m => m.GetString()));

        // Each role can point at a different provider.
        using var roles = await browser.PutAsJsonAsync("/api/settings", new
        {
            models = new
            {
                grader = new { provider = "deepseek", model = "deepseek-v4-pro" },
                fast = new { provider = "openrouter", model = "anthropic/claude-haiku-5.5" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        var stored = await Service<ISettingsRepository>().GetOrCreateAsync(userId);
        Assert.Equal(("deepseek", "deepseek-v4-pro"), (stored.GraderProvider, stored.GraderModel));
        Assert.Equal(("openrouter", "anthropic/claude-haiku-5.5"), (stored.FastProvider, stored.FastModel));

        // Taking the examiner's model off the list moves the examiner to the first model left.
        using var edited = await browser.PutAsJsonAsync("/api/settings/providers/deepseek", new { models = new[] { "deepseek-flash", "deepseek-v4-lite", "deepseek-flash" } });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        stored = await Service<ISettingsRepository>().GetOrCreateAsync(userId);
        Assert.Equal(["deepseek-flash", "deepseek-v4-lite"], stored.ProviderModels["deepseek"]);
        Assert.Equal(("deepseek", "deepseek-flash"), (stored.GraderProvider, stored.GraderModel));
        Assert.Single(stored.EncryptedApiKeys);

        // Another user never sees it.
        var otherSettings = await GetJsonAsync(other, "/api/settings");
        Assert.All(otherSettings.GetProperty("providers").EnumerateArray(), p => Assert.False(p.GetProperty("hasStoredKey").GetBoolean()));
        Assert.Empty((await Service<ISettingsRepository>().GetOrCreateAsync(otherId)).ProviderModels);

        // Removing a provider moves its roles to one the user still has, onto a model that provider lists.
        using var openAi = await browser.PutAsJsonAsync("/api/settings/providers/openai", new { apiKey = key, models = new[] { "gpt-6-luna" } });
        Assert.Equal(HttpStatusCode.OK, openAi.StatusCode);
        using var removed = await browser.DeleteAsync("/api/settings/providers/deepseek");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        stored = await Service<ISettingsRepository>().GetOrCreateAsync(userId);
        Assert.Equal(["openai"], stored.EncryptedApiKeys.Keys);
        Assert.False(stored.ProviderModels.ContainsKey("deepseek"));
        // The examiner's default (gpt-6.2-sol) is not on the list, so it gets the first listed model.
        Assert.Equal(("openai", "gpt-6-luna"), (stored.GraderProvider, stored.GraderModel));
    }

    [Theory]
    [InlineData("deepseek", """{ "apiKey": "" }""", HttpStatusCode.BadRequest)]
    [InlineData("deepseek", """{ "apiKey": "sk", "models": [] }""", HttpStatusCode.BadRequest)]
    [InlineData("deepseek", """{ "apiKey": "sk", "models": [42] }""", HttpStatusCode.BadRequest)]
    [InlineData("deepseek", """{ "apiKey": "sk", "models": ["vendor/slashed"] }""", HttpStatusCode.BadRequest)]
    [InlineData("openrouter", """{ "apiKey": "sk", "models": ["no-slash"] }""", HttpStatusCode.BadRequest)]
    [InlineData("someone-elses-server", """{ "apiKey": "sk" }""", HttpStatusCode.NotFound)]
    public async Task Provider_settings_that_make_no_sense_are_refused(string id, string body, HttpStatusCode expected)
    {
        var (browser, _, _) = await SignUpAsync();

        using var response = await browser.PutAsync($"/api/settings/providers/{id}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(expected, response.StatusCode);
    }

    // ---- Backup and export ----

    [Fact]
    public async Task A_backup_restores_into_another_account_with_fresh_ids_and_its_images()
    {
        var (ana, anaId, _) = await SignUpAsync();
        var (ben, benId, _) = await SignUpAsync();
        var prompt = (await PostJsonAsync(ana, "/api/prompts", new { taskType = "task1", promptText = "Describe the chart.", imageDataUrl = Png })).GetProperty("prompt");
        var promptId = prompt.GetProperty("id").GetString()!;
        var curatedId = CuratedPromptSeed.LoadEmbedded().First(s => s.Prompt.TaskType == "task2").Prompt.Id;
        var sessionId = (await PostJsonAsync(ana, "/api/sessions", new { mode = "simulator_full_60m", task1PromptId = promptId, task2PromptId = curatedId })).GetProperty("session").GetProperty("id").GetString()!;
        var writing = Service<IWritingRepository>();
        await writing.SaveDraftAsync(anaId, TestData.Draft("d1", sessionId) with { PromptId = promptId });
        await writing.SaveEvaluationAsync(anaId, TestData.Evaluation("e1", sessionId, "d1"));
        await Service<IVaultRepository>().SaveAsync(anaId, TestData.VaultItem("from-draft") with { SourceDraftId = "d1" });
        await Service<IDrillRepository>().SaveAsync(anaId, TestData.Drill("drill-1"));

        using var export = await ana.GetAsync("/api/backup");
        var backupText = await export.Content.ReadAsStringAsync();
        var backup = JsonDocument.Parse(backupText).RootElement;
        Assert.Equal(["exportedAt", "app", "version", "data"], Names(backup));
        Assert.Equal(2, backup.GetProperty("version").GetInt32());
        Assert.Equal(["prompts", "sessions", "drafts", "evaluations", "vaultItems", "microDrills"], Names(backup.GetProperty("data")));
        // No owner in the file, no curated prompts, and the image is inlined.
        Assert.DoesNotContain(anaId, backupText);
        Assert.DoesNotContain("\"userId\"", backupText);
        var exportedPrompt = Assert.Single(backup.GetProperty("data").GetProperty("prompts").EnumerateArray());
        Assert.Equal(Png, exportedPrompt.GetProperty("imageDataUrl").GetString());
        Assert.Equal(5, backup.GetProperty("data").GetProperty("vaultItems").GetArrayLength());

        using var import = await ben.PostAsync("/api/backup", new StringContent(backupText, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        Assert.Equal("""{"ok":true,"importedCount":10}""", await import.Content.ReadAsStringAsync());

        var restored = (await GetJsonAsync(ben, "/api/sessions")).GetProperty("sessions")[0];
        Assert.NotEqual(sessionId, restored.GetProperty("id").GetString());
        Assert.Equal(benId, restored.GetProperty("userId").GetString());
        // The reference to the user's own prompt follows it to its new id; the curated one is kept.
        var restoredPrompt = restored.GetProperty("task1Prompt");
        Assert.NotEqual(promptId, restoredPrompt.GetProperty("id").GetString());
        Assert.Equal(benId, restoredPrompt.GetProperty("userId").GetString());
        Assert.Equal(curatedId, restored.GetProperty("task2Prompt").GetProperty("id").GetString());
        var draft = Assert.Single(restored.GetProperty("drafts").EnumerateArray());
        Assert.NotEqual("d1", draft.GetProperty("id").GetString());
        Assert.Equal(restoredPrompt.GetProperty("id").GetString(), draft.GetProperty("promptId").GetString());
        Assert.Equal(draft.GetProperty("id").GetString(), draft.GetProperty("evaluation").GetProperty("draftId").GetString());
        var benVault = await Service<IVaultRepository>().ListAsync(benId);
        Assert.Equal(9, benVault.Count);
        Assert.Equal(draft.GetProperty("id").GetString(), benVault.Single(v => v.SourceDraftId is not null).SourceDraftId);

        // The image is a new copy that belongs to the importer.
        var restoredImage = restoredPrompt.GetProperty("imageDataUrl").GetString()!;
        Assert.NotEqual(prompt.GetProperty("imageDataUrl").GetString(), restoredImage);
        using var benImage = await ben.GetAsync(restoredImage);
        Assert.Equal(HttpStatusCode.OK, benImage.StatusCode);
        using var anaCannot = await ana.GetAsync(restoredImage);
        Assert.Equal(HttpStatusCode.NotFound, anaCannot.StatusCode);

        // The source account is as it was.
        Assert.Single((await GetJsonAsync(ana, "/api/sessions")).GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task A_backup_file_cannot_write_into_another_account_or_the_curated_bank()
    {
        var (ana, anaId, _) = await SignUpAsync();
        var (ben, benId, _) = await SignUpAsync();
        var anaItem = (await PostJsonAsync(ana, "/api/vault", new { originalText = "ana's", upgradedText = "untouched" })).GetProperty("item").GetProperty("id").GetString()!;
        var curated = CuratedPromptSeed.LoadEmbedded()[0].Prompt;

        // A forged file: someone else's user id, an existing item's id, and a curated prompt's id.
        var forged = new
        {
            data = new
            {
                prompts = new[] { new { id = curated.Id, userId = anaId, taskType = "task2", category = "c", topic = "t", title = "Overwritten", promptText = "forged", source = "curated", createdAt = "2026-03-01T09:00:00.000Z" } },
                vaultItems = new[] { new { id = anaItem, userId = anaId, itemType = "error", criterion = "LR", originalText = "forged", upgradedText = "forged", explanation = "x", exampleSentence = "x", createdAt = "2026-03-01T09:00:00.000Z" } },
            },
        };
        await PostJsonAsync(ben, "/api/backup", forged);

        var anaVault = (await GetJsonAsync(ana, "/api/vault")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(5, anaVault.Count);
        Assert.Equal("ana's", anaVault.Single(i => i.GetProperty("id").GetString() == anaItem).GetProperty("originalText").GetString());
        Assert.Equal(curated.Title, (await Service<IPromptRepository>().GetVisibleAsync(anaId, curated.Id))!.Title);
        var benPrompts = (await GetJsonAsync(ben, "/api/prompts")).GetProperty("prompts").EnumerateArray().Where(p => p.GetProperty("title").GetString() == "Overwritten").ToList();
        var imported = Assert.Single(benPrompts);
        Assert.Equal(benId, imported.GetProperty("userId").GetString());
        Assert.Equal("custom", imported.GetProperty("source").GetString());
        Assert.NotEqual(curated.Id, imported.GetProperty("id").GetString());

        await PostJsonAsync(ben, "/api/backup", new { notData = true }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_account_export_holds_everything_but_credentials_and_is_limited_to_five_an_hour()
    {
        var (browser, userId, email) = await SignUpAsync();
        await Service<IAiUsageRepository>().RecordAsync(userId, TestData.Usage("usage-1", "rubric_score"));

        using var response = await browser.GetAsync("/api/account/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Matches(@"^attachment; filename=""escribot-my-data-\d{4}-\d\d-\d\d\.json""$", response.Content.Headers.ContentDisposition!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        var export = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["exportedAt", "app", "account", "settings", "subscription", "data"], Names(export));
        var account = export.GetProperty("account");
        Assert.Equal(["name", "email", "emailVerified", "createdAt", "signInMethods"], Names(account));
        Assert.Equal(email, account.GetProperty("email").GetString());
        Assert.Equal("credential", account.GetProperty("signInMethods")[0].GetProperty("provider").GetString());
        Assert.Equal(
            ["currentBand", "targetBand", "examDate", "nativeLanguage", "l1CoachingEnabled", "activeProvider", "graderProvider", "graderModel", "fastProvider", "fastModel", "visionProvider", "visionModel", "providerModels", "plan", "onboardedAt", "updatedAt", "savedApiKeyProviders"],
            Names(export.GetProperty("settings")));
        Assert.Equal(JsonValueKind.Null, export.GetProperty("subscription").ValueKind);
        Assert.Equal(["prompts", "sessions", "drafts", "evaluations", "vaultItems", "microDrills", "aiUsage"], Names(export.GetProperty("data")));
        Assert.Equal("usage-1", export.GetProperty("data").GetProperty("aiUsage")[0].GetProperty("id").GetString());

        var stored = await Service<IUserAccountRepository>().GetAsync(userId);
        Assert.DoesNotContain(stored!.PasswordHash!, text);
        Assert.DoesNotContain(stored.SecurityStamp!, text);
        Assert.DoesNotContain("passwordHash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"userId\"", text);
        // Meant to be read by a person: indented.
        Assert.Contains("\n  \"account\": {", text);

        for (var i = 0; i < 4; i++)
        {
            (await browser.GetAsync("/api/account/export")).Dispose();
        }

        using var limited = await browser.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("""{"error":"Too many exports. Please try again later."}""", await limited.Content.ReadAsStringAsync());
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    // ---- Error reports ----

    [Fact]
    public async Task A_browser_error_report_is_stored_without_credentials_also_from_a_signed_out_page()
    {
        var marker = Guid.NewGuid().ToString("N");
        var token = $"sk-{TestSecrets.NewSecret()}";
        using var signedOut = fixture.Api.CreateBrowser();

        using var response = await signedOut.PostAsJsonAsync("/api/errors", new
        {
            message = $"Synthetic failure {marker} with Bearer {token}",
            stack = $"Error: at render ({marker})",
            digest = "d-123",
            path = $"/write/{marker}?token={token}",
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var recorded = (await Service<IErrorEventRepository>().ListRecentAsync(200)).Single(e => e.Message.Contains(marker));
        Assert.Equal("client", recorded.Source);
        Assert.Equal($"/write/{marker}", recorded.Path);
        Assert.Equal("d-123", recorded.Digest);
        Assert.Null(recorded.UserId);
        Assert.DoesNotContain(token, recorded.Message);

        var (browser, userId, _) = await SignUpAsync();
        var own = Guid.NewGuid().ToString("N");
        (await browser.PostAsJsonAsync("/api/errors", new { message = $"Synthetic {own}" })).Dispose();
        Assert.Equal(userId, (await Service<IErrorEventRepository>().ListRecentAsync(200)).Single(e => e.Message.Contains(own)).UserId);

        using var invalid = await signedOut.PostAsJsonAsync("/api/errors", new { message = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var notJson = await signedOut.PostAsync("/api/errors", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
    }

    [Fact]
    public async Task Error_reports_beyond_ten_a_minute_are_dropped_quietly()
    {
        var (browser, userId, _) = await SignUpAsync();
        var marker = Guid.NewGuid().ToString("N");

        for (var i = 0; i < 12; i++)
        {
            using var response = await browser.PostAsJsonAsync("/api/errors", new { message = $"Synthetic {marker} #{i}" });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var recorded = (await Service<IErrorEventRepository>().ListRecentAsync(500)).Count(e => e.UserId == userId);
        Assert.InRange(recorded, 1, 10);
    }

    // ---- Isolation, as in scripts/test-isolation.mjs ----

    [Fact]
    public async Task One_account_cannot_read_or_change_anothers_data_and_deleting_one_leaves_the_other_intact()
    {
        var run = Guid.NewGuid().ToString("N")[..10];
        var (a, aId, _) = await SignUpAsync();
        var (b, _, _) = await SignUpAsync();

        // User A creates data.
        var prompt = (await PostJsonAsync(a, "/api/prompts", new { taskType = "task1", promptText = $"Isolation prompt {run}: describe the chart.", title = $"Isolation {run}", imageDataUrl = Png })).GetProperty("prompt");
        var promptId = prompt.GetProperty("id").GetString()!;
        var imagePath = prompt.GetProperty("imageDataUrl").GetString()!;
        var imageId = imagePath.Split('/').Last();
        var sessionId = (await PostJsonAsync(a, "/api/sessions", new { mode = "coach", task1PromptId = promptId })).GetProperty("session").GetProperty("id").GetString()!;
        var draftId = $"draft-{run}";
        await Service<IWritingRepository>().SaveDraftAsync(aId, TestData.Draft(draftId, sessionId) with { PromptId = promptId, EssayText = $"essay-{run}" });
        await Service<IWritingRepository>().SaveEvaluationAsync(aId, TestData.Evaluation($"eval-{run}", sessionId, draftId));
        var vaultItemId = (await PostJsonAsync(a, "/api/vault", new { originalText = $"orig-{run}", upgradedText = $"upgraded-{run}" })).GetProperty("item").GetProperty("id").GetString()!;
        string[] secrets = [promptId, sessionId, draftId, vaultItemId, imageId, $"orig-{run}", $"essay-{run}", aId];

        // User B cannot read A's data.
        foreach (var path in new[] { "/api/sessions", "/api/prompts", "/api/vault", "/api/drills", "/api/settings", "/api/backup", "/api/account/export" })
        {
            using var response = await b.GetAsync(path);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(secrets, text.Contains);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/sessions/{sessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(imagePath)).StatusCode);

        // User B cannot change A's data.
        Assert.Equal(HttpStatusCode.NotFound, (await b.PatchAsJsonAsync("/api/vault", new { id = vaultItemId, masteryLevel = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.DeleteAsync($"/api/vault?id={Uri.EscapeDataString(vaultItemId)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync("/api/sessions", new { mode = "coach", task1PromptId = promptId })).StatusCode);

        var aItem = (await GetJsonAsync(a, "/api/vault")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == vaultItemId);
        Assert.Equal(0, aItem.GetProperty("masteryLevel").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync(imagePath)).StatusCode);

        // Signed-out requests are refused.
        using var anonymous = fixture.Api.CreateBrowser();
        foreach (var (method, path) in new[]
        {
            ("GET", "/api/sessions"), ("POST", "/api/sessions"), ("GET", $"/api/sessions/{sessionId}"),
            ("GET", "/api/prompts"), ("POST", "/api/prompts"), ("GET", "/api/vault"), ("POST", "/api/vault"), ("PATCH", "/api/vault"),
            ("DELETE", $"/api/vault?id={vaultItemId}"), ("GET", "/api/drills"), ("GET", "/api/settings"), ("PUT", "/api/settings"),
            ("PUT", "/api/settings/providers/openai"), ("DELETE", "/api/settings/providers/openai"),
            ("GET", "/api/backup"), ("POST", "/api/backup"), ("GET", "/api/account/export"), ("GET", imagePath),
        })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET")
            {
                request.Content = JsonContent.Create(new { });
            }

            using var response = await anonymous.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} answered {(int)response.StatusCode}");
        }

        // Deleting user B leaves user A intact.
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/auth/delete-user", new { password = _password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/settings")).StatusCode);
        var aSessions = (await GetJsonAsync(a, "/api/sessions")).GetProperty("sessions").EnumerateArray().ToList();
        Assert.Contains(aSessions, s => s.GetProperty("id").GetString() == sessionId);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync(imagePath)).StatusCode);
    }
}
