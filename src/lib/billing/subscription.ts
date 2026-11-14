import type Stripe from "stripe";
import { eq } from "drizzle-orm";
import type { Db } from "@/lib/db";
import * as schema from "@/lib/db/schema";
import { getOrCreateSettings } from "@/lib/db/scope";
import { resolvePlan, type PlanId } from "@/lib/plans";
import { getStripe } from "./stripe";

export type SubscriptionRow = typeof schema.subscriptions.$inferSelect;

/**
 * Statuses that grant Pro. `past_due` keeps access while Stripe retries the
 * card (Smart Retries); Stripe moves the subscription to `canceled` or
 * `unpaid` if every retry fails, which removes access.
 */
const PRO_STATUSES = new Set(["active", "trialing", "past_due"]);

export function hasProAccess(sub: Pick<SubscriptionRow, "status"> | null | undefined): boolean {
  return Boolean(sub?.status && PRO_STATUSES.has(sub.status));
}

export async function getSubscription(db: Db, userId: string) {
  const [row] = await db
    .select()
    .from(schema.subscriptions)
    .where(eq(schema.subscriptions.userId, userId));
  return row ?? null;
}

/** The plan that applies to a user right now: owner, paid or complimentary Pro, or Free. */
export async function getEffectivePlan(db: Db, userId: string): Promise<PlanId> {
  const [settings, sub, [account]] = await Promise.all([
    getOrCreateSettings(db, userId),
    getSubscription(db, userId),
    db.select({ email: schema.user.email }).from(schema.user).where(eq(schema.user.id, userId)),
  ]);
  return resolvePlan(settings.plan, account?.email ?? "", hasProAccess(sub));
}

/** Returns the user's Stripe customer id, creating the customer on first use. */
export async function getOrCreateStripeCustomer(
  db: Db,
  user: { id: string; email: string; name: string }
): Promise<string> {
  const existing = await getSubscription(db, user.id);
  if (existing) return existing.stripeCustomerId;

  const customer = await getStripe().customers.create(
    { email: user.email, name: user.name, metadata: { userId: user.id } },
    // Two quick clicks must not create two customers for the same user.
    { idempotencyKey: `customer-create-${user.id}` }
  );

  await db
    .insert(schema.subscriptions)
    .values({
      userId: user.id,
      stripeCustomerId: customer.id,
      updatedAt: new Date().toISOString(),
    })
    .onConflictDoNothing();

  // Re-read: a concurrent request may have stored its customer first.
  const row = await getSubscription(db, user.id);
  return row!.stripeCustomerId;
}

function periodEnd(sub: Stripe.Subscription): string | null {
  // Billing periods live on the subscription items in current Stripe API versions.
  const ends = sub.items.data.map((item) => item.current_period_end).filter(Boolean);
  return ends.length ? new Date(Math.max(...ends) * 1000).toISOString() : null;
}

function customerId(sub: Stripe.Subscription): string {
  return typeof sub.customer === "string" ? sub.customer : sub.customer.id;
}

export type ApplyResult = "applied" | "stale" | "unknown_customer" | "ignored";

/**
 * Stores the state carried by a customer.subscription.* webhook event. The
 * event payload is the full subscription object at that moment, so no API
 * call is needed. Events can arrive late or out of order: an event older than
 * the last one applied is skipped.
 */
export async function applySubscriptionEvent(
  db: Db,
  sub: Stripe.Subscription,
  eventCreated: number
): Promise<ApplyResult> {
  const custId = customerId(sub);
  let [row] = await db
    .select()
    .from(schema.subscriptions)
    .where(eq(schema.subscriptions.stripeCustomerId, custId));

  if (!row) {
    // Customer created outside the app (e.g. in the dashboard) but tagged with our user id.
    const userId = sub.metadata?.userId;
    if (!userId) return "unknown_customer";
    const [owner] = await db
      .select({ id: schema.user.id })
      .from(schema.user)
      .where(eq(schema.user.id, userId));
    if (!owner) return "unknown_customer";
    await db
      .insert(schema.subscriptions)
      .values({ userId, stripeCustomerId: custId, updatedAt: new Date().toISOString() })
      .onConflictDoNothing();
    [row] = await db
      .select()
      .from(schema.subscriptions)
      .where(eq(schema.subscriptions.stripeCustomerId, custId));
    if (!row) return "unknown_customer";
  }

  const sameSubscription = row.stripeSubscriptionId === sub.id;
  if (sameSubscription && eventCreated < row.lastEventAt) return "stale";

  // A second subscription on the same customer only takes over the row if it
  // grants access and the tracked one does not; this stops the end of an old
  // subscription from downgrading a user who has since resubscribed.
  if (row.stripeSubscriptionId && !sameSubscription) {
    if (hasProAccess(row) || !PRO_STATUSES.has(sub.status)) return "ignored";
  }

  await db
    .update(schema.subscriptions)
    .set({
      stripeSubscriptionId: sub.id,
      status: sub.status,
      priceId: sub.items.data[0]?.price?.id ?? null,
      currentPeriodEnd: periodEnd(sub),
      cancelAtPeriodEnd: sub.cancel_at_period_end || Boolean(sub.cancel_at),
      lastEventAt: eventCreated,
      updatedAt: new Date().toISOString(),
    })
    .where(eq(schema.subscriptions.userId, row.userId));

  return "applied";
}
