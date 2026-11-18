import { useLoaderData, useSearchParams } from "react-router";
import { AppShell } from "@/components/AppShell";
import { PricingClient } from "@/components/PricingClient";
import { PublicHeader } from "@/components/PublicHeader";
import { SiteFooter } from "@/components/SiteFooter";
import { getViewer, loadJson } from "@/lib/api";
import type { BillingInterval, PriceDisplay } from "@/lib/billing";
import { PLANS, type PlanId } from "@/lib/plans";

interface PricesData {
  billingEnabled: boolean;
  prices: Partial<Record<BillingInterval, PriceDisplay>>;
  viewer: PlanId | "anonymous";
  hasSubscription: boolean;
}

export async function loader({ request }: { request: Request }) {
  const [user, pricing] = await Promise.all([
    getViewer(request),
    loadJson<PricesData>("/api/billing/prices", request),
  ]);
  return { user, pricing };
}

export function PricingPage() {
  const { user, pricing } = useLoaderData<typeof loader>();
  const [searchParams] = useSearchParams();

  const content = (
    <PricingClient
      free={PLANS.free}
      pro={PLANS.pro}
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
