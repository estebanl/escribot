using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Escribot.Api.Ai;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Escribot.Api.Tests;

/// <summary>Each provider's live model list: what is kept, its order, and how keys are sent.</summary>
public class ProviderModelsTests
{
    private const string OpenAiPayload = """
        {"data":[
          {"id":"gpt-6-luna","created":1790100786},
          {"id":"text-embedding-3-large","created":1795000000},
          {"id":"gpt-6.2-sol","created":1794938371},
          {"id":"gpt-6-tts","created":1795000001},
          {"id":"whisper-1","created":1},
          {"id":"o3","created":1744000000},
          {"id":"o3-2025-04-16","created":1744000001},
          {"id":"gpt-4o-2024-08-06","created":1722000000},
          {"id":"gpt-live-1","created":1795000002},
          {"id":"dall-e-3","created":2}
        ]}
        """;

    private const string GeminiPayload = """
        {"models":[
          {"name":"models/gemini-3.5-flash-lite","displayName":"Gemini 3.5 Flash-Lite","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-embedding-001","supportedGenerationMethods":["embedContent"]},
          {"name":"models/gemini-4-carbon","displayName":"Gemini 4 Carbon","supportedGenerationMethods":["generateContent","countTokens"]},
          {"name":"models/gemini-3.8-flash","displayName":"Gemini 3.8 Flash","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-3.1-flash-tts-preview","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-3.5-transcribe","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-robotics-er-2-preview","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemma-4-31b-it","supportedGenerationMethods":["generateContent"]}
        ]}
        """;

    private const string AnthropicPayload = """
        {"data":[
          {"id":"claude-opus-5-5","display_name":"Claude Opus 5.5","created_at":"2026-09-01T00:00:00Z"},
          {"id":"claude-sonnet-5-5","display_name":"Claude Sonnet 5.5","created_at":"2026-08-01T00:00:00Z"}
        ],"has_more":false}
        """;

    [Fact]
    public void OpenAi_keeps_chat_models_newest_first()
    {
        var models = ProviderModelsClient.ParseOpenAi(JsonNode.Parse(OpenAiPayload)).Select(m => m.Id);
        // o3's snapshot goes (its alias is listed); gpt-4o's stays (no alias).
        Assert.Equal(["gpt-6.2-sol", "gpt-6-luna", "o3", "gpt-4o-2024-08-06"], models);
    }

    [Fact]
    public void Gemini_keeps_text_models_by_version_without_the_models_prefix()
    {
        var models = ProviderModelsClient.ParseGemini(JsonNode.Parse(GeminiPayload)).ToList();
        Assert.Equal(["gemini-4-carbon", "gemini-3.8-flash", "gemini-3.5-flash-lite"], models.Select(m => m.Id));
        Assert.Equal("Gemini 4 Carbon", models[0].Name);
    }

    [Fact]
    public void Anthropic_keeps_its_own_order_and_display_names()
    {
        var models = ProviderModelsClient.ParseAnthropic(JsonNode.Parse(AnthropicPayload)).ToList();
        Assert.Equal([new ProviderModel("claude-opus-5-5", "Claude Opus 5.5"), new ProviderModel("claude-sonnet-5-5", "Claude Sonnet 5.5")], models);
    }

