using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Escribot.Api.Api;
using Microsoft.Extensions.Caching.Memory;

namespace Escribot.Api.Ai;

/// <summary>One model a provider offers, as its list-models API names it.</summary>
public sealed record ProviderModel(string Id, string Name);

/// <summary>The provider answered 401 or 403 to the key it was given.</summary>
public sealed class ProviderKeyRejectedException(string provider)
    : Exception($"{provider} rejected the API key.");

/// <summary>
/// The chat models a direct provider (DeepSeek, Gemini, OpenAI, Anthropic) offers to a key,
/// newest first, from the provider's own list-models API. OpenRouter's public catalog is
/// <see cref="OpenRouterModelsClient"/>. Lists are cached per provider and key for an hour;
/// failures are not cached.
/// </summary>
public sealed partial class ProviderModelsClient(HttpClient http, IMemoryCache cache, IConfiguration configuration)
{
    /// <summary>The most models one list returns.</summary>
    public const int MaxModels = 100;

    private const int MaxModelNameLength = 200;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    // Model families that are not chat models, or that the chat completions API cannot call.
    private static readonly string[] OpenAiExcluded =
        ["embedding", "tts", "whisper", "dall-e", "image", "audio", "realtime", "live", "transcribe", "moderation", "search", "instruct", "codex"];

    private static readonly string[] GeminiExcluded =
        ["embedding", "tts", "image", "banana", "live", "native-audio", "aqa", "transcribe", "computer-use", "robotics", "customtools"];

    public static bool Supports(string provider) => provider is "deepseek" or "gemini" or "openai" or "anthropic";

