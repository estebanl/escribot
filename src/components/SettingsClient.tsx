"use client";

import { useEffect, useState } from "react";
import {
  Check,
  Download,
  KeyRound,
  Loader2,
  Save,
  ShieldCheck,
  Upload,
} from "lucide-react";
import { L1_LANGUAGE_PROFILES } from "@/lib/ielts-utils";
import type { AIProviderId, NativeLanguageCode } from "@/lib/types";

const BAND_OPTIONS = [5.0, 5.5, 6.0, 6.5, 7.0, 7.5, 8.0, 8.5, 9.0];

interface ProviderStatusMap {
  [key: string]: {
    hasStoredKey: boolean;
    maskedStoredKey: string | null;
    hasEnvKey: boolean;
  };
}

export function SettingsClient() {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [savedBanner, setSavedBanner] = useState(false);

  const [currentBand, setCurrentBand] = useState(6.5);
  const [targetBand, setTargetBand] = useState(7.5);
  const [examDate, setExamDate] = useState("");
  const [nativeLanguage, setNativeLanguage] = useState<NativeLanguageCode>("es");
  const [l1CoachingEnabled, setL1CoachingEnabled] = useState(true);

  const [activeProvider, setActiveProvider] = useState<AIProviderId>("deepseek");
  const [graderModel, setGraderModel] = useState("deepseek-v4-pro");
  const [fastModel, setFastModel] = useState("deepseek-flash");

  const [providerDefaults, setProviderDefaults] = useState<
    Record<AIProviderId, { label: string; graderModel: string; fastModel: string }>
  >({
    deepseek: {
      label: "DeepSeek (V4 Pro + V4.1 Flash Vision)",
      graderModel: "deepseek-v4-pro",
      fastModel: "deepseek-flash",
    },
    gemini: {
      label: "Google Gemini (2.5 Pro + 2.5 Flash)",
      graderModel: "gemini-2.5-pro",
      fastModel: "gemini-2.5-flash",
    },
    openai: {
      label: "OpenAI (GPT-4.1 + GPT-4.1 Mini)",
      graderModel: "gpt-4.1",
      fastModel: "gpt-4.1-mini",
    },
    anthropic: {
      label: "Anthropic Claude (Sonnet 4.5 + Haiku 4.5)",
      graderModel: "claude-sonnet-4-5",
      fastModel: "claude-haiku-4-5",
    },
  });

  const [keyStatus, setKeyStatus] = useState<ProviderStatusMap>({});
  const [newKeyInputs, setNewKeyInputs] = useState<Record<AIProviderId, string>>({
    deepseek: "",
    gemini: "",
    openai: "",
    anthropic: "",
  });

  const [backupMessage, setBackupMessage] = useState<string | null>(null);

  async function fetchSettings() {
    try {
      const res = await fetch("/api/settings");
      const data = await res.json();
      if (res.ok && data.settings) {
        setCurrentBand(data.settings.currentBand);
        setTargetBand(data.settings.targetBand);
        setExamDate(data.settings.examDate || "");
        setNativeLanguage(data.settings.nativeLanguage);
        setL1CoachingEnabled(data.settings.l1CoachingEnabled);
        setActiveProvider(data.settings.activeProvider);
        setGraderModel(data.settings.graderModel);
        setFastModel(data.settings.fastModel);
        setKeyStatus(data.keyStatus || {});
        if (data.providerDefaults) setProviderDefaults(data.providerDefaults);
      }
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    let active = true;
    fetch("/api/settings")
      .then((res) => res.json())
      .then((data) => {
        if (!active || !data?.settings) return;
        setCurrentBand(data.settings.currentBand);
        setTargetBand(data.settings.targetBand);
        setExamDate(data.settings.examDate || "");
        setNativeLanguage(data.settings.nativeLanguage);
        setL1CoachingEnabled(data.settings.l1CoachingEnabled);
        setActiveProvider(data.settings.activeProvider);
        setGraderModel(data.settings.graderModel);
        setFastModel(data.settings.fastModel);
        setKeyStatus(data.keyStatus || {});
        if (data.providerDefaults) setProviderDefaults(data.providerDefaults);
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, []);

  function handleProviderSwitch(nextProv: AIProviderId) {
    setActiveProvider(nextProv);
    const defs = providerDefaults[nextProv];
    if (defs) {
      setGraderModel(defs.graderModel);
      setFastModel(defs.fastModel);
    }
  }

  async function handleSaveSettings(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    setSavedBanner(false);

    const apiKeysToUpdate: Partial<Record<AIProviderId, string>> = {};
    for (const p of ["deepseek", "gemini", "openai", "anthropic"] as AIProviderId[]) {
      if (newKeyInputs[p].trim()) {
        apiKeysToUpdate[p] = newKeyInputs[p].trim();
      }
    }

    await fetch("/api/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        currentBand,
        targetBand,
        examDate: examDate || null,
        nativeLanguage,
        l1CoachingEnabled,
        activeProvider,
        graderModel,
        fastModel,
        apiKeysToUpdate,
      }),
    });

    setNewKeyInputs({ deepseek: "", gemini: "", openai: "", anthropic: "" });
    await fetchSettings();
    setSaving(false);
    setSavedBanner(true);
    setTimeout(() => setSavedBanner(false), 3000);
  }

  async function handleClearKey(prov: AIProviderId) {
    await fetch("/api/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ apiKeysToUpdate: { [prov]: "__CLEAR__" } }),
    });
    await fetchSettings();
  }

  async function handleExportBackup() {
    const res = await fetch("/api/backup");
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `fluentescritor-backup-${new Date().toISOString().slice(0, 10)}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  async function handleImportBackup(file: File | undefined) {
    if (!file) return;
    setBackupMessage(null);
    try {
      const text = await file.text();
      const parsed = JSON.parse(text);
      const res = await fetch("/api/backup", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(parsed),
      });
      const data = await res.json();
      if (res.ok) {
        setBackupMessage(`Imported ${data.importedCount} study records cleanly.`);
      } else {
        setBackupMessage(data.error || "Import failed.");
      }
    } catch {
      setBackupMessage("Invalid backup JSON file.");
    }
  }

  if (loading) {
    return (
      <div className="max-w-3xl mx-auto px-4 py-16 flex items-center justify-center">
        <Loader2 className="w-6 h-6 animate-spin text-accent" />
      </div>
    );
  }

  return (
    <div className="max-w-3xl mx-auto px-4 sm:px-6 pt-7 space-y-7">
      <header className="space-y-1.5 pb-5 border-b border-line">
        <span className="text-xs font-mono uppercase tracking-wider text-accent font-semibold">
          Workspace Calibration &amp; Cloud Sync
        </span>
        <h1 className="font-serif text-3xl font-semibold text-ink">
          Settings &amp; AI Engine
        </h1>
        <p className="text-sm text-ink-secondary">
          Tune your target IELTS band, L1 interference coaching lens, and DeepSeek /
          multi-provider routing.
        </p>
      </header>

      <form onSubmit={handleSaveSettings} className="space-y-6">
        {/* 1. Target Band & L1 Calibration */}
        <section className="editorial-surface rounded-2xl p-6 space-y-4">
          <h2 className="font-serif text-lg font-semibold text-ink">
            1. Band Calibration &amp; Linguistic Lens
          </h2>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Current Baseline Band
              </label>
              <select
                value={currentBand}
                onChange={(e) => setCurrentBand(Number(e.target.value))}
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink font-mono"
              >
                {BAND_OPTIONS.map((b) => (
                  <option key={b} value={b}>
                    Band {b.toFixed(1)}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Target IELTS Band
              </label>
              <select
                value={targetBand}
                onChange={(e) => setTargetBand(Number(e.target.value))}
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink font-mono"
              >
                {BAND_OPTIONS.map((b) => (
                  <option key={b} value={b}>
                    Band {b.toFixed(1)}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Target Exam Date (Optional)
              </label>
              <input
                type="date"
                value={examDate}
                onChange={(e) => setExamDate(e.target.value)}
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink font-mono"
              />
            </div>
          </div>

          <div className="pt-2 space-y-3">
            <div className="flex items-center justify-between">
              <div>
                <span className="block text-sm font-medium text-ink">
                  Native-Language (L1) Interference Coaching
                </span>
                <span className="text-xs text-ink-secondary">
                  Flags false cognates, comma splices, and article/preposition calques
                  during grading and drills.
                </span>
              </div>
              <input
                type="checkbox"
                checked={l1CoachingEnabled}
                onChange={(e) => setL1CoachingEnabled(e.target.checked)}
                className="w-4 h-4 accent-teal-700 cursor-pointer"
              />
            </div>

            {l1CoachingEnabled && (
              <div>
                <label className="block text-xs font-medium text-ink-secondary mb-1">
                  Native Language Profile
                </label>
                <select
                  value={nativeLanguage}
                  onChange={(e) =>
                    setNativeLanguage(e.target.value as NativeLanguageCode)
                  }
                  className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
                >
                  {Object.entries(L1_LANGUAGE_PROFILES).map(([code, meta]) => (
                    <option key={code} value={code}>
                      {meta.label}
                    </option>
                  ))}
                </select>
                <p className="text-xs text-ink-muted mt-1.5 leading-relaxed">
                  {L1_LANGUAGE_PROFILES[nativeLanguage]?.coachingFocus}
                </p>
              </div>
            )}
          </div>
        </section>

        {/* 2. Multi-Provider AI Routing (DeepSeek Hybrid Default) */}
        <section className="editorial-surface rounded-2xl p-6 space-y-4">
          <div className="flex items-start justify-between gap-2">
            <div>
              <h2 className="font-serif text-lg font-semibold text-ink">
                2. Multi-Provider AI Engine (DeepSeek Default)
              </h2>
              <p className="text-xs text-ink-secondary mt-0.5">
                Keys entered below are encrypted with <code className="font-mono">AES-256-GCM</code> in Turso, or read automatically from your Vercel environment variables.
              </p>
            </div>
            <ShieldCheck className="w-5 h-5 text-accent shrink-0" />
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Active Provider
              </label>
              <select
                id="active-provider-select"
                value={activeProvider}
                onChange={(e) =>
                  handleProviderSwitch(e.target.value as AIProviderId)
                }
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
              >
                <option value="deepseek">DeepSeek (Recommended)</option>
                <option value="gemini">Google Gemini</option>
                <option value="openai">OpenAI</option>
                <option value="anthropic">Anthropic Claude</option>
              </select>
            </div>

            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Deep Rubric Examiner Model
              </label>
              <input
                type="text"
                value={graderModel}
                onChange={(e) => setGraderModel(e.target.value)}
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm font-mono text-ink"
              />
            </div>

            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Vision &amp; Fast Coach Model
              </label>
              <input
                type="text"
                value={fastModel}
                onChange={(e) => setFastModel(e.target.value)}
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm font-mono text-ink"
              />
            </div>
          </div>

          <div className="space-y-3 pt-2">
            {(["deepseek", "gemini", "openai", "anthropic"] as AIProviderId[]).map(
              (prov) => {
                const st = keyStatus[prov];
                return (
                  <div
                    key={prov}
                    className="p-3.5 rounded-xl bg-canvas border border-line space-y-2"
                  >
                    <div className="flex flex-wrap items-center justify-between gap-2 text-xs">
                      <span className="font-semibold uppercase font-mono text-ink flex items-center gap-1.5">
                        <KeyRound className="w-3.5 h-3.5 text-accent" />
                        {providerDefaults[prov]?.label || prov}
                      </span>

                      <div className="flex items-center gap-2">
                        {st?.hasEnvKey && (
                          <span className="px-2 py-0.5 rounded bg-accent-soft text-accent font-mono text-[11px]">
                            Env Key Active
                          </span>
                        )}
                        {st?.hasStoredKey && (
                          <span className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded bg-subtle text-ink font-mono text-[11px]">
                            <span>Saved: {st.maskedStoredKey}</span>
                            <button
                              type="button"
                              onClick={() => handleClearKey(prov)}
                              className="text-red-500 hover:underline ml-1 cursor-pointer"
                            >
                              Clear
                            </button>
                          </span>
                        )}
                      </div>
                    </div>

                    <input
                      id={`apikey-input-${prov}`}
                      type="password"
                      value={newKeyInputs[prov]}
                      onChange={(e) =>
                        setNewKeyInputs((prev) => ({
                          ...prev,
                          [prov]: e.target.value,
                        }))
                      }
                      placeholder={
                        st?.hasStoredKey || st?.hasEnvKey
                          ? "Leave blank to keep existing key, or paste new key to override..."
                          : `Paste ${prov.toUpperCase()}_API_KEY...`
                      }
                      className="w-full px-3 py-2 rounded-lg bg-surface border border-line text-xs font-mono text-ink"
                    />
                  </div>
                );
              }
            )}
          </div>
        </section>

        <div className="flex items-center justify-end gap-3">
          {savedBanner && (
            <span className="inline-flex items-center gap-1 text-xs font-semibold text-accent">
              <Check className="w-4 h-4" /> Settings saved
            </span>
          )}
          <button
            id="save-settings-btn"
            type="submit"
            disabled={saving}
            className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl bg-accent hover:bg-accent-hover text-accent-fg text-sm font-semibold cursor-pointer disabled:opacity-50"
          >
            {saving ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <Save className="w-4 h-4" />
            )}
            <span>Save Workspace Settings</span>
          </button>
        </div>
      </form>

      {/* 3. Portable JSON Backup Export / Import */}
      <section className="editorial-surface rounded-2xl p-6 space-y-3">
        <h2 className="font-serif text-lg font-semibold text-ink">
          3. Portable Study Archive (JSON Export &amp; Import)
        </h2>
        <p className="text-xs text-ink-secondary">
          Download a complete snapshot of your custom prompts, uploaded chart images,
          essay drafts, examiner evaluations, and Vault flashcards anytime.
        </p>

        <div className="flex flex-wrap items-center gap-3 pt-1">
          <button
            id="export-json-btn"
            type="button"
            onClick={handleExportBackup}
            className="inline-flex items-center gap-2 px-4 py-2 rounded-xl border border-line-strong bg-canvas hover:bg-subtle text-xs font-semibold text-ink cursor-pointer"
          >
            <Download className="w-4 h-4 text-accent" />
            <span>Export JSON Snapshot</span>
          </button>

          <label className="inline-flex items-center gap-2 px-4 py-2 rounded-xl border border-line-strong bg-canvas hover:bg-subtle text-xs font-semibold text-ink cursor-pointer">
            <Upload className="w-4 h-4 text-ink-secondary" />
            <span>Import JSON Snapshot</span>
            <input
              type="file"
              accept="application/json"
              className="hidden"
              onChange={(e) => handleImportBackup(e.target.files?.[0])}
            />
          </label>
        </div>

        {backupMessage && (
          <p className="text-xs font-medium text-accent pt-1">{backupMessage}</p>
        )}
      </section>
    </div>
  );
}
