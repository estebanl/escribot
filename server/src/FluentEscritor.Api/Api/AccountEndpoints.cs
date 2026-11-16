using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Common;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Api;

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
        api.MapGet("/images/{id}", GetImageAsync);
        api.MapGet("/account/export", ExportAccountAsync);
        api.MapPost("/errors", ReportClientErrorAsync);
        return api;
    }

    // ---- Settings ----

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

        var keyStatus = new JsonObject();
        var ready = new Dictionary<string, bool>();
        foreach (var provider in AiProviders.Ids)
        {
            // A saved key is never returned, only whether there is one and how it starts and ends.
            var savedKey = stored.EncryptedApiKeys.TryGetValue(provider, out var encrypted) ? protector.Decrypt(encrypted) : null;
            var hasPlatformKey = usage.HasPlatformKey(provider);
            keyStatus[provider] = new JsonObject
            {
                ["hasStoredKey"] = savedKey is not null,
                ["maskedStoredKey"] = savedKey is null ? null : ApiKeyProtector.Mask(savedKey),
                ["hasEnvKey"] = hasPlatformKey,
            };
            ready[provider] = savedKey is not null || hasPlatformKey;
        }

        // A provider with no key at all falls back to OpenRouter when that one has a key.
        var activeProvider = AiProviders.IsKnown(stored.ActiveProvider) ? stored.ActiveProvider : AiProviders.OpenRouter;
        if (!ready[activeProvider] && ready[AiProviders.OpenRouter])
        {
            activeProvider = AiProviders.OpenRouter;
        }

        var defaults = AiProviders.Defaults[activeProvider];
        var subscription = await subscriptions.GetAsync(account.Id, cancellationToken);

        return Results.Json(new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["currentBand"] = stored.CurrentBand,
                ["targetBand"] = stored.TargetBand,
                ["examDate"] = stored.ExamDate ?? string.Empty,
                ["nativeLanguage"] = stored.NativeLanguage,
                ["l1CoachingEnabled"] = stored.L1CoachingEnabled,
                ["activeProvider"] = activeProvider,
                ["graderModel"] = AiProviders.ModelFor(activeProvider, stored.GraderModel, defaults.GraderModel),
                ["fastModel"] = AiProviders.ModelFor(activeProvider, stored.FastModel, defaults.FastModel),
                ["visionModel"] = AiProviders.ModelFor(activeProvider, stored.VisionModel, defaults.VisionModel),
            },
            ["keyStatus"] = keyStatus,
            ["activeProviderReady"] = ready[activeProvider],
            ["providerDefaults"] = JsonSerializer.SerializeToNode(AiProviders.Defaults, JsonSerializerOptions.Web),
            ["usage"] = JsonSerializer.SerializeToNode(await usage.GetSummaryAsync(account, stored, cancellationToken), JsonSerializerOptions.Web),
            ["billing"] = new JsonObject
            {
                ["enabled"] = IsBillingEnabled(configuration),
                ["hasSubscription"] = !string.IsNullOrEmpty(subscription?.StripeSubscriptionId),
                ["status"] = subscription?.Status,
                ["currentPeriodEnd"] = subscription?.CurrentPeriodEnd is { } end ? Wire.Time(end) : null,
                ["cancelAtPeriodEnd"] = subscription?.CancelAtPeriodEnd ?? false,
            },
        });
    }

    /// <summary>
    /// Paid plans need the Stripe key, the monthly Pro price and the webhook secret.
    /// Without the webhook, checkout would take payment but never record the subscription.
    /// </summary>
    private static bool IsBillingEnabled(IConfiguration configuration) =>
        new[] { "STRIPE_SECRET_KEY", "STRIPE_PRICE_PRO_MONTHLY", "STRIPE_WEBHOOK_SECRET" }.All(name => !string.IsNullOrWhiteSpace(configuration[name]));

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
        var requestedProvider = body.Trimmed("activeProvider");
        var examDate = body.String("examDate")?.Trim();
        var models = new[] { body.Trimmed("graderModel"), body.Trimmed("fastModel"), body.Trimmed("visionModel") };
        var keyUpdates = body.Object("apiKeysToUpdate")?.Properties().ToList() ?? [];

        if (currentBand is < 0 or > 9 || targetBand is < 0 or > 9
            || (nativeLanguage is not null && !NativeLanguages.Contains(nativeLanguage))
            || (requestedProvider is not null && !AiProviders.IsKnown(requestedProvider))
            || examDate?.Length > 40
            || models.Any(m => m?.Length > MaxModelNameLength)
            || keyUpdates.Any(k => !AiProviders.IsKnown(k.Name) || (k.Value.ValueKind == JsonValueKind.String && k.Value.GetString()!.Length > MaxApiKeyLength)))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "These settings are not valid.");
        }

        await settings.UpdateAsync(userId, existing =>
        {
            // Keys this server cannot read (saved under another secret) are dropped rather
            // than kept as text that looks like a saved key but can never be used.
            var keys = existing.EncryptedApiKeys
                .Where(k => AiProviders.IsKnown(k.Key) && protector.Decrypt(k.Value) is not null)
                .ToDictionary(k => k.Key, k => k.Value);

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

            var activeProvider = requestedProvider ?? (AiProviders.IsKnown(existing.ActiveProvider) ? existing.ActiveProvider : AiProviders.OpenRouter);
            var defaults = AiProviders.Defaults[activeProvider];

            return existing with
            {
                CurrentBand = currentBand ?? existing.CurrentBand,
                TargetBand = targetBand ?? existing.TargetBand,
                ExamDate = body.Has("examDate") ? (string.IsNullOrEmpty(examDate) ? null : examDate) : existing.ExamDate,
                NativeLanguage = nativeLanguage ?? existing.NativeLanguage,
                L1CoachingEnabled = body.Boolean("l1CoachingEnabled") ?? existing.L1CoachingEnabled,
                ActiveProvider = activeProvider,
                // As before, a model left out of the request goes back to the provider's default.
                GraderModel = AiProviders.ModelFor(activeProvider, models[0], defaults.GraderModel),
                FastModel = AiProviders.ModelFor(activeProvider, models[1], defaults.FastModel),
                VisionModel = AiProviders.ModelFor(activeProvider, models[2], defaults.VisionModel),
                EncryptedApiKeys = keys,
                OnboardedAt = existing.OnboardedAt ?? (body.Boolean("completeOnboarding") == true ? time.GetUtcNow() : null),
            };
        }, cancellationToken);

        return Results.Json(new JsonObject { ["ok"] = true });
    }

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
            ["app"] = "FluentEscritor",
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
                ["graderModel"] = s.GraderModel,
                ["fastModel"] = s.FastModel,
                ["visionModel"] = s.VisionModel,
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

        context.Response.Headers.ContentDisposition = $"attachment; filename=\"fluentescritor-my-data-{Wire.Time(now)[..10]}.json\"";
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
