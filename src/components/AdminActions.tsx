"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";

export function CompPlanToggle({ userId, compPro }: { userId: string; compPro: boolean }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);

  async function toggle() {
    setBusy(true);
    const res = await fetch(`/api/admin/users/${encodeURIComponent(userId)}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ plan: compPro ? "free" : "pro" }),
    });
    setBusy(false);
    if (res.ok) router.refresh();
    else alert("Could not change the plan.");
  }

  return (
    <button
      type="button"
      onClick={toggle}
      disabled={busy}
      className="text-xs px-2 py-1 rounded border border-line-strong hover:bg-subtle disabled:opacity-50 cursor-pointer whitespace-nowrap"
    >
      {compPro ? "Remove comp Pro" : "Give comp Pro"}
    </button>
  );
}

export function MigrateImagesButton({ count }: { count: number }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  async function run() {
    setBusy(true);
    setMessage(null);
    let moved = 0;
    let failed = 0;
    // Process in batches until nothing movable is left.
    for (let i = 0; i < 200; i++) {
      const res = await fetch("/api/admin/migrate-images", { method: "POST" });
      if (!res.ok) {
        setMessage("Migration failed. Try again.");
        break;
      }
      const data = (await res.json()) as { moved: number; failed: number; remaining: number };
      moved += data.moved;
      failed += data.failed;
      if (data.moved === 0 || data.remaining === 0) break;
    }
    setMessage(`Moved ${moved} image${moved === 1 ? "" : "s"}${failed ? `, ${failed} could not be moved` : ""}.`);
    setBusy(false);
    router.refresh();
  }

  return (
    <div className="flex items-center gap-3">
      <button
        type="button"
        onClick={run}
        disabled={busy || count === 0}
        className="px-3 py-1.5 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-sm font-medium disabled:opacity-50 cursor-pointer"
      >
        {busy ? "Moving..." : `Move ${count} inline image${count === 1 ? "" : "s"} to storage`}
      </button>
      {message && <span className="text-sm text-ink-secondary">{message}</span>}
    </div>
  );
}
