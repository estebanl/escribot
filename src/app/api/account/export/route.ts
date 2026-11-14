import { NextResponse } from "next/server";
import { eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { checkRateLimit } from "@/lib/rate-limit";
import { resolveImageForAI } from "@/lib/storage";

/**
 * Everything stored about the signed-in user, as one JSON download (GDPR
 * access / portability). Saved API keys are listed by provider but never
 * exported, even encrypted.
 */
export async function GET() {
  const user = await getCurrentUser();
  if (!user) return unauthorized();

  const rate = await checkRateLimit(`export:${user.id}`, 5, 3600);
  if (!rate.allowed) {
    return NextResponse.json(
      { error: "Too many exports. Please try again later." },
      { status: 429, headers: { "Retry-After": String(rate.retryAfterSeconds) } }
    );
  }

  const db = await getDb();
  const uid = user.id;
  const strip = <T extends { userId: unknown }>(rows: T[]) =>
    rows.map((row) => {
      const copy: Partial<T> = { ...row };
      delete copy.userId;
      return copy;
    });

  const [account] = await db.select().from(schema.user).where(eq(schema.user.id, uid));
  const signInMethods = await db
    .select({ provider: schema.account.providerId, createdAt: schema.account.createdAt })
    .from(schema.account)
    .where(eq(schema.account.userId, uid));
  const [settings] = await db
    .select()
    .from(schema.userSettings)
    .where(eq(schema.userSettings.userId, uid));
  const [subscription] = await db
    .select()
    .from(schema.subscriptions)
    .where(eq(schema.subscriptions.userId, uid));

  let savedKeyProviders: string[] = [];
  try {
    savedKeyProviders = Object.keys(JSON.parse(settings?.encryptedApiKeys || "{}"));
  } catch {
    savedKeyProviders = [];
  }
  const settingsExport = settings ? { ...settings } : null;
  if (settingsExport) {
    delete (settingsExport as Partial<typeof settings>).encryptedApiKeys;
    delete (settingsExport as Partial<typeof settings>).userId;
  }

  const prompts = await db.select().from(schema.prompts).where(eq(schema.prompts.userId, uid));

  const payload = {
    exportedAt: new Date().toISOString(),
    app: "FluentEscritor",
    account: {
      name: account?.name,
      email: account?.email,
      emailVerified: account?.emailVerified,
      createdAt: account?.createdAt,
      signInMethods,
    },
    settings: settingsExport ? { ...settingsExport, savedApiKeyProviders: savedKeyProviders } : null,
    subscription: subscription
      ? {
          status: subscription.status,
          currentPeriodEnd: subscription.currentPeriodEnd,
          cancelAtPeriodEnd: subscription.cancelAtPeriodEnd,
        }
      : null,
    data: {
      prompts: await Promise.all(
        strip(prompts).map(async (p) => ({
          ...p,
          imageDataUrl: await resolveImageForAI(db, uid, p.imageDataUrl),
        }))
      ),
      sessions: strip(
        await db.select().from(schema.writingSessions).where(eq(schema.writingSessions.userId, uid))
      ),
      drafts: strip(await db.select().from(schema.drafts).where(eq(schema.drafts.userId, uid))),
      evaluations: strip(
        await db.select().from(schema.evaluations).where(eq(schema.evaluations.userId, uid))
      ),
      vaultItems: strip(
        await db.select().from(schema.vaultItems).where(eq(schema.vaultItems.userId, uid))
      ),
      microDrills: strip(
        await db.select().from(schema.microDrills).where(eq(schema.microDrills.userId, uid))
      ),
      aiUsage: strip(await db.select().from(schema.aiUsage).where(eq(schema.aiUsage.userId, uid))),
    },
  };

  return new NextResponse(JSON.stringify(payload, null, 2), {
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Content-Disposition": `attachment; filename="fluentescritor-my-data-${new Date().toISOString().slice(0, 10)}.json"`,
      "Cache-Control": "no-store",
    },
  });
}
