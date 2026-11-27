import { useEffect, useState } from "react";
import {
  X,
  Upload,
  Sparkles,
  Image as ImageIcon,
  Loader2,
  Wand2,
  RefreshCw,
  Save,
  Trash2,
} from "lucide-react";
import { safePostJson } from "@/lib/client-evaluate";
import type { PromptCategory, TaskType } from "@/lib/types";
import { TEXT_LIMITS } from "@/lib/limits";

export interface PromptRecord {
  id: string;
  taskType: string;
  category: string;
  topic: string;
  title: string;
  promptText: string;
  chartSpecJson: string | null;
  imageDataUrl: string | null;
  extractedVisualSpec?: string | null;
  source: string;
  createdAt: string;
  updatedAt?: string | null;
}

const TASK1_CATEGORIES: Array<{ value: PromptCategory; label: string }> = [
  { value: "line", label: "Line Graph (Trends Over Time)" },
  { value: "bar", label: "Bar Chart (Comparative Categories)" },
  { value: "pie", label: "Pie Charts (Proportional Shifts)" },
  { value: "table", label: "Data Table (Multi-Variable Synthesis)" },
  { value: "mixed", label: "Mixed Charts (Bar + Line Dual Axis)" },
  { value: "process", label: "Process / Lifecycle Diagram" },
  { value: "map", label: "Two-Period Comparison Map" },
];

const TASK2_CATEGORIES: Array<{ value: PromptCategory; label: string }> = [
  { value: "opinion", label: "Opinion (Agree or Disagree)" },
  { value: "discussion", label: "Discussion (Both Views & Opinion)" },
  { value: "advantages", label: "Advantages vs. Disadvantages" },
  { value: "problem_solution", label: "Causes / Problems & Solutions" },
  { value: "two_part", label: "Two-Part Direct Question" },
];

/**
 * Compresses an uploaded/pasted chart image client-side into a crisp WebP data URL
 * (~60–140 KB), small enough to upload and store cheaply.
 */
async function compressImageFileToWebP(
  file: File,
  maxDimension = 1400,
  quality = 0.84
): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error("Failed to read image file."));
    reader.onload = () => {
      const img = new window.Image();
      img.onerror = () => reject(new Error("Invalid image format."));
      img.onload = () => {
        let { width, height } = img;
        if (width > maxDimension || height > maxDimension) {
          const ratio = Math.min(maxDimension / width, maxDimension / height);
          width = Math.round(width * ratio);
          height = Math.round(height * ratio);
        }
        const canvas = document.createElement("canvas");
        canvas.width = width;
        canvas.height = height;
        const ctx = canvas.getContext("2d");
        if (!ctx) {
          reject(new Error("Canvas unavailable."));
          return;
        }
        ctx.fillStyle = "#ffffff";
        ctx.fillRect(0, 0, width, height);
        ctx.drawImage(img, 0, 0, width, height);
        const webp = canvas.toDataURL("image/webp", quality);
        resolve(webp);
      };
      img.src = reader.result as string;
    };
    reader.readAsDataURL(file);
  });
}

function readFileAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error("Failed to read image file."));
    reader.onload = () => resolve(reader.result as string);
    reader.readAsDataURL(file);
  });
}

/**
 * Uploads a custom or prep-book task, or, given `initialPrompt`, edits one the user
 * uploaded before. Mount a fresh instance (a `key` per prompt) for each prompt edited.
 */
