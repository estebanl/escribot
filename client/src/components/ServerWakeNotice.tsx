import { useSyncExternalStore } from "react";
import { isServerStarting, subscribeServerWake } from "@/lib/server-wake";

/**
 * Shown while the server starts after being idle. main.tsx renders it in its own root rather
 * than in the router, which renders nothing until the first page's data arrives. On that
 * empty first page it sits in the middle; over a page already showing, at the bottom.
 */
export function ServerWakeNotice() {
  const starting = useSyncExternalStore(subscribeServerWake, isServerStarting, () => false);
  const pageEmpty = starting && document.getElementById("root")?.childElementCount === 0;

  return (
    <div
      role="status"
      aria-live="polite"
      className={`pointer-events-none fixed inset-x-0 z-50 flex justify-center px-4 ${pageEmpty ? "inset-y-0 items-center" : "bottom-6"}`}
    >
      {starting && (
        <p className="flex items-center gap-3 rounded-full border border-line bg-surface px-4 py-2.5 text-sm text-ink-secondary shadow-[var(--shadow-float)]">
          <span className="size-2 shrink-0 animate-pulse rounded-full bg-accent" aria-hidden="true" />
          Starting up after a quiet spell. This takes about 30 seconds.
        </p>
      )}
    </div>
  );
}
