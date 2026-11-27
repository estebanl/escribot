using System.Text.Json;

namespace Escribot.Api.Data.Models;

// Values that were JSON text in the relational schema (chart specs, outlines, feedback)
// are stored as nested JSON. This layer does not interpret them, so they are JsonElement.

public sealed record Prompt
{
    public required string Id { get; init; }

    /// <summary>Null for a curated prompt shared by all users.</summary>
    public string? UserId { get; init; }

    /// <summary>"task1" or "task2".</summary>
    public required string TaskType { get; init; }

    public required string Category { get; init; }

    public required string Topic { get; init; }

    public required string Title { get; init; }

    public required string PromptText { get; init; }

    public JsonElement? ChartSpec { get; init; }

    /// <summary>Address of an uploaded image (<c>/api/images/{id}</c>), if the prompt has one.</summary>
    public string? ImageUrl { get; init; }

    public string? ExtractedVisualSpec { get; init; }

    /// <summary>"curated", "ai_generated" or "custom".</summary>
    public string Source { get; init; } = "custom";

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the user last edited the prompt; null if never.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// When the user deleted a prompt that sessions still use. It is then left out of the
    /// library and cannot be picked or edited, but the sessions keep it to resume and review.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; init; }
}

public sealed record WritingSession
{
    public required string Id { get; init; }

    public string UserId { get; init; } = string.Empty;

    /// <summary>"coach", "simulator_t1", "simulator_t2" or "simulator_full_60m".</summary>
    public required string Mode { get; init; }

    /// <summary>"in_progress" or "completed".</summary>
    public string Status { get; init; } = "in_progress";

    public string? Task1PromptId { get; init; }

    public string? Task2PromptId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int TotalSecondsSpent { get; init; }

    public double? WeightedBand { get; init; }
}

public sealed record Draft
{
    public required string Id { get; init; }

    public string UserId { get; init; } = string.Empty;

    public required string SessionId { get; init; }

    public required string PromptId { get; init; }

    public required string TaskType { get; init; }

    public int Version { get; init; } = 1;

    public JsonElement? Outline { get; init; }

    public JsonElement? OutlineFeedback { get; init; }

    public string EssayText { get; init; } = string.Empty;

    public int WordCount { get; init; }

    public int SecondsSpent { get; init; }

    public JsonElement? PromptHighlights { get; init; }

    public string? ScratchNotes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record Evaluation
{
    public required string Id { get; init; }

    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// Copied from the draft, so a session and everything under it load in one query.
    /// </summary>
    public required string SessionId { get; init; }

    public required string DraftId { get; init; }

    public double OverallBand { get; init; }

    public double TaTrBand { get; init; }

    public double CcBand { get; init; }

    public double LrBand { get; init; }

    public double GraBand { get; init; }

    public string ExaminerSummary { get; init; } = string.Empty;

    public JsonElement? CriterionFeedback { get; init; }

    public JsonElement? InlineAnnotations { get; init; }

    public string TargetBandRewrite { get; init; } = string.Empty;

    public JsonElement? RewriteKeyChanges { get; init; }

    public JsonElement? TopPriorities { get; init; }

    public string ProviderUsed { get; init; } = string.Empty;

    public string ModelUsed { get; init; } = string.Empty;

    public double? CostUsd { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A writing session with its drafts and their evaluations, oldest first.</summary>
public sealed record SessionBundle(WritingSession Session, IReadOnlyList<Draft> Drafts, IReadOnlyList<Evaluation> Evaluations);

public sealed record VaultItem
{
    public required string Id { get; init; }

    public string UserId { get; init; } = string.Empty;

    public string? SourceDraftId { get; init; }

    /// <summary>"error" or "collocation".</summary>
    public required string ItemType { get; init; }

    /// <summary>"TA_TR", "CC", "LR", "GRA" or "L1".</summary>
    public required string Criterion { get; init; }

    public string OriginalText { get; init; } = string.Empty;

    public string UpgradedText { get; init; } = string.Empty;

    public string Explanation { get; init; } = string.Empty;

    public string ExampleSentence { get; init; } = string.Empty;

    /// <summary>0 to 3.</summary>
    public int MasteryLevel { get; init; }

    public int ReviewCount { get; init; }

    public DateTimeOffset? NextReviewAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record MicroDrill
{
    public required string Id { get; init; }

    public string UserId { get; init; } = string.Empty;

    public required string DrillType { get; init; }

    public required string TargetCriterion { get; init; }

    public JsonElement? PromptPayload { get; init; }

    public string? UserResponse { get; init; }

    public JsonElement? Score { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}
