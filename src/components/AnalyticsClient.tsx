"use client";

import Link from "next/link";
import {
  ResponsiveContainer,
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ReferenceLine,
} from "recharts";
import { ArrowUpRight, Target, Zap } from "lucide-react";
import type { CriterionKey } from "@/lib/types";

interface TrendPoint {
  index: number;
  date: string;
  label: string;
  overall: number;
  taTr: number;
  cc: number;
  lr: number;
  gra: number;
}

const CRITERION_NAMES: Record<CriterionKey, string> = {
  TA_TR: "Task Achievement / Response",
  CC: "Coherence & Cohesion",
  LR: "Lexical Resource",
  GRA: "Grammatical Range & Accuracy",
};

export function AnalyticsClient({
  targetBand,
  totalEssaysGraded,
  totalWordsWritten,
  averages,
  weakestCriterion,
  trendPoints,
  sessionRows,
}: {
  targetBand: number;
  totalEssaysGraded: number;
  totalWordsWritten: number;
  averages: {
    overall: number;
    TA_TR: number;
    CC: number;
    LR: number;
    GRA: number;
  };
  weakestCriterion: CriterionKey;
  trendPoints: TrendPoint[];
  sessionRows: Array<{
    id: string;
    mode: string;
    status: string;
    startedAt: string;
    weightedBand: number | null;
    title: string;
    draftCount: number;
    wordCount: number;
  }>;
}) {
  return (
    <div className="max-w-6xl mx-auto px-4 sm:px-6 pt-7 space-y-7">
      <header className="space-y-1.5 pb-5 border-b border-line">
        <span className="text-xs font-mono uppercase tracking-wider text-accent font-semibold">
          Diagnostic Trajectory · Target Band {targetBand.toFixed(1)}
        </span>
        <h1 className="font-serif text-3xl font-semibold text-ink">
          4-Criterion Band Progression
        </h1>
        <p className="text-sm text-ink-secondary max-w-2xl">
          Track how each of the four equally weighted IELTS Writing criteria evolves
          across timed simulations and multi-draft revisions.
        </p>
      </header>

      {/* Top Key Metrics Row */}
      <section className="grid grid-cols-2 lg:grid-cols-4 gap-4">
        <div className="editorial-surface rounded-2xl p-5">
          <span className="text-[11px] font-mono uppercase text-ink-muted">
            Mean Band Score
          </span>
          <p className="font-mono text-2xl font-bold text-ink mt-1 tabular-nums">
            {totalEssaysGraded > 0 ? averages.overall.toFixed(1) : "—"}
          </p>
          <span className="text-xs text-ink-secondary">
            Target: {targetBand.toFixed(1)}
          </span>
        </div>

        <div className="editorial-surface rounded-2xl p-5">
          <span className="text-[11px] font-mono uppercase text-ink-muted">
            Graded Drafts
          </span>
          <p className="font-mono text-2xl font-bold text-ink mt-1 tabular-nums">
            {totalEssaysGraded}
          </p>
          <span className="text-xs text-ink-secondary">Includes v2/v3 redrafts</span>
        </div>

        <div className="editorial-surface rounded-2xl p-5">
          <span className="text-[11px] font-mono uppercase text-ink-muted">
            Academic Words Written
          </span>
          <p className="font-mono text-2xl font-bold text-ink mt-1 tabular-nums">
            {totalWordsWritten.toLocaleString()}
          </p>
          <span className="text-xs text-ink-secondary">Cumulative output</span>
        </div>

        <div className="editorial-surface rounded-2xl p-5 flex flex-col justify-between">
          <div>
            <span className="text-[11px] font-mono uppercase text-warm font-semibold">
              Primary Bottleneck
            </span>
            <p className="font-sans text-sm font-bold text-ink mt-1">
              {CRITERION_NAMES[weakestCriterion]}
            </p>
          </div>
          <Link
            href={`/drills?focus=${weakestCriterion}`}
            className="inline-flex items-center gap-1 text-xs font-semibold text-accent hover:underline mt-2"
          >
            <Zap className="w-3.5 h-3.5" />
            <span>Run 5-Min Fix Drill →</span>
          </Link>
        </div>
      </section>

      {/* Band Trajectory Chart + 4-Criterion Breakdown */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
        <section className="lg:col-span-8 editorial-surface rounded-2xl p-6 space-y-4">
          <div className="flex items-center justify-between">
            <h2 className="font-serif text-lg font-semibold text-ink">
              Score Trajectory Over Time
            </h2>
            <span className="inline-flex items-center gap-1 text-xs font-mono text-accent">
              <Target className="w-3.5 h-3.5" /> Goal line: {targetBand.toFixed(1)}
            </span>
          </div>

          {trendPoints.length === 0 ? (
            <div className="h-64 flex flex-col items-center justify-center text-center px-4">
              <p className="text-sm text-ink-secondary">
                Submit your first Task 1 or Task 2 draft to unlock your 4-criterion
                band curve.
              </p>
              <Link
                href="/"
                className="mt-3 px-4 py-2 rounded-xl bg-accent text-accent-fg text-xs font-semibold"
              >
                Choose a Practice Prompt
              </Link>
            </div>
          ) : (
            <div style={{ width: "100%", height: 290 }}>
              <ResponsiveContainer width="100%" height="100%">
                <LineChart
                  data={trendPoints}
                  margin={{ top: 10, right: 18, left: -8, bottom: 4 }}
                >
                  <CartesianGrid strokeDasharray="3 3" stroke="var(--border-subtle)" />
                  <XAxis dataKey="label" tick={{ fontSize: 11 }} />
                  <YAxis domain={[4.5, 9]} ticks={[5, 6, 6.5, 7, 7.5, 8, 8.5, 9]} tick={{ fontSize: 11 }} />
                  <Tooltip
                    contentStyle={{
                      borderRadius: "2px",
                      background: "var(--bg-elevated)",
                      border: "1px solid var(--border-strong)",
                      color: "var(--ink-primary)",
                      fontSize: "12px",
                    }}
                  />
                  <Legend wrapperStyle={{ fontSize: "12px" }} />
                  <ReferenceLine
                    y={targetBand}
                    stroke="var(--accent-primary)"
                    strokeDasharray="4 4"
                  />
                  <Line
                    type="monotone"
                    dataKey="overall"
                    name="Overall Band"
                    stroke="var(--ink-primary)"
                    strokeWidth={3}
                    dot={{ r: 4 }}
                  />
                  <Line
                    type="monotone"
                    dataKey="taTr"
                    name="TA/TR"
                    stroke="var(--crit-tatr)"
                    strokeWidth={1.5}
                  />
                  <Line
                    type="monotone"
                    dataKey="cc"
                    name="Cohesion"
                    stroke="var(--crit-cc)"
                    strokeWidth={1.5}
                  />
                  <Line
                    type="monotone"
                    dataKey="lr"
                    name="Lexical"
                    stroke="var(--crit-lr)"
                    strokeWidth={1.5}
                  />
                  <Line
                    type="monotone"
                    dataKey="gra"
                    name="Grammar"
                    stroke="var(--crit-gra)"
                    strokeWidth={1.5}
                  />
                </LineChart>
              </ResponsiveContainer>
            </div>
          )}
        </section>

        <section className="lg:col-span-4 editorial-surface rounded-2xl p-6 space-y-4">
          <h2 className="font-serif text-lg font-semibold text-ink">
            Criterion Averages
          </h2>
          <div className="space-y-4">
            {(["TA_TR", "CC", "LR", "GRA"] as const).map((key) => {
              const val = averages[key];
              const pct = Math.min(100, Math.round((val / 9) * 100));
              return (
                <div key={key} className="space-y-1.5">
                  <div className="flex items-center justify-between text-xs">
                    <span className="font-medium text-ink">
                      {CRITERION_NAMES[key]}
                    </span>
                    <span className="font-mono font-bold tabular-nums text-ink">
                      {totalEssaysGraded > 0 ? val.toFixed(2) : "—"}
                    </span>
                  </div>
                  <div className="h-2 rounded-full bg-subtle overflow-hidden">
                    <div
                      className="h-full bg-accent transition-all"
                      style={{ width: `${pct}%` }}
                    />
                  </div>
                </div>
              );
            })}
          </div>
        </section>
      </div>

      {/* Complete Session Archive */}
      <section className="editorial-surface rounded-2xl p-6 space-y-4">
        <h2 className="font-serif text-lg font-semibold text-ink">
          Writing Session Archive
        </h2>

        {sessionRows.length === 0 ? (
          <p className="text-xs text-ink-muted">No sessions recorded yet.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left border-collapse text-xs sm:text-sm tabular-nums">
              <thead>
                <tr className="border-b border-line text-ink-muted font-mono text-[11px] uppercase">
                  <th className="py-2.5 pr-4">Date</th>
                  <th className="py-2.5 px-3">Mode</th>
                  <th className="py-2.5 px-3">Prompt</th>
                  <th className="py-2.5 px-3">Drafts</th>
                  <th className="py-2.5 px-3">Words</th>
                  <th className="py-2.5 px-3">Band</th>
                  <th className="py-2.5 pl-3 text-right">Action</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-line">
                {sessionRows.map((row) => {
                  const completed = row.status === "completed";
                  return (
                    <tr key={row.id} className="hover:bg-subtle/50">
                      <td className="py-3 pr-4 font-mono text-xs text-ink-secondary">
                        {row.startedAt.slice(0, 10)}
                      </td>
                      <td className="py-3 px-3 text-xs text-ink-secondary">
                        {row.mode === "coach"
                          ? "Coach Studio"
                          : row.mode === "simulator_full_60m"
                          ? "60m Full Exam"
                          : "Timed Exam"}
                      </td>
                      <td className="py-3 px-3 font-medium text-ink max-w-xs truncate">
                        {row.title}
                      </td>
                      <td className="py-3 px-3 font-mono text-xs text-ink-secondary">
                        v{Math.max(1, row.draftCount)}
                      </td>
                      <td className="py-3 px-3 font-mono text-xs text-ink-secondary">
                        {row.wordCount}
                      </td>
                      <td className="py-3 px-3 font-mono font-bold text-ink">
                        {row.weightedBand !== null
                          ? row.weightedBand.toFixed(1)
                          : "—"}
                      </td>
                      <td className="py-3 pl-3 text-right">
                        <Link
                          href={
                            completed ? `/session/${row.id}` : `/write/${row.id}`
                          }
                          className="inline-flex items-center gap-1 text-xs font-semibold text-accent hover:underline"
                        >
                          <span>{completed ? "Inspect" : "Resume"}</span>
                          <ArrowUpRight className="w-3.5 h-3.5" />
                        </Link>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
