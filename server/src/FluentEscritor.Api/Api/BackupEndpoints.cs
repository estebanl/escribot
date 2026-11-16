using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Common;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Api;

/// <summary>Backup files: a download of the user's writing data, and restoring one.</summary>
internal static class BackupEndpoints
{
    /// <summary>More rows than anyone's real backup holds; a larger file is refused outright.</summary>
    private const int MaxRows = 20_000;

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/backup", ExportAsync).OnFailure("Export failed.");
        api.MapPost("/backup", ImportAsync).OnFailure("Import failed.");
        return api;
    }

    private static async Task<IResult> ExportAsync(HttpContext context, IUserDataRepository userData, ImageStore images, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var export = await userData.ExportAsync(userId, cancellationToken);

        return Results.Json(new JsonObject
        {
            ["exportedAt"] = Wire.Time(time.GetUtcNow()),
            ["app"] = "FluentEscritor",
            ["version"] = 2,
            ["data"] = await DataAsync(export, userId, images, cancellationToken, includeAiUsage: false),
        });
    }

    /// <summary>The user's rows without their owner. Stored images are inlined so the file is self-contained.</summary>
    internal static async Task<JsonObject> DataAsync(UserDataExport export, string userId, ImageStore images, CancellationToken cancellationToken, bool includeAiUsage)
    {
        var prompts = new JsonArray();
        foreach (var prompt in export.Prompts)
        {
            var row = Wire.Prompt(prompt).WithoutOwner();
            row["imageDataUrl"] = await images.ResolveAsDataUrlAsync(userId, prompt.ImageUrl, cancellationToken);
            prompts.Add(row);
        }

        var data = new JsonObject
        {
            ["prompts"] = prompts,
            ["sessions"] = Wire.Array(export.Sessions.Select(s => Wire.Session(s, userId).WithoutOwner())),
            ["drafts"] = Wire.Array(export.Drafts.Select(d => Wire.Draft(d, userId).WithoutOwner())),
            ["evaluations"] = Wire.Array(export.Evaluations.Select(e => Wire.Evaluation(e, userId).WithoutOwner())),
            ["vaultItems"] = Wire.Array(export.VaultItems.Select(v => Wire.VaultItem(v, userId).WithoutOwner())),
            ["microDrills"] = Wire.Array(export.MicroDrills.Select(d => Wire.Drill(d, userId).WithoutOwner())),
        };
        if (includeAiUsage)
        {
            data["aiUsage"] = Wire.Array(export.AiUsage.Select(Wire.AiUsage));
        }

        return data;
    }

    private static async Task<IResult> ImportAsync(
        JsonElement json,
        HttpContext context,
        IUserDataRepository userData,
        ImageStore images,
        PlanResolver plans,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        if (BackupFile.Parse(json, MaxRows) is not { } file)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Invalid backup JSON format.");
        }

        // Every imported row gets a fresh id owned by the importing user, so a backup can
        // never collide with, or overwrite, existing rows. References between rows are
        // rewritten through these maps; a reference to a row that is not in the file is
        // kept as it is (a curated prompt id, for example).
        var promptIds = file.Prompts.ToLookup(p => p.Id).ToDictionary(g => g.Key, _ => NewId("custom"));
        var sessionIds = file.Sessions.ToLookup(s => s.Id).ToDictionary(g => g.Key, _ => NewId("sess"));
        var draftIds = file.Drafts.Where(d => sessionIds.ContainsKey(d.SessionId)).ToLookup(d => d.Id).ToDictionary(g => g.Key, _ => NewId("draft"));
        var sessionOfDraft = file.Drafts.ToLookup(d => d.Id).ToDictionary(g => g.Key, g => g.First().SessionId);
        string? MapPrompt(string? id) => string.IsNullOrEmpty(id) ? null : promptIds.GetValueOrDefault(id, id);

        // Inline images go back into storage, within the plan's allowance. An image that
        // cannot be stored is dropped, not the prompt. Any other reference would not resolve.
        var plan = await plans.GetPlanAsync(account, cancellationToken);
        var prompts = new List<Prompt>();
        foreach (var prompt in file.Prompts)
        {
            string? imageUrl = null;
            if (prompt.ImageUrl?.StartsWith("data:", StringComparison.Ordinal) == true)
            {
                try
                {
                    imageUrl = await images.StoreAsync(account.Id, plan, prompt.ImageUrl, cancellationToken);
                }
                catch (ImageUploadException)
                {
                }
            }

            prompts.Add(prompt with { Id = promptIds[prompt.Id], UserId = account.Id, ImageUrl = imageUrl });
        }

        var import = new UserDataImport
        {
            Prompts = prompts,
            Sessions = [.. file.Sessions.Select(s => s with { Id = sessionIds[s.Id], Task1PromptId = MapPrompt(s.Task1PromptId), Task2PromptId = MapPrompt(s.Task2PromptId) })],
            // A draft whose session is not in the file is left out, and so is its evaluation.
            Drafts = [.. file.Drafts.Where(d => sessionIds.ContainsKey(d.SessionId))
                .Select(d => d with { Id = draftIds[d.Id], SessionId = sessionIds[d.SessionId], PromptId = MapPrompt(d.PromptId)! })],
            Evaluations = [.. file.Evaluations.Where(e => draftIds.ContainsKey(e.DraftId))
                .Select(e => e with { Id = NewId("eval"), DraftId = draftIds[e.DraftId], SessionId = sessionIds[sessionOfDraft[e.DraftId]] })],
            VaultItems = [.. file.VaultItems.Select(v => v with { Id = NewId("vault"), SourceDraftId = v.SourceDraftId is not null ? draftIds.GetValueOrDefault(v.SourceDraftId) : null })],
            MicroDrills = [.. file.MicroDrills.Select(d => d with { Id = NewId("drill") })],
        };
        try
        {
            await userData.ImportAsync(account.Id, import, cancellationToken);
        }
        catch
        {
            // The prompts these images belong to may not have been saved.
            foreach (var prompt in prompts)
            {
                await images.DiscardAsync(account.Id, prompt.ImageUrl);
            }

            throw;
        }

        var importedCount = import.Prompts.Count + import.Sessions.Count + import.Drafts.Count + import.Evaluations.Count + import.VaultItems.Count + import.MicroDrills.Count;
        return Results.Json(new JsonObject { ["ok"] = true, ["importedCount"] = importedCount });
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid()}";
}

