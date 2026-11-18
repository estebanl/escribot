using System.Text.Json.Serialization;
using Escribot.Api.Auth;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Api;

public sealed record ProviderDefaults(
    string Label,
    string GraderModel,
    string FastModel,
    string VisionModel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? GraderFallbacks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? FastFallbacks = null);

/// <summary>The AI providers a user can choose in Settings, with the models each starts with.</summary>
public static class AiProviders
{
    public const string OpenRouter = "openrouter";

    /// <summary>Also the order in which a platform key is picked when the plan does not allow choosing.</summary>
    public static readonly IReadOnlyList<string> Ids = [OpenRouter, "deepseek", "gemini", "openai", "anthropic"];

    public static readonly IReadOnlyDictionary<string, ProviderDefaults> Defaults = new Dictionary<string, ProviderDefaults>
    {
        [OpenRouter] = new(
            "OpenRouter (Sonnet 5.5 + DS Flash Vision)",
            "anthropic/claude-sonnet-5.5",
            "deepseek/deepseek-v4.1-flash-1017",
            "deepseek/deepseek-v4.1-flash-1017",
            ["anthropic/claude-sonnet-5.5", "openai/gpt-6.1-sol", "google/gemini-4-argon"],
            ["deepseek/deepseek-v4.1-flash-1017", "anthropic/claude-haiku-5.5", "openai/gpt-6-luna"]),
        ["deepseek"] = new("DeepSeek (V4 Pro + V4.1 Flash Vision)", "deepseek-v4-pro", "deepseek-flash", "deepseek-flash"),
        ["gemini"] = new("Google Gemini (2.5 Pro + 2.5 Flash)", "gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash"),
        ["openai"] = new("OpenAI (GPT-4.1 + GPT-4.1 Mini)", "gpt-4.1", "gpt-4.1-mini", "gpt-4.1"),
        ["anthropic"] = new("Anthropic Claude (Sonnet 4.5 + Haiku 4.5)", "claude-sonnet-4-5", "claude-haiku-4-5", "claude-sonnet-4-5"),
    };

    public static bool IsKnown(string? id) => id is not null && Defaults.ContainsKey(id);

    /// <summary>The configuration name of the platform's own key for a provider, such as <c>OPENROUTER_API_KEY</c>.</summary>
    public static string PlatformKeySetting(string id) => $"{id.ToUpperInvariant()}_API_KEY";

    /// <summary>
    /// OpenRouter model names contain a slash and no other provider's do. A name of the
    /// wrong kind for the provider is replaced by that provider's default.
    /// </summary>
    public static string ModelFor(string provider, string? model, string fallback) =>
        string.IsNullOrEmpty(model) || model.Contains('/') != (provider == OpenRouter) ? fallback : model;
}

public sealed record UsageCount(int Used, int? Limit);

public sealed record UsageSummary(
    string Plan,
    string PlanLabel,
    string KeySource,
    bool CanChooseModels,
    UsageCount Evaluations,
    UsageCount Assists,
    bool VerificationRequired,
    string ResetsAt);

/// <summary>
/// How much of this month's AI allowance a user has spent, for the Settings page.
/// The allowance itself is enforced by the AI endpoints.
/// </summary>
public sealed class UsageService(
    IAiUsageRepository usage,
    PlanResolver plans,
    ApiKeyProtector protector,
    AuthSettings auth,
    IConfiguration configuration,
    IHostEnvironment environment,
    TimeProvider time)
{
    // The usage features that mark one billable unit of each kind.
    public static readonly string[] EvaluationFeatures = ["rubric_score", "rubric_score_fallback"];
    public static readonly string[] AssistFeatures = ["outline_check", "toolkit", "prompt_generation", "drill_generate", "drill_score"];

    /// <summary>
    /// Free AI on the platform's keys needs an account tied to a real address: verified
    /// or invited. Outside production there is no email delivery and no abuse to prevent.
    /// </summary>
    public static bool IsTrustedForFreeUse(UserAccount account, AuthSettings auth, IHostEnvironment environment) =>
        !environment.IsProduction() || account.EmailVerified || auth.IsInvited(account.Email);

    public bool HasPlatformKey(string provider) =>
        !string.IsNullOrWhiteSpace(configuration[AiProviders.PlatformKeySetting(provider)]);

    public async Task<UsageSummary> GetSummaryAsync(UserAccount account, UserSettings settings, CancellationToken cancellationToken = default)
    {
        var plan = await plans.GetPlanAsync(account, cancellationToken);
        var limits = Plans.Limits[plan];

        // A user's own key always beats spending the platform's budget.
        var keySource = settings.EncryptedApiKeys.Values.Any(v => protector.Decrypt(v) is not null) ? "user"
            : AiProviders.Ids.Any(HasPlatformKey) ? "platform"
            : "none";

        var now = time.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var evaluations = await usage.CountAsync(account.Id, monthStart, EvaluationFeatures, "platform", cancellationToken);
        var assists = await usage.CountAsync(account.Id, monthStart, AssistFeatures, "platform", cancellationToken);

        var trusted = IsTrustedForFreeUse(account, auth, environment);

        return new UsageSummary(
            plan,
            limits.Label,
            keySource,
            limits.CanChooseModels,
            new UsageCount(evaluations, limits.EvaluationsPerMonth),
            new UsageCount(assists, limits.AssistsPerMonth),
            VerificationRequired: plan == Plans.Free && keySource == "platform" && !trusted,
            Wire.Time(monthStart.AddMonths(1)));
    }
}
