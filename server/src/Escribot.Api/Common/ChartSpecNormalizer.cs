using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Escribot.Api.Common;

/// <summary>
/// Turns a chart specification written by an AI model (or stored earlier) into the exact
/// shape the chart renderer expects.
/// </summary>
public static partial class ChartSpecNormalizer
{
    private static readonly string[] DefaultSeriesColors = ["#16656e", "#c55e1b", "#2b7a52", "#475569", "#9a6b25", "#9e3a2b"];

    private static readonly string[] CommonXKeys = ["year", "xLabel", "label", "name", "period", "month", "date", "category", "x"];

    private static readonly string[] OtherKinds = ["pie", "table", "process", "map", "composite"];

    /// <summary>
    /// A line, bar or mixed chart is normalised; the other kinds are returned as they are.
    /// Returns null for anything that is not a usable chart.
    /// </summary>
    public static JsonObject? Normalize(JsonNode? raw)
    {
        if (raw is not JsonObject obj)
        {
            return null;
        }

        return NormalizeCartesian(obj)
            ?? (Text(obj["kind"]) is { } kind && OtherKinds.Contains(kind) ? (JsonObject)obj.DeepClone() : null);
    }

    /// <summary>Returns null if the input cannot be salvaged into a line, bar or mixed chart.</summary>
    public static JsonObject? NormalizeCartesian(JsonNode? raw)
    {
        if (raw is not JsonObject obj)
        {
            return null;
        }

        var kind = CartesianKind(obj["kind"] ?? obj["type"] ?? obj["chartType"]);
        if (kind is null)
        {
            return null;
        }

        var title = TrimmedText(obj["title"]) ?? "Data Chart";
        var subtitle = TrimmedText(obj["subtitle"]);
        var sourceNote = TrimmedText(obj["sourceNote"]);
        var yAxisUnit = Text(obj["yAxisUnit"] ?? obj["unit"] ?? obj["yUnit"])?.Trim() ?? string.Empty;
        var yAxisRightUnit = TrimmedText(obj["yAxisRightUnit"]);

        // Chart.js and Highcharts write the data the other way round:
        // { labels/categories: [...], datasets/series: [{ label/name, data: [...] }] }
        var rawSeries = obj["series"] as JsonArray ?? obj["datasets"] as JsonArray;
        var rawLabels = obj["labels"] as JsonArray ?? obj["categories"] as JsonArray ?? obj["xLabels"] as JsonArray;

        List<JsonObject>? rows = null;
        string? transposedXKey = null;

        if (obj["data"] is not JsonArray { Count: > 0 }
            && rawSeries is { Count: > 0 }
            && rawSeries[0] is JsonObject firstSeries
            && firstSeries["data"] is JsonArray firstData)
        {
            if (firstData.Count >= 2)
            {
                transposedXKey = "label";
                rows = [];
                for (var i = 0; i < firstData.Count; i++)
                {
                    var row = new JsonObject
                    {
                        ["label"] = rawLabels is not null && i < rawLabels.Count && rawLabels[i] is { } label ? Stringify(label) : (i + 1).ToString(CultureInfo.InvariantCulture),
                    };
                    for (var s = 0; s < rawSeries.Count; s++)
                    {
                        if (rawSeries[s] is JsonObject series)
                        {
                            var key = TrimmedText(series["key"]) ?? $"s{s + 1}";
                            row[key] = series["data"] is JsonArray values && i < values.Count ? values[i]?.DeepClone() : null;
                        }
                    }

                    rows.Add(row);
                }
            }
        }
        else if (obj["data"] is JsonArray { Count: > 0 } data)
        {
            rows = data.OfType<JsonObject>().ToList();
        }

        if (rows is null || rows.Count < 2)
        {
            return null;
        }

        var sampleKeys = rows[0].Select(p => p.Key).ToList();
        if (sampleKeys.Count < 2)
        {
            return null;
        }

        var candidateXKey = Text(obj["xAxisKey"] ?? obj["xKey"] ?? obj["xAxis"]) ?? transposedXKey;
        var xAxisKey = candidateXKey is not null && sampleKeys.Contains(candidateXKey)
            ? candidateXKey
            : CommonXKeys.FirstOrDefault(sampleKeys.Contains)
                ?? sampleKeys.FirstOrDefault(k => ToFiniteNumber(rows[0][k]) is null)
                ?? sampleKeys[0];

        List<Series> series_ = [];
        if (rawSeries is { Count: > 0 })
        {
            for (var index = 0; index < rawSeries.Count; index++)
            {
                var defaultColor = DefaultSeriesColors[index % DefaultSeriesColors.Length];
                var item = rawSeries[index];

                if (TrimmedText(item) is { } name)
                {
                    var matched = sampleKeys.FirstOrDefault(k => k == name)
                        ?? sampleKeys.FirstOrDefault(k => SameIgnoringCase(k, name));
                    if (matched is not null && matched != xAxisKey)
                    {
                        series_.Add(new Series(matched, name, null, null, defaultColor));
                    }

                    continue;
                }

                if (item is not JsonObject definition)
                {
                    continue;
                }

                var rawKey = TrimmedText(definition["key"]) ?? TrimmedText(definition["dataKey"]) ?? TrimmedText(definition["id"]) ?? $"s{index + 1}";
                var rawLabel = TrimmedText(definition["label"]) ?? TrimmedText(definition["name"]) ?? rawKey;

                // Match against the real data keys: exactly, ignoring case, or by label.
                var matchedKey = sampleKeys.FirstOrDefault(k => k == rawKey && k != xAxisKey)
                    ?? sampleKeys.FirstOrDefault(k => SameIgnoringCase(k, rawKey) && k != xAxisKey)
                    ?? sampleKeys.FirstOrDefault(k => SameIgnoringCase(k, rawLabel) && k != xAxisKey);
                if (matchedKey is null)
                {
                    continue;
                }

                series_.Add(new Series(
                    matchedKey,
                    rawLabel,
                    Text(definition["type"]) is "line" or "bar" ? Text(definition["type"]) : null,
                    Text(definition["yAxisId"]) is "left" or "right" ? Text(definition["yAxisId"]) : null,
                    TrimmedText(definition["color"]) ?? defaultColor));
            }
        }

        // No usable series list: every numeric column is a series.
        if (series_.Count == 0)
        {
            var numericKeys = sampleKeys.Where(k => k != xAxisKey && ToFiniteNumber(rows[0][k]) is not null).ToList();
            for (var index = 0; index < numericKeys.Count; index++)
            {
                series_.Add(new Series(numericKeys[index], Humanize(numericKeys[index]), null, null, DefaultSeriesColors[index % DefaultSeriesColors.Length]));
            }
        }

        if (series_.Count == 0)
        {
            return null;
        }

        var cleanRows = new JsonArray();
        var seriesWithNumbers = new HashSet<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var x = rows[index][xAxisKey];
            var clean = new JsonObject
            {
                [xAxisKey] = x is JsonValue value && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number
                    ? x.DeepClone()
                    : (index + 1).ToString(CultureInfo.InvariantCulture),
            };
            foreach (var series in series_)
            {
                if (ToFiniteNumber(rows[index][series.Key]) is { } number)
                {
                    clean[series.Key] = number;
                    seriesWithNumbers.Add(series.Key);
                }
            }

            cleanRows.Add(clean);
        }

