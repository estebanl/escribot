import { notFound } from "next/navigation";
import { and, asc, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { requireUser } from "@/lib/session";
import { getOwnedSession, getVisiblePrompt } from "@/lib/db/scope";
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
  const user = await requireUser();
  const db = await getDb();

  const session = await getOwnedSession(db, user.id, sessionId);

  if (!session) {
    notFound();
  }

  const rawDrafts = await db
    .select()
    .from(schema.drafts)
    .where(and(eq(schema.drafts.sessionId, sessionId), eq(schema.drafts.userId, user.id)))
    .orderBy(asc(schema.drafts.version));

  const hydratedDrafts: HydratedDraft[] = await Promise.all(
    rawDrafts.map(async (d) => {
      const prompt = await getVisiblePrompt(db, user.id, d.promptId);
      const [ev] = await db
        .select()
        .from(schema.evaluations)
        .where(
          and(eq(schema.evaluations.draftId, d.id), eq(schema.evaluations.userId, user.id))
        );
      return {
        ...d,
        prompt,
        evaluation: ev || null,
      };
    })
  );

  return (
    <AppShell user={user}>
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
