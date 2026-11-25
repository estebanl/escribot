import { useEffect, useRef, useState } from "react";
import { Link, useNavigate } from "react-router";
import {
  ArrowLeft,
  CheckCircle2,
  Clock,
  Compass,
  Loader2,
  RotateCcw,
  Send,
  Sparkles,
  Languages,
  AlignLeft,
  Copy,
  Check,
  AlertTriangle,
  Bookmark,
  BookmarkCheck,
  Target,
  History,
  Tags,
} from "lucide-react";
import { TaskVisualRenderer } from "./TaskVisualRenderer";
import { ModelAnswer } from "./ModelAnswer";
import { countEssayWords, getMinimumWords, getRecommendedMinutes } from "@/lib/ielts-utils";
import { getTaskKeywords, isKeywordUsedInText } from "@/lib/task-keywords";
import {
  safePostJson,
  submitEvaluationStream,
  type EvaluationErrorKind,
} from "@/lib/client-evaluate";
import type {
  OutlineFeedback,
  OutlineInput,
  TaskType,
  ToolkitResponse,
} from "@/lib/types";
import type { PromptRecord } from "./PromptModals";
import { TEXT_LIMITS } from "@/lib/limits";

export interface RestoredCoachDraft {
  essayText: string;
  secondsSpent: number;
  outline?: OutlineInput | null;
  outlineFeedback?: OutlineFeedback | null;
}

/** Examiner priorities carried over from the student's most recent graded draft of this task type. */
export interface PreviousFocus {
  overallBand: number;
  topPriorities: string[];
  fromVersion: number;
  /** True when the priorities come from an earlier version of this same essay. */
  isRedraft: boolean;
}

type ToolName = ToolkitResponse["tool"];

/** A toolkit result plus the exact text the coach inspected, so feedback stays anchored to the draft. */
interface ToolkitEntry extends ToolkitResponse {
  checkedText: string;
}

interface LocalCoachDraft {
  essayText: string;
  outline: OutlineInput;
  outlineFeedback: OutlineFeedback | null;
  validatedOutline: OutlineInput | null;
  elapsedSeconds: number;
  savedAt: string;
}

const AUTOSAVE_DEBOUNCE_MS = 1500;

function outlineKey(o: OutlineInput | null | undefined): string {
  if (!o) return "";
  return [o.thesisOrOverview, o.body1Plan, o.body2Plan]
    .map((v) => (v || "").trim())
    .join("␞");
}

/**
 * Returns the paragraph containing the caret (paragraphs are separated by blank lines),
 * falling back to the last non-empty paragraph.
 */
function paragraphAtCursor(text: string, cursor: number | null | undefined): string {
  const separator = /\n\s*\n/g;
  const paragraphs: Array<{ start: number; end: number }> = [];
  let start = 0;
  let match: RegExpExecArray | null;
  while ((match = separator.exec(text)) !== null) {
    paragraphs.push({ start, end: match.index });
    start = match.index + match[0].length;
  }
  paragraphs.push({ start, end: text.length });

  if (typeof cursor === "number") {
    const hit = paragraphs.find((p) => cursor >= p.start && cursor <= p.end);
    const hitText = hit ? text.slice(hit.start, hit.end).trim() : "";
    if (hitText) return hitText;
  }
  for (let i = paragraphs.length - 1; i >= 0; i--) {
    const candidate = text.slice(paragraphs[i].start, paragraphs[i].end).trim();
    if (candidate) return candidate;
  }
  return "";
}

function toolCriterion(tool: ToolName): string {
  if (tool === "l1_check") return "L1";
  if (tool === "paragraph_check") return "CC";
  return "LR";
}

