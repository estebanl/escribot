using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Escribot.Api.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Escribot.Api.Tests;

/// <summary>
/// The roles Settings shows always match what AI requests use: once a user has a key of
/// their own, providers only the platform covers are not usable, and roles move off them.
/// </summary>
public class ProviderRoleTests
{
    private static ApiFactory CreateFactory(bool platformOpenRouter) => new()
    {
        Settings = new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = platformOpenRouter ? "or-platform" : null,
        },
    };

    private static async Task<(HttpClient Browser, string UserId)> SignUpAsync(ApiFactory factory)
    {
        var browser = factory.CreateBrowser();
        using var res = await AuthEndpointTests.SignUpAsync(browser, $"roles-{Guid.NewGuid():N}@example.test", $"pw-{TestSecrets.NewSecret()}", "Roles");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (browser, (await AuthEndpointTests.Json(res)).GetProperty("user").GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> ProviderAsync(HttpClient browser, string id)
    {
        using var res = await browser.GetAsync("/api/settings");
        var json = await AuthEndpointTests.Json(res);
        return json.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task The_first_provider_a_user_adds_takes_every_role()
    {
        await using var factory = CreateFactory(platformOpenRouter: false);
        var (browser, userId) = await SignUpAsync(factory);

        using var added = await browser.PutAsJsonAsync("/api/settings/providers/deepseek", new { apiKey = $"sk-{TestSecrets.NewSecret()}" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var stored = await factory.Services.GetRequiredService<ISettingsRepository>().GetOrCreateAsync(userId);
        Assert.Equal(("deepseek", "deepseek-v4-pro"), (stored.GraderProvider, stored.GraderModel));
        Assert.Equal(("deepseek", "deepseek-flash"), (stored.FastProvider, stored.FastModel));
        Assert.Equal(("deepseek", "deepseek-flash"), (stored.VisionProvider, stored.VisionModel));
    }

    [Fact]
    public async Task A_platform_only_provider_stops_being_usable_once_the_user_has_a_key_and_returns_when_it_goes()
    {
        await using var factory = CreateFactory(platformOpenRouter: true);
        var (browser, userId) = await SignUpAsync(factory);
        var repo = factory.Services.GetRequiredService<ISettingsRepository>();

        Assert.True((await ProviderAsync(browser, "openrouter")).GetProperty("configured").GetBoolean());

        // An own key takes over: OpenRouter, covered only by the platform, is no longer usable,
        // and the roles on it move to the new provider.
        using var added = await browser.PutAsJsonAsync("/api/settings/providers/anthropic", new { apiKey = $"sk-{TestSecrets.NewSecret()}" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var openRouter = await ProviderAsync(browser, "openrouter");
        Assert.False(openRouter.GetProperty("configured").GetBoolean());
        Assert.True(openRouter.GetProperty("hasEnvKey").GetBoolean());
        var stored = await repo.GetOrCreateAsync(userId);
        Assert.All(new[] { stored.GraderProvider, stored.FastProvider, stored.VisionProvider }, p => Assert.Equal("anthropic", p));

        // ...so adding it now needs the user's own key.
        using var noKey = await browser.PutAsJsonAsync("/api/settings/providers/openrouter", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        // Removing the only own key brings the platform's provider back, and the roles with it.
        using var removed = await browser.DeleteAsync("/api/settings/providers/anthropic");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.True((await ProviderAsync(browser, "openrouter")).GetProperty("configured").GetBoolean());
        stored = await repo.GetOrCreateAsync(userId);
        Assert.Equal(("openrouter", "anthropic/claude-sonnet-5.5"), (stored.GraderProvider, stored.GraderModel));
        Assert.Equal(("openrouter", "deepseek/deepseek-v4.1-flash-1017"), (stored.FastProvider, stored.FastModel));
    }

    [Fact]
    public async Task Removing_a_key_the_platform_also_covers_keeps_the_provider_but_drops_custom_models()
    {
        await using var factory = CreateFactory(platformOpenRouter: true);
        var (browser, userId) = await SignUpAsync(factory);
        var repo = factory.Services.GetRequiredService<ISettingsRepository>();

        using var added = await browser.PutAsJsonAsync("/api/settings/providers/openrouter", new
        {
            apiKey = $"sk-{TestSecrets.NewSecret()}",
            models = new[] { "acme/custom-model" },
        });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var stored = await repo.GetOrCreateAsync(userId);
        Assert.Equal(("openrouter", "acme/custom-model"), (stored.GraderProvider, stored.GraderModel));

        using var removed = await browser.DeleteAsync("/api/settings/providers/openrouter");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // Still on OpenRouter (the platform's key), but back on a model it lists.
        stored = await repo.GetOrCreateAsync(userId);
        Assert.Empty(stored.EncryptedApiKeys);
        Assert.Equal(("openrouter", "anthropic/claude-sonnet-5.5"), (stored.GraderProvider, stored.GraderModel));
        Assert.Equal(("openrouter", "deepseek/deepseek-v4.1-flash-1017"), (stored.VisionProvider, stored.VisionModel));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/model")]
    [InlineData("vendor/")]
    public async Task OpenRouter_model_names_need_something_on_both_sides_of_the_slash(string model)
    {
        await using var factory = CreateFactory(platformOpenRouter: false);
        var (browser, _) = await SignUpAsync(factory);

        using var res = await browser.PutAsJsonAsync("/api/settings/providers/openrouter", new { apiKey = "sk", models = new[] { model } });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
