import { NextResponse } from "next/server";
import { and, desc, eq } from "drizzle-orm";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { getVisiblePrompt, visiblePrompts } from "@/lib/db/scope";
import { getCurrentUser, unauthorized } from "@/lib/session";
import type { CriterionKey, SessionMode } from "@/lib/types";

export async function GET() {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const db = await getDb();

    const sessions = await db
      .select()
      .from(schema.writingSessions)
      .where(eq(schema.writingSessions.userId, user.id))
      .orderBy(desc(schema.writingSessions.startedAt));

    const allDrafts = await db
      .select()
      .from(schema.drafts)
      .where(eq(schema.drafts.userId, user.id))
      .orderBy(desc(schema.drafts.createdAt));

    const allEvals = await db
      .select()
      .from(schema.evaluations)
      .where(eq(schema.evaluations.userId, user.id))
      .orderBy(desc(schema.evaluations.createdAt));

    const allPrompts = await db
      .select()
      .from(schema.prompts)
      .where(visiblePrompts(user.id));
    const promptMap = new Map(allPrompts.map((p) => [p.id, p]));
    const evalByDraft = new Map(allEvals.map((e) => [e.draftId, e]));

    const enrichedSessions = sessions.map((s) => {
      const sessionDrafts = allDrafts
        .filter((d) => d.sessionId === s.id)
        .map((d) => ({
          ...d,
          prompt: promptMap.get(d.promptId) || null,
          evaluation: evalByDraft.get(d.id) || null,
        }));

      return {
        ...s,
        task1Prompt: s.task1PromptId ? promptMap.get(s.task1PromptId) || null : null,
        task2Prompt: s.task2PromptId ? promptMap.get(s.task2PromptId) || null : null,
        drafts: sessionDrafts,
      };
    });

    // Analytics aggregation across chronological evaluations
    const chronologicalEvals = [...allEvals].reverse();
    const trendPoints = chronologicalEvals.map((ev, index) => {
      const draft = allDrafts.find((d) => d.id === ev.draftId);
      const prompt = draft ? promptMap.get(draft.promptId) : null;
      return {
        index: index + 1,
        date: ev.createdAt.slice(0, 10),
        label: prompt
          ? `${prompt.taskType === "task1" ? "T1" : "T2"} v${draft?.version ?? 1}`
          : `Draft #${index + 1}`,
        overall: ev.overallBand,
        taTr: ev.taTrBand,
        cc: ev.ccBand,
        lr: ev.lrBand,
        gra: ev.graBand,
      };
    });

    let weakestCriterion: CriterionKey = "CC";
    let averages = { overall: 0, TA_TR: 0, CC: 0, LR: 0, GRA: 0 };

    if (allEvals.length > 0) {
      const sum = allEvals.reduce(
        (acc, e) => ({
          overall: acc.overall + e.overallBand,
          TA_TR: acc.TA_TR + e.taTrBand,
          CC: acc.CC + e.ccBand,
          LR: acc.LR + e.lrBand,
          GRA: acc.GRA + e.graBand,
        }),
        { overall: 0, TA_TR: 0, CC: 0, LR: 0, GRA: 0 }
      );
      const n = allEvals.length;
      averages = {
        overall: Number((sum.overall / n).toFixed(2)),
        TA_TR: Number((sum.TA_TR / n).toFixed(2)),
        CC: Number((sum.CC / n).toFixed(2)),
        LR: Number((sum.LR / n).toFixed(2)),
        GRA: Number((sum.GRA / n).toFixed(2)),
      };

      const entries: Array<[CriterionKey, number]> = [
        ["TA_TR", averages.TA_TR],
        ["CC", averages.CC],
        ["LR", averages.LR],
        ["GRA", averages.GRA],
      ];
      entries.sort((a, b) => a[1] - b[1]);
      weakestCriterion = entries[0][0];
    }

    const totalWordsWritten = allDrafts.reduce((acc, d) => acc + d.wordCount, 0);

    return NextResponse.json({
      sessions: enrichedSessions,
      analytics: {
        totalEssaysGraded: allEvals.length,
        totalWordsWritten,
        averages,
        weakestCriterion,
        trendPoints,
      },
    });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to load sessions." },
      { status: 500 }
    );
  }
}

export async function POST(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const body = (await request.json()) as {
      mode?: SessionMode;
      task1PromptId?: string | null;
      task2PromptId?: string | null;
    };

    const mode: SessionMode = body.mode || "coach";
    const db = await getDb();

    let task1PromptId = body.task1PromptId || null;
    let task2PromptId = body.task2PromptId || null;

    // A session may only reference prompts this user can see.
    for (const promptId of [task1PromptId, task2PromptId]) {
      if (promptId && !(await getVisiblePrompt(db, user.id, promptId))) {
        return NextResponse.json({ error: "Prompt not found." }, { status: 404 });
      }
    }

    if (mode === "simulator_full_60m") {
      if (!task1PromptId) {
        const t1s = await db
          .select()
          .from(schema.prompts)
          .where(and(eq(schema.prompts.taskType, "task1"), visiblePrompts(user.id)));
        task1PromptId = t1s[0]?.id || null;
      }
      if (!task2PromptId) {
        const t2s = await db
          .select()
          .from(schema.prompts)
          .where(and(eq(schema.prompts.taskType, "task2"), visiblePrompts(user.id)));
        task2PromptId = t2s[0]?.id || null;
      }
    }

    const id = `sess-${crypto.randomUUID().slice(0, 10)}`;
    const now = new Date().toISOString();

    const record = {
      id,
      userId: user.id,
      mode,
      status: "in_progress",
      task1PromptId,
      task2PromptId,
      startedAt: now,
      completedAt: null,
      totalSecondsSpent: 0,
      weightedBand: null,
    };

    await db.insert(schema.writingSessions).values(record);

    return NextResponse.json({ session: record });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to create session." },
      { status: 500 }
    );
  }
}
