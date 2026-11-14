import crypto from "crypto";
import { lt } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";

const MAX_MESSAGE = 1000;
const MAX_STACK = 4000;
const RETENTION_DAYS = 30;

export interface ErrorReport {
  source: "server" | "client";
  error: unknown;
  path?: string | null;
  digest?: string | null;
  userId?: string | null;
}

/**
 * Records an error for the admin view and logs it. Never throws: error
 * reporting must not turn one failure into two.
 */
export async function reportError({ source, error, path, digest, userId }: ErrorReport) {
  const message =
    error instanceof Error ? error.message : typeof error === "string" ? error : "Unknown error";
  const stack = error instanceof Error ? error.stack ?? null : null;
  console.error(`[${source} error]${path ? ` ${path}` : ""}:`, error);

  try {
    const db = await getDb();
    await db.insert(schema.errorEvents).values({
      id: crypto.randomUUID(),
      source,
      message: message.slice(0, MAX_MESSAGE),
      stack: stack?.slice(0, MAX_STACK) ?? null,
      path: path?.slice(0, 500) ?? null,
      digest: digest?.slice(0, 100) ?? null,
      userId: userId ?? null,
      createdAt: new Date().toISOString(),
    });

    // Prune old reports now and then instead of on every write.
    if (Math.random() < 0.02) {
      const cutoff = new Date(Date.now() - RETENTION_DAYS * 86_400_000).toISOString();
      await db.delete(schema.errorEvents).where(lt(schema.errorEvents.createdAt, cutoff));
    }
  } catch (storeErr) {
    console.error("Failed to store error report:", storeErr);
  }
}
