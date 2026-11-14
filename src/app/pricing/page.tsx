import Link from "next/link";
import { Feather } from "lucide-react";
import { AppShell } from "@/components/AppShell";
import { PricingClient } from "@/components/PricingClient";
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
    <main className="min-h-screen bg-canvas">
      <header className="border-b border-line bg-surface">
        <div className="max-w-4xl mx-auto px-4 sm:px-6 h-16 flex items-center justify-between">
          <Link href="/login" className="flex items-center gap-2.5">
            <span className="w-9 h-9 rounded-lg bg-accent text-accent-fg flex items-center justify-center">
              <Feather className="w-4 h-4" />
            </span>
            <span className="font-serif text-lg font-semibold text-ink">FluentEscritor</span>
          </Link>
          <Link href="/login?next=/pricing" className="text-sm font-medium text-accent hover:underline">
            Sign in
          </Link>
        </div>
      </header>
      {content}
    </main>
  );
}
