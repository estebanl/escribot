import { useState } from "react";
import { Link, useNavigate } from "react-router";
import {
  Clock,
  Compass,
  Upload,
  Sparkles,
  ArrowUpRight,
  SlidersHorizontal,
  ArrowRight,
  ChevronDown,
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

function modeLabel(mode: string) {
  if (mode === "coach") return "Coach";
  if (mode === "simulator_full_60m") return "Full mock exam";
  return "Timed test";
}

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
  const [openPromptId, setOpenPromptId] = useState<string | null>(null);
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
  const resumeSession = recentSessions.find((s) => s.status !== "completed") ?? null;

  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 pt-7 space-y-8">
      {/* Masthead */}
      <section className="flex flex-col lg:flex-row lg:items-end justify-between gap-6 pb-6 border-b border-line">
        <div className="space-y-3 max-w-2xl">
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-ink-secondary">
            <span>
              Band{" "}
              <span className="font-mono text-ink">{settingsSummary.currentBand.toFixed(1)}</span>{" "}
              → target{" "}
              <span className="font-mono font-semibold text-accent">
                {settingsSummary.targetBand.toFixed(1)}
              </span>
            </span>
            {daysUntilExam !== null && (
              <>
                <span aria-hidden className="text-line-strong">|</span>
                <span className="text-warm font-medium">
                  {daysUntilExam} {daysUntilExam === 1 ? "day" : "days"} to your exam
                </span>
              </>
            )}
          </div>
          <h1 className="font-serif text-4xl sm:text-5xl font-semibold tracking-tight text-ink leading-[1.05]">
            Writing <span className="text-accent">Studio</span>
          </h1>
          <p className="text-base text-ink-secondary leading-relaxed">
            Write with the coach, who checks your outline before you start, or sit a timed
            task under test-day conditions.
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <button
            id="open-full-mock-btn"
            type="button"
            onClick={() => setMockOpen(!mockOpen)}
            aria-expanded={mockOpen}
            className="btn btn-primary"
          >
            <Layers className="w-4 h-4" />
            <span>Full mock exam</span>
          </button>
          <button
            id="open-ai-generator-btn"
            type="button"
            onClick={() => setGenerateOpen(true)}
            className="btn btn-secondary"
          >
            <Sparkles className="w-4 h-4 text-accent" />
            <span>New prompt</span>
          </button>
          <button
            id="open-custom-upload-btn"
            type="button"
            onClick={() => setUploadOpen(true)}
            className="btn btn-secondary"
          >
            <Upload className="w-4 h-4 text-ink-secondary" />
            <span>Upload a task</span>
          </button>
        </div>
      </section>

      {/* AI provider readiness */}
      {!settingsSummary.activeProviderReady && (
        <div
          id="ai-key-setup-banner"
          className="editorial-surface rounded-xl p-4 flex flex-col sm:flex-row sm:items-center justify-between gap-3 border-l-4"
          style={{ borderLeftColor: "var(--accent-warm)" }}
        >
          <div className="flex items-start gap-3">
            <AlertTriangle className="w-4 h-4 text-warm shrink-0 mt-0.5" />
            <p className="text-sm text-ink-secondary">
              <strong className="text-ink font-semibold">Grading isn't connected yet.</strong>{" "}
              Add your AI key in Settings to get band scores, rewrites and chart reading.
            </p>
          </div>
          <Link to="/settings" className="btn btn-sm btn-secondary shrink-0">
            <SlidersHorizontal className="w-3.5 h-3.5" />
            <span>Open Settings</span>
          </Link>
        </div>
      )}

      {/* 60-minute full mock exam */}
      {mockOpen && (
        <section className="editorial-surface rounded-2xl p-6 space-y-5">
          <div>
            <h2 className="font-serif text-xl font-semibold text-ink">Full mock exam</h2>
            <p className="text-sm text-ink-secondary mt-1">
              Both tasks share one 60-minute clock, as on test day. Task 2 counts for two
              thirds of your band.
            </p>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label htmlFor="mock-t1" className="block text-sm font-medium text-ink-secondary mb-1.5">
                Task 1 · 150+ words
              </label>
              <select
                id="mock-t1"
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
              <label htmlFor="mock-t2" className="block text-sm font-medium text-ink-secondary mb-1.5">
                Task 2 · 250+ words
              </label>
              <select
                id="mock-t2"
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

          <div className="flex justify-end gap-2">
            <button type="button" onClick={() => setMockOpen(false)} className="btn btn-ghost">
              Cancel
            </button>
            <button
              id="launch-60m-mock-btn"
              type="button"
              disabled={!selectedMockT1 || !selectedMockT2 || Boolean(launchingId)}
              onClick={() => startSession("simulator_full_60m", selectedMockT1, selectedMockT2)}
              className="btn btn-primary"
            >
              {launchingId?.startsWith("simulator_full_60m") ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Clock className="w-4 h-4" />
              )}
              <span>Start 60-minute exam</span>
            </button>
          </div>
        </section>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-12 gap-7 items-start">
        <div className="lg:col-span-8 space-y-5 min-w-0">
          {resumeSession && (
            <Link
              to={`/write/${resumeSession.id}`}
              id="resume-session-card"
              className="group editorial-surface rounded-2xl p-5 flex flex-col sm:flex-row sm:items-center justify-between gap-4 hover:border-line-strong transition"
            >
              <div className="min-w-0">
                <p className="text-sm text-ink-muted">Pick up where you left off</p>
                <p className="font-serif text-lg font-semibold text-ink sm:truncate">
                  {resumeSession.task2Title || resumeSession.task1Title || "Writing practice"}
                </p>
                <p className="text-sm text-ink-secondary">{modeLabel(resumeSession.mode)}</p>
              </div>
              <span className="btn btn-primary shrink-0 self-start sm:self-auto">
                <span>Continue</span>
                <ArrowRight className="w-4 h-4" />
              </span>
            </Link>
          )}

          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="inline-flex p-1 rounded-xl bg-subtle max-w-full overflow-x-auto">
              {(
                [
                  { id: "all", label: "All", count: prompts.length },
                  { id: "task1", label: "Task 1", count: task1Prompts.length },
                  { id: "task2", label: "Task 2", count: task2Prompts.length },
                ] as const
              ).map((tab) => (
                <button
                  key={tab.id}
                  id={`filter-tab-${tab.id}`}
                  type="button"
                  aria-pressed={activeTab === tab.id}
                  onClick={() => {
                    setActiveTab(tab.id);
                    setCategoryFilter("all");
                  }}
                  className={`px-3.5 py-1.5 rounded-lg text-sm font-medium whitespace-nowrap transition cursor-pointer ${
                    activeTab === tab.id
                      ? "bg-surface text-ink shadow-xs"
                      : "text-ink-secondary hover:text-ink"
                  }`}
                >
                  {tab.label} <span className="text-ink-muted font-mono">{tab.count}</span>
                </button>
              ))}
            </div>

            <select
              aria-label="Filter by question type"
              value={categoryFilter}
              onChange={(e) => setCategoryFilter(e.target.value)}
              className="h-9 px-3 rounded-lg bg-surface border border-line text-sm text-ink-secondary"
            >
              <option value="all">All question types</option>
              {Object.entries(CATEGORY_LABELS)
                .filter(([val]) =>
                  activeTab === "all"
                    ? true
                    : prompts.some((p) => p.taskType === activeTab && p.category === val)
                )
                .map(([val, label]) => (
                  <option key={val} value={val}>
                    {label}
                  </option>
                ))}
            </select>
          </div>

          {visiblePrompts.length === 0 ? (
            <p className="editorial-surface rounded-2xl p-6 text-sm text-ink-secondary">
              No prompts of this type yet. Generate one or upload a task from your prep book.
            </p>
          ) : (
            <ul className="editorial-surface rounded-2xl divide-y divide-line">
              {visiblePrompts.map((prompt) => {
                const isTask1 = prompt.taskType === "task1";
                const isOpen = openPromptId === prompt.id;
                const simMode: SessionMode = isTask1 ? "simulator_t1" : "simulator_t2";

                return (
                  <li key={prompt.id} className="p-4 sm:p-5">
                    <div className="flex flex-col sm:flex-row sm:items-center gap-3 sm:gap-5">
                      <button
                        type="button"
                        onClick={() => setOpenPromptId(isOpen ? null : prompt.id)}
                        aria-expanded={isOpen}
                        className="flex-1 min-w-0 text-left cursor-pointer group"
                      >
                        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-ink-muted">
                          <span
                            className={`font-semibold ${isTask1 ? "text-accent" : "text-warm"}`}
                          >
                            {isTask1 ? "Task 1" : "Task 2"}
                          </span>
                          <span aria-hidden>·</span>
                          <span>{CATEGORY_LABELS[prompt.category] || prompt.category}</span>
                          <span aria-hidden>·</span>
                          <span>{prompt.topic}</span>
                          {prompt.source !== "curated" && (
                            <span className="px-1.5 py-px rounded border border-line">
                              {prompt.source === "custom" ? "Uploaded" : "Generated"}
                            </span>
                          )}
                        </div>
                        <h2 className="font-serif text-lg font-semibold text-ink mt-0.5 flex items-start gap-1.5">
                          <span className="group-hover:underline decoration-line-strong underline-offset-4">
                            {prompt.title}
                          </span>
                          <ChevronDown
                            className={`w-4 h-4 mt-1.5 shrink-0 text-ink-muted transition-transform ${
                              isOpen ? "rotate-180" : ""
                            }`}
                          />
                        </h2>
                      </button>

                      <div className="flex items-center gap-2 shrink-0">
                        <button
                          id={`coach-btn-${prompt.id}`}
                          type="button"
                          disabled={Boolean(launchingId)}
                          onClick={() =>
                            startSession("coach", isTask1 ? prompt.id : null, !isTask1 ? prompt.id : null)
                          }
                          className="btn btn-sm btn-primary"
                        >
                          <Compass className="w-3.5 h-3.5" />
                          <span>Coach</span>
                        </button>
                        <button
                          id={`sim-btn-${prompt.id}`}
                          type="button"
                          disabled={Boolean(launchingId)}
                          onClick={() =>
                            startSession(simMode, isTask1 ? prompt.id : null, !isTask1 ? prompt.id : null)
                          }
                          className="btn btn-sm btn-secondary"
                        >
                          <Clock className="w-3.5 h-3.5 text-ink-secondary" />
                          <span>{isTask1 ? "20 min test" : "40 min test"}</span>
                        </button>
                      </div>
                    </div>

                    {isOpen && (
                      <div className="mt-4 space-y-3">
                        <p className="text-sm text-ink-secondary whitespace-pre-line leading-relaxed max-w-prose">
                          {prompt.promptText}
                        </p>
                        {isTask1 && (prompt.chartSpecJson || prompt.imageDataUrl) && (
                          <TaskVisualRenderer
                            chartSpecJson={prompt.chartSpecJson}
                            imageDataUrl={prompt.imageDataUrl}
                            compact
                          />
                        )}
                        <ModelAnswer promptId={prompt.id} />
                      </div>
                    )}
                  </li>
                );
              })}
            </ul>
          )}
        </div>

        {/* Sidebar */}
        <aside className="lg:col-span-4 space-y-5">
          <div className="editorial-surface rounded-2xl p-5 space-y-4">
            <div className="flex items-center justify-between">
              <h2 className="font-serif text-lg font-semibold text-ink">Recent sessions</h2>
              <Link to="/analytics" className="text-sm font-medium text-accent hover:underline">
                See all
              </Link>
            </div>

            {recentSessions.length === 0 ? (
              <p className="text-sm text-ink-muted leading-relaxed py-2">
                Nothing yet. Pick a prompt to write your first essay.
              </p>
            ) : (
              <ul className="-mx-2">
                {recentSessions.slice(0, 6).map((s) => {
                  const completed = s.status === "completed";
                  const href = completed ? `/session/${s.id}` : `/write/${s.id}`;
                  return (
                    <li key={s.id}>
                      <Link
                        to={href}
                        className="flex items-center justify-between gap-3 px-2 py-2.5 rounded-lg hover:bg-subtle transition"
                      >
                        <div className="min-w-0">
                          <p className="text-sm font-medium text-ink truncate">
                            {s.task2Title || s.task1Title || "Writing practice"}
                          </p>
                          <p className="text-xs text-ink-muted">
                            {modeLabel(s.mode)}
                            {s.latestDraftVersion && s.latestDraftVersion > 1
                              ? ` · draft ${s.latestDraftVersion}`
                              : ""}
                          </p>
                        </div>
                        {completed && s.weightedBand !== null ? (
                          <span className="shrink-0 font-mono text-sm font-semibold text-ink">
                            {s.weightedBand.toFixed(1)}
                          </span>
                        ) : (
                          <span className="shrink-0 text-xs font-medium text-warm">In progress</span>
                        )}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          <div className="editorial-surface rounded-2xl p-5 space-y-3">
            <h3 className="font-serif text-base font-semibold text-ink">Short on time?</h3>
            <p className="text-sm text-ink-secondary leading-relaxed">
              Five-minute drills on Task 1 overviews, linking words and first-language slips.
            </p>
            <Link to="/drills" id="sidebar-drills-cta" className="btn btn-secondary w-full justify-between">
              <span>Open drills</span>
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