export function CustomPromptModal({
  open,
  onClose,
  onCreated,
  canTransformSvg = false,
  initialPrompt = null,
  usageCount = 0,
}: {
  open: boolean;
  onClose: () => void;
  onCreated: (prompt: PromptRecord) => void;
  canTransformSvg?: boolean;
  initialPrompt?: PromptRecord | null;
  /** Sessions that use `initialPrompt`; they will show the edited version. */
  usageCount?: number;
}) {
  const editing = initialPrompt !== null;
  const savedImageUrl = initialPrompt?.taskType === "task1" ? initialPrompt.imageDataUrl : null;
  const [taskType, setTaskType] = useState<TaskType>(
    (initialPrompt?.taskType as TaskType | undefined) ?? "task1"
  );
  const [category, setCategory] = useState<PromptCategory>(
    (initialPrompt?.category as PromptCategory | undefined) ?? "line"
  );
  const [title, setTitle] = useState(initialPrompt?.title ?? "");
  const [topic, setTopic] = useState(initialPrompt?.topic ?? "");
  const [promptText, setPromptText] = useState(initialPrompt?.promptText ?? "");
  const [imageDataUrl, setImageDataUrl] = useState<string | null>(savedImageUrl);
  // A saved image is a link, so whether it is an SVG is read from the image itself.
  const [savedImageIsSvg, setSavedImageIsSvg] = useState(false);
  const [svgDataUrl, setSvgDataUrl] = useState<string | null>(null);
  const [extractedVisualSpec, setExtractedVisualSpec] = useState<string | null>(null);
  const [previewMode, setPreviewMode] = useState<"original" | "svg">("original");
  const [transforming, setTransforming] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!savedImageUrl || savedImageUrl.startsWith("data:")) return;
    let cancelled = false;
    fetch(savedImageUrl)
      .then((res) => {
        if (!cancelled && res.headers.get("content-type")?.startsWith("image/svg+xml")) {
          setSavedImageIsSvg(true);
        }
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [savedImageUrl]);

  if (!open) return null;

  const showingSavedImage = editing && imageDataUrl !== null && imageDataUrl === savedImageUrl;
  const isUploadedSvg =
    Boolean(imageDataUrl?.startsWith("data:image/svg+xml;")) ||
    (showingSavedImage && (savedImageIsSvg || Boolean(savedImageUrl?.startsWith("data:image/svg+xml;"))));
  // The task type of a task that has been practised cannot change: its drafts are for that task.
  const taskTypeLocked = editing && usageCount > 0;
  const usingGeneratedSvg = previewMode === "svg" && Boolean(svgDataUrl);
  const activePreviewUrl = usingGeneratedSvg ? svgDataUrl : imageDataUrl;

  function clearImageState() {
    setImageDataUrl(null);
    setSvgDataUrl(null);
    setExtractedVisualSpec(null);
    setPreviewMode("original");
  }

  async function handleFileSelect(file: File | undefined | null) {
    if (!file) return;
    setError(null);
    try {
      // SVGs are stored as SVGs on Pro; on Free they are drawn into a WebP like any image.
      const processed =
        canTransformSvg && file.type === "image/svg+xml"
          ? await readFileAsDataUrl(file)
          : await compressImageFileToWebP(file);
      setImageDataUrl(processed);
      setSvgDataUrl(null);
      setExtractedVisualSpec(null);
      setPreviewMode("original");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not process image.");
    }
  }

  async function handlePaste(e: React.ClipboardEvent) {
    const items = e.clipboardData?.items;
    if (!items) return;
    for (const item of Array.from(items)) {
      if (item.type.startsWith("image/")) {
        e.preventDefault();
        const file = item.getAsFile();
        await handleFileSelect(file);
        return;
      }
    }
  }

  async function handleTransformToSvg() {
    if (!imageDataUrl || isUploadedSvg || transforming) return;
    setTransforming(true);
    setError(null);
    try {
      const outcome = await safePostJson<{
        svgDataUrl: string;
        extractedVisualSpec?: string | null;
      }>(
        "/api/ai/transform-svg",
        {
          // A saved image is read on the server; a new one is sent as it is.
          ...(showingSavedImage && !imageDataUrl.startsWith("data:")
            ? { promptId: initialPrompt?.id }
            : { imageDataUrl }),
          category,
          promptText: promptText.trim() || undefined,
        },
        "transforming your image into SVG"
      );
      if (!outcome.ok) {
        setError(outcome.error);
        return;
      }
      setSvgDataUrl(outcome.data.svgDataUrl);
      setExtractedVisualSpec(outcome.data.extractedVisualSpec ?? null);
      setPreviewMode("svg");
    } finally {
      setTransforming(false);
    }
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      const finalImageUrl =
        taskType === "task1"
          ? usingGeneratedSvg
            ? svgDataUrl
            : imageDataUrl
          : null;
      const finalVisualSpec =
        taskType === "task1" && usingGeneratedSvg ? extractedVisualSpec : null;
      // Left out, the saved image and its visual spec stay as they are.
      const imageFields =
        editing && taskType === "task1" && finalImageUrl === savedImageUrl
          ? {}
          : { imageDataUrl: finalImageUrl, extractedVisualSpec: finalVisualSpec };

      const res = await fetch(
        initialPrompt ? `/api/prompts/${encodeURIComponent(initialPrompt.id)}` : "/api/prompts",
        {
          method: editing ? "PATCH" : "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            taskType,
            category,
            title,
            topic,
            promptText,
            ...imageFields,
          }),
        }
      );
      const data = await res.json();
      if (!res.ok) {
        setError(
          data.error || (editing ? "Failed to save your changes." : "Failed to save custom prompt.")
        );
        setSaving(false);
        return;
      }
      setTitle("");
      setTopic("");
      setPromptText("");
      clearImageState();
      onCreated(data.prompt);
      onClose();
    } catch {
      setError(
        editing ? "Network error while saving your changes." : "Network error while saving custom prompt."
      );
    } finally {
      setSaving(false);
    }
  }

  const categories = taskType === "task1" ? TASK1_CATEGORIES : TASK2_CATEGORIES;

  return (
    <div
      onPaste={handlePaste}
      className="fixed inset-0 z-50 bg-black/60 backdrop-blur-xs flex items-center justify-center p-4 overflow-y-auto"
    >
      <div className="w-full max-w-xl editorial-surface rounded-2xl p-6 sm:p-7 my-8">
        <div className="flex items-center justify-between mb-5">
          <div>
            <h2 className="font-serif text-xl font-semibold text-ink">
              {editing ? "Edit Task" : "Upload Custom or Prep-Book Task"}
            </h2>
            <p className="text-xs text-ink-secondary mt-0.5">
              {editing ? "Change the instructions, or" : "Paste instructions and"} press <kbd className="font-mono px-1.5 py-0.5 rounded bg-subtle">Ctrl/Cmd+V</kbd> anywhere to {editing ? "replace the" : "attach a"} Task 1 chart screenshot.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="p-1.5 rounded-lg hover:bg-subtle text-ink-muted hover:text-ink cursor-pointer"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          {editing && usageCount > 0 && (
            <p id="edit-prompt-usage-notice" className="text-xs text-ink-secondary rounded-lg bg-subtle px-3 py-2">
              {usageCount === 1
                ? "You have practised this task once. That session will show the edited task; its scores stay as graded."
                : `You have practised this task ${usageCount} times. Those sessions will show the edited task; their scores stay as graded.`}
            </p>
          )}
          <div className="grid grid-cols-2 gap-2 p-1 rounded-xl bg-subtle">
            <button
              type="button"
              disabled={taskTypeLocked && taskType !== "task1"}
              title={taskTypeLocked && taskType !== "task1" ? "A task you have practised keeps its task type." : undefined}
              onClick={() => {
                if (taskType === "task1") return;
                setTaskType("task1");
                setCategory("line");
              }}
              className={`py-2 rounded-lg text-xs font-semibold transition cursor-pointer disabled:cursor-not-allowed disabled:opacity-50 ${
                taskType === "task1"
                  ? "bg-surface text-ink shadow-xs"
                  : "text-ink-secondary"
              }`}
            >
              Academic Task 1 (Visual · 150w)
            </button>
            <button
              type="button"
              disabled={taskTypeLocked && taskType !== "task2"}
              title={taskTypeLocked && taskType !== "task2" ? "A task you have practised keeps its task type." : undefined}
              onClick={() => {
                if (taskType === "task2") return;
                setTaskType("task2");
                setCategory("opinion");
              }}
              className={`py-2 rounded-lg text-xs font-semibold transition cursor-pointer disabled:cursor-not-allowed disabled:opacity-50 ${
                taskType === "task2"
                  ? "bg-surface text-ink shadow-xs"
                  : "text-ink-secondary"
              }`}
            >
              Academic Task 2 (Essay · 250w)
            </button>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Task Archetype
              </label>
              <select
                value={category}
                onChange={(e) => setCategory(e.target.value as PromptCategory)}
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
              >
                {categories.map((c) => (
                  <option key={c.value} value={c.value}>
                    {c.label}
                  </option>
                ))}
              </select>
            </div>
            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Short Title
              </label>
              <input
                type="text"
                value={title}
                maxLength={TEXT_LIMITS.shortText}
                onChange={(e) => setTitle(e.target.value)}
                placeholder="Cambridge 18 Test 2..."
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Subject Topic
              </label>
              <input
                type="text"
                value={topic}
                maxLength={TEXT_LIMITS.shortText}
                onChange={(e) => setTopic(e.target.value)}
                placeholder="Economics, Energy..."
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
              />
            </div>
          </div>

          {taskType === "task1" && (
            <div className="space-y-2">
              <div className="flex items-center justify-between gap-2">
                <label className="block text-xs font-medium text-ink-secondary">
                  {`Chart / Diagram / Map Screenshot (${
                    isUploadedSvg || usingGeneratedSvg ? "SVG" : "WebP Compressed"
                  })`}
                </label>
                {canTransformSvg && imageDataUrl && !isUploadedSvg && svgDataUrl && (
                  <div className="inline-flex items-center gap-0.5 p-0.5 rounded-lg bg-subtle border border-line">
                    <button
                      type="button"
                      onClick={() => setPreviewMode("svg")}
                      className={`px-2 py-0.5 rounded-md text-xs font-medium transition cursor-pointer ${
                        previewMode === "svg"
                          ? "bg-surface text-ink shadow-xs"
                          : "text-ink-secondary hover:text-ink"
                      }`}
                    >
                      SVG
                    </button>
                    <button
                      type="button"
                      onClick={() => setPreviewMode("original")}
                      className={`px-2 py-0.5 rounded-md text-xs font-medium transition cursor-pointer ${
                        previewMode === "original"
                          ? "bg-surface text-ink shadow-xs"
                          : "text-ink-secondary hover:text-ink"
                      }`}
                    >
                      Original
                    </button>
                  </div>
                )}
              </div>

              {activePreviewUrl ? (
                <div className="space-y-2">
                  <div className="relative rounded-xl border border-line-strong bg-white p-3">
                    <img
                      src={activePreviewUrl}
                      alt={
                        usingGeneratedSvg
                          ? "Task 1 generated SVG preview"
                          : "Task 1 screenshot preview"
                      }
                      className="max-h-56 mx-auto object-contain rounded"
                    />
                    <button
                      type="button"
                      disabled={transforming}
                      onClick={clearImageState}
                      className="absolute top-2 right-2 px-2 py-1 rounded bg-slate-900/80 text-white text-xs cursor-pointer disabled:opacity-50"
                    >
                      Remove
                    </button>
                  </div>

                  {canTransformSvg && !isUploadedSvg && (
                    <div className="flex flex-wrap items-center justify-between gap-2 px-1">
                      <span className="text-xs text-ink-secondary">
                        {usingGeneratedSvg
                          ? "Clean SVG selected — saving will store the SVG version."
                          : svgDataUrl
                          ? "Original image selected — saving will store the original image."
                          : "Redraw photos or blurry screenshots into a crisp vector SVG."}
                      </span>
                      <button
                        id="transform-to-svg-btn"
                        type="button"
                        disabled={transforming || saving}
                        onClick={handleTransformToSvg}
                        className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-line-strong bg-surface hover:bg-subtle text-xs font-medium text-ink disabled:opacity-50 transition cursor-pointer"
                      >
                        {transforming ? (
                          <Loader2 className="w-3.5 h-3.5 animate-spin text-accent" />
                        ) : svgDataUrl ? (
                          <RefreshCw className="w-3.5 h-3.5 text-accent" />
                        ) : (
                          <Wand2 className="w-3.5 h-3.5 text-accent" />
                        )}
                        <span>
                          {transforming
                            ? "Transforming..."
                            : svgDataUrl
                            ? "Regenerate"
                            : "Transform to SVG"}
                        </span>
                      </button>
                    </div>
                  )}
                </div>
              ) : (
                <label className="flex flex-col items-center justify-center gap-2 py-6 px-4 rounded-xl border border-dashed border-line-strong bg-canvas hover:bg-subtle transition cursor-pointer text-center">
                  <ImageIcon className="w-6 h-6 text-accent" />
                  <span className="text-xs text-ink-secondary">
                    Click to select an image, drag &amp; drop, or paste screenshot (<span className="font-mono">Ctrl+V</span>)
                  </span>
                  <input
                    type="file"
                    accept="image/*"
                    className="hidden"
                    onChange={(e) => handleFileSelect(e.target.files?.[0])}
                  />
                </label>
              )}
            </div>
          )}

          <div>
            <label className="block text-xs font-medium text-ink-secondary mb-1">
              Prompt Instructions *
            </label>
            <textarea
              required
              rows={4}
              value={promptText}
              maxLength={TEXT_LIMITS.promptText}
              onChange={(e) => setPromptText(e.target.value)}
              placeholder={
                taskType === "task1"
                  ? "The chart below shows... Summarise the information by selecting and reporting the main features, and make comparisons where relevant."
                  : "Some people believe that... Discuss both these views and give your own opinion."
              }
              className="w-full px-3 py-2.5 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
            />
          </div>

          {error && <p className="text-xs text-red-600">{error}</p>}

          <div className="flex items-center justify-end gap-2.5 pt-2">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 rounded-lg border border-line text-sm text-ink-secondary hover:text-ink cursor-pointer"
            >
              Cancel
            </button>
            <button
              id="save-custom-prompt-btn"
              type="submit"
              disabled={saving || transforming || !promptText.trim()}
              className="flex items-center gap-2 px-4 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-sm font-medium disabled:opacity-50 cursor-pointer"
            >
              {saving ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : editing ? (
                <Save className="w-4 h-4" />
              ) : (
                <Upload className="w-4 h-4" />
              )}
              <span>{editing ? "Save Changes" : "Save to Library"}</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

/** Confirms and deletes one of the user's uploaded tasks. */
export function DeletePromptDialog({
  prompt,
  usageCount = 0,
  onClose,
  onDeleted,
}: {
  prompt: PromptRecord;
  usageCount?: number;
  onClose: () => void;
  onDeleted: (promptId: string) => void;
}) {
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleDelete() {
    setDeleting(true);
    setError(null);
    try {
      const res = await fetch(`/api/prompts/${encodeURIComponent(prompt.id)}`, { method: "DELETE" });
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        setError(data.error || "Failed to delete the task.");
        return;
      }
      onDeleted(prompt.id);
      onClose();
    } catch {
      setError("Network error while deleting the task.");
    } finally {
      setDeleting(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 bg-black/60 backdrop-blur-xs flex items-center justify-center p-4">
      <div
        role="alertdialog"
        aria-labelledby="delete-prompt-title"
        className="w-full max-w-md editorial-surface rounded-2xl p-6 space-y-4"
      >
        <h2 id="delete-prompt-title" className="font-serif text-xl font-semibold text-ink">
          Delete this task?
        </h2>
        <p className="text-sm text-ink-secondary">
          {usageCount > 0
            ? `“${prompt.title}” will be removed from your library. You have practised it ${
                usageCount === 1 ? "once" : `${usageCount} times`
              }, so ${
                usageCount === 1 ? "that session keeps" : "those sessions keep"
              } the task and its image, to resume or review.`
            : `“${prompt.title}” and its image will be removed from your library.`}
        </p>
        {error && <p className="text-xs text-red-600">{error}</p>}
        <div className="flex items-center justify-end gap-2.5">
          <button
            type="button"
            onClick={onClose}
            className="px-4 py-2 rounded-lg border border-line text-sm text-ink-secondary hover:text-ink cursor-pointer"
          >
            Cancel
          </button>
          <button
            id="confirm-delete-prompt-btn"
            type="button"
            disabled={deleting}
            onClick={handleDelete}
            className="flex items-center gap-2 px-4 py-2 rounded-lg bg-red-600 hover:bg-red-700 text-white text-sm font-medium disabled:opacity-50 cursor-pointer"
          >
            {deleting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Trash2 className="w-4 h-4" />}
            <span>Delete</span>
          </button>
        </div>
      </div>
    </div>
  );
}

export function GeneratePromptModal({
  open,
  onClose,
  onCreated,
}: {
  open: boolean;
  onClose: () => void;
  onCreated: (prompt: PromptRecord) => void;
}) {
  const [taskType, setTaskType] = useState<TaskType>("task1");
  const [category, setCategory] = useState<PromptCategory>("line");
  const [topic, setTopic] = useState("");
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!open) return null;

  const categories = taskType === "task1" ? TASK1_CATEGORIES : TASK2_CATEGORIES;

  async function handleGenerate(e: React.FormEvent) {
    e.preventDefault();
    setGenerating(true);
    setError(null);
    try {
      const outcome = await safePostJson<{ prompt: PromptRecord }>(
        "/api/ai/generate-prompt",
        { taskType, category, topic },
        "generating your IELTS prompt"
      );
      if (!outcome.ok) {
        setError(outcome.error);
        return;
      }
      onCreated(outcome.data.prompt);
      onClose();
    } finally {
      setGenerating(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 bg-black/60 backdrop-blur-xs flex items-center justify-center p-4">
      <div className="w-full max-w-md editorial-surface rounded-2xl p-6 sm:p-7">
        <div className="flex items-center justify-between mb-4">
          <h2 className="font-serif text-xl font-semibold text-ink">
            Generate Fresh IELTS Prompt
          </h2>
          <button
            type="button"
            onClick={onClose}
            className="p-1.5 rounded-lg hover:bg-subtle text-ink-muted hover:text-ink cursor-pointer"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        <form onSubmit={handleGenerate} className="space-y-4">
          <div className="grid grid-cols-2 gap-2 p-1 rounded-xl bg-subtle">
            <button
              type="button"
              onClick={() => {
                setTaskType("task1");
                setCategory("line");
              }}
              className={`py-2 rounded-lg text-xs font-semibold transition cursor-pointer ${
                taskType === "task1"
                  ? "bg-surface text-ink shadow-xs"
                  : "text-ink-secondary"
              }`}
            >
              Task 1 (Chart / Map / Process)
            </button>
            <button
              type="button"
              onClick={() => {
                setTaskType("task2");
                setCategory("opinion");
              }}
              className={`py-2 rounded-lg text-xs font-semibold transition cursor-pointer ${
                taskType === "task2"
                  ? "bg-surface text-ink shadow-xs"
                  : "text-ink-secondary"
              }`}
            >
              Task 2 (Academic Essay)
            </button>
          </div>

          <div>
            <label className="block text-xs font-medium text-ink-secondary mb-1">
              Archetype
            </label>
            <select
              value={category}
              onChange={(e) => setCategory(e.target.value as PromptCategory)}
              className="w-full px-3 py-2.5 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
            >
              {categories.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.label}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-xs font-medium text-ink-secondary mb-1">
              Optional Topic Focus
            </label>
            <input
              type="text"
              value={topic}
              maxLength={TEXT_LIMITS.shortText}
              onChange={(e) => setTopic(e.target.value)}
              placeholder="e.g., Renewable Desalination, Urban Housing, Telemedicine..."
              className="w-full px-3 py-2.5 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
            />
          </div>

          {error && <p className="text-xs text-red-600">{error}</p>}

          <div className="flex items-center justify-end gap-2 pt-2">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 rounded-lg border border-line text-sm text-ink-secondary cursor-pointer"
            >
              Cancel
            </button>
            <button
              id="confirm-generate-prompt-btn"
              type="submit"
              disabled={generating}
              className="flex items-center gap-2 px-4 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-sm font-medium disabled:opacity-50 cursor-pointer"
            >
              {generating ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Sparkles className="w-4 h-4" />
              )}
              <span>{generating ? "Designing Task..." : "Generate Task"}</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
