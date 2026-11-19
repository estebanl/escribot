using System.Text.RegularExpressions;
using Escribot.Api.Api;

namespace Escribot.Api.Ai;

/// <summary>The models a provider starts with, and the model each role uses on it by default.</summary>
public sealed record ProviderPicks(string Grader, string Fast, string Vision, IReadOnlyList<string> Starting)
{
    public string For(ModelRole role) => role switch
    {
        ModelRole.Grader => Grader,
        ModelRole.Fast => Fast,
        _ => Vision,
    };
}

/// <summary>
/// Picks a direct provider's role models from the models it offers now, so nobody has to
/// keep a list of model names up to date. OpenRouter's public catalogue supplies the price
/// and image support of each lab's models:
/// <list type="bullet">
/// <item>fast: the newest cheap model, where cheap is within 1.5× of the cheapest recent model;</item>
/// <item>grader (the examiner): the newest model priced above that but no higher than the median;</item>
/// <item>vision: the newest cheap model that reads images, else the grader or fast model if it does.</item>
/// </list>
/// Only the eight newest models count, so an old model that is still cheap never wins.
/// The defaults in <see cref="AiProviders.Defaults"/> are used only when there is too little
/// price data to pick (offline, or a provider whose model names OpenRouter does not use).
/// </summary>
public static partial class ModelPicker
{
    /// <summary>How many models a newly added provider starts with, at least.</summary>
    public const int StartingSize = 5;

    private const int Window = 8;
    private const double CheapFactor = 1.5;

    private static readonly Dictionary<string, string> CatalogVendor = new()
    {
        ["anthropic"] = "anthropic",
        ["openai"] = "openai",
        ["gemini"] = "google",
        ["deepseek"] = "deepseek",
    };

    /// <summary>
    /// Picks from <paramref name="newestFirst"/> by price, or null when fewer than two of
    /// the newest models have a known price (and always for OpenRouter, whose catalogue
    /// spans every lab).
    /// </summary>
    public static ProviderPicks? Pick(string provider, IReadOnlyList<string> newestFirst, IReadOnlyList<OpenRouterModelInfo>? catalog)
    {
        if (catalog is null || !CatalogVendor.TryGetValue(provider, out var vendor))
        {
            return null;
        }

        var byName = new Dictionary<string, OpenRouterModelInfo>(StringComparer.Ordinal);
        foreach (var m in catalog)
        {
            var slash = m.Id.IndexOf('/');
            if (slash > 0 && m.Id[..slash] == vendor && Price(m) > 0)
            {
                byName.TryAdd(Normalize(m.Id[(slash + 1)..]), m);
            }
        }

        var priced = newestFirst
            .Take(Window)
            .Select(id => (Id: id, Info: Find(byName, id)))
            .Where(x => x.Info is not null)
            .Select(x => (x.Id, Info: x.Info!, Price: Price(x.Info!)))
            .ToList();
        if (priced.Count < 2)
        {
            return null;
        }

        var cheapCeiling = priced.Min(p => p.Price) * CheapFactor;
        var cheap = priced.Where(p => p.Price <= cheapCeiling).ToList();
        var prices = priced.Select(p => p.Price).Order().ToList();
        var median = prices[prices.Count / 2];

        var fast = cheap[0];
        var grader = priced.FirstOrDefault(p => p.Price > cheapCeiling && p.Price <= median);
        if (grader.Id is null)
        {
            grader = priced.FirstOrDefault(p => p.Price > cheapCeiling);
        }

        if (grader.Id is null)
        {
            grader = fast;
        }

        var vision = cheap.FirstOrDefault(p => p.Info.HasVision).Id
            ?? (grader.Info.HasVision ? grader.Id : fast.Id);

        return WithStarting(grader.Id, fast.Id, vision, newestFirst);
    }

    /// <summary>
    /// <see cref="Pick"/>, else the provider's configured defaults where it still offers
    /// them and its newest model where it does not. With no models at all, the configured defaults.
    /// </summary>
    public static ProviderPicks Resolve(string provider, IReadOnlyList<string> newestFirst, IReadOnlyList<OpenRouterModelInfo>? catalog)
    {
        var defaults = AiProviders.Defaults[provider];
        if (provider == AiProviders.OpenRouter || newestFirst.Count == 0)
        {
            return new ProviderPicks(defaults.GraderModel, defaults.FastModel, defaults.VisionModel, defaults.SuggestedModels);
        }

        if (Pick(provider, newestFirst, catalog) is { } picked)
        {
            return picked;
        }

        string Offered(string configured) => newestFirst.Contains(configured) ? configured : newestFirst[0];
        return WithStarting(Offered(defaults.GraderModel), Offered(defaults.FastModel), Offered(defaults.VisionModel), newestFirst);
    }

    private static ProviderPicks WithStarting(string grader, string fast, string vision, IReadOnlyList<string> newestFirst)
    {
        var picks = new[] { grader, fast, vision }.Distinct().ToList();
        IReadOnlyList<string> starting = [.. picks.Concat(newestFirst).Distinct().Take(Math.Max(StartingSize, picks.Count))];
        return new ProviderPicks(grader, fast, vision, starting);
    }

    private static double Price(OpenRouterModelInfo m) => m.PromptPricePerM + m.CompletionPricePerM;

    /// <summary>
    /// The catalogue entry for a provider's own model name: the same name (dots and dashes
    /// alike, so <c>claude-sonnet-5-5</c> is <c>claude-sonnet-5.5</c>), without a date
    /// suffix, or else a catalogue name that adds a version stamp to it (<c>deepseek-v4-pro-0813</c>).
    /// </summary>
    private static OpenRouterModelInfo? Find(Dictionary<string, OpenRouterModelInfo> byName, string id)
    {
        var name = Normalize(DateSuffixRegex().Replace(id, string.Empty));
        if (byName.TryGetValue(name, out var exact))
        {
            return exact;
        }

        // Only a version stamp of four or more digits: "claude-sonnet-5" must not match "claude-sonnet-5-5".
        return byName
            .Where(e => e.Key.StartsWith(name + "-", StringComparison.Ordinal) && VersionStampRegex().IsMatch(e.Key[(name.Length + 1)..]))
            .OrderBy(e => e.Key.Length)
            .Select(e => e.Value)
            .FirstOrDefault();
    }

    private static string Normalize(string id) => id.ToLowerInvariant().Replace('.', '-');

    [GeneratedRegex(@"^\d{4,}$")]
    private static partial Regex VersionStampRegex();

    [GeneratedRegex(@"-(\d{8}|\d{4}-\d{2}-\d{2})$")]
    private static partial Regex DateSuffixRegex();
}
