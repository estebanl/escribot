using System.Text.Json;
using System.Text.Json.Nodes;
using Escribot.Api.Ai;
using Escribot.Api.Auth;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Api;

/// <summary>
/// What the browser app needs to draw a page, for the pages that need more than one
/// endpoint's data. Each answer has the shape of the props that page's component takes. Pages that only show what an
/// existing endpoint returns (the Vault, the evaluation of a session, pricing, admin) use
/// that endpoint instead.
/// </summary>
internal static class PageEndpoints
{
    private const int RecentSessionLimit = 10;

    /// <summary>How many of the latest graded drafts of a task type the Coach looks through for priorities.</summary>
    private const int FocusSearchLimit = 25;

    public static IEndpointRouteBuilder MapPageEndpoints(this IEndpointRouteBuilder api)
    {
        // Public: they answer for signed-out visitors too (see ApiGuard).
        api.MapGet("/viewer", GetViewerAsync).OnFailure("Failed to load the signed-in user.");
        api.MapGet("/config", GetPublicConfig);

        var pages = api.MapGroup("/pages");
        pages.MapGet("/studio", GetStudioAsync).OnFailure("Failed to load the studio.");
        pages.MapGet("/write/{id}", GetWriteAsync).OnFailure("Failed to load the writing session.");
        pages.MapGet("/analytics", GetAnalyticsAsync).OnFailure("Failed to load analytics.");
        pages.MapGet("/settings", GetSettingsPageAsync).OnFailure("Failed to load settings.");
        pages.MapGet("/drills", GetDrillsPageAsync).OnFailure("Failed to load drills.");
        return api;
    }

    /// <summary>
    /// Who is signed in, with what every signed-in page decides on: the plan, whether to
    /// offer an upgrade, admin access and whether the welcome questions are answered.
    /// <c>{ "user": null }</c> when nobody is.
    /// </summary>
    private static async Task<IResult> GetViewerAsync(
        HttpContext context,
        PlanResolver plans,
        ISettingsRepository settings,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Personal, so never stored by a cache.
        context.Response.Headers.CacheControl = "private, no-store";
        if (context.GetSession() is not { } session)
        {
            return Results.Json(new JsonObject { ["user"] = null });
        }

        var account = session.Account;
        var plan = await plans.GetPlanAsync(account, cancellationToken);
        var stored = await settings.GetOrCreateAsync(account.Id, cancellationToken);

        return Results.Json(new JsonObject
        {
            ["user"] = new JsonObject
            {
                ["id"] = account.Id,
                ["name"] = account.Name,
                ["email"] = account.Email,
                ["emailVerified"] = account.EmailVerified,
                ["hasPassword"] = account.PasswordHash is not null,
                ["plan"] = plan,
                ["canUpgrade"] = plan == Plans.Free && Plans.IsBillingEnabled(configuration),
                ["isAdmin"] = session.IsAdmin,
                ["onboarded"] = stored.OnboardedAt is not null,
            },
        });
    }

    /// <summary>The operator details the legal pages show: <c>SITE_OPERATOR</c>, <c>SUPPORT_EMAIL</c> and <c>SITE_JURISDICTION</c>.</summary>
    private static IResult GetPublicConfig(IConfiguration configuration) => Results.Json(new JsonObject
    {
        ["site"] = new JsonObject
        {
            ["operator"] = Setting(configuration, "SITE_OPERATOR") ?? "the operator of Escribot",
            ["supportEmail"] = Setting(configuration, "SUPPORT_EMAIL"),
            ["jurisdiction"] = Setting(configuration, "SITE_JURISDICTION"),
        },
    });

    private static string? Setting(IConfiguration configuration, string name) =>
        configuration[name]?.Trim() is { Length: > 0 } value ? value : null;

    // ---- Studio (the home page when signed in) ----

