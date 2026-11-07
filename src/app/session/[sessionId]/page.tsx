import { notFound } from "next/navigation";
import { asc, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { isPasscodeRequired } from "@/lib/auth";
import { AppShell } from "@/components/AppShell";
import {
  EvaluationWorkbench,
  type HydratedDraft,
} from "@/components/EvaluationWorkbench";

export const dynamic = "force-dynamic";

export default async function SessionEvaluationPage({
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

  const rawDrafts = await db
    .select()
    .from(schema.drafts)
    .where(eq(schema.drafts.sessionId, sessionId))
    .orderBy(asc(schema.drafts.version));

  const hydratedDrafts: HydratedDraft[] = await Promise.all(
    rawDrafts.map(async (d) => {
      const [prompt] = await db
        .select()
        .from(schema.prompts)
        .where(eq(schema.prompts.id, d.promptId));
      const [ev] = await db
        .select()
        .from(schema.evaluations)
        .where(eq(schema.evaluations.draftId, d.id));
      return {
        ...d,
        prompt: prompt || null,
        evaluation: ev || null,
      };
    })
  );

  return (
    <AppShell passcodeActive={isPasscodeRequired()}>
      <EvaluationWorkbench
        initialSession={{
          id: session.id,
          mode: session.mode,
          weightedBand: session.weightedBand,
          totalSecondsSpent: session.totalSecondsSpent,
          drafts: hydratedDrafts,
        }}
      />
    </AppShell>
  );
}
