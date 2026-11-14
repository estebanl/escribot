import { NextResponse } from "next/server";
import { getDb } from "@/lib/db";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { getAppOrigin, getStripe, isBillingEnabled } from "@/lib/billing/stripe";
import { getSubscription } from "@/lib/billing/subscription";
import { reportError } from "@/lib/errors";

/** Opens the Stripe customer portal (change plan, update card, cancel, invoices). */
export async function POST(request: Request) {
  const user = await getCurrentUser();
  if (!user) return unauthorized();

  if (!isBillingEnabled()) {
    return NextResponse.json({ error: "Billing is not available yet." }, { status: 503 });
  }

  const db = await getDb();
  const sub = await getSubscription(db, user.id);
  if (!sub?.stripeSubscriptionId) {
    return NextResponse.json(
      { error: "There is no subscription to manage yet." },
      { status: 404 }
    );
  }

  try {
    const session = await getStripe().billingPortal.sessions.create({
      customer: sub.stripeCustomerId,
      return_url: `${getAppOrigin(request)}/settings`,
    });
    return NextResponse.json({ url: session.url });
  } catch (err) {
    await reportError({ source: "server", error: err, path: "/api/billing/portal" });
    return NextResponse.json(
      { error: "Could not open billing management. Please try again." },
      { status: 502 }
    );
  }
}
