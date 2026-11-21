import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router";
import {
  Clock,
  Eye,
  EyeOff,
  Highlighter,
  StickyNote,
  CheckCircle2,
  Loader2,
  RotateCcw,
  Send,
  AlertTriangle,
} from "lucide-react";
import { TaskVisualRenderer } from "./TaskVisualRenderer";
import { countEssayWords } from "@/lib/ielts-utils";
import {
  submitEvaluationStream,
  type DraftSubmissionPayload,
} from "@/lib/client-evaluate";
import type { SessionMode } from "@/lib/types";
import type { PromptRecord } from "./PromptModals";
import { TEXT_LIMITS } from "@/lib/limits";

export interface RestoredSimulatorPart {
  essayText: string;
  secondsSpent: number;
  promptHighlights?: string[];
  scratchNotes?: string | null;
}

export function InsperaSimulator({
  sessionId,
  mode,
  task1Prompt,
  task2Prompt,
  initialDrafts,
}: {
  sessionId: string;
  mode: SessionMode;
  task1Prompt: PromptRecord | null;
  task2Prompt: PromptRecord | null;
  initialDrafts?: {
    task1?: RestoredSimulatorPart | null;
    task2?: RestoredSimulatorPart | null;
  };
}) {
  const navigate = useNavigate();
  const isFull60 = mode === "simulator_full_60m";
  const initialTotalSeconds =
    mode === "simulator_t1" ? 20 * 60 : mode === "simulator_t2" ? 40 * 60 : 60 * 60;
  const elapsedInitial =
    (initialDrafts?.task1?.secondsSpent || 0) +
    (initialDrafts?.task2?.secondsSpent || 0);

  const [activePart, setActivePart] = useState<"task1" | "task2">(
    task1Prompt ? "task1" : "task2"
  );
  const [remainingSeconds, setRemainingSeconds] = useState(
    Math.max(0, initialTotalSeconds - elapsedInitial)
  );
  const [hideClockDigits, setHideClockDigits] = useState(false);
  const [leftWidthPct, setLeftWidthPct] = useState<42 | 50 | 58>(50);

  // Per-part essay, time spent, highlights & scratch notes
  const [essays, setEssays] = useState<{ task1: string; task2: string }>({
    task1: initialDrafts?.task1?.essayText || "",
    task2: initialDrafts?.task2?.essayText || "",
  });
  const [secondsPerPart, setSecondsPerPart] = useState<{ task1: number; task2: number }>({
    task1: initialDrafts?.task1?.secondsSpent || 0,
    task2: initialDrafts?.task2?.secondsSpent || 0,
  });
  const [highlights, setHighlights] = useState<{ task1: string[]; task2: string[] }>({
    task1: initialDrafts?.task1?.promptHighlights || [],
    task2: initialDrafts?.task2?.promptHighlights || [],
  });
  const [notesOpen, setNotesOpen] = useState(false);
  const [scratchNotes, setScratchNotes] = useState<{ task1: string; task2: string }>({
    task1: initialDrafts?.task1?.scratchNotes || "",
    task2: initialDrafts?.task2?.scratchNotes || "",
  });

  const [confirmModal, setConfirmModal] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [gradingStage, setGradingStage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const completedPromptIdsRef = useRef<Set<string>>(new Set());

  useEffect(() => {
    if (submitting) return;
    const timer = setInterval(() => {
      setRemainingSeconds((r) => (r > 0 ? r - 1 : 0));
      setSecondsPerPart((prev) => ({
        ...prev,
        [activePart]: prev[activePart] + 1,
      }));
    }, 1000);
    return () => clearInterval(timer);
  }, [activePart, submitting]);

  const currentPrompt = activePart === "task1" ? task1Prompt : task2Prompt;
  const t1Words = countEssayWords(essays.task1);
  const t2Words = countEssayWords(essays.task2);
  const currentWords = activePart === "task1" ? t1Words : t2Words;

  function handleHighlightSelection() {
    const sel = window.getSelection()?.toString().trim();
    if (!sel || sel.length < 3) return;
    setHighlights((prev) => {
      const existing = prev[activePart];
      if (existing.includes(sel)) return prev;
      return { ...prev, [activePart]: [...existing, sel] };
    });
    window.getSelection()?.removeAllRanges();
  }

  function renderPromptWithHighlights(rawText: string, terms: string[]) {
    if (!terms.length) {
      return <p className="text-[15px] leading-relaxed whitespace-pre-line">{rawText}</p>;
    }
    let segments: Array<{ text: string; highlighted: boolean }> = [
      { text: rawText, highlighted: false },
    ];
    for (const term of terms) {
      const next: typeof segments = [];
      for (const seg of segments) {
        if (seg.highlighted) {
          next.push(seg);
          continue;
        }
        const idx = seg.text.indexOf(term);
        if (idx === -1) {
          next.push(seg);
        } else {
          if (idx > 0) next.push({ text: seg.text.slice(0, idx), highlighted: false });
          next.push({ text: term, highlighted: true });
          const after = seg.text.slice(idx + term.length);
          if (after) next.push({ text: after, highlighted: false });
        }
      }
      segments = next;
    }
    return (
      <p className="text-[15px] leading-relaxed whitespace-pre-line">
        {segments.map((s, i) =>
          s.highlighted ? (
            <mark key={i} className="bg-yellow-200 px-0.5 rounded-xs text-slate-900">
              {s.text}
            </mark>
          ) : (
            <span key={i}>{s.text}</span>
          )
        )}
      </p>
    );
  }

  async function executeSubmission() {
    setSubmitting(true);
    setError(null);

    const allSubmissions: DraftSubmissionPayload[] = [];

    if (task1Prompt && (mode === "simulator_t1" || isFull60)) {
      allSubmissions.push({
        promptId: task1Prompt.id,
        taskType: "task1",
        essayText: essays.task1.trim() || "(No response entered for Task 1)",
        secondsSpent: secondsPerPart.task1,
        promptHighlights: highlights.task1,
        scratchNotes: scratchNotes.task1,
      });
    }

    if (task2Prompt && (mode === "simulator_t2" || isFull60)) {
      allSubmissions.push({
        promptId: task2Prompt.id,
        taskType: "task2",
        essayText: essays.task2.trim() || "(No response entered for Task 2)",
        secondsSpent: secondsPerPart.task2,
        promptHighlights: highlights.task2,
        scratchNotes: scratchNotes.task2,
      });
    }

    const totalSeconds = secondsPerPart.task1 + secondsPerPart.task2;

    // Submit each part in its own dedicated request so 60m Full Mock Exams never exceed per-invocation time limits
    for (let idx = 0; idx < allSubmissions.length; idx++) {
      const sub = allSubmissions[idx];
      if (completedPromptIdsRef.current.has(sub.promptId)) {
        continue;
      }

      const partLabel =
        allSubmissions.length > 1
          ? `Part ${idx + 1} of ${allSubmissions.length}: `
          : "";

      setGradingStage(`${partLabel}Examiner reviewing response...`);

      const outcome = await submitEvaluationStream(
        {
          sessionId,
          totalSecondsSpent: totalSeconds,
          submissions: [sub],
        },
        (stage) => setGradingStage(`${partLabel}${stage}`)
      );

      if (!outcome.ok) {
        setError(outcome.error || "Grading failed. Please try again.");
        setSubmitting(false);
        setGradingStage(null);
        return;
      }

      completedPromptIdsRef.current.add(sub.promptId);
    }

    navigate(`/session/${sessionId}`);
  }

  const minsLeft = Math.floor(remainingSeconds / 60);
  const secsLeft = remainingSeconds % 60;
  const urgentTime = remainingSeconds <= 300;

  return (
    <div className="inspera-shell h-screen flex flex-col overflow-hidden select-none">
      {/* Official Inspera Dark Top Exam Banner */}
      <header className="h-14 bg-[#1d242b] text-white px-4 sm:px-6 flex items-center justify-between shrink-0 border-b border-slate-700">
        <div className="flex items-center gap-4">
          <span className="font-bold tracking-wide text-sm sm:text-base">
            IELTS Academic Writing
          </span>
          <span className="hidden md:inline-block text-xs text-slate-300 font-mono">
            Candidate: FE-{sessionId.slice(-5).toUpperCase()}
          </span>
        </div>

        {/* Center Countdown Clock */}
        <div className="flex items-center gap-2">
          <div
            className={`flex items-center gap-2 px-3 py-1 rounded-md font-mono text-sm font-semibold tabular-nums ${
              urgentTime ? "bg-red-600 text-white" : "bg-slate-800 text-slate-100"
            }`}
          >
            <Clock className="w-4 h-4" />
            {hideClockDigits ? (
              <span>Timer Hidden</span>
            ) : (
              <span>
                {String(minsLeft).padStart(2, "0")}:{String(secsLeft).padStart(2, "0")}{" "}
                remaining
              </span>
            )}
          </div>
          <button
            type="button"
            onClick={() => setHideClockDigits((h) => !h)}
            title="Show/Hide Timer"
            className="p-1.5 rounded bg-slate-800 hover:bg-slate-700 text-slate-300 cursor-pointer"
          >
            {hideClockDigits ? (
              <Eye className="w-4 h-4" />
            ) : (
              <EyeOff className="w-4 h-4" />
            )}
          </button>
        </div>

        <div className="flex items-center gap-2">
          <button
            id="inspera-finish-test-btn"
            type="button"
            disabled={submitting}
            onClick={() => setConfirmModal(true)}
            className="flex items-center gap-1.5 px-4 py-1.5 rounded bg-emerald-600 hover:bg-emerald-500 text-white text-xs sm:text-sm font-bold transition cursor-pointer"
          >
            <Send className="w-3.5 h-3.5" />
            <span>Submit Test</span>
          </button>
        </div>
      </header>

      {/* Official Sub-instruction Ribbon + Tools */}
      <div className="bg-[#f1f3f5] border-b border-slate-300 px-4 sm:px-6 py-2 flex flex-wrap items-center justify-between gap-2 shrink-0 text-xs text-slate-800">
        <div>
          <strong>
            {activePart === "task1" ? "Part 1 (Academic Task 1)" : "Part 2 (Academic Task 2)"}:
          </strong>{" "}
          {activePart === "task1"
            ? "You should spend about 20 minutes on this task. Write at least 150 words."
            : "You should spend about 40 minutes on this task. Write at least 250 words."}
        </div>

        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={handleHighlightSelection}
            className="inline-flex items-center gap-1 px-2.5 py-1 rounded border border-slate-300 bg-white hover:bg-yellow-50 text-slate-800 font-medium cursor-pointer"
          >
            <Highlighter className="w-3.5 h-3.5 text-amber-600" />
            <span>Highlight Prompt Text</span>
          </button>

          <button
            type="button"
            onClick={() => setNotesOpen((n) => !n)}
            className={`inline-flex items-center gap-1 px-2.5 py-1 rounded border font-medium cursor-pointer ${
              notesOpen
                ? "border-slate-700 bg-slate-800 text-white"
                : "border-slate-300 bg-white text-slate-800"
            }`}
          >
            <StickyNote className="w-3.5 h-3.5" />
            <span>Notes</span>
          </button>

          <div className="hidden lg:flex items-center border border-slate-300 rounded overflow-hidden bg-white">
            {([42, 50, 58] as const).map((pct) => (
              <button
                key={pct}
                type="button"
                onClick={() => setLeftWidthPct(pct)}
                className={`px-2 py-1 text-[11px] font-mono cursor-pointer ${
                  leftWidthPct === pct
                    ? "bg-slate-800 text-white font-bold"
                    : "text-slate-600 hover:bg-slate-100"
                }`}
              >
                {pct}/{100 - pct}
              </button>
            ))}
          </div>
        </div>
      </div>

      {error && (
        <div className="bg-amber-100 border-b border-amber-300 text-slate-900 px-6 py-2 text-xs font-semibold flex flex-wrap items-center justify-between gap-3">
          <span>{error}</span>
          <button
            type="button"
            disabled={submitting}
            onClick={executeSubmission}
            className="inline-flex items-center gap-1.5 px-3 py-1 rounded bg-emerald-700 hover:bg-emerald-600 text-white text-xs font-bold cursor-pointer"
          >
            <RotateCcw className="w-3.5 h-3.5" />
            <span>Retry Grading</span>
          </button>
        </div>
      )}

      {/* Main Split-Screen Exam Area */}
      <div className="flex-1 flex flex-col lg:flex-row overflow-hidden select-text">
        {/* Left Scrollable Prompt & Chart Pane */}
        <div
          style={{ width: undefined }}
          className={`bg-white border-b lg:border-b-0 lg:border-r-4 border-slate-300 overflow-y-auto p-5 sm:p-6 ${
            leftWidthPct === 42
              ? "lg:w-[42%]"
              : leftWidthPct === 58
              ? "lg:w-[58%]"
              : "lg:w-1/2"
          }`}
        >
          {currentPrompt && (
            <div className="space-y-4">
              <div className="p-4 rounded border border-slate-300 bg-slate-50">
                {renderPromptWithHighlights(
                  currentPrompt.promptText,
                  highlights[activePart]
                )}
              </div>

              {highlights[activePart].length > 0 && (
                <button
                  type="button"
                  onClick={() =>
                    setHighlights((prev) => ({ ...prev, [activePart]: [] }))
                  }
                  className="text-[11px] text-slate-500 underline cursor-pointer"
                >
                  Clear prompt highlights
                </button>
              )}

              {activePart === "task1" && (
                <TaskVisualRenderer
                  chartSpecJson={currentPrompt.chartSpecJson}
                  imageDataUrl={currentPrompt.imageDataUrl}
                />
              )}

              {notesOpen && (
                <div className="mt-4 p-3.5 rounded-lg border border-amber-300 bg-amber-50 space-y-1.5">
                  <label className="block text-xs font-bold text-amber-950">
                    Candidate Scratchpad Notes (Not graded by examiner)
                  </label>
                  <textarea
                    rows={4}
                    value={scratchNotes[activePart]}
                    maxLength={TEXT_LIMITS.scratchNotes}
                    onChange={(e) =>
                      setScratchNotes((prev) => ({
                        ...prev,
                        [activePart]: e.target.value,
                      }))
                    }
                    placeholder="Jot down your 3-minute plan, overview trends, or paragraph ideas..."
                    className="w-full p-2 rounded bg-white border border-amber-300 text-xs text-slate-900 focus:outline-none"
                  />
                </div>
              )}
            </div>
          )}
        </div>

        {/* Right Plain Spellcheck-Disabled Exam Editor Pane */}
        <div
          className={`flex-1 flex flex-col bg-[#f8fafc] p-4 sm:p-6 overflow-hidden ${
            leftWidthPct === 42
              ? "lg:w-[58%]"
              : leftWidthPct === 58
              ? "lg:w-[42%]"
              : "lg:w-1/2"
          }`}
        >
          <textarea
            id="inspera-exam-textarea"
            spellCheck={false}
            autoCorrect="off"
            autoCapitalize="off"
            autoComplete="off"
            data-gramm="false"
            value={essays[activePart]}
            maxLength={TEXT_LIMITS.essay}
            onChange={(e) =>
              setEssays((prev) => ({ ...prev, [activePart]: e.target.value }))
            }
            placeholder="Type your response here..."
            className="inspera-editor flex-1 w-full p-4 rounded border border-slate-400 resize-none shadow-inner"
          />

          <div className="mt-2.5 flex items-center justify-between text-xs font-mono text-slate-700">
            <span>Spellcheck &amp; Autocorrect: OFF (Exam Mode)</span>
            <span className="font-bold text-sm text-slate-900 tabular-nums">
              Words: {currentWords}
            </span>
          </div>
        </div>
      </div>

      {/* Official Bottom Part 1 / Part 2 Switcher Bar */}
      <footer className="h-12 bg-[#e5e7eb] border-t border-slate-300 px-4 sm:px-6 flex items-center justify-between shrink-0">
        <div className="flex items-center gap-2">
          {task1Prompt && (
            <button
              id="inspera-tab-part1"
              type="button"
              onClick={() => setActivePart("task1")}
              className={`flex items-center gap-2 px-4 py-1.5 rounded text-xs font-bold transition cursor-pointer ${
                activePart === "task1"
                  ? "bg-[#1d242b] text-white"
                  : "bg-white text-slate-800 border border-slate-300 hover:bg-slate-100"
              }`}
            >
              {t1Words >= 150 && <CheckCircle2 className="w-3.5 h-3.5 text-emerald-400" />}
              <span>Part 1 ({t1Words}/150w)</span>
            </button>
          )}

          {task2Prompt && (
            <button
              id="inspera-tab-part2"
              type="button"
              onClick={() => setActivePart("task2")}
              className={`flex items-center gap-2 px-4 py-1.5 rounded text-xs font-bold transition cursor-pointer ${
                activePart === "task2"
                  ? "bg-[#1d242b] text-white"
                  : "bg-white text-slate-800 border border-slate-300 hover:bg-slate-100"
              }`}
            >
              {t2Words >= 250 && <CheckCircle2 className="w-3.5 h-3.5 text-emerald-400" />}
              <span>Part 2 ({t2Words}/250w)</span>
            </button>
          )}
        </div>

        <div className="text-[11px] font-mono text-slate-600 hidden sm:block">
          {isFull60
            ? `Time split — Part 1: ${Math.floor(secondsPerPart.task1 / 60)}m · Part 2: ${Math.floor(
                secondsPerPart.task2 / 60
              )}m`
            : "Computer-Delivered IELTS Simulator"}
        </div>
      </footer>

      {/* Submission Confirmation Modal */}
      {confirmModal && (
        <div className="fixed inset-0 z-50 bg-black/65 flex items-center justify-center p-4">
          <div className="w-full max-w-md bg-white text-slate-900 rounded-xl p-6 space-y-4 shadow-2xl">
            <h2 className="text-lg font-bold">Submit IELTS Writing Test?</h2>
            <div className="space-y-2 text-sm text-slate-700">
              {task1Prompt && (
                <div className="flex items-center justify-between p-2.5 rounded bg-slate-100">
                  <span>Part 1 (Task 1 · min 150 words)</span>
                  <span
                    className={`font-mono font-bold ${
                      t1Words < 150 ? "text-amber-700" : "text-emerald-700"
                    }`}
                  >
                    {t1Words} words
                  </span>
                </div>
              )}
              {task2Prompt && (
                <div className="flex items-center justify-between p-2.5 rounded bg-slate-100">
                  <span>Part 2 (Task 2 · min 250 words)</span>
                  <span
                    className={`font-mono font-bold ${
                      t2Words < 250 ? "text-amber-700" : "text-emerald-700"
                    }`}
                  >
                    {t2Words} words
                  </span>
                </div>
              )}

              {((task1Prompt && t1Words < 150) || (task2Prompt && t2Words < 250)) && (
                <p className="flex items-start gap-2 text-xs text-amber-800 bg-amber-50 border border-amber-200 p-2.5 rounded">
                  <AlertTriangle className="w-4 h-4 shrink-0 mt-0.5" />
                  <span>
                    One or more responses are below the official minimum word threshold
                    and will incur a Task Achievement / Response penalty.
                  </span>
                </p>
              )}
              {submitting && gradingStage && (
                <div className="flex items-center gap-2 text-xs text-emerald-900 bg-emerald-50 border border-emerald-200 p-2.5 rounded font-medium">
                  <Loader2 className="w-4 h-4 animate-spin text-emerald-700 shrink-0" />
                  <span>{gradingStage}</span>
                </div>
              )}

              {error && (
                <div className="flex items-start gap-2 text-xs text-red-900 bg-red-50 border border-red-200 p-2.5 rounded font-medium">
                  <AlertTriangle className="w-4 h-4 text-red-600 shrink-0 mt-0.5" />
                  <span>{error}</span>
                </div>
              )}
            </div>

            <div className="flex items-center justify-end gap-2.5 pt-2">
              <button
                type="button"
                disabled={submitting}
                onClick={() => setConfirmModal(false)}
                className="px-4 py-2 rounded border border-slate-300 text-xs font-bold text-slate-700 cursor-pointer"
              >
                Return to Test
              </button>
              <button
                id="confirm-submit-exam-btn"
                type="button"
                disabled={submitting}
                onClick={executeSubmission}
                className="flex items-center gap-2 px-4 py-2 rounded bg-emerald-600 hover:bg-emerald-500 text-white text-xs font-bold cursor-pointer disabled:opacity-60"
              >
                {submitting ? (
                  <Loader2 className="w-4 h-4 animate-spin" />
                ) : error ? (
                  <RotateCcw className="w-3.5 h-3.5" />
                ) : null}
                <span>
                  {submitting
                    ? "Examiner Grading..."
                    : error
                    ? "Retry Grading"
                    : "Confirm & Grade Now"}
                </span>
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
