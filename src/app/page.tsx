import { redirect } from "next/navigation";
import { desc, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { getCurrentUser, requireUser } from "@/lib/session";
import { LandingPage } from "@/components/LandingPage";
import { getOrCreateSettings, visiblePrompts } from "@/lib/db/scope";
import { resolveUserAndAIConfig } from "@/lib/ai/providers";
import { computeDaysUntilExam } from "@/lib/ielts-utils";
import { AppShell } from "@/components/AppShell";
import { StudioHomeClient } from "@/components/StudioHomeClient";

export const dynamic = "force-dynamic";

export default async function HomePage({
  searchParams,
}: {
  searchParams: Promise<{ deleted?: string }>;
}) {
  // Signed-out visitors get the marketing page; signed-in users the studio.
  if (!(await getCurrentUser())) {
    const { deleted } = await searchParams;
    return <LandingPage deleted={deleted === "1"} />;
  }

  const user = await requireUser();
  const db = await getDb();
  const settingsRow = await getOrCreateSettings(db, user.id);
  if (!settingsRow.onboardedAt) redirect("/welcome");
  const aiCfg = await resolveUserAndAIConfig(user.id);

  const allPrompts = await db
    .select()
    .from(schema.prompts)
    .where(visiblePrompts(user.id))
    .orderBy(desc(schema.prompts.createdAt));

  const sessions = await db
    .select()
    .from(schema.writingSessions)
    .where(eq(schema.writingSessions.userId, user.id))
    .orderBy(desc(schema.writingSessions.startedAt))
    .limit(10);

  const allDrafts = await db
    .select()
    .from(schema.drafts)
    .where(eq(schema.drafts.userId, user.id))
    .orderBy(desc(schema.drafts.version));

  const allEvals = await db
    .select({ draftId: schema.evaluations.draftId })
    .from(schema.evaluations)
    .where(eq(schema.evaluations.userId, user.id));
  const evaluatedDraftIds = new Set(allEvals.map((e) => e.draftId));

  const promptMap = new Map(allPrompts.map((p) => [p.id, p]));

  const recentSessions = sessions.map((s) => {
    const sDrafts = allDrafts.filter(
      (d) => d.sessionId === s.id && evaluatedDraftIds.has(d.id)
    );
    const maxVersion = sDrafts.reduce((m, d) => Math.max(m, d.version), 0);
    return {
      id: s.id,
      mode: s.mode,
      status: s.status,
      startedAt: s.startedAt,
      weightedBand: s.weightedBand,
      task1Title: s.task1PromptId ? promptMap.get(s.task1PromptId)?.title : null,
      task2Title: s.task2PromptId ? promptMap.get(s.task2PromptId)?.title : null,
      latestDraftVersion: maxVersion,
    };
  });

  const daysUntilExam = computeDaysUntilExam(settingsRow?.examDate);

  return (
    <AppShell user={user}>
      <StudioHomeClient
        initialPrompts={allPrompts}
        recentSessions={recentSessions}
        settingsSummary={{
          currentBand: aiCfg.currentBand,
          targetBand: aiCfg.targetBand,
          daysUntilExam,
          nativeLanguage: aiCfg.nativeLanguage,
          l1CoachingEnabled: aiCfg.l1CoachingEnabled,
          activeProvider: aiCfg.provider,
          activeProviderReady: Boolean(aiCfg.apiKey),
        }}
      />
    </AppShell>
  );
}
