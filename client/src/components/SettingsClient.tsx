import { useEffect, useState } from "react";
import {
  Check,
  Download,
  Loader2,
  Save,
  SlidersHorizontal,
  Upload,
} from "lucide-react";
import { ModelProvidersDialog } from "@/components/ModelProvidersDialog";
import { ProviderIcon } from "@/components/ProviderIcon";
import { L1_LANGUAGE_PROFILES } from "@/lib/ielts-utils";
import { PLANS } from "@/lib/plans";
import type {
  AIProviderId,
  ModelRef,
  ModelRole,
  NativeLanguageCode,
  ProviderInfo,
} from "@/lib/types";

const BAND_OPTIONS = [5.0, 5.5, 6.0, 6.5, 7.0, 7.5, 8.0, 8.5, 9.0];

const ROLES: Array<{ role: ModelRole; label: string; help: string }> = [
  {
    role: "grader",
    label: "Deep rubric examiner",
    help: "Scores the four IELTS criteria. Pick your strongest model.",
  },
  {
    role: "fast",
    label: "Fast coach",
    help: "Outlines, toolkit, drills and inline upgrades. A quick, cheap model works well.",
  },
  {
    role: "vision",
    label: "Task 1 screenshot vision",
    help: "Reads data from pasted chart screenshots. Needs a model that accepts images.",
  },
];

const DEFAULT_MODELS: Record<ModelRole, ModelRef> = {
  grader: { provider: "openrouter", model: "anthropic/claude-sonnet-5.5" },
  fast: { provider: "openrouter", model: "deepseek/deepseek-v4.1-flash-1017" },
  vision: { provider: "openrouter", model: "deepseek/deepseek-v4.1-flash-1017" },
};

const refKey = (r: ModelRef) => `${r.provider}::${r.model}`;

function parseRefKey(key: string): ModelRef {
  const at = key.indexOf("::");
  return { provider: key.slice(0, at) as AIProviderId, model: key.slice(at + 2) };
}

