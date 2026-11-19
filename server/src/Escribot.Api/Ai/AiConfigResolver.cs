using Escribot.Api.Api;
using Escribot.Api.Auth;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Ai;

/// <summary>
/// Resolves which provider, model and key each AI role uses for a user, and their plan.
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
        var canChooseModels = Plans.Limits[plan].CanChooseModels;

        // Any own key beats spending the platform's budget.
        var ownFallback = AiProviders.Ids.FirstOrDefault(storedKeys.ContainsKey);
        var platformDefault = AiProviders.Ids.FirstOrDefault(platformKeys.ContainsKey);

        ModelTarget Resolve(ModelRole role)
        {
            var (preferred, chosenModel) = AiProviders.Chosen(settings, role);

            // 1. The user's own key for the provider picked for this role.
            if (storedKeys.TryGetValue(preferred, out var ownKey))
            {
                return new ModelTarget(preferred, chosenModel, ownKey, "user");
            }

            // 2. Another provider the user has a key for, with its default model for the role.
            // Once a user has a key of their own, platform keys are never used, even for a
            // provider only the platform covers. Settings shows such providers as not
            // configured and moves roles off them when providers change, so this step is
            // only a safety net for settings saved before that or through PUT /api/settings.
            if (ownFallback is not null)
            {
                return new ModelTarget(ownFallback, AiProviders.DefaultModel(ownFallback, role), storedKeys[ownFallback], "user");
            }

            // 3. Platform keys, metered by the plan's monthly quota. Only plans that may
            // choose models get the picked provider and model.
            var platformProvider = canChooseModels && platformKeys.ContainsKey(preferred) ? preferred : platformDefault;
            if (platformProvider is null)
            {
                return new ModelTarget(preferred, chosenModel, null, "none");
            }

            var model = platformProvider == preferred && canChooseModels
                ? chosenModel
                : AiProviders.DefaultModel(platformProvider, role);
            return new ModelTarget(platformProvider, model, platformKeys[platformProvider], "platform");
        }

        return new ResolvedAiConfig(
            UserId: userId,
            Grader: Resolve(ModelRole.Grader),
            Fast: Resolve(ModelRole.Fast),
            Vision: Resolve(ModelRole.Vision),
            Plan: plan,
            CurrentBand: settings.CurrentBand,
            TargetBand: settings.TargetBand,
            NativeLanguage: string.IsNullOrEmpty(settings.NativeLanguage) ? "es" : settings.NativeLanguage,
            L1CoachingEnabled: settings.L1CoachingEnabled);
    }
}
