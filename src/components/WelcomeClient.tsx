"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { ArrowRight, Feather } from "lucide-react";
import { L1_LANGUAGE_PROFILES } from "@/lib/ielts-utils";
import type { NativeLanguageCode } from "@/lib/types";

const BAND_OPTIONS = [5.0, 5.5, 6.0, 6.5, 7.0, 7.5, 8.0, 8.5, 9.0];

const selectClass =
  "w-full px-3 py-2.5 rounded-lg bg-canvas border border-line-strong text-ink focus:outline-none focus:border-accent";
const labelClass = "block text-sm font-medium text-ink mb-1.5";

export function WelcomeClient({ firstName }: { firstName: string }) {
  const router = useRouter();
  const [currentBand, setCurrentBand] = useState(6.5);
  const [targetBand, setTargetBand] = useState(7.5);
  const [examDate, setExamDate] = useState("");
  const [nativeLanguage, setNativeLanguage] = useState<NativeLanguageCode>("es");
  const [l1CoachingEnabled, setL1CoachingEnabled] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save(skip: boolean) {
    setBusy(true);
    setError(null);
    try {
      const res = await fetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          skip
            ? { completeOnboarding: true }
            : {
                completeOnboarding: true,
                currentBand,
                targetBand,
                examDate: examDate || null,
                nativeLanguage,
                l1CoachingEnabled,
              }
        ),
      });
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        setError(data.error || "Could not save. Please try again.");
        setBusy(false);
        return;
      }
      router.push("/");
      router.refresh();
    } catch {
      setError("Network error. Please try again.");
      setBusy(false);
    }
  }

  return (
    <main className="min-h-screen flex items-center justify-center px-4 py-12 bg-canvas">
      <form
        id="welcome-form"
        onSubmit={(e) => {
          e.preventDefault();
          save(false);
        }}
        className="w-full max-w-lg editorial-surface rounded-2xl p-8 sm:p-10 space-y-6"
      >
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-accent text-accent-fg flex items-center justify-center">
            <Feather className="w-5 h-5" />
          </div>
          <div>
            <p className="text-xs font-mono uppercase tracking-widest text-ink-muted">
              Welcome{firstName ? `, ${firstName}` : ""}
            </p>
            <h1 className="font-serif text-2xl font-semibold tracking-tight text-ink">
              Let&apos;s calibrate your coach
            </h1>
          </div>
        </div>

        <p className="text-sm text-ink-secondary leading-relaxed">
          Your answers set how strictly essays are marked, which drills you get, and which
          first-language habits the coach watches for. You can change them any time in Settings.
        </p>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label htmlFor="welcome-current-band" className={labelClass}>
              Current writing band
            </label>
            <select
              id="welcome-current-band"
              value={currentBand}
              onChange={(e) => setCurrentBand(Number(e.target.value))}
              className={selectClass}
            >
              {BAND_OPTIONS.map((b) => (
                <option key={b} value={b}>
                  Band {b.toFixed(1)}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor="welcome-target-band" className={labelClass}>
              Target band
            </label>
            <select
              id="welcome-target-band"
              value={targetBand}
              onChange={(e) => setTargetBand(Number(e.target.value))}
              className={selectClass}
            >
              {BAND_OPTIONS.map((b) => (
                <option key={b} value={b}>
                  Band {b.toFixed(1)}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div>
          <label htmlFor="welcome-exam-date" className={labelClass}>
            Exam date <span className="text-ink-muted font-normal">(optional)</span>
          </label>
          <input
            id="welcome-exam-date"
            type="date"
            value={examDate}
            onChange={(e) => setExamDate(e.target.value)}
            className={selectClass}
          />
        </div>

        <div>
          <label htmlFor="welcome-native-language" className={labelClass}>
            First language
          </label>
          <select
            id="welcome-native-language"
            value={nativeLanguage}
            onChange={(e) => setNativeLanguage(e.target.value as NativeLanguageCode)}
            className={selectClass}
          >
            {Object.entries(L1_LANGUAGE_PROFILES).map(([code, meta]) => (
              <option key={code} value={code}>
                {meta.label}
              </option>
            ))}
          </select>
          <label className="mt-3 flex items-start gap-2.5 text-sm text-ink-secondary">
            <input
              id="welcome-l1-coaching"
              type="checkbox"
              checked={l1CoachingEnabled}
              onChange={(e) => setL1CoachingEnabled(e.target.checked)}
              className="mt-0.5"
            />
            Flag mistakes typical of speakers of my first language
          </label>
        </div>

        {error && (
          <p role="alert" className="text-sm" style={{ color: "var(--status-danger)" }}>
            {error}
          </p>
        )}

        <div className="flex flex-col-reverse sm:flex-row gap-3 sm:justify-between sm:items-center">
          <button
            id="welcome-skip-button"
            type="button"
            onClick={() => save(true)}
            disabled={busy}
            className="text-sm text-ink-secondary hover:text-ink cursor-pointer disabled:opacity-50"
          >
            Skip for now
          </button>
          <button
            id="welcome-submit-button"
            type="submit"
            disabled={busy}
            className="flex items-center justify-center gap-2 py-3 px-5 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg font-medium text-sm transition disabled:opacity-50 cursor-pointer"
          >
            {busy ? "Saving..." : "Start writing"}
            <ArrowRight className="w-4 h-4" />
          </button>
        </div>
      </form>
    </main>
  );
}
