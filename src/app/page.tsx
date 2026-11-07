import { desc, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { isPasscodeRequired } from "@/lib/auth";
import { resolveUserAndAIConfig } from "@/lib/ai/providers";
import { computeDaysUntilExam } from "@/lib/ielts-utils";
import { AppShell } from "@/components/AppShell";
import { StudioHomeClient } from "@/components/StudioHomeClient";

export const dynamic = "force-dynamic";

export default async function StudioHomePage() {
  const db = await getDb();
  const aiCfg = await resolveUserAndAIConfig();

  const allPrompts = await db
    .select()
    .from(schema.prompts)
    .orderBy(desc(schema.prompts.createdAt));

  const sessions = await db
    .select()
    .from(schema.writingSessions)
    .orderBy(desc(schema.writingSessions.startedAt))
    .limit(10);

  const allDrafts = await db
    .select()
    .from(schema.drafts)
    .orderBy(desc(schema.drafts.version));

  const promptMap = new Map(allPrompts.map((p) => [p.id, p]));

  const recentSessions = sessions.map((s) => {
    const sDrafts = allDrafts.filter((d) => d.sessionId === s.id);
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

  const [settingsRow] = await db
    .select()
    .from(schema.userSettings)
    .where(eq(schema.userSettings.id, 1));

  const daysUntilExam = computeDaysUntilExam(settingsRow?.examDate);

  return (
    <AppShell passcodeActive={isPasscodeRequired()}>
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
