import { desc, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { requireUser } from "@/lib/session";
import { visiblePrompts } from "@/lib/db/scope";
import { resolveUserAndAIConfig } from "@/lib/ai/providers";
import { AppShell } from "@/components/AppShell";
import { AnalyticsClient } from "@/components/AnalyticsClient";
import type { CriterionKey } from "@/lib/types";

export const dynamic = "force-dynamic";

export default async function AnalyticsPage() {
  const user = await requireUser();
  const db = await getDb();
  const cfg = await resolveUserAndAIConfig(user.id);

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

  const evaluatedIds = new Set(allEvals.map((e) => e.draftId));
  const evaluatedDrafts = allDrafts.filter((d) => evaluatedIds.has(d.id));

  const totalWordsWritten = evaluatedDrafts.reduce((acc, d) => acc + d.wordCount, 0);

  const sessionRows = sessions.map((s) => {
    const sDrafts = evaluatedDrafts.filter((d) => d.sessionId === s.id);
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

  const now = new Date();
  const currentMonthPrefix = now.toISOString().slice(0, 7); // "YYYY-MM"
  const allUsage = await db
    .select()
    .from(schema.aiUsage)
    .where(eq(schema.aiUsage.userId, user.id));
  const monthlyUsage = allUsage.filter((u) => u.createdAt.startsWith(currentMonthPrefix));
  const usageSpend = monthlyUsage.reduce((acc, u) => acc + (u.costUsd || 0), 0);
  const evalsSpend = allEvals
    .filter((e) => e.createdAt.startsWith(currentMonthPrefix))
    .reduce((acc, e) => acc + (e.costUsd || 0), 0);
  const monthlySpendUsd = Number(Math.max(usageSpend, evalsSpend).toFixed(4));

  const featureSpend: Record<string, number> = {};
  for (const u of monthlyUsage) {
    featureSpend[u.feature] = (featureSpend[u.feature] || 0) + (u.costUsd || 0);
  }

  return (
    <AppShell user={user}>
      <AnalyticsClient
        targetBand={cfg.targetBand}
        totalEssaysGraded={allEvals.length}
        totalWordsWritten={totalWordsWritten}
        averages={averages}
        weakestCriterion={weakestCriterion}
        trendPoints={trendPoints}
        sessionRows={sessionRows}
        monthlySpendUsd={monthlySpendUsd}
        usageCallsCount={monthlyUsage.length}
        featureSpend={featureSpend}
      />
    </AppShell>
  );
}
