import crypto from "crypto";
import { and, eq, isNull, or } from "drizzle-orm";
import type { Db } from "./index";
import * as schema from "./schema";
import { STARTER_VAULT_ITEMS } from "./seed-prompts";

/**
 * Per-user data access helpers. Every query against an app table must be
 * constrained by the signed-in user's id; these helpers are the single place
 * that knows how ownership works so routes cannot forget the filter.
 */

/** Prompts a user may see: the shared curated bank plus their own. */
export function visiblePrompts(userId: string) {
  return or(isNull(schema.prompts.userId), eq(schema.prompts.userId, userId));
}

export async function getVisiblePrompt(db: Db, userId: string, promptId: string) {
  const [prompt] = await db
    .select()
    .from(schema.prompts)
    .where(and(eq(schema.prompts.id, promptId), visiblePrompts(userId)));
  return prompt ?? null;
}

export async function getOwnedSession(db: Db, userId: string, sessionId: string) {
  const [session] = await db
    .select()
    .from(schema.writingSessions)
    .where(
      and(
        eq(schema.writingSessions.id, sessionId),
        eq(schema.writingSessions.userId, userId)
      )
    );
  return session ?? null;
}

/** Returns the user's settings row, creating it with defaults on first use. */
export async function getOrCreateSettings(db: Db, userId: string) {
  const [existing] = await db
    .select()
    .from(schema.userSettings)
    .where(eq(schema.userSettings.userId, userId));
  if (existing) return existing;

  await db
    .insert(schema.userSettings)
    .values({ userId, updatedAt: new Date().toISOString() })
    .onConflictDoNothing();

  const [created] = await db
    .select()
    .from(schema.userSettings)
    .where(eq(schema.userSettings.userId, userId));
  return created;
}

async function seedStarterVault(db: Db, userId: string) {
  const now = new Date().toISOString();
  await db.insert(schema.vaultItems).values(
    STARTER_VAULT_ITEMS.map((v) => ({
      id: `vault-${crypto.randomUUID()}`,
      userId,
      sourceDraftId: null,
      itemType: v.itemType,
      criterion: v.criterion,
      originalText: v.originalText,
      upgradedText: v.upgradedText,
      explanation: v.explanation,
      exampleSentence: v.exampleSentence,
      masteryLevel: 0,
      reviewCount: 0,
      nextReviewAt: now,
      createdAt: now,
    }))
  );
}

/**
 * Hands every pre-multi-user row (owner = LEGACY_USER_ID) to `userId`.
 * Returns false when there was nothing to claim.
 */
async function claimLegacyData(db: Db, userId: string): Promise<boolean> {
  const legacy = schema.LEGACY_USER_ID;
  const [legacySettings] = await db
    .select({ userId: schema.userSettings.userId })
    .from(schema.userSettings)
    .where(eq(schema.userSettings.userId, legacy));
  if (!legacySettings) return false;

  await db.transaction(async (tx) => {
    await tx.delete(schema.userSettings).where(eq(schema.userSettings.userId, userId));
    await tx
      .update(schema.userSettings)
      .set({ userId })
      .where(eq(schema.userSettings.userId, legacy));
    await tx.update(schema.prompts).set({ userId }).where(eq(schema.prompts.userId, legacy));
    await tx
      .update(schema.writingSessions)
      .set({ userId })
      .where(eq(schema.writingSessions.userId, legacy));
    await tx.update(schema.drafts).set({ userId }).where(eq(schema.drafts.userId, legacy));
    await tx
      .update(schema.evaluations)
      .set({ userId })
      .where(eq(schema.evaluations.userId, legacy));
    await tx
      .update(schema.vaultItems)
      .set({ userId })
      .where(eq(schema.vaultItems.userId, legacy));
    await tx
      .update(schema.microDrills)
      .set({ userId })
      .where(eq(schema.microDrills.userId, legacy));
    await tx.update(schema.aiUsage).set({ userId }).where(eq(schema.aiUsage.userId, legacy));
  });
  return true;
}

/**
 * Runs once when an account is created: the OWNER_EMAIL account inherits the
 * data written before multi-user support; everyone else starts with default
 * settings and the starter Vault.
 */
export async function provisionNewUser(db: Db, user: { id: string; email: string }) {
  const ownerEmail = process.env.OWNER_EMAIL?.trim().toLowerCase();
  if (ownerEmail && user.email.trim().toLowerCase() === ownerEmail) {
    if (await claimLegacyData(db, user.id)) return;
  }
  await getOrCreateSettings(db, user.id);
  await seedStarterVault(db, user.id);
}
