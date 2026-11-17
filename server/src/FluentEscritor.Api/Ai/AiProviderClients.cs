using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentEscritor.Api.Api;

namespace FluentEscritor.Api.Ai;

public interface IAiProviderClient
{
    string ProviderId { get; }
    Task<LlmResultWithMeta> CompleteAsync(LlmProviderRequest request, CancellationToken cancellationToken);
}

internal static partial class AiHttpHelpers
{
    public static (string MimeType, string Base64)? SplitDataUrl(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl))
        {
            return null;
        }

        var match = DataUrlRegex().Match(dataUrl);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    public static async Task<JsonNode> PostJsonAsync(
        HttpClient http,
        string url,
        JsonObject body,
        Action<HttpRequestHeaders>? configureHeaders,
        string providerLabel,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        configureHeaders?.Invoke(message.Headers);

        using var response = await http.SendAsync(message, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = responseText.Length > 300 ? responseText[..300] : responseText;
            throw new HttpRequestException($"{providerLabel} API error ({(int)response.StatusCode}): {snippet}", null, response.StatusCode);
        }

        return JsonNode.Parse(responseText)
            ?? throw new InvalidOperationException($"Empty JSON response from {providerLabel} API.");
    }

    [GeneratedRegex(@"^data:([^;]+);base64,(.+)$")]
    private static partial Regex DataUrlRegex();
}

public sealed class OpenRouterClient(HttpClient http, IConfiguration configuration) : IAiProviderClient
{
    public string ProviderId => AiProviders.OpenRouter;

    public async Task<LlmResultWithMeta> CompleteAsync(LlmProviderRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = (configuration["OPENROUTER_BASE_URL"]?.Trim() is { Length: > 0 } u ? u : "https://openrouter.ai/api/v1").TrimEnd('/');

        JsonNode userContent = request.ImageDataUrl is { Length: > 0 } imageUrl
            ? new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = request.UserPrompt },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imageUrl } })
            : JsonValue.Create(request.UserPrompt)!;

        var modelList = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(request.Model) && seen.Add(request.Model))
        {
            modelList.Add(request.Model);
        }

        if (request.FallbackModels is not null)
        {
            foreach (var fallback in request.FallbackModels)
            {
                if (!string.IsNullOrWhiteSpace(fallback) && seen.Add(fallback))
                {
                    modelList.Add(fallback);
                }
            }
        }

        var body = new JsonObject
        {
            ["model"] = modelList.FirstOrDefault() ?? request.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent }),
            ["provider"] = new JsonObject { ["data_collection"] = "deny" },
            ["temperature"] = 0.25,
        };

        if (request.ThinkingMode == "enabled")
        {
            body["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort };
        }

        if (modelList.Count > 1)
        {
            var modelsArray = new JsonArray();
            foreach (var m in modelList)
            {
                modelsArray.Add(m);
            }

            body["models"] = modelsArray;
        }

        if (request.MaxTokens is > 0)
        {
            // OpenRouter reasoning models allocate internal reasoning tokens against max_tokens before producing content.
            body["max_tokens"] = Math.Max(request.MaxTokens.Value + 2048, 4096);
        }

        if (request.JsonMode)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        var data = await AiHttpHelpers.PostJsonAsync(
            http,
            $"{baseUrl}/chat/completions",
            body,
            headers =>
            {
                headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
                headers.TryAddWithoutValidation("HTTP-Referer", "https://fluentescritor.app");
                headers.TryAddWithoutValidation("X-Title", "FluentEscritor");
            },
            "OpenRouter",
            cancellationToken);

        var firstChoice = data["choices"]?[0];
        var msg = firstChoice?["message"];
        var content = msg?["content"]?.GetValue<string>();
        if (string.IsNullOrEmpty(content))
        {
            var refusal = msg?["refusal"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(refusal))
            {
                throw new InvalidOperationException($"OpenRouter model refused request: {refusal}");
            }

            var reasoning = msg?["reasoning"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(reasoning))
            {
                content = reasoning;
            }
        }

        var actualModel = data["model"]?.GetValue<string>() ?? request.Model;
        if (string.IsNullOrEmpty(content))
        {
            var finishReason = firstChoice?["finish_reason"]?.GetValue<string>() ?? "unknown";
            throw new InvalidOperationException($"Empty response from OpenRouter API (model: {actualModel}, finish_reason: {finishReason}).");
        }

        var usage = data["usage"];
        return new LlmResultWithMeta(
            Content: content,
            ModelUsed: actualModel,
            CostUsd: usage?["cost"]?.GetValue<double?>(),
            PromptTokens: usage?["prompt_tokens"]?.GetValue<int?>(),
            CompletionTokens: usage?["completion_tokens"]?.GetValue<int?>(),
            TotalTokens: usage?["total_tokens"]?.GetValue<int?>());
    }
}

