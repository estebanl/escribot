namespace Escribot.Api.Billing;

/// <summary>The Pro prices as configured in Stripe, for the pricing page, kept for an hour.</summary>
public sealed partial class PriceCatalog(StripeSettings settings, IStripeGateway stripe, TimeProvider time, ILogger<PriceCatalog> logger)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private (DateTimeOffset At, IReadOnlyDictionary<string, PriceDisplay> Prices)? _cached;

    /// <summary>Interval ("month", "year") to price. Empty when billing is off; a price Stripe could not return is left out.</summary>
    public async Task<IReadOnlyDictionary<string, PriceDisplay>> GetProPricesAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return new Dictionary<string, PriceDisplay>();
        }

        if (Fresh() is { } cached)
        {
            return cached;
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (Fresh() is { } refreshed)
            {
                return refreshed;
            }

            var prices = new Dictionary<string, PriceDisplay>();
            foreach (var interval in new[] { StripeSettings.Month, StripeSettings.Year })
            {
                if (settings.ProPriceId(interval) is not { } id)
                {
                    continue;
                }

                try
                {
                    if (await stripe.GetPriceAsync(id, interval, cancellationToken) is { } price)
                    {
                        prices[interval] = price;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPriceFailed(logger, ex, id);
                }
            }

            _cached = (time.GetUtcNow(), prices);
            return prices;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private IReadOnlyDictionary<string, PriceDisplay>? Fresh() =>
        _cached is { } c && time.GetUtcNow() - c.At < CacheFor ? c.Prices : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not load Stripe price {PriceId}.")]
    private static partial void LogPriceFailed(ILogger logger, Exception exception, string priceId);
}