export function CoachWorkspace({
  sessionId,
  prompt,
  initialDraft,
  previousFocus,
}: {
  sessionId: string;
  prompt: PromptRecord;
  initialDraft?: RestoredCoachDraft | null;
  previousFocus?: PreviousFocus | null;
}) {
  const navigate = useNavigate();
  const taskType = (prompt.taskType === "task1" ? "task1" : "task2") as TaskType;
  const minWords = getMinimumWords(taskType);
  const recMinutes = getRecommendedMinutes(taskType);

  // Step 1: 5-Minute Outline Check state
  const [outlineOpen, setOutlineOpen] = useState(
    !initialDraft?.essayText || Boolean(initialDraft?.outline?.thesisOrOverview)
  );
  const [outline, setOutline] = useState<OutlineInput>(
    initialDraft?.outline || {
      thesisOrOverview: "",
      body1Plan: "",
      body2Plan: "",
      targetKeyTerms: "",
    }
  );
  const [outlineFeedback, setOutlineFeedback] = useState<OutlineFeedback | null>(
    initialDraft?.outlineFeedback || null
  );
  const [checkingOutline, setCheckingOutline] = useState(false);
  // Outline snapshot the current feedback was produced for (to flag stale feedback after edits)
  const [validatedOutline, setValidatedOutline] = useState<OutlineInput | null>(
    initialDraft?.outlineFeedback ? initialDraft.outline || null : null
  );
  const [doneTweaks, setDoneTweaks] = useState<Set<number>>(() => new Set());
  const [doneFocus, setDoneFocus] = useState<Set<number>>(() => new Set());

  // Step 2: Distraction-free Essay Editor state
  const [essayText, setEssayText] = useState(initialDraft?.essayText || "");
  const [serifEditor, setSerifEditor] = useState(true);
  const [elapsedSeconds, setElapsedSeconds] = useState(initialDraft?.secondsSpent || 0);
  const [timerPaused, setTimerPaused] = useState(false);
  const [keywordsOpen, setKeywordsOpen] = useState(true);
  const editorRef = useRef<HTMLTextAreaElement | null>(null);

  // On-demand Coach Toolkit state (NO autocomplete)
  const [selectedSnippet, setSelectedSnippet] = useState("");
  const [toolkitLoading, setToolkitLoading] = useState<string | null>(null);
  const [toolkitResult, setToolkitResult] = useState<ToolkitEntry | null>(null);
  const [toolkitHistory, setToolkitHistory] = useState<ToolkitEntry[]>([]);
  const [showEarlierChecks, setShowEarlierChecks] = useState(false);
  const [copiedKey, setCopiedKey] = useState<string | null>(null);

  // Save-to-Vault state (keys identify individual suggestions / collocations)
  const [savedVaultKeys, setSavedVaultKeys] = useState<Set<string>>(() => new Set());
  const [savingVaultKey, setSavingVaultKey] = useState<string | null>(null);
  const [vaultError, setVaultError] = useState<string | null>(null);

  // Local autosave state
  const storageKey = `fe-coach-draft:${sessionId}:${prompt.id}`;
  const [autosaveReady, setAutosaveReady] = useState(false);
  const [restoredNotice, setRestoredNotice] = useState(false);
  const [lastLocalSave, setLastLocalSave] = useState<string | null>(null);

  // Final evaluation submission state
  const [submitting, setSubmitting] = useState(false);
  const [gradingStage, setGradingStage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [errorKind, setErrorKind] = useState<EvaluationErrorKind | null>(null);
  const [canRetryGrading, setCanRetryGrading] = useState(false);

  useEffect(() => {
    if (timerPaused || submitting) return;
    const id = setInterval(() => {
      setElapsedSeconds((s) => s + 1);
    }, 1000);
    return () => clearInterval(id);
  }, [timerPaused, submitting]);

  // Keep the latest snapshot in a ref so the ticking timer doesn't reset the autosave debounce
  const snapshotRef = useRef<LocalCoachDraft | null>(null);
  // Set once grading succeeds so the unmount flush can't resurrect the cleared draft
  const draftClearedRef = useRef(false);
  useEffect(() => {
    snapshotRef.current = {
      essayText,
      outline,
      outlineFeedback,
      validatedOutline,
      elapsedSeconds,
      savedAt: new Date().toISOString(),
    };
  }, [essayText, outline, outlineFeedback, validatedOutline, elapsedSeconds]);

  function persistLocalDraft(): boolean {
    const snap = snapshotRef.current;
    if (!snap || draftClearedRef.current) return false;
    try {
      if (!snap.essayText.trim() && !outlineKey(snap.outline).replace(/\u241E/g, "")) {
        window.localStorage.removeItem(storageKey);
        return false;
      }
      window.localStorage.setItem(
        storageKey,
        JSON.stringify({ ...snap, savedAt: new Date().toISOString() })
      );
      return true;
    } catch {
      return false;
    }
  }

  // Restore unsaved local work (localStorage is only readable after hydration)
  useEffect(() => {
    try {
      const raw = window.localStorage.getItem(storageKey);
      if (raw) {
        const local = JSON.parse(raw) as Partial<LocalCoachDraft>;
        const serverEssay = initialDraft?.essayText || "";
        const serverOutline = outlineKey(initialDraft?.outline);
        const differs =
          typeof local.essayText === "string" &&
          (local.essayText !== serverEssay || outlineKey(local.outline) !== serverOutline);
        if (differs) {
          /* eslint-disable react-hooks/set-state-in-effect -- localStorage is only readable after hydration */
          setEssayText(local.essayText || "");
          if (local.outline) setOutline(local.outline);
          setOutlineFeedback(local.outlineFeedback ?? null);
          setValidatedOutline(local.validatedOutline ?? null);
          if (typeof local.elapsedSeconds === "number") {
            setElapsedSeconds(Math.max(local.elapsedSeconds, initialDraft?.secondsSpent || 0));
          }
          if (local.outline?.thesisOrOverview) setOutlineOpen(true);
          setRestoredNotice(true);
          /* eslint-enable react-hooks/set-state-in-effect */
        }
      }
    } catch {
      // Corrupt or blocked storage: start from the server draft
    }
    setAutosaveReady(true);
    // Run once on mount for this session/prompt
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storageKey]);

  // Coarse timer bucket so the stored duration refreshes every ~30s without
  // resetting the debounce on every one-second tick
  const elapsedBucket = Math.floor(elapsedSeconds / 30);

  // Debounced autosave whenever the essay or outline changes
  useEffect(() => {
    if (!autosaveReady || submitting) return;
    const id = setTimeout(() => {
      if (persistLocalDraft()) {
        setLastLocalSave(
          new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
        );
      }
    }, AUTOSAVE_DEBOUNCE_MS);
    return () => clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    autosaveReady,
    submitting,
    essayText,
    outline,
    outlineFeedback,
    validatedOutline,
    elapsedBucket,
  ]);

  // Flush immediately when the tab is hidden or closed, and on unmount
  // (App Router client navigation fires neither pagehide nor visibilitychange)
  useEffect(() => {
    if (!autosaveReady) return;
    const flush = () => {
      persistLocalDraft();
    };
    window.addEventListener("pagehide", flush);
    const onVisibility = () => {
      if (document.visibilityState === "hidden") flush();
    };
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      window.removeEventListener("pagehide", flush);
      document.removeEventListener("visibilitychange", onVisibility);
      flush();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autosaveReady, storageKey]);

  function discardRestoredDraft() {
    try {
      window.localStorage.removeItem(storageKey);
    } catch {
      // ignore
    }
    setEssayText(initialDraft?.essayText || "");
    setOutline(
      initialDraft?.outline || {
        thesisOrOverview: "",
        body1Plan: "",
        body2Plan: "",
        targetKeyTerms: "",
      }
    );
    setOutlineFeedback(initialDraft?.outlineFeedback || null);
    setValidatedOutline(initialDraft?.outlineFeedback ? initialDraft.outline || null : null);
    setElapsedSeconds(initialDraft?.secondsSpent || 0);
    setRestoredNotice(false);
    setLastLocalSave(null);
  }

  const outlineIsStale =
    Boolean(outlineFeedback) && outlineKey(outline) !== outlineKey(validatedOutline);

  // The visible result is always the newest history entry, so "earlier" excludes it while shown
  const earlierChecks = toolkitResult ? toolkitHistory.slice(0, -1) : toolkitHistory;

  const wordCount = countEssayWords(essayText);
  const wordProgress = Math.min(100, Math.round((wordCount / minWords) * 100));

  function captureSelection() {
    const el = editorRef.current;
    if (!el) return;
    const start = el.selectionStart;
    const end = el.selectionEnd;
    // A collapsed selection means "inspect the paragraph at the cursor" instead of a stale snippet
    setSelectedSnippet(end > start ? essayText.slice(start, end).trim() : "");
  }

  async function handleValidateOutline() {
    setCheckingOutline(true);
    setError(null);
    setErrorKind(null);
    setCanRetryGrading(false);
    try {
      const outcome = await safePostJson<{ feedback: OutlineFeedback }>(
        "/api/ai/outline-check",
        {
          promptId: prompt.id,
          taskType,
          promptText: prompt.promptText,
          outline,
        },
        "checking your outline"
      );
      if (!outcome.ok) {
        setError(outcome.error);
      } else {
        setOutlineFeedback(outcome.data.feedback);
        setValidatedOutline({ ...outline });
        setDoneTweaks(new Set());
      }
    } finally {
      setCheckingOutline(false);
    }
  }

  async function handleRunToolkit(tool: ToolName) {
    const targetText =
      selectedSnippet.trim() ||
      paragraphAtCursor(essayText, editorRef.current?.selectionStart);

    if (!targetText) {
      setError(
        "Highlight a phrase/sentence in your draft (or write at least one sentence) before running an on-demand check."
      );
      setErrorKind(null);
      setCanRetryGrading(false);
      return;
    }

    setToolkitLoading(tool);
    setError(null);
    setErrorKind(null);
    setCanRetryGrading(false);
    try {
      const outcome = await safePostJson<{ result: ToolkitResponse }>(
        "/api/ai/toolkit",
        {
          tool,
          taskType,
          promptText: prompt.promptText,
          selectedText: targetText,
          fullEssayContext: essayText,
        },
        "consulting the coach toolkit"
      );
      if (!outcome.ok) {
        setError(outcome.error);
      } else {
        const entry: ToolkitEntry = { ...outcome.data.result, checkedText: targetText };
        setToolkitResult(entry);
        setToolkitHistory((prev) => [...prev, entry]);
      }
    } finally {
      setToolkitLoading(null);
    }
  }

  async function handleSubmitEssay() {
    if (wordCount < 15) {
      setError("Please write your draft before submitting for 4-criterion grading.");
      setErrorKind(null);
      setCanRetryGrading(false);
      return;
    }
    setSubmitting(true);
    setGradingStage("Examiner reviewing prompt & essay...");
    setError(null);
    setErrorKind(null);
    setCanRetryGrading(false);

    const outcome = await submitEvaluationStream(
      {
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
            coachingHistory: toolkitHistory,
          },
        ],
      },
      (stage) => setGradingStage(stage)
    );

    if (!outcome.ok) {
      setError(outcome.error || "Evaluation failed. Please try again.");
      setErrorKind(outcome.errorKind || "server");
      setCanRetryGrading(true);
      setSubmitting(false);
      setGradingStage(null);
      return;
    }

    draftClearedRef.current = true;
    try {
      window.localStorage.removeItem(storageKey);
    } catch {
      // ignore
    }
    navigate(`/session/${sessionId}`);
  }

  function copyText(key: string, text: string) {
    navigator.clipboard?.writeText(text);
    setCopiedKey(key);
    setTimeout(() => setCopiedKey((k) => (k === key ? null : k)), 1500);
  }

  async function saveToVault(
    key: string,
    item: {
      itemType: "error" | "collocation";
      criterion: string;
      originalText: string;
      upgradedText: string;
      explanation: string;
    }
  ) {
    if (savedVaultKeys.has(key) || savingVaultKey) return;
    setSavingVaultKey(key);
    setVaultError(null);
    try {
      const outcome = await safePostJson<{ item: unknown }>(
        "/api/vault",
        item,
        "saving to your Vault"
      );
      if (!outcome.ok) {
        setVaultError(outcome.error);
      } else {
        setSavedVaultKeys((prev) => new Set(prev).add(key));
      }
    } finally {
      setSavingVaultKey(null);
    }
  }

  function toggleInSet(setter: (fn: (prev: Set<number>) => Set<number>) => void, idx: number) {
    setter((prev) => {
      const next = new Set(prev);
      if (next.has(idx)) next.delete(idx);
      else next.add(idx);
      return next;
    });
  }

  function insertKeywordAtCursor(keyword: string) {
    const el = editorRef.current;
    const start = el ? (el.selectionStart ?? essayText.length) : essayText.length;
    const end = el ? (el.selectionEnd ?? start) : start;
    const before = essayText.slice(0, start);
    const after = essayText.slice(end);
    const needsLeadingSpace = before.length > 0 && !/[\s(["'“‘]$/.test(before);
    const needsTrailingSpace = after.length > 0 && !/^[\s.,;:!?)}"'”’]/.test(after);
    const inserted = `${needsLeadingSpace ? " " : ""}${keyword}${needsTrailingSpace ? " " : ""}`;
    const nextText = `${before}${inserted}${after}`;
    const nextCaret = before.length + inserted.length;

    setEssayText(nextText);
    setSelectedSnippet("");

    window.requestAnimationFrame(() => {
      if (editorRef.current) {
        editorRef.current.focus();
        editorRef.current.setSelectionRange(nextCaret, nextCaret);
      }
    });
  }

  function renderVaultButton(
    key: string,
    item: Parameters<typeof saveToVault>[1]
  ) {
    const saved = savedVaultKeys.has(key);
    const saving = savingVaultKey === key;
    return (
      <button
        type="button"
        disabled={saved || Boolean(savingVaultKey)}
        onClick={() => saveToVault(key, item)}
        title={saved ? "Saved to your Vault" : "Save to Vault for spaced review"}
        aria-label={saved ? "Saved to Vault" : "Save to Vault"}
        className="p-1.5 rounded hover:bg-surface text-ink-secondary shrink-0 cursor-pointer disabled:cursor-default"
      >
        {saving ? (
          <Loader2 className="w-3.5 h-3.5 animate-spin" />
        ) : saved ? (
          <BookmarkCheck className="w-3.5 h-3.5 text-accent" />
        ) : (
          <Bookmark className="w-3.5 h-3.5" />
        )}
      </button>
    );
  }

  function renderToolkitEntry(entry: ToolkitEntry, entryIdx: number, compact: boolean) {
    return (
      <div className="space-y-2.5">
        <div className="p-2 rounded-lg border-l-2 border-accent/60 bg-subtle/50">
          <span className="text-xs font-mono uppercase text-ink-muted">Checked text</span>
          <p className="text-xs text-ink-secondary italic line-clamp-3">
            &ldquo;{entry.checkedText}&rdquo;
          </p>
        </div>
        {!compact && (
          <p className="text-ink-secondary leading-relaxed">{entry.analysis}</p>
        )}
        <div className="space-y-2 pt-1">
          {entry.suggestions.map((sug, idx) => {
            const key = `tk-${entryIdx}-${idx}`;
            return (
              <div
                key={key}
                className="p-2.5 rounded-lg bg-subtle flex items-start justify-between gap-3"
              >
                <div className="space-y-0.5">
                  <span className="text-xs font-mono uppercase font-semibold text-accent">
                    {sug.label}
                  </span>
                  <p className="text-ink font-medium">{sug.rewrittenOrPhrase}</p>
                  <p className="text-xs text-ink-secondary">{sug.rationale}</p>
                </div>
                <div className="flex items-center gap-0.5 shrink-0">
                  <button
                    type="button"
                    onClick={() => copyText(key, sug.rewrittenOrPhrase)}
                    title="Copy phrase"
                    aria-label="Copy phrase"
                    className="p-1.5 rounded hover:bg-surface text-ink-secondary shrink-0 cursor-pointer"
                  >
                    {copiedKey === key ? (
                      <Check className="w-3.5 h-3.5 text-accent" />
                    ) : (
                      <Copy className="w-3.5 h-3.5" />
                    )}
                  </button>
                  {renderVaultButton(key, {
                    itemType: entry.tool === "collocations" ? "collocation" : "error",
                    criterion: toolCriterion(entry.tool),
                    originalText: entry.checkedText.slice(0, 600),
                    upgradedText: sug.rewrittenOrPhrase,
                    explanation: sug.rationale,
                  })}
                </div>
              </div>
            );
          })}
        </div>
      </div>
    );
  }

  const mm = String(Math.floor(elapsedSeconds / 60)).padStart(2, "0");
  const ss = String(elapsedSeconds % 60).padStart(2, "0");

  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 py-5 space-y-5">
      {/* Top Studio Bar */}
      <div className="flex flex-wrap items-center justify-between gap-3 pb-4 border-b border-line">
        <div className="flex items-center gap-3 min-w-0">
          <Link
            to="/"
            aria-label="Back to Studio"
            className="p-2 rounded-lg border border-line bg-surface hover:bg-subtle text-ink-secondary hover:text-ink transition shrink-0"
          >
            <ArrowLeft className="w-4 h-4" />
          </Link>
          <div>
            <p className="text-sm text-ink-muted">
              <span className="font-semibold text-accent">
                Coach · {taskType === "task1" ? "Task 1" : "Task 2"}
              </span>{" "}
              · about {recMinutes} min · {minWords}+ words
            </p>
            <h1 className="font-serif text-lg sm:text-xl font-semibold text-ink mt-0.5">
              {prompt.title}
            </h1>
          </div>
        </div>

        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => setTimerPaused((p) => !p)}
            title="Click to pause/resume study stopwatch"
            className="btn btn-secondary font-mono tabular-nums"
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
            title={wordCount < 10 ? "Write a few sentences first" : undefined}
            className="btn btn-primary"
          >
            {submitting ? (
              <>
                <Loader2 className="w-4 h-4 animate-spin" />
                <span>Grading…</span>
              </>
            ) : (
              <>
                <Send className="w-3.5 h-3.5" />
                <span>Submit for grading</span>
              </>
            )}
          </button>
        </div>
      </div>

      {submitting && gradingStage && (
        <div className="p-3.5 rounded-xl border border-accent/35 bg-accent-soft/60 flex items-center gap-2.5 text-xs sm:text-sm text-ink">
          <Loader2 className="w-4 h-4 animate-spin text-accent shrink-0" />
          <span className="font-medium">{gradingStage}</span>
        </div>
      )}

      {error && (
        <div
          role="alert"
          className={`p-3.5 rounded-xl border flex flex-wrap items-center justify-between gap-3 text-xs sm:text-sm ${
            errorKind === "timeout"
              ? "border-warm bg-warm-soft text-ink"
              : "border-red-400 bg-red-500/10 text-red-600 dark:text-red-300"
          }`}
        >
          <div className="flex items-start gap-2.5">
            <AlertTriangle
              className={`w-4 h-4 shrink-0 mt-0.5 ${
                errorKind === "timeout" ? "text-warm" : "text-red-500"
              }`}
            />
            <span>{error}</span>
          </div>

          {canRetryGrading && (
            <button
              id="retry-coach-essay-btn"
              type="button"
              disabled={submitting}
              onClick={handleSubmitEssay}
              className="inline-flex items-center gap-1.5 px-3.5 py-1.5 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-xs font-semibold transition cursor-pointer shrink-0"
            >
              <RotateCcw className="w-3.5 h-3.5" />
              <span>Retry Grading</span>
            </button>
          )}
        </div>
      )}

      {restoredNotice && (
        <div
          role="status"
          className="p-3 rounded-xl border border-accent/35 bg-accent-soft/50 flex flex-wrap items-center justify-between gap-2 text-xs sm:text-sm text-ink"
        >
          <div className="flex items-center gap-2">
            <History className="w-4 h-4 text-accent shrink-0" />
            <span>Restored your unsaved draft from this device.</span>
          </div>
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={() => setRestoredNotice(false)}
              className="text-xs font-medium text-accent hover:underline cursor-pointer"
            >
              Keep
            </button>
            <button
              type="button"
              onClick={discardRestoredDraft}
              className="text-xs font-medium text-ink-muted hover:text-ink hover:underline cursor-pointer"
            >
              Discard
            </button>
          </div>
        </div>
      )}

      {vaultError && (
        <div
          role="alert"
          className="p-3 rounded-xl border border-red-400 bg-red-500/10 text-red-600 dark:text-red-300 flex items-center justify-between gap-3 text-xs sm:text-sm"
        >
          <span>{vaultError}</span>
          <button
            type="button"
            onClick={() => setVaultError(null)}
            className="text-xs font-medium hover:underline cursor-pointer"
          >
            Dismiss
          </button>
        </div>
      )}

      {/* Split Studio Workspace */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
        {/* Left Column: Prompt + Visual + 5-Min Interactive Outline Check */}
        <div className="lg:col-span-5 space-y-5">
          <section className="editorial-surface rounded-2xl p-5">
            <span className="text-xs font-mono uppercase tracking-wider text-ink-muted">
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
            <ModelAnswer promptId={prompt.id} className="mt-4" />
          </section>

          {previousFocus && previousFocus.topPriorities.length > 0 && (
            <section className="editorial-surface rounded-2xl p-5 space-y-3">
              <div className="flex items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <Target className="w-4 h-4 text-warm" />
                  <h2 className="font-serif text-base font-semibold text-ink">
                    Your Focus for This Draft
                  </h2>
                </div>
                <span className="text-xs font-mono text-ink-muted">
                  {previousFocus.isRedraft
                    ? `From v${previousFocus.fromVersion}`
                    : "Last graded essay"}{" "}
                  · Band {previousFocus.overallBand.toFixed(1)}
                </span>
              </div>
              <p className="text-xs text-ink-secondary leading-relaxed">
                {previousFocus.isRedraft
                  ? "The examiner's highest-leverage fixes from your previous version. Tick each one off as you address it."
                  : "Carry these examiner priorities from your last graded essay of this task type into this one."}
              </p>
              <ol className="space-y-1.5">
                {previousFocus.topPriorities.map((p, idx) => {
                  const done = doneFocus.has(idx);
                  return (
                    <li key={idx}>
                      <label className="flex items-start gap-2 p-2 rounded-lg bg-subtle/60 text-xs cursor-pointer">
                        <input
                          type="checkbox"
                          checked={done}
                          onChange={() => toggleInSet(setDoneFocus, idx)}
                          className="mt-0.5"
                        />
                        <span className={done ? "text-ink-muted line-through" : "text-ink"}>
                          {p}
                        </span>
                      </label>
                    </li>
                  );
                })}
              </ol>
            </section>
          )}

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
                  <label className="block text-xs font-semibold uppercase tracking-wider text-ink-secondary mb-1">
                    {taskType === "task1"
                      ? "Macro Overview (2–3 overarching trends, NO raw numbers)"
                      : "Thesis & Clear Position"}
                  </label>
                  <textarea
                    id="outline-thesis-input"
                    rows={2}
                    value={outline.thesisOrOverview}
                    maxLength={TEXT_LIMITS.outlineField}
                    onChange={(e) =>
                      setOutline({ ...outline, thesisOrOverview: e.target.value })
                    }
                    placeholder={
                      taskType === "task1"
                        ? "Overall, coal generation declined precipitously while renewables surged to become the dominant power source..."
                        : "While applied research delivers short-term utility, restricting theoretical grants would cripple foundational breakthroughs..."
                    }
                    className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-xs sm:text-sm text-ink"
                  />
                </div>

                <div className="grid grid-cols-1 sm:grid-cols-2 gap-2.5">
                  <div>
                    <label className="block text-xs font-semibold uppercase tracking-wider text-ink-secondary mb-1">
                      Body 1 Grouping / Claim
                    </label>
                    <textarea
                      id="outline-body1-input"
                      rows={2}
                      value={outline.body1Plan}
                      maxLength={TEXT_LIMITS.outlineField}
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
                    <label className="block text-xs font-semibold uppercase tracking-wider text-ink-secondary mb-1">
                      Body 2 Grouping / Claim
                    </label>
                    <textarea
                      id="outline-body2-input"
                      rows={2}
                      value={outline.body2Plan}
                      maxLength={TEXT_LIMITS.outlineField}
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
                        className={`px-2 py-0.5 rounded font-mono font-semibold uppercase text-xs ${
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

                    {outlineIsStale && (
                      <div className="flex items-center justify-between gap-2 p-2 rounded-lg bg-warm-soft text-xs text-ink">
                        <span className="flex items-center gap-1.5">
                          <AlertTriangle className="w-3.5 h-3.5 text-warm shrink-0" />
                          Outline changed since this check.
                        </span>
                        <button
                          type="button"
                          disabled={checkingOutline || !outline.thesisOrOverview.trim()}
                          onClick={handleValidateOutline}
                          className="font-semibold text-accent hover:underline cursor-pointer disabled:opacity-50"
                        >
                          Re-validate
                        </button>
                      </div>
                    )}

                    <p className="text-ink font-medium">{outlineFeedback.verdictSummary}</p>
                    <p className="text-ink-secondary">
                      <strong>Overview/Thesis:</strong>{" "}
                      {outlineFeedback.overviewOrThesisCheck}
                    </p>
                    <p className="text-ink-secondary">
                      <strong>Paragraphing:</strong> {outlineFeedback.bodyGroupingCheck}
                    </p>

                    {outlineFeedback.actionableTweaks?.length > 0 && (
                      <div className="pt-2 border-t border-line space-y-1.5">
                        <p className="text-xs font-mono uppercase text-ink-muted">
                          Fix Before You Write ({doneTweaks.size}/
                          {outlineFeedback.actionableTweaks.length})
                        </p>
                        <ol className="space-y-1">
                          {outlineFeedback.actionableTweaks.map((tweak, idx) => {
                            const done = doneTweaks.has(idx);
                            return (
                              <li key={idx}>
                                <label className="flex items-start gap-2 cursor-pointer">
                                  <input
                                    type="checkbox"
                                    checked={done}
                                    onChange={() => toggleInSet(setDoneTweaks, idx)}
                                    className="mt-0.5"
                                  />
                                  <span
                                    className={
                                      done ? "text-ink-muted line-through" : "text-ink"
                                    }
                                  >
                                    {idx + 1}. {tweak}
                                  </span>
                                </label>
                              </li>
                            );
                          })}
                        </ol>
                      </div>
                    )}

                    {outlineFeedback.suggestedAcademicCollocations?.length > 0 && (
                      <div className="pt-2 border-t border-line space-y-1.5">
                        <p className="text-xs font-mono uppercase text-ink-muted">
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
                              <p className="text-xs text-ink-secondary">
                                {c.usageNote}
                              </p>
                            </div>
                            <div className="flex items-center gap-0.5 shrink-0">
                              <button
                                type="button"
                                onClick={() => copyText(`col-${idx}`, c.phrase)}
                                title="Copy phrase"
                                aria-label="Copy phrase"
                                className="p-1.5 rounded hover:bg-surface text-ink-secondary cursor-pointer"
                              >
                                {copiedKey === `col-${idx}` ? (
                                  <Check className="w-3.5 h-3.5 text-accent" />
                                ) : (
                                  <Copy className="w-3.5 h-3.5" />
                                )}
                              </button>
                              {renderVaultButton(`col-${c.phrase}`, {
                                itemType: "collocation",
                                criterion: "LR",
                                originalText: c.phrase,
                                upgradedText: c.phrase,
                                explanation: c.usageNote,
                              })}
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

          {/* Suggested Reusable Keywords */}
          {(() => {
            const keywordGroups = getTaskKeywords(taskType, prompt.category);
            const allKeywords = keywordGroups.flatMap((g) => g.keywords);
            const usedCount = allKeywords.filter((kw) =>
              isKeywordUsedInText(essayText, kw)
            ).length;
            return (
              <section
                id="coach-suggested-keywords"
                className="editorial-surface rounded-2xl p-5 space-y-3"
              >
                <div className="flex items-center justify-between gap-2">
                  <div className="flex items-center gap-2">
                    <Tags className="w-4 h-4 text-accent" />
                    <h2 className="font-serif text-base font-semibold text-ink">
                      Suggested Keywords
                    </h2>
                    <span className="text-xs font-mono text-ink-muted tabular-nums">
                      {usedCount}/{allKeywords.length} used
                    </span>
                  </div>
                  <button
                    type="button"
                    onClick={() => setKeywordsOpen((o) => !o)}
                    aria-expanded={keywordsOpen}
                    className="text-xs font-medium text-accent hover:underline cursor-pointer"
                  >
                    {keywordsOpen ? "Collapse" : "Expand"}
                  </button>
                </div>

                {keywordsOpen && (
                  <div className="space-y-3">
                    <p className="text-xs text-ink-secondary leading-relaxed">
                      Reusable academic terms for{" "}
                      <span className="font-medium text-ink">
                        {prompt.category.replace("_", " ")}
                      </span>{" "}
                      tasks. Click any chip to insert at your cursor.
                    </p>

                    <div className="space-y-2.5">
                      {keywordGroups.map((group) => (
                        <div key={group.label} className="space-y-1.5">
                          <p className="text-xs font-mono font-semibold uppercase tracking-wider text-ink-muted">
                            {group.label}
                          </p>
                          <div className="flex flex-wrap gap-1.5">
                            {group.keywords.map((kw) => {
                              const used = isKeywordUsedInText(essayText, kw);
                              return (
                                <button
                                  key={kw}
                                  type="button"
                                  onClick={() => insertKeywordAtCursor(kw)}
                                  title={
                                    used
                                      ? `"${kw}" is already in your draft — click to insert again`
                                      : `Insert "${kw}" at cursor`
                                  }
                                  className={`inline-flex items-center gap-1 px-2.5 py-1 rounded-lg border text-xs font-medium transition cursor-pointer ${
                                    used
                                      ? "bg-accent-soft text-accent border-accent/30"
                                      : "bg-canvas hover:bg-subtle text-ink border-line-strong"
                                  }`}
                                >
                                  {used && <Check className="w-3 h-3 shrink-0" />}
                                  <span>{kw}</span>
                                </button>
                              );
                            })}
                          </div>
                        </div>
                      ))}
                    </div>
                  </div>
                )}
              </section>
            );
          })()}
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
                {lastLocalSave && (
                  <span
                    className="text-xs text-ink-muted"
                    title="Your draft is saved on this device until you submit it for grading"
                  >
                    Saved locally · {lastLocalSave}
                  </span>
                )}
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
              maxLength={TEXT_LIMITS.essay}
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
                <span className="text-xs font-mono uppercase tracking-wider text-ink-muted">
                  Coach tools{" "}
                  <span className="normal-case tracking-normal font-normal">
                    {selectedSnippet
                      ? `· checks “${selectedSnippet.slice(0, 28)}${
                          selectedSnippet.length > 28 ? "…" : ""
                        }”`
                      : "· checks the paragraph your cursor is in"}
                  </span>
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
                  {renderToolkitEntry(toolkitResult, toolkitHistory.length - 1, false)}
                </div>
              )}

              {earlierChecks.length > 0 && (
                <div className="rounded-xl border border-line">
                  <button
                    type="button"
                    onClick={() => setShowEarlierChecks((v) => !v)}
                    aria-expanded={showEarlierChecks}
                    className="w-full flex items-center justify-between px-3 py-2 text-xs font-medium text-ink-secondary hover:text-ink cursor-pointer"
                  >
                    <span className="flex items-center gap-1.5">
                      <History className="w-3.5 h-3.5" />
                      Earlier checks ({earlierChecks.length})
                    </span>
                    <span>{showEarlierChecks ? "Hide" : "Show"}</span>
                  </button>
                  {showEarlierChecks && (
                    <div className="px-3 pb-3 space-y-4 text-xs">
                      {earlierChecks
                        .map((entry, idx) => ({ entry, idx }))
                        .reverse()
                        .map(({ entry, idx }) => (
                          <div key={idx} className="pt-3 border-t border-line space-y-2">
                            <h4 className="font-semibold text-ink">{entry.headline}</h4>
                            {renderToolkitEntry(entry, idx, true)}
                          </div>
                        ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          </section>
        </div>
      </div>
    </div>
  );
}
