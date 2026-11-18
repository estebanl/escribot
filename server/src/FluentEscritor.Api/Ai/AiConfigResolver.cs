using FluentEscritor.Api.Api;
using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Common;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Ai;

/// <summary>
/// Resolves which AI provider, models, key and plan apply to a user.
/// </summary>
public sealed class AiConfigResolver(
    ISettingsRepository settingsRepo,
    IUserAccountRepository accountsRepo,
    PlanResolver planResolver,
    ApiKeyProtector protector,
    IConfiguration configuration)
{
    public async Task<ResolvedAiConfig> ResolveAsync(string userId, CancellationToken cancellationToken = default)
    {
        var account = await accountsRepo.GetAsync(userId, cancellationToken);
        return await ResolveCoreAsync(userId, account, cancellationToken);
    }

    public Task<ResolvedAiConfig> ResolveAsync(UserAccount account, CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(account.Id, account, cancellationToken);

    private async Task<ResolvedAiConfig> ResolveCoreAsync(string userId, UserAccount? account, CancellationToken cancellationToken)
    {
        var settings = await settingsRepo.GetOrCreateAsync(userId, cancellationToken);

        var storedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (prov, enc) in settings.EncryptedApiKeys)
        {
            if (string.IsNullOrEmpty(enc))
            {
                continue;
            }

            var dec = protector.Decrypt(enc);
            if (!string.IsNullOrEmpty(dec))
            {
                storedKeys[prov] = dec;
            }
        }

        var platformKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in AiProviders.Ids)
        {
            var val = configuration[AiProviders.PlatformKeySetting(id)]?.Trim();
            if (!string.IsNullOrEmpty(val))
            {
                platformKeys[id] = val;
            }
        }

        var plan = account is not null
            ? await planResolver.GetPlanAsync(account, cancellationToken)
            : Plans.Free;

        var preferred = AiProviders.IsKnown(settings.ActiveProvider) ? settings.ActiveProvider : AiProviders.OpenRouter;
        var provider = preferred;
        var keySource = "none";
        string? apiKey = null;
        var honourModelChoice = true;

        var ownFallback = AiProviders.Ids.FirstOrDefault(storedKeys.ContainsKey);

        if (storedKeys.TryGetValue(preferred, out var preferredUserKey))
        {
            // 1. The user's own key for their chosen provider.
            apiKey = preferredUserKey;
            keySource = "user";
        }
        else if (ownFallback is not null)
        {
            // 2. Any own key beats spending the platform's budget.
            provider = ownFallback;
            apiKey = storedKeys[ownFallback];
            keySource = "user";
        }
        else
        {
            // 3. Platform keys, metered by the plan's monthly quota.
            var platformDefault = AiProviders.Ids.FirstOrDefault(platformKeys.ContainsKey);
            honourModelChoice = Plans.Limits[plan].CanChooseModels;
            var platformProvider = honourModelChoice && platformKeys.ContainsKey(preferred)
                ? preferred
                : platformDefault;
            if (platformProvider is not null)
            {
                provider = platformProvider;
                apiKey = platformKeys[platformProvider];
                keySource = "platform";
            }
        }

        if (provider != preferred)
        {
            honourModelChoice = false;
        }

        var defaults = AiProviders.Defaults.TryGetValue(provider, out var d) ? d : AiProviders.Defaults[AiProviders.OpenRouter];

        var graderModel = AiProviders.ModelFor(provider, honourModelChoice ? settings.GraderModel : null, defaults.GraderModel);
        var fastModel = AiProviders.ModelFor(provider, honourModelChoice ? settings.FastModel : null, defaults.FastModel);
        var visionModel = AiProviders.ModelFor(provider, honourModelChoice ? settings.VisionModel : null, defaults.VisionModel);

        return new ResolvedAiConfig(
            UserId: userId,
            Provider: provider,
            GraderModel: graderModel,
            FastModel: fastModel,
            VisionModel: visionModel,
            ApiKey: apiKey,
            KeySource: keySource,
            Plan: plan,
            CurrentBand: settings.CurrentBand,
            TargetBand: settings.TargetBand,
            NativeLanguage: string.IsNullOrEmpty(settings.NativeLanguage) ? "es" : settings.NativeLanguage,
            L1CoachingEnabled: settings.L1CoachingEnabled);
    }
}
