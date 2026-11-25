import { useState } from "react";
import { BookOpen } from "lucide-react";
import { getModelAnswer } from "@/lib/model-answers";
import { countEssayWords } from "@/lib/ielts-utils";

/**
 * Collapsible model essay for a prompt. Always starts hidden so the writer
 * is never shown the answer before choosing to see it. Renders nothing for
 * prompts without a model answer (custom uploads, AI-generated prompts).
 */
export function ModelAnswer({
  promptId,
  className = "",
}: {
  promptId: string | null | undefined;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const answer = getModelAnswer(promptId);
  if (!answer) return null;

  return (
    <div className={className}>
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className="inline-flex items-center gap-1.5 text-xs font-medium text-accent hover:underline cursor-pointer"
      >
        <BookOpen className="w-3.5 h-3.5" />
        <span>{open ? "Hide Model Answer" : "Show Model Answer"}</span>
      </button>

      {open && (
        <div className="mt-3 rounded-xl border border-line bg-subtle p-4">
          <div className="flex flex-wrap items-center justify-between gap-2 mb-3">
            <span className="text-xs font-mono uppercase tracking-wider text-ink-muted">
              Model Answer · {countEssayWords(answer)} words
            </span>
            <span className="text-xs text-ink-muted">
              One possible high-band response, not the only valid one
            </span>
          </div>
          <div className="space-y-3 font-serif text-sm text-ink leading-relaxed">
            {answer.split("\n\n").map((paragraph, i) => (
              <p key={i}>{paragraph}</p>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
