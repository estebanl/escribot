import { eq, inArray } from "drizzle-orm";
import type { Db } from "@/lib/db";
import * as schema from "@/lib/db/schema";
import { getStripe } from "@/lib/billing/stripe";
import { getSubscription } from "@/lib/billing/subscription";
import { deleteUserImages } from "@/lib/storage";

/**
 * Erases everything the app holds about a user, before Better Auth deletes
 * the user, session and account rows. Stripe is handled first: deleting the
 * customer cancels any subscription immediately and removes saved cards, so
 * a deleted user is never billed again. If Stripe cannot be reached the
 * whole deletion is aborted rather than leaving a live subscription behind.
 * Invoices stay in Stripe, which is required for accounting.
 */
export async function deleteAllUserData(db: Db, userId: string) {
  const sub = await getSubscription(db, userId);
  if (sub) {
    try {
      await getStripe().customers.del(sub.stripeCustomerId);
    } catch (err) {
      const code = (err as { code?: string }).code;
      if (code !== "resource_missing") {
        throw new Error("Could not cancel your subscription. Please try again in a minute.");
      }
    }
  }

  await deleteUserImages(db, userId);

  await db.transaction(async (tx) => {
    await tx.delete(schema.evaluations).where(eq(schema.evaluations.userId, userId));
    await tx.delete(schema.drafts).where(eq(schema.drafts.userId, userId));
    await tx.delete(schema.writingSessions).where(eq(schema.writingSessions.userId, userId));
    await tx.delete(schema.vaultItems).where(eq(schema.vaultItems.userId, userId));
    await tx.delete(schema.microDrills).where(eq(schema.microDrills.userId, userId));
    await tx.delete(schema.prompts).where(eq(schema.prompts.userId, userId));
    await tx.delete(schema.aiUsage).where(eq(schema.aiUsage.userId, userId));
    await tx.delete(schema.userSettings).where(eq(schema.userSettings.userId, userId));
    await tx.delete(schema.subscriptions).where(eq(schema.subscriptions.userId, userId));
    await tx
      .delete(schema.apiRateLimits)
      .where(
        inArray(schema.apiRateLimits.key, [
          `ai:${userId}`,
          `billing:${userId}`,
          `export:${userId}`,
        ])
      );
    // Keep error reports for debugging, but detach them from the person.
    await tx
      .update(schema.errorEvents)
      .set({ userId: null })
      .where(eq(schema.errorEvents.userId, userId));
  });
}
