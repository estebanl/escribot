import type { AIProviderId } from "@/lib/types";

const MARKS: Record<AIProviderId, { letters: string; color: string }> = {
  openrouter: { letters: "OR", color: "#6467f2" },
  deepseek: { letters: "DS", color: "#4d6bfe" },
  gemini: { letters: "G", color: "#3b7ded" },
  openai: { letters: "AI", color: "#10a37f" },
  anthropic: { letters: "A", color: "#d97757" },
};

/** A small lettermark tile in the provider's color. */
export function ProviderIcon({ id, size = 28 }: { id: AIProviderId; size?: number }) {
  const mark = MARKS[id] ?? { letters: id.slice(0, 2).toUpperCase(), color: "var(--ink-muted)" };
  return (
    <span
      aria-hidden="true"
      className="inline-flex items-center justify-center rounded-md font-semibold text-white shrink-0 select-none"
      style={{
        width: size,
        height: size,
        backgroundColor: mark.color,
        fontSize: Math.round(size * (mark.letters.length > 1 ? 0.36 : 0.46)),
        letterSpacing: "-0.02em",
      }}
    >
      {mark.letters}
    </span>
  );
}