    private static async Task<IResult> GetStudioAsync(
        HttpContext context,
        IPromptRepository prompts,
        IWritingRepository writing,
        ISettingsRepository settings,
        AiConfigResolver aiConfig,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var userId = account.Id;
        var stored = await settings.GetOrCreateAsync(userId, cancellationToken);
        var config = await aiConfig.ResolveAsync(account, cancellationToken);

        var visible = (await prompts.ListVisibleAsync(userId, cancellationToken)).OrderByDescending(p => p.CreatedAt).ToList();
        var promptsById = visible.ToDictionary(p => p.Id);
        var sessions = (await writing.ListSessionsAsync(userId, cancellationToken)).Take(RecentSessionLimit);
        var drafts = await writing.ListDraftsAsync(userId, cancellationToken);
        var evaluatedDraftIds = (await writing.ListEvaluationsAsync(userId, cancellationToken)).Select(e => e.DraftId).ToHashSet();

        var recentSessions = sessions.Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id,
            ["mode"] = s.Mode,
            ["status"] = s.Status,
            ["startedAt"] = Wire.Time(s.StartedAt),
            ["weightedBand"] = s.WeightedBand,
            ["task1Title"] = Title(promptsById, s.Task1PromptId),
            ["task2Title"] = Title(promptsById, s.Task2PromptId),
            ["latestDraftVersion"] = drafts.Where(d => d.SessionId == s.Id && evaluatedDraftIds.Contains(d.Id)).Select(d => d.Version).DefaultIfEmpty(0).Max(),
        });

        return Results.Json(new JsonObject
        {
            ["prompts"] = Wire.Array(visible.Select(Wire.Prompt)),
            ["recentSessions"] = new JsonArray([.. recentSessions]),
            ["settingsSummary"] = new JsonObject
            {
                ["currentBand"] = config.CurrentBand,
                ["targetBand"] = config.TargetBand,
                // The browser counts the days itself, in the student's own time zone.
                ["examDate"] = stored.ExamDate,
                ["nativeLanguage"] = config.NativeLanguage,
                ["l1CoachingEnabled"] = config.L1CoachingEnabled,
                ["activeProvider"] = config.Grader.Provider,
                ["activeProviderReady"] = !string.IsNullOrEmpty(config.Grader.ApiKey),
            },
        });
    }

    private static string? Title(Dictionary<string, Prompt> prompts, string? id) =>
        id is not null && prompts.TryGetValue(id, out var prompt) ? prompt.Title : null;

    // ---- Writing: the Coach workspace or the exam simulator ----

    private static async Task<IResult> GetWriteAsync(
        string id,
        HttpContext context,
        IWritingRepository writing,
        IPromptRepository prompts,
        CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var bundle = await writing.GetSessionBundleAsync(userId, id, cancellationToken);
        if (bundle is null)
        {
            // Also the answer for another user's session: its existence is not revealed.
            return ApiResults.Error(StatusCodes.Status404NotFound, "Writing session not found.");
        }

        var session = bundle.Session;
        var task1Prompt = session.Task1PromptId is { } t1 ? await prompts.GetVisibleAsync(userId, t1, cancellationToken) : null;
        var task2Prompt = session.Task2PromptId is { } t2 ? await prompts.GetVisibleAsync(userId, t2, cancellationToken) : null;

        // Saved drafts of an interrupted or unfinished attempt, latest version first.
        var savedDrafts = bundle.Drafts.OrderByDescending(d => d.Version).ToList();

        if (session.Mode == "coach")
        {
            if ((task1Prompt ?? task2Prompt) is not { } activePrompt)
            {
                return ApiResults.Error(StatusCodes.Status404NotFound, "Writing session not found.");
            }

            var latestSaved = savedDrafts.FirstOrDefault(d => d.PromptId == activePrompt.Id);
            return Results.Json(new JsonObject
            {
                ["sessionId"] = session.Id,
                ["mode"] = session.Mode,
                ["coach"] = new JsonObject
                {
                    ["prompt"] = Wire.Prompt(activePrompt),
                    ["previousFocus"] = await PreviousFocusAsync(writing, userId, session.Id, activePrompt, cancellationToken),
                    ["initialDraft"] = latestSaved is null ? null : new JsonObject
                    {
                        ["essayText"] = latestSaved.EssayText,
                        ["secondsSpent"] = latestSaved.SecondsSpent,
                        ["outline"] = Node(latestSaved.Outline),
                        ["outlineFeedback"] = Node(latestSaved.OutlineFeedback),
                    },
                },
            });
        }

        return Results.Json(new JsonObject
        {
            ["sessionId"] = session.Id,
            ["mode"] = session.Mode,
            ["simulator"] = new JsonObject
            {
                ["task1Prompt"] = task1Prompt is null ? null : Wire.Prompt(task1Prompt),
                ["task2Prompt"] = task2Prompt is null ? null : Wire.Prompt(task2Prompt),
                ["initialDrafts"] = new JsonObject
                {
                    ["task1"] = SimulatorPart(savedDrafts, task1Prompt),
                    ["task2"] = SimulatorPart(savedDrafts, task2Prompt),
                },
            },
        });
    }

    /// <summary>
    /// The examiner's top priorities from the most recent graded draft of the same task
    /// type, preferring this session and prompt (a redraft), so the student practises
    /// exactly what held them back.
    /// </summary>
    private static async Task<JsonObject?> PreviousFocusAsync(IWritingRepository writing, string userId, string sessionId, Prompt prompt, CancellationToken cancellationToken)
    {
        var draftsById = new Dictionary<string, Draft>();
        foreach (var draft in await writing.ListDraftsAsync(userId, cancellationToken))
        {
            draftsById.TryAdd(draft.Id, draft);
        }

        var graded = (await writing.ListEvaluationsAsync(userId, cancellationToken))
            .Select(e => (Evaluation: e, Draft: draftsById.GetValueOrDefault(e.DraftId)))
            .Where(g => g.Draft?.TaskType == prompt.TaskType)
            .Take(FocusSearchLimit)
            .ToList();

        var source = graded.FirstOrDefault(g => g.Draft!.SessionId == sessionId && g.Draft.PromptId == prompt.Id);
        if (source.Draft is null)
        {
            source = graded.FirstOrDefault();
        }

        if (source.Draft is null)
        {
            return null;
        }

        var priorities = source.Evaluation.TopPriorities is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                .Select(p => (JsonNode)p.GetString()!)
                .ToList()
            : [];
        if (priorities.Count == 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["overallBand"] = source.Evaluation.OverallBand,
            ["topPriorities"] = new JsonArray([.. priorities]),
            ["fromVersion"] = source.Draft.Version,
            ["isRedraft"] = source.Draft.SessionId == sessionId && source.Draft.PromptId == prompt.Id,
        };
    }

    private static JsonObject? SimulatorPart(List<Draft> savedDrafts, Prompt? prompt)
    {
        var latest = prompt is null ? null : savedDrafts.FirstOrDefault(d => d.PromptId == prompt.Id);
        if (latest is null)
        {
            return null;
        }

        var highlights = latest.PromptHighlights is { ValueKind: JsonValueKind.Array } list ? Node(list) : new JsonArray();
        return new JsonObject
        {
            ["essayText"] = latest.EssayText,
            ["secondsSpent"] = latest.SecondsSpent,
            ["promptHighlights"] = highlights,
            ["scratchNotes"] = latest.ScratchNotes,
        };
    }

    private static JsonNode? Node(JsonElement? value) =>
        value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element ? JsonNode.Parse(element.GetRawText()) : null;

    // ---- Band progress ----

    private static async Task<IResult> GetAnalyticsAsync(
        HttpContext context,
        IWritingRepository writing,
        IPromptRepository prompts,
        IAiUsageRepository usage,
        AiConfigResolver aiConfig,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var userId = account.Id;
        var config = await aiConfig.ResolveAsync(account, cancellationToken);
        var sessions = await writing.ListSessionsAsync(userId, cancellationToken);
        var drafts = await writing.ListDraftsAsync(userId, cancellationToken);
        var evaluations = await writing.ListEvaluationsAsync(userId, cancellationToken);
        var promptsById = (await prompts.ListVisibleAsync(userId, cancellationToken)).ToDictionary(p => p.Id);

        var draftsById = new Dictionary<string, Draft>();
        foreach (var draft in drafts)
        {
            draftsById.TryAdd(draft.Id, draft);
        }

        var trendPoints = evaluations.Reverse().Select((evaluation, index) =>
        {
            var draft = draftsById.GetValueOrDefault(evaluation.DraftId);
            var prompt = draft is null ? null : promptsById.GetValueOrDefault(draft.PromptId);
            return new JsonObject
            {
                ["index"] = index + 1,
                ["date"] = Wire.Time(evaluation.CreatedAt)[..10],
                ["label"] = prompt is null ? $"#{index + 1}" : $"{(prompt.TaskType == Ielts.Task1 ? "T1" : "T2")} v{draft!.Version}",
                ["overall"] = evaluation.OverallBand,
                ["taTr"] = evaluation.TaTrBand,
                ["cc"] = evaluation.CcBand,
                ["lr"] = evaluation.LrBand,
                ["gra"] = evaluation.GraBand,
            };
        });

        var (overall, taTr, cc, lr, gra) = (0d, 0d, 0d, 0d, 0d);
        var weakest = "CC";
        if (evaluations.Count > 0)
        {
            overall = Average(e => e.OverallBand);
            taTr = Average(e => e.TaTrBand);
            cc = Average(e => e.CcBand);
            lr = Average(e => e.LrBand);
            gra = Average(e => e.GraBand);

            // The lowest average; on a tie the first in this order.
            weakest = new[] { ("TA_TR", taTr), ("CC", cc), ("LR", lr), ("GRA", gra) }.OrderBy(c => c.Item2).First().Item1;
        }

        var evaluatedIds = evaluations.Select(e => e.DraftId).ToHashSet();
        var evaluatedDrafts = drafts.Where(d => evaluatedIds.Contains(d.Id)).ToList();

        var sessionRows = sessions.Select(s =>
        {
            var sessionDrafts = evaluatedDrafts.Where(d => d.SessionId == s.Id).ToList();
            var t1 = Title(promptsById, s.Task1PromptId);
            var t2 = Title(promptsById, s.Task2PromptId);
            return (JsonNode)new JsonObject
            {
                ["id"] = s.Id,
                ["mode"] = s.Mode,
                ["status"] = s.Status,
                ["startedAt"] = Wire.Time(s.StartedAt),
                ["weightedBand"] = s.WeightedBand,
                ["title"] = !string.IsNullOrEmpty(t1) && !string.IsNullOrEmpty(t2) ? $"{t1} + {t2}" : Nonempty(t2) ?? Nonempty(t1) ?? "Writing Practice",
                ["draftCount"] = sessionDrafts.Count,
                ["wordCount"] = sessionDrafts.Sum(d => d.WordCount),
            };
        });

        // This calendar month, in UTC.
        var now = time.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var monthlyUsage = await usage.ListSinceAsync(userId, monthStart, cancellationToken);
        var usageSpend = monthlyUsage.Sum(u => u.CostUsd ?? 0);
        var evaluationSpend = evaluations.Where(e => e.CreatedAt >= monthStart).Sum(e => e.CostUsd ?? 0);

        var featureSpend = new JsonObject();
        foreach (var record in monthlyUsage)
        {
            featureSpend[record.Feature] = (featureSpend[record.Feature]?.GetValue<double>() ?? 0) + (record.CostUsd ?? 0);
        }

        return Results.Json(new JsonObject
        {
            ["targetBand"] = config.TargetBand,
            ["totalEssaysGraded"] = evaluations.Count,
            ["totalWordsWritten"] = evaluatedDrafts.Sum(d => d.WordCount),
            ["averages"] = new JsonObject { ["overall"] = overall, ["TA_TR"] = taTr, ["CC"] = cc, ["LR"] = lr, ["GRA"] = gra },
            ["weakestCriterion"] = weakest,
            ["trendPoints"] = Wire.Array(trendPoints),
            ["sessionRows"] = new JsonArray([.. sessionRows]),
            ["monthlySpendUsd"] = Math.Round(Math.Max(usageSpend, evaluationSpend), 4, MidpointRounding.AwayFromZero),
            ["usageCallsCount"] = monthlyUsage.Count,
            ["featureSpend"] = featureSpend,
        });

        double Average(Func<Evaluation, double> band) => Math.Round(evaluations.Average(band), 2, MidpointRounding.AwayFromZero);

        static string? Nonempty(string? text) => string.IsNullOrEmpty(text) ? null : text;
    }

    // ---- Settings and drills ----

    /// <summary>What the account panel needs beyond the viewer: whether deleting the account also ends a subscription.</summary>
    private static async Task<IResult> GetSettingsPageAsync(HttpContext context, ISubscriptionRepository subscriptions, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetAsync(context.GetSession()!.Account.Id, cancellationToken);
        return Results.Json(new JsonObject { ["hasSubscription"] = Plans.HasProAccess(subscription) });
    }

    /// <summary>The current band, which picks the drills' default level.</summary>
    private static async Task<IResult> GetDrillsPageAsync(HttpContext context, ISettingsRepository settings, CancellationToken cancellationToken)
    {
        var stored = await settings.GetOrCreateAsync(context.GetSession()!.Account.Id, cancellationToken);
        return Results.Json(new JsonObject { ["currentBand"] = stored.CurrentBand });
    }
}
