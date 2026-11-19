using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Escribot.Api.Ai;
using Microsoft.Extensions.Configuration;

namespace Escribot.Api.Tests;

/// <summary>What the OpenRouter client sends.</summary>
public class OpenRouterClientTests
{
    [Fact]
    public async Task A_chosen_model_outside_the_fallbacks_still_sends_at_most_three_models()
    {
        var handler = new Capture();
        var client = new OpenRouterClient(new HttpClient(handler), new ConfigurationBuilder().Build());

        // The fast role on Sonnet, with the role's three fallbacks: four models before the cap.
        await client.CompleteAsync(new LlmProviderRequest(
            "sk-or", "anthropic/claude-sonnet-5.5",
            ["deepseek/deepseek-v4.1-flash-1017", "anthropic/claude-haiku-5.5", "openai/gpt-6-luna"],
            "system", "user", null, JsonMode: true, "disabled", "low", 100), CancellationToken.None);

        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("anthropic/claude-sonnet-5.5", body["model"]!.GetValue<string>());
        Assert.Equal(
            ["anthropic/claude-sonnet-5.5", "deepseek/deepseek-v4.1-flash-1017", "anthropic/claude-haiku-5.5"],
            body["models"]!.AsArray().Select(m => m!.GetValue<string>()));
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"anthropic/claude-sonnet-5.5","choices":[{"message":{"content":"{}"}}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
