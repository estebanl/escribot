import { useMemo, useState } from "react";
import { Link } from "react-router";
import {
  AlertTriangle,
  ArrowLeft,
  ArrowUpRight,
  BookmarkPlus,
  Check,
  Edit3,
  Eye,
  FileCheck2,
  Loader2,
  RotateCcw,
  Sparkles,
  TrendingUp,
} from "lucide-react";
import { TaskVisualRenderer } from "./TaskVisualRenderer";
import { ModelAnswer } from "./ModelAnswer";
import { countEssayWords, getMinimumWords } from "@/lib/ielts-utils";
import {
  submitEvaluationStream,
  type EvaluationErrorKind,
} from "@/lib/client-evaluate";
import type {
  AnnotationCategory,
  CriterionAssessment,
  EvaluationPayload,
  InlineAnnotation,
  TaskType,
} from "@/lib/types";
import type { PromptRecord } from "./PromptModals";

export interface HydratedDraft {
  id: string;
  sessionId: string;
  promptId: string;
  taskType: string;
  version: number;
  essayText: string;
  wordCount: number;
  secondsSpent: number;
  createdAt: string;
  prompt: PromptRecord | null;
  evaluation: {
    id: string;
    overallBand: number;
    taTrBand: number;
    ccBand: number;
    lrBand: number;
    graBand: number;
    examinerSummary: string;
    criterionFeedbackJson: string;
    inlineAnnotationsJson: string;
    targetBandRewrite: string;
    rewriteKeyChangesJson: string;
    topPrioritiesJson: string;
    providerUsed: string;
    modelUsed: string;
  } | null;
}

const CRITERION_META: Record<
  AnnotationCategory,
  { short: string; full: string; badgeClass: string }
> = {
  TA_TR: {
    short: "TA / TR",
    full: "Task Achievement / Response",
    badgeClass: "span-TA_TR",
  },
  CC: {
    short: "CC",
    full: "Coherence & Cohesion",
    badgeClass: "span-CC",
  },
  LR: {
    short: "LR",
    full: "Lexical Resource",
    badgeClass: "span-LR",
  },
  GRA: {
    short: "GRA",
    full: "Grammatical Range & Accuracy",
    badgeClass: "span-GRA",
  },
  L1: {
    short: "L1 Transfer",
    full: "Native-Language Interference",
    badgeClass: "span-L1",
  },
};

function renderAnnotatedEssay(
  essayText: string,
  annotations: InlineAnnotation[],
  activeCategory: AnnotationCategory | "ALL",
  selectedAnnId: string | null,
  onSelectAnnotation: (id: string) => void
) {
  const filtered = annotations.filter(
    (a) => activeCategory === "ALL" || a.category === activeCategory
  );

  // Locate non-overlapping exact substring spans
  const matches: Array<{
    start: number;
    end: number;
    ann: InlineAnnotation;
  }> = [];

  for (const ann of filtered) {
    if (!ann.originalSpan) continue;
    const idx = essayText.indexOf(ann.originalSpan);
    if (idx === -1) continue;
    const end = idx + ann.originalSpan.length;
    const overlaps = matches.some((m) => !(end <= m.start || idx >= m.end));
    if (!overlaps) {
      matches.push({ start: idx, end, ann });
    }
  }

  matches.sort((a, b) => a.start - b.start);

  if (matches.length === 0) {
    return (
      <div className="editorial-prose whitespace-pre-line text-ink">
        {essayText}
      </div>
    );
  }

  const nodes: React.ReactNode[] = [];
  let cursor = 0;

  matches.forEach((m, i) => {
    if (m.start > cursor) {
      nodes.push(
        <span key={`txt-${i}`}>{essayText.slice(cursor, m.start)}</span>
      );
    }
    const isSelected = selectedAnnId === m.ann.id;
    nodes.push(
      <mark
        key={`ann-${m.ann.id}`}
        onClick={() => onSelectAnnotation(m.ann.id)}
        className={`span-mark span-${m.ann.category} ${
          isSelected ? "span-mark-active" : ""
        } text-ink`}
      >
        {essayText.slice(m.start, m.end)}
      </mark>
    );
    cursor = m.end;
  });

  if (cursor < essayText.length) {
    nodes.push(<span key="txt-tail">{essayText.slice(cursor)}</span>);
  }

  return (
    <div className="editorial-prose whitespace-pre-line text-ink">{nodes}</div>
  );
}

