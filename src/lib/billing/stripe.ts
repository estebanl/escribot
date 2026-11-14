import Stripe from "stripe";

export type BillingInterval = "month" | "year";

/**
 * Billing is on only when Stripe is fully configured: the API key, the
 * monthly Pro price, and the webhook secret. Without the webhook, checkout
 * would take payment but the subscription would never be recorded.
 */
export function isBillingEnabled(): boolean {
  return Boolean(
    process.env.STRIPE_SECRET_KEY?.trim() &&
      process.env.STRIPE_PRICE_PRO_MONTHLY?.trim() &&
      process.env.STRIPE_WEBHOOK_SECRET?.trim()
  );
}

export function getProPriceId(interval: BillingInterval): string | null {
  const id =
    interval === "year"
      ? process.env.STRIPE_PRICE_PRO_YEARLY?.trim()
      : process.env.STRIPE_PRICE_PRO_MONTHLY?.trim();
  return id || null;
}

let client: Stripe | null = null;

/** Lazily created so builds and pages work without Stripe configured. */
export function getStripe(): Stripe {
  if (!client) {
    const key = process.env.STRIPE_SECRET_KEY?.trim();
    if (!key) throw new Error("Billing is not configured (STRIPE_SECRET_KEY is missing).");

    // Local testing against stripe-mock or a fake server; never honoured in production.
    const apiBase =
      process.env.NODE_ENV !== "production" ? process.env.STRIPE_API_BASE?.trim() : undefined;
    const target = apiBase ? new URL(apiBase) : null;

    client = new Stripe(key, {
      maxNetworkRetries: 2,
      appInfo: { name: "FluentEscritor" },
      ...(target
        ? {
            host: target.hostname,
            port: Number(target.port) || (target.protocol === "http:" ? 80 : 443),
            protocol: target.protocol === "http:" ? "http" : "https",
          }
        : {}),
    });
  }
  return client;
}

export interface PriceDisplay {
  unitAmount: number; // minor units (cents)
  currency: string;
  interval: BillingInterval;
}

let priceCache: { at: number; prices: Partial<Record<BillingInterval, PriceDisplay>> } | null =
  null;
const PRICE_CACHE_MS = 60 * 60 * 1000;

/** Pro prices as configured in Stripe, for the pricing page (cached for an hour). */
export async function getProPrices(): Promise<Partial<Record<BillingInterval, PriceDisplay>>> {
  if (!isBillingEnabled()) return {};
  if (priceCache && Date.now() - priceCache.at < PRICE_CACHE_MS) return priceCache.prices;

  const prices: Partial<Record<BillingInterval, PriceDisplay>> = {};
  for (const interval of ["month", "year"] as const) {
    const id = getProPriceId(interval);
    if (!id) continue;
    try {
      const price = await getStripe().prices.retrieve(id);
      if (price.unit_amount !== null) {
        prices[interval] = {
          unitAmount: price.unit_amount,
          currency: price.currency,
          interval,
        };
      }
    } catch (err) {
      console.warn(`Could not load Stripe price ${id}:`, err);
    }
  }
  priceCache = { at: Date.now(), prices };
  return prices;
}

/** Absolute base URL for Stripe redirect URLs. */
export function getAppOrigin(request: Request): string {
  const configured = process.env.BETTER_AUTH_URL?.trim();
  return (configured || new URL(request.url).origin).replace(/\/+$/, "");
}
