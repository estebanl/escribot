"use client";

import { useEffect, useState } from "react";
import {
  Clock,
  Lightbulb,
  Loader2,
  Play,
  RotateCcw,
  Send,
  Sparkles,
  CheckCircle2,
} from "lucide-react";
import { TaskVisualRenderer } from "./TaskVisualRenderer";
import { countEssayWords } from "@/lib/ielts-utils";
import type {
  DrillType,
  MicroDrillChallenge,
  MicroDrillScore,
} from "@/lib/types";

const DRILL_CARDS: Array<{
  type: DrillType;
  badge: string;
  title: string;
  duration: string;
  description: string;
}> = [
  {
    type: "overview_sprint",
    badge: "TA · Task 1",
    title: "4-Minute Task 1 Overview Sprint",
    duration: "4 min",
    description:
      "Read a fresh data chart and draft ONLY a 2-sentence macro Overview capturing main trends and extremes without raw statistics.",
  },
  {
    type: "cohesion",
    badge: "CC · Cohesion",
    title: "Cohesion De-Mechanizer",
    duration: "5 min",
    description:
      "Eliminate robotic 'Firstly / Moreover / On the other hand' transitions using demonstrative nominalisation and thematic links.",
  },
  {
    type: "l1_surgeon",
    badge: "L1 · Transfer",
    title: "L1 Clause & False-Cognate Surgeon",
    duration: "5 min",
    description:
      "Spot and repair false friends ('actually' vs 'currently'), comma-spliced 'however' clauses, and preposition/article calques.",
  },
  {
    type: "lexical_hedging",
    badge: "LR · Register",
    title: "Academic Hedging & Lexical Upgrade",
    duration: "5 min",
    description:
      "Transform sweeping, informal Band 6 claims into nuanced, academically hedged C1/C2 arguments.",
  },
];

