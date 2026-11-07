"use client";

import { Suspense, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Feather, Lock, ArrowRight, AlertCircle } from "lucide-react";

function LoginForm() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const nextPath = searchParams.get("next") || "/";
  const [passcode, setPasscode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setLoading(true);
    try {
      const res = await fetch("/api/auth", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ passcode }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.error || "Unable to unlock workspace.");
        setLoading(false);
        return;
      }
      router.push(nextPath);
      router.refresh();
    } catch {
      setError("Network error while verifying passcode.");
      setLoading(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} className="space-y-5" id="login-passcode-form">
      <div>
        <label
          htmlFor="passcode-input"
          className="block text-xs font-semibold uppercase tracking-wider text-ink-secondary mb-2"
        >
          Workspace Passcode
        </label>
        <div className="relative">
          <Lock className="w-4 h-4 text-ink-muted absolute left-3.5 top-1/2 -translate-y-1/2" />
          <input
            id="passcode-input"
            type="password"
            required
            autoFocus
            value={passcode}
            onChange={(e) => setPasscode(e.target.value)}
            placeholder="Enter your APP_PASSCODE..."
            className="w-full pl-10 pr-4 py-3 rounded-lg bg-canvas border border-line-strong text-ink placeholder:text-ink-muted focus:outline-none focus:border-accent transition"
          />
        </div>
      </div>

      {error && (
        <div
          id="login-error-banner"
          className="flex items-start gap-2.5 p-3 rounded-lg text-sm border"
          style={{
            backgroundColor: "var(--status-danger-soft)",
            color: "var(--status-danger)",
            borderColor: "var(--status-danger)",
          }}
        >
          <AlertCircle className="w-4 h-4 shrink-0 mt-0.5" />
          <span>{error}</span>
        </div>
      )}

      <button
        id="unlock-workspace-button"
        type="submit"
        disabled={loading || !passcode.trim()}
        className="w-full flex items-center justify-center gap-2 py-3 px-4 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg font-medium text-sm transition disabled:opacity-50 cursor-pointer"
      >
        <span>{loading ? "Unlocking Studio..." : "Unlock Writing Studio"}</span>
        <ArrowRight className="w-4 h-4" />
      </button>
    </form>
  );
}

export default function LoginPage() {
  return (
    <main className="min-h-screen flex items-center justify-center px-4 py-12 bg-canvas">
      <div className="w-full max-w-md editorial-surface rounded-2xl p-8 sm:p-10">
        <div className="flex items-center gap-3 mb-6">
          <div className="w-10 h-10 rounded-xl bg-accent text-accent-fg flex items-center justify-center">
            <Feather className="w-5 h-5" />
          </div>
          <div>
            <p className="text-xs font-mono uppercase tracking-widest text-ink-muted">
              Academic IELTS Prep
            </p>
            <h1 className="font-serif text-2xl font-semibold tracking-tight text-ink">
              FluentEscritor
            </h1>
          </div>
        </div>

        <p className="text-sm text-ink-secondary leading-relaxed mb-7">
          Enter your personal workspace passcode to sync your Academic Task 1 &amp; Task 2
          drafts, Error Vault, and DeepSeek examiner sessions across devices.
        </p>

        <Suspense fallback={<div className="h-32 animate-pulse bg-subtle rounded-lg" />}>
          <LoginForm />
        </Suspense>
      </div>
    </main>
  );
}
