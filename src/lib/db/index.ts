import path from "path";
import { createClient, type Client } from "@libsql/client";
import { drizzle, type LibSQLDatabase } from "drizzle-orm/libsql";
import { migrate } from "drizzle-orm/libsql/migrator";
import * as schema from "./schema";
import { CURATED_IELTS_PROMPTS } from "./seed-prompts";

export type Db = LibSQLDatabase<typeof schema>;

let rawClient: Client | null = null;
let drizzleInstance: Db | null = null;
let bootstrapPromise: Promise<void> | null = null;

function getClient(): Client {
  if (!rawClient) {
    const url = process.env.TURSO_DATABASE_URL?.trim() || "file:./local.db";
    const authToken = process.env.TURSO_AUTH_TOKEN?.trim() || undefined;
    rawClient = createClient({ url, authToken });
  }
  return rawClient;
}

async function migrateAndSeed(db: Db) {
  // Versioned migrations live in /drizzle (generated with `npm run db:generate`).
  // Applying them here keeps first boot zero-setup locally and on Vercel; the
  // folder is bundled via `outputFileTracingIncludes` in next.config.ts.
  await migrate(db, { migrationsFolder: path.join(process.cwd(), "drizzle") });

  // Curated prompts are global (user_id NULL) and shared by every account.
  const now = new Date().toISOString();
  for (const p of CURATED_IELTS_PROMPTS) {
    await db
      .insert(schema.prompts)
      .values({
        id: p.id,
        userId: null,
        taskType: p.taskType,
        category: p.category,
        topic: p.topic,
        title: p.title,
        promptText: p.promptText,
        chartSpecJson: p.chartSpec ? JSON.stringify(p.chartSpec) : null,
        imageDataUrl: p.imageDataUrl || null,
        extractedVisualSpec: null,
        source: "curated",
        createdAt: now,
      })
      .onConflictDoNothing();
  }
}

export async function getDb(): Promise<Db> {
  if (!drizzleInstance) {
    drizzleInstance = drizzle(getClient(), { schema });
  }
  if (!bootstrapPromise) {
    bootstrapPromise = migrateAndSeed(drizzleInstance).catch((err) => {
      bootstrapPromise = null;
      throw err;
    });
  }
  await bootstrapPromise;
  return drizzleInstance;
}

export { schema };
