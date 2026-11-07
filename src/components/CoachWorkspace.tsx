"use client";

import { useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import {
  ArrowLeft,
  CheckCircle2,
  Clock,
  Compass,
  Loader2,
  Send,
  Sparkles,
  Languages,
  AlignLeft,
  Copy,
  Check,
} from "lucide-react";
import { TaskVisualRenderer } from "./TaskVisualRenderer";
import { countEssayWords, getMinimumWords, getRecommendedMinutes } from "@/lib/ielts-utils";
import type {
  OutlineFeedback,
  OutlineInput,
  TaskType,
  ToolkitResponse,
} from "@/lib/types";
import type { PromptRecord } from "./PromptModals";

export function CoachWorkspace({
  sessionId,
  prompt,
}: {
  sessionId: string;
  prompt: PromptRecord;
}) {
  const router = useRouter();
  const taskType = (prompt.taskType === "task1" ? "task1" : "task2") as TaskType;
  const minWords = getMinimumWords(taskType);
  const recMinutes = getRecommendedMinutes(taskType);

  // Step 1: 5-Minute Outline Check state
  const [outlineOpen, setOutlineOpen] = useState(true);
  const [outline, setOutline] = useState<OutlineInput>({
    thesisOrOverview: "",
    body1Plan: "",
    body2Plan: "",
    targetKeyTerms: "",
  });
  const [outlineFeedback, setOutlineFeedback] = useState<OutlineFeedback | null>(null);
  const [checkingOutline, setCheckingOutline] = useState(false);

  // Step 2: Distraction-free Essay Editor state
  const [essayText, setEssayText] = useState("");
  const [serifEditor, setSerifEditor] = useState(true);
  const [elapsedSeconds, setElapsedSeconds] = useState(0);
  const [timerPaused, setTimerPaused] = useState(false);
  const editorRef = useRef<HTMLTextAreaElement | null>(null);

  // On-demand Coach Toolkit state (NO autocomplete)
  const [selectedSnippet, setSelectedSnippet] = useState("");
  const [toolkitLoading, setToolkitLoading] = useState<string | null>(null);
  const [toolkitResult, setToolkitResult] = useState<ToolkitResponse | null>(null);
  const [copiedIdx, setCopiedIdx] = useState<number | null>(null);

  // Final evaluation submission state
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (timerPaused || submitting) return;
    const id = setInterval(() => {
      setElapsedSeconds((s) => s + 1);
    }, 1000);
    return () => clearInterval(id);
  }, [timerPaused, submitting]);

  const wordCount = countEssayWords(essayText);
  const wordProgress = Math.min(100, Math.round((wordCount / minWords) * 100));

  function captureSelection() {
    const el = editorRef.current;
    if (!el) return;
    const start = el.selectionStart;
    const end = el.selectionEnd;
    if (end > start) {
      setSelectedSnippet(essayText.slice(start, end).trim());
    }
  }

  async function handleValidateOutline() {
    setCheckingOutline(true);
    setError(null);
    try {
      const res = await fetch("/api/ai/outline-check", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          promptId: prompt.id,
          taskType,
          promptText: prompt.promptText,
          outline,
        }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.error || "Outline check failed.");
      } else {
        setOutlineFeedback(data.feedback);
      }
    } catch {
      setError("Network error while checking outline.");
    } finally {
      setCheckingOutline(false);
    }
  }

  async function handleRunToolkit(tool: "collocations" | "l1_check" | "paragraph_check") {
    const targetText =
      selectedSnippet.trim() ||
      essayText
        .split(/\n\s*\n/)
        .filter(Boolean)
        .pop()
        ?.trim() ||
      "";

    if (!targetText) {
      setError(
        "Highlight a phrase/sentence in your draft (or write at least one sentence) before running an on-demand check."
      );
      return;
    }

    setToolkitLoading(tool);
    setError(null);
    try {
      const res = await fetch("/api/ai/toolkit", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          tool,
          taskType,
          promptText: prompt.promptText,
          selectedText: targetText,
          fullEssayContext: essayText,
        }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.error || "Toolkit request failed.");
      } else {
        setToolkitResult(data.result);
      }
    } catch {
      setError("Network error while consulting toolkit.");
    } finally {
      setToolkitLoading(null);
    }
  }

  async function handleSubmitEssay() {
    if (wordCount < 15) {
      setError("Please write your draft before submitting for 4-criterion grading.");
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      const res = await fetch("/api/ai/evaluate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          sessionId,
          totalSecondsSpent: elapsedSeconds,
          submissions: [
            {
              promptId: prompt.id,
              taskType,
              essayText,
              secondsSpent: elapsedSeconds,
              outline,
              outlineFeedback,
            },
          ],
        }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.error || "Evaluation failed.");
        setSubmitting(false);
        return;
      }
      router.push(`/session/${sessionId}`);
    } catch {
      setError("Network error during evaluation.");
      setSubmitting(false);
    }
  }

  const mm = String(Math.floor(elapsedSeconds / 60)).padStart(2, "0");
  const ss = String(elapsedSeconds % 60).padStart(2, "0");

  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 py-5 space-y-5">
      {/* Top Studio Bar */}
      <div className="flex flex-wrap items-center justify-between gap-3 pb-4 border-b border-line">
        <div className="flex items-center gap-3">
          <Link
            href="/"
            className="p-2 rounded-lg border border-line bg-surface hover:bg-subtle text-ink-secondary hover:text-ink transition"
          >
            <ArrowLeft className="w-4 h-4" />
          </Link>
          <div>
            <div className="flex items-center gap-2">
              <span className="text-[11px] font-mono uppercase px-2 py-0.5 rounded bg-accent-soft text-accent font-semibold">
                Coach Studio · {taskType === "task1" ? "Task 1" : "Task 2"}
              </span>
              <span className="text-xs text-ink-muted">
                Target pace: ~{recMinutes} min · {minWords}+ words
              </span>
            </div>
            <h1 className="font-serif text-lg sm:text-xl font-semibold text-ink mt-0.5">
              {prompt.title}
            </h1>
          </div>
        </div>

        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={() => setTimerPaused((p) => !p)}
            title="Click to pause/resume study stopwatch"
            className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-line bg-surface text-xs font-mono tabular-nums text-ink cursor-pointer"
          >
            <Clock className="w-3.5 h-3.5 text-accent" />
            <span>
              {mm}:{ss}
            </span>
            {timerPaused && <span className="text-warm">(Paused)</span>}
          </button>

          <button
            id="submit-coach-essay-btn"
            type="button"
            disabled={submitting || wordCount < 10}
            onClick={handleSubmitEssay}
            className="flex items-center gap-2 px-4 py-2 rounded-xl bg-accent hover:bg-accent-hover text-accent-fg text-xs sm:text-sm font-semibold transition cursor-pointer disabled:opacity-50"
          >
            {submitting ? (
              <>
                <Loader2 className="w-4 h-4 animate-spin" />
                <span>Examiner Grading...</span>
              </>
            ) : (
              <>
                <Send className="w-3.5 h-3.5" />
                <span>Submit for Band Grading</span>
              </>
            )}
          </button>
        </div>
      </div>

      {error && (
        <div className="p-3.5 rounded-xl border border-red-400 bg-red-500/10 text-xs sm:text-sm text-red-600 dark:text-red-300">
          {error}
        </div>
      )}

      {/* Split Studio Workspace */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
        {/* Left Column: Prompt + Visual + 5-Min Interactive Outline Check */}
        <div className="lg:col-span-5 space-y-5">
          <section className="editorial-surface rounded-2xl p-5">
            <span className="text-[11px] font-mono uppercase tracking-wider text-ink-muted">
              Prompt Instructions
            </span>
            <p className="text-sm text-ink leading-relaxed whitespace-pre-line mt-2">
              {prompt.promptText}
            </p>
            {taskType === "task1" && (
              <TaskVisualRenderer
                chartSpecJson={prompt.chartSpecJson}
                imageDataUrl={prompt.imageDataUrl}
              />
            )}
          </section>

          {/* 5-Minute Plan-First Outline Check */}
          <section className="editorial-surface rounded-2xl p-5 space-y-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <Compass className="w-4 h-4 text-accent" />
                <h2 className="font-serif text-base font-semibold text-ink">
                  Step 1 · 5-Min Outline Check
                </h2>
              </div>
              <button
                type="button"
                onClick={() => setOutlineOpen((o) => !o)}
                className="text-xs font-medium text-accent hover:underline cursor-pointer"
              >
                {outlineOpen ? "Collapse" : "Expand"}
              </button>
            </div>

            {outlineOpen && (
              <div className="space-y-3">
                <p className="text-xs text-ink-secondary leading-relaxed">
                  {taskType === "task1"
                    ? "Sketch your number-free Macro Overview + how you will group figures across Body 1 & Body 2 before writing."
                    : "Lock in a clear Thesis position + two distinct, extended Body Paragraph claims before writing."}
                </p>

                <div>
                  <label className="block text-[11px] font-semibold uppercase tracking-wider text-ink-secondary mb-1">
                    {taskType === "task1"
                      ? "Macro Overview (2–3 overarching trends, NO raw numbers)"
                      : "Thesis & Clear Position"}
                  </label>
                  <textarea
                    id="outline-thesis-input"
                    rows={2}
                    value={outline.thesisOrOverview}
                    onChange={(e) =>
                      setOutline({ ...outline, thesisOrOverview: e.target.value })
                    }
                    placeholder={
                      taskType === "task1"
                        ? "Overall, coal collapsed while renewables surged to become the dominant power source..."
                        : "While applied research delivers short-term utility, restricting theoretical grants would cripple foundational breakthroughs..."
                    }
                    className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-xs sm:text-sm text-ink"
                  />
                </div>

                <div className="grid grid-cols-1 sm:grid-cols-2 gap-2.5">
                  <div>
                    <label className="block text-[11px] font-semibold uppercase tracking-wider text-ink-secondary mb-1">
                      Body 1 Grouping / Claim
                    </label>
                    <textarea
                      id="outline-body1-input"
                      rows={2}
                      value={outline.body1Plan}
                      onChange={(e) =>
                        setOutline({ ...outline, body1Plan: e.target.value })
                      }
                      placeholder={
                        taskType === "task1"
                          ? "Fossil fuels: Coal 65%->0% vs Gas peaking in 2010..."
                          : "Core claim + causal mechanism + concrete example..."
                      }
                      className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-xs text-ink"
                    />
                  </div>
                  <div>
                    <label className="block text-[11px] font-semibold uppercase tracking-wider text-ink-secondary mb-1">
                      Body 2 Grouping / Claim
                    </label>
                    <textarea
                      id="outline-body2-input"
                      rows={2}
                      value={outline.body2Plan}
                      onChange={(e) =>
                        setOutline({ ...outline, body2Plan: e.target.value })
                      }
                      placeholder={
                        taskType === "task1"
                          ? "Low-carbon: Renewables overtake Gas in 2020 (51%) vs steady Nuclear..."
                          : "Second distinct claim / counter-perspective + example..."
                      }
                      className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-xs text-ink"
                    />
                  </div>
                </div>

                <button
                  id="validate-outline-btn"
                  type="button"
                  disabled={checkingOutline || !outline.thesisOrOverview.trim()}
                  onClick={handleValidateOutline}
                  className="w-full flex items-center justify-center gap-2 py-2 px-3 rounded-lg bg-subtle hover:bg-line text-ink text-xs font-semibold transition cursor-pointer disabled:opacity-50"
                >
                  {checkingOutline ? (
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  ) : (
                    <CheckCircle2 className="w-3.5 h-3.5 text-accent" />
                  )}
                  <span>
                    {checkingOutline ? "Validating Plan..." : "Validate Outline"}
                  </span>
                </button>

                {outlineFeedback && (
                  <div className="mt-3 p-3.5 rounded-xl bg-canvas border border-line-strong space-y-2.5 text-xs">
                    <div className="flex items-center justify-between">
                      <span
                        className={`px-2 py-0.5 rounded font-mono font-semibold uppercase text-[10px] ${
                          outlineFeedback.readiness === "strong"
                            ? "bg-accent-soft text-accent"
                            : "bg-warm-soft text-warm"
                        }`}
                      >
                        {outlineFeedback.readiness.replace("_", " ")}
                      </span>
                      <span className="font-mono font-semibold text-ink">
                        Plan Ceiling: ~Band{" "}
                        {outlineFeedback.estimatedTaskBandCeiling.toFixed(1)}
                      </span>
                    </div>

                    <p className="text-ink font-medium">{outlineFeedback.verdictSummary}</p>
                    <p className="text-ink-secondary">
                      <strong>Overview/Thesis:</strong>{" "}
                      {outlineFeedback.overviewOrThesisCheck}
                    </p>
                    <p className="text-ink-secondary">
                      <strong>Paragraphing:</strong> {outlineFeedback.bodyGroupingCheck}
                    </p>

                    {outlineFeedback.suggestedAcademicCollocations?.length > 0 && (
                      <div className="pt-2 border-t border-line space-y-1.5">
                        <p className="text-[11px] font-mono uppercase text-ink-muted">
                          High-Register Collocations for This Topic
                        </p>
                        {outlineFeedback.suggestedAcademicCollocations.map((c, idx) => (
                          <div
                            key={idx}
                            className="p-2 rounded-lg bg-subtle/70 flex items-start justify-between gap-2"
                          >
                            <div>
                              <span className="font-semibold text-ink">
                                &ldquo;{c.phrase}&rdquo;
                              </span>
                              <p className="text-[11px] text-ink-secondary">
                                {c.usageNote}
                              </p>
                            </div>
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                )}
              </div>
            )}
          </section>
        </div>

        {/* Right Column: Distraction-Free Writing Sheet + Surgical On-Demand Toolkit */}
        <div className="lg:col-span-7 space-y-4">
          <section className="editorial-surface rounded-2xl p-5 sm:p-6 space-y-4">
            <div className="flex flex-wrap items-center justify-between gap-2 pb-3 border-b border-line">
              <div className="flex items-center gap-3">
                <span className="text-xs font-mono tabular-nums font-semibold text-ink">
                  {wordCount} / {minWords}+ words
                </span>
                <div className="w-28 h-1.5 rounded-full bg-subtle overflow-hidden">
                  <div
                    className="h-full bg-accent transition-all duration-300"
                    style={{ width: `${wordProgress}%` }}
                  />
                </div>
              </div>

              <button
                type="button"
                onClick={() => setSerifEditor((s) => !s)}
                className="text-xs font-mono text-ink-muted hover:text-ink cursor-pointer"
              >
                Font: {serifEditor ? "Editorial Serif" : "Clean Sans"}
              </button>
            </div>

            <textarea
              id="coach-essay-textarea"
              ref={editorRef}
              value={essayText}
              onChange={(e) => setEssayText(e.target.value)}
              onSelect={captureSelection}
              onKeyUp={captureSelection}
              onMouseUp={captureSelection}
              rows={17}
              placeholder={
                taskType === "task1"
                  ? "Begin with a concise 1-sentence paraphrase of the chart, followed immediately by your 2-sentence Overview ('Overall, ...')..."
                  : "Introduce the topic and state your clear position, then develop two well-supported body paragraphs..."
              }
              className={`w-full bg-transparent text-ink placeholder:text-ink-muted/60 focus:outline-none resize-y min-h-[380px] ${
                serifEditor
                  ? "editorial-prose"
                  : "font-sans text-base leading-relaxed"
              }`}
            />

            {/* On-Demand Surgical Toolkit Bar (No Ghost Autocomplete) */}
            <div className="pt-3 border-t border-line space-y-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="text-[11px] font-mono uppercase tracking-wider text-ink-muted">
                  On-Demand Coach Toolkit{" "}
                  {selectedSnippet
                    ? `(Selected: "${selectedSnippet.slice(0, 28)}${
                        selectedSnippet.length > 28 ? "…" : ""
                      }")`
                    : "(Inspects highlighted phrase or current paragraph)"}
                </span>
              </div>

              <div className="flex flex-wrap gap-2">
                <button
                  id="toolkit-collocations-btn"
                  type="button"
                  disabled={Boolean(toolkitLoading)}
                  onClick={() => handleRunToolkit("collocations")}
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-line-strong bg-canvas hover:bg-subtle text-xs font-medium text-ink transition cursor-pointer"
                >
                  {toolkitLoading === "collocations" ? (
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  ) : (
                    <Sparkles className="w-3.5 h-3.5 text-accent" />
                  )}
                  <span>Academic Collocations &amp; Hedging</span>
                </button>

                <button
                  id="toolkit-l1-btn"
                  type="button"
                  disabled={Boolean(toolkitLoading)}
                  onClick={() => handleRunToolkit("l1_check")}
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-line-strong bg-canvas hover:bg-subtle text-xs font-medium text-ink transition cursor-pointer"
                >
                  {toolkitLoading === "l1_check" ? (
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  ) : (
                    <Languages className="w-3.5 h-3.5 text-warm" />
                  )}
                  <span>L1 False-Cognate &amp; Clause Scan</span>
                </button>

                <button
                  id="toolkit-paragraph-btn"
                  type="button"
                  disabled={Boolean(toolkitLoading)}
                  onClick={() => handleRunToolkit("paragraph_check")}
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-line-strong bg-canvas hover:bg-subtle text-xs font-medium text-ink transition cursor-pointer"
                >
                  {toolkitLoading === "paragraph_check" ? (
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  ) : (
                    <AlignLeft className="w-3.5 h-3.5 text-ink-secondary" />
                  )}
                  <span>Check Current Paragraph</span>
                </button>
              </div>

              {toolkitResult && (
                <div className="p-4 rounded-xl bg-canvas border border-line-strong space-y-2.5 text-xs">
                  <div className="flex items-center justify-between">
                    <h3 className="font-semibold text-sm text-ink">
                      {toolkitResult.headline}
                    </h3>
                    <button
                      type="button"
                      onClick={() => setToolkitResult(null)}
                      className="text-ink-muted hover:text-ink cursor-pointer"
                    >
                      Dismiss
                    </button>
                  </div>
                  <p className="text-ink-secondary leading-relaxed">
                    {toolkitResult.analysis}
                  </p>
                  <div className="space-y-2 pt-1">
                    {toolkitResult.suggestions.map((sug, idx) => (
                      <div
                        key={idx}
                        className="p-2.5 rounded-lg bg-subtle flex items-start justify-between gap-3"
                      >
                        <div className="space-y-0.5">
                          <span className="text-[10px] font-mono uppercase font-semibold text-accent">
                            {sug.label}
                          </span>
                          <p className="text-ink font-medium">{sug.rewrittenOrPhrase}</p>
                          <p className="text-[11px] text-ink-secondary">{sug.rationale}</p>
                        </div>
                        <button
                          type="button"
                          onClick={() => {
                            navigator.clipboard?.writeText(sug.rewrittenOrPhrase);
                            setCopiedIdx(idx);
                            setTimeout(() => setCopiedIdx(null), 1500);
                          }}
                          title="Copy phrase"
                          className="p-1.5 rounded hover:bg-surface text-ink-secondary shrink-0 cursor-pointer"
                        >
                          {copiedIdx === idx ? (
                            <Check className="w-3.5 h-3.5 text-accent" />
                          ) : (
                            <Copy className="w-3.5 h-3.5" />
                          )}
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          </section>
        </div>
      </div>
    </div>
  );
}
