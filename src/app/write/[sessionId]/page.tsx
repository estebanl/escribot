import { notFound } from "next/navigation";
import { desc, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { isPasscodeRequired } from "@/lib/auth";
import { AppShell } from "@/components/AppShell";
import { CoachWorkspace, type PreviousFocus } from "@/components/CoachWorkspace";
import { InsperaSimulator } from "@/components/InsperaSimulator";
import type { OutlineFeedback, OutlineInput, SessionMode } from "@/lib/types";

export const dynamic = "force-dynamic";

function safeParseJson<T>(raw: string | null | undefined): T | null {
  if (!raw) return null;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

export default async function WriteSessionPage({
  params,
}: {
  params: Promise<{ sessionId: string }>;
}) {
  const { sessionId } = await params;
  const db = await getDb();

  const [session] = await db
    .select()
    .from(schema.writingSessions)
    .where(eq(schema.writingSessions.id, sessionId));

  if (!session) {
    notFound();
  }

  const task1Prompt = session.task1PromptId
    ? (
        await db
          .select()
          .from(schema.prompts)
          .where(eq(schema.prompts.id, session.task1PromptId))
      )[0] || null
    : null;

  const task2Prompt = session.task2PromptId
    ? (
        await db
          .select()
          .from(schema.prompts)
          .where(eq(schema.prompts.id, session.task2PromptId))
      )[0] || null
    : null;

  // Restore any previously saved draft rows for this session (e.g. from an interrupted or in-progress attempt)
  const existingDrafts = await db
    .select()
    .from(schema.drafts)
    .where(eq(schema.drafts.sessionId, sessionId))
    .orderBy(desc(schema.drafts.version));

  if (session.mode === "coach") {
    const activePrompt = task1Prompt || task2Prompt;
    if (!activePrompt) notFound();

    const latestSaved = existingDrafts.find(
      (d) => d.promptId === activePrompt.id
    );

    // Surface the examiner's top priorities from the most recent graded draft of the same task type,
    // preferring this session + prompt (redraft), so the student practises exactly what held them back.
    const gradedSameTask = await db
      .select({
        sessionId: schema.drafts.sessionId,
        promptId: schema.drafts.promptId,
        version: schema.drafts.version,
        overallBand: schema.evaluations.overallBand,
        topPrioritiesJson: schema.evaluations.topPrioritiesJson,
      })
      .from(schema.evaluations)
      .innerJoin(schema.drafts, eq(schema.evaluations.draftId, schema.drafts.id))
      .where(eq(schema.drafts.taskType, activePrompt.taskType))
      .orderBy(desc(schema.evaluations.createdAt))
      .limit(25);

    const focusSource =
      gradedSameTask.find(
        (g) => g.sessionId === session.id && g.promptId === activePrompt.id
      ) || gradedSameTask[0];
    const focusPriorities = focusSource
      ? (safeParseJson<string[]>(focusSource.topPrioritiesJson) || []).filter(
          (p) => typeof p === "string" && p.trim()
        )
      : [];
    const previousFocus: PreviousFocus | null =
      focusSource && focusPriorities.length > 0
        ? {
            overallBand: focusSource.overallBand,
            topPriorities: focusPriorities,
            fromVersion: focusSource.version,
            isRedraft:
              focusSource.sessionId === session.id &&
              focusSource.promptId === activePrompt.id,
          }
        : null;

    return (
      <AppShell passcodeActive={isPasscodeRequired()}>
        <CoachWorkspace
          sessionId={session.id}
          prompt={activePrompt}
          previousFocus={previousFocus}
          initialDraft={
            latestSaved
              ? {
                  essayText: latestSaved.essayText,
                  secondsSpent: latestSaved.secondsSpent,
                  outline: safeParseJson<OutlineInput>(latestSaved.outlineJson),
                  outlineFeedback: safeParseJson<OutlineFeedback>(
                    latestSaved.outlineFeedbackJson
                  ),
                }
              : null
          }
        />
      </AppShell>
    );
  }

  const latestT1 = task1Prompt
    ? existingDrafts.find((d) => d.promptId === task1Prompt.id)
    : undefined;
  const latestT2 = task2Prompt
    ? existingDrafts.find((d) => d.promptId === task2Prompt.id)
    : undefined;

  // Authentic Full-Screen Computer-Delivered IELTS (Inspera) Simulator
  return (
    <InsperaSimulator
      sessionId={session.id}
      mode={session.mode as SessionMode}
      task1Prompt={task1Prompt}
      task2Prompt={task2Prompt}
      initialDrafts={{
        task1: latestT1
          ? {
              essayText: latestT1.essayText,
              secondsSpent: latestT1.secondsSpent,
              promptHighlights:
                safeParseJson<string[]>(latestT1.promptHighlightsJson) || [],
              scratchNotes: latestT1.scratchNotes,
            }
          : null,
        task2: latestT2
          ? {
              essayText: latestT2.essayText,
              secondsSpent: latestT2.secondsSpent,
              promptHighlights:
                safeParseJson<string[]>(latestT2.promptHighlightsJson) || [],
              scratchNotes: latestT2.scratchNotes,
            }
          : null,
      }}
    />
  );
}