/// <summary>
/// A backup file after validation. The file is untrusted input: every row is checked
/// field by field as the zod schemas of the Next.js route did, and anything not named
/// here, a <c>userId</c> included, is ignored.
/// </summary>
internal sealed record BackupFile(
    List<Prompt> Prompts,
    List<WritingSession> Sessions,
    List<Draft> Drafts,
    List<Evaluation> Evaluations,
    List<VaultItem> VaultItems,
    List<MicroDrill> MicroDrills)
{
    /// <summary>Returns null if the file is not a valid backup.</summary>
    public static BackupFile? Parse(JsonElement root, int maxRows)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var remaining = maxRows;
            return new BackupFile(
                Rows(data, "prompts", ref remaining, r => new Prompt
                {
                    Id = r.Text("id"),
                    TaskType = r.Text("taskType") is Ielts.Task1 or Ielts.Task2 ? r.Text("taskType") : throw new FormatException(),
                    Category = r.Text("category"),
                    Topic = r.Text("topic"),
                    Title = r.Text("title"),
                    PromptText = r.Text("promptText"),
                    ChartSpec = r.OptionalJson("chartSpecJson"),
                    ImageUrl = r.OptionalText("imageDataUrl"),
                    ExtractedVisualSpec = r.OptionalText("extractedVisualSpec"),
                    // Anything else, "curated" included, becomes the user's own custom prompt.
                    Source = r.OptionalText("source") == "ai_generated" ? "ai_generated" : "custom",
                    CreatedAt = r.Time("createdAt"),
                }),
                Rows(data, "sessions", ref remaining, r => new WritingSession
                {
                    Id = r.Text("id"),
                    Mode = r.Text("mode"),
                    Status = r.TextOr("status", "in_progress"),
                    Task1PromptId = r.OptionalText("task1PromptId"),
                    Task2PromptId = r.OptionalText("task2PromptId"),
                    StartedAt = r.Time("startedAt"),
                    CompletedAt = r.OptionalTime("completedAt"),
                    TotalSecondsSpent = r.IntegerOr("totalSecondsSpent", 0),
                    WeightedBand = r.OptionalNumber("weightedBand"),
                }),
                Rows(data, "drafts", ref remaining, r => new Draft
                {
                    Id = r.Text("id"),
                    SessionId = r.Text("sessionId"),
                    PromptId = r.Text("promptId"),
                    TaskType = r.Text("taskType"),
                    Version = r.IntegerOr("version", 1),
                    Outline = r.OptionalJson("outlineJson"),
                    OutlineFeedback = r.OptionalJson("outlineFeedbackJson"),
                    EssayText = r.Text("essayText"),
                    WordCount = r.IntegerOr("wordCount", 0),
                    SecondsSpent = r.IntegerOr("secondsSpent", 0),
                    PromptHighlights = r.OptionalJson("promptHighlightsJson"),
                    ScratchNotes = r.OptionalText("scratchNotes"),
                    CreatedAt = r.Time("createdAt"),
                }),
                Rows(data, "evaluations", ref remaining, r => new Evaluation
                {
                    Id = r.Text("id"),
                    SessionId = string.Empty,
                    DraftId = r.Text("draftId"),
                    OverallBand = r.Number("overallBand"),
                    TaTrBand = r.Number("taTrBand"),
                    CcBand = r.Number("ccBand"),
                    LrBand = r.Number("lrBand"),
                    GraBand = r.Number("graBand"),
                    ExaminerSummary = r.Text("examinerSummary"),
                    CriterionFeedback = r.Json("criterionFeedbackJson"),
                    InlineAnnotations = r.Json("inlineAnnotationsJson"),
                    TargetBandRewrite = r.Text("targetBandRewrite"),
                    RewriteKeyChanges = r.Has("rewriteKeyChangesJson") ? r.Json("rewriteKeyChangesJson") : JsonSerializer.SerializeToElement(Array.Empty<string>()),
                    TopPriorities = r.Json("topPrioritiesJson"),
                    ProviderUsed = r.Text("providerUsed"),
                    ModelUsed = r.Text("modelUsed"),
                    CostUsd = r.OptionalNumber("costUsd"),
                    CreatedAt = r.Time("createdAt"),
                }),
                Rows(data, "vaultItems", ref remaining, r => new VaultItem
                {
                    Id = r.Text("id"),
                    SourceDraftId = r.OptionalText("sourceDraftId"),
                    ItemType = r.Text("itemType"),
                    Criterion = r.Text("criterion"),
                    OriginalText = r.Text("originalText"),
                    UpgradedText = r.Text("upgradedText"),
                    Explanation = r.Text("explanation"),
                    ExampleSentence = r.Text("exampleSentence"),
                    MasteryLevel = Math.Clamp(r.IntegerOr("masteryLevel", 0), 0, 3),
                    ReviewCount = r.IntegerOr("reviewCount", 0),
                    NextReviewAt = r.OptionalTime("nextReviewAt"),
                    CreatedAt = r.Time("createdAt"),
                }),
                Rows(data, "microDrills", ref remaining, r => new MicroDrill
                {
                    Id = r.Text("id"),
                    DrillType = r.Text("drillType"),
                    TargetCriterion = r.Text("targetCriterion"),
                    PromptPayload = r.Json("promptPayloadJson"),
                    UserResponse = r.OptionalText("userResponse"),
                    Score = r.OptionalJson("scoreJson"),
                    CreatedAt = r.Time("createdAt"),
                    CompletedAt = r.OptionalTime("completedAt"),
                }));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static List<T> Rows<T>(JsonElement data, string name, ref int remaining, Func<Row, T> read)
    {
        // A missing list is an empty one; anything else that is not a list is an error.
        if (!data.TryGetProperty(name, out var list))
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array || (remaining -= list.GetArrayLength()) < 0)
        {
            throw new FormatException();
        }

        return [.. list.EnumerateArray().Select(row => read(new Row(row)))];
    }

    /// <summary>One row of the file. A required field of the wrong type fails the whole file.</summary>
    private readonly struct Row
    {
        private readonly JsonElement _row;

        public Row(JsonElement row)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException();
            }

            _row = row;
        }

        /// <summary>Present and not null.</summary>
        public bool Has(string name) => Field(name) is not null;

        private JsonElement? Field(string name) =>
            _row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

        public string Text(string name) => OptionalText(name) ?? throw new FormatException();

        public string TextOr(string name, string whenMissing) => OptionalText(name) ?? whenMissing;

        public string? OptionalText(string name) => Field(name) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => throw new FormatException(),
        };

        public double Number(string name) => OptionalNumber(name) ?? throw new FormatException();

        public double? OptionalNumber(string name) => Field(name) switch
        {
            null => null,
            { ValueKind: JsonValueKind.Number } value when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
            _ => throw new FormatException(),
        };

        public int IntegerOr(string name, int whenMissing) => OptionalNumber(name) switch
        {
            null => whenMissing,
            >= 0 and <= int.MaxValue and var number => (int)number,
            _ => throw new FormatException(),
        };

        public DateTimeOffset Time(string name) => OptionalTime(name) ?? throw new FormatException();

        public DateTimeOffset? OptionalTime(string name) => OptionalText(name) switch
        {
            null => null,
            var text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) => time,
            _ => throw new FormatException(),
        };

        /// <summary>A field that holds JSON as text, as the relational columns did.</summary>
        public JsonElement Json(string name) => OptionalJson(name) ?? throw new FormatException();

        public JsonElement? OptionalJson(string name)
        {
            if (OptionalText(name) is not { } text)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new FormatException();
            }
        }
    }
}