public sealed class DeepSeekClient(HttpClient http, IConfiguration configuration) : IAiProviderClient
{
    public string ProviderId => "deepseek";

    public async Task<LlmResultWithMeta> CompleteAsync(LlmProviderRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = (configuration["DEEPSEEK_BASE_URL"]?.Trim() is { Length: > 0 } u ? u : "https://api.deepseek.com").TrimEnd('/');
        var effectiveModel = !string.IsNullOrEmpty(request.ImageDataUrl) && request.Model.Contains("pro", StringComparison.Ordinal)
            ? "deepseek-flash"
            : request.Model;

        JsonNode userContent = request.ImageDataUrl is { Length: > 0 } imageUrl
            ? new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = request.UserPrompt },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = imageUrl, ["detail"] = "high" },
                })
            : JsonValue.Create(request.UserPrompt)!;

        var body = new JsonObject
        {
            ["model"] = effectiveModel,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent }),
            ["thinking"] = new JsonObject { ["type"] = request.ThinkingMode },
        };

        if (request.ThinkingMode == "enabled")
        {
            body["reasoning_effort"] = request.ReasoningEffort;
        }
        else
        {
            body["temperature"] = 0.25;
        }

        if (request.MaxTokens is > 0)
        {
            body["max_tokens"] = request.MaxTokens.Value;
        }

        if (request.JsonMode)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        var data = await AiHttpHelpers.PostJsonAsync(
            http,
            $"{baseUrl}/chat/completions",
            body,
            headers => headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey),
            "DeepSeek",
            cancellationToken);

        var content = data["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException("Empty response from DeepSeek API.");
        }

        var usage = data["usage"];
        return new LlmResultWithMeta(
            Content: content,
            ModelUsed: effectiveModel,
            PromptTokens: usage?["prompt_tokens"]?.GetValue<int?>(),
            CompletionTokens: usage?["completion_tokens"]?.GetValue<int?>(),
            TotalTokens: usage?["total_tokens"]?.GetValue<int?>());
    }
}

public sealed class OpenAiClient(HttpClient http) : IAiProviderClient
{
    public string ProviderId => "openai";

    public async Task<LlmResultWithMeta> CompleteAsync(LlmProviderRequest request, CancellationToken cancellationToken)
    {
        JsonNode userContent = request.ImageDataUrl is { Length: > 0 } imageUrl
            ? new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = request.UserPrompt },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = imageUrl, ["detail"] = "high" },
                })
            : JsonValue.Create(request.UserPrompt)!;

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent }),
        };

        if (request.MaxTokens is > 0)
        {
            body["max_tokens"] = request.MaxTokens.Value;
        }

        if (request.JsonMode)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        var data = await AiHttpHelpers.PostJsonAsync(
            http,
            "https://api.openai.com/v1/chat/completions",
            body,
            headers => headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey),
            "OpenAI",
            cancellationToken);

        var content = data["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException("Empty response from OpenAI API.");
        }

        var usage = data["usage"];
        return new LlmResultWithMeta(
            Content: content,
            ModelUsed: request.Model,
            PromptTokens: usage?["prompt_tokens"]?.GetValue<int?>(),
            CompletionTokens: usage?["completion_tokens"]?.GetValue<int?>(),
            TotalTokens: usage?["total_tokens"]?.GetValue<int?>());
    }
}

public sealed class GeminiClient(HttpClient http) : IAiProviderClient
{
    public string ProviderId => "gemini";

