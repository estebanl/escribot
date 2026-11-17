using System.Text.Json.Nodes;
using FluentEscritor.Api.Ai;
using FluentEscritor.Api.Api;
using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Common;
using FluentEscritor.Api.Configuration;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FluentEscritor.Api.Tests;

public class AiUnitTests
{
    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "FluentEscritor.Api";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeProviderClient(
        string providerId,
        Func<LlmProviderRequest, LlmResultWithMeta> handler) : IAiProviderClient
    {
        public string ProviderId { get; } = providerId;
        public List<LlmProviderRequest> Requests { get; } = [];

        public Task<LlmResultWithMeta> CompleteAsync(LlmProviderRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    private static ApiKeyProtector CreateProtector() =>
        new(Options.Create(new AppOptions { EncryptionSecret = TestSecrets.NewSecret() }));

    private static IConfiguration CreateConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // ---- JSON Extraction ----

    [Fact]
    public void ExtractJsonObject_handles_plain_fenced_and_prose_wrapped_responses()
    {
        var plain = JsonExtractor.ExtractJsonObject<JsonObject>("""{"band": 7.5, "ok": true}""");
        Assert.Equal(7.5, plain["band"]!.GetValue<double>());

        var fencedJson = JsonExtractor.ExtractJsonObject<JsonObject>("""
            Here is your result:
            ```json
            {"band": 8.0, "summary": "strong"}
            ```
            """);
        Assert.Equal(8.0, fencedJson["band"]!.GetValue<double>());

        var fencedPlain = JsonExtractor.ExtractJsonObject<JsonObject>("""
            ```
            {"band": 6.5}
            ```
            """);
        Assert.Equal(6.5, fencedPlain["band"]!.GetValue<double>());

        var prose = JsonExtractor.ExtractJsonObject<JsonObject>("""Sure! {"band": 7.0, "nested": {"a": 1}} Hope that helps.""");
        Assert.Equal(7.0, prose["band"]!.GetValue<double>());
        Assert.Equal(1, prose["nested"]!["a"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{not valid json}")]
    [InlineData("[1, 2, 3]")]
    public void ExtractJsonObject_throws_on_invalid_input(string raw)
    {
        Assert.Throws<InvalidOperationException>(() => JsonExtractor.ExtractJsonObject<JsonObject>(raw));
    }

    // ---- Key & Config Resolution ----

    [Fact]
    public async Task ConfigResolver_prefers_user_key_then_other_user_key_then_platform_key()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var data = new InMemoryData(clock);
        var protector = CreateProtector();
        var user = new UserAccount { Id = "u1", Email = "u1@example.test", EmailVerified = true };
        await data.CreateAsync(user);

        var config = CreateConfig(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = "or-platform",
            ["DEEPSEEK_API_KEY"] = "ds-platform",
        });

        var planResolver = new PlanResolver(data, data, new AuthSettings(config));
        var resolver = new AiConfigResolver(data, data, planResolver, protector, config);

        // 1. No user keys -> platform key (OpenRouter first in order), Free ignores custom model choice
        await ((ISettingsRepository)data).UpdateAsync("u1", s => s with
        {
            ActiveProvider = "deepseek",
            GraderModel = "custom-model",
        });
        var platformCfg = await resolver.ResolveAsync(user);
        Assert.Equal("platform", platformCfg.KeySource);
        Assert.Equal("openrouter", platformCfg.Provider);
        Assert.Equal("or-platform", platformCfg.ApiKey);
        Assert.Equal(AiProviders.Defaults["openrouter"].GraderModel, platformCfg.GraderModel);

        // 2. Pro plan honours model choice when platform key exists for preferred provider
        await ((ISettingsRepository)data).UpdateAsync("u1", s => s with
        {
            Plan = Plans.Pro,
            ActiveProvider = "deepseek",
            GraderModel = "deepseek-custom-pro",
        });
        var proPlatformCfg = await resolver.ResolveAsync(user);
        Assert.Equal("platform", proPlatformCfg.KeySource);
        Assert.Equal("deepseek", proPlatformCfg.Provider);
        Assert.Equal("ds-platform", proPlatformCfg.ApiKey);
        Assert.Equal("deepseek-custom-pro", proPlatformCfg.GraderModel);

        // 3. User's own key for another provider beats platform key
        await ((ISettingsRepository)data).UpdateAsync("u1", s => s with
        {
            Plan = Plans.Free,
            ActiveProvider = "deepseek",
            EncryptedApiKeys = new Dictionary<string, string>
            {
                ["openai"] = protector.Encrypt("sk-user-openai"),
            },
        });
        var otherUserCfg = await resolver.ResolveAsync(user);
        Assert.Equal("user", otherUserCfg.KeySource);
        Assert.Equal("openai", otherUserCfg.Provider);
        Assert.Equal("sk-user-openai", otherUserCfg.ApiKey);

        // 4. User's own key for preferred provider wins and honours model choice
        await ((ISettingsRepository)data).UpdateAsync("u1", s => s with
        {
            ActiveProvider = "deepseek",
            GraderModel = "deepseek-v4-custom",
            EncryptedApiKeys = new Dictionary<string, string>
            {
                ["openai"] = protector.Encrypt("sk-user-openai"),
                ["deepseek"] = protector.Encrypt("sk-user-deepseek"),
            },
        });
        var preferredUserCfg = await resolver.ResolveAsync(user);
        Assert.Equal("user", preferredUserCfg.KeySource);
        Assert.Equal("deepseek", preferredUserCfg.Provider);
        Assert.Equal("sk-user-deepseek", preferredUserCfg.ApiKey);
        Assert.Equal("deepseek-v4-custom", preferredUserCfg.GraderModel);
    }

    // ---- Access & Quota Guard ----

    [Fact]
    public async Task AccessGuard_enforces_verification_daily_cap_monthly_quota_and_rate_limit()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var data = new InMemoryData(clock);
        var protector = CreateProtector();
        var config = CreateConfig(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = "or-platform",
            ["FREE_PLAN_DAILY_AI_CAP"] = "2",
            ["STRIPE_SECRET_KEY"] = "sk_test",
            ["STRIPE_PRICE_PRO_MONTHLY"] = "price_test",
            ["STRIPE_WEBHOOK_SECRET"] = "whsec_test",
        });

        var authSettings = new AuthSettings(config);
        var planResolver = new PlanResolver(data, data, authSettings);
        var resolver = new AiConfigResolver(data, data, planResolver, protector, config);
        var env = new FakeEnvironment("Production");
        var guard = new AiAccessGuard(data, resolver, data, data, authSettings, config, env, clock);

        // Unverified Free user in Production -> 403 verification_required
        var unverified = new UserAccount { Id = "u-unver", Email = "unver@example.test", EmailVerified = false };
        await data.CreateAsync(unverified);
        var blockedVerify = await guard.GuardAsync(unverified, AiRequestKind.Evaluation);
        Assert.NotNull(blockedVerify);

        // Verified Free user
        var freeUser = new UserAccount { Id = "u-free", Email = "free@example.test", EmailVerified = true };
        await data.CreateAsync(freeUser);
        Assert.Null(await guard.GuardAsync(freeUser, AiRequestKind.Evaluation));

        // Daily cap = 2: after 2 increments, 3rd is blocked
        await ((IFreeAiDailyCounter)data).IncrementTodayAsync();
        await ((IFreeAiDailyCounter)data).IncrementTodayAsync();
        var blockedDaily = await guard.GuardAsync(freeUser, AiRequestKind.Assist);
        Assert.NotNull(blockedDaily);

        // Advance day -> daily cap resets, now test monthly quota (Free = 5 evaluations)
        clock.Now = clock.Now.AddDays(1);
        for (var i = 0; i < 4; i++)
        {
            await ((IAiUsageRepository)data).RecordAsync(freeUser.Id, new AiUsageRecord
            {
                Id = $"use-{i}",
                Feature = "rubric_score",
                Provider = "openrouter",
                KeySource = "platform",
                Plan = Plans.Free,
                ModelUsed = "m",
                CreatedAt = clock.GetUtcNow(),
            });
        }

        // 4 used + 1 unit = 5 <= 5 -> allowed
        Assert.Null(await guard.GuardAsync(freeUser, AiRequestKind.Evaluation, units: 1));
        // 4 used + 2 units = 6 > 5 -> blocked
        Assert.NotNull(await guard.GuardAsync(freeUser, AiRequestKind.Evaluation, units: 2));

        // User's own key bypasses monthly quota and daily cap
        await ((ISettingsRepository)data).UpdateAsync(freeUser.Id, s => s with
        {
            EncryptedApiKeys = new Dictionary<string, string> { ["openrouter"] = protector.Encrypt("sk-own") },
        });
        Assert.Null(await guard.GuardAsync(freeUser, AiRequestKind.Evaluation, units: 2));

        // Rate limit: 20 per minute
        var rateUser = new UserAccount { Id = "u-rate", Email = "rate@example.test", EmailVerified = true };
        await data.CreateAsync(rateUser);
        for (var i = 0; i < AiAccessGuard.AiRequestsPerMinute; i++)
        {
            Assert.Null(await guard.GuardAsync(rateUser, AiRequestKind.Assist));
        }

        var rateBlocked = await guard.GuardAsync(rateUser, AiRequestKind.Assist);
        Assert.NotNull(rateBlocked);
    }

    // ---- Examiner & Usage Tracking with Fake Provider ----

    [Fact]
    public async Task ExaminerService_evaluates_with_fake_provider_normalizes_annotations_and_records_usage()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var data = new InMemoryData(clock);
        var protector = CreateProtector();
        var config = CreateConfig(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = "or-platform",
        });

