using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Core;
using Escribot.Api.Api;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Escribot.Api.Ai;

/// <summary>Request parameters needed to construct a provider-backed <see cref="IChatClient"/>.</summary>
public sealed record AiChatClientRequest(
    string Provider,
    string ApiKey,
    string Model,
    IReadOnlyList<string>? FallbackModels,
    string ThinkingMode,
    string ReasoningEffort,
    int? MaxTokens,
    bool HasImage,
    bool EnableTools);

/// <summary>
/// Creates a per-request <see cref="IChatClient"/> for the resolved provider, model, and key.
/// Replaced with a fake implementation in unit and endpoint tests.
/// </summary>
public interface IAiChatClientFactory
{
    IChatClient CreateClient(AiChatClientRequest request);
}

/// <summary>
/// Builds provider-backed <see cref="IChatClient"/> instances using:
/// <list type="bullet">
/// <item><c>OpenAI</c> SDK (<c>.AsIChatClient()</c>) for OpenAI, OpenRouter (with <see cref="OpenRouterHttpHandler"/>), and DeepSeek (with <see cref="DeepSeekHttpHandler"/>);</item>
/// <item>Official <c>Google.GenAI</c> SDK (<c>Client.AsIChatClient(model)</c>) with fallback routing for Gemini;</item>
/// <item>Official <c>Anthropic</c> SDK (<c>Microsoft.Agents.AI.Anthropic</c>) for Anthropic Claude.</item>
/// </list>
/// All providers use pooled <see cref="IHttpClientFactory"/> clients with Aspire's default 10s/30s
/// resilience handlers removed so long IELTS grading calls are governed strictly by per-call timeouts.
/// </summary>
public sealed class UserChatClientFactory(
    IHttpClientFactory httpClientFactory,
    IHttpMessageHandlerFactory handlerFactory,
    IConfiguration configuration) : IAiChatClientFactory
{
    public const string OpenAiHttpClientName = "ai-openai";
    public const string OpenRouterHttpClientName = "ai-openrouter";
    public const string DeepSeekHttpClientName = "ai-deepseek";
    public const string GeminiHttpClientName = "ai-gemini";
    public const string AnthropicHttpClientName = "ai-anthropic";

    public IChatClient CreateClient(AiChatClientRequest request)
    {
        var rawClient = request.Provider switch
        {
            AiProviders.OpenRouter => CreateOpenRouterClient(request),
            "deepseek" => CreateDeepSeekClient(request),
            "openai" => CreateOpenAiClient(request),
            "gemini" => CreateGeminiClient(request),
            "anthropic" => CreateAnthropicClient(request),
            _ => throw new UnsupportedAiProviderException(request.Provider),
        };

        var builder = rawClient.AsBuilder()
            .UseOpenTelemetry(
                sourceName: AiTelemetry.SourceName,
                configure: otel => otel.EnableSensitiveData = false);

        if (request.EnableTools)
        {
            builder.UseFunctionInvocation(configure: fi => fi.MaximumIterationsPerRequest = 3);
        }

        return builder.Build();
    }

    private IChatClient CreateOpenAiClient(AiChatClientRequest request)
    {
        var httpClient = httpClientFactory.CreateClient(OpenAiHttpClientName);
        var options = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(httpClient),
            NetworkTimeout = Timeout.InfiniteTimeSpan,
        };
        return new ChatClient(request.Model, new ApiKeyCredential(request.ApiKey), options).AsIChatClient();
    }

    private IChatClient CreateOpenRouterClient(AiChatClientRequest request)
    {
        var baseUrl = (configuration["OPENROUTER_BASE_URL"]?.Trim() is { Length: > 0 } u ? u : "https://openrouter.ai/api/v1").TrimEnd('/') + "/";
        var inner = handlerFactory.CreateHandler(OpenRouterHttpClientName);
        var handler = new OpenRouterHttpHandler(
            request.Model,
            request.FallbackModels,
            request.ThinkingMode,
            request.ReasoningEffort,
            request.MaxTokens,
            inner);

        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl),
            Transport = new HttpClientPipelineTransport(httpClient),
            NetworkTimeout = Timeout.InfiniteTimeSpan,
        };
        var innerClient = new ChatClient(request.Model, new ApiKeyCredential(request.ApiKey), options).AsIChatClient();
        return new DisposableOwnerChatClient(innerClient, httpClient);
    }

    private IChatClient CreateDeepSeekClient(AiChatClientRequest request)
    {
        var baseUrl = (configuration["DEEPSEEK_BASE_URL"]?.Trim() is { Length: > 0 } u ? u : "https://api.deepseek.com").TrimEnd('/') + "/";
        var effectiveModel = request.HasImage && request.Model.Contains("pro", StringComparison.Ordinal)
            ? "deepseek-flash"
            : request.Model;

        var inner = handlerFactory.CreateHandler(DeepSeekHttpClientName);
        var handler = new DeepSeekHttpHandler(request.ThinkingMode, request.ReasoningEffort, inner);
        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl),
            Transport = new HttpClientPipelineTransport(httpClient),
            NetworkTimeout = Timeout.InfiniteTimeSpan,
        };
        var innerClient = new ChatClient(effectiveModel, new ApiKeyCredential(request.ApiKey), options).AsIChatClient();
        return new DisposableOwnerChatClient(innerClient, httpClient);
    }

    private IChatClient CreateGeminiClient(AiChatClientRequest request)
    {
        var googleClient = new Client(
            apiKey: request.ApiKey,
            clientOptions: new Google.GenAI.Types.ClientOptions
            {
                HttpClientFactory = () => httpClientFactory.CreateClient(GeminiHttpClientName),
            });

        var models = OpenRouterHttpHandler.BuildModelList(request.Model, request.FallbackModels);
        if (models.Count <= 1)
        {
            return new DisposableOwnerChatClient(googleClient.AsIChatClient(request.Model), googleClient);
        }

        return new GeminiFallbackChatClient(googleClient, models);
    }

    private IChatClient CreateAnthropicClient(AiChatClientRequest request)
    {
        var httpClient = httpClientFactory.CreateClient(AnthropicHttpClientName);
        var anthropic = new global::Anthropic.AnthropicClient(new Anthropic.Core.ClientOptions
        {
            ApiKey = request.ApiKey,
            HttpClient = httpClient,
            MaxRetries = 0,
        });
        return anthropic.AsIChatClient(request.Model);
    }

    /// <summary>Ensures per-request wrapper resources (e.g. <see cref="HttpClient"/> or <see cref="Google.GenAI.Client"/>) are disposed with the <see cref="IChatClient"/>.</summary>
    private sealed class DisposableOwnerChatClient(IChatClient inner, IDisposable owner) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                owner.Dispose();
            }
        }
    }

    /// <summary>
    /// Tries Gemini fallback models in order on 404/429/5xx, matching Escribot's previous
    /// <c>GeminiClient</c> resilience while using the official <c>Google.GenAI</c> SDK.
    /// </summary>
    private sealed class GeminiFallbackChatClient(Client googleClient, IReadOnlyList<string> models)
        : DelegatingChatClient(googleClient.AsIChatClient(models[0]))
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < models.Count; i++)
            {
                var model = models[i];
                var client = i == 0 ? InnerClient : googleClient.AsIChatClient(model);
                try
                {
                    var response = await client.GetResponseAsync(messages, options, cancellationToken);
                    if (string.IsNullOrWhiteSpace(response.ModelId))
                    {
                        response.ModelId = model;
                    }

                    return response;
                }
                catch (Exception ex) when (i < models.Count - 1 && IsWorthAnotherModel(ex))
                {
                    // Try the next Gemini fallback model.
                }
            }

            return await base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < models.Count; i++)
            {
                var model = models[i];
                var client = i == 0 ? InnerClient : googleClient.AsIChatClient(model);
                var yieldedAny = false;
                IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
                var shouldRetryNext = false;

                try
                {
                    enumerator = client.GetStreamingResponseAsync(messages, options, cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);

                    while (true)
                    {
                        bool moved;
                        try
                        {
                            moved = await enumerator.MoveNextAsync();
                        }
                        catch (Exception ex) when (!yieldedAny && i < models.Count - 1 && IsWorthAnotherModel(ex))
                        {
                            shouldRetryNext = true;
                            break;
                        }

                        if (!moved)
                        {
                            yield break;
                        }

                        yieldedAny = true;
                        var update = enumerator.Current;
                        if (string.IsNullOrWhiteSpace(update.ModelId))
                        {
                            update.ModelId = model;
                        }

                        yield return update;
                    }
                }
                finally
                {
                    if (enumerator is not null)
                    {
                        await enumerator.DisposeAsync();
                    }
                }

                if (!shouldRetryNext)
                {
                    yield break;
                }
            }
        }

        private static bool IsWorthAnotherModel(Exception ex)
        {
            if (ex is HttpRequestException httpEx)
            {
                return httpEx.StatusCode is
                    HttpStatusCode.NotFound
                    or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout;
            }

            var message = ex.Message;
            return message.Contains("404", StringComparison.Ordinal)
                || message.Contains("429", StringComparison.Ordinal)
                || message.Contains("503", StringComparison.Ordinal)
                || message.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                || message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                googleClient.Dispose();
            }
        }
    }
}
