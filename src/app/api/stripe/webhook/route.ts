import { NextResponse } from "next/server";
import type Stripe from "stripe";
import { getDb } from "@/lib/db";
import { getStripe } from "@/lib/billing/stripe";
import { applySubscriptionEvent } from "@/lib/billing/subscription";

const SUBSCRIPTION_EVENTS = new Set([
  "customer.subscription.created",
  "customer.subscription.updated",
  "customer.subscription.deleted",
  "customer.subscription.paused",
  "customer.subscription.resumed",
]);

/**
 * Stripe webhook endpoint (public; authenticated by the signature header).
 * Subscribe it to the customer.subscription.* events listed above.
 */
export async function POST(request: Request) {
  const secret = process.env.STRIPE_WEBHOOK_SECRET?.trim();
  if (!secret || !process.env.STRIPE_SECRET_KEY?.trim()) {
    return NextResponse.json({ error: "Webhook not configured." }, { status: 503 });
  }

  const signature = request.headers.get("stripe-signature");
  // The signature covers the exact bytes Stripe sent, so read the raw body.
  const payload = await request.text();

  let event: Stripe.Event;
  try {
    if (!signature) throw new Error("Missing stripe-signature header.");
    event = getStripe().webhooks.constructEvent(payload, signature, secret);
  } catch {
    return NextResponse.json({ error: "Invalid signature." }, { status: 400 });
  }

  if (!SUBSCRIPTION_EVENTS.has(event.type)) {
    return NextResponse.json({ received: true, handled: false });
  }

  try {
    const db = await getDb();
    const result = await applySubscriptionEvent(
      db,
      event.data.object as Stripe.Subscription,
      event.created
    );
    if (result === "unknown_customer") {
      console.warn(`Stripe event ${event.id} is for a customer with no matching user.`);
    }
    return NextResponse.json({ received: true, result });
  } catch (err) {
    // A 5xx makes Stripe retry the delivery with backoff.
    console.error(`Failed to process Stripe event ${event.id}:`, err);
    return NextResponse.json({ error: "Processing failed." }, { status: 500 });
  }
}