        var user = new UserAccount { Id = "u-eval", Email = "eval@example.test", EmailVerified = true };
        await data.CreateAsync(user);

        var fakeProvider = new FakeProviderClient("openrouter", req =>
        {
            if (req.UserPrompt.Contains("taTrBand", StringComparison.Ordinal))
            {
                return new LlmResultWithMeta(
                    Content: """
                    ```json
                    {
                      "taTrBand": 6.5,
                      "ccBand": 6.5,
                      "lrBand": 7.0,
                      "graBand": 6.0,
                      "examinerSummary": "Clear essay with minor comma splices.",
                      "criteria": {
                        "TA_TR": { "band": 6.5, "descriptorSummary": "Good overview.", "strengths": ["Clear"], "limitingFactors": ["Add numbers to the Overview"], "howToGainHalfBand": "Include 65% in the overview." },
                        "CC": { "band": 6.5, "descriptorSummary": "Logical.", "strengths": [], "limitingFactors": [], "howToGainHalfBand": "Use referencing." },
                        "LR": { "band": 7.0, "descriptorSummary": "Precise.", "strengths": [], "limitingFactors": [], "howToGainHalfBand": "Replace actually with extremely." },
                        "GRA": { "band": 6.0, "descriptorSummary": "Contains missing subject and comma splices.", "strengths": [], "limitingFactors": ["Missing subject"], "howToGainHalfBand": "Fix missing subjects." }
                      },
                      "topPriorities": ["Put 45% in the Overview", "Fix comma splices"]
                    }
                    ```
                    """,
                    ModelUsed: "anthropic/claude-sonnet-5.5",
                    CostUsd: 0.012,
                    PromptTokens: 100,
                    CompletionTokens: 200,
                    TotalTokens: 300);
            }

            return new LlmResultWithMeta(
                Content: """
                {
                  "inlineAnnotations": [
                    { "id": "a1", "category": "LR", "severity": "upgrade", "originalSpan": "whereas coal declined", "replacement": "while coal declined", "explanation": "Swap whereas for while" },
                    { "id": "a2", "category": "CC", "severity": "upgrade", "originalSpan": "rose sharply, however, gas fell", "replacement": "rose sharply; however, gas fell", "explanation": "Fix comma splice between independent clauses" },
                    { "id": "a3", "category": "LR", "severity": "upgrade", "originalSpan": "it has actually grown very fast", "replacement": "it has actually expanded very fast", "explanation": "False cognate actually" }
                  ],
                  "targetBandRewrite": "Overall, renewable energy expanded rapidly.",
                  "rewriteKeyChanges": ["Removed mechanical linkers"],
                  "vaultItems": [
                    { "itemType": "error", "criterion": "CC", "originalText": "a, however, b", "upgradedText": "a; however, b", "explanation": "Comma splice", "exampleSentence": "A; however, B." }
                  ]
                }
                """,
                ModelUsed: "deepseek/deepseek-v4.1-flash-1017",
                CostUsd: 0.003);
        });

