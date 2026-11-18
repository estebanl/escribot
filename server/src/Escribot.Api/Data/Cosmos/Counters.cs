using System.Globalization;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Data.Cosmos;

// A counter is one document per key and time window, with the window start in its id.
// A new window is therefore a new document starting at one, and an increment inside a
// window is a single server-side patch, so concurrent requests can never lose a count.
// Old windows are removed by time-to-live.

internal sealed class CosmosRateLimiter(CosmosStore store, TimeProvider time) : IRateLimiter
{
    public async Task<RateLimitResult> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var windowSeconds = (long)window.TotalSeconds;
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSeconds, 1, nameof(window));

        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var windowStart = now - (now % windowSeconds);

        // The key is hashed: it may hold an IP address, which is not kept, or characters
        // that an id cannot.
        var id = $"rate:{DocumentId.Hash(key)}:{windowStart.ToString(CultureInfo.InvariantCulture)}";
        var count = await CosmosStore.IncrementAsync(store.Counters, id, TimeToLive(windowSeconds), cancellationToken);

        return new RateLimitResult(count <= limit, (int)Math.Max(1, windowStart + windowSeconds - now));
    }

    /// <summary>Long enough to outlive the window, with a margin for clock differences.</summary>
    private static int TimeToLive(long windowSeconds) => (int)Math.Min(int.MaxValue, windowSeconds + 60);
}

internal sealed class CosmosFreeAiDailyCounter(CosmosStore store, TimeProvider time) : IFreeAiDailyCounter
{
    private const int TwoDays = 2 * 24 * 60 * 60;

    public async Task<long> GetTodayAsync(CancellationToken cancellationToken = default)
    {
        var document = await CosmosStore.ReadAsync(store.Counters, TodayId(), TodayId(), cancellationToken);
        return document?["count"]?.GetValue<long>() ?? 0;
    }

    public Task<long> IncrementTodayAsync(CancellationToken cancellationToken = default) =>
        CosmosStore.IncrementAsync(store.Counters, TodayId(), TwoDays, cancellationToken);

    private string TodayId() => $"free-ai:{time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
