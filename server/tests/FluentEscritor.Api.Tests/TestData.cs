using System.Text.Json;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Tests;

/// <summary>Synthetic documents. Nothing here is real user content.</summary>
internal static class TestData
{
    public static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static UserAccount Account(string userId, string? email = null) => new()
    {
        Id = userId,
        Email = email ?? $"{userId}@example.test",
        Name = "Test Person",
        PasswordHash = "synthetic-password-hash",
        SecurityStamp = "synthetic-stamp",
    };

    public static Prompt Prompt(string id) => new()
    {
        Id = id,
        TaskType = "task1",
        Category = "bar",
        Topic = "Synthetic topic",
        Title = "Synthetic chart",
        PromptText = "Describe the synthetic chart.",
        ChartSpec = Json("""{"kind":"bar","data":[{"x":"a","y":1}]}"""),
        CreatedAt = Start,
    };

    public static WritingSession Session(string id, int minutes = 0) => new()
    {
        Id = id,
        Mode = "coach",
        StartedAt = Start.AddMinutes(minutes),
    };

    public static Draft Draft(string id, string sessionId, int minutes = 0) => new()
    {
        Id = id,
        SessionId = sessionId,
        PromptId = "t2-synthetic",
        TaskType = "task2",
        EssayText = "Synthetic essay text for a test.",
        WordCount = 6,
        Outline = Json("""{"thesis":"synthetic"}"""),
        CreatedAt = Start.AddMinutes(minutes),
    };

    public static Evaluation Evaluation(string id, string sessionId, string draftId, int minutes = 0) => new()
    {
        Id = id,
        SessionId = sessionId,
        DraftId = draftId,
        OverallBand = 6.5,
        ExaminerSummary = "Synthetic summary.",
        CriterionFeedback = Json("""{"TA_TR":{"band":6.5}}"""),
        ProviderUsed = "fake",
        ModelUsed = "fake-model",
        CostUsd = 0.01,
        CreatedAt = Start.AddMinutes(minutes),
    };

    public static VaultItem VaultItem(string id, int minutes = 0) => new()
    {
        Id = id,
        ItemType = "collocation",
        Criterion = "LR",
        OriginalText = "big problem",
        UpgradedText = "pressing concern",
        CreatedAt = Start.AddMinutes(minutes),
    };

    public static MicroDrill Drill(string id) => new()
    {
        Id = id,
        DrillType = "rewrite",
        TargetCriterion = "GRA",
        PromptPayload = Json("""{"sentence":"synthetic"}"""),
        CreatedAt = Start,
    };

    public static AiUsageRecord Usage(string id, string feature, string keySource = "platform", string? plan = "free", double cost = 0.02, int minutes = 0) => new()
    {
        Id = id,
        Feature = feature,
        Provider = "fake",
        KeySource = keySource,
        Plan = plan,
        ModelUsed = "fake-model",
        CostUsd = cost,
        CreatedAt = Start.AddMinutes(minutes),
    };

    public static ImageMetadata Image(string id, long bytes) => new()
    {
        Id = id,
        ContentType = "image/png",
        Bytes = bytes,
        BlobName = $"images/{id}",
        CreatedAt = Start,
    };
}
