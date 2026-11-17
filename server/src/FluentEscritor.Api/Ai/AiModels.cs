using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FluentEscritor.Api.Ai;

public sealed record ResolvedAiConfig(
    string UserId,
    string Provider,
    string GraderModel,
    string FastModel,
    string VisionModel,
    string? ApiKey,
    string KeySource,
    string Plan,
    double CurrentBand,
    double TargetBand,
    string NativeLanguage,
    bool L1CoachingEnabled);

public sealed record LlmCompletionRequest
{
    public required string UserId { get; init; }
    public required string KeySource { get; init; }
    public required string Plan { get; init; }
    public required string Provider { get; init; }
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
    public IReadOnlyList<string>? FallbackModels { get; init; }
    public string Feature { get; init; } = "general";
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    public string? ImageDataUrl { get; init; }
    public bool JsonMode { get; init; } = true;
    public string ThinkingMode { get; init; } = "disabled";
    public string ReasoningEffort { get; init; } = "high";
    public int? MaxTokens { get; init; }
    public int TimeoutMs { get; init; } = 45_000;
}

public sealed record LlmProviderRequest(
    string ApiKey,
    string Model,
    IReadOnlyList<string>? FallbackModels,
    string SystemPrompt,
    string UserPrompt,
    string? ImageDataUrl,
    bool JsonMode,
    string ThinkingMode,
    string ReasoningEffort,
    int? MaxTokens);

public sealed record LlmResultWithMeta(
    string Content,
    string ModelUsed,
    double? CostUsd = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null);

public enum AiRequestKind
{
    Evaluation,
    Assist,
}

public sealed record CriterionFeedback
{
    [JsonPropertyName("band")]
    public double Band { get; set; }

    [JsonPropertyName("descriptorSummary")]
    public string DescriptorSummary { get; set; } = string.Empty;

    [JsonPropertyName("strengths")]
    public List<string> Strengths { get; set; } = [];

    [JsonPropertyName("limitingFactors")]
    public List<string> LimitingFactors { get; set; } = [];

    [JsonPropertyName("howToGainHalfBand")]
    public string HowToGainHalfBand { get; set; } = string.Empty;
}

public sealed record EvaluationCriteria
{
    [JsonPropertyName("TA_TR")]
    public CriterionFeedback TaTr { get; set; } = new();

    [JsonPropertyName("CC")]
    public CriterionFeedback Cc { get; set; } = new();

    [JsonPropertyName("LR")]
    public CriterionFeedback Lr { get; set; } = new();

    [JsonPropertyName("GRA")]
    public CriterionFeedback Gra { get; set; } = new();
}

public sealed record InlineAnnotation
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "LR";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "upgrade";

    [JsonPropertyName("originalSpan")]
    public string OriginalSpan { get; set; } = string.Empty;

    [JsonPropertyName("replacement")]
    public string Replacement { get; set; } = string.Empty;

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;
}

public sealed record EvaluationVaultItem
{
    [JsonPropertyName("itemType")]
    public string ItemType { get; set; } = "error";

    [JsonPropertyName("criterion")]
    public string Criterion { get; set; } = "LR";

    [JsonPropertyName("originalText")]
    public string OriginalText { get; set; } = string.Empty;

    [JsonPropertyName("upgradedText")]
    public string UpgradedText { get; set; } = string.Empty;

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("exampleSentence")]
    public string ExampleSentence { get; set; } = string.Empty;
}

public sealed record EvaluationPayload
{
    [JsonPropertyName("overallBand")]
    public double OverallBand { get; init; }

    [JsonPropertyName("taTrBand")]
    public double TaTrBand { get; init; }

    [JsonPropertyName("ccBand")]
    public double CcBand { get; init; }

    [JsonPropertyName("lrBand")]
    public double LrBand { get; init; }

    [JsonPropertyName("graBand")]
    public double GraBand { get; init; }

    [JsonPropertyName("examinerSummary")]
    public string ExaminerSummary { get; init; } = string.Empty;

    [JsonPropertyName("criteria")]
    public EvaluationCriteria Criteria { get; init; } = new();

    [JsonPropertyName("inlineAnnotations")]
    public List<InlineAnnotation> InlineAnnotations { get; init; } = [];

    [JsonPropertyName("targetBandRewrite")]
    public string TargetBandRewrite { get; init; } = string.Empty;

    [JsonPropertyName("rewriteKeyChanges")]
    public List<string> RewriteKeyChanges { get; init; } = [];

    [JsonPropertyName("topPriorities")]
    public List<string> TopPriorities { get; init; } = [];

    [JsonPropertyName("vaultItems")]
    public List<EvaluationVaultItem> VaultItems { get; init; } = [];
}

public sealed record OutlineInput
{
    [JsonPropertyName("thesisOrOverview")]
    public string ThesisOrOverview { get; init; } = string.Empty;

    [JsonPropertyName("body1Plan")]
    public string Body1Plan { get; init; } = string.Empty;

    [JsonPropertyName("body2Plan")]
    public string Body2Plan { get; init; } = string.Empty;

    [JsonPropertyName("targetKeyTerms")]
    public string? TargetKeyTerms { get; init; }
}