    public async Task<LlmResultWithMeta> CompleteAsync(LlmProviderRequest request, CancellationToken cancellationToken)
    {
        var parts = new JsonArray(new JsonObject { ["text"] = request.UserPrompt });
        if (AiHttpHelpers.SplitDataUrl(request.ImageDataUrl) is { } parsed)
        {
            parts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = parsed.MimeType,
                    ["data"] = parsed.Base64,
                },
            });
        }

        var generationConfig = request.JsonMode
            ? new JsonObject { ["responseMimeType"] = "application/json", ["temperature"] = 0.25 }
            : new JsonObject { ["temperature"] = 0.3 };

        if (request.MaxTokens is > 0)
        {
            generationConfig["maxOutputTokens"] = request.MaxTokens.Value;
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = generationConfig,
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(request.Model)}:generateContent?key={Uri.EscapeDataString(request.ApiKey)}";
        var data = await AiHttpHelpers.PostJsonAsync(http, url, body, null, "Gemini", cancellationToken);

        var resultParts = data["candidates"]?[0]?["content"]?["parts"] as JsonArray;
        var text = resultParts is null
            ? string.Empty
            : string.Concat(resultParts.Select(p => p?["text"]?.GetValue<string>() ?? string.Empty));

        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Empty response from Gemini API.");
        }

        return new LlmResultWithMeta(Content: text, ModelUsed: request.Model);
    }
}

public sealed class AnthropicClient(HttpClient http) : IAiProviderClient
{
    public string ProviderId => "anthropic";

    public async Task<LlmResultWithMeta> CompleteAsync(LlmProviderRequest request, CancellationToken cancellationToken)
    {
        var contentBlocks = new JsonArray();
        if (AiHttpHelpers.SplitDataUrl(request.ImageDataUrl) is { } parsed)
        {
            contentBlocks.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = parsed.MimeType,
                    ["data"] = parsed.Base64,
                },
            });
        }

        var promptText = request.JsonMode
            ? $"{request.UserPrompt}\n\nRespond strictly with a single valid JSON object and no markdown prose outside JSON."
            : request.UserPrompt;

        contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = promptText });

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["system"] = request.SystemPrompt,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = contentBlocks }),
        };

        var data = await AiHttpHelpers.PostJsonAsync(
            http,
            "https://api.anthropic.com/v1/messages",
            body,
            headers =>
            {
                headers.TryAddWithoutValidation("x-api-key", request.ApiKey);
                headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            },
            "Anthropic",
            cancellationToken);

        var blocks = data["content"] as JsonArray;
        var text = blocks is null
            ? string.Empty
            : string.Concat(blocks
                .Where(c => c?["type"]?.GetValue<string>() == "text")
                .Select(c => c?["text"]?.GetValue<string>() ?? string.Empty));

        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Empty response from Anthropic API.");
        }

        var usage = data["usage"];
        var inputTokens = usage?["input_tokens"]?.GetValue<int?>();
        var outputTokens = usage?["output_tokens"]?.GetValue<int?>();
        int? totalTokens = inputTokens is not null && outputTokens is not null ? inputTokens + outputTokens : null;

        return new LlmResultWithMeta(
            Content: text,
            ModelUsed: request.Model,
            PromptTokens: inputTokens,
            CompletionTokens: outputTokens,
            TotalTokens: totalTokens);
    }
}