        var authSettings = new AuthSettings(config);
        var planResolver = new PlanResolver(data, data, authSettings);
        var resolver = new AiConfigResolver(data, data, planResolver, protector, config);
        var completion = new AiCompletionService([fakeProvider], data, data, clock, NullLogger<AiCompletionService>.Instance);
        var examiner = new ExaminerService(resolver, completion);

        var result = await examiner.EvaluateEssayWithAiAsync(new EvaluateEssayInput(
            UserId: user.Id,
            TaskType: Ielts.Task1,
            PromptTitle: "Energy Mix",
            PromptText: "Summarise the chart.",
            VisualContext: null,
            EssayText: "Overall, coal declined whereas gas rose. It has actually grown very fast, however, coal fell.",
            DraftVersion: 1,
            PreviousBand: null,
            Outline: null,
            OutlineFeedback: null,
            CoachingHistory: null));

        Assert.Equal("openrouter", result.ProviderUsed);
        Assert.Equal("anthropic/claude-sonnet-5.5", result.ModelUsed);
        Assert.Equal(0.015, result.CostUsd);
        Assert.Equal(6.5, result.Payload.OverallBand);

        // Lateral swap "whereas" <-> "while" filtered out; comma splice moved to GRA; "actually" cleaned
        Assert.Equal(2, result.Payload.InlineAnnotations.Count);
        Assert.Equal("GRA", result.Payload.InlineAnnotations[0].Category);
        Assert.Equal("error", result.Payload.InlineAnnotations[0].Severity);
        Assert.Equal("L1", result.Payload.InlineAnnotations[1].Category);
        Assert.DoesNotContain("actually", result.Payload.InlineAnnotations[1].Replacement, StringComparison.OrdinalIgnoreCase);

