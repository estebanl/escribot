import type { NativeLanguageCode, TaskType } from "./types";

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
      "Watch for Romance false cognates ('actually' vs 'currently', 'sensible' vs 'sensitive', 'assist' vs 'attend', 'realize' vs 'carry out'), comma splices joining independent clauses with ', however,' or ', therefore,', overuse of definite articles before plural abstract nouns ('the society', 'the technology'), missing subject pronouns ('is important to note'), and overly ornate multi-clause sentences.",
  },
  pt: {
    label: "Portuguese (Português → English)",
    coachingFocus:
      "Watch for Romance false cognates ('actually', 'pretend', 'push'), definite articles with abstract nouns, comma-spliced long clauses, and preposition calques ('depend of', 'consist in').",
  },
  fr: {
    label: "French (Français → English)",
    coachingFocus:
      "Watch for false cognates ('actual', 'eventually', 'assist', 'demand'), article generalizations, and French-style three-part thesis delays that hide a clear position in IELTS Task 2.",
  },
  zh: {
    label: "Mandarin Chinese (中文 → English)",
    coachingFocus:
      "Watch for singular/plural countability & article accuracy, tense consistency in historical vs present data, topic-comment comma splices ('Although..., but...'), and mechanical memorized idioms.",
  },
  ar: {
    label: "Arabic (العربية → English)",
    coachingFocus:
      "Watch for syndetic 'and' clause chaining, relative pronoun resumption ('the policy which governments implement it'), definite article patterns, and rhetorical repetition.",
  },
  hi: {
    label: "Hindi (हिन्दी → English)",
    coachingFocus:
      "Watch for progressive aspect overuse for habitual states, article placement, preposition idioms ('discuss about', 'cope up with'), and formal bureaucratic phrasing ('do the needful').",
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