public sealed class OpenRouterModelsClient(HttpClient http, IConfiguration configuration, TimeProvider time)
{
    private static readonly TimeSpan SuccessCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset ExpiresAt, IReadOnlyList<OpenRouterModelInfo> Models)? _cache;

    private static readonly IReadOnlyList<OpenRouterModelInfo> FallbackModels =
    [
        new("anthropic/claude-sonnet-5.5", "Anthropic: Claude Sonnet 5.5", 1000000, true, true, 2.0, 10.0),
        new("anthropic/claude-opus-5.5", "Anthropic: Claude Opus 5.5", 1000000, true, true, 4.0, 20.0),
        new("anthropic/claude-haiku-5.5", "Anthropic: Claude Haiku 5.5", 1000000, true, true, 0.1, 0.5),
        new("deepseek/deepseek-v4.1-flash-1017", "DeepSeek: DeepSeek V4.1 Flash", 1048576, true, true, 0.006, 0.5),
        new("openai/gpt-6.1-sol", "OpenAI: GPT-6.1 Sol", 1050000, true, true, 2.0, 10.0),
        new("openai/gpt-6-luna", "OpenAI: GPT-6 Luna", 1050000, true, true, 0.1, 0.5),
        new("google/gemini-4-argon", "Google: Gemini 4 Argon", 1048576, true, true, 2.0, 10.0),
    ];

    public async Task<IReadOnlyList<OpenRouterModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (_cache is { } cached && now < cached.ExpiresAt)
        {
            return cached.Models;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = time.GetUtcNow();
            if (_cache is { } rechecked && now < rechecked.ExpiresAt)
            {
                return rechecked.Models;
            }

            try
            {
                var baseUrl = (configuration["OPENROUTER_BASE_URL"]?.Trim() is { Length: > 0 } u ? u : "https://openrouter.ai/api/v1").TrimEnd('/');
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
                req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://fluentescritor.app");
                req.Headers.TryAddWithoutValidation("X-Title", "FluentEscritor");

                using var res = await http.SendAsync(req, cts.Token);
                if (!res.IsSuccessStatusCode)
                {
                    var fallback = _cache?.Models ?? FallbackModels;
                    _cache = (now.Add(FailureCacheTtl), fallback);
                    return fallback;
                }

                var json = await res.Content.ReadAsStringAsync(cts.Token);
                var root = JsonNode.Parse(json);
                if (root?["data"] is not JsonArray dataArray)
                {
                    var fallback = _cache?.Models ?? FallbackModels;
                    _cache = (now.Add(FailureCacheTtl), fallback);
                    return fallback;
                }

                var mapped = new List<OpenRouterModelInfo>();
                foreach (var item in dataArray)
                {
                    if (item is not JsonObject m)
                    {
                        continue;
                    }

                    var id = m["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id) || id.Contains(":batch", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var supportedParams = (m["supported_parameters"] as JsonArray)?
                        .Select(p => p?.GetValue<string>())
                        .Where(p => p is not null)
                        .ToHashSet(StringComparer.Ordinal) ?? [];

                    var supportsJson = supportedParams.Contains("response_format") || supportedParams.Contains("structured_outputs");
                    if (!supportsJson)
                    {
                        continue;
                    }

                    var modalities = (m["architecture"]?["input_modalities"] as JsonArray)?
                        .Select(p => p?.GetValue<string>())
                        .Where(p => p is not null)
                        .ToHashSet(StringComparer.Ordinal) ?? [];

                    var hasVision = modalities.Contains("image");
                    var contextLength = m["context_length"]?.GetValue<int?>() ?? 0;
                    var name = m["name"]?.GetValue<string>() ?? id;
                    var pPrompt = ParsePrice(m["pricing"]?["prompt"]) * 1_000_000;
                    var pComp = ParsePrice(m["pricing"]?["completion"]) * 1_000_000;

                    mapped.Add(new OpenRouterModelInfo(
                        id,
                        name,
                        contextLength,
                        hasVision,
                        supportsJson,
                        Math.Round(pPrompt, 4, MidpointRounding.AwayFromZero),
                        Math.Round(pComp, 4, MidpointRounding.AwayFromZero)));
                }

                mapped.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                var models = mapped.Count > 0 ? (IReadOnlyList<OpenRouterModelInfo>)mapped : FallbackModels;
                _cache = (now.Add(mapped.Count > 0 ? SuccessCacheTtl : FailureCacheTtl), models);
                return models;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                var fallback = _cache?.Models ?? FallbackModels;
                _cache = (now.Add(FailureCacheTtl), fallback);
                return fallback;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static double ParsePrice(JsonNode? node)
    {
        if (node is null)
        {
            return 0;
        }

        if (node is JsonValue val)
        {
            if (val.TryGetValue<double>(out var d))
            {
                return double.IsFinite(d) ? d : 0;
            }

            if (val.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return double.IsFinite(parsed) ? parsed : 0;
            }
        }

        return 0;
    }
}
