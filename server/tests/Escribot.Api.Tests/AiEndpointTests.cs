using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Escribot.Api.Ai;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Escribot.Api.Tests;

public class AiEndpointTests
{
    private sealed class FakeProvider(string providerId, Func<LlmProviderRequest, LlmResultWithMeta> handler) : IAiProviderClient
    {
        public string ProviderId { get; } = providerId;

        public Task<LlmResultWithMeta> CompleteAsync(LlmProviderRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private static ApiFactory CreateAiFactory(FakeProvider? provider = null)
    {
        return new ApiFactory
        {
            Settings = new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = provider is null ? null : "or-test-key",
            },
            ConfigureServices = services =>
            {
                if (provider is not null)
                {
                    services.RemoveAll<IAiProviderClient>();
                    services.AddTransient<IAiProviderClient>(_ => provider);
                }
            },
        };
    }

    private static async Task<(HttpClient Browser, string UserId)> SignUpAsync(ApiFactory factory, string label)
    {
        var browser = factory.CreateBrowser();
        var email = $"{label}-{Guid.NewGuid():N}@example.test";
        using var res = await AuthEndpointTests.SignUpAsync(browser, email, $"pw-{TestSecrets.NewSecret()}", $"User {label}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var userId = (await AuthEndpointTests.Json(res)).GetProperty("user").GetProperty("id").GetString()!;
        return (browser, userId);
    }

    [Fact]
    public async Task Signed_out_requests_to_ai_routes_return_401()
    {
        await using var factory = CreateAiFactory();
        var anon = factory.CreateBrowser();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, "/api/ai/evaluate"),
            (HttpMethod.Post, "/api/ai/outline-check"),
            (HttpMethod.Post, "/api/ai/toolkit"),
            (HttpMethod.Post, "/api/ai/generate-prompt"),
            (HttpMethod.Get, "/api/ai/models"),
            (HttpMethod.Post, "/api/drills"),
        })
        {
            using var req = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post)
            {
                req.Content = JsonContent.Create(new { });
            }