    [Theory]
    [InlineData("openai", "Authorization", "Bearer KEY")]
    [InlineData("deepseek", "Authorization", "Bearer KEY")]
    [InlineData("anthropic", "x-api-key", "KEY")]
    [InlineData("gemini", "x-goog-api-key", "KEY")]
    public async Task The_key_goes_in_a_header_never_in_the_url(string provider, string header, string expected)
    {
        var key = $"sk-{TestSecrets.NewSecret()}";
        var fake = new FakeProvider(HttpStatusCode.OK, provider == "gemini" ? GeminiPayload : AnthropicPayload);
        var client = NewClient(fake);

        await client.ListAsync(provider, key);

        var request = Assert.Single(fake.Requests);
        Assert.Equal(expected.Replace("KEY", key, StringComparison.Ordinal), request.Headers.GetValues(header).Single());
        Assert.DoesNotContain(key, request.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_list_is_cached_per_key_and_a_rejected_key_is_reported()
    {
        var fake = new FakeProvider(HttpStatusCode.OK, AnthropicPayload);
        var client = NewClient(fake);

        await client.ListAsync("anthropic", "key-a");
        await client.ListAsync("anthropic", "key-a");
        Assert.Single(fake.Requests);

        fake.Status = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAsync<ProviderKeyRejectedException>(() => client.ListAsync("anthropic", "key-b"));
    }

    // ---- The endpoint ----

    // OpenRouter's catalogue as its /models API answers it: prices are per token.
    private const string CatalogPayload = """
        {"data":[
          {"id":"openai/gpt-6.2-sol","name":"OpenAI: GPT-6.2 Sol","supported_parameters":["response_format"],"architecture":{"input_modalities":["text","image"]},"pricing":{"prompt":"0.000002","completion":"0.00001"}},
          {"id":"openai/gpt-6-luna","name":"OpenAI: GPT-6 Luna","supported_parameters":["response_format"],"architecture":{"input_modalities":["text","image"]},"pricing":{"prompt":"0.0000001","completion":"0.0000005"}},
          {"id":"openai/o3","name":"OpenAI: o3","supported_parameters":["response_format"],"architecture":{"input_modalities":["text","image"]},"pricing":{"prompt":"0.00002","completion":"0.00008"}}
        ]}
        """;

    private static ApiFactory CreateFactory(FakeProvider fake, string? platformOpenAiKey = null) => new()
    {
        Settings = new Dictionary<string, string?> { ["OPENAI_API_KEY"] = platformOpenAiKey },
        ConfigureServices = services =>
        {
            services.AddHttpClient<ProviderModelsClient>().ConfigurePrimaryHttpMessageHandler(() => fake);
            services.AddHttpClient<OpenRouterModelsClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeProvider(HttpStatusCode.OK, CatalogPayload));
        },
    };

    private static async Task<HttpClient> SignUpAsync(ApiFactory factory)
    {
        var browser = factory.CreateBrowser();
        using var res = await AuthEndpointTests.SignUpAsync(browser, $"models-{Guid.NewGuid():N}@example.test", $"pw-{TestSecrets.NewSecret()}", "Models");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return browser;
    }

    [Fact]
    public async Task Signed_out_is_401_an_unknown_provider_404_and_no_key_400()
    {
        var fake = new FakeProvider(HttpStatusCode.OK, OpenAiPayload);
        await using var factory = CreateFactory(fake);

        using var signedOut = await factory.CreateBrowser().PostAsJsonAsync("/api/settings/providers/openai/models", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);

        var browser = await SignUpAsync(factory);
        using var unknown = await browser.PostAsJsonAsync("/api/settings/providers/nope/models", new { });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var noKey = await browser.PostAsJsonAsync("/api/settings/providers/openai/models", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Lists_with_the_pasted_key_then_the_saved_one()
    {
        var fake = new FakeProvider(HttpStatusCode.OK, OpenAiPayload);
        await using var factory = CreateFactory(fake);
        var browser = await SignUpAsync(factory);
        var pasted = $"sk-{TestSecrets.NewSecret()}";

        using var listed = await browser.PostAsJsonAsync("/api/settings/providers/openai/models", new { apiKey = pasted });
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(
            """{"models":[{"id":"gpt-6.2-sol","name":"gpt-6.2-sol"},{"id":"gpt-6-luna","name":"gpt-6-luna"},{"id":"o3","name":"o3"},{"id":"gpt-4o-2024-08-06","name":"gpt-4o-2024-08-06"}],"starting":["gpt-6.2-sol","gpt-6-luna","o3","gpt-4o-2024-08-06"],"roles":{"grader":"gpt-6.2-sol","fast":"gpt-6-luna","vision":"gpt-6-luna"}}""",
            await listed.Content.ReadAsStringAsync());
        Assert.Equal($"Bearer {pasted}", fake.Requests[^1].Headers.Authorization!.ToString());

        var saved = $"sk-{TestSecrets.NewSecret()}";
        using var save = await browser.PutAsJsonAsync("/api/settings/providers/openai", new { apiKey = saved });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var again = await browser.PostAsJsonAsync("/api/settings/providers/openai/models", new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal($"Bearer {saved}", fake.Requests[^1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Saving_after_the_list_loads_starts_with_the_picked_models_and_roles()
    {
        var fake = new FakeProvider(HttpStatusCode.OK, OpenAiPayload);
        await using var factory = CreateFactory(fake);
        var browser = await SignUpAsync(factory);
        var key = $"sk-{TestSecrets.NewSecret()}";

        using var listed = await browser.PostAsJsonAsync("/api/settings/providers/openai/models", new { apiKey = key });
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var saved = await browser.PutAsJsonAsync("/api/settings/providers/openai", new { apiKey = key });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Saving reads the cached list: one call to OpenAI in all.
        Assert.Single(fake.Requests);
        var provider = await AuthEndpointTests.Json(saved);
        Assert.Equal(["gpt-6.2-sol", "gpt-6-luna", "o3", "gpt-4o-2024-08-06"], provider.GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        using var settings = await browser.GetAsync("/api/settings");
        Assert.Equal(
            """{"grader":{"provider":"openai","model":"gpt-6.2-sol"},"fast":{"provider":"openai","model":"gpt-6-luna"},"vision":{"provider":"openai","model":"gpt-6-luna"}}""",
            (await AuthEndpointTests.Json(settings)).GetProperty("settings").GetProperty("models").GetRawText());
    }

    [Fact]
    public async Task Uses_the_platform_key_when_the_user_has_none()
    {
        var fake = new FakeProvider(HttpStatusCode.OK, OpenAiPayload);
        var platform = $"sk-{TestSecrets.NewSecret()}";
        await using var factory = CreateFactory(fake, platform);
        var browser = await SignUpAsync(factory);

        using var listed = await browser.PostAsJsonAsync("/api/settings/providers/openai/models", new { });
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal($"Bearer {platform}", fake.Requests[^1].Headers.Authorization!.ToString());
        Assert.DoesNotContain(platform, await listed.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway)]
    public async Task Provider_failures_become_clear_errors(HttpStatusCode upstream, HttpStatusCode expected)
    {
        var fake = new FakeProvider(upstream, "{}");
        await using var factory = CreateFactory(fake);
        var browser = await SignUpAsync(factory);

        using var res = await browser.PostAsJsonAsync("/api/settings/providers/openai/models", new { apiKey = "sk-bad" });
        Assert.Equal(expected, res.StatusCode);
        Assert.True((await AuthEndpointTests.Json(res)).TryGetProperty("error", out _));
    }

    private static ProviderModelsClient NewClient(FakeProvider fake) =>
        new(new HttpClient(fake), new MemoryCache(new MemoryCacheOptions()), new ConfigurationBuilder().Build());

    private sealed class FakeProvider(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
