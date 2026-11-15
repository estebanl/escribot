namespace FluentEscritor.Api.Data.Models;

/// <summary>A person's account. Sign-in (issue 10) builds its user store on this document.</summary>
public sealed record UserAccount
{
    public required string Id { get; init; }

    public required string Email { get; init; }

    /// <summary>Set by the repository; the value the email lookup is keyed on.</summary>
    public string NormalizedEmail { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool EmailVerified { get; init; }

    public string? Image { get; init; }

    public string? PasswordHash { get; init; }

    public string? SecurityStamp { get; init; }

    public IReadOnlyList<ExternalLogin> ExternalLogins { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ExternalLogin(string Provider, string ProviderKey, DateTimeOffset CreatedAt);

public enum CreateAccountResult
{
    Created,
    EmailTaken,
}

/// <summary>A signed-in browser session, found by its token. Only a hash of the token is stored.</summary>
public sealed record AuthSession(string UserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record UserSettings
{
    public required string UserId { get; init; }

    public double CurrentBand { get; init; } = 6.5;

    public double TargetBand { get; init; } = 7.5;

    public string? ExamDate { get; init; }

    public string NativeLanguage { get; init; } = "es";

    public bool L1CoachingEnabled { get; init; } = true;

    public string ActiveProvider { get; init; } = "openrouter";

    public string GraderModel { get; init; } = "anthropic/claude-sonnet-5.5";

    public string FastModel { get; init; } = "deepseek/deepseek-v4.1-flash-1017";

    public string VisionModel { get; init; } = "deepseek/deepseek-v4.1-flash-1017";

    /// <summary>Provider name to the encrypted API key the user saved for it.</summary>
    public IReadOnlyDictionary<string, string> EncryptedApiKeys { get; init; } = new Dictionary<string, string>();

    /// <summary>Manual override: "pro" is complimentary Pro. Paid Pro comes from <see cref="Subscription"/>.</summary>
    public string Plan { get; init; } = "free";

    /// <summary>Null until the welcome questions are answered.</summary>
    public DateTimeOffset? OnboardedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Stripe billing state: one document, and one Stripe customer, per user.</summary>
public sealed record Subscription
{
    public required string UserId { get; init; }

    public required string StripeCustomerId { get; init; }

    public string? StripeSubscriptionId { get; init; }

    /// <summary>Stripe subscription status; null until the first subscription.</summary>
    public string? Status { get; init; }

    public string? PriceId { get; init; }

    public DateTimeOffset? CurrentPeriodEnd { get; init; }

    public bool CancelAtPeriodEnd { get; init; }

    /// <summary>
    /// <c>created</c> (Unix seconds) of the last Stripe event applied, so stale or
    /// out-of-order deliveries can be ignored.
    /// </summary>
    public long LastEventAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
