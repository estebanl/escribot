using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Escribot.Api.Ai;

/// <summary>
/// Tolerant JSON extraction from LLM responses that may wrap JSON in markdown fences or prose.
/// </summary>
public static partial class JsonExtractor
{
    public static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static T ExtractJsonObject<T>(string raw)
    {
        var json = ExtractRawJson(raw);
        return JsonSerializer.Deserialize<T>(json, WebOptions)
            ?? throw new InvalidOperationException("Model response did not contain a valid JSON object.");
    }

    public static JsonObject ExtractJsonNode(string raw)
    {
        var json = ExtractRawJson(raw);
        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Model response did not contain a valid JSON object.");
    }

    private static string ExtractRawJson(string raw)
    {
        var trimmed = raw.Trim();
        if (TryValidateJsonObject(trimmed))
        {
            return trimmed;
        }

        var fenced = FenceRegex().Match(trimmed);
        if (fenced.Success)
        {
            var inner = fenced.Groups[1].Value.Trim();
            if (TryValidateJsonObject(inner))
            {
                return inner;
            }
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace != -1 && lastBrace > firstBrace)
        {
            var candidate = trimmed[firstBrace..(lastBrace + 1)];
            if (TryValidateJsonObject(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Model response did not contain a valid JSON object.");
    }

    private static bool TryValidateJsonObject(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !candidate.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(candidate);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex FenceRegex();
}
