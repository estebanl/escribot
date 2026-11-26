using System.Text.Json;
using System.Text.Json.Nodes;
using Escribot.Api.Auth;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Api;

/// <summary>Writing sessions, prompts, the Vault and drills, and the JSON the browser app expects.</summary>
internal static class WritingEndpoints
{
    private const int MaxShortText = 300;
    private const int MaxLongText = 20_000;

    private static readonly string[] SessionModes = ["coach", "simulator_t1", "simulator_t2", "simulator_full_60m"];

    public static IEndpointRouteBuilder MapWritingEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/sessions", ListSessionsAsync).OnFailure("Failed to load sessions.");
        api.MapPost("/sessions", CreateSessionAsync).OnFailure("Failed to create session.");
        api.MapGet("/sessions/{id}", GetSessionAsync).OnFailure("Failed to fetch session.");

        api.MapGet("/prompts", ListPromptsAsync).OnFailure("Failed to load prompts.");
        api.MapPost("/prompts", CreatePromptAsync).OnFailure("Failed to save custom prompt.");
        api.MapPatch("/prompts/{id}", UpdatePromptAsync).OnFailure("Failed to save your changes to the prompt.");
        api.MapDelete("/prompts/{id}", DeletePromptAsync).OnFailure("Failed to delete the prompt.");

        api.MapGet("/vault", ListVaultAsync).OnFailure("Failed to load Vault.");
        api.MapPost("/vault", AddVaultItemAsync).OnFailure("Failed to add Vault item.");
        api.MapPatch("/vault", UpdateVaultItemAsync).OnFailure("Failed to update Vault item.");
        api.MapDelete("/vault", DeleteVaultItemAsync).OnFailure("Failed to delete Vault item.");

        // Generating and scoring drills are AI requests and come with the AI endpoints.
        api.MapGet("/drills", ListDrillsAsync).OnFailure("Failed to load drills.");

