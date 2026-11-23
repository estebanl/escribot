using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Escribot.Api.Ai;

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

    private sealed class Capture(string responseJson) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
