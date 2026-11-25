import { useState } from "react";
import { Link, useNavigate } from "react-router";
import {
  Clock,
  Compass,
  Upload,
  Sparkles,
  ArrowUpRight,
  SlidersHorizontal,
  CheckCircle2,
  AlertTriangle,
  Layers,
  Loader2,
} from "lucide-react";
import {
  CustomPromptModal,
  GeneratePromptModal,
  type PromptRecord,
} from "./PromptModals";
import { TaskVisualRenderer } from "./TaskVisualRenderer";
import { ModelAnswer } from "./ModelAnswer";
import type { SessionMode } from "@/lib/types";

interface RecentSessionSummary {
  id: string;
  mode: string;
  status: string;
  startedAt: string;
  weightedBand: number | null;
  task1Title?: string | null;
  task2Title?: string | null;
  latestDraftVersion?: number;
}

const CATEGORY_LABELS: Record<string, string> = {
  line: "Line Graph",
  bar: "Bar Chart",
  pie: "Pie Charts",
  table: "Data Table",
  mixed: "Mixed Charts",
  process: "Process Diagram",
  map: "Comparison Map",
  opinion: "Opinion Essay",
  discussion: "Discuss Both Views",
  advantages: "Advantages / Disadvantages",
  problem_solution: "Problem & Solution",
  two_part: "Two-Part Question",
};

