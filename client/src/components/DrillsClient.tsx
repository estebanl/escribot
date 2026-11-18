import { useEffect, useRef, useState } from "react";
import {
  Clock,
  Lightbulb,
  Loader2,
  Play,
  RotateCcw,
  Send,
  Sparkles,
  CheckCircle2,
  Target,
} from "lucide-react";
import { TaskVisualRenderer } from "./TaskVisualRenderer";
import { countEssayWords } from "@/lib/ielts-utils";
import type {
  DrillLevel,
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
  beginnerTitle: string;
  beginnerDescription: string;
}> = [
  {
    type: "overview_sprint",
    badge: "TA · Task 1",
    title: "4-Minute Task 1 Overview Sprint",
    duration: "4 min",
    description:
      "Read a fresh data chart and draft ONLY a 2-sentence macro Overview capturing main trends and extremes without raw statistics.",
    beginnerTitle: "Write a Chart Summary",
    beginnerDescription:
      "Look at a simple chart and write 2 sentences about the big change. Sentence starters help you begin.",
  },
  {
    type: "cohesion",
    badge: "CC · Cohesion",
    title: "Cohesion De-Mechanizer",
    duration: "5 min",
    description:
      "Eliminate robotic 'Firstly / Moreover / On the other hand' transitions using demonstrative nominalisation and thematic links.",
    beginnerTitle: "Make Your Sentences Flow",
    beginnerDescription:
      "Replace list words like 'Firstly' and 'Also' so your sentences connect naturally.",
  },
  {
    type: "l1_surgeon",
    badge: "L1 · Transfer",
    title: "L1 Clause & False-Cognate Surgeon",
    duration: "5 min",
    description:
      "Spot and repair false friends ('actually' vs 'currently'), comma-spliced 'however' clauses, and preposition/article calques.",
    beginnerTitle: "Fix First-Language Mistakes",
    beginnerDescription:
      "Find 2 small mistakes that come from your first language and write the correct sentence.",
  },
  {
    type: "lexical_hedging",
    badge: "LR · Register",
    title: "Academic Hedging & Lexical Upgrade",
    duration: "5 min",
    description:
      "Transform sweeping, informal Band 6 claims into nuanced, academically hedged C1/C2 arguments.",
    beginnerTitle: "Make Strong Claims Softer",
    beginnerDescription:
      "Change words like 'always' and 'all' into careful words like 'can' and 'some'.",
  },
];

const LEVEL_OPTIONS: Array<{ value: DrillLevel; label: string }> = [
  { value: "beginner", label: "Beginner" },
  { value: "intermediate", label: "Intermediate" },
  { value: "advanced", label: "Advanced" },
];

const LEVEL_STORAGE_KEY = "fluentescritor:drill-level";

function readStoredLevel(): DrillLevel | null {
  try {
    const v = window.localStorage.getItem(LEVEL_STORAGE_KEY);
    return v === "beginner" || v === "intermediate" || v === "advanced" ? v : null;
  } catch {
    return null;
  }
}

function storeLevel(level: DrillLevel) {
  try {
    window.localStorage.setItem(LEVEL_STORAGE_KEY, level);
  } catch {
    /* storage unavailable — the choice just won't persist */
  }
}

