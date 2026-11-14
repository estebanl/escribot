import { AppShell } from "@/components/AppShell";
import { PricingClient } from "@/components/PricingClient";
import { PublicHeader } from "@/components/PublicHeader";
import { SiteFooter } from "@/components/SiteFooter";
import { getDb } from "@/lib/db";
import { PLANS } from "@/lib/plans";
import { getCurrentUser } from "@/lib/session";
import { getProPrices, isBillingEnabled } from "@/lib/billing/stripe";
import { getEffectivePlan, getSubscription } from "@/lib/billing/subscription";

export const dynamic = "force-dynamic";

export const metadata = { title: "Pricing — FluentEscritor" };

export default async function PricingPage({
  searchParams,
}: {
  searchParams: Promise<{ billing?: string }>;
}) {
  const { billing } = await searchParams;
  const user = await getCurrentUser();
  const db = await getDb();
  const plan = user ? await getEffectivePlan(db, user.id) : null;
  const sub = user ? await getSubscription(db, user.id) : null;
  const billingEnabled = isBillingEnabled();

  const content = (
    <PricingClient
      free={PLANS.free}
      pro={PLANS.pro}
      prices={await getProPrices()}
      viewer={plan ?? "anonymous"}
      billingEnabled={billingEnabled}
      hasSubscription={Boolean(sub?.stripeSubscriptionId)}
      canceled={billing === "canceled"}
    />
  );

  if (user) {
    return (
      <AppShell user={{ ...user, canUpgrade: plan === "free" && billingEnabled }}>
        {content}
      </AppShell>
    );
  }

  return (
    <div className="min-h-screen flex flex-col bg-canvas">
      <PublicHeader />
      <main className="flex-1">{content}</main>
      <SiteFooter />
    </div>
  );
}
