import { NextResponse } from "next/server";
import { and, desc, eq } from "drizzle-orm";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { evaluateEssayWithAI, extractVisualSpecFromImage } from "@/lib/ai/examiner";
import { computeWeightedExamBand, countEssayWords } from "@/lib/ielts-utils";
import type { OutlineFeedback, OutlineInput, TaskType } from "@/lib/types";

export const maxDuration = 60;

interface SingleDraftSubmission {
  promptId: string;
  taskType: TaskType;
  essayText: string;
  secondsSpent?: number;
  outline?: OutlineInput | null;
  outlineFeedback?: OutlineFeedback | null;
  promptHighlights?: string[];
  scratchNotes?: string;
}

export async function POST(request: Request) {
  try {
    const body = (await request.json()) as {
      sessionId: string;
      submissions: SingleDraftSubmission[];
      totalSecondsSpent?: number;
    };

    if (!body.sessionId || !Array.isArray(body.submissions) || body.submissions.length === 0) {
      return NextResponse.json(
        { error: "Session ID and at least one draft submission are required." },
        { status: 400 }
      );
    }

    const db = await getDb();
    const now = new Date().toISOString();
    const existingVault = await db.select().from(schema.vaultItems);
    const knownOriginals = new Set(
      existingVault.map((v) => v.originalText.trim().toLowerCase())
    );

    const gradedResults = [];

    for (const sub of body.submissions) {
      if (!sub.essayText || sub.essayText.trim().length < 15) {
        return NextResponse.json(
          { error: `Please write at least a short draft for ${sub.taskType.toUpperCase()} before submitting.` },
          { status: 400 }
        );
      }

      const [prompt] = await db
        .select()
        .from(schema.prompts)
        .where(eq(schema.prompts.id, sub.promptId));

      if (!prompt) {
        return NextResponse.json({ error: "Prompt not found." }, { status: 404 });
      }

      // Resolve visual ground truth (caching vision extraction for uploaded Task 1 images)
      let visualContext: string | null = prompt.chartSpecJson || prompt.extractedVisualSpec || null;
      if (!visualContext && prompt.imageDataUrl) {
        visualContext = await extractVisualSpecFromImage(
          prompt.imageDataUrl,
          prompt.promptText
        );
        await db
          .update(schema.prompts)
          .set({ extractedVisualSpec: visualContext })
          .where(eq(schema.prompts.id, prompt.id));
      }

      // Determine draft version (v1, v2, v3...) for this session + prompt
      const priorDrafts = await db
        .select()
        .from(schema.drafts)
        .where(
          and(
            eq(schema.drafts.sessionId, body.sessionId),
            eq(schema.drafts.promptId, sub.promptId)
          )
        )
        .orderBy(desc(schema.drafts.version));

      const nextVersion = (priorDrafts[0]?.version ?? 0) + 1;
      let previousBand: number | null = null;
      if (priorDrafts[0]) {
        const [prevEval] = await db
          .select()
          .from(schema.evaluations)
          .where(eq(schema.evaluations.draftId, priorDrafts[0].id));
        previousBand = prevEval?.overallBand ?? null;
      }

      const draftId = `draft-${crypto.randomUUID().slice(0, 10)}`;
      const wordCount = countEssayWords(sub.essayText);

      await db.insert(schema.drafts).values({
        id: draftId,
        sessionId: body.sessionId,
        promptId: sub.promptId,
        taskType: sub.taskType,
        version: nextVersion,
        outlineJson: sub.outline ? JSON.stringify(sub.outline) : null,
        outlineFeedbackJson: sub.outlineFeedback
          ? JSON.stringify(sub.outlineFeedback)
          : null,
        essayText: sub.essayText,
        wordCount,
        secondsSpent: sub.secondsSpent ?? 0,
        promptHighlightsJson: sub.promptHighlights
          ? JSON.stringify(sub.promptHighlights)
          : null,
        scratchNotes: sub.scratchNotes || null,
        createdAt: now,
      });

      const { payload, providerUsed, modelUsed } = await evaluateEssayWithAI({
        taskType: sub.taskType,
        promptTitle: prompt.title,
        promptText: prompt.promptText,
        visualContext,
        essayText: sub.essayText,
        draftVersion: nextVersion,
        previousBand,
      });

      const evalId = `eval-${crypto.randomUUID().slice(0, 10)}`;
      await db.insert(schema.evaluations).values({
        id: evalId,
        draftId,
        overallBand: payload.overallBand,
        taTrBand: payload.taTrBand,
        ccBand: payload.ccBand,
        lrBand: payload.lrBand,
        graBand: payload.graBand,
        examinerSummary: payload.examinerSummary,
        criterionFeedbackJson: JSON.stringify(payload.criteria),
        inlineAnnotationsJson: JSON.stringify(payload.inlineAnnotations),
        targetBandRewrite: payload.targetBandRewrite,
        rewriteKeyChangesJson: JSON.stringify(payload.rewriteKeyChanges),
        topPrioritiesJson: JSON.stringify(payload.topPriorities),
        providerUsed,
        modelUsed,
        createdAt: now,
      });

      // Auto-populate Personal Error & Collocation Vault
      for (const v of payload.vaultItems) {
        const key = v.originalText.trim().toLowerCase();
        if (!key || !v.upgradedText.trim() || knownOriginals.has(key)) continue;
        knownOriginals.add(key);

        await db.insert(schema.vaultItems).values({
          id: `vault-${crypto.randomUUID().slice(0, 9)}`,
          sourceDraftId: draftId,
          itemType: v.itemType,
          criterion: v.criterion,
          originalText: v.originalText.trim(),
          upgradedText: v.upgradedText.trim(),
          explanation: v.explanation.trim(),
          exampleSentence: v.exampleSentence.trim() || v.upgradedText.trim(),
          masteryLevel: 0,
          reviewCount: 0,
          nextReviewAt: now,
          createdAt: now,
        });
      }

      gradedResults.push({
        draftId,
        version: nextVersion,
        taskType: sub.taskType,
        wordCount,
        evaluationId: evalId,
        providerUsed,
        modelUsed,
        payload,
      });
    }

    // Recompute session status & official 1:2 weighted band if both Task 1 & Task 2 exist
    const allSessionDrafts = await db
      .select()
      .from(schema.drafts)
      .where(eq(schema.drafts.sessionId, body.sessionId))
      .orderBy(desc(schema.drafts.version));

    const latestT1 = allSessionDrafts.find((d) => d.taskType === "task1");
    const latestT2 = allSessionDrafts.find((d) => d.taskType === "task2");

    let weightedBand: number | null = null;
    if (latestT1 && latestT2) {
      const [e1] = await db
        .select()
        .from(schema.evaluations)
        .where(eq(schema.evaluations.draftId, latestT1.id));
      const [e2] = await db
        .select()
        .from(schema.evaluations)
        .where(eq(schema.evaluations.draftId, latestT2.id));
      if (e1 && e2) {
        weightedBand = computeWeightedExamBand(e1.overallBand, e2.overallBand);
      }
    } else if (gradedResults[0]) {
      weightedBand = gradedResults[0].payload.overallBand;
    }

    await db
      .update(schema.writingSessions)
      .set({
        status: "completed",
        completedAt: now,
        totalSecondsSpent:
          body.totalSecondsSpent ??
          allSessionDrafts.reduce((acc, d) => acc + d.secondsSpent, 0),
        weightedBand,
      })
      .where(eq(schema.writingSessions.id, body.sessionId));

    return NextResponse.json({
      sessionId: body.sessionId,
      weightedBand,
      results: gradedResults,
    });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to evaluate draft." },
      { status: 500 }
    );
  }
}