export function DrillsClient({
  recommendedDrill,
}: {
  recommendedDrill: DrillType;
}) {
  const [selectedType, setSelectedType] = useState<DrillType>(recommendedDrill);
  const [loadingChallenge, setLoadingChallenge] = useState(false);
  const [drillId, setDrillId] = useState<string | null>(null);
  const [challenge, setChallenge] = useState<MicroDrillChallenge | null>(null);
  const [response, setResponse] = useState("");
  const [secondsLeft, setSecondsLeft] = useState(0);
  const [showHints, setShowHints] = useState(false);
  const [scoring, setScoring] = useState(false);
  const [scoreResult, setScoreResult] = useState<MicroDrillScore | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!challenge || scoreResult || secondsLeft <= 0) return;
    const t = setInterval(() => {
      setSecondsLeft((s) => (s > 0 ? s - 1 : 0));
    }, 1000);
    return () => clearInterval(t);
  }, [challenge, scoreResult, secondsLeft]);

  async function launchDrill(type: DrillType) {
    setSelectedType(type);
    setLoadingChallenge(true);
    setScoreResult(null);
    setResponse("");
    setShowHints(false);
    setError(null);
    try {
      const res = await fetch("/api/drills", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action: "generate", drillType: type }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.error || "Failed to generate micro-drill.");
      } else {
        setDrillId(data.drillId);
        setChallenge(data.challenge);
        setSecondsLeft(data.challenge.timeLimitSeconds || 300);
      }
    } catch {
      setError("Network error while starting drill.");
    } finally {
      setLoadingChallenge(false);
    }
  }

  async function submitDrill() {
    if (!challenge || !response.trim()) return;
    setScoring(true);
    setError(null);
    try {
      const res = await fetch("/api/drills", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          action: "score",
          drillId,
          challenge,
          userResponse: response,
        }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.error || "Failed to score drill.");
      } else {
        setScoreResult(data.score);
      }
    } catch {
      setError("Network error while scoring drill.");
    } finally {
      setScoring(false);
    }
  }

  const mm = String(Math.floor(secondsLeft / 60)).padStart(2, "0");
  const ss = String(secondsLeft % 60).padStart(2, "0");

  return (
    <div className="max-w-5xl mx-auto px-4 sm:px-6 pt-7 space-y-7">
      <header className="space-y-2 pb-5 border-b border-line">
        <span className="text-xs font-mono uppercase tracking-wider text-accent font-semibold">
          High-Frequency Skill Conditioning
        </span>
        <h1 className="font-serif text-3xl font-semibold text-ink">
          5-Minute Targeted Micro-Drills
        </h1>
        <p className="text-sm text-ink-secondary max-w-2xl">
          Isolate and repair specific Band 6.5 bottlenecks—Task 1 Overviews, mechanical
          linkers, L1 punctuation/cognates, and academic hedging—in focused 5-minute
          sprints.
        </p>
      </header>

      {/* Drill Selector Cards */}
      <section className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        {DRILL_CARDS.map((card) => {
          const isRec = card.type === recommendedDrill;
          const isSelected = card.type === selectedType && Boolean(challenge);
          return (
            <div
              key={card.type}
              className={`editorial-surface rounded-2xl p-5 flex flex-col justify-between gap-4 transition ${
                isSelected ? "ring-2 ring-accent" : ""
              }`}
            >
              <div className="space-y-2">
                <div className="flex items-center justify-between gap-2">
                  <span className="px-2 py-0.5 rounded bg-subtle text-ink-secondary text-[11px] font-mono font-semibold">
                    {card.badge} · {card.duration}
                  </span>
                  {isRec && (
                    <span className="text-[10px] font-mono uppercase px-2 py-0.5 rounded bg-accent-soft text-accent font-semibold">
                      Recommended Focus
                    </span>
                  )}
                </div>
                <h2 className="font-serif text-lg font-semibold text-ink">
                  {card.title}
                </h2>
                <p className="text-xs text-ink-secondary leading-relaxed">
                  {card.description}
                </p>
              </div>

              <button
                id={`start-drill-${card.type}`}
                type="button"
                disabled={loadingChallenge}
                onClick={() => launchDrill(card.type)}
                className="inline-flex items-center justify-center gap-2 py-2.5 px-4 rounded-xl bg-accent hover:bg-accent-hover text-accent-fg text-xs font-semibold transition cursor-pointer disabled:opacity-50"
              >
                {loadingChallenge && selectedType === card.type ? (
                  <Loader2 className="w-3.5 h-3.5 animate-spin" />
                ) : (
                  <Play className="w-3.5 h-3.5" />
                )}
                <span>Start {card.duration} Sprint</span>
              </button>
            </div>
          );
        })}
      </section>

      {error && (
        <div className="p-3.5 rounded-xl border border-red-400 bg-red-500/10 text-xs text-red-600">
          {error}
        </div>
      )}

      {/* Active Micro-Drill Workbench */}
      {challenge && (
        <section className="editorial-surface rounded-2xl p-6 space-y-5">
          <div className="flex flex-wrap items-center justify-between gap-3 pb-4 border-b border-line">
            <div>
              <span className="text-[11px] font-mono uppercase text-accent font-semibold">
                {challenge.sourceContext}
              </span>
              <h2 className="font-serif text-xl font-semibold text-ink">
                {challenge.title}
              </h2>
            </div>

            <div className="flex items-center gap-2">
              <span
                className={`inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg font-mono text-xs font-bold tabular-nums ${
                  secondsLeft <= 45
                    ? "bg-warm-soft text-warm"
                    : "bg-subtle text-ink"
                }`}
              >
                <Clock className="w-3.5 h-3.5" />
                {mm}:{ss}
              </span>
              <button
                type="button"
                onClick={() => launchDrill(challenge.drillType)}
                title="Load another variation"
                className="p-2 rounded-lg border border-line hover:bg-subtle text-ink-secondary cursor-pointer"
              >
                <RotateCcw className="w-3.5 h-3.5" />
              </button>
            </div>
          </div>

          <p className="text-sm text-ink leading-relaxed font-medium">
            {challenge.instructions}
          </p>

          {challenge.chartSpec && (
            <TaskVisualRenderer chartSpecDirect={challenge.chartSpec} compact />
          )}

          {challenge.flawedText && (
            <div className="p-4 rounded-xl bg-subtle border border-line space-y-1">
              <span className="text-[10px] font-mono uppercase text-ink-muted">
                Passage to Upgrade
              </span>
              <p className="editorial-prose text-ink">{challenge.flawedText}</p>
            </div>
          )}

          {challenge.hints?.length > 0 && (
            <div>
              <button
                type="button"
                onClick={() => setShowHints((h) => !h)}
                className="inline-flex items-center gap-1.5 text-xs font-medium text-accent hover:underline cursor-pointer"
              >
                <Lightbulb className="w-3.5 h-3.5" />
                <span>{showHints ? "Hide Coaching Hints" : "Need a Quick Hint?"}</span>
              </button>
              {showHints && (
                <ul className="mt-2 space-y-1 text-xs text-ink-secondary list-disc list-inside bg-canvas p-3 rounded-lg border border-line">
                  {challenge.hints.map((h, i) => (
                    <li key={i}>{h}</li>
                  ))}
                </ul>
              )}
            </div>
          )}

          <div className="space-y-2">
            <div className="flex items-center justify-between text-xs font-mono text-ink-muted">
              <span>Your Upgraded Academic Prose</span>
              <span>{countEssayWords(response)} words</span>
            </div>
            <textarea
              id="micro-drill-textarea"
              rows={4}
              value={response}
              onChange={(e) => setResponse(e.target.value)}
              placeholder="Write your concise Band 8.0+ version here..."
              className="w-full p-4 rounded-xl bg-canvas border border-line-strong editorial-prose text-ink focus:outline-none focus:border-accent"
            />
          </div>

          <div className="flex justify-end">
            <button
              id="submit-drill-btn"
              type="button"
              disabled={scoring || !response.trim()}
              onClick={submitDrill}
              className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl bg-accent hover:bg-accent-hover text-accent-fg text-xs sm:text-sm font-semibold cursor-pointer disabled:opacity-50"
            >
              {scoring ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Send className="w-3.5 h-3.5" />
              )}
              <span>{scoring ? "Checking..." : "Check My Rewrite"}</span>
            </button>
          </div>

          {scoreResult && (
            <div className="mt-4 p-5 rounded-2xl bg-canvas border border-line-strong space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <CheckCircle2 className="w-5 h-5 text-accent" />
                  <span className="font-serif text-lg font-semibold text-ink">
                    {scoreResult.estimatedBandImpact}
                  </span>
                </div>
                <span className="px-3 py-1 rounded-lg bg-accent-soft text-accent font-mono text-sm font-bold">
                  {scoreResult.scoreOutOf10} / 10
                </span>
              </div>

              <p className="text-sm text-ink-secondary leading-relaxed">
                {scoreResult.feedback}
              </p>

              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                <div className="p-3.5 rounded-xl bg-subtle space-y-1">
                  <span className="text-[10px] font-mono uppercase text-accent font-semibold">
                    Polished Band 8.5 Version of Your Draft
                  </span>
                  <p className="editorial-prose text-sm text-ink">
                    {scoreResult.improvedVersion}
                  </p>
                </div>
                <div className="p-3.5 rounded-xl bg-subtle space-y-1">
                  <span className="text-[10px] font-mono uppercase text-ink-muted font-semibold">
                    Examiner Reference Benchmark
                  </span>
                  <p className="editorial-prose text-sm text-ink">
                    {challenge.referenceModelAnswer}
                  </p>
                </div>
              </div>

              <div className="flex items-center gap-2 text-xs text-ink font-medium pt-2 border-t border-line">
                <Sparkles className="w-4 h-4 text-accent shrink-0" />
                <span>Rule to keep: {scoreResult.keyTakeaway}</span>
              </div>
            </div>
          )}
        </section>
      )}
    </div>
  );
}
