import { NextResponse } from "next/server";
import { and, desc, eq } from "drizzle-orm";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { getOwnedSession, getVisiblePrompt } from "@/lib/db/scope";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { evaluateEssayWithAI, extractVisualSpecFromImage } from "@/lib/ai/examiner";
import { computeWeightedExamBand, countEssayWords } from "@/lib/ielts-utils";
import type {
  OutlineFeedback,
  OutlineInput,
  TaskType,
  ToolkitResponse,
} from "@/lib/types";

export const maxDuration = 60;

interface SingleDraftSubmission {
  promptId: string;
  taskType: TaskType;
  essayText: string;
  secondsSpent?: number;
  outline?: OutlineInput | null;
  outlineFeedback?: OutlineFeedback | null;
  coachingHistory?: ToolkitResponse[] | null;
  promptHighlights?: string[];
  scratchNotes?: string;
}

export async function POST(request: Request) {
  const user = await getCurrentUser();
  if (!user) return unauthorized();
  const userId = user.id;

  let body: {
    sessionId: string;
    submissions: SingleDraftSubmission[];
    totalSecondsSpent?: number;
  };

  try {
    body = await request.json();
  } catch {
    return NextResponse.json(
      { error: "Invalid JSON request payload." },
      { status: 400 }
    );
  }

  if (
    !body.sessionId ||
    !Array.isArray(body.submissions) ||
    body.submissions.length === 0
  ) {
    return NextResponse.json(
      { error: "Session ID and at least one draft submission are required." },
      { status: 400 }
    );
  }

  const db = await getDb();

  if (!(await getOwnedSession(db, userId, body.sessionId))) {
    return NextResponse.json({ error: "Writing session not found." }, { status: 404 });
  }

  // Validate submissions & prompts synchronously before opening NDJSON stream
  const validatedSubmissions: Array<{
    sub: SingleDraftSubmission;
    prompt: typeof schema.prompts.$inferSelect;
  }> = [];

  for (const sub of body.submissions) {
    if (!sub.essayText || sub.essayText.trim().length < 15) {
      return NextResponse.json(
        {
          error: `Please write at least a short draft for ${sub.taskType.toUpperCase()} before submitting.`,
        },
        { status: 400 }
      );
    }

    const prompt = await getVisiblePrompt(db, userId, sub.promptId);

    if (!prompt) {
      return NextResponse.json({ error: "Prompt not found." }, { status: 404 });
    }

    validatedSubmissions.push({ sub, prompt });
  }

  const encoder = new TextEncoder();

  const stream = new ReadableStream<Uint8Array>({
    async start(controller) {
      let closed = false;
      const sendEvent = (event: Record<string, unknown>) => {
        if (closed) return;
        try {
          controller.enqueue(encoder.encode(`${JSON.stringify(event)}\n`));
        } catch {
          closed = true;
        }
      };

      // Flush initial bytes immediately so HTTP 200 headers arrive right away
      sendEvent({
        type: "progress",
        stage: "Examiner reviewing prompt & essay...",
      });

      const keepAliveTimer = setInterval(() => {
        sendEvent({ type: "ping", ts: Date.now() });
      }, 4_000);

      try {
        const now = new Date().toISOString();
        const existingVault = await db
          .select({ originalText: schema.vaultItems.originalText })
          .from(schema.vaultItems)
          .where(eq(schema.vaultItems.userId, userId));
        const knownOriginals = new Set(
          existingVault.map((v) => v.originalText.trim().toLowerCase())
        );

        const gradedResults = [];

        for (let i = 0; i < validatedSubmissions.length; i++) {
          const { sub, prompt } = validatedSubmissions[i];
          const taskPrefix =
            validatedSubmissions.length > 1
              ? `[Part ${i + 1}/${validatedSubmissions.length}] `
              : "";

          // Resolve visual ground truth (caching vision extraction for uploaded Task 1 images)
          let visualContext: string | null =
            prompt.chartSpecJson || prompt.extractedVisualSpec || null;

          if (!visualContext && prompt.imageDataUrl) {
            sendEvent({
              type: "progress",
              stage: `${taskPrefix}Extracting chart data from uploaded image...`,
            });
            visualContext = await extractVisualSpecFromImage(
              prompt.imageDataUrl,
              prompt.promptText,
              userId
            );
            await db
              .update(schema.prompts)
              .set({ extractedVisualSpec: visualContext })
              .where(eq(schema.prompts.id, prompt.id));
          }

          // Inspect prior drafts for this (sessionId, promptId), distinguishing evaluated drafts from interrupted orphans
          const priorDrafts = await db
            .select()
            .from(schema.drafts)
            .where(
              and(
                eq(schema.drafts.userId, userId),
                eq(schema.drafts.sessionId, body.sessionId),
                eq(schema.drafts.promptId, sub.promptId)
              )
            )
            .orderBy(desc(schema.drafts.version));

          const evaluatedPriorDrafts: Array<{
            version: number;
            overallBand: number;
          }> = [];
          const orphanDraftIds: string[] = [];

          for (const d of priorDrafts) {
            const [ev] = await db
              .select({ overallBand: schema.evaluations.overallBand })
              .from(schema.evaluations)
              .where(
                and(
                  eq(schema.evaluations.draftId, d.id),
                  eq(schema.evaluations.userId, userId)
                )
              );

            if (ev) {
              evaluatedPriorDrafts.push({
                version: d.version,
                overallBand: ev.overallBand,
              });
            } else {
              orphanDraftIds.push(d.id);
            }
          }

          const nextVersion = (evaluatedPriorDrafts[0]?.version ?? 0) + 1;
          const previousBand = evaluatedPriorDrafts[0]?.overallBand ?? null;
          const wordCount = countEssayWords(sub.essayText);

          // Run AI evaluation BEFORE writing draft row so interrupted attempts never leave orphan drafts
          const { payload, providerUsed, modelUsed, costUsd } = await evaluateEssayWithAI({
            userId,
            taskType: sub.taskType,
            promptTitle: prompt.title,
            promptText: prompt.promptText,
            visualContext,
            essayText: sub.essayText,
            draftVersion: nextVersion,
            previousBand,
            outline: sub.outline,
            outlineFeedback: sub.outlineFeedback,
            coachingHistory: sub.coachingHistory,
            onProgress: (stage) =>
              sendEvent({ type: "progress", stage: `${taskPrefix}${stage}` }),
          });

          sendEvent({
            type: "progress",
            stage: `${taskPrefix}Saving band scores & marginalia...`,
          });

          // Clean up any historical un-evaluated orphan drafts for this (sessionId, promptId)
          for (const orphanId of orphanDraftIds) {
            await db
              .delete(schema.drafts)
              .where(and(eq(schema.drafts.id, orphanId), eq(schema.drafts.userId, userId)));
          }

          const draftId = `draft-${crypto.randomUUID().slice(0, 10)}`;
          await db.insert(schema.drafts).values({
            id: draftId,
            userId,
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

          const evalId = `eval-${crypto.randomUUID().slice(0, 10)}`;
          await db.insert(schema.evaluations).values({
            id: evalId,
            userId,
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
            costUsd: costUsd ?? null,
            createdAt: now,
          });

          // Auto-populate Personal Error & Collocation Vault
          for (const v of payload.vaultItems) {
            const key = v.originalText.trim().toLowerCase();
            if (!key || !v.upgradedText.trim() || knownOriginals.has(key)) continue;
            knownOriginals.add(key);

            await db.insert(schema.vaultItems).values({
              id: `vault-${crypto.randomUUID().slice(0, 9)}`,
              userId,
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
            costUsd: costUsd ?? null,
            payload,
          });
        }

        // Recompute session status & official 1:2 weighted band from evaluated drafts
        const allSessionDrafts = await db
          .select()
          .from(schema.drafts)
          .where(
            and(
              eq(schema.drafts.sessionId, body.sessionId),
              eq(schema.drafts.userId, userId)
            )
          )
          .orderBy(desc(schema.drafts.version));

        let t1EvalBand: number | null = null;
        let t2EvalBand: number | null = null;

        for (const d of allSessionDrafts) {
          if (d.taskType === "task1" && t1EvalBand === null) {
            const [e1] = await db
              .select({ overallBand: schema.evaluations.overallBand })
              .from(schema.evaluations)
              .where(
                and(
                  eq(schema.evaluations.draftId, d.id),
                  eq(schema.evaluations.userId, userId)
                )
              );
            if (e1) t1EvalBand = e1.overallBand;
          } else if (d.taskType === "task2" && t2EvalBand === null) {
            const [e2] = await db
              .select({ overallBand: schema.evaluations.overallBand })
              .from(schema.evaluations)
              .where(
                and(
                  eq(schema.evaluations.draftId, d.id),
                  eq(schema.evaluations.userId, userId)
                )
              );
            if (e2) t2EvalBand = e2.overallBand;
          }
        }

        let weightedBand: number | null = null;
        if (t1EvalBand !== null && t2EvalBand !== null) {
          weightedBand = computeWeightedExamBand(t1EvalBand, t2EvalBand);
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
          .where(
            and(
              eq(schema.writingSessions.id, body.sessionId),
              eq(schema.writingSessions.userId, userId)
            )
          );

        sendEvent({
          type: "result",
          sessionId: body.sessionId,
          weightedBand,
          results: gradedResults,
        });
      } catch (err) {
        const message =
          err instanceof Error ? err.message : "Failed to evaluate draft.";
        const isTimeout =
          err instanceof Error &&
          (err.name === "TimeoutError" ||
            err.name === "AbortError" ||
            /timed?\s*out/i.test(message));

        sendEvent({
          type: "error",
          isTimeout,
          error: isTimeout
            ? "Grading took too long, please try again. Your essay is safely preserved in the editor."
            : message,
        });
      } finally {
        clearInterval(keepAliveTimer);
        if (!closed) {
          closed = true;
          try {
            controller.close();
          } catch {
            // Stream already closed by client
          }
        }
      }
    },
  });

  return new Response(stream, {
    status: 200,
    headers: {
      "Content-Type": "application/x-ndjson; charset=utf-8",
      "Cache-Control": "no-cache, no-transform",
      "X-Accel-Buffering": "no",
    },
  });
}
