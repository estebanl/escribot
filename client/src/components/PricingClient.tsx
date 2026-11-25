import { useState } from "react";
import { Link } from "react-router";
import { Check, Loader2 } from "lucide-react";
import type { BillingInterval, PriceDisplay } from "@/lib/billing";
import type { PlanLimits } from "@/lib/plans";

type Viewer = "anonymous" | "free" | "pro" | "owner";

function formatPrice(p: PriceDisplay) {
  return new Intl.NumberFormat(undefined, {
    style: "currency",
    currency: p.currency.toUpperCase(),
    minimumFractionDigits: p.unitAmount % 100 === 0 ? 0 : 2,
  }).format(p.unitAmount / 100);
}

function limitText(n: number | null, noun: string) {
  return n === null ? `Unlimited ${noun}` : `${n.toLocaleString()} ${noun} a month`;
}

export function PricingClient({
  free,
  pro,
  prices,
  viewer,
  billingEnabled,
  hasSubscription,
  canceled,
}: {
  free: PlanLimits;
  pro: PlanLimits;
  prices: Partial<Record<BillingInterval, PriceDisplay>>;
  viewer: Viewer;
  billingEnabled: boolean;
  hasSubscription: boolean;
  canceled: boolean;
}) {
  const [interval, setInterval] = useState<BillingInterval>("month");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const price = prices[interval] ?? prices.month;
  const yearlySaving =
    prices.month && prices.year
      ? Math.round((1 - prices.year.unitAmount / (prices.month.unitAmount * 12)) * 100)
      : 0;

  async function go(endpoint: "/api/billing/checkout" | "/api/billing/portal") {
    setBusy(true);
    setError(null);
    try {
      const res = await fetch(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ interval }),
      });
      const data = await res.json();
      if (!res.ok || !data.url) {
        setError(data.error || "Something went wrong. Please try again.");
        setBusy(false);
        return;
      }
      window.location.href = data.url;
    } catch {
      setError("Network error. Please try again.");
      setBusy(false);
    }
  }

  const freeFeatures = [
    limitText(free.evaluationsPerMonth, "graded essays"),
    limitText(free.assistsPerMonth, "coaching requests"),
    "Coach Studio, Inspera exam simulator, drills and Vault",
    "Standard examiner models",
  ];
  const proFeatures = [
    limitText(pro.evaluationsPerMonth, "graded essays"),
    limitText(pro.assistsPerMonth, "coaching requests"),
    "Everything in Free",
    "Choose your examiner and coaching models",
    "Upload SVGs, or turn Task 1 photos into clean SVGs",
  ];

  let proAction: React.ReactNode;
  if (viewer === "anonymous") {
    proAction = (
      <Link to="/signup?next=/pricing" className="pricing-cta" id="pricing-signup-button">
        Create a free account
      </Link>
    );
  } else if (viewer === "owner") {
    proAction = <p className="text-sm text-ink-secondary">Your owner account is unlimited.</p>;
  } else if (viewer === "pro") {
    proAction = hasSubscription ? (
      <button
        type="button"
        id="pricing-manage-button"
        className="pricing-cta"
        onClick={() => go("/api/billing/portal")}
        disabled={busy}
      >
        {busy ? <Loader2 className="w-4 h-4 animate-spin" /> : null}
        Manage billing
      </button>
    ) : (
      <p className="text-sm text-ink-secondary">You have complimentary Pro access.</p>
    );
  } else if (!billingEnabled) {
    proAction = <p className="text-sm text-ink-secondary">Upgrades are not open yet.</p>;
  } else {
    proAction = (
      <button
        type="button"
        id="pricing-upgrade-button"
        className="pricing-cta"
        onClick={() => go("/api/billing/checkout")}
        disabled={busy}
      >
        {busy ? <Loader2 className="w-4 h-4 animate-spin" /> : null}
        Upgrade to Pro
      </button>
    );
  }

  return (
    <div className="max-w-4xl mx-auto px-4 sm:px-6 py-10 space-y-8">
      <header className="text-center space-y-2">
        <h1 className="font-serif text-3xl sm:text-4xl font-semibold text-ink">
          Plans &amp; Pricing
        </h1>
        <p className="text-ink-secondary max-w-xl mx-auto">
          Practise for free, and upgrade when you want examiner feedback on every essay.
        </p>
      </header>

      {canceled && (
        <p role="status" className="text-center text-sm text-ink-secondary">
          Checkout was canceled. You have not been charged.
        </p>
      )}

      {prices.month && prices.year && (
        <div
          role="radiogroup"
          aria-label="Billing period"
          className="mx-auto w-fit flex rounded-lg border border-line-strong p-1 bg-canvas"
        >
          {(["month", "year"] as const).map((value) => (
            <button
              key={value}
              type="button"
              role="radio"
              aria-checked={interval === value}
              id={`pricing-interval-${value}`}
              onClick={() => setInterval(value)}
              className={`px-4 py-1.5 rounded-md text-sm font-medium transition cursor-pointer ${
                interval === value ? "bg-accent text-accent-fg" : "text-ink-secondary hover:text-ink"
              }`}
            >
              {value === "month" ? "Monthly" : `Yearly${yearlySaving > 0 ? ` (save ${yearlySaving}%)` : ""}`}
            </button>
          ))}
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
        <section className="editorial-surface rounded-2xl p-6 sm:p-7 flex flex-col gap-5">
          <div>
            <h2 className="font-serif text-xl font-semibold text-ink">{free.label}</h2>
            <p className="mt-2">
              <span className="font-serif text-4xl font-semibold text-ink">
                {price ? formatPrice({ ...price, unitAmount: 0 }) : "Free"}
              </span>
            </p>
          </div>
          <ul className="space-y-2.5 text-sm text-ink flex-1">
            {freeFeatures.map((f) => (
              <li key={f} className="flex gap-2.5">
                <Check className="w-4 h-4 shrink-0 mt-0.5 text-accent" />
                <span>{f}</span>
              </li>
            ))}
          </ul>
          {viewer === "anonymous" ? (
            <Link to="/signup?next=/pricing" className="pricing-cta-secondary">
              Get started
            </Link>
          ) : viewer === "free" ? (
            <p className="text-sm text-ink-secondary">Your current plan.</p>
          ) : null}
        </section>

        <section
          className="editorial-surface rounded-2xl p-6 sm:p-7 flex flex-col gap-5 border-2"
          style={{ borderColor: "var(--accent-primary)" }}
        >
          <div>
            <h2 className="font-serif text-xl font-semibold text-ink">{pro.label}</h2>
            <p className="mt-2 flex items-baseline gap-1.5">
              <span className="font-serif text-4xl font-semibold text-ink">
                {price ? formatPrice(price) : "—"}
              </span>
              {price && (
                <span className="text-sm text-ink-secondary">
                  / {price.interval === "year" ? "year" : "month"}
                </span>
              )}
            </p>
          </div>
          <ul className="space-y-2.5 text-sm text-ink flex-1">
            {proFeatures.map((f) => (
              <li key={f} className="flex gap-2.5">
                <Check className="w-4 h-4 shrink-0 mt-0.5 text-accent" />
                <span>{f}</span>
              </li>
            ))}
          </ul>
          {proAction}
          {error && (
            <p role="alert" className="text-sm" style={{ color: "var(--status-danger)" }}>
              {error}
            </p>
          )}
        </section>
      </div>

      <p className="text-center text-sm text-ink-secondary max-w-xl mx-auto">
        Have your own OpenRouter, OpenAI, Anthropic, Gemini or DeepSeek API key? Add it in
        Settings to use Escribot without monthly limits on any plan. Subscriptions
        renew automatically and can be canceled any time from Manage billing.
      </p>
    </div>
  );
}