export function StudioHomeClient({
  initialPrompts,
  recentSessions,
  settingsSummary,
  canTransformSvg = false,
}: {
  initialPrompts: PromptRecord[];
  recentSessions: RecentSessionSummary[];
  settingsSummary: {
    currentBand: number;
    targetBand: number;
    daysUntilExam: number | null;
    nativeLanguage: string;
    l1CoachingEnabled: boolean;
    activeProvider: string;
    activeProviderReady: boolean;
  };
  canTransformSvg?: boolean;
}) {
  const navigate = useNavigate();
  const [prompts, setPrompts] = useState<PromptRecord[]>(initialPrompts);
  const [activeTab, setActiveTab] = useState<"all" | "task1" | "task2">("all");
  const [categoryFilter, setCategoryFilter] = useState<string>("all");
  const [previewPromptId, setPreviewPromptId] = useState<string | null>(null);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [generateOpen, setGenerateOpen] = useState(false);
  const [launchingId, setLaunchingId] = useState<string | null>(null);

  // Full 60m Mock Exam selector state
  const [mockOpen, setMockOpen] = useState(false);
  const task1Prompts = prompts.filter((p) => p.taskType === "task1");
  const task2Prompts = prompts.filter((p) => p.taskType === "task2");
  const [selectedMockT1, setSelectedMockT1] = useState(task1Prompts[0]?.id || "");
  const [selectedMockT2, setSelectedMockT2] = useState(task2Prompts[0]?.id || "");

  async function startSession(
    mode: SessionMode,
    t1Id?: string | null,
    t2Id?: string | null
  ) {
    const key = `${mode}-${t1Id || ""}-${t2Id || ""}`;
    setLaunchingId(key);
    try {
      const res = await fetch("/api/sessions", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          mode,
          task1PromptId: t1Id || null,
          task2PromptId: t2Id || null,
        }),
      });
      const data = await res.json();
      if (res.ok && data.session?.id) {
        navigate(`/write/${data.session.id}`);
      } else {
        setLaunchingId(null);
      }
    } catch {
      setLaunchingId(null);
    }
  }

  const visiblePrompts = prompts.filter((p) => {
    if (activeTab !== "all" && p.taskType !== activeTab) return false;
    if (categoryFilter !== "all" && p.category !== categoryFilter) return false;
    return true;
  });

  const daysUntilExam = settingsSummary.daysUntilExam;

  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 pt-7 space-y-8">
      {/* Studio Editorial Masthead */}
      <section className="flex flex-col lg:flex-row lg:items-end justify-between gap-6 pb-6 border-b-[3px] border-double border-line-strong">
        <div className="space-y-2 max-w-2xl">
          <div className="flex flex-wrap items-center gap-2 text-xs font-mono text-ink-secondary">
            <span>
              CALIBRATION: BAND {settingsSummary.currentBand.toFixed(1)} →{" "}
              <strong className="text-accent font-semibold">
                {settingsSummary.targetBand.toFixed(1)}
              </strong>
            </span>
            <span>·</span>
            <span>
              {settingsSummary.l1CoachingEnabled
                ? `L1 LENS: ${settingsSummary.nativeLanguage.toUpperCase()} → EN`
                : "L1 NEUTRAL"}
            </span>
            {daysUntilExam !== null && (
              <>
                <span>·</span>
                <span className="text-warm font-semibold">
                  {daysUntilExam} DAYS TO EXAM
                </span>
              </>
            )}
          </div>
          <h1 className="font-serif text-4xl sm:text-5xl font-semibold tracking-tight text-ink leading-[1.05]">
            Academic Writing <em className="italic text-accent">Studio</em>
          </h1>
          <p className="text-sm sm:text-base text-ink-secondary leading-relaxed">
            Build Band {settingsSummary.targetBand.toFixed(1)}+ precision with 5-minute
            outline checks in <strong>Coach Mode</strong>, or test exam-day stamina inside
            an authentic <strong>Inspera Computer-Delivered IELTS</strong> replica.
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2.5">
          <button
            id="open-full-mock-btn"
            type="button"
            onClick={() => setMockOpen(!mockOpen)}
            className="flex items-center gap-2 px-4 py-2.5 rounded-xl bg-accent hover:bg-accent-hover text-accent-fg text-sm font-medium transition cursor-pointer"
          >
            <Layers className="w-4 h-4" />
            <span>60m Full Mock Exam</span>
          </button>

          <button
            id="open-ai-generator-btn"
            type="button"
            onClick={() => setGenerateOpen(true)}
            className="flex items-center gap-2 px-3.5 py-2.5 rounded-xl border border-line-strong bg-surface hover:bg-subtle text-sm font-medium text-ink transition cursor-pointer"
          >
            <Sparkles className="w-4 h-4 text-accent" />
            <span>AI Prompt</span>
          </button>

          <button
            id="open-custom-upload-btn"
            type="button"
            onClick={() => setUploadOpen(true)}
            className="flex items-center gap-2 px-3.5 py-2.5 rounded-xl border border-line-strong bg-surface hover:bg-subtle text-sm font-medium text-ink transition cursor-pointer"
          >
            <Upload className="w-4 h-4 text-ink-secondary" />
            <span>Upload Task / Chart</span>
          </button>
        </div>
      </section>

      {/* AI Provider Readiness Strip */}
      {!settingsSummary.activeProviderReady && (
        <div
          id="ai-key-setup-banner"
          className="editorial-surface rounded-xl p-4 flex flex-col sm:flex-row sm:items-center justify-between gap-3 border-l-4"
          style={{ borderLeftColor: "var(--accent-warm)" }}
        >
          <div className="flex items-start gap-3">
            <AlertTriangle className="w-4 h-4 text-warm shrink-0 mt-0.5" />
            <div className="text-xs sm:text-sm text-ink-secondary">
              <strong className="text-ink font-semibold">
                Local Diagnostic Preview Active:
              </strong>{" "}
              Connect your{" "}
              <span className="font-mono uppercase">
                {settingsSummary.activeProvider}
              </span>{" "}
              API key in Settings to activate live{" "}
              <code className="font-mono">deepseek-v4-pro</code> rubric grading, Band 8+
              rewrites, and <code className="font-mono">deepseek-flash</code> chart vision.
            </div>
          </div>
          <Link
            to="/settings"
            className="shrink-0 inline-flex items-center gap-1.5 px-3.5 py-2 rounded-lg bg-subtle hover:bg-line text-xs font-semibold text-ink transition"
          >
            <SlidersHorizontal className="w-3.5 h-3.5" />
            <span>Configure AI Key</span>
          </Link>
        </div>
      )}

      {/* 60-Minute Combined Mock Exam Drawer */}
      {mockOpen && (
        <section className="editorial-surface rounded-2xl p-6 space-y-4">
          <div className="flex items-start justify-between gap-4">
            <div>
              <span className="text-[11px] font-mono uppercase tracking-wider text-accent font-semibold">
                Authentic Inspera Computer-Delivered Exam · 1:2 Weighted Band
              </span>
              <h2 className="font-serif text-xl font-semibold text-ink mt-0.5">
                Configure 60-Minute Full Writing Test
              </h2>
              <p className="text-xs text-ink-secondary mt-1">
                Both tasks run under a single shared 60:00 clock with Part 1 &amp; Part 2
                bottom switcher tabs—mirroring real test-day time management.
              </p>
            </div>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label className="block text-xs font-semibold text-ink-secondary mb-1.5">
                Part 1 — Academic Task 1 (150+ words · 1/3 weight)
              </label>
              <select
                value={selectedMockT1}
                onChange={(e) => setSelectedMockT1(e.target.value)}
                className="w-full px-3 py-2.5 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
              >
                {task1Prompts.map((p) => (
                  <option key={p.id} value={p.id}>
                    [{CATEGORY_LABELS[p.category] || p.category}] {p.title}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label className="block text-xs font-semibold text-ink-secondary mb-1.5">
                Part 2 — Academic Task 2 (250+ words · 2/3 weight)
              </label>
              <select
                value={selectedMockT2}
                onChange={(e) => setSelectedMockT2(e.target.value)}
                className="w-full px-3 py-2.5 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
              >
                {task2Prompts.map((p) => (
                  <option key={p.id} value={p.id}>
                    [{CATEGORY_LABELS[p.category] || p.category}] {p.title}
                  </option>
                ))}
              </select>
            </div>
          </div>

          <div className="flex justify-end gap-2.5">
            <button
              type="button"
              onClick={() => setMockOpen(false)}
              className="px-4 py-2 rounded-lg border border-line text-xs font-medium text-ink-secondary cursor-pointer"
            >
              Close
            </button>
            <button
              id="launch-60m-mock-btn"
              type="button"
              disabled={!selectedMockT1 || !selectedMockT2 || Boolean(launchingId)}
              onClick={() =>
                startSession("simulator_full_60m", selectedMockT1, selectedMockT2)
              }
              className="flex items-center gap-2 px-5 py-2.5 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-xs font-semibold cursor-pointer"
            >
              {launchingId?.startsWith("simulator_full_60m") ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Clock className="w-4 h-4" />
              )}
              <span>Begin 60:00 Inspera Exam</span>
            </button>
          </div>
        </section>
      )}

      {/* Main Two-Column Layout: Prompt Bank (Left 8 cols) + Recent Drafts (Right 4 cols) */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-7 items-start">
        <div className="lg:col-span-8 space-y-5">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="inline-flex p-1 rounded-xl bg-subtle">
              {(
                [
                  { id: "all", label: `All Tasks (${prompts.length})` },
                  { id: "task1", label: `Task 1 Visuals (${task1Prompts.length})` },
                  { id: "task2", label: `Task 2 Essays (${task2Prompts.length})` },
                ] as const
              ).map((tab) => (
                <button
                  key={tab.id}
                  id={`filter-tab-${tab.id}`}
                  type="button"
                  onClick={() => {
                    setActiveTab(tab.id);
                    setCategoryFilter("all");
                  }}
                  className={`px-3.5 py-1.5 rounded-lg text-xs font-semibold transition cursor-pointer ${
                    activeTab === tab.id
                      ? "bg-surface text-ink shadow-xs"
                      : "text-ink-secondary hover:text-ink"
                  }`}
                >
                  {tab.label}
                </button>
              ))}
            </div>

            <select
              aria-label="Filter by archetype"
              value={categoryFilter}
              onChange={(e) => setCategoryFilter(e.target.value)}
              className="px-3 py-1.5 rounded-lg bg-surface border border-line text-xs text-ink-secondary"
            >
              <option value="all">All Archetypes</option>
              {Object.entries(CATEGORY_LABELS).map(([val, label]) => (
                <option key={val} value={val}>
                  {label}
                </option>
              ))}
            </select>
          </div>

          <div className="space-y-4">
            {visiblePrompts.map((prompt) => {
              const isTask1 = prompt.taskType === "task1";
              const isPreviewing = previewPromptId === prompt.id;
              const simMode: SessionMode = isTask1 ? "simulator_t1" : "simulator_t2";

              return (
                <article
                  key={prompt.id}
                  className="editorial-surface rounded-2xl p-5 sm:p-6 transition hover:border-line-strong"
                >
                  <div className="flex flex-wrap items-center justify-between gap-2 mb-2.5">
                    <div className="flex flex-wrap items-center gap-2">
                      <span
                        className={`px-2.5 py-0.5 rounded-md text-[11px] font-mono font-semibold uppercase ${
                          isTask1
                            ? "bg-accent-soft text-accent"
                            : "bg-warm-soft text-warm"
                        }`}
                      >
                        {isTask1 ? "Task 1 · 20m · 150w+" : "Task 2 · 40m · 250w+"}
                      </span>
                      <span className="px-2 py-0.5 rounded-md bg-subtle text-ink-secondary text-[11px] font-medium">
                        {CATEGORY_LABELS[prompt.category] || prompt.category}
                      </span>
                      <span className="text-xs text-ink-muted">{prompt.topic}</span>
                    </div>

                    {prompt.source !== "curated" && (
                      <span className="text-[10px] font-mono uppercase px-2 py-0.5 rounded border border-line text-ink-muted">
                        {prompt.source === "custom" ? "Uploaded" : "AI Generated"}
                      </span>
                    )}
                  </div>

                  <h2 className="font-serif text-xl font-semibold text-ink">
                    {prompt.title}
                  </h2>

                  <p className="text-sm text-ink-secondary mt-2 whitespace-pre-line line-clamp-3">
                    {prompt.promptText}
                  </p>

                  {isPreviewing && isTask1 && (
                    <TaskVisualRenderer
                      chartSpecJson={prompt.chartSpecJson}
                      imageDataUrl={prompt.imageDataUrl}
                      compact
                    />
                  )}

                  <ModelAnswer promptId={prompt.id} className="mt-3" />

                  <div className="mt-4 pt-4 border-t border-line flex flex-wrap items-center justify-between gap-3">
                    <div>
                      {isTask1 && (prompt.chartSpecJson || prompt.imageDataUrl) && (
                        <button
                          type="button"
                          onClick={() =>
                            setPreviewPromptId(isPreviewing ? null : prompt.id)
                          }
                          className="text-xs font-medium text-accent hover:underline cursor-pointer"
                        >
                          {isPreviewing ? "Hide Visual Preview" : "Preview Chart / Diagram"}
                        </button>
                      )}
                    </div>

                    <div className="flex flex-wrap items-center gap-2">
                      <button
                        id={`coach-btn-${prompt.id}`}
                        type="button"
                        disabled={Boolean(launchingId)}
                        onClick={() =>
                          startSession(
                            "coach",
                            isTask1 ? prompt.id : null,
                            !isTask1 ? prompt.id : null
                          )
                        }
                        className="inline-flex items-center gap-1.5 px-3.5 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-xs font-semibold transition cursor-pointer disabled:opacity-50"
                      >
                        <Compass className="w-3.5 h-3.5" />
                        <span>Coach Studio</span>
                      </button>

                      <button
                        id={`sim-btn-${prompt.id}`}
                        type="button"
                        disabled={Boolean(launchingId)}
                        onClick={() =>
                          startSession(
                            simMode,
                            isTask1 ? prompt.id : null,
                            !isTask1 ? prompt.id : null
                          )
                        }
                        className="inline-flex items-center gap-1.5 px-3.5 py-2 rounded-lg border border-line-strong bg-canvas hover:bg-subtle text-ink text-xs font-semibold transition cursor-pointer disabled:opacity-50"
                      >
                        <Clock className="w-3.5 h-3.5 text-ink-secondary" />
                        <span>{isTask1 ? "20m Exam" : "40m Exam"}</span>
                      </button>
                    </div>
                  </div>
                </article>
              );
            })}
          </div>
        </div>

        {/* Right Sidebar: Recent Sessions & Quick Study Actions */}
        <aside className="lg:col-span-4 space-y-5">
          <div className="editorial-surface rounded-2xl p-5 space-y-4">
            <div className="flex items-center justify-between">
              <h2 className="font-serif text-lg font-semibold text-ink">
                Recent Sessions
              </h2>
              <Link
                to="/analytics"
                className="text-xs font-medium text-accent hover:underline"
              >
                All History
              </Link>
            </div>

            {recentSessions.length === 0 ? (
              <p className="text-xs text-ink-muted leading-relaxed py-3">
                No essays submitted yet. Pick any Task 1 or Task 2 prompt on the left to
                start your first guided outline or timed simulation.
              </p>
            ) : (
              <div className="space-y-2.5">
                {recentSessions.slice(0, 6).map((s) => {
                  const completed = s.status === "completed";
                  const href = completed ? `/session/${s.id}` : `/write/${s.id}`;
                  return (
                    <Link
                      key={s.id}
                      to={href}
                      className="block p-3 rounded-xl border border-line hover:border-line-strong bg-canvas transition"
                    >
                      <div className="flex items-center justify-between gap-2">
                        <span className="text-[11px] font-mono uppercase text-ink-muted">
                          {s.mode === "coach"
                            ? "Coach Studio"
                            : s.mode === "simulator_full_60m"
                            ? "60m Full Exam"
                            : "Timed Exam"}
                        </span>
                        {completed && s.weightedBand !== null ? (
                          <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-accent-soft text-accent font-mono text-xs font-bold">
                            <CheckCircle2 className="w-3 h-3" /> Band{" "}
                            {s.weightedBand.toFixed(1)}
                          </span>
                        ) : (
                          <span className="text-[11px] font-mono text-warm">
                            In Progress →
                          </span>
                        )}
                      </div>
                      <p className="text-xs font-semibold text-ink mt-1 truncate">
                        {s.task2Title || s.task1Title || "Writing Practice"}
                      </p>
                      {s.latestDraftVersion && s.latestDraftVersion > 1 && (
                        <p className="text-[11px] text-ink-muted mt-0.5">
                          Redrafted through v{s.latestDraftVersion}
                        </p>
                      )}
                    </Link>
                  );
                })}
              </div>
            )}
          </div>

          <div className="editorial-surface rounded-2xl p-5 space-y-3">
            <h3 className="font-serif text-base font-semibold text-ink">
              Short on Time Today?
            </h3>
            <p className="text-xs text-ink-secondary leading-relaxed">
              Run a 5-minute surgical micro-drill on Task 1 Overviews, mechanical linker
              replacement, or Spanish → English clause accuracy.
            </p>
            <Link
              to="/drills"
              id="sidebar-drills-cta"
              className="w-full inline-flex items-center justify-between px-3.5 py-2.5 rounded-xl bg-subtle hover:bg-line text-xs font-semibold text-ink transition"
            >
              <span>Open 5-Minute Micro-Drills</span>
              <ArrowUpRight className="w-4 h-4 text-accent" />
            </Link>
          </div>
        </aside>
      </div>

      <CustomPromptModal
        open={uploadOpen}
        onClose={() => setUploadOpen(false)}
        onCreated={(newP) => setPrompts((prev) => [newP, ...prev])}
        canTransformSvg={canTransformSvg}
      />

      <GeneratePromptModal
        open={generateOpen}
        onClose={() => setGenerateOpen(false)}
        onCreated={(newP) => setPrompts((prev) => [newP, ...prev])}
      />
    </div>
  );
}
