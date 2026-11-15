using System.Reflection;
using System.Text.Json;
using FluentEscritor.Api.Data.Cosmos;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Data;

/// <summary>The curated prompt bank shipped with the app, read from an embedded JSON file.</summary>
public sealed record CuratedPromptSeed(Prompt Prompt, string Hash)
{
    private const string ResourceName = "FluentEscritor.Api.Data.Seed.curated-prompts.json";

    public static IReadOnlyList<CuratedPromptSeed> LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var document = JsonDocument.Parse(stream);

        return document.RootElement.EnumerateArray()
            .Select(entry => new CuratedPromptSeed(
                entry.Deserialize<Prompt>(DocumentJson.Options)! with { Source = "curated" },
                DocumentId.Hash(entry.GetRawText())))
            .ToList();
    }
}

/// <summary>
/// Makes the stored curated prompts equal to the shipped list: adds new ones, rewrites
/// changed ones and deletes ones no longer shipped. Running it again changes nothing,
/// and an unchanged list costs one read and no writes.
/// </summary>
public sealed class CuratedPromptSynchronizer(ICuratedPromptStore store)
{
    /// <summary>Returns the number of documents written or deleted.</summary>
    public async Task<int> SynchronizeAsync(IReadOnlyList<CuratedPromptSeed> seeds, CancellationToken cancellationToken = default)
    {
        var stored = await store.GetSeedHashesAsync(cancellationToken);
        var changes = 0;

        foreach (var seed in seeds)
        {
            if (!stored.TryGetValue(seed.Prompt.Id, out var hash) || hash != seed.Hash)
            {
                await store.UpsertAsync(seed.Prompt, seed.Hash, cancellationToken);
                changes++;
            }
        }

        foreach (var id in stored.Keys.Except(seeds.Select(s => s.Prompt.Id)))
        {
            await store.DeleteAsync(id, cancellationToken);
            changes++;
        }

        return changes;
    }
}

/// <summary>
/// Seeds the curated prompts when the app starts. It runs in the background and retries,
/// so a database that is not reachable yet delays the seeding instead of stopping the app.
/// </summary>
internal sealed partial class CuratedPromptSeeder(CuratedPromptSynchronizer synchronizer, ILogger<CuratedPromptSeeder> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seeds = CuratedPromptSeed.LoadEmbedded();
        var delay = FirstRetry;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var changes = await synchronizer.SynchronizeAsync(seeds, stoppingToken);
                LogSeeded(seeds.Count, changes);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The exception type only: its message can contain the account endpoint.
                LogRetry(ex.GetType().Name, delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay * 2 < LongestRetry ? delay * 2 : LongestRetry;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Curated prompts are in place: {Count} shipped, {Changes} written or deleted.")]
    private partial void LogSeeded(int count, int changes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not seed the curated prompts ({Error}); trying again in {Seconds} seconds.")]
    private partial void LogRetry(string error, double seconds);
}
