import type { DrillLevel, NativeLanguageCode, TaskType } from "./types";

/**
 * Official IELTS rounding to nearest 0.5 band:
 * .25 rounds up to .5, .75 rounds up to the next whole band.
 */
export function roundIeltsBand(value: number): number {
  const clamped = Math.max(0, Math.min(9, value));
  return Math.round(clamped * 2) / 2;
}

/**
 * Official Academic IELTS Writing combined score:
 * Task 2 carries twice the weight of Task 1.
 */
export function computeWeightedExamBand(task1Band: number, task2Band: number): number {
  return roundIeltsBand((task1Band + task2Band * 2) / 3);
}

/** Maps a student's current band to a sensible default drill difficulty. */
export function levelFromBand(band: number | null | undefined): DrillLevel {
  if (band == null || Number.isNaN(band)) return "intermediate";
  if (band <= 5.5) return "beginner";
  if (band <= 6.5) return "intermediate";
  return "advanced";
}

export function isDrillLevel(value: unknown): value is DrillLevel {
  return value === "beginner" || value === "intermediate" || value === "advanced";
}

export function countEssayWords(text: string): number {
  const cleaned = text.trim();
  if (!cleaned) return 0;
  return cleaned.split(/\s+/).filter(Boolean).length;
}

export function getMinimumWords(taskType: TaskType): number {
  return taskType === "task1" ? 150 : 250;
}

export function getRecommendedMinutes(taskType: TaskType): number {
  return taskType === "task1" ? 20 : 40;
}

export function computeDaysUntilExam(examDate: string | null | undefined): number | null {
  if (!examDate) return null;
  const targetMs = new Date(examDate).getTime();
  if (Number.isNaN(targetMs)) return null;
  return Math.max(0, Math.ceil((targetMs - Date.now()) / 86_400_000));
}

export const L1_LANGUAGE_PROFILES: Record<
  NativeLanguageCode,
  { label: string; coachingFocus: string }
> = {
  es: {
    label: "Spanish (Español → English)",
    coachingFocus:
      "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): Romance false cognates ('actually' used for 'actualmente' = 'currently / at present / in recent years' — never keep 'actually' or swap it with an intensifier like 'extremely'; 'sensible' vs 'sensitive'; 'assist' vs 'attend'; 'realize' vs 'carry out'), comma splices joining independent clauses with ', however,' or ', therefore,' (classify comma splices under GRA, not CC), and definite articles before plural/uncountable general nouns ('the society', 'the technology'). Do NOT claim a 'missing subject' when a sentence uses a valid compound predicate sharing a subject (e.g., 'X rose and then fell').",
  },
  pt: {
    label: "Portuguese (Português → English)",
    coachingFocus:
      "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): Romance false cognates ('actually' used for 'atualmente' = 'currently / at present', 'pretend', 'push'), definite articles with abstract nouns, comma-spliced independent clauses (classify under GRA), and preposition calques ('depend of', 'consist in').",
  },
  fr: {
    label: "French (Français → English)",
    coachingFocus:
      "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): false cognates ('actual' / 'actually' used for 'actuel / actuellement' = 'current / currently', 'eventually', 'assist', 'demand'), article generalizations, and delayed thesis positioning in IELTS Task 2.",
  },
  zh: {
    label: "Mandarin Chinese (中文 → English)",
    coachingFocus:
      "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): singular/plural countability & article accuracy, tense consistency in historical vs present data, topic-comment comma splices ('Although..., but...' — classify under GRA), and mechanical memorized idioms.",
  },
  ar: {
    label: "Arabic (العربية → English)",
    coachingFocus:
      "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): syndetic 'and' clause chaining, relative pronoun resumption ('the policy which governments implement it'), definite article patterns, and rhetorical repetition.",
  },
  hi: {
    label: "Hindi (हिन्दी → English)",
    coachingFocus:
      "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): progressive aspect overuse for habitual states, article placement, preposition idioms ('discuss about', 'cope up with'), and formal bureaucratic phrasing ('do the needful').",
  },
  neutral: {
    label: "Language-Neutral Academic English",
    coachingFocus:
      "Focus strictly on international Academic IELTS Band 7.5–8.5 standards: natural cohesion without mechanical linkers, precise academic collocations, and balanced complex syntax.",
  },
};

/**
 * Extracts a valid JSON object from an LLM response that may wrap output in markdown fences.
 */
export function extractJsonObject<T>(raw: string): T {
  const trimmed = raw.trim();
  try {
    return JSON.parse(trimmed) as T;
  } catch {
    const fenced = trimmed.match(/```(?:json)?\s*([\s\S]*?)```/i);
    if (fenced?.[1]) {
      return JSON.parse(fenced[1].trim()) as T;
    }
    const firstBrace = trimmed.indexOf("{");
    const lastBrace = trimmed.lastIndexOf("}");
    if (firstBrace !== -1 && lastBrace > firstBrace) {
      return JSON.parse(trimmed.slice(firstBrace, lastBrace + 1)) as T;
    }
    throw new Error("Model response did not contain a valid JSON object.");
  }
}
