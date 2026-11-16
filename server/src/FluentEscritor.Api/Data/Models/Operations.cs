namespace FluentEscritor.Api.Data.Models;

/// <summary>One call to an AI provider. Holds token counts and cost, never prompt or essay text.</summary>
public sealed record AiUsageRecord
{
    public required string Id { get; init; }

    public string UserId { get; init; } = string.Empty;

    public required string Feature { get; init; }

    public required string Provider { get; init; }

    /// <summary>"platform" (our key, counts toward the quota) or "user" (their own key).</summary>
    public string KeySource { get; init; } = "platform";

    /// <summary>The user's plan when the request was made.</summary>
    public string? Plan { get; init; }

    public required string ModelUsed { get; init; }

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public int? TotalTokens { get; init; }

    public double? CostUsd { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Describes an uploaded image. The bytes live in Blob Storage under <see cref="BlobName"/>.</summary>
public sealed record ImageMetadata
{
    public required string Id { get; init; }

    public string UserId { get; init; } = string.Empty;

    public required string ContentType { get; init; }

    public long Bytes { get; init; }

    public required string BlobName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A server or browser error, kept for 30 days for the admin view.</summary>
public sealed record ErrorEvent
{
    public string Id { get; init; } = string.Empty;

    /// <summary>"server" or "client".</summary>
    public required string Source { get; init; }

    public required string Message { get; init; }

    public string? Stack { get; init; }

    public string? Path { get; init; }

    public string? Digest { get; init; }

    public string? UserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record RateLimitResult(bool Allowed, int RetryAfterSeconds);

/// <summary>Everything stored for one user, with credentials left out.</summary>
public sealed record UserDataExport
{
    /// <summary>Without the password hash and security stamp.</summary>
    public UserAccount? Account { get; init; }

    /// <summary>Without the encrypted API keys; see <see cref="SavedApiKeyProviders"/>.</summary>
    public UserSettings? Settings { get; init; }

    public IReadOnlyList<string> SavedApiKeyProviders { get; init; } = [];

    public Subscription? Subscription { get; init; }

    public IReadOnlyList<Prompt> Prompts { get; init; } = [];

    public IReadOnlyList<WritingSession> Sessions { get; init; } = [];

    public IReadOnlyList<Draft> Drafts { get; init; } = [];

    public IReadOnlyList<Evaluation> Evaluations { get; init; } = [];

    public IReadOnlyList<VaultItem> VaultItems { get; init; } = [];

    public IReadOnlyList<MicroDrill> MicroDrills { get; init; } = [];

    public IReadOnlyList<AiUsageRecord> AiUsage { get; init; } = [];

    public IReadOnlyList<ImageMetadata> Images { get; init; } = [];
}

/// <summary>Documents to save for one user in one go, for restoring a backup.</summary>
public sealed record UserDataImport
{
    public IReadOnlyList<Prompt> Prompts { get; init; } = [];

    public IReadOnlyList<WritingSession> Sessions { get; init; } = [];

    public IReadOnlyList<Draft> Drafts { get; init; } = [];

    public IReadOnlyList<Evaluation> Evaluations { get; init; } = [];

    public IReadOnlyList<VaultItem> VaultItems { get; init; } = [];

    public IReadOnlyList<MicroDrill> MicroDrills { get; init; } = [];
}

public sealed record AccountStats(int Total, int Verified, int CreatedLast7Days, int CreatedLast30Days);

public sealed record AdminAccountRow(
    string Id,
    string Name,
    string Email,
    bool EmailVerified,
    DateTimeOffset CreatedAt,
    string? StoredPlan,
    string? SubscriptionStatus);

public sealed record AiUsageTotal(string? Plan, string KeySource, double CostUsd, int Calls);

/// <param name="CountedCalls">Calls to the features the caller asked to count.</param>
public sealed record UserAiUsage(string UserId, int CountedCalls, double PlatformCostUsd);
