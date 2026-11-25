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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? FastFallbacks = null)
{
    /// <summary>The provider's name in the Settings picker, such as "Anthropic".</summary>
    public string Name { get; init; } = Label;

    /// <summary>One line under the name in the Settings picker.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// The models a newly added provider starts with when its live list cannot be loaded
    /// (see <c>POST /api/settings/providers/{id}/models</c>). Roles normally use models
    /// picked from the live list by <see cref="Ai.ModelPicker"/>; these ids are the fallback,
    /// so they need no updating when providers release new models.
    /// </summary>
    public IReadOnlyList<string> SuggestedModels =>
        [.. new[] { GraderModel, FastModel, VisionModel }.Concat(GraderFallbacks ?? []).Concat(FastFallbacks ?? []).Distinct()];
}

/// <summary>Which model the examiner, the fast coach and the Task 1 vision step each use.</summary>
public enum ModelRole
{
    Grader,
    Fast,
    Vision,
}

/// <summary>The AI providers a user can choose in Settings, with the models each starts with.</summary>
public static class AiProviders
{
    public const string OpenRouter = "openrouter";

    /// <summary>The most models one provider can list in Settings.</summary>
    public const int MaxModelsPerProvider = 20;

    /// <summary>Also the order in which a platform key is picked when the plan does not allow choosing.</summary>
    public static readonly IReadOnlyList<string> Ids = [OpenRouter, "deepseek", "gemini", "openai", "anthropic"];

    public static readonly IReadOnlyDictionary<string, ProviderDefaults> Defaults = new Dictionary<string, ProviderDefaults>
    {
        [OpenRouter] = new(
            "OpenRouter (Sonnet 5.5 + DS Flash Vision)",
            "anthropic/claude-sonnet-5.5",
            "deepseek/deepseek-v4.1-flash-1017",
            "deepseek/deepseek-v4.1-flash-1017",
            ["anthropic/claude-sonnet-5.5", "openai/gpt-6.2-sol", "google/gemini-4-carbon"],
            ["deepseek/deepseek-v4.1-flash-1017", "anthropic/claude-haiku-5.5", "openai/gpt-6-luna"])
        {
            Name = "OpenRouter",
            Description = "One key for hundreds of models from every major lab.",
        },
        ["deepseek"] = new("DeepSeek (V4 Pro + V4.1 Flash Vision)", "deepseek-v4-pro", "deepseek-flash", "deepseek-flash")
        {
            Name = "DeepSeek",
            Description = "DeepSeek's own API: V4 Pro and the fast V4.1 Flash.",
        },
        ["gemini"] = new(
            "Google Gemini (4 Carbon + 3.8 Flash)",
            "gemini-4-carbon",
            "gemini-3.8-flash",
            "gemini-3.8-flash",
            ["gemini-4-carbon", "gemini-4-argon"],
            ["gemini-3.8-flash", "gemini-3.5-flash-lite"])
        {
            Name = "Google Gemini",
            Description = "Gemini models through Google AI Studio.",
        },
        ["openai"] = new(
            "OpenAI (GPT-6.2 Sol + GPT-6 Luna)",
            "gpt-6.2-sol",
            "gpt-6-luna",
            "gpt-6.1-terra",
            ["gpt-6.2-sol", "gpt-6.2-astra"],
            ["gpt-6-luna", "gpt-6.1-terra"])
        {
            Name = "OpenAI",
            Description = "GPT models over the OpenAI API.",
        },
        ["anthropic"] = new(
            "Anthropic Claude (Sonnet 5.5 + Haiku 5.5)",
            "claude-sonnet-5-5",
            "claude-haiku-5-5",
            "claude-sonnet-5-5",
            ["claude-sonnet-5-5", "claude-opus-5-5"],
            ["claude-haiku-5-5"])
        {
            Name = "Anthropic",
            Description = "Hosted Claude models over the Messages API.",
        },
    };

    public static bool IsKnown(string? id) => id is not null && Defaults.ContainsKey(id);

    /// <summary>The configuration name of the platform's own key for a provider, such as <c>OPENROUTER_API_KEY</c>.</summary>
    public static string PlatformKeySetting(string id) => $"{id.ToUpperInvariant()}_API_KEY";

    /// <summary>
    /// OpenRouter model names contain a slash and no other provider's do. A name of the
    /// wrong kind for the provider is replaced by that provider's default.
    /// </summary>
    public static string ModelFor(string provider, string? model, string fallback) =>
        IsValidModel(provider, model) ? model! : fallback;

    /// <summary>
    /// Whether a model name has the right shape for the provider: <c>vendor/model</c> for
    /// OpenRouter, with something on both sides of the slash, and no slash for the others.
    /// </summary>
    public static bool IsValidModel(string provider, string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return false;
        }

        if (provider != OpenRouter)
        {
            return !model.Contains('/');
        }

        var slash = model.IndexOf('/');
        return slash > 0 && slash < model.Length - 1;
    }

    /// <summary>
    /// The provider and model a user picked for a role, before any key-based fallback.
    /// A role with no provider of its own (documents saved before roles had one) uses
    /// the legacy <see cref="UserSettings.ActiveProvider"/>.
    /// </summary>
    public static (string Provider, string Model) Chosen(UserSettings settings, ModelRole role)
    {
        var legacy = IsKnown(settings.ActiveProvider) ? settings.ActiveProvider : OpenRouter;
        var (provider, model) = role switch
        {
            ModelRole.Grader => (settings.GraderProvider, settings.GraderModel),
            ModelRole.Fast => (settings.FastProvider, settings.FastModel),
            _ => (settings.VisionProvider, settings.VisionModel),
        };
        var resolved = IsKnown(provider) ? provider! : legacy;
        return (resolved, ModelFor(resolved, model, DefaultModel(resolved, role)));
    }

    /// <summary>The provider's default model for a role.</summary>
    public static string DefaultModel(string provider, ModelRole role)
    {
        var d = Defaults[provider];
        return role switch
        {
            ModelRole.Grader => d.GraderModel,
            ModelRole.Fast => d.FastModel,
            _ => d.VisionModel,
        };
    }
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
    public static readonly string[] AssistFeatures = ["outline_check", "toolkit", "prompt_generation", "drill_generate", "drill_score", "svg_transform"];

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