        // Sanitized Task 1 Overview number advice & hallucinated missing subject
        Assert.DoesNotContain("65%", result.Payload.Criteria.TaTr.HowToGainHalfBand);
        Assert.DoesNotContain("Missing subject", result.Payload.Criteria.Gra.LimitingFactors);
        Assert.DoesNotContain("45%", result.Payload.TopPriorities[0]);

        // Usage recorded for both calls and daily counter incremented once (for rubric_score)
        var usage = await ((IAiUsageRepository)data).ListSinceAsync(user.Id, DateTimeOffset.MinValue);
        Assert.Equal(2, usage.Count);
        Assert.Equal(1, await ((IFreeAiDailyCounter)data).GetTodayAsync());
    }

    [Fact]
    public async Task Invalid_rubric_json_falls_back_without_double_charging_quota_and_null_client_fields_do_not_throw()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var data = new InMemoryData(clock);
        var protector = CreateProtector();
        var config = CreateConfig(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = "or-platform",
        });

        var user = new UserAccount { Id = "u-fb", Email = "fb@example.test", EmailVerified = true };
        await data.CreateAsync(user);

        var fakeProvider = new FakeProviderClient("openrouter", req =>
        {
            if (req.UserPrompt.Contains("taTrBand", StringComparison.Ordinal))
            {
                // Primary grader returns invalid JSON; fastModel fallback returns valid JSON
                if (req.Model == AiProviders.Defaults["openrouter"].GraderModel)
                {
                    return new LlmResultWithMeta("not valid json at all", req.Model, CostUsd: 0.001);
                }

                return new LlmResultWithMeta("""{"taTrBand":7.0,"ccBand":7.0,"lrBand":7.0,"graBand":7.0,"examinerSummary":"Fallback worked."}""", req.Model, CostUsd: 0.002);
            }

            return new LlmResultWithMeta("""{"inlineAnnotations":[],"targetBandRewrite":"Clean.","rewriteKeyChanges":[],"vaultItems":[]}""", req.Model);
        });

        var authSettings = new AuthSettings(config);
        var planResolver = new PlanResolver(data, data, authSettings);
        var resolver = new AiConfigResolver(data, data, planResolver, protector, config);
        var completion = new AiCompletionService([fakeProvider], data, data, clock, NullLogger<AiCompletionService>.Instance);
        var examiner = new ExaminerService(resolver, completion);

        var coachingWithNullSuggestions = new List<ToolkitResponse>
        {
            new() { Tool = "collocations", Headline = "Test", Analysis = "Test", Suggestions = null! },
        };

        var eval = await examiner.EvaluateEssayWithAiAsync(new EvaluateEssayInput(
            UserId: user.Id,
            TaskType: Ielts.Task2,
            PromptTitle: "Tech",
            PromptText: "Discuss tech.",
            VisualContext: null,
            EssayText: "Technology has transformed education in many ways across the globe.",
            DraftVersion: 1,
            PreviousBand: null,
            Outline: null,
            OutlineFeedback: null,
            CoachingHistory: coachingWithNullSuggestions));

        Assert.Equal(7.0, eval.Payload.OverallBand);
        Assert.Equal(1, await ((IFreeAiDailyCounter)data).GetTodayAsync());
        var monthStart = new DateTimeOffset(clock.GetUtcNow().Year, clock.GetUtcNow().Month, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, await ((IAiUsageRepository)data).CountAsync(user.Id, monthStart, UsageService.EvaluationFeatures, "platform"));

        // Null hints in drill and null outline fields without API key do not throw
        var noKeyConfig = CreateConfig([]);
        var noKeyResolver = new AiConfigResolver(data, data, new PlanResolver(data, data, new AuthSettings(noKeyConfig)), protector, noKeyConfig);
        var noKeyExaminer = new ExaminerService(noKeyResolver, completion);

        Assert.Null(await noKeyExaminer.ExtractVisualSpecFromImageAsync("data:image/png;base64,AAAA", "Prompt", user.Id));

        var outline = await noKeyExaminer.CheckOutlineWithAiAsync(
            user.Id,
            Ielts.Task2,
            "Prompt",
            null,
            new OutlineInput { ThesisOrOverview = null!, Body1Plan = null!, Body2Plan = null! });
        Assert.Equal("needs_refinement", outline.Readiness);

        var drillScore = await noKeyExaminer.ScoreMicroDrillWithAiAsync(
            user.Id,
            new MicroDrillChallenge { DrillType = "cohesion", Level = "beginner", Hints = null!, ReferenceModelAnswer = "Model." },
            "My short answer here for testing.");
        Assert.InRange(drillScore.ScoreOutOf10, 3, 9);
    }
}