            using var res = await anon.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task Evaluate_streams_ndjson_persists_draft_evaluation_vault_and_enforces_isolation()
    {
        await using var factory = CreateAiFactory();
        var (browserA, _) = await SignUpAsync(factory, "a");
        var (browserB, _) = await SignUpAsync(factory, "b");

        // User A creates a prompt and session
        using var promptRes = await browserA.PostAsJsonAsync("/api/prompts", new
        {
            taskType = "task1",
            promptText = "Describe the chart showing energy trends.",
            title = "Energy Trends",
        });
        Assert.Equal(HttpStatusCode.OK, promptRes.StatusCode);
        var promptId = (await AuthEndpointTests.Json(promptRes)).GetProperty("prompt").GetProperty("id").GetString()!;

        using var sessionRes = await browserA.PostAsJsonAsync("/api/sessions", new
        {
            mode = "coach",
            task1PromptId = promptId,
        });
        Assert.Equal(HttpStatusCode.OK, sessionRes.StatusCode);
        var sessionId = (await AuthEndpointTests.Json(sessionRes)).GetProperty("session").GetProperty("id").GetString()!;

        // User A evaluates (zero-key local diagnostic preview)
        using var evalRes = await browserA.PostAsJsonAsync("/api/ai/evaluate", new
        {
            sessionId,
            submissions = new[]
            {
                new
                {
                    promptId,
                    taskType = "task1",
                    essayText = "Overall, renewable energy rose steadily across the period, whereas coal declined sharply.",
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, evalRes.StatusCode);
        Assert.Contains("application/x-ndjson", evalRes.Content.Headers.ContentType?.ToString());

        var lines = (await evalRes.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToList();

        Assert.Contains(lines, e => e.GetProperty("type").GetString() == "progress");
        var resultEvent = Assert.Single(lines, e => e.GetProperty("type").GetString() == "result");
        Assert.Equal(sessionId, resultEvent.GetProperty("sessionId").GetString());
        var firstResult = resultEvent.GetProperty("results")[0];
        Assert.StartsWith("draft-", firstResult.GetProperty("draftId").GetString());
        Assert.StartsWith("eval-", firstResult.GetProperty("evaluationId").GetString());

        // Session is now completed with weightedBand
        using var getSessionRes = await browserA.GetAsync($"/api/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, getSessionRes.StatusCode);
        var sessionJson = (await AuthEndpointTests.Json(getSessionRes)).GetProperty("session");
        Assert.Equal("completed", sessionJson.GetProperty("status").GetString());
        Assert.True(sessionJson.GetProperty("weightedBand").GetDouble() > 0);

        // User B cannot evaluate User A's session
        using var attackRes = await browserB.PostAsJsonAsync("/api/ai/evaluate", new
        {
            sessionId,
            submissions = new[]
            {
                new
                {
                    promptId,
                    taskType = "task1",
                    essayText = "This is a synthetic essay attempt by user B on user A's session.",
                },
            },
        });
        Assert.Equal(HttpStatusCode.NotFound, attackRes.StatusCode);
    }

    [Fact]
    public async Task Outline_toolkit_generate_prompt_and_drills_work_with_fake_provider()
    {
        var fake = new FakeProvider("openrouter", req =>
        {
            if (req.UserPrompt.Contains("\"readiness\"", StringComparison.Ordinal))
            {
                return new LlmResultWithMeta("""{"readiness":"strong","estimatedTaskBandCeiling":7.5,"verdictSummary":"Ready.","overviewOrThesisCheck":"Clear.","bodyGroupingCheck":"Good.","actionableTweaks":["Keep going"],"suggestedAcademicCollocations":[]}""", "m");
            }

            if (req.UserPrompt.Contains("\"headline\"", StringComparison.Ordinal))
            {
                return new LlmResultWithMeta("""{"tool":"collocations","headline":"Upgraded","analysis":"Better.","suggestions":[{"label":"Option 1","rewrittenOrPhrase":"rose markedly","rationale":"Formal"}]}""", "m");
            }

            if (req.UserPrompt.Contains("\"promptText\"", StringComparison.Ordinal))
            {
                return new LlmResultWithMeta("""{"title":"AI Prompt","topic":"Tech","promptText":"Discuss technology. Write at least 250 words.","chartSpec":null}""", "m");
            }

            if (req.UserPrompt.Contains("\"drillType\"", StringComparison.Ordinal))
            {
                return new LlmResultWithMeta("""{"drillType":"cohesion","targetCriterion":"CC","title":"Cohesion Drill","instructions":"Fix linkers.","timeLimitSeconds":300,"sourceContext":"Cities","flawedText":"Firstly, cities grow.","hints":["Remove Firstly"],"referenceModelAnswer":"Urban expansion continues."}""", "m");
            }

            return new LlmResultWithMeta("""{"scoreOutOf10":8,"estimatedBandImpact":"Band 8.0","feedback":"Well done.","improvedVersion":"Urban expansion continues.","keyTakeaway":"Avoid list markers."}""", "m");
        });

        await using var factory = CreateAiFactory(fake);
        var (browser, userId) = await SignUpAsync(factory, "ai");

        // Outline check
        using var outlineRes = await browser.PostAsJsonAsync("/api/ai/outline-check", new
        {
            taskType = "task2",
            promptText = "Discuss urban growth.",
            outline = new { thesisOrOverview = "Cities grow.", body1Plan = "Jobs.", body2Plan = "Housing." },
        });
        Assert.Equal(HttpStatusCode.OK, outlineRes.StatusCode);
        Assert.Equal("strong", (await AuthEndpointTests.Json(outlineRes)).GetProperty("feedback").GetProperty("readiness").GetString());

        // Toolkit
        using var toolkitRes = await browser.PostAsJsonAsync("/api/ai/toolkit", new
        {
            tool = "collocations",
            taskType = "task1",
            promptText = "Describe the chart.",
            selectedText = "went up a lot",
        });
        Assert.Equal(HttpStatusCode.OK, toolkitRes.StatusCode);
        Assert.Equal("Upgraded", (await AuthEndpointTests.Json(toolkitRes)).GetProperty("result").GetProperty("headline").GetString());

        // Generate prompt
        using var genPromptRes = await browser.PostAsJsonAsync("/api/ai/generate-prompt", new
        {
            taskType = "task2",
            category = "opinion",
            topic = "Tech",
        });
        Assert.Equal(HttpStatusCode.OK, genPromptRes.StatusCode);
        Assert.Equal("ai_generated", (await AuthEndpointTests.Json(genPromptRes)).GetProperty("prompt").GetProperty("source").GetString());

        // Drills generate + score
        using var genDrillRes = await browser.PostAsJsonAsync("/api/drills", new
        {
            action = "generate",
            drillType = "cohesion",
            level = "advanced",
        });
        Assert.Equal(HttpStatusCode.OK, genDrillRes.StatusCode);
        var genDrillJson = await AuthEndpointTests.Json(genDrillRes);
        var drillId = genDrillJson.GetProperty("drillId").GetString()!;

        using var scoreDrillRes = await browser.PostAsJsonAsync("/api/drills", new
        {
            action = "score",
            drillId,
            challenge = genDrillJson.GetProperty("challenge"),
            userResponse = "Urban expansion continues across major regions.",
        });
        Assert.Equal(HttpStatusCode.OK, scoreDrillRes.StatusCode);
        Assert.Equal(8, (await AuthEndpointTests.Json(scoreDrillRes)).GetProperty("score").GetProperty("scoreOutOf10").GetInt32());

        // Models endpoint (returns fallback list when offline)
        using var modelsRes = await browser.GetAsync("/api/ai/models?type=vision");
        Assert.Equal(HttpStatusCode.OK, modelsRes.StatusCode);
        Assert.True((await AuthEndpointTests.Json(modelsRes)).GetProperty("models").GetArrayLength() > 0);
    }
}
