import type {
  OutlineFeedback,
  OutlineInput,
  TaskType,
  ToolkitResponse,
} from "./types";

export type EvaluationErrorKind = "timeout" | "network" | "server";

export interface DraftSubmissionPayload {
  promptId: string;
  taskType: TaskType;
  essayText: string;
  secondsSpent?: number;
  outline?: OutlineInput | null;
  outlineFeedback?: OutlineFeedback | null;
  coachingHistory?: ToolkitResponse[] | null;
  promptHighlights?: string[];
  scratchNotes?: string;
}

export interface SubmitEvaluationResponse {
  ok: boolean;
  sessionId?: string;
  weightedBand?: number | null;
  results?: Array<{
    draftId: string;
    version: number;
    taskType: TaskType;
    wordCount: number;
    evaluationId: string;
  }>;
  error?: string;
  errorKind?: EvaluationErrorKind;
}

const TIMEOUT_MESSAGE =
  "Grading took too long, please try again. Your essay is still safely in the editor.";

function detectHttpTimeout(res: Response, rawText: string): boolean {
  const vercelError = res.headers.get("x-vercel-error") || "";
  if (
    res.status === 504 ||
    res.status === 408 ||
    res.status === 524 ||
    vercelError.toUpperCase().includes("TIMEOUT") ||
    /FUNCTION_INVOCATION_TIMEOUT|gateway timeout|timed?\s*out/i.test(rawText)
  ) {
    return true;
  }
  return false;
}

function parseJsonSafely<T>(raw: string): T | null {
  if (!raw || !raw.trim()) return null;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

export async function submitEvaluationStream(
  payload: {
    sessionId: string;
    totalSecondsSpent?: number;
    submissions: DraftSubmissionPayload[];
  },
  onProgress?: (stage: string) => void
): Promise<SubmitEvaluationResponse> {
  const controller = new AbortController();
  let timedOutByClient = false;
  const safetyTimer = setTimeout(() => {
    timedOutByClient = true;
    controller.abort();
  }, 68_000);

  let res: Response;
  try {
    res = await fetch("/api/ai/evaluate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
      signal: controller.signal,
    });
  } catch (err) {
    clearTimeout(safetyTimer);
    if (
      timedOutByClient ||
      (err instanceof Error &&
        (err.name === "AbortError" || err.name === "TimeoutError"))
    ) {
      return {
        ok: false,
        errorKind: "timeout",
        error: TIMEOUT_MESSAGE,
      };
    }
    return {
      ok: false,
      errorKind: "network",
      error:
        "Could not connect to the server. Please check your internet connection and try again.",
    };
  }

  try {
    if (!res.ok) {
      const rawText = await res.text().catch(() => "");
      if (detectHttpTimeout(res, rawText)) {
        return {
          ok: false,
          errorKind: "timeout",
          error: TIMEOUT_MESSAGE,
        };
      }

      const parsedErr = parseJsonSafely<{ error?: string }>(rawText);
      if (parsedErr?.error) {
        return {
          ok: false,
          errorKind: "server",
          error: parsedErr.error,
        };
      }

      if (res.status === 502 || res.status === 503) {
        return {
          ok: false,
          errorKind: "server",
          error:
            "The grading server was temporarily unavailable. Please try again in a moment.",
        };
      }

      return {
        ok: false,
        errorKind: "server",
        error:
          rawText.trim().length > 0 && rawText.trim().length < 200
            ? rawText.trim()
            : `Grading request failed (HTTP ${res.status}). Please try again.`,
      };
    }

    const contentType = res.headers.get("content-type") || "";
    if (contentType.includes("application/json") || !res.body) {
      const rawText = await res.text();
      const parsed = parseJsonSafely<{
        sessionId: string;
        weightedBand: number | null;
        results: SubmitEvaluationResponse["results"];
        error?: string;
      }>(rawText);

      if (!parsed) {
        return {
          ok: false,
          errorKind: "server",
          error: "Received an unexpected response while grading. Please try again.",
        };
      }
      if (parsed.error) {
        return {
          ok: false,
          errorKind: "server",
          error: parsed.error,
        };
      }
      return {
        ok: true,
        sessionId: parsed.sessionId,
        weightedBand: parsed.weightedBand,
        results: parsed.results,
      };
    }

    // Stream NDJSON progress & final result
    const reader = res.body.getReader();
    const decoder = new TextDecoder("utf-8");
    let buffer = "";
    let finalResult: SubmitEvaluationResponse | null = null;

    const handleLine = (line: string): SubmitEvaluationResponse | null => {
      const trimmed = line.trim();
      if (!trimmed) return null;
      const event = parseJsonSafely<{
        type?: string;
        stage?: string;
        error?: string;
        isTimeout?: boolean;
        sessionId?: string;
        weightedBand?: number | null;
        results?: SubmitEvaluationResponse["results"];
      }>(trimmed);

      if (!event) return null;

      if (event.type === "progress" && event.stage) {
        onProgress?.(event.stage);
        return null;
      }

      if (event.type === "error") {
        return {
          ok: false,
          errorKind: event.isTimeout ? "timeout" : "server",
          error: event.error || "Grading failed. Please try again.",
        };
      }

      if (event.type === "result") {
        return {
          ok: true,
          sessionId: event.sessionId,
          weightedBand: event.weightedBand,
          results: event.results,
        };
      }

      return null;
    };

    try {
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n");
        buffer = lines.pop() ?? "";
        for (const line of lines) {
          const outcome = handleLine(line);
          if (outcome) {
            finalResult = outcome;
          }
        }
      }
      buffer += decoder.decode();
      if (buffer.trim()) {
        const outcome = handleLine(buffer);
        if (outcome) {
          finalResult = outcome;
        }
      }
    } catch {
      // Stream terminated mid-flight (e.g. serverless invocation cutoff or client abort)
      if (finalResult) return finalResult;
      return {
        ok: false,
        errorKind: "timeout",
        error: TIMEOUT_MESSAGE,
      };
    }

    if (finalResult) {
      return finalResult;
    }

    // Stream ended cleanly with 0 result/error frames -> serverless cutoff at maxDuration
    return {
      ok: false,
      errorKind: "timeout",
      error: TIMEOUT_MESSAGE,
    };
  } finally {
    clearTimeout(safetyTimer);
  }
}

export async function safePostJson<T>(
  url: string,
  payload: unknown,
  actionLabel: string
): Promise<{ ok: true; data: T } | { ok: false; error: string }> {
  let res: Response;
  try {
    res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
  } catch {
    return {
      ok: false,
      error: `Could not connect to the server while ${actionLabel}. Please check your internet connection.`,
    };
  }

  const rawText = await res.text().catch(() => "");
  if (!res.ok) {
    if (detectHttpTimeout(res, rawText)) {
      return {
        ok: false,
        error: `Request timed out while ${actionLabel}. Please try again.`,
      };
    }
    const parsedErr = parseJsonSafely<{ error?: string }>(rawText);
    return {
      ok: false,
      error: parsedErr?.error || `Request failed (HTTP ${res.status}). Please try again.`,
    };
  }

  const parsed = parseJsonSafely<T>(rawText);
  if (!parsed) {
    return {
      ok: false,
      error: `Unexpected server response while ${actionLabel}. Please try again.`,
    };
  }
  return { ok: true, data: parsed };
}
