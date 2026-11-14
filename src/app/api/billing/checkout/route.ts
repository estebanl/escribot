import { NextResponse } from "next/server";
import { getDb } from "@/lib/db";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { checkRateLimit } from "@/lib/rate-limit";
import {
  getAppOrigin,
  getProPriceId,
  getStripe,
  isBillingEnabled,
  type BillingInterval,
} from "@/lib/billing/stripe";
import {
  getEffectivePlan,
  getOrCreateStripeCustomer,
  getSubscription,
  hasProAccess,
} from "@/lib/billing/subscription";

/** Starts a Stripe Checkout session for the Pro plan and returns its URL. */
export async function POST(request: Request) {
  const user = await getCurrentUser();
  if (!user) return unauthorized();

  if (!isBillingEnabled()) {
    return NextResponse.json({ error: "Billing is not available yet." }, { status: 503 });
  }

  const rate = await checkRateLimit(`billing:${user.id}`, 10, 600);
  if (!rate.allowed) {
    return NextResponse.json(
      { error: "Too many attempts. Please wait a few minutes and try again." },
      { status: 429, headers: { "Retry-After": String(rate.retryAfterSeconds) } }
    );
  }

  const body = (await request.json().catch(() => ({}))) as { interval?: string };
  const interval: BillingInterval = body.interval === "year" ? "year" : "month";
  const priceId = getProPriceId(interval);
  if (!priceId) {
    return NextResponse.json({ error: "That billing period is not offered." }, { status: 400 });
  }

  try {
    const db = await getDb();
    const plan = await getEffectivePlan(db, user.id);
    if (plan === "owner") {
      return NextResponse.json(
        { error: "The owner account already has unlimited access." },
        { status: 400 }
      );
    }
    if (hasProAccess(await getSubscription(db, user.id))) {
      return NextResponse.json(
        {
          error: "You already have Pro. Use Manage billing to change or cancel it.",
          code: "already_subscribed",
        },
        { status: 409 }
      );
    }

    const customer = await getOrCreateStripeCustomer(db, user);
    const origin = getAppOrigin(request);

    const session = await getStripe().checkout.sessions.create({
      mode: "subscription",
      customer,
      client_reference_id: user.id,
      line_items: [{ price: priceId, quantity: 1 }],
      subscription_data: { metadata: { userId: user.id } },
      allow_promotion_codes: true,
      success_url: `${origin}/settings?billing=success`,
      cancel_url: `${origin}/pricing?billing=canceled`,
    });

    if (!session.url) throw new Error("Stripe did not return a checkout URL.");
    return NextResponse.json({ url: session.url });
  } catch (err) {
    console.error("Stripe checkout failed:", err);
    return NextResponse.json(
      { error: "Could not start checkout. Please try again." },
      { status: 502 }
    );
  }
}
