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
import { PLANS } from "@/lib/plans";
import type { AIProviderId, NativeLanguageCode } from "@/lib/types";

const BAND_OPTIONS = [5.0, 5.5, 6.0, 6.5, 7.0, 7.5, 8.0, 8.5, 9.0];

interface ProviderStatusMap {
  [key: string]: {
    hasStoredKey: boolean;
    maskedStoredKey: string | null;
    hasEnvKey: boolean;
  };
}

interface UsageSummary {
  planLabel: string;
  keySource: "platform" | "user" | "none";
  canChooseModels: boolean;
  evaluations: { used: number; limit: number | null };
  assists: { used: number; limit: number | null };
  verificationRequired: boolean;
  resetsAt: string;
}

function UsageMeter({
  label,
  used,
  limit,
}: {
  label: string;
  used: number;
  limit: number | null;
}) {
  const exhausted = limit !== null && used >= limit;
  const percent = limit ? Math.min(100, Math.round((used / limit) * 100)) : 0;
  return (
    <div>
      <div className="flex items-baseline justify-between gap-3 text-sm">
        <span className="text-ink-secondary">{label}</span>
        <span className="font-mono text-ink whitespace-nowrap shrink-0">
          {limit === null
            ? `${used.toLocaleString()} · unlimited`
            : `${used.toLocaleString()} / ${limit.toLocaleString()}`}
        </span>
      </div>
      {limit !== null && (
        <div
          className="mt-1.5 h-1.5 rounded-full bg-subtle overflow-hidden"
          role="progressbar"
          aria-label={label}
          aria-valuemin={0}
          aria-valuemax={limit}
          aria-valuenow={Math.min(used, limit)}
        >
          <div
            className="h-full rounded-full"
            style={{
              width: `${percent}%`,
              backgroundColor: exhausted ? "var(--status-danger)" : "var(--accent-primary)",
            }}
          />
        </div>
      )}
    </div>
  );
}

interface BillingStatus {
  enabled: boolean;
  hasSubscription: boolean;
  status: string | null;
  currentPeriodEnd: string | null;
  cancelAtPeriodEnd: boolean;
}

function formatDate(iso: string) {
  return new Date(iso).toLocaleDateString(undefined, {
    year: "numeric",
    month: "long",
    day: "numeric",
  });
}

function describeSubscription(b: BillingStatus): string | null {
  if (!b.hasSubscription || !b.status) return null;
  const end = b.currentPeriodEnd ? formatDate(b.currentPeriodEnd) : null;
  switch (b.status) {
    case "active":
    case "trialing":
      if (b.cancelAtPeriodEnd) {
        return end ? `Pro ends on ${end}. You can resume it any time before then.` : "Pro is set to cancel.";
      }
      return end ? `Pro renews on ${end}.` : "Pro is active.";
    case "past_due":
      return "Your last payment failed. Update your card in Manage billing to keep Pro.";
    case "canceled":
      return "Your Pro subscription has ended.";
    default:
      return "Your Pro subscription is not active. Open Manage billing for details.";
  }
}

