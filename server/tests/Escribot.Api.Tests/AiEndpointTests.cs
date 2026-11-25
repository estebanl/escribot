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
    private static ApiFactory CreateAiFactory(FakeChatClientFactory? chatFactory = null, Dictionary<string, string?>? extraSettings = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = chatFactory is null ? null : "or-test-key",
        };
        if (extraSettings is not null)
        {
            foreach (var (k, v) in extraSettings)
            {
                settings[k] = v;
            }
        }

        return new ApiFactory
        {
            Settings = settings,
            ConfigureServices = services =>
            {
                if (chatFactory is not null)
                {
                    services.RemoveAll<IAiChatClientFactory>();
                    services.AddSingleton<IAiChatClientFactory>(chatFactory);
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
            (HttpMethod.Post, "/api/ai/transform-svg"),
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
        var fake = new FakeChatClientFactory((_, userPrompt) =>
        {
            if (userPrompt.Contains("\"readiness\"", StringComparison.Ordinal))
            {
                return new FakeChatResult("""{"readiness":"strong","estimatedTaskBandCeiling":7.5,"verdictSummary":"Ready.","overviewOrThesisCheck":"Clear.","bodyGroupingCheck":"Good.","actionableTweaks":["Keep going"],"suggestedAcademicCollocations":[]}""", "m");
            }

            if (userPrompt.Contains("\"headline\"", StringComparison.Ordinal))
            {
                return new FakeChatResult("""{"tool":"collocations","headline":"Upgraded","analysis":"Better.","suggestions":[{"label":"Option 1","rewrittenOrPhrase":"rose markedly","rationale":"Formal"}]}""", "m");
            }

            if (userPrompt.Contains("\"promptText\"", StringComparison.Ordinal))
            {
                return new FakeChatResult("""{"title":"AI Prompt","topic":"Tech","promptText":"Discuss technology. Write at least 250 words.","chartSpec":null}""", "m");
            }

            if (userPrompt.Contains("\"drillType\"", StringComparison.Ordinal))
            {
                return new FakeChatResult("""{"drillType":"cohesion","targetCriterion":"CC","title":"Cohesion Drill","instructions":"Fix linkers.","timeLimitSeconds":300,"sourceContext":"Cities","flawedText":"Firstly, cities grow.","hints":["Remove Firstly"],"referenceModelAnswer":"Urban expansion continues."}""", "m");
            }

            return new FakeChatResult("""{"scoreOutOf10":8,"estimatedBandImpact":"Band 8.0","feedback":"Well done.","improvedVersion":"Urban expansion continues.","keyTakeaway":"Avoid list markers."}""", "m");
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

    [Fact]
    public async Task Oversized_ai_inputs_return_400()
    {
        await using var factory = CreateAiFactory();
        var (browser, _) = await SignUpAsync(factory, "limits");

        using var promptRes = await browser.PostAsJsonAsync("/api/prompts", new
        {
            taskType = "task2",
            promptText = "Discuss urban growth.",
            title = "Urban Growth",
        });
        Assert.Equal(HttpStatusCode.OK, promptRes.StatusCode);
        var promptId = (await AuthEndpointTests.Json(promptRes)).GetProperty("prompt").GetProperty("id").GetString()!;

        using var sessionRes = await browser.PostAsJsonAsync("/api/sessions", new
        {
            mode = "coach",
            task2PromptId = promptId,
        });
        Assert.Equal(HttpStatusCode.OK, sessionRes.StatusCode);
        var sessionId = (await AuthEndpointTests.Json(sessionRes)).GetProperty("session").GetProperty("id").GetString()!;

        var validEssay = "Urbanization has reshaped modern societies in numerous ways.";

        // Evaluate: > 2 submissions
        using var tooManySubs = await browser.PostAsJsonAsync("/api/ai/evaluate", new
        {
            sessionId,
            submissions = new[]
            {
                new { promptId, taskType = "task2", essayText = validEssay },
                new { promptId, taskType = "task2", essayText = validEssay },
                new { promptId, taskType = "task2", essayText = validEssay },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooManySubs.StatusCode);

        // Evaluate: essayText > 20,000 chars
        using var longEssay = await browser.PostAsJsonAsync("/api/ai/evaluate", new
        {
            sessionId,
            submissions = new[]
            {
                new { promptId, taskType = "task2", essayText = new string('a', 20_001) },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, longEssay.StatusCode);

        // Evaluate: scratchNotes > 10,000 chars
        using var longNotes = await browser.PostAsJsonAsync("/api/ai/evaluate", new
        {
            sessionId,
            submissions = new[]
            {
                new { promptId, taskType = "task2", essayText = validEssay, scratchNotes = new string('n', 10_001) },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, longNotes.StatusCode);

        // Evaluate: outline field > 5,000 chars
        using var longEvalOutline = await browser.PostAsJsonAsync("/api/ai/evaluate", new
        {
            sessionId,
            submissions = new[]
            {
                new
                {
                    promptId,
                    taskType = "task2",
                    essayText = validEssay,
                    outline = new { thesisOrOverview = new string('o', 5_001), body1Plan = "p1", body2Plan = "p2" },
                },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, longEvalOutline.StatusCode);

        // Outline check: promptText > 20,000 chars
        using var longOutlinePrompt = await browser.PostAsJsonAsync("/api/ai/outline-check", new
        {
            taskType = "task2",
            promptText = new string('p', 20_001),
            outline = new { thesisOrOverview = "t", body1Plan = "b1", body2Plan = "b2" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, longOutlinePrompt.StatusCode);

        // Outline check: outline field > 5,000 chars
        using var longOutlineField = await browser.PostAsJsonAsync("/api/ai/outline-check", new
        {
            taskType = "task2",
            promptText = "Discuss urban growth.",
            outline = new { thesisOrOverview = "t", body1Plan = new string('b', 5_001), body2Plan = "b2" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, longOutlineField.StatusCode);

        // Toolkit: selectedText > 10,000, promptText > 20,000, fullEssayContext > 20,000
        using var longSelected = await browser.PostAsJsonAsync("/api/ai/toolkit", new
        {
            tool = "collocations",
            taskType = "task2",
            promptText = "Discuss urban growth.",
            selectedText = new string('s', 10_001),
        });
        Assert.Equal(HttpStatusCode.BadRequest, longSelected.StatusCode);

        using var longToolkitPrompt = await browser.PostAsJsonAsync("/api/ai/toolkit", new
        {
            tool = "collocations",
            taskType = "task2",
            promptText = new string('p', 20_001),
            selectedText = "urban growth",
        });
        Assert.Equal(HttpStatusCode.BadRequest, longToolkitPrompt.StatusCode);

        using var longContext = await browser.PostAsJsonAsync("/api/ai/toolkit", new
        {
            tool = "collocations",
            taskType = "task2",
            promptText = "Discuss urban growth.",
            selectedText = "urban growth",
            fullEssayContext = new string('c', 20_001),
        });
        Assert.Equal(HttpStatusCode.BadRequest, longContext.StatusCode);

        // Generate prompt: topic > 300, category > 100
        using var longTopic = await browser.PostAsJsonAsync("/api/ai/generate-prompt", new
        {
            taskType = "task2",
            category = "opinion",
            topic = new string('t', 301),
        });
        Assert.Equal(HttpStatusCode.BadRequest, longTopic.StatusCode);

        using var longCategory = await browser.PostAsJsonAsync("/api/ai/generate-prompt", new
        {
            taskType = "task2",
            category = new string('c', 101),
            topic = "Tech",
        });
        Assert.Equal(HttpStatusCode.BadRequest, longCategory.StatusCode);

        // Drills: drillType > 100, userResponse > 20,000
        using var longDrillType = await browser.PostAsJsonAsync("/api/drills", new
        {
            action = "generate",
            drillType = new string('d', 101),
        });
        Assert.Equal(HttpStatusCode.BadRequest, longDrillType.StatusCode);

        using var longDrillResponse = await browser.PostAsJsonAsync("/api/drills", new
        {
            action = "score",
            userResponse = new string('r', 20_001),
        });
        Assert.Equal(HttpStatusCode.BadRequest, longDrillResponse.StatusCode);
    }

    [Fact]
    public async Task Concurrent_requests_cannot_bypass_free_monthly_quota_or_daily_cap()
    {
        // 1. Concurrent evaluations cannot bypass the Free monthly evaluation quota (5).
        var evalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightEvalCalls = 0;
        var evalProvider = new FakeChatClientFactory(async (_, userPrompt, _) =>
        {
            if (userPrompt.Contains("taTrBand", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref inFlightEvalCalls) >= 5)
                {
                    evalGate.TrySetResult();
                }

                await Task.WhenAny(evalGate.Task, Task.Delay(150));
                return new FakeChatResult(
                    """{"taTrBand":7.0,"ccBand":7.0,"lrBand":7.0,"graBand":7.0,"examinerSummary":"Good.","criteria":{"TA_TR":{"band":7.0},"CC":{"band":7.0},"LR":{"band":7.0},"GRA":{"band":7.0}},"topPriorities":[]}""",
                    "m");
            }

            return new FakeChatResult(
                """{"inlineAnnotations":[],"targetBandRewrite":"Improved.","rewriteKeyChanges":[],"vaultItems":[]}""",
                "m");
        });

        await using (var factory = CreateAiFactory(evalProvider))
        {
            var (browser, userId) = await SignUpAsync(factory, "race-eval");
            using var promptRes = await browser.PostAsJsonAsync("/api/prompts", new { taskType = "task2", promptText = "Discuss tech.", title = "Tech" });
            var promptId = (await AuthEndpointTests.Json(promptRes)).GetProperty("prompt").GetProperty("id").GetString()!;
            using var sessionRes = await browser.PostAsJsonAsync("/api/sessions", new { mode = "coach", task2PromptId = promptId });
            var sessionId = (await AuthEndpointTests.Json(sessionRes)).GetProperty("session").GetProperty("id").GetString()!;

            var tasks = Enumerable.Range(0, 10).Select(_ => browser.PostAsJsonAsync("/api/ai/evaluate", new
            {
                sessionId,
                submissions = new[]
                {
                    new { promptId, taskType = "task2", essayText = "Technology has transformed education across the globe in recent decades." },
                },
            }));

            var responses = await Task.WhenAll(tasks);
            try
            {
                Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
                Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
                var monthStart = new DateTimeOffset(factory.Clock.GetUtcNow().Year, factory.Clock.GetUtcNow().Month, 1, 0, 0, 0, TimeSpan.Zero);
                var recorded = await ((IAiUsageRepository)factory.Data).CountAsync(userId, monthStart, Api.UsageService.EvaluationFeatures, "platform");
                Assert.Equal(5, recorded);
            }
            finally
            {
                foreach (var r in responses)
                {
                    r.Dispose();
                }
            }
        }

        // 2. Concurrent assist requests cannot bypass FREE_PLAN_DAILY_AI_CAP (3).
        var dailyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightDailyCalls = 0;
        var dailyProvider = new FakeChatClientFactory(async (_, _, _) =>
        {
            if (Interlocked.Increment(ref inFlightDailyCalls) >= 3)
            {
                dailyGate.TrySetResult();
            }

            await Task.WhenAny(dailyGate.Task, Task.Delay(150));
            return new FakeChatResult(
                """{"tool":"collocations","headline":"Upgraded","analysis":"Better.","suggestions":[{"label":"Option 1","rewrittenOrPhrase":"rose markedly","rationale":"Formal"}]}""",
                "m");
        });

        await using (var factory = CreateAiFactory(dailyProvider, new Dictionary<string, string?> { ["FREE_PLAN_DAILY_AI_CAP"] = "3" }))
        {
            var (browser, _) = await SignUpAsync(factory, "race-daily");

            var tasks = Enumerable.Range(0, 8).Select(_ => browser.PostAsJsonAsync("/api/ai/toolkit", new
            {
                tool = "collocations",
                taskType = "task1",
                promptText = "Describe the chart.",
                selectedText = "went up a lot",
            }));

            var responses = await Task.WhenAll(tasks);
            try
            {
                Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
                Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
                Assert.Equal(3, await ((IFreeAiDailyCounter)factory.Data).GetTodayAsync());
            }
            finally
            {
                foreach (var r in responses)
                {
                    r.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task Backup_import_is_rate_limited_to_five_an_hour_and_rejects_oversized_fields()
    {
        await using var factory = CreateAiFactory();
        var (browser, _) = await SignUpAsync(factory, "backup-limit");

        var validBackup = new { data = new { sessions = Array.Empty<object>() } };
        for (var i = 0; i < 5; i++)
        {
            using var ok = await browser.PostAsJsonAsync("/api/backup", validBackup);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await browser.PostAsJsonAsync("/api/backup", validBackup);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        // Next hour window resets the rate limit; oversized fields return 400.
        factory.Clock.Now = factory.Clock.Now.AddHours(1);
        using var oversized = await browser.PostAsJsonAsync("/api/backup", new
        {
            data = new
            {
                prompts = new[]
                {
                    new
                    {
                        id = "p1",
                        taskType = "task2",
                        category = "opinion",
                        topic = "Tech",
                        title = new string('t', 301),
                        promptText = "Discuss.",
                        createdAt = "2026-03-01T09:00:00.000Z",
                    },
                },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task Evaluate_does_not_leak_raw_internal_exception_messages()
    {
        var internalDetail = $"secret-upstream-detail-{TestSecrets.NewSecret()}";
        var failingProvider = new FakeChatClientFactory((_, _) =>
            throw new InvalidOperationException($"Upstream error: {internalDetail}"));

        await using var factory = CreateAiFactory(failingProvider);
        var (browser, _) = await SignUpAsync(factory, "err");

        using var promptRes = await browser.PostAsJsonAsync("/api/prompts", new
        {
            taskType = "task2",
            promptText = "Discuss remote work.",
            title = "Remote Work",
        });
        Assert.Equal(HttpStatusCode.OK, promptRes.StatusCode);
        var promptId = (await AuthEndpointTests.Json(promptRes)).GetProperty("prompt").GetProperty("id").GetString()!;

        using var sessionRes = await browser.PostAsJsonAsync("/api/sessions", new
        {
            mode = "coach",
            task2PromptId = promptId,
        });
        Assert.Equal(HttpStatusCode.OK, sessionRes.StatusCode);
        var sessionId = (await AuthEndpointTests.Json(sessionRes)).GetProperty("session").GetProperty("id").GetString()!;

        using var evalRes = await browser.PostAsJsonAsync("/api/ai/evaluate", new
        {
            sessionId,
            submissions = new[]
            {
                new
                {
                    promptId,
                    taskType = "task2",
                    essayText = "Remote work has reshaped modern employment across many industries worldwide.",
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, evalRes.StatusCode);

        var body = await evalRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain(internalDetail, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Upstream error", body, StringComparison.Ordinal);

        var lines = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToList();

        var errorEvent = Assert.Single(lines, e => e.GetProperty("type").GetString() == "error");
        Assert.False(errorEvent.GetProperty("isTimeout").GetBoolean());
        Assert.Equal(
            "Grading failed. Your essay is safely preserved in the editor. Please try again.",
            errorEvent.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Evaluate_streams_sse_with_semantic_progress_milestones_when_accept_is_event_stream()
    {
        var fake = new FakeChatClientFactory((_, userPrompt) =>
        {
            if (userPrompt.Contains("taTrBand", StringComparison.Ordinal))
            {
                return new FakeChatResult(
                    Content: """{"taTrBand":7.0,"ccBand":7.0,"lrBand":7.5,"graBand":7.0,"examinerSummary":"Strong.","criteria":{"TA_TR":{"band":7.0},"CC":{"band":7.0},"LR":{"band":7.5},"GRA":{"band":7.0}},"topPriorities":["Refine conclusion"]}""",
                    ModelUsed: "anthropic/claude-sonnet-5.5",
                    StreamChunks:
                    [
                        """{"taTrBand":7.0,"ccBand":7.0,"lrBand":7.5,"graBand":7.0,"examinerSummary":"Strong.",""",
                        """ "criteria":{"TA_TR":{"band":7.0},"CC":{"band":7.0},"LR":{"band":7.5},"GRA":{"band":7.0}},""",
                        """ "topPriorities":["Refine conclusion"]}""",
                    ]);
            }

            return new FakeChatResult(
                Content: """{"inlineAnnotations":[],"targetBandRewrite":"Polished essay.","rewriteKeyChanges":["Sharper thesis"],"vaultItems":[]}""",
                ModelUsed: "deepseek/deepseek-v4.1-flash-1017",
                StreamChunks:
                [
                    """{"inlineAnnotations":[],""",
                    """ "targetBandRewrite":"Polished essay.","rewriteKeyChanges":["Sharper thesis"],""",
                    """ "vaultItems":[]}""",
                ]);
        });

        await using var factory = CreateAiFactory(fake);
        var (browser, _) = await SignUpAsync(factory, "sse");

        using var promptRes = await browser.PostAsJsonAsync("/api/prompts", new
        {
            taskType = "task2",
            promptText = "Discuss urban transit.",
            title = "Urban Transit",
        });
        var promptId = (await AuthEndpointTests.Json(promptRes)).GetProperty("prompt").GetProperty("id").GetString()!;
        using var sessionRes = await browser.PostAsJsonAsync("/api/sessions", new { mode = "coach", task2PromptId = promptId });
        var sessionId = (await AuthEndpointTests.Json(sessionRes)).GetProperty("session").GetProperty("id").GetString()!;

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/evaluate")
        {
            Content = JsonContent.Create(new
            {
                sessionId,
                submissions = new[]
                {
                    new { promptId, taskType = "task2", essayText = "Public transit reduces urban congestion and lowers carbon emissions across major cities." },
                },
            }),
        };
        req.Headers.Accept.ParseAdd("text/event-stream");

        using var evalRes = await browser.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, evalRes.StatusCode);
        Assert.Contains("text/event-stream", evalRes.Content.Headers.ContentType?.ToString());
        Assert.Equal("no", evalRes.Headers.GetValues("X-Accel-Buffering").Single());

        var sseBody = await evalRes.Content.ReadAsStringAsync();
        Assert.Contains("event: progress\n", sseBody);
        Assert.Contains("event: result\n", sseBody);

        var stages = sseBody
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l[6..]).RootElement)
            .Where(e => e.GetProperty("type").GetString() == "progress")
            .Select(e => e.GetProperty("stage").GetString())
            .ToList();

        Assert.Contains("Scoring 4 IELTS criteria...", stages);
        Assert.Contains("Drafting Band 8.0+ rewrite...", stages);
    }
}
