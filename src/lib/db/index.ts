import { createClient, type Client } from "@libsql/client";
import { drizzle, type LibSQLDatabase } from "drizzle-orm/libsql";
import { count, eq } from "drizzle-orm";
import * as schema from "./schema";
import { CURATED_IELTS_PROMPTS, STARTER_VAULT_ITEMS } from "./seed-prompts";

let rawClient: Client | null = null;
let drizzleInstance: LibSQLDatabase<typeof schema> | null = null;
let bootstrapPromise: Promise<void> | null = null;

function getClient(): Client {
  if (!rawClient) {
    const url = process.env.TURSO_DATABASE_URL?.trim() || "file:./local.db";
    const authToken = process.env.TURSO_AUTH_TOKEN?.trim() || undefined;
    rawClient = createClient({ url, authToken });
  }
  return rawClient;
}

async function ensureSchemaAndSeed(client: Client, db: LibSQLDatabase<typeof schema>) {
  await client.executeMultiple(`
    CREATE TABLE IF NOT EXISTS user_settings (
      id INTEGER PRIMARY KEY,
      current_band REAL NOT NULL DEFAULT 6.5,
      target_band REAL NOT NULL DEFAULT 7.5,
      exam_date TEXT,
      native_language TEXT NOT NULL DEFAULT 'es',
      l1_coaching_enabled INTEGER NOT NULL DEFAULT 1,
      active_provider TEXT NOT NULL DEFAULT 'openrouter',
      grader_model TEXT NOT NULL DEFAULT 'anthropic/claude-sonnet-5.5',
      fast_model TEXT NOT NULL DEFAULT 'deepseek/deepseek-v4.1-flash-1017',
      vision_model TEXT NOT NULL DEFAULT 'deepseek/deepseek-v4.1-flash-1017',
      encrypted_api_keys TEXT NOT NULL DEFAULT '{}',
      updated_at TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS prompts (
      id TEXT PRIMARY KEY,
      task_type TEXT NOT NULL,
      category TEXT NOT NULL,
      topic TEXT NOT NULL,
      title TEXT NOT NULL,
      prompt_text TEXT NOT NULL,
      chart_spec_json TEXT,
      image_data_url TEXT,
      extracted_visual_spec TEXT,
      source TEXT NOT NULL DEFAULT 'curated',
      created_at TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS writing_sessions (
      id TEXT PRIMARY KEY,
      mode TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'in_progress',
      task1_prompt_id TEXT,
      task2_prompt_id TEXT,
      started_at TEXT NOT NULL,
      completed_at TEXT,
      total_seconds_spent INTEGER NOT NULL DEFAULT 0,
      weighted_band REAL
    );

    CREATE TABLE IF NOT EXISTS drafts (
      id TEXT PRIMARY KEY,
      session_id TEXT NOT NULL,
      prompt_id TEXT NOT NULL,
      task_type TEXT NOT NULL,
      version INTEGER NOT NULL DEFAULT 1,
      outline_json TEXT,
      outline_feedback_json TEXT,
      essay_text TEXT NOT NULL,
      word_count INTEGER NOT NULL DEFAULT 0,
      seconds_spent INTEGER NOT NULL DEFAULT 0,
      prompt_highlights_json TEXT,
      scratch_notes TEXT,
      created_at TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS evaluations (
      id TEXT PRIMARY KEY,
      draft_id TEXT NOT NULL,
      overall_band REAL NOT NULL,
      ta_tr_band REAL NOT NULL,
      cc_band REAL NOT NULL,
      lr_band REAL NOT NULL,
      gra_band REAL NOT NULL,
      examiner_summary TEXT NOT NULL,
      criterion_feedback_json TEXT NOT NULL,
      inline_annotations_json TEXT NOT NULL,
      target_band_rewrite TEXT NOT NULL,
      rewrite_key_changes_json TEXT NOT NULL DEFAULT '[]',
      top_priorities_json TEXT NOT NULL,
      provider_used TEXT NOT NULL,
      model_used TEXT NOT NULL,
      cost_usd REAL,
      created_at TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS vault_items (
      id TEXT PRIMARY KEY,
      source_draft_id TEXT,
      item_type TEXT NOT NULL,
      criterion TEXT NOT NULL,
      original_text TEXT NOT NULL,
      upgraded_text TEXT NOT NULL,
      explanation TEXT NOT NULL,
      example_sentence TEXT NOT NULL,
      mastery_level INTEGER NOT NULL DEFAULT 0,
      review_count INTEGER NOT NULL DEFAULT 0,
      next_review_at TEXT,
      created_at TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS micro_drills (
      id TEXT PRIMARY KEY,
      drill_type TEXT NOT NULL,
      target_criterion TEXT NOT NULL,
      prompt_payload_json TEXT NOT NULL,
      user_response TEXT,
      score_json TEXT,
      created_at TEXT NOT NULL,
      completed_at TEXT
    );

    CREATE TABLE IF NOT EXISTS ai_usage (
      id TEXT PRIMARY KEY,
      feature TEXT NOT NULL,
      provider TEXT NOT NULL,
      model_used TEXT NOT NULL,
      prompt_tokens INTEGER,
      completion_tokens INTEGER,
      total_tokens INTEGER,
      cost_usd REAL,
      created_at TEXT NOT NULL
    );

    CREATE INDEX IF NOT EXISTS idx_drafts_session ON drafts(session_id);
    CREATE INDEX IF NOT EXISTS idx_evals_draft ON evaluations(draft_id);
    CREATE INDEX IF NOT EXISTS idx_prompts_task ON prompts(task_type);
    CREATE INDEX IF NOT EXISTS idx_ai_usage_created ON ai_usage(created_at);
  `);

  // Idempotent column migrations for existing databases
  try {
    const userSettingsInfo = await client.execute("PRAGMA table_info(user_settings)");
    const userCols = new Set(userSettingsInfo.rows.map((r) => String(r.name)));
    if (!userCols.has("vision_model")) {
      await client.execute("ALTER TABLE user_settings ADD COLUMN vision_model TEXT");
    }

    // Ensure vision_model uses the active provider's native default (avoids assigning OpenRouter slugs to direct providers)
    await client.execute(`
      UPDATE user_settings
      SET vision_model = CASE active_provider
        WHEN 'deepseek' THEN 'deepseek-flash'
        WHEN 'gemini' THEN 'gemini-2.5-flash'
        WHEN 'openai' THEN 'gpt-4.1'
        WHEN 'anthropic' THEN 'claude-sonnet-4-5'
        ELSE 'deepseek/deepseek-v4.1-flash-1017'
      END
      WHERE vision_model IS NULL
         OR vision_model = ''
         OR (active_provider != 'openrouter' AND vision_model LIKE '%/%')
    `);
  } catch {
    // Ignore if table doesn't support pragma
  }

  try {
    const evalsInfo = await client.execute("PRAGMA table_info(evaluations)");
    const evalCols = new Set(evalsInfo.rows.map((r) => String(r.name)));
    if (!evalCols.has("cost_usd")) {
      await client.execute("ALTER TABLE evaluations ADD COLUMN cost_usd REAL");
    }
  } catch {
    // Ignore if table doesn't support pragma
  }

  const now = new Date().toISOString();

  // Ensure singleton user_settings row id=1
  const existingSettings = await db
    .select()
    .from(schema.userSettings)
    .where(eq(schema.userSettings.id, 1));

  if (existingSettings.length === 0) {
    await db.insert(schema.userSettings).values({
      id: 1,
      currentBand: 6.5,
      targetBand: 7.5,
      examDate: null,
      nativeLanguage: "es",
      l1CoachingEnabled: true,
      activeProvider: "openrouter",
      graderModel: "anthropic/claude-sonnet-5.5",
      fastModel: "deepseek/deepseek-v4.1-flash-1017",
      visionModel: "deepseek/deepseek-v4.1-flash-1017",
      encryptedApiKeys: "{}",
      updatedAt: now,
    });
  }

  // Ensure curated prompts exist
  for (const p of CURATED_IELTS_PROMPTS) {
    await db
      .insert(schema.prompts)
      .values({
        id: p.id,
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

  // Seed starter L1 & Band 8+ Academic Vault items if empty
  const [{ value: vaultCount }] = await db
    .select({ value: count() })
    .from(schema.vaultItems);

  if (vaultCount === 0) {
    for (const v of STARTER_VAULT_ITEMS) {
      await db
        .insert(schema.vaultItems)
        .values({
          id: v.id,
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
        })
        .onConflictDoNothing();
    }
  }
}

export async function getDb(): Promise<LibSQLDatabase<typeof schema>> {
  const client = getClient();
  if (!drizzleInstance) {
    drizzleInstance = drizzle(client, { schema });
  }
  if (!bootstrapPromise) {
    bootstrapPromise = ensureSchemaAndSeed(client, drizzleInstance).catch((err) => {
      bootstrapPromise = null;
      throw err;
    });
  }
  await bootstrapPromise;
  return drizzleInstance;
}

export { schema };