    public async Task<IReadOnlyList<ProviderModel>> ListAsync(string provider, string apiKey, CancellationToken cancellationToken = default)
    {
        if (!Supports(provider))
        {
            throw new ArgumentException($"No list-models API for {provider}.", nameof(provider));
        }

        var cacheKey = CacheKey(provider, apiKey);
        if (Cached(provider, apiKey) is { } cached)
        {
            return cached;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var models = provider switch
        {
            "openai" => ParseOpenAi(await GetAsync(provider, "https://api.openai.com/v1/models", h => h.Authorization = new("Bearer", apiKey), cts.Token)),
            "anthropic" => ParseAnthropic(await GetAsync(provider, "https://api.anthropic.com/v1/models?limit=1000", h =>
            {
                h.TryAddWithoutValidation("x-api-key", apiKey);
                h.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }, cts.Token)),
            "gemini" => ParseGemini(await GetAsync(provider, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000", h => h.TryAddWithoutValidation("x-goog-api-key", apiKey), cts.Token)),
            _ => ParseDeepSeek(await GetAsync(provider, $"{DeepSeekBaseUrl()}/models", h => h.Authorization = new("Bearer", apiKey), cts.Token)),
        };

        IReadOnlyList<ProviderModel> result = [.. models
            .Where(m => m.Id.Length <= MaxModelNameLength && AiProviders.IsValidModel(provider, m.Id))
            .DistinctBy(m => m.Id)
            .Take(MaxModels)];

        cache.Set(cacheKey, result, CacheTtl);
        return result;
    }

    /// <summary>The list last loaded for this provider and key within the hour, without calling the provider.</summary>
    public IReadOnlyList<ProviderModel>? Cached(string provider, string apiKey) =>
        cache.TryGetValue(CacheKey(provider, apiKey), out IReadOnlyList<ProviderModel>? cached) ? cached : null;

    private static string CacheKey(string provider, string apiKey) =>
        $"provider-models:{provider}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))}";

    private string DeepSeekBaseUrl() =>
        (configuration["DEEPSEEK_BASE_URL"]?.Trim() is { Length: > 0 } u ? u : "https://api.deepseek.com").TrimEnd('/');

    private async Task<JsonNode?> GetAsync(string provider, string url, Action<System.Net.Http.Headers.HttpRequestHeaders> headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        headers(request.Headers);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ProviderKeyRejectedException(provider);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{provider} list-models API error ({(int)response.StatusCode}).", null, response.StatusCode);
        }

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static IEnumerable<JsonObject> Items(JsonNode? root, string field) =>
        (root?[field] as JsonArray)?.OfType<JsonObject>() ?? [];

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    /// <summary>
    /// OpenAI lists every model it has; keep the chat ones, newest <c>created</c> first. A
    /// dated snapshot (<c>gpt-5.4-2026-03-05</c>) is left out when its undated alias is listed.
    /// </summary>
    internal static IEnumerable<ProviderModel> ParseOpenAi(JsonNode? root)
    {
        var chat = Items(root, "data")
            .Select(m => (Id: Str(m["id"]), Created: m["created"] is JsonValue c && c.TryGetValue<long>(out var t) ? t : 0))
            .Where(m => m.Id is not null && IsOpenAiChatModel(m.Id))
            .ToList();
        var ids = chat.Select(m => m.Id!).ToHashSet(StringComparer.Ordinal);
        return chat
            .Where(m => OpenAiSnapshotRegex().Match(m.Id!) is not { Success: true } snapshot || !ids.Contains(m.Id![..snapshot.Index]))
            .OrderByDescending(m => m.Created)
            .Select(m => new ProviderModel(m.Id!, m.Id!));
    }

    private static bool IsOpenAiChatModel(string id) =>
        (id.StartsWith("gpt-", StringComparison.Ordinal) || id.StartsWith("chatgpt-", StringComparison.Ordinal) || OpenAiReasoningRegex().IsMatch(id))
        && !OpenAiExcluded.Any(x => id.Contains(x, StringComparison.Ordinal));

    /// <summary>Anthropic already lists its models newest first.</summary>
    internal static IEnumerable<ProviderModel> ParseAnthropic(JsonNode? root) =>
        Items(root, "data")
            .Select(m => (Id: Str(m["id"]), Name: Str(m["display_name"])))
            .Where(m => m.Id is not null)
            .Select(m => new ProviderModel(m.Id!, m.Name ?? m.Id!));

    /// <summary>
    /// Gemini models that generate text, under their <c>gemini-</c> ids. The API gives no
    /// release date, so the version in the id orders them, highest first.
    /// </summary>
    internal static IEnumerable<ProviderModel> ParseGemini(JsonNode? root) =>
        Items(root, "models")
            .Where(m => (m["supportedGenerationMethods"] as JsonArray)?.Any(x => Str(x) == "generateContent") == true)
            .Select(m => (Id: Str(m["name"]) is { } name && name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : Str(m["name"]), Name: Str(m["displayName"])))
            .Where(m => m.Id is not null && m.Id.StartsWith("gemini-", StringComparison.Ordinal)
                && !GeminiExcluded.Any(x => m.Id.Contains(x, StringComparison.Ordinal)))
            .OrderByDescending(m => GeminiVersion(m.Id!))
            .Select(m => new ProviderModel(m.Id!, m.Name ?? m.Id!));

    private static double GeminiVersion(string id) =>
        GeminiVersionRegex().Match(id) is { Success: true } match
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>DeepSeek lists only a few models; keep its order.</summary>
    internal static IEnumerable<ProviderModel> ParseDeepSeek(JsonNode? root) =>
        Items(root, "data")
            .Select(m => Str(m["id"]))
            .Where(id => id is not null)
            .Select(id => new ProviderModel(id!, id!));

    [GeneratedRegex(@"-\d{4}-\d{2}-\d{2}$")]
    private static partial Regex OpenAiSnapshotRegex();

    [GeneratedRegex(@"^o\d")]
    private static partial Regex OpenAiReasoningRegex();

    [GeneratedRegex(@"^gemini-(\d+(?:\.\d+)?)")]
    private static partial Regex GeminiVersionRegex();
}
