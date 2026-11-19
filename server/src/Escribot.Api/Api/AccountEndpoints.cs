using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Escribot.Api.Auth;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Api;

/// <summary>Settings, images, the full data export and browser error reports.</summary>
internal static class AccountEndpoints
{
    private const int MaxApiKeyLength = 500;
    private const int MaxModelNameLength = 200;

    private static readonly string[] NativeLanguages = ["es", "pt", "fr", "zh", "ar", "hi", "neutral"];

    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/settings", GetSettingsAsync).OnFailure("Failed to load settings.");
        api.MapPut("/settings", SaveSettingsAsync).OnFailure("Failed to save settings.");
        api.MapPut("/settings/providers/{id}", SaveProviderAsync).OnFailure("Failed to save the provider.");
        api.MapDelete("/settings/providers/{id}", RemoveProviderAsync).OnFailure("Failed to remove the provider.");
        api.MapGet("/images/{id}", GetImageAsync);
        api.MapGet("/account/export", ExportAccountAsync);
        api.MapPost("/errors", ReportClientErrorAsync);
        return api;
    }

    // ---- Settings ----

    private static readonly (string Name, ModelRole Role)[] Roles =
        [("grader", ModelRole.Grader), ("fast", ModelRole.Fast), ("vision", ModelRole.Vision)];

    private static async Task<IResult> GetSettingsAsync(
        HttpContext context,
        ISettingsRepository settings,
        ISubscriptionRepository subscriptions,
        ApiKeyProtector protector,
        UsageService usage,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var stored = await settings.GetOrCreateAsync(account.Id, cancellationToken);
        var subscription = await subscriptions.GetAsync(account.Id, cancellationToken);

        var models = new JsonObject();
        foreach (var (name, role) in Roles)
        {
            var (provider, model) = AiProviders.Chosen(stored, role);
            models[name] = new JsonObject { ["provider"] = provider, ["model"] = model };
        }

        return Results.Json(new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["currentBand"] = stored.CurrentBand,
                ["targetBand"] = stored.TargetBand,
                ["examDate"] = stored.ExamDate ?? string.Empty,
                ["nativeLanguage"] = stored.NativeLanguage,
                ["l1CoachingEnabled"] = stored.L1CoachingEnabled,
                ["models"] = models,
            },
            ["providers"] = new JsonArray([.. AiProviders.Ids.Select(id => (JsonNode)ProviderJson(stored, id, protector, usage))]),
            ["usage"] = JsonSerializer.SerializeToNode(await usage.GetSummaryAsync(account, stored, cancellationToken), JsonSerializerOptions.Web),
            ["billing"] = new JsonObject
            {
                ["enabled"] = Plans.IsBillingEnabled(configuration),
                ["hasSubscription"] = !string.IsNullOrEmpty(subscription?.StripeSubscriptionId),
                ["status"] = subscription?.Status,
                ["currentPeriodEnd"] = subscription?.CurrentPeriodEnd is { } end ? Wire.Time(end) : null,
                ["cancelAtPeriodEnd"] = subscription?.CancelAtPeriodEnd ?? false,
            },
        });
    }

    /// <summary>
    /// One provider as the Settings dialog shows it. A saved key is never returned, only
    /// whether there is one and how it starts and ends.
    /// </summary>
    private static JsonObject ProviderJson(UserSettings stored, string id, ApiKeyProtector protector, UsageService usage)
    {
        var defaults = AiProviders.Defaults[id];
        var savedKey = stored.EncryptedApiKeys.TryGetValue(id, out var encrypted) ? protector.Decrypt(encrypted) : null;
        var hasPlatformKey = usage.HasPlatformKey(id);
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = defaults.Name,
            ["description"] = defaults.Description,
            ["configured"] = savedKey is not null || hasPlatformKey,
            ["hasStoredKey"] = savedKey is not null,
            ["maskedStoredKey"] = savedKey is null ? null : ApiKeyProtector.Mask(savedKey),
            ["hasEnvKey"] = hasPlatformKey,
            ["models"] = new JsonArray([.. ModelsOf(stored, id).Select(m => (JsonNode)m)]),
            ["suggestedModels"] = new JsonArray([.. defaults.SuggestedModels.Select(m => (JsonNode)m)]),
        };
    }

    /// <summary>The models listed for a provider; its suggested ones until the user lists their own.</summary>
    private static IReadOnlyList<string> ModelsOf(UserSettings stored, string id) =>
        stored.ProviderModels.TryGetValue(id, out var listed) && listed.Where(m => AiProviders.IsValidModel(id, m)).ToList() is { Count: > 0 } valid
            ? valid
            : AiProviders.Defaults[id].SuggestedModels;

    private static async Task<IResult> SaveSettingsAsync(
        JsonElement json,
        HttpContext context,
        ISettingsRepository settings,
        ApiKeyProtector protector,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var userId = context.GetSession()!.Account.Id;
        var body = new Body(json);

        var currentBand = body.Number("currentBand");
        var targetBand = body.Number("targetBand");
        var nativeLanguage = body.Trimmed("nativeLanguage");
        var examDate = body.String("examDate")?.Trim();
        var keyUpdates = body.Object("apiKeysToUpdate")?.Properties().ToList() ?? [];

        // The model each role uses: { grader: { provider, model }, fast: {...}, vision: {...} }.
        var roleChoices = new Dictionary<ModelRole, (string Provider, string Model)>();
        var rolesValid = true;
        if (body.Object("models") is { } modelsBody)
        {
            foreach (var (name, role) in Roles)
            {
                if (!modelsBody.Has(name))
                {
                    continue;
                }

                var choice = modelsBody.Object(name);
                var provider = choice?.Trimmed("provider");
                var model = choice?.Trimmed("model");
                if (provider is null || !AiProviders.IsKnown(provider) || model is null
                    || model.Length > MaxModelNameLength || !AiProviders.IsValidModel(provider, model))
                {
                    rolesValid = false;
                    break;
                }

                roleChoices[role] = (provider, model);
            }
        }

        // Older clients send one provider and three model names for it.
        var legacyProvider = body.Trimmed("activeProvider");
        var legacyModels = new[] { body.Trimmed("graderModel"), body.Trimmed("fastModel"), body.Trimmed("visionModel") };
        var legacyChoice = legacyProvider is not null || legacyModels.Any(m => m is not null);

        if (currentBand is < 0 or > 9 || targetBand is < 0 or > 9
            || (nativeLanguage is not null && !NativeLanguages.Contains(nativeLanguage))
            || (legacyProvider is not null && !AiProviders.IsKnown(legacyProvider))
            || examDate?.Length > 40
            || !rolesValid
            || legacyModels.Any(m => m?.Length > MaxModelNameLength)
            || keyUpdates.Any(k => !AiProviders.IsKnown(k.Name) || (k.Value.ValueKind == JsonValueKind.String && k.Value.GetString()!.Length > MaxApiKeyLength)))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "These settings are not valid.");
        }

        await settings.UpdateAsync(userId, existing =>
        {
            var keys = ReadableKeys(existing, protector);
            foreach (var update in keyUpdates)
            {
                var value = update.Value.ValueKind == JsonValueKind.String ? update.Value.GetString()!.Trim() : null;
                if (update.Value.ValueKind == JsonValueKind.Null || value == "__CLEAR__")
                {
                    keys.Remove(update.Name);
                }
                else if (!string.IsNullOrEmpty(value))
                {
                    keys[update.Name] = protector.Encrypt(value);
                }
            }

            var next = existing with
            {
                CurrentBand = currentBand ?? existing.CurrentBand,
                TargetBand = targetBand ?? existing.TargetBand,
                ExamDate = body.Has("examDate") ? (string.IsNullOrEmpty(examDate) ? null : examDate) : existing.ExamDate,
                NativeLanguage = nativeLanguage ?? existing.NativeLanguage,
                L1CoachingEnabled = body.Boolean("l1CoachingEnabled") ?? existing.L1CoachingEnabled,
                EncryptedApiKeys = keys,
                OnboardedAt = existing.OnboardedAt ?? (body.Boolean("completeOnboarding") == true ? time.GetUtcNow() : null),
            };

            if (roleChoices.Count == 0 && legacyChoice)
            {
                // As before, a model left out of a legacy request goes back to the provider's default.
                var activeProvider = legacyProvider ?? (AiProviders.IsKnown(existing.ActiveProvider) ? existing.ActiveProvider : AiProviders.OpenRouter);
                var defaults = AiProviders.Defaults[activeProvider];
                next = next with
                {
                    ActiveProvider = activeProvider,
                    GraderProvider = null,
                    FastProvider = null,
                    VisionProvider = null,
                    GraderModel = AiProviders.ModelFor(activeProvider, legacyModels[0], defaults.GraderModel),
                    FastModel = AiProviders.ModelFor(activeProvider, legacyModels[1], defaults.FastModel),
                    VisionModel = AiProviders.ModelFor(activeProvider, legacyModels[2], defaults.VisionModel),
                };
            }

            foreach (var (role, (provider, model)) in roleChoices)
            {
                next = WithRole(next, role, provider, model);
            }

            return next;
        }, cancellationToken);

        return Results.Json(new JsonObject { ["ok"] = true });
    }

    /// <summary>
    /// Adds a provider or changes its key or model list. Adding one needs a key, unless the
    /// platform has its own key for it; a provider added without a model list starts with
    /// its suggested models.
    /// </summary>
    private static async Task<IResult> SaveProviderAsync(
        string id,
        JsonElement json,
        HttpContext context,
        ISettingsRepository settings,
        ApiKeyProtector protector,
        UsageService usage,
        CancellationToken cancellationToken)
    {
        if (!AiProviders.IsKnown(id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "Provider not found.");
        }

        var userId = context.GetSession()!.Account.Id;
        var body = new Body(json);
        var apiKey = body.Trimmed("apiKey");
        var models = body.Strings("models");

        if ((body.Has("apiKey") && (apiKey is null || apiKey.Length > MaxApiKeyLength))
            || (body.Has("models") && (models is null || models.Count == 0 || models.Count > AiProviders.MaxModelsPerProvider
                || models.Any(m => m is null || m.Length > MaxModelNameLength || !AiProviders.IsValidModel(id, m)))))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "These provider settings are not valid.");
        }

        var hasPlatformKey = usage.HasPlatformKey(id);
        var missingKey = false;
        var updated = await settings.UpdateAsync(userId, existing =>
        {
            var keys = ReadableKeys(existing, protector);
            if (apiKey is not null)
            {
                keys[id] = protector.Encrypt(apiKey);
            }

            missingKey = !keys.ContainsKey(id) && !hasPlatformKey;
            if (missingKey)
            {
                return existing;
            }

            var listed = new Dictionary<string, IReadOnlyList<string>>(existing.ProviderModels);
            if (models is not null)
            {
                listed[id] = [.. models.Select(m => m!).Distinct(StringComparer.Ordinal)];
            }
            else if (!listed.ContainsKey(id))
            {
                listed[id] = AiProviders.Defaults[id].SuggestedModels;
            }

            var next = existing with { EncryptedApiKeys = keys, ProviderModels = listed };

            // A role whose model was just taken off this provider's list moves to the
            // provider's first remaining model.
            foreach (var (_, role) in Roles)
            {
                var (provider, model) = AiProviders.Chosen(next, role);
                if (provider == id && !listed[id].Contains(model))
                {
                    next = WithRole(next, role, id, listed[id][0]);
                }
            }

            return next;
        }, cancellationToken);

        if (missingKey)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Add an API key for this provider.");
        }

        return Results.Json(ProviderJson(updated, id, protector, usage));
    }

    /// <summary>
    /// Removes a provider's saved key and model list. Roles that used it move to another
    /// provider the user still has, or fall back when AI requests are resolved.
    /// </summary>
    private static async Task<IResult> RemoveProviderAsync(
        string id,
        HttpContext context,
        ISettingsRepository settings,
        ApiKeyProtector protector,
        UsageService usage,
        CancellationToken cancellationToken)
    {
        if (!AiProviders.IsKnown(id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "Provider not found.");
        }

        var userId = context.GetSession()!.Account.Id;
        await settings.UpdateAsync(userId, existing =>
        {
            var keys = ReadableKeys(existing, protector);
            keys.Remove(id);
            var listed = new Dictionary<string, IReadOnlyList<string>>(existing.ProviderModels);
            listed.Remove(id);
            var next = existing with { EncryptedApiKeys = keys, ProviderModels = listed };

            // Prefer a provider with the user's own key, then one the platform covers.
            var replacement = AiProviders.Ids.FirstOrDefault(p => p != id && keys.ContainsKey(p))
                ?? AiProviders.Ids.FirstOrDefault(p => p != id && usage.HasPlatformKey(p));
            if (replacement is not null)
            {
                foreach (var (_, role) in Roles)
                {
                    if (AiProviders.Chosen(next, role).Provider == id)
                    {
                        // The replacement's default for the role if the user still lists it, else its first listed model.
                        var listedModels = ModelsOf(next, replacement);
                        var preferred = AiProviders.DefaultModel(replacement, role);
                        next = WithRole(next, role, replacement, listedModels.Contains(preferred) ? preferred : listedModels[0]);
                    }
                }
            }

            return next;
        }, cancellationToken);

        return Results.NoContent();
    }

    /// <summary>
    /// The saved keys this server can still read. Keys saved under another secret are
    /// dropped rather than kept as text that looks like a saved key but can never be used.
    /// </summary>
    private static Dictionary<string, string> ReadableKeys(UserSettings settings, ApiKeyProtector protector) =>
        settings.EncryptedApiKeys
            .Where(k => AiProviders.IsKnown(k.Key) && protector.Decrypt(k.Value) is not null)
            .ToDictionary(k => k.Key, k => k.Value);

    private static UserSettings WithRole(UserSettings settings, ModelRole role, string provider, string model) => role switch
    {
        ModelRole.Grader => settings with { GraderProvider = provider, GraderModel = model },
        ModelRole.Fast => settings with { FastProvider = provider, FastModel = model },
        _ => settings with { VisionProvider = provider, VisionModel = model },
    };

    // ---- Images ----

    /// <summary>Sends an uploaded image to its owner only.</summary>
    private static async Task<IResult> GetImageAsync(string id, HttpContext context, ImageStore images, CancellationToken cancellationToken)
    {
        var image = await images.GetOwnedAsync(context.GetSession()!.Account.Id, id, cancellationToken);
        if (image is not { } found)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "Image not found.");
        }

        // Private to this browser. Ids never change what they point to, so caching is safe.
        context.Response.Headers.CacheControl = "private, max-age=86400, immutable";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(found.Data, found.ContentType);
    }

    // ---- Export of everything ----

    /// <summary>
    /// Everything stored about the signed-in user as one download (GDPR access and
    /// portability). Saved API keys are listed by provider but never exported, even encrypted.
    /// </summary>
    private static async Task<IResult> ExportAccountAsync(
        HttpContext context,
        IUserDataRepository userData,
        IRateLimiter limiter,
        ImageStore images,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var userId = account.Id;

        var rate = await limiter.CheckAsync($"export:{userId}", 5, TimeSpan.FromHours(1), cancellationToken);
        if (!rate.Allowed)
        {
            context.Response.Headers.RetryAfter = rate.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return ApiResults.Error(StatusCodes.Status429TooManyRequests, "Too many exports. Please try again later.");
        }

        var export = await userData.ExportAsync(userId, cancellationToken);
        var now = time.GetUtcNow();

        var signInMethods = new JsonArray();
        if (account.PasswordHash is not null)
        {
            signInMethods.Add(new JsonObject { ["provider"] = "credential", ["createdAt"] = Wire.Time(account.CreatedAt) });
        }

        foreach (var login in account.ExternalLogins)
        {
            signInMethods.Add(new JsonObject { ["provider"] = login.Provider, ["createdAt"] = Wire.Time(login.CreatedAt) });
        }

        var payload = new JsonObject
        {
            ["exportedAt"] = Wire.Time(now),
            ["app"] = "Escribot",
            ["account"] = new JsonObject
            {
                ["name"] = account.Name,
                ["email"] = account.Email,
                ["emailVerified"] = account.EmailVerified,
                ["createdAt"] = Wire.Time(account.CreatedAt),
                ["signInMethods"] = signInMethods,
            },
            ["settings"] = export.Settings is not { } s ? null : new JsonObject
            {
                ["currentBand"] = s.CurrentBand,
                ["targetBand"] = s.TargetBand,
                ["examDate"] = s.ExamDate,
                ["nativeLanguage"] = s.NativeLanguage,
                ["l1CoachingEnabled"] = s.L1CoachingEnabled,
                ["activeProvider"] = s.ActiveProvider,
                ["graderProvider"] = s.GraderProvider,
                ["graderModel"] = s.GraderModel,
                ["fastProvider"] = s.FastProvider,
                ["fastModel"] = s.FastModel,
                ["visionProvider"] = s.VisionProvider,
                ["visionModel"] = s.VisionModel,
                ["providerModels"] = JsonSerializer.SerializeToNode(s.ProviderModels),
                ["plan"] = s.Plan,
                ["onboardedAt"] = s.OnboardedAt is { } onboarded ? Wire.Time(onboarded) : null,
                ["updatedAt"] = Wire.Time(s.UpdatedAt),
                ["savedApiKeyProviders"] = new JsonArray([.. export.SavedApiKeyProviders.Select(p => (JsonNode)p)]),
            },
            ["subscription"] = export.Subscription is not { } subscription ? null : new JsonObject
            {
                ["status"] = subscription.Status,
                ["currentPeriodEnd"] = subscription.CurrentPeriodEnd is { } end ? Wire.Time(end) : null,
                ["cancelAtPeriodEnd"] = subscription.CancelAtPeriodEnd,
            },
            ["data"] = await BackupEndpoints.DataAsync(export, userId, images, cancellationToken, includeAiUsage: true),
        };

        context.Response.Headers.ContentDisposition = $"attachment; filename=\"escribot-my-data-{Wire.Time(now)[..10]}.json\"";
        context.Response.Headers.CacheControl = "no-store";
        return Results.Text(payload.ToJsonString(Indented), "application/json; charset=utf-8");
    }

    // ---- Browser error reports ----

    /// <summary>Receives errors caught by the browser's error boundaries, also from signed-out pages.</summary>
    private static async Task<IResult> ReportClientErrorAsync(HttpContext context, IRateLimiter limiter, ErrorReporter reporter, CancellationToken cancellationToken)
    {
        var userId = context.GetSession()?.Account.Id;
        var reporterKey = userId ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Over the limit the report is dropped quietly: the page that sent it has nothing to do about it.
        var rate = await limiter.CheckAsync($"client-error:{reporterKey}", 10, TimeSpan.FromMinutes(1), cancellationToken);
        if (!rate.Allowed)
        {
            return Results.NoContent();
        }

        Body body;
        try
        {
            body = new Body(await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Invalid report.");
        }

        if (body.String("message") is not { } message)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Invalid report.");
        }

        await reporter.ReportClientErrorAsync(message, body.String("stack"), body.String("path"), body.String("digest"), userId);
        return Results.NoContent();
    }
}