export function SettingsClient({
  billingReturn = null,
}: {
  billingReturn?: "success" | null;
}) {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [savedBanner, setSavedBanner] = useState(false);

  const [currentBand, setCurrentBand] = useState(6.5);
  const [targetBand, setTargetBand] = useState(7.5);
  const [examDate, setExamDate] = useState("");
  const [nativeLanguage, setNativeLanguage] = useState<NativeLanguageCode>("es");
  const [l1CoachingEnabled, setL1CoachingEnabled] = useState(true);

  const [activeProvider, setActiveProvider] = useState<AIProviderId>("openrouter");
  const [graderModel, setGraderModel] = useState("anthropic/claude-sonnet-5.5");
  const [fastModel, setFastModel] = useState("deepseek/deepseek-v4.1-flash-1017");
  const [visionModel, setVisionModel] = useState("deepseek/deepseek-v4.1-flash-1017");
  const [openRouterModels, setOpenRouterModels] = useState<
    Array<{ id: string; name: string; hasVision: boolean }>
  >([]);

  const [providerDefaults, setProviderDefaults] = useState<
    Record<
      AIProviderId,
      { label: string; graderModel: string; fastModel: string; visionModel?: string }
    >
  >({
    openrouter: {
      label: "OpenRouter (Sonnet 5.5 + DS Flash Vision)",
      graderModel: "anthropic/claude-sonnet-5.5",
      fastModel: "deepseek/deepseek-v4.1-flash-1017",
      visionModel: "deepseek/deepseek-v4.1-flash-1017",
    },
    deepseek: {
      label: "DeepSeek (V4 Pro + V4.1 Flash Vision)",
      graderModel: "deepseek-v4-pro",
      fastModel: "deepseek-flash",
      visionModel: "deepseek-flash",
    },
    gemini: {
      label: "Google Gemini (2.5 Pro + 2.5 Flash)",
      graderModel: "gemini-2.5-pro",
      fastModel: "gemini-2.5-flash",
      visionModel: "gemini-2.5-flash",
    },
    openai: {
      label: "OpenAI (GPT-4.1 + GPT-4.1 Mini)",
      graderModel: "gpt-4.1",
      fastModel: "gpt-4.1-mini",
      visionModel: "gpt-4.1",
    },
    anthropic: {
      label: "Anthropic Claude (Sonnet 4.5 + Haiku 4.5)",
      graderModel: "claude-sonnet-4-5",
      fastModel: "claude-haiku-4-5",
      visionModel: "claude-sonnet-4-5",
    },
  });

  const [keyStatus, setKeyStatus] = useState<ProviderStatusMap>({});
  const [newKeyInputs, setNewKeyInputs] = useState<Record<AIProviderId, string>>({
    openrouter: "",
    deepseek: "",
    gemini: "",
    openai: "",
    anthropic: "",
  });

  const [backupMessage, setBackupMessage] = useState<string | null>(null);
  const [usage, setUsage] = useState<UsageSummary | null>(null);
  const [billing, setBilling] = useState<BillingStatus | null>(null);
  const [billingBusy, setBillingBusy] = useState(false);
  const [billingError, setBillingError] = useState<string | null>(null);

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
        if (data.settings.visionModel) setVisionModel(data.settings.visionModel);
        setKeyStatus(data.keyStatus || {});
        if (data.providerDefaults) setProviderDefaults(data.providerDefaults);
        if (data.usage) setUsage(data.usage);
        if (data.billing) setBilling(data.billing);
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
        if (data.settings.visionModel) setVisionModel(data.settings.visionModel);
        setKeyStatus(data.keyStatus || {});
        if (data.providerDefaults) setProviderDefaults(data.providerDefaults);
        if (data.usage) setUsage(data.usage);
        if (data.billing) setBilling(data.billing);
      })
      .finally(() => {
        if (active) setLoading(false);
      });

    // Prefetch OpenRouter models list for datalist autocomplete
    fetch("/api/ai/models")
      .then((res) => res.json())
      .then((data) => {
        if (!active || !Array.isArray(data?.models)) return;
        setOpenRouterModels(data.models);
      })
      .catch(() => {});

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
      if (defs.visionModel) setVisionModel(defs.visionModel);
    }
  }

  async function handleSaveSettings(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    setSavedBanner(false);

    const apiKeysToUpdate: Partial<Record<AIProviderId, string>> = {};
    for (const p of [
      "openrouter",
      "deepseek",
      "gemini",
      "openai",
      "anthropic",
    ] as AIProviderId[]) {
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
        visionModel,
        apiKeysToUpdate,
      }),
    });

    setNewKeyInputs({
      openrouter: "",
      deepseek: "",
      gemini: "",
      openai: "",
      anthropic: "",
    });
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

  async function handleManageBilling() {
    setBillingBusy(true);
    setBillingError(null);
    try {
      const res = await fetch("/api/billing/portal", { method: "POST" });
      const data = await res.json();
      if (!res.ok || !data.url) {
        setBillingError(data.error || "Could not open billing management.");
        setBillingBusy(false);
        return;
      }
      window.location.href = data.url;
    } catch {
      setBillingError("Network error. Please try again.");
      setBillingBusy(false);
    }
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

      {usage && (
        <section id="plan-usage-card" className="editorial-surface rounded-2xl p-6 space-y-4">
          <div className="flex items-baseline justify-between gap-2">
            <h2 className="font-serif text-lg font-semibold text-ink">Plan &amp; AI Usage</h2>
            <span className="text-xs font-mono uppercase tracking-wider text-accent font-semibold">
              {usage.planLabel}
            </span>
          </div>

          {usage.keySource === "user" ? (
            <p className="text-sm text-ink-secondary">
              AI requests run on your own API key, so no monthly limits apply. The counts
              below only apply if you remove your key.
            </p>
          ) : usage.keySource === "none" ? (
            <p className="text-sm text-ink-secondary">
              No AI key is available yet. Add your own key below to enable examiner
              feedback.
            </p>
          ) : usage.evaluations.limit === null && usage.assists.limit === null ? (
            <p className="text-sm text-ink-secondary">
              No monthly limits apply to this account.
            </p>
          ) : (
            <p className="text-sm text-ink-secondary">
              Included AI usage this month. Resets on{" "}
              {new Date(usage.resetsAt).toLocaleDateString(undefined, {
                month: "long",
                day: "numeric",
                timeZone: "UTC",
              })}
              . Add your own API key below to remove these limits.
            </p>
          )}

          {usage.verificationRequired && (
            <p
              id="verification-required-note"
              role="status"
              className="text-sm p-3 rounded-lg border border-line-strong bg-subtle text-ink"
            >
              Verify your email address to use AI feedback on the Free plan. Upgrading to
              Pro or adding your own API key also works.
            </p>
          )}

          {billingReturn === "success" && (
            <p
              id="billing-success-banner"
              role="status"
              className="text-sm p-3 rounded-lg border"
              style={{
                backgroundColor: "var(--status-success-soft)",
                color: "var(--status-success)",
                borderColor: "var(--status-success)",
              }}
            >
              {usage.planLabel === "Pro"
                ? "Thanks for upgrading. Pro is now active."
                : "Thanks for upgrading. Pro activates as soon as Stripe confirms your payment, usually within a few seconds. Refresh this page if it has not updated."}
            </p>
          )}

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-5">
            <UsageMeter
              label="Graded essays"
              used={usage.evaluations.used}
              limit={usage.evaluations.limit}
            />
            <UsageMeter
              label="Coaching requests (outlines, toolkit, drills)"
              used={usage.assists.used}
              limit={usage.assists.limit}
            />
          </div>

          {billing?.enabled && usage.planLabel !== "Owner" && (
            <div className="pt-4 border-t border-line flex flex-col sm:flex-row sm:items-center justify-between gap-3">
              <p id="subscription-status" className="text-sm text-ink-secondary">
                {describeSubscription(billing) ??
                  `Upgrade to Pro for ${PLANS.pro.evaluationsPerMonth} graded essays a month and your choice of examiner model.`}
              </p>
              {billing.hasSubscription ? (
                <button
                  id="manage-billing-button"
                  type="button"
                  onClick={handleManageBilling}
                  disabled={billingBusy}
                  className="shrink-0 px-4 py-2 rounded-lg border border-line-strong bg-canvas hover:bg-subtle text-ink text-sm font-medium transition disabled:opacity-50 cursor-pointer"
                >
                  {billingBusy ? "Opening..." : "Manage billing"}
                </button>
              ) : (
                <a
                  id="upgrade-to-pro-button"
                  href="/pricing"
                  className="shrink-0 px-4 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-sm font-medium transition text-center"
                >
                  See Pro plan
                </a>
              )}
            </div>
          )}
          {billingError && (
            <p className="text-sm" style={{ color: "var(--status-danger)" }}>
              {billingError}
            </p>
          )}
        </section>
      )}

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

        {/* 2. Multi-Provider AI Routing (OpenRouter Unified Default) */}
        <section className="editorial-surface rounded-2xl p-6 space-y-4">
          <div className="flex items-start justify-between gap-2">
            <div>
              <h2 className="font-serif text-lg font-semibold text-ink">
                2. Multi-Provider AI Engine (OpenRouter Default)
              </h2>
              <p className="text-xs text-ink-secondary mt-0.5">
                Keys entered below are encrypted with <code className="font-mono">AES-256-GCM</code>. Requests made with your own key do not count toward your plan&apos;s monthly limits.
              </p>
              {usage && usage.keySource !== "user" && !usage.canChooseModels && (
                <p id="model-choice-locked-note" className="text-xs text-ink-secondary mt-1.5">
                  On the {usage.planLabel} plan the examiner uses the standard models. The
                  provider and model choices below take effect once you add your own key.
                </p>
              )}
            </div>
            <ShieldCheck className="w-5 h-5 text-accent shrink-0" />
          </div>

          <datalist id="openrouter-models">
            {openRouterModels.map((m) => (
              <option key={m.id} value={m.id}>
                {m.name}
              </option>
            ))}
          </datalist>
          <datalist id="openrouter-vision-models">
            {openRouterModels
              .filter((m) => m.hasVision)
              .map((m) => (
                <option key={m.id} value={m.id}>
                  {m.name}
                </option>
              ))}
          </datalist>

          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
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
                <option value="openrouter">OpenRouter (Recommended Gateway)</option>
                <option value="deepseek">DeepSeek Direct</option>
                <option value="gemini">Google Gemini Direct</option>
                <option value="openai">OpenAI Direct</option>
                <option value="anthropic">Anthropic Claude Direct</option>
              </select>
            </div>

            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Deep Rubric Examiner Model
              </label>
              <input
                type="text"
                list={activeProvider === "openrouter" ? "openrouter-models" : undefined}
                value={graderModel}
                onChange={(e) => setGraderModel(e.target.value)}
                placeholder="e.g. anthropic/claude-sonnet-5.5"
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm font-mono text-ink"
              />
            </div>

            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1">
                Fast Coach Model
              </label>
              <input
                type="text"
                list={activeProvider === "openrouter" ? "openrouter-models" : undefined}
                value={fastModel}
                onChange={(e) => setFastModel(e.target.value)}
                placeholder="e.g. deepseek/deepseek-v4.1-flash-1017"
                className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm font-mono text-ink"
              />
            </div>

            {activeProvider === "openrouter" && (
              <div className="sm:col-span-2 lg:col-span-3">
                <label className="block text-xs font-medium text-ink-secondary mb-1">
                  Task 1 Screenshot Vision Model
                </label>
                <input
                  type="text"
                  list="openrouter-vision-models"
                  value={visionModel}
                  onChange={(e) => setVisionModel(e.target.value)}
                  placeholder="e.g. deepseek/deepseek-v4.1-flash-1017"
                  className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm font-mono text-ink"
                />
                <p className="text-[11px] text-ink-muted mt-1">
                  Model used to extract data tables and trends from pasted Task 1 chart screenshots.
                </p>
              </div>
            )}
          </div>

          <div className="space-y-3 pt-2">
            {(
              [
                "openrouter",
                "deepseek",
                "gemini",
                "openai",
                "anthropic",
              ] as AIProviderId[]
            ).map((prov) => {
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
            })}
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