        return api;
    }

    // ---- Sessions ----

    private static async Task<IResult> ListSessionsAsync(HttpContext context, IWritingRepository writing, IPromptRepository prompts, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var sessions = await writing.ListSessionsAsync(userId, cancellationToken);
        var drafts = await writing.ListDraftsAsync(userId, cancellationToken);
        var evaluations = await writing.ListEvaluationsAsync(userId, cancellationToken);
        var promptsById = (await prompts.ListVisibleAsync(userId, cancellationToken)).ToDictionary(p => p.Id);

        // Newest first, so with several evaluations of one draft the earliest is shown, as before.
        var evaluationByDraft = new Dictionary<string, Evaluation>();
        foreach (var evaluation in evaluations)
        {
            evaluationByDraft[evaluation.DraftId] = evaluation;
        }

        var sessionRows = sessions.Select(session =>
        {
            var row = Wire.Session(session, userId);
            row["task1Prompt"] = PromptOrNull(promptsById, session.Task1PromptId);
            row["task2Prompt"] = PromptOrNull(promptsById, session.Task2PromptId);
            row["drafts"] = Wire.Array(drafts.Where(d => d.SessionId == session.Id).Select(draft =>
            {
                var draftRow = Wire.Draft(draft, userId);
                draftRow["prompt"] = PromptOrNull(promptsById, draft.PromptId);
                draftRow["evaluation"] = evaluationByDraft.TryGetValue(draft.Id, out var evaluation) ? Wire.Evaluation(evaluation, userId) : null;
                return draftRow;
            }));
            return row;
        });

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
                ["label"] = prompt is null ? $"Draft #{index + 1}" : $"{(prompt.TaskType == Ielts.Task1 ? "T1" : "T2")} v{draft!.Version}",
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

        return Results.Json(new JsonObject
        {
            ["sessions"] = Wire.Array(sessionRows),
            ["analytics"] = new JsonObject
            {
                ["totalEssaysGraded"] = evaluations.Count,
                ["totalWordsWritten"] = drafts.Sum(d => d.WordCount),
                ["averages"] = new JsonObject { ["overall"] = overall, ["TA_TR"] = taTr, ["CC"] = cc, ["LR"] = lr, ["GRA"] = gra },
                ["weakestCriterion"] = weakest,
                ["trendPoints"] = Wire.Array(trendPoints),
            },
        });

        double Average(Func<Evaluation, double> band) => Math.Round(evaluations.Average(band), 2, MidpointRounding.AwayFromZero);
    }

    private static async Task<IResult> CreateSessionAsync(JsonElement json, HttpContext context, IWritingRepository writing, IPromptRepository prompts, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var body = new Body(json);
        var mode = body.Trimmed("mode") ?? "coach";
        if (!SessionModes.Contains(mode))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Unknown session mode.");
        }

        var task1PromptId = body.Trimmed("task1PromptId");
        var task2PromptId = body.Trimmed("task2PromptId");

        // A session may only reference prompts this user can see and has not deleted, each for its own task.
        foreach (var (promptId, slotTaskType) in new[] { (task1PromptId, Ielts.Task1), (task2PromptId, Ielts.Task2) })
        {
            if (promptId is null)
            {
                continue;
            }

            var prompt = await prompts.GetVisibleAsync(userId, promptId, cancellationToken);
            if (prompt is null || prompt.ArchivedAt is not null)
            {
                return ApiResults.Error(StatusCodes.Status404NotFound, "Prompt not found.");
            }

            if (prompt.TaskType != slotTaskType)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, $"The {(slotTaskType == Ielts.Task1 ? "Task 1" : "Task 2")} prompt must be a {(slotTaskType == Ielts.Task1 ? "Task 1" : "Task 2")} task.");
            }
        }

        if (mode == "simulator_full_60m" && (task1PromptId is null || task2PromptId is null))
        {
            var visible = (await prompts.ListVisibleAsync(userId, cancellationToken)).Where(p => p.ArchivedAt is null).ToList();
            task1PromptId ??= visible.FirstOrDefault(p => p.TaskType == Ielts.Task1)?.Id;
            task2PromptId ??= visible.FirstOrDefault(p => p.TaskType == Ielts.Task2)?.Id;
        }

        var session = new WritingSession
        {
            Id = NewId("sess"),
            Mode = mode,
            Status = "in_progress",
            Task1PromptId = task1PromptId,
            Task2PromptId = task2PromptId,
            StartedAt = time.GetUtcNow(),
        };
        await writing.SaveSessionAsync(userId, session, cancellationToken);

        return Results.Json(new JsonObject { ["session"] = Wire.Session(session, userId) });
    }

    private static async Task<IResult> GetSessionAsync(string id, HttpContext context, IWritingRepository writing, IPromptRepository prompts, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var bundle = await writing.GetSessionBundleAsync(userId, id, cancellationToken);
        if (bundle is null)
        {
            // Also the answer for another user's session: its existence is not revealed.
            return ApiResults.Error(StatusCodes.Status404NotFound, "Writing session not found.");
        }

        var row = Wire.Session(bundle.Session, userId);
        row["task1Prompt"] = await VisiblePromptAsync(bundle.Session.Task1PromptId);
        row["task2Prompt"] = await VisiblePromptAsync(bundle.Session.Task2PromptId);

        var drafts = new JsonArray();
        foreach (var draft in bundle.Drafts.OrderBy(d => d.Version))
        {
            var draftRow = Wire.Draft(draft, userId);
            draftRow["prompt"] = await VisiblePromptAsync(draft.PromptId);
            draftRow["evaluation"] = bundle.Evaluations.FirstOrDefault(e => e.DraftId == draft.Id) is { } evaluation ? Wire.Evaluation(evaluation, userId) : null;
            drafts.Add(draftRow);
        }

        row["drafts"] = drafts;
        return Results.Json(new JsonObject { ["session"] = row });

        async Task<JsonObject?> VisiblePromptAsync(string? promptId) =>
            promptId is not null && await prompts.GetVisibleAsync(userId, promptId, cancellationToken) is { } prompt ? Wire.Prompt(prompt) : null;
    }

    private static JsonObject? PromptOrNull(Dictionary<string, Prompt> prompts, string? id) =>
        id is not null && prompts.TryGetValue(id, out var prompt) ? Wire.Prompt(prompt) : null;

    // ---- Prompts ----

    private static async Task<IResult> ListPromptsAsync(string? taskType, HttpContext context, IPromptRepository prompts, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var visible = await prompts.ListVisibleAsync(userId, cancellationToken);
        var rows = visible
            .Where(p => p.ArchivedAt is null && (string.IsNullOrEmpty(taskType) || p.TaskType == taskType))
            .OrderByDescending(p => p.CreatedAt)
            .Select(Wire.Prompt);

        return Results.Json(new JsonObject { ["prompts"] = Wire.Array(rows) });
    }

    private static async Task<IResult> CreatePromptAsync(
        JsonElement json,
        HttpContext context,
        IPromptRepository prompts,
        ImageStore images,
        PlanResolver plans,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var body = new Body(json);
        var taskType = body.String("taskType");
        var promptText = body.Trimmed("promptText");
        if (taskType is not (Ielts.Task1 or Ielts.Task2) || promptText is null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Task type and prompt instructions are required.");
        }

        var category = body.Trimmed("category");
        var topic = body.Trimmed("topic");
        var title = body.Trimmed("title");
        var extractedVisualSpec = taskType == Ielts.Task1 ? body.Trimmed("extractedVisualSpec") : null;
        if (IsTooLong(promptText, extractedVisualSpec, category, topic, title))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "The prompt is too long.");
        }

        // Chart screenshots go to file storage; the prompt keeps only a link.
        string? imageUrl = null;
        if (taskType == Ielts.Task1 && body.String("imageDataUrl") is { Length: > 0 } imageDataUrl)
        {
            try
            {
                imageUrl = await images.StoreAsync(account.Id, await plans.GetPlanAsync(account, cancellationToken), imageDataUrl, cancellationToken: cancellationToken);
            }
            catch (ImageUploadException ex)
            {
                return ApiResults.Error(ex.StatusCode, ex.Message, ex.Code);
            }
        }

        var prompt = new Prompt
        {
            Id = NewId("custom"),
            UserId = account.Id,
            TaskType = taskType,
            Category = category ?? DefaultCategory(taskType),
            Topic = topic ?? DefaultTopic,
            Title = title ?? DefaultTitle(taskType),
            PromptText = promptText,
            ImageUrl = imageUrl,
            ExtractedVisualSpec = imageUrl is not null ? extractedVisualSpec : null,
            Source = "custom",
            CreatedAt = time.GetUtcNow(),
        };
        try
        {
            await prompts.SaveAsync(account.Id, prompt, cancellationToken);
        }
        catch
        {
            // Without its prompt the image would be unreachable but still count as used space.
            await images.DiscardAsync(account.Id, imageUrl);
            throw;
        }

        return Results.Json(new JsonObject { ["prompt"] = Wire.Prompt(prompt) });
    }

    private const string DefaultTopic = "Custom Practice";

    private static string DefaultCategory(string taskType) => taskType == Ielts.Task1 ? "line" : "opinion";

    private static string DefaultTitle(string taskType) => $"Custom {(taskType == Ielts.Task1 ? "Task 1 Visual" : "Task 2 Essay")}";

    private static bool IsTooLong(string promptText, string? extractedVisualSpec, string? category, string? topic, string? title) =>
        promptText.Length > MaxLongText
        || extractedVisualSpec?.Length > MaxLongText
        || new[] { category, topic, title }.Any(t => t?.Length > MaxShortText);

    /// <summary>Whether a prompt is one of the user's uploads that they can still edit or delete.</summary>
    private static bool IsEditable(Prompt prompt, string userId) =>
        prompt is { Source: "custom", ArchivedAt: null } && prompt.UserId == userId;

    /// <summary>
    /// The user's own uploaded prompt, or null. Curated and AI-generated prompts cannot be
    /// edited or deleted, and another user's prompt is not found, as for any other document.
    /// </summary>
    private static async Task<Prompt?> GetEditablePromptAsync(IPromptRepository prompts, string userId, string promptId, CancellationToken cancellationToken) =>
        await prompts.GetVisibleAsync(userId, promptId, cancellationToken) is { } prompt && IsEditable(prompt, userId) ? prompt : null;

    private static async Task<bool> IsUsedBySessionAsync(IWritingRepository writing, string userId, string promptId, CancellationToken cancellationToken) =>
        (await writing.ListSessionsAsync(userId, cancellationToken)).Any(s => s.Task1PromptId == promptId || s.Task2PromptId == promptId);

    /// <summary>
    /// Changes one of the user's uploaded prompts. A field left out keeps its value. For the
    /// image, <c>imageDataUrl</c> left out (or sent back as its saved link) keeps it, null or
    /// empty removes it, and a data URL replaces it. The task type cannot change while a session
    /// uses the prompt, since its drafts are written for the old task.
    /// The change is applied to the prompt as stored when it is written: an edit from another
    /// tab in between is kept unless this one changes the same field, and a prompt deleted in
    /// between is not written back. An image is discarded only once no saved version uses it.
    /// </summary>
    private static async Task<IResult> UpdatePromptAsync(
        string id,
        JsonElement json,
        HttpContext context,
        IPromptRepository prompts,
        IWritingRepository writing,
        ImageStore images,
        PlanResolver plans,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var existing = await GetEditablePromptAsync(prompts, account.Id, id, cancellationToken);
        if (existing is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "Prompt not found.");
        }

        var body = new Body(json);
        if (!body.IsObject)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "The request could not be read.");
        }

        var taskType = body.Has("taskType") ? body.String("taskType") : existing.TaskType;
        var promptText = body.Has("promptText") ? body.Trimmed("promptText") : existing.PromptText;
        if (taskType is not (Ielts.Task1 or Ielts.Task2) || promptText is null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Task type and prompt instructions are required.");
        }

        string newTaskType = taskType;
        string newPromptText = promptText;

        // What the request sets; anything it leaves out comes from the prompt as stored.
        var hasCategory = body.Has("category");
        var category = body.Trimmed("category");
        var hasTopic = body.Has("topic");
        var topic = body.Trimmed("topic");
        var hasTitle = body.Has("title");
        var title = body.Trimmed("title");
        var hasPromptText = body.Has("promptText");
        var hasVisualSpec = body.Has("extractedVisualSpec");
        var visualSpec = body.Trimmed("extractedVisualSpec");

        // The image and its visual spec belong to Task 1 only.
        var imageField = body.Has("imageDataUrl") ? body.String("imageDataUrl") : null;
        var newImageDataUrl = taskType == Ielts.Task1 && imageField is { Length: > 0 } && !imageField.StartsWith(ImageStore.RoutePrefix, StringComparison.Ordinal)
            ? imageField
            : null;
        var removesImage = taskType != Ielts.Task1 || (body.Has("imageDataUrl") && string.IsNullOrEmpty(imageField));
        if (IsTooLong(promptText, visualSpec, category, topic, title))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "The prompt is too long.");
        }

        if (taskType != existing.TaskType && await IsUsedBySessionAsync(writing, account.Id, id, cancellationToken))
        {
            return ApiResults.Error(
                StatusCodes.Status409Conflict,
                "This task already has practice sessions, so its task type cannot change. Upload it again as a new task instead.",
                "prompt_in_use");
        }

        string? storedImageUrl = null;
        if (newImageDataUrl is not null)
        {
            try
            {
                storedImageUrl = await images.StoreAsync(
                    account.Id,
                    await plans.GetPlanAsync(account, cancellationToken),
                    newImageDataUrl,
                    replacingImageUrl: existing.ImageUrl,
                    cancellationToken: cancellationToken);
            }
            catch (ImageUploadException ex)
            {
                return ApiResults.Error(ex.StatusCode, ex.Message, ex.Code);
            }
        }

        var now = time.GetUtcNow();
        (Prompt Before, Prompt After)? result;
        try
        {
            result = await prompts.UpdateAsync(account.Id, id, current =>
            {
                if (!IsEditable(current, account.Id))
                {
                    return null;
                }

                var taskTypeChanged = newTaskType != current.TaskType;
                var imageUrl = removesImage ? null : storedImageUrl ?? current.ImageUrl;
                var extractedVisualSpec = imageUrl is null ? null
                    : storedImageUrl is not null || hasVisualSpec ? visualSpec
                    : current.ExtractedVisualSpec;
                return current with
                {
                    TaskType = newTaskType,
                    Category = (hasCategory ? category : taskTypeChanged ? null : current.Category) ?? DefaultCategory(newTaskType),
                    Topic = (hasTopic ? topic : current.Topic) ?? DefaultTopic,
                    Title = (hasTitle ? title : current.Title) ?? DefaultTitle(newTaskType),
                    PromptText = hasPromptText ? newPromptText : current.PromptText,
                    ImageUrl = imageUrl,
                    ExtractedVisualSpec = extractedVisualSpec,
                    UpdatedAt = now,
                };
            }, cancellationToken);
        }
        catch
        {
            // Nothing points at the new image.
            await images.DiscardAsync(account.Id, storedImageUrl);
            throw;
        }

        if (result is not { } written || !IsEditable(written.Before, account.Id))
        {
            // Deleted (or archived) in the meantime.
            await images.DiscardAsync(account.Id, storedImageUrl);
            return ApiResults.Error(StatusCodes.Status404NotFound, "Prompt not found.");
        }

        // Only once the saved prompt no longer points at it.
        if (written.Before.ImageUrl is { } oldImage && oldImage != written.After.ImageUrl)
        {
            await images.DiscardAsync(account.Id, oldImage);
        }

        return Results.Json(new JsonObject { ["prompt"] = Wire.Prompt(written.After) });
    }

    /// <summary>
    /// Deletes one of the user's uploaded prompts and its image. A prompt that sessions use is
    /// archived instead: it leaves the library, but those sessions can still be resumed and
    /// reviewed with it, so it keeps its image too.
    /// </summary>
    private static async Task<IResult> DeletePromptAsync(string id, HttpContext context, IPromptRepository prompts, IWritingRepository writing, ImageStore images, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        if (await GetEditablePromptAsync(prompts, userId, id, cancellationToken) is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "Prompt not found.");
        }

        if (await IsUsedBySessionAsync(writing, userId, id, cancellationToken))
        {
            var now = time.GetUtcNow();
            var archived = await prompts.UpdateAsync(userId, id, current => IsEditable(current, userId) ? current with { ArchivedAt = now } : null, cancellationToken);
            return archived is { } a && IsEditable(a.Before, userId)
                ? Results.Json(new JsonObject { ["ok"] = true, ["archived"] = true })
                : ApiResults.Error(StatusCodes.Status404NotFound, "Prompt not found.");
        }

        // Only the version read as editable, so a prompt archived meanwhile is not deleted.
        var deleted = await prompts.DeleteIfAsync(userId, id, current => IsEditable(current, userId), cancellationToken);
        if (deleted is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "Prompt not found.");
        }

        await images.DiscardAsync(userId, deleted.ImageUrl);
        return Results.Json(new JsonObject { ["ok"] = true, ["archived"] = false });
    }

    // ---- Vault ----

    private static async Task<IResult> ListVaultAsync(HttpContext context, IVaultRepository vault, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var items = await vault.ListAsync(userId, cancellationToken);
        return Results.Json(new JsonObject { ["items"] = Wire.Array(items.Select(i => Wire.VaultItem(i, userId))) });
    }

    private static async Task<IResult> AddVaultItemAsync(JsonElement json, HttpContext context, IVaultRepository vault, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var body = new Body(json);
        var originalText = body.Trimmed("originalText");
        var upgradedText = body.Trimmed("upgradedText");
        if (originalText is null || upgradedText is null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Original phrase and upgraded academic phrasing are required.");
        }

        var explanation = body.Trimmed("explanation") ?? "Saved manually to personal Vault.";
        var exampleSentence = body.Trimmed("exampleSentence") ?? upgradedText;
        var criterion = body.Trimmed("criterion") ?? "LR";
        if (new[] { originalText, upgradedText, explanation, exampleSentence }.Any(t => t.Length > MaxLongText) || criterion.Length > MaxShortText)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "The Vault item is too long.");
        }

        var now = time.GetUtcNow();
        var item = new VaultItem
        {
            Id = NewId("vault"),
            ItemType = body.String("itemType") == "collocation" ? "collocation" : "error",
            Criterion = criterion,
            OriginalText = originalText,
            UpgradedText = upgradedText,
            Explanation = explanation,
            ExampleSentence = exampleSentence,
            NextReviewAt = now,
            CreatedAt = now,
        };
        await vault.SaveAsync(userId, item, cancellationToken);

        return Results.Json(new JsonObject { ["item"] = Wire.VaultItem(item, userId) });
    }

    private static async Task<IResult> UpdateVaultItemAsync(JsonElement json, HttpContext context, IVaultRepository vault, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var body = new Body(json);
        if (body.Trimmed("id") is not { } id || body.Number("masteryLevel") is not { } requested)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Item id and masteryLevel required.");
        }

        var existing = await vault.GetAsync(userId, id, cancellationToken);
        if (existing is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "Vault item not found.");
        }

        // The better an item is known, the later it comes up for review again.
        var mastery = (int)Math.Clamp(Math.Floor(requested + 0.5), 0, 3);
        var daysAhead = mastery switch { 0 => 0, 1 => 1, 2 => 3, _ => 7 };
        var nextReviewAt = time.GetUtcNow().AddDays(daysAhead);

        await vault.SaveAsync(userId, existing with { MasteryLevel = mastery, ReviewCount = existing.ReviewCount + 1, NextReviewAt = nextReviewAt }, cancellationToken);

        return Results.Json(new JsonObject { ["ok"] = true, ["masteryLevel"] = mastery, ["nextReviewAt"] = Wire.Time(nextReviewAt) });
    }

    private static async Task<IResult> DeleteVaultItemAsync(string? id, HttpContext context, IVaultRepository vault, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Missing id.");
        }

        // The same answer whether or not there was such an item of this user's.
        await vault.DeleteAsync(context.GetSession()!.Account.Id, id, cancellationToken);
        return Results.Json(new JsonObject { ["ok"] = true });
    }

    // ---- Drills ----

    private static async Task<IResult> ListDrillsAsync(HttpContext context, IDrillRepository drills, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var recent = (await drills.ListAsync(userId, cancellationToken)).Take(20);
        return Results.Json(new JsonObject { ["drills"] = Wire.Array(recent.Select(d => Wire.Drill(d, userId))) });
    }

    /// <summary>An id such as <c>sess-3f2a9c0d1b7e4a55</c>: random, and unique within one user's data.</summary>
    internal static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..16]}";
}
