import { NextResponse } from "next/server";
import { and, eq, ne } from "drizzle-orm";
import crypto from "crypto";
import { z } from "zod";
import { getDb, schema } from "@/lib/db";
import { getCurrentUser, unauthorized } from "@/lib/session";

export async function GET() {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();

    const db = await getDb();
    // Ownership is implied by who imports the file, so user ids are not exported.
    const owned = <T extends { userId: unknown }>(rows: T[]) =>
      rows.map((row) => {
        const copy: Partial<T> = { ...row };
        delete copy.userId;
        return copy;
      });

    const prompts = await db
      .select()
      .from(schema.prompts)
      .where(and(eq(schema.prompts.userId, user.id), ne(schema.prompts.source, "curated")));
    const sessions = await db
      .select()
      .from(schema.writingSessions)
      .where(eq(schema.writingSessions.userId, user.id));
    const drafts = await db
      .select()
      .from(schema.drafts)
      .where(eq(schema.drafts.userId, user.id));
    const evaluations = await db
      .select()
      .from(schema.evaluations)
      .where(eq(schema.evaluations.userId, user.id));
    const vaultItems = await db
      .select()
      .from(schema.vaultItems)
      .where(eq(schema.vaultItems.userId, user.id));
    const microDrills = await db
      .select()
      .from(schema.microDrills)
      .where(eq(schema.microDrills.userId, user.id));

    return NextResponse.json({
      exportedAt: new Date().toISOString(),
      app: "FluentEscritor",
      version: 2,
      data: {
        prompts: owned(prompts),
        sessions: owned(sessions),
        drafts: owned(drafts),
        evaluations: owned(evaluations),
        vaultItems: owned(vaultItems),
        microDrills: owned(microDrills),
      },
    });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Export failed." },
      { status: 500 }
    );
  }
}

// Backup files are untrusted input: every row is validated field by field and
// unknown keys (including any `userId`) are dropped.
const str = z.string();
const optStr = z.string().nullish();
const num = z.number();
const optNum = z.number().nullish();

const promptRow = z.object({
  id: str,
  taskType: z.enum(["task1", "task2"]),
  category: str,
  topic: str,
  title: str,
  promptText: str,
  chartSpecJson: optStr,
  imageDataUrl: optStr,
  extractedVisualSpec: optStr,
  source: z.enum(["ai_generated", "custom"]).catch("custom"),
  createdAt: str,
});

const sessionRow = z.object({
  id: str,
  mode: str,
  status: str.default("in_progress"),
  task1PromptId: optStr,
  task2PromptId: optStr,
  startedAt: str,
  completedAt: optStr,
  totalSecondsSpent: num.default(0),
  weightedBand: optNum,
});

const draftRow = z.object({
  id: str,
  sessionId: str,
  promptId: str,
  taskType: str,
  version: num.default(1),
  outlineJson: optStr,
  outlineFeedbackJson: optStr,
  essayText: str,
  wordCount: num.default(0),
  secondsSpent: num.default(0),
  promptHighlightsJson: optStr,
  scratchNotes: optStr,
  createdAt: str,
});

const evaluationRow = z.object({
  id: str,
  draftId: str,
  overallBand: num,
  taTrBand: num,
  ccBand: num,
  lrBand: num,
  graBand: num,
  examinerSummary: str,
  criterionFeedbackJson: str,
  inlineAnnotationsJson: str,
  targetBandRewrite: str,
  rewriteKeyChangesJson: str.default("[]"),
  topPrioritiesJson: str,
  providerUsed: str,
  modelUsed: str,
  costUsd: optNum,
  createdAt: str,
});

const vaultRow = z.object({
  id: str,
  sourceDraftId: optStr,
  itemType: str,
  criterion: str,
  originalText: str,
  upgradedText: str,
  explanation: str,
  exampleSentence: str,
  masteryLevel: num.default(0),
  reviewCount: num.default(0),
  nextReviewAt: optStr,
  createdAt: str,
});

const drillRow = z.object({
  id: str,
  drillType: str,
  targetCriterion: str,
  promptPayloadJson: str,
  userResponse: optStr,
  scoreJson: optStr,
  createdAt: str,
  completedAt: optStr,
});

const backupPayload = z.object({
  data: z.object({
    prompts: z.array(promptRow).default([]),
    sessions: z.array(sessionRow).default([]),
    drafts: z.array(draftRow).default([]),
    evaluations: z.array(evaluationRow).default([]),
    vaultItems: z.array(vaultRow).default([]),
    microDrills: z.array(drillRow).default([]),
  }),
});

const newId = (prefix: string) => `${prefix}-${crypto.randomUUID()}`;

export async function POST(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const userId = user.id;

    const parsed = backupPayload.safeParse(await request.json().catch(() => null));
    if (!parsed.success) {
      return NextResponse.json({ error: "Invalid backup JSON format." }, { status: 400 });
    }
    const { data } = parsed.data;

    // Every imported row gets a fresh id owned by the importing user, so a
    // backup can never collide with (or overwrite) another account's rows.
    // References between rows are rewritten through these maps; a reference
    // to a row that is not in the file is kept as-is (e.g. curated prompt ids).
    const promptIds = new Map(data.prompts.map((p) => [p.id, newId("custom")]));
    const sessionIds = new Map(data.sessions.map((s) => [s.id, newId("sess")]));
    const draftIds = new Map(
      data.drafts.filter((d) => sessionIds.has(d.sessionId)).map((d) => [d.id, newId("draft")])
    );
    const mapPrompt = (id: string | null | undefined) =>
      id ? promptIds.get(id) ?? id : null;

    const db = await getDb();
    let importedCount = 0;

    await db.transaction(async (tx) => {
      for (const p of data.prompts) {
        await tx.insert(schema.prompts).values({ ...p, id: promptIds.get(p.id)!, userId });
        importedCount++;
      }
      for (const s of data.sessions) {
        await tx.insert(schema.writingSessions).values({
          ...s,
          id: sessionIds.get(s.id)!,
          userId,
          task1PromptId: mapPrompt(s.task1PromptId),
          task2PromptId: mapPrompt(s.task2PromptId),
        });
        importedCount++;
      }
      for (const d of data.drafts) {
        const id = draftIds.get(d.id);
        if (!id) continue; // draft of a session that is not in this backup
        await tx.insert(schema.drafts).values({
          ...d,
          id,
          userId,
          sessionId: sessionIds.get(d.sessionId)!,
          promptId: mapPrompt(d.promptId)!,
        });
        importedCount++;
      }
      for (const e of data.evaluations) {
        const draftId = draftIds.get(e.draftId);
        if (!draftId) continue;
        await tx
          .insert(schema.evaluations)
          .values({ ...e, id: newId("eval"), userId, draftId });
        importedCount++;
      }
      for (const v of data.vaultItems) {
        await tx.insert(schema.vaultItems).values({
          ...v,
          id: newId("vault"),
          userId,
          sourceDraftId: v.sourceDraftId ? draftIds.get(v.sourceDraftId) ?? null : null,
        });
        importedCount++;
      }
      for (const m of data.microDrills) {
        await tx.insert(schema.microDrills).values({ ...m, id: newId("drill"), userId });
        importedCount++;
      }
    });

    return NextResponse.json({ ok: true, importedCount });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Import failed." },
      { status: 500 }
    );
  }
}