export function EvaluationWorkbench({
  initialSession,
}: {
  initialSession: {
    id: string;
    mode: string;
    weightedBand: number | null;
    totalSecondsSpent: number;
    drafts: HydratedDraft[];
  };
}) {
  const [sessionData, setSessionData] = useState(initialSession);

  const gradedDrafts = useMemo(
    () => sessionData.drafts.filter((d) => d.evaluation !== null),
    [sessionData.drafts]
  );

  const hasTask1 = gradedDrafts.some((d) => d.taskType === "task1");
  const hasTask2 = gradedDrafts.some((d) => d.taskType === "task2");

  const [activeTaskType, setActiveTaskType] = useState<TaskType>(
    hasTask2 && !hasTask1 ? "task2" : "task1"
  );

  const taskDrafts = useMemo(
    () =>
      gradedDrafts
        .filter((d) => d.taskType === activeTaskType)
        .sort((a, b) => a.version - b.version),
    [gradedDrafts, activeTaskType]
  );

  const latestDraft = taskDrafts[taskDrafts.length - 1];
  const [selectedVersion, setSelectedVersion] = useState<number>(
    latestDraft?.version || 1
  );

  const currentDraft =
    taskDrafts.find((d) => d.version === selectedVersion) || latestDraft;

  const firstDraftBand = taskDrafts[0]?.evaluation?.overallBand ?? null;
  const currentBand = currentDraft?.evaluation?.overallBand ?? null;
  const bandDelta =
    firstDraftBand !== null &&
    currentBand !== null &&
    currentDraft.version > 1
      ? Number((currentBand - firstDraftBand).toFixed(1))
      : null;

  // Active workbench tab: "annotated" | "rewrite" | "redraft"
  const [activeView, setActiveView] = useState<"annotated" | "rewrite" | "redraft">(
    "annotated"
  );
  const [categoryFilter, setCategoryFilter] = useState<AnnotationCategory | "ALL">(
    "ALL"
  );
  const [selectedAnnId, setSelectedAnnId] = useState<string | null>(null);
  const [savedVaultIds, setSavedVaultIds] = useState<Record<string, boolean>>({});

  // Redraft editor state
  const [redraftText, setRedraftText] = useState(currentDraft?.essayText || "");
  const [redraftSubmitting, setRedraftSubmitting] = useState(false);
  const [redraftStage, setRedraftStage] = useState<string | null>(null);
  const [redraftError, setRedraftError] = useState<string | null>(null);
  const [redraftErrorKind, setRedraftErrorKind] =
    useState<EvaluationErrorKind | null>(null);
  const [promptExpanded, setPromptExpanded] = useState(false);

  if (!currentDraft || !currentDraft.evaluation) {
    return (
      <div className="max-w-4xl mx-auto px-4 py-12 text-center space-y-4">
        <p className="text-ink-secondary">No graded evaluation found for this session yet.</p>
        <Link to="/" className="text-accent underline text-sm">
          Return to Studio
        </Link>
      </div>
    );
  }

  const ev = currentDraft.evaluation;
  const criteria: EvaluationPayload["criteria"] = JSON.parse(
    ev.criterionFeedbackJson
  );
  const annotations: InlineAnnotation[] = JSON.parse(ev.inlineAnnotationsJson);
  const rewriteChanges: string[] = JSON.parse(ev.rewriteKeyChangesJson || "[]");
  const topPriorities: string[] = JSON.parse(ev.topPrioritiesJson || "[]");

  async function handleSaveAnnotationToVault(ann: InlineAnnotation) {
    setSavedVaultIds((prev) => ({ ...prev, [ann.id]: true }));
    await fetch("/api/vault", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        itemType: ann.severity === "error" ? "error" : "collocation",
        criterion: ann.category,
        originalText: ann.originalSpan,
        upgradedText: ann.replacement,
        explanation: ann.explanation,
        exampleSentence: ann.replacement,
      }),
    });
  }

  async function handleSubmitRedraft() {
    if (!currentDraft.prompt) return;
    setRedraftSubmitting(true);
    setRedraftStage("Examiner reviewing redraft...");
    setRedraftError(null);
    setRedraftErrorKind(null);

    try {
      const outcome = await submitEvaluationStream(
        {
          sessionId: sessionData.id,
          submissions: [
            {
              promptId: currentDraft.promptId,
              taskType: activeTaskType,
              essayText: redraftText,
              secondsSpent: 0,
            },
          ],
        },
        (stage) => setRedraftStage(stage)
      );

      if (!outcome.ok) {
        setRedraftError(outcome.error || "Redraft evaluation failed. Please try again.");
        setRedraftErrorKind(outcome.errorKind || "server");
        return;
      }

      // Refresh full session state
      const refreshedRes = await fetch(`/api/sessions/${sessionData.id}`);
      const refreshed = await refreshedRes.json();
      if (refreshed.session) {
        setSessionData(refreshed.session);
        const newVersion =
          outcome.results?.[0]?.version || (latestDraft?.version || currentDraft.version) + 1;
        setSelectedVersion(newVersion);
        setActiveView("annotated");
      }
    } catch {
      setRedraftError(
        "Could not refresh session results. Please reload the page to view your score."
      );
      setRedraftErrorKind("network");
    } finally {
      setRedraftSubmitting(false);
      setRedraftStage(null);
    }
  }

  const redraftWords = countEssayWords(redraftText);
  const minWords = getMinimumWords(activeTaskType);

  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 py-6 space-y-6">
      {/* Top Breadcrumb + Combined Score Header */}
      <div className="flex flex-wrap items-center justify-between gap-4 pb-5 border-b border-line">
        <div className="space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <Link
              to="/"
              className="inline-flex items-center gap-1.5 text-xs font-medium text-ink-secondary hover:text-ink"
            >
              <ArrowLeft className="w-3.5 h-3.5" />
              <span>Studio</span>
            </Link>
            <span className="text-ink-muted">·</span>
            <span className="text-xs font-mono uppercase text-ink-muted">
              {ev.providerUsed} / {ev.modelUsed}
            </span>
          </div>
          <h1 className="font-serif text-2xl sm:text-3xl font-semibold text-ink">
            {currentDraft.prompt?.title || "Diagnostic Evaluation"}
          </h1>
        </div>

        {/* Overall Score Pill + 60m Combined Weighting */}
        <div className="flex items-center gap-3">
          {hasTask1 && hasTask2 && sessionData.weightedBand !== null && (
            <div className="px-4 py-2 rounded-xl border border-line-strong bg-surface text-right">
              <span className="block text-[10px] font-mono uppercase text-ink-muted">
                Combined Exam (1:2 Weight)
              </span>
              <span className="font-mono text-xl font-bold text-ink">
                Band {sessionData.weightedBand.toFixed(1)}
              </span>
            </div>
          )}

          <div className="px-5 py-2.5 rounded-xl bg-accent text-accent-fg text-right">
            <span className="block text-[10px] font-mono uppercase opacity-85">
              {activeTaskType === "task1" ? "Task 1 Band" : "Task 2 Band"} (v
              {currentDraft.version})
            </span>
            <div className="flex items-center justify-end gap-2">
              <span className="font-mono text-2xl font-bold tabular-nums">
                {ev.overallBand.toFixed(1)}
              </span>
              {bandDelta !== null && bandDelta !== 0 && (
                <span className="px-1.5 py-0.5 rounded bg-white/20 text-xs font-mono font-semibold">
                  {bandDelta > 0 ? `+${bandDelta.toFixed(1)} ↑` : bandDelta.toFixed(1)}
                </span>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Part Switcher (for 60m Mock) + Draft Version Switcher (v1 -> v2 -> v3) */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-2">
          {hasTask1 && hasTask2 && (
            <div className="inline-flex p-1 rounded-xl bg-subtle mr-2">
              <button
                type="button"
                onClick={() => {
                  setActiveTaskType("task1");
                  const t1Latest = gradedDrafts
                    .filter((d) => d.taskType === "task1")
                    .pop();
                  setSelectedVersion(t1Latest?.version || 1);
                  setRedraftText(t1Latest?.essayText || "");
                }}
                className={`px-3 py-1.5 rounded-lg text-xs font-semibold cursor-pointer ${
                  activeTaskType === "task1"
                    ? "bg-surface text-ink shadow-xs"
                    : "text-ink-secondary"
                }`}
              >
                Part 1 · Task 1
              </button>
              <button
                type="button"
                onClick={() => {
                  setActiveTaskType("task2");
                  const t2Latest = gradedDrafts
                    .filter((d) => d.taskType === "task2")
                    .pop();
                  setSelectedVersion(t2Latest?.version || 1);
                  setRedraftText(t2Latest?.essayText || "");
                }}
                className={`px-3 py-1.5 rounded-lg text-xs font-semibold cursor-pointer ${
                  activeTaskType === "task2"
                    ? "bg-surface text-ink shadow-xs"
                    : "text-ink-secondary"
                }`}
              >
                Part 2 · Task 2
              </button>
            </div>
          )}

          {/* Multi-Draft Version Pills */}
          <div className="flex items-center gap-1.5">
            {taskDrafts.map((d) => (
              <button
                key={d.id}
                type="button"
                onClick={() => {
                  setSelectedVersion(d.version);
                  setRedraftText(d.essayText);
                }}
                className={`px-3 py-1.5 rounded-lg text-xs font-mono font-semibold border transition cursor-pointer ${
                  d.version === currentDraft.version
                    ? "border-accent bg-accent-soft text-accent"
                    : "border-line bg-surface text-ink-secondary hover:text-ink"
                }`}
              >
                Draft v{d.version}{" "}
                {d.evaluation ? `(${d.evaluation.overallBand.toFixed(1)})` : ""}
              </button>
            ))}
          </div>
        </div>

        <button
          type="button"
          onClick={() => setPromptExpanded((p) => !p)}
          className="text-xs font-medium text-accent hover:underline cursor-pointer"
        >
          {promptExpanded ? "Hide Task Prompt & Chart" : "Show Task Prompt & Chart"}
        </button>
      </div>

      {promptExpanded && currentDraft.prompt && (
        <section className="editorial-surface rounded-2xl p-5">
          <p className="text-sm text-ink whitespace-pre-line">
            {currentDraft.prompt.promptText}
          </p>
          {activeTaskType === "task1" && (
            <TaskVisualRenderer
              chartSpecJson={currentDraft.prompt.chartSpecJson}
              imageDataUrl={currentDraft.prompt.imageDataUrl}
              compact
            />
          )}
        </section>
      )}

      {currentDraft.prompt && (
        <ModelAnswer key={currentDraft.prompt.id} promptId={currentDraft.prompt.id} />
      )}

      {/* Examiner Executive Summary + 4 Official Criterion Cards */}
      <section className="space-y-4">
        <div className="editorial-surface rounded-2xl p-5">
          <p className="text-sm sm:text-base text-ink leading-relaxed">
            {ev.examinerSummary}
          </p>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-4">
          {(["TA_TR", "CC", "LR", "GRA"] as const).map((key) => {
            const crit: CriterionAssessment | undefined = criteria[key];
            const meta = CRITERION_META[key];
            const score =
              key === "TA_TR"
                ? ev.taTrBand
                : key === "CC"
                ? ev.ccBand
                : key === "LR"
                ? ev.lrBand
                : ev.graBand;

            return (
              <div
                key={key}
                className="editorial-surface rounded-2xl p-4 flex flex-col justify-between space-y-3"
              >
                <div>
                  <div className="flex items-center justify-between gap-2 mb-2">
                    <span
                      className={`px-2 py-0.5 rounded text-xs font-mono font-bold ${meta.badgeClass}`}
                    >
                      {key === "TA_TR"
                        ? activeTaskType === "task1"
                          ? "Task Achievement"
                          : "Task Response"
                        : meta.full}
                    </span>
                    <span className="font-mono text-lg font-bold tabular-nums text-ink">
                      {score.toFixed(1)}
                    </span>
                  </div>
                  <p className="text-xs text-ink-secondary leading-relaxed">
                    {crit?.descriptorSummary}
                  </p>
                </div>

                {crit?.howToGainHalfBand && (
                  <div className="pt-2.5 border-t border-line">
                    <span className="block text-[10px] font-mono uppercase text-accent font-semibold mb-0.5">
                      Next +0.5 Unlock
                    </span>
                    <p className="text-xs text-ink font-medium leading-snug">
                      {crit.howToGainHalfBand}
                    </p>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </section>

      {/* Main Workbench Mode Switcher: Annotated Essay | Your Ideas at Band 8+ | Redraft v(N+1) */}
      <div className="border-b border-line flex flex-wrap items-center gap-2 pb-3">
        <button
          id="tab-annotated-essay"
          type="button"
          onClick={() => setActiveView("annotated")}
          className={`inline-flex items-center gap-2 px-4 py-2 rounded-xl text-xs sm:text-sm font-semibold transition cursor-pointer ${
            activeView === "annotated"
              ? "bg-accent text-accent-fg"
              : "bg-surface border border-line text-ink-secondary hover:text-ink"
          }`}
        >
          <FileCheck2 className="w-4 h-4" />
          <span>Inline Examiner Annotations ({annotations.length})</span>
        </button>

        <button
          id="tab-band8-rewrite"
          type="button"
          onClick={() => setActiveView("rewrite")}
          className={`inline-flex items-center gap-2 px-4 py-2 rounded-xl text-xs sm:text-sm font-semibold transition cursor-pointer ${
            activeView === "rewrite"
              ? "bg-accent text-accent-fg"
              : "bg-surface border border-line text-ink-secondary hover:text-ink"
          }`}
        >
          <Eye className="w-4 h-4" />
          <span>&ldquo;Your Ideas at Band 8.0+&rdquo; Diff</span>
        </button>

        <button
          id="tab-redraft-editor"
          type="button"
          onClick={() => {
            setRedraftText(latestDraft?.essayText || currentDraft.essayText);
            setActiveView("redraft");
          }}
          className={`inline-flex items-center gap-2 px-4 py-2 rounded-xl text-xs sm:text-sm font-semibold transition cursor-pointer ${
            activeView === "redraft"
              ? "bg-accent text-accent-fg"
              : "bg-surface border border-line text-ink-secondary hover:text-ink"
          }`}
        >
          <Edit3 className="w-4 h-4" />
          <span>Redraft &amp; Re-Score (v{(latestDraft?.version || 1) + 1})</span>
        </button>
      </div>

      {/* VIEW 1: INLINE ANNOTATED ESSAY + EXAMINER MARGINALIA */}
      {activeView === "annotated" && (
        <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
          <div className="lg:col-span-7 editorial-surface rounded-2xl p-6 space-y-4">
            <div className="flex flex-wrap items-center justify-between gap-2 pb-3 border-b border-line">
              <div className="flex flex-wrap items-center gap-1.5">
                {(["ALL", "TA_TR", "CC", "LR", "GRA", "L1"] as const).map((cat) => (
                  <button
                    key={cat}
                    type="button"
                    onClick={() => setCategoryFilter(cat)}
                    className={`px-2.5 py-1 rounded-md text-[11px] font-mono font-semibold transition cursor-pointer ${
                      categoryFilter === cat
                        ? "bg-ink text-canvas"
                        : cat === "ALL"
                        ? "bg-subtle text-ink-secondary"
                        : `span-${cat} text-ink`
                    }`}
                  >
                    {cat === "ALL" ? "All Spans" : CRITERION_META[cat].short}
                  </button>
                ))}
              </div>
              <span className="text-xs font-mono text-ink-muted">
                {currentDraft.wordCount} words
              </span>
            </div>

            {renderAnnotatedEssay(
              currentDraft.essayText,
              annotations,
              categoryFilter,
              selectedAnnId,
              setSelectedAnnId
            )}
          </div>

          <div className="lg:col-span-5 space-y-3">
            <h2 className="font-serif text-lg font-semibold text-ink">
              Examiner Marginalia &amp; Upgrades
            </h2>
            {annotations
              .filter((a) => categoryFilter === "ALL" || a.category === categoryFilter)
              .map((ann) => {
                const isSelected = selectedAnnId === ann.id;
                const saved = savedVaultIds[ann.id];
                return (
                  <div
                    key={ann.id}
                    onClick={() => setSelectedAnnId(ann.id)}
                    className={`editorial-surface rounded-xl p-4 space-y-2 transition cursor-pointer ${
                      isSelected ? "ring-2 ring-accent" : ""
                    }`}
                  >
                    <div className="flex items-center justify-between gap-2">
                      <span
                        className={`px-2 py-0.5 rounded text-[10px] font-mono font-bold uppercase span-${ann.category}`}
                      >
                        {CRITERION_META[ann.category]?.short || ann.category} ·{" "}
                        {ann.severity}
                      </span>

                      <button
                        type="button"
                        onClick={(e) => {
                          e.stopPropagation();
                          handleSaveAnnotationToVault(ann);
                        }}
                        disabled={saved}
                        className="inline-flex items-center gap-1 text-[11px] font-medium text-accent hover:underline cursor-pointer disabled:opacity-60"
                      >
                        {saved ? (
                          <>
                            <Check className="w-3 h-3" /> Saved in Vault
                          </>
                        ) : (
                          <>
                            <BookmarkPlus className="w-3.5 h-3.5" /> Save to Vault
                          </>
                        )}
                      </button>
                    </div>

                    <div className="text-xs space-y-1">
                      <p className="line-through text-ink-muted">
                        &ldquo;{ann.originalSpan}&rdquo;
                      </p>
                      <p className="font-semibold text-ink">
                        → &ldquo;{ann.replacement}&rdquo;
                      </p>
                    </div>

                    <p className="text-xs text-ink-secondary leading-relaxed">
                      {ann.explanation}
                    </p>
                  </div>
                );
              })}

            {annotations.length === 0 && (
              <p className="text-xs text-ink-muted">
                No inline span annotations returned for this draft.
              </p>
            )}
          </div>
        </div>
      )}

      {/* VIEW 2: SIDE-BY-SIDE "YOUR IDEAS AT BAND 8.0+" DIFF */}
      {activeView === "rewrite" && (
        <div className="space-y-5">
          {rewriteChanges.length > 0 && (
            <div className="editorial-surface rounded-2xl p-5 space-y-2">
              <div className="flex items-center gap-2 text-accent">
                <Sparkles className="w-4 h-4" />
                <h2 className="font-serif text-base font-semibold text-ink">
                  What Changed to Reach Band 8.0+ (Preserving Your Exact Arguments)
                </h2>
              </div>
              <ul className="grid grid-cols-1 md:grid-cols-2 gap-2 text-xs text-ink-secondary pt-1">
                {rewriteChanges.map((change, i) => (
                  <li key={i} className="flex items-start gap-2">
                    <ArrowUpRight className="w-3.5 h-3.5 text-accent shrink-0 mt-0.5" />
                    <span>{change}</span>
                  </li>
                ))}
              </ul>
            </div>
          )}

          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            <div className="editorial-surface rounded-2xl p-6 space-y-3">
              <div className="flex items-center justify-between pb-2 border-b border-line">
                <span className="text-xs font-mono uppercase text-ink-muted">
                  Your Draft v{currentDraft.version} (Band {ev.overallBand.toFixed(1)})
                </span>
                <span className="text-xs font-mono text-ink-muted">
                  {currentDraft.wordCount} words
                </span>
              </div>
              <div className="editorial-prose whitespace-pre-line text-ink-secondary">
                {currentDraft.essayText}
              </div>
            </div>

            <div className="editorial-surface rounded-2xl p-6 space-y-3 border-accent/40">
              <div className="flex items-center justify-between pb-2 border-b border-line">
                <span className="text-xs font-mono uppercase font-semibold text-accent">
                  Your Arguments Elevated to Band 8.0+
                </span>
                <span className="text-xs font-mono text-ink-muted">
                  {countEssayWords(ev.targetBandRewrite)} words
                </span>
              </div>
              <div className="editorial-prose whitespace-pre-line text-ink">
                {ev.targetBandRewrite}
              </div>
            </div>
          </div>
        </div>
      )}

      {/* VIEW 3: ITERATIVE REDRAFTING (v1 -> v2 -> v3) */}
      {activeView === "redraft" && (
        <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
          <div className="lg:col-span-4 editorial-surface rounded-2xl p-5 space-y-4">
            <div className="flex items-center gap-2">
              <TrendingUp className="w-4 h-4 text-accent" />
              <h2 className="font-serif text-base font-semibold text-ink">
                Top 3 Priorities for Draft v{(latestDraft?.version || 1) + 1}
              </h2>
            </div>

            <ol className="space-y-2.5 text-xs text-ink-secondary list-decimal list-inside">
              {topPriorities.map((p, idx) => (
                <li key={idx} className="leading-relaxed text-ink font-medium">
                  {p}
                </li>
              ))}
            </ol>

            <div className="pt-3 border-t border-line space-y-2">
              <span className="block text-[11px] font-mono uppercase text-ink-muted">
                Key Span Fixes to Apply
              </span>
              <div className="max-h-72 overflow-y-auto space-y-2 pr-1">
                {annotations.map((ann) => (
                  <div key={ann.id} className="p-2.5 rounded-lg bg-subtle text-xs">
                    <p className="line-through text-ink-muted">{ann.originalSpan}</p>
                    <p className="font-semibold text-ink mt-0.5">→ {ann.replacement}</p>
                  </div>
                ))}
              </div>
            </div>
          </div>

          <div className="lg:col-span-8 editorial-surface rounded-2xl p-6 space-y-4">
            <div className="flex items-center justify-between pb-3 border-b border-line">
              <span className="text-xs font-mono font-semibold text-ink">
                Editing Draft v{(latestDraft?.version || 1) + 1} · {redraftWords} /{" "}
                {minWords}+ words
              </span>
              <button
                id="submit-redraft-btn"
                type="button"
                disabled={redraftSubmitting || redraftWords < 15}
                onClick={handleSubmitRedraft}
                className="inline-flex items-center gap-2 px-4 py-2 rounded-xl bg-accent hover:bg-accent-hover text-accent-fg text-xs font-semibold transition cursor-pointer disabled:opacity-50"
              >
                {redraftSubmitting ? (
                  <Loader2 className="w-4 h-4 animate-spin" />
                ) : (
                  <Sparkles className="w-4 h-4" />
                )}
                <span>
                  {redraftSubmitting
                    ? "Re-Scoring Draft..."
                    : `Submit & Re-Score v${(latestDraft?.version || 1) + 1}`}
                </span>
              </button>
            </div>

            {redraftSubmitting && redraftStage && (
              <div className="p-3 rounded-xl border border-accent/35 bg-accent-soft/60 flex items-center gap-2 text-xs text-ink">
                <Loader2 className="w-3.5 h-3.5 animate-spin text-accent shrink-0" />
                <span className="font-medium">{redraftStage}</span>
              </div>
            )}

            {redraftError && (
              <div
                role="alert"
                className={`p-3 rounded-xl border flex flex-wrap items-center justify-between gap-3 text-xs ${
                  redraftErrorKind === "timeout"
                    ? "border-warm bg-warm-soft text-ink"
                    : "border-red-400 bg-red-500/10 text-red-600 dark:text-red-300"
                }`}
              >
                <div className="flex items-start gap-2">
                  <AlertTriangle
                    className={`w-4 h-4 shrink-0 mt-0.5 ${
                      redraftErrorKind === "timeout" ? "text-warm" : "text-red-500"
                    }`}
                  />
                  <span>{redraftError}</span>
                </div>
                <button
                  id="retry-redraft-btn"
                  type="button"
                  disabled={redraftSubmitting}
                  onClick={handleSubmitRedraft}
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-xs font-semibold transition cursor-pointer shrink-0"
                >
                  <RotateCcw className="w-3.5 h-3.5" />
                  <span>Retry Re-Score</span>
                </button>
              </div>
            )}

            <textarea
              id="redraft-editor-textarea"
              rows={16}
              value={redraftText}
              onChange={(e) => setRedraftText(e.target.value)}
              className="w-full bg-transparent editorial-prose text-ink focus:outline-none resize-y"
            />
          </div>
        </div>
      )}
    </div>
  );
}