        var validSeries = series_.Where(s => seriesWithNumbers.Contains(s.Key)).ToList();
        if (validSeries.Count == 0)
        {
            return null;
        }

        var result = new JsonObject { ["kind"] = kind, ["title"] = title };
        AddIfPresent(result, "subtitle", subtitle);
        result["xAxisKey"] = xAxisKey;
        result["yAxisUnit"] = yAxisUnit;
        AddIfPresent(result, "yAxisRightUnit", yAxisRightUnit);
        result["series"] = new JsonArray(validSeries.Select(s =>
        {
            var node = new JsonObject { ["key"] = s.Key, ["label"] = s.Label };
            AddIfPresent(node, "type", s.Type);
            AddIfPresent(node, "yAxisId", s.YAxisId);
            node["color"] = s.Color;
            return (JsonNode)node;
        }).ToArray());
        result["data"] = cleanRows;
        AddIfPresent(result, "sourceNote", sourceNote);
        return result;
    }

    private sealed record Series(string Key, string Label, string? Type, string? YAxisId, string Color);

    private static void AddIfPresent(JsonObject target, string name, string? value)
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }

    private static string? CartesianKind(JsonNode? raw) => Text(raw)?.Trim().ToLowerInvariant() switch
    {
        "line" or "linechart" or "line_chart" => "line",
        "bar" or "barchart" or "bar_chart" or "column" => "bar",
        "mixed" or "composed" => "mixed",
        _ => null,
    };

    /// <summary>A number, or text holding one such as "1,250" or "45%".</summary>
    internal static double? ToFiniteNumber(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                var number = value.GetValue<double>();
                return double.IsFinite(number) ? number : null;

            case JsonValueKind.String:
                var cleaned = value.GetValue<string>().Trim().Replace(",", string.Empty, StringComparison.Ordinal);
                if (cleaned.EndsWith('%'))
                {
                    cleaned = cleaned[..^1];
                }

                return cleaned.Length > 0
                    && double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    && double.IsFinite(parsed)
                    ? parsed
                    : null;

            default:
                return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>The trimmed text, or null when the node is not text or is blank.</summary>
    private static string? TrimmedText(JsonNode? node) =>
        Text(node)?.Trim() is { Length: > 0 } text ? text : null;

    private static string Stringify(JsonNode node) => Text(node) ?? node.ToJsonString();

    private static bool SameIgnoringCase(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>"unit_sales" becomes "Unit Sales".</summary>
    private static string Humanize(string key) =>
        WordStart().Replace(Separators().Replace(key, " "), m => m.Value.ToUpperInvariant());

    [GeneratedRegex("[_-]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\b\w")]
    private static partial Regex WordStart();
}
