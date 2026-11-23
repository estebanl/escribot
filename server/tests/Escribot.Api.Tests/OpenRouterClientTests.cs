using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Escribot.Api.Ai;
using Escribot.Api.Api;

namespace Escribot.Api.Tests;

/// <summary>What the OpenRouter HTTP interceptor sends and captures.</summary>
public class OpenRouterClientTests
{
    [Fact]
    public async Task A_chosen_model_outside_the_fallbacks_still_sends_at_most_three_models_and_captures_cost()
    {
        var capture = new Capture("""{"model":"anthropic/claude-sonnet-5.5","choices":[{"message":{"content":"{}"}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"cost":0.0042}}""");
        using var handler = new OpenRouterHttpHandler(
            primaryModel: "anthropic/claude-sonnet-5.5",
            fallbackModels: ["deepseek/deepseek-v4.1-flash-1017", "anthropic/claude-haiku-5.5", "openai/gpt-6-luna"],
            thinkingMode: "disabled",
            reasoningEffort: "low",
            requestedMaxTokens: 100,
            innerHandler: capture);

        using var http = new HttpClient(handler);
        using var scope = ProviderCallContext.BeginScope(out var meta);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"anthropic/claude-sonnet-5.5","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"),
        };
        using var res = await http.SendAsync(req, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = JsonNode.Parse(capture.Body!)!;
        Assert.Equal("anthropic/claude-sonnet-5.5", body["model"]!.GetValue<string>());
        Assert.Equal(
            ["anthropic/claude-sonnet-5.5", "deepseek/deepseek-v4.1-flash-1017", "anthropic/claude-haiku-5.5"],
            body["models"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Equal("deny", body["provider"]!["data_collection"]!.GetValue<string>());
        Assert.Equal(4096, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("anthropic/claude-sonnet-5.5", meta.ActualModel);
        Assert.Equal(0.0042, meta.CostUsd);
    }

    [Fact]
    public async Task Cost_from_several_calls_in_one_scope_is_added_up()
    {
        // A function-invocation loop makes several HTTP calls inside one metering scope.
        var capture = new Capture("""{"model":"m","choices":[{"message":{"content":"{}"}}],"usage":{"cost":0.25}}""");
        using var handler = NewHandler(capture);
        using var http = new HttpClient(handler);
        using var scope = ProviderCallContext.BeginScope(out var meta);

        for (var i = 0; i < 3; i++)
        {
            using var res = await http.SendAsync(NewRequest(), CancellationToken.None);
        }

        Assert.Equal(0.75, meta.CostUsd);
    }

    [Fact]
    public void Reasoning_and_refusal_are_captured_from_json_and_stream_deltas()
    {
        var meta = new ProviderCallMetadata();
        OpenRouterHttpHandler.CaptureFromJson("""{"choices":[{"message":{"content":"","reasoning":"{\"a\":1}"}}]}""", meta);
        Assert.Equal("{\"a\":1}", meta.Reasoning);

        var streamed = new ProviderCallMetadata();
        OpenRouterHttpHandler.CaptureFromJson("""{"choices":[{"delta":{"reasoning":"par"}}]}""", streamed);
        OpenRouterHttpHandler.CaptureFromJson("""{"choices":[{"delta":{"reasoning":"tial","refusal":"no"}}]}""", streamed);
        Assert.Equal("partial", streamed.Reasoning);
        Assert.Equal("no", streamed.Refusal);
    }

    [Fact]
    public async Task An_empty_answer_falls_back_to_the_reasoning_text()
    {
        var capture = new Capture("""{"model":"m","choices":[{"message":{"role":"assistant","content":"","reasoning":"{\"ok\":true}"},"finish_reason":"stop"}]}""");
        using var client = NewFactory(capture).CreateClient(NewChatRequest());
        var captured = new CapturedLlmUsage();
        using var metered = new MeteringChatClient(client, captured);

        await metered.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi")]);

        Assert.Equal("{\"ok\":true}", captured.Reasoning);
    }

    [Fact]
    public async Task A_server_error_is_not_retried_by_the_openai_sdk()
    {
        var capture = new Capture("""{"error":{"message":"upstream"}}""", HttpStatusCode.BadGateway);
        using var client = NewFactory(capture).CreateClient(NewChatRequest());

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi")]));

        Assert.Equal(1, capture.Calls);
    }

    private static OpenRouterHttpHandler NewHandler(HttpMessageHandler inner) => new(
        primaryModel: "m",
        fallbackModels: null,
        thinkingMode: "disabled",
        reasoningEffort: "low",
        requestedMaxTokens: 100,
        innerHandler: inner);

    private static HttpRequestMessage NewRequest() => new(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
    {
        Content = new StringContent("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"),
    };

    private static UserChatClientFactory NewFactory(HttpMessageHandler inner) => new(
        new SingleHandlerFactory(inner),
        new SingleHandlerFactory(inner),
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static AiChatClientRequest NewChatRequest() => new(
        Provider: AiProviders.OpenRouter,
        ApiKey: "test-key",
        Model: "m",
        FallbackModels: null,
        ThinkingMode: "disabled",
        ReasoningEffort: "low",
        MaxTokens: 100,
        HasImage: false,
        EnableTools: false);

    private sealed class SingleHandlerFactory(HttpMessageHandler inner) : IHttpClientFactory, IHttpMessageHandlerFactory
    {
        public HttpClient CreateClient(string name) => new(new NonDisposingHandler(inner));

        public HttpMessageHandler CreateHandler(string name) => new NonDisposingHandler(inner);
    }

    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
            // The shared test handler outlives each client.
        }
    }

    private sealed class Capture(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
