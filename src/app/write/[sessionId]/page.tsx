import { notFound } from "next/navigation";
import { eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { isPasscodeRequired } from "@/lib/auth";
import { AppShell } from "@/components/AppShell";
import { CoachWorkspace } from "@/components/CoachWorkspace";
import { InsperaSimulator } from "@/components/InsperaSimulator";
import type { SessionMode } from "@/lib/types";

export const dynamic = "force-dynamic";

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

  if (session.mode === "coach") {
    const activePrompt = task1Prompt || task2Prompt;
    if (!activePrompt) notFound();
    return (
      <AppShell passcodeActive={isPasscodeRequired()}>
        <CoachWorkspace sessionId={session.id} prompt={activePrompt} />
      </AppShell>
    );
  }

  // Authentic Full-Screen Computer-Delivered IELTS (Inspera) Simulator
  return (
    <InsperaSimulator
      sessionId={session.id}
      mode={session.mode as SessionMode}
      task1Prompt={task1Prompt}
      task2Prompt={task2Prompt}
    />
  );
}