public sealed record AcademicCollocation
{
    [JsonPropertyName("phrase")]
    public string Phrase { get; init; } = string.Empty;

    [JsonPropertyName("usageNote")]
    public string UsageNote { get; init; } = string.Empty;
}

public sealed record OutlineFeedback
{
    [JsonPropertyName("readiness")]
    public string Readiness { get; init; } = "needs_refinement";

    [JsonPropertyName("estimatedTaskBandCeiling")]
    public double EstimatedTaskBandCeiling { get; init; }

    [JsonPropertyName("verdictSummary")]
    public string VerdictSummary { get; init; } = string.Empty;

    [JsonPropertyName("overviewOrThesisCheck")]
    public string OverviewOrThesisCheck { get; init; } = string.Empty;

    [JsonPropertyName("bodyGroupingCheck")]
    public string BodyGroupingCheck { get; init; } = string.Empty;

    [JsonPropertyName("actionableTweaks")]
    public List<string> ActionableTweaks { get; init; } = [];

    [JsonPropertyName("suggestedAcademicCollocations")]
    public List<AcademicCollocation> SuggestedAcademicCollocations { get; init; } = [];
}

public sealed record ToolkitRequest(
    string Tool,
    string TaskType,
    string PromptText,
    string SelectedText,
    string? FullEssayContext = null);

public sealed record ToolkitSuggestion
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("rewrittenOrPhrase")]
    public string RewrittenOrPhrase { get; init; } = string.Empty;

    [JsonPropertyName("rationale")]
    public string Rationale { get; init; } = string.Empty;
}

public sealed record ToolkitResponse
{
    [JsonPropertyName("tool")]
    public string Tool { get; init; } = string.Empty;

    [JsonPropertyName("headline")]
    public string Headline { get; init; } = string.Empty;

    [JsonPropertyName("analysis")]
    public string Analysis { get; init; } = string.Empty;

    [JsonPropertyName("suggestions")]
    public List<ToolkitSuggestion> Suggestions { get; init; } = [];
}

public sealed record MicroDrillStep
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("starter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Starter { get; init; }
}

public sealed record MicroDrillChallenge
{
    [JsonPropertyName("drillType")]
    public string DrillType { get; init; } = string.Empty;

    [JsonPropertyName("targetCriterion")]
    public string TargetCriterion { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("instructions")]
    public string Instructions { get; init; } = string.Empty;

    [JsonPropertyName("timeLimitSeconds")]
    public int TimeLimitSeconds { get; init; }

    [JsonPropertyName("sourceContext")]
    public string SourceContext { get; init; } = string.Empty;

    [JsonPropertyName("chartSpec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? ChartSpec { get; init; }

    [JsonPropertyName("flawedText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FlawedText { get; init; }

    [JsonPropertyName("hints")]
    public List<string> Hints { get; init; } = [];

    [JsonPropertyName("referenceModelAnswer")]
    public string ReferenceModelAnswer { get; init; } = string.Empty;

    [JsonPropertyName("level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Level { get; init; }

    [JsonPropertyName("steps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MicroDrillStep>? Steps { get; init; }

    [JsonPropertyName("wordBank")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? WordBank { get; init; }
}

public sealed record MicroDrillScore
{
    [JsonPropertyName("scoreOutOf10")]
    public int ScoreOutOf10 { get; init; }

    [JsonPropertyName("estimatedBandImpact")]
    public string EstimatedBandImpact { get; init; } = string.Empty;

    [JsonPropertyName("feedback")]
    public string Feedback { get; init; } = string.Empty;

    [JsonPropertyName("improvedVersion")]
    public string ImprovedVersion { get; init; } = string.Empty;

    [JsonPropertyName("keyTakeaway")]
    public string KeyTakeaway { get; init; } = string.Empty;

    [JsonPropertyName("oneThingToFix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OneThingToFix { get; init; }

    [JsonPropertyName("stepFeedback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? StepFeedback { get; init; }
}

public sealed record GeneratedPromptResult(
    string Title,
    string Topic,
    string PromptText,
    JsonObject? ChartSpec);

public sealed record OpenRouterModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("contextLength")] int ContextLength,
    [property: JsonPropertyName("hasVision")] bool HasVision,
    [property: JsonPropertyName("supportsJson")] bool SupportsJson,
    [property: JsonPropertyName("promptPricePerM")] double PromptPricePerM,
    [property: JsonPropertyName("completionPricePerM")] double CompletionPricePerM);

public sealed record EvaluateEssayInput(
    string UserId,
    string TaskType,
    string PromptTitle,
    string PromptText,
    string? VisualContext,
    string EssayText,
    int DraftVersion,
    double? PreviousBand,
    OutlineInput? Outline,
    OutlineFeedback? OutlineFeedback,
    IReadOnlyList<ToolkitResponse>? CoachingHistory,
    Action<string>? OnProgress = null,
    ResolvedAiConfig? Config = null);

public sealed record EvaluateEssayResult(
    EvaluationPayload Payload,
    string ProviderUsed,
    string ModelUsed,
    double? CostUsd);