export function DrillsClient({
  recommendedDrill,
  defaultLevel,
  levelForcedByUrl = false,
}: {
  recommendedDrill: DrillType;
  defaultLevel: DrillLevel;
  levelForcedByUrl?: boolean;
}) {
  const [level, setLevel] = useState<DrillLevel>(defaultLevel);
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
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  // Bumped on every new request and on level change, so stale responses are ignored.
  const requestSeq = useRef(0);

  const isBeginner = level === "beginner";
  const isAdvanced = level === "advanced";

  useEffect(() => {
    if (levelForcedByUrl) return;
    const stored = readStoredLevel();
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage is only readable after hydration
    if (stored) setLevel(stored);
  }, [levelForcedByUrl]);

  function changeLevel(next: DrillLevel) {
    setLevel(next);
    storeLevel(next);
    requestSeq.current += 1;
    setLoadingChallenge(false);
    setScoring(false);
    // A drill generated for another level no longer matches; clear it.
    setChallenge(null);
    setScoreResult(null);
    setResponse("");
    setError(null);
  }

  function insertAtCursor(text: string) {
    const el = textareaRef.current;
    const start = el?.selectionStart ?? response.length;
    const end = el?.selectionEnd ?? response.length;
    const before = response.slice(0, start);
    const after = response.slice(end);
    const needsSpace = before.length > 0 && !/\s$/.test(before);
    const insert = (needsSpace ? " " : "") + text;
    const next = before + insert + after;
    setResponse(next);
    requestAnimationFrame(() => {
      if (!el) return;
      el.focus();
      const caret = before.length + insert.length;
      el.setSelectionRange(caret, caret);
    });
  }

  useEffect(() => {
    if (!challenge || scoreResult || secondsLeft <= 0) return;
    const t = setInterval(() => {
      setSecondsLeft((s) => (s > 0 ? s - 1 : 0));
    }, 1000);
    return () => clearInterval(t);
  }, [challenge, scoreResult, secondsLeft]);

  async function launchDrill(type: DrillType) {
    const seq = ++requestSeq.current;
    setSelectedType(type);
    setLoadingChallenge(true);
    setScoreResult(null);
    setResponse("");
    setShowHints(level === "beginner");
    setError(null);
    try {
      const res = await fetch("/api/drills", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action: "generate", drillType: type, level }),
      });
      const data = await res.json();
      if (seq !== requestSeq.current) return;
      if (!res.ok) {
        setError(data.error || "Failed to generate micro-drill.");
      } else {
        setDrillId(data.drillId);
        setChallenge(data.challenge);
        setSecondsLeft(data.challenge.timeLimitSeconds || 300);
      }
    } catch {
      if (seq !== requestSeq.current) return;
      setError("Network error while starting drill.");
    } finally {
      if (seq === requestSeq.current) setLoadingChallenge(false);
    }
  }

  async function submitDrill() {
    if (!challenge || !response.trim()) return;
    const seq = ++requestSeq.current;
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
          level: challenge.level ?? level,
        }),
      });
      const data = await res.json();
      if (seq !== requestSeq.current) return;
      if (!res.ok) {
        setError(data.error || "Failed to score drill.");
      } else {
        setScoreResult(data.score);
      }
    } catch {
      if (seq !== requestSeq.current) return;
      setError("Network error while scoring drill.");
    } finally {
      if (seq === requestSeq.current) setScoring(false);
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
          {isBeginner
            ? "Short, guided writing practice. Each drill gives you small steps, sentence starters and useful words, so you never start from a blank page."
            : "Isolate and repair specific Band 6.5 bottlenecks—Task 1 Overviews, mechanical linkers, L1 punctuation/cognates, and academic hedging—in focused 5-minute sprints."}
        </p>
        <div className="flex flex-wrap items-center gap-3 pt-2">
          <span className="text-xs font-mono uppercase text-ink-muted">Level</span>
          <div
            role="radiogroup"
            aria-label="Drill level"
            className="inline-flex rounded-xl border border-line p-1 bg-canvas"
          >
            {LEVEL_OPTIONS.map((opt) => (
              <button
                key={opt.value}
                id={`drill-level-${opt.value}`}
                type="button"
                role="radio"
                aria-checked={level === opt.value}
                onClick={() => changeLevel(opt.value)}
                className={`px-3 py-1.5 rounded-lg text-xs font-semibold transition cursor-pointer ${
                  level === opt.value
                    ? "bg-accent text-accent-fg"
                    : "text-ink-secondary hover:bg-subtle"
                }`}
              >
                {opt.label}
              </button>
            ))}
          </div>
          <span className="text-xs text-ink-muted">
            {isBeginner
              ? "Simple texts, step-by-step help, friendly feedback."
              : isAdvanced
              ? "Band 8+ academic rewrites."
              : "Standard drills with a few guiding steps."}
          </span>
        </div>
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
                    {card.badge} · {isBeginner ? "5 min" : card.duration}
                  </span>
                  {isRec && !isBeginner && (
                    <span className="text-[10px] font-mono uppercase px-2 py-0.5 rounded bg-accent-soft text-accent font-semibold">
                      Recommended Focus
                    </span>
                  )}
                </div>
                <h2 className="font-serif text-lg font-semibold text-ink">
                  {isBeginner ? card.beginnerTitle : card.title}
                </h2>
                <p className="text-xs text-ink-secondary leading-relaxed">
                  {isBeginner ? card.beginnerDescription : card.description}
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
                <span>Start {isBeginner ? "5 min" : card.duration} Sprint</span>
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
                {isAdvanced ? "Passage to Upgrade" : "Text to Improve"}
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
                <span>
                  {showHints
                    ? isBeginner
                      ? "Hide Tips"
                      : "Hide Coaching Hints"
                    : isBeginner
                    ? "Show Tips"
                    : "Need a Quick Hint?"}
                </span>
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

          {challenge.steps && challenge.steps.length > 0 && (
            <div className="p-4 rounded-xl border border-line space-y-3">
              <span className="text-[10px] font-mono uppercase text-accent font-semibold">
                Step by Step
              </span>
              <ol className="space-y-3">
                {challenge.steps.map((step, i) => (
                  <li key={i} className="flex gap-3">
                    <span className="shrink-0 w-6 h-6 rounded-full bg-accent-soft text-accent text-xs font-bold flex items-center justify-center">
                      {i + 1}
                    </span>
                    <div className="space-y-1.5 min-w-0">
                      <p className="text-sm text-ink leading-relaxed">{step.prompt}</p>
                      {step.starter && (
                        <button
                          type="button"
                          onClick={() => insertAtCursor(step.starter!)}
                          title="Insert this sentence starter into your answer"
                          className="text-left text-xs px-2.5 py-1.5 rounded-lg bg-subtle border border-line hover:border-accent text-ink-secondary cursor-pointer break-words"
                        >
                          <span className="font-semibold text-accent">+ Use starter: </span>
                          {step.starter}
                        </button>
                      )}
                      {scoreResult?.stepFeedback?.[i] && (
                        <p className="text-xs text-ink-secondary italic">
                          {scoreResult.stepFeedback[i]}
                        </p>
                      )}
                    </div>
                  </li>
                ))}
              </ol>
            </div>
          )}

          {challenge.wordBank && challenge.wordBank.length > 0 && (
            <div className="space-y-2">
              <span className="text-[10px] font-mono uppercase text-ink-muted">
                Useful Words · click to add
              </span>
              <div className="flex flex-wrap gap-2">
                {challenge.wordBank.map((w) => (
                  <button
                    key={w}
                    type="button"
                    onClick={() => insertAtCursor(w)}
                    className="px-2.5 py-1 rounded-full border border-line bg-canvas hover:bg-accent-soft hover:text-accent text-xs text-ink cursor-pointer"
                  >
                    {w}
                  </button>
                ))}
              </div>
            </div>
          )}

          <div className="space-y-2">
            <div className="flex items-center justify-between text-xs font-mono text-ink-muted">
              <span>{isAdvanced ? "Your Upgraded Academic Prose" : "Your Answer"}</span>
              <span>{countEssayWords(response)} words</span>
            </div>
            <textarea
              id="micro-drill-textarea"
              ref={textareaRef}
              rows={4}
              value={response}
              onChange={(e) => setResponse(e.target.value)}
              placeholder={
                isAdvanced
                  ? "Write your concise Band 8.0+ version here..."
                  : challenge.steps?.some((st) => st.starter)
                  ? "Start with one of the sentence starters above, then fill in the ___ gaps..."
                  : "Write your answer here. Short, clear sentences are fine!"
              }
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
              <span>
                {scoring ? "Checking..." : isAdvanced ? "Check My Rewrite" : "Check My Answer"}
              </span>
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

              {scoreResult.oneThingToFix && (
                <div className="flex items-start gap-2 p-3 rounded-xl bg-warm-soft text-sm text-ink">
                  <Target className="w-4 h-4 text-warm shrink-0 mt-0.5" />
                  <span>
                    <span className="font-semibold">One thing to fix: </span>
                    {scoreResult.oneThingToFix}
                  </span>
                </div>
              )}

              <p className="text-sm text-ink-secondary leading-relaxed">
                {scoreResult.feedback}
              </p>

              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                {scoreResult.improvedVersion.trim() !== challenge.referenceModelAnswer.trim() && (
                <div className="p-3.5 rounded-xl bg-subtle space-y-1">
                  <span className="text-[10px] font-mono uppercase text-accent font-semibold">
                    {isAdvanced
                      ? "Polished Band 8.5 Version of Your Draft"
                      : "A Better Version of Your Answer"}
                  </span>
                  <p className="editorial-prose text-sm text-ink">
                    {scoreResult.improvedVersion}
                  </p>
                </div>
                )}
                <div className="p-3.5 rounded-xl bg-subtle space-y-1">
                  <span className="text-[10px] font-mono uppercase text-ink-muted font-semibold">
                    {isAdvanced ? "Examiner Reference Benchmark" : "Example Answer"}
                  </span>
                  <p className="editorial-prose text-sm text-ink">
                    {challenge.referenceModelAnswer}
                  </p>
                </div>
              </div>

              <div className="flex items-center gap-2 text-xs text-ink font-medium pt-2 border-t border-line">
                <Sparkles className="w-4 h-4 text-accent shrink-0" />
                <span>
                  {isAdvanced ? "Rule to keep" : "Remember"}: {scoreResult.keyTakeaway}
                </span>
              </div>
            </div>
          )}
        </section>
      )}
    </div>
  );
}
