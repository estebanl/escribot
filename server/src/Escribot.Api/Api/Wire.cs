using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Api;

/// <summary>
/// The JSON the browser receives: one
/// object per stored record, with nested values (chart specs, outlines, feedback) as JSON
/// text in fields ending in <c>Json</c>. The stored documents hold those values as real
/// JSON; this is where the two meet.
/// </summary>
internal static class Wire
{
    /// <summary>UTC with three decimals, such as <c>2026-11-15T18:04:05.123Z</c>.</summary>
    public static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static JsonNode? Time(DateTimeOffset? value) => value is { } time ? Time(time) : null;

    private static JsonNode? Text(JsonElement? value) => value?.GetRawText();

    private static JsonNode Text(JsonElement? value, string whenMissing) => value?.GetRawText() ?? whenMissing;

    public static JsonObject Prompt(Prompt p) => new()
    {
        ["id"] = p.Id,
        ["userId"] = p.UserId,
        ["taskType"] = p.TaskType,
        ["category"] = p.Category,
        ["topic"] = p.Topic,
        ["title"] = p.Title,
        ["promptText"] = p.PromptText,
        ["chartSpecJson"] = Text(p.ChartSpec),
        ["imageDataUrl"] = p.ImageUrl,
        ["extractedVisualSpec"] = p.ExtractedVisualSpec,
        ["source"] = p.Source,
        ["createdAt"] = Time(p.CreatedAt),
    };

    public static JsonObject Session(WritingSession s, string userId) => new()
    {
        ["id"] = s.Id,
        ["userId"] = userId,
        ["mode"] = s.Mode,
        ["status"] = s.Status,
        ["task1PromptId"] = s.Task1PromptId,
        ["task2PromptId"] = s.Task2PromptId,
        ["startedAt"] = Time(s.StartedAt),
        ["completedAt"] = Time(s.CompletedAt),
        ["totalSecondsSpent"] = s.TotalSecondsSpent,
        ["weightedBand"] = s.WeightedBand,
    };

    public static JsonObject Draft(Draft d, string userId) => new()
    {
        ["id"] = d.Id,
        ["userId"] = userId,
        ["sessionId"] = d.SessionId,
        ["promptId"] = d.PromptId,
        ["taskType"] = d.TaskType,
        ["version"] = d.Version,
        ["outlineJson"] = Text(d.Outline),
        ["outlineFeedbackJson"] = Text(d.OutlineFeedback),
        ["essayText"] = d.EssayText,
        ["wordCount"] = d.WordCount,
        ["secondsSpent"] = d.SecondsSpent,
        ["promptHighlightsJson"] = Text(d.PromptHighlights),
        ["scratchNotes"] = d.ScratchNotes,
        ["createdAt"] = Time(d.CreatedAt),
    };

    public static JsonObject Evaluation(Evaluation e, string userId) => new()
    {
        ["id"] = e.Id,
        ["userId"] = userId,
        ["draftId"] = e.DraftId,
        ["overallBand"] = e.OverallBand,
        ["taTrBand"] = e.TaTrBand,
        ["ccBand"] = e.CcBand,
        ["lrBand"] = e.LrBand,
        ["graBand"] = e.GraBand,
        ["examinerSummary"] = e.ExaminerSummary,
        ["criterionFeedbackJson"] = Text(e.CriterionFeedback, "{}"),
        ["inlineAnnotationsJson"] = Text(e.InlineAnnotations, "[]"),
        ["targetBandRewrite"] = e.TargetBandRewrite,
        ["rewriteKeyChangesJson"] = Text(e.RewriteKeyChanges, "[]"),
        ["topPrioritiesJson"] = Text(e.TopPriorities, "[]"),
        ["providerUsed"] = e.ProviderUsed,
        ["modelUsed"] = e.ModelUsed,
        ["costUsd"] = e.CostUsd,
        ["createdAt"] = Time(e.CreatedAt),
    };

    public static JsonObject VaultItem(VaultItem v, string userId) => new()
    {
        ["id"] = v.Id,
        ["userId"] = userId,
        ["sourceDraftId"] = v.SourceDraftId,
        ["itemType"] = v.ItemType,
        ["criterion"] = v.Criterion,
        ["originalText"] = v.OriginalText,
        ["upgradedText"] = v.UpgradedText,
        ["explanation"] = v.Explanation,
        ["exampleSentence"] = v.ExampleSentence,
        ["masteryLevel"] = v.MasteryLevel,
        ["reviewCount"] = v.ReviewCount,
        ["nextReviewAt"] = Time(v.NextReviewAt),
        ["createdAt"] = Time(v.CreatedAt),
    };

    public static JsonObject Drill(MicroDrill d, string userId) => new()
    {
        ["id"] = d.Id,
        ["userId"] = userId,
        ["drillType"] = d.DrillType,
        ["targetCriterion"] = d.TargetCriterion,
        ["promptPayloadJson"] = Text(d.PromptPayload, "{}"),
        ["userResponse"] = d.UserResponse,
        ["scoreJson"] = Text(d.Score),
        ["createdAt"] = Time(d.CreatedAt),
        ["completedAt"] = Time(d.CompletedAt),
    };

    public static JsonObject AiUsage(AiUsageRecord u) => new()
    {
        ["id"] = u.Id,
        ["feature"] = u.Feature,
        ["provider"] = u.Provider,
        ["keySource"] = u.KeySource,
        ["plan"] = u.Plan,
        ["modelUsed"] = u.ModelUsed,
        ["promptTokens"] = u.PromptTokens,
        ["completionTokens"] = u.CompletionTokens,
        ["totalTokens"] = u.TotalTokens,
        ["costUsd"] = u.CostUsd,
        ["createdAt"] = Time(u.CreatedAt),
    };

    /// <summary>Ownership is implied by who holds the file, so exported rows carry no user id.</summary>
    public static JsonObject WithoutOwner(this JsonObject row)
    {
        row.Remove("userId");
        return row;
    }

    public static JsonArray Array(IEnumerable<JsonObject> rows) => new([.. rows]);
}

/// <summary>
/// Reads a request body the forgiving way: a field of the wrong
/// type counts as missing, and each route then decides what it requires.
/// </summary>
internal readonly struct Body(JsonElement root)
{
    public bool IsObject => root.ValueKind == JsonValueKind.Object;

    private JsonElement? Field(string name) =>
        IsObject && root.TryGetProperty(name, out var value) ? value : null;

    /// <summary>True when the field is present at all, even as null.</summary>
    public bool Has(string name) => Field(name) is not null;

    public bool IsNull(string name) => Field(name) is { ValueKind: JsonValueKind.Null };

    public string? String(string name) => Field(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>The trimmed text, or null when it is missing, not text, or blank.</summary>
    public string? Trimmed(string name) => String(name)?.Trim() is { Length: > 0 } text ? text : null;

    public double? Number(string name) =>
        Field(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    public bool? Boolean(string name) => Field(name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    public Body? Object(string name) => Field(name) is { ValueKind: JsonValueKind.Object } value ? new Body(value) : null;

    public IEnumerable<JsonProperty> Properties() => IsObject ? root.EnumerateObject() : [];

    /// <summary>
    /// The items of an array field, each trimmed, with null for an item that is not text;
    /// null when the field is missing or not an array.
    /// </summary>
    public IReadOnlyList<string?>? Strings(string name) => Field(name) is { ValueKind: JsonValueKind.Array } value
        ? [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : null)]
        : null;
}
