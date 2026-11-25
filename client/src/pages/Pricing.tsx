import { useLoaderData, useSearchParams } from "react-router";
import { AppShell } from "@/components/AppShell";
import { PricingClient } from "@/components/PricingClient";
import { PublicHeader } from "@/components/PublicHeader";
import { SiteFooter } from "@/components/SiteFooter";
import { getViewer, loadJson } from "@/lib/api";
import type { BillingInterval, PriceDisplay } from "@/lib/billing";
import { loadPlans, type PlanId } from "@/lib/plans";

interface PricesData {
  billingEnabled: boolean;
  prices: Partial<Record<BillingInterval, PriceDisplay>>;
  viewer: PlanId | "anonymous";
  hasSubscription: boolean;
}

export async function loader({ request }: { request: Request }) {
  const [user, pricing, plans] = await Promise.all([
    getViewer(request),
    loadJson<PricesData>("/api/billing/prices", request),
    loadPlans(request),
  ]);
  return { user, pricing, plans };
}

export function PricingPage() {
  const { user, pricing, plans } = useLoaderData<typeof loader>();
  const [searchParams] = useSearchParams();

  const content = (
    <PricingClient
      free={plans.free}
      pro={plans.pro}
      prices={pricing.prices}
      viewer={pricing.viewer}
      billingEnabled={pricing.billingEnabled}
      hasSubscription={pricing.hasSubscription}
      canceled={searchParams.get("billing") === "canceled"}
    />
  );

  if (user) {
    return <AppShell user={user}>{content}</AppShell>;
  }

  return (
    <div className="min-h-screen flex flex-col bg-canvas">
      <PublicHeader />
      <main className="flex-1">{content}</main>
      <SiteFooter />
    </div>
  );
}
