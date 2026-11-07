import { desc } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { isPasscodeRequired } from "@/lib/auth";
import { resolveUserAndAIConfig } from "@/lib/ai/providers";
import { AppShell } from "@/components/AppShell";
import { AnalyticsClient } from "@/components/AnalyticsClient";
import type { CriterionKey } from "@/lib/types";

export const dynamic = "force-dynamic";

export default async function AnalyticsPage() {
  const db = await getDb();
  const cfg = await resolveUserAndAIConfig();

  const sessions = await db
    .select()
    .from(schema.writingSessions)
    .orderBy(desc(schema.writingSessions.startedAt));

  const allDrafts = await db
    .select()
    .from(schema.drafts)
    .orderBy(desc(schema.drafts.createdAt));

  const allEvals = await db
    .select()
    .from(schema.evaluations)
    .orderBy(desc(schema.evaluations.createdAt));

  const allPrompts = await db.select().from(schema.prompts);
  const promptMap = new Map(allPrompts.map((p) => [p.id, p]));

  const chronological = [...allEvals].reverse();
  const trendPoints = chronological.map((ev, i) => {
    const draft = allDrafts.find((d) => d.id === ev.draftId);
    const prompt = draft ? promptMap.get(draft.promptId) : null;
    return {
      index: i + 1,
      date: ev.createdAt.slice(0, 10),
      label: prompt
        ? `${prompt.taskType === "task1" ? "T1" : "T2"} v${draft?.version ?? 1}`
        : `#${i + 1}`,
      overall: ev.overallBand,
      taTr: ev.taTrBand,
      cc: ev.ccBand,
      lr: ev.lrBand,
      gra: ev.graBand,
    };
  });

  let averages = { overall: 0, TA_TR: 0, CC: 0, LR: 0, GRA: 0 };
  let weakestCriterion: CriterionKey = "CC";

  if (allEvals.length > 0) {
    const n = allEvals.length;
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
    averages = {
      overall: Number((sum.overall / n).toFixed(2)),
      TA_TR: Number((sum.TA_TR / n).toFixed(2)),
      CC: Number((sum.CC / n).toFixed(2)),
      LR: Number((sum.LR / n).toFixed(2)),
      GRA: Number((sum.GRA / n).toFixed(2)),
    };
    const pairs: Array<[CriterionKey, number]> = [
      ["TA_TR", averages.TA_TR],
      ["CC", averages.CC],
      ["LR", averages.LR],
      ["GRA", averages.GRA],
    ];
    pairs.sort((a, b) => a[1] - b[1]);
    weakestCriterion = pairs[0][0];
  }

  const totalWordsWritten = allDrafts.reduce((acc, d) => acc + d.wordCount, 0);

  const sessionRows = sessions.map((s) => {
    const sDrafts = allDrafts.filter((d) => d.sessionId === s.id);
    const t2 = s.task2PromptId ? promptMap.get(s.task2PromptId)?.title : null;
    const t1 = s.task1PromptId ? promptMap.get(s.task1PromptId)?.title : null;
    return {
      id: s.id,
      mode: s.mode,
      status: s.status,
      startedAt: s.startedAt,
      weightedBand: s.weightedBand,
      title: t2 && t1 ? `${t1} + ${t2}` : t2 || t1 || "Writing Practice",
      draftCount: sDrafts.length,
      wordCount: sDrafts.reduce((acc, d) => acc + d.wordCount, 0),
    };
  });

  return (
    <AppShell passcodeActive={isPasscodeRequired()}>
      <AnalyticsClient
        targetBand={cfg.targetBand}
        totalEssaysGraded={allEvals.length}
        totalWordsWritten={totalWordsWritten}
        averages={averages}
        weakestCriterion={weakestCriterion}
        trendPoints={trendPoints}
        sessionRows={sessionRows}
      />
    </AppShell>
  );
}