/** One role's model, chosen from the models of every configured provider. */
function RoleSelect({
  role,
  label,
  help,
  value,
  providers,
  disabled,
  onChange,
}: {
  role: ModelRole;
  label: string;
  help: string;
  value: ModelRef;
  providers: ProviderInfo[];
  disabled: boolean;
  onChange: (value: ModelRef) => void;
}) {
  const id = `role-${role}-select`;
  const listed = providers.some((p) => p.id === value.provider && p.models.includes(value.model));
  const current = providers.find((p) => p.id === value.provider);
  return (
    <div>
      <label htmlFor={id} className="block text-xs font-medium text-ink-secondary mb-1">
        {label}
      </label>
      <select
        id={id}
        value={refKey(value)}
        disabled={disabled}
        onChange={(e) => onChange(parseRefKey(e.target.value))}
        className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm font-mono text-ink disabled:opacity-60"
      >
        {!listed && (
          <option value={refKey(value)}>
            {current?.name ?? value.provider} · {value.model}
          </option>
        )}
        {providers.map((p) => (
          <optgroup key={p.id} label={p.name}>
            {p.models.map((m) => (
              <option key={m} value={refKey({ provider: p.id, model: m })}>
                {m}
              </option>
            ))}
          </optgroup>
        ))}
      </select>
      <p className="text-[11px] text-ink-muted mt-1">{help}</p>
    </div>
  );
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

  const [providers, setProviders] = useState<ProviderInfo[]>([]);
  const [models, setModels] = useState<Record<ModelRole, ModelRef>>(DEFAULT_MODELS);
  const [providersOpen, setProvidersOpen] = useState(false);

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
        setModels(data.settings.models ?? DEFAULT_MODELS);
        setProviders(data.providers ?? []);
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
        setModels(data.settings.models ?? DEFAULT_MODELS);
        setProviders(data.providers ?? []);
        if (data.usage) setUsage(data.usage);
        if (data.billing) setBilling(data.billing);
      })
      .finally(() => {
        if (active) setLoading(false);
      });

    return () => {
      active = false;
    };
  }, []);

  /**
   * After the providers dialog saves a change: reload providers, usage and roles (removing
   * a provider can move a role to another one) without touching the rest of the form.
   */
  async function refreshProviders() {
    const res = await fetch("/api/settings");
    if (!res.ok) return;
    const data = await res.json();
    const fresh: ProviderInfo[] = data.providers ?? [];
    setProviders(fresh);
    const saved: Record<ModelRole, ModelRef> | undefined = data.settings?.models;
    if (saved) {
      // Keep unsaved role choices that are still on a usable provider's list; take the
      // saved choice for the rest (for example a role moved off a removed provider).
      const stillListed = (r: ModelRef) =>
        fresh.some((p) => p.configured && p.id === r.provider && p.models.includes(r.model));
      setModels((local) => ({
        grader: stillListed(local.grader) ? local.grader : saved.grader,
        fast: stillListed(local.fast) ? local.fast : saved.fast,
        vision: stillListed(local.vision) ? local.vision : saved.vision,
      }));
    }
    if (data.usage) setUsage(data.usage);
  }

  async function handleSaveSettings(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    setSavedBanner(false);

    await fetch("/api/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        currentBand,
        targetBand,
        examDate: examDate || null,
        nativeLanguage,
        l1CoachingEnabled,
        models,
      }),
    });

    await fetchSettings();
    setSaving(false);
    setSavedBanner(true);
    setTimeout(() => setSavedBanner(false), 3000);
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
    a.download = `escribot-backup-${new Date().toISOString().slice(0, 10)}.json`;
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

  const configuredProviders = providers.filter((p) => p.configured);

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
          Tune your target IELTS band, L1 interference coaching lens, and the AI models
          that grade and coach you.
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
              No AI key is available yet. Add a model provider below to enable examiner
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
              . Add a model provider with your own API key below to remove these limits.
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

        {/* 2. Model providers and the model each role uses */}
        <section id="ai-models-card" className="editorial-surface rounded-2xl p-6 space-y-5">
          <div className="flex flex-col sm:flex-row sm:items-start justify-between gap-3">
            <div>
              <h2 className="font-serif text-lg font-semibold text-ink">2. AI Models</h2>
              <p className="text-xs text-ink-secondary mt-0.5">
                Connect providers with your own API keys, then pick which model does each job.
                Requests made with your own key do not count toward your plan&apos;s monthly limits.
              </p>
              {usage && usage.keySource !== "user" && !usage.canChooseModels && (
                <p id="model-choice-locked-note" className="text-xs text-ink-secondary mt-1.5">
                  On the {usage.planLabel} plan the examiner uses the standard models. The
                  model choices below take effect once you add your own key.
                </p>
              )}
            </div>
            <button
              id="manage-providers-button"
              type="button"
              onClick={() => setProvidersOpen(true)}
              className="shrink-0 inline-flex items-center gap-2 px-3.5 py-2 rounded-lg border border-line-strong bg-canvas hover:bg-subtle text-sm font-medium text-ink cursor-pointer"
            >
              <SlidersHorizontal className="w-4 h-4 text-accent" />
              Manage providers
            </button>
          </div>

          {configuredProviders.length > 0 ? (
            <ul className="flex flex-wrap gap-2" aria-label="Configured providers">
              {configuredProviders.map((p) => (
                <li
                  key={p.id}
                  className="inline-flex items-center gap-2 pl-1.5 pr-3 py-1 rounded-full border border-line bg-canvas text-sm text-ink"
                >
                  <ProviderIcon id={p.id} size={20} />
                  {p.name}
                  <span className="text-xs text-ink-muted">
                    {p.hasStoredKey ? `${p.models.length} model${p.models.length === 1 ? "" : "s"}` : "plan key"}
                  </span>
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-sm text-ink-secondary p-3 rounded-lg border border-dashed border-line-strong">
              No providers are connected yet. Choose <span className="font-medium text-ink">Manage providers</span> to
              add one with your API key.
            </p>
          )}

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            {ROLES.map(({ role, label, help }) => (
              <RoleSelect
                key={role}
                role={role}
                label={label}
                help={help}
                value={models[role]}
                providers={configuredProviders}
                disabled={configuredProviders.length === 0}
                onChange={(value) => setModels((prev) => ({ ...prev, [role]: value }))}
              />
            ))}
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

      {providersOpen && (
        <ModelProvidersDialog
          providers={providers}
          onClose={() => setProvidersOpen(false)}
          onChanged={refreshProviders}
        />
      )}
    </div>
  );
}
