import { useEffect, useId, useMemo, useRef, useState } from "react";
import { Loader2, Plus, Search, Trash2, X } from "lucide-react";
import { ProviderIcon } from "@/components/ProviderIcon";
import { modelShapeError, removeProvider, saveProvider } from "@/lib/providers";
import type { AIProviderId, ProviderInfo } from "@/lib/types";

const MAX_MODELS = 20;

interface Props {
  providers: ProviderInfo[];
  /** OpenRouter's catalogue, for suggesting model names. */
  openRouterModels: Array<{ id: string; name: string }>;
  onClose: () => void;
  /** Called after any change is saved, so the page can reload providers and roles. */
  onChanged: () => Promise<void> | void;
}

/**
 * The "Model providers" dialog: the providers in use, each with its key and models, and
 * an "Add provider" picker for the rest. Every change is saved as soon as it is made.
 */
export function ModelProvidersDialog({ providers, openRouterModels, onClose, onChanged }: Props) {
  const titleId = useId();
  const [pickerOpen, setPickerOpen] = useState(false);
  const [settingUp, setSettingUp] = useState<AIProviderId | null>(null);
  const closeRef = useRef<HTMLButtonElement>(null);

  const configured = providers.filter((p) => p.configured);
  const available = providers.filter((p) => !p.configured && p.id !== settingUp);
  const setupProvider = providers.find((p) => p.id === settingUp) ?? null;

  // Focus moves into the dialog and back to whatever opened it.
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    closeRef.current?.focus();
    return () => opener?.focus();
  }, []);

  // Escape closes the picker first, then the dialog.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key !== "Escape" || e.defaultPrevented) return;
      if (pickerOpen) setPickerOpen(false);
      else onClose();
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [pickerOpen, onClose]);

  return (
    <div
      className="fixed inset-0 z-50 bg-black/60 backdrop-blur-xs flex items-start sm:items-center justify-center p-4 overflow-y-auto"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <datalist id="provider-models-openrouter">
        {openRouterModels.map((m) => (
          <option key={m.id} value={m.id}>
            {m.name}
          </option>
        ))}
      </datalist>

      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="w-full max-w-2xl editorial-surface rounded-2xl my-8"
      >
        <div className="flex items-center justify-between px-6 pt-5 pb-4">
          <h2 id={titleId} className="font-serif text-xl font-semibold text-ink">
            Model providers
          </h2>
          <button
            ref={closeRef}
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="p-1.5 rounded-lg hover:bg-subtle text-ink-muted hover:text-ink cursor-pointer"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        <div className="px-6 pb-6 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-start justify-between gap-3">
            <div>
              <h3 className="text-sm font-semibold text-ink">Configured providers</h3>
              <p className="text-sm text-ink-secondary mt-0.5">
                Use models from other providers with your own API keys. Keys are encrypted
                and only used to call that provider.
              </p>
            </div>
            <div className="relative shrink-0">
              <button
                id="add-provider-button"
                type="button"
                aria-haspopup="listbox"
                aria-expanded={pickerOpen}
                onClick={() => setPickerOpen((o) => !o)}
                className="inline-flex items-center gap-1.5 px-3.5 py-2 rounded-lg border border-line-strong bg-canvas hover:bg-subtle text-sm font-medium text-ink cursor-pointer"
              >
                <Plus className="w-4 h-4" />
                Add provider
              </button>
              {pickerOpen && (
                <ProviderPicker
                  providers={available}
                  onPick={(id) => {
                    setSettingUp(id);
                    setPickerOpen(false);
                  }}
                  onDismiss={() => setPickerOpen(false)}
                />
              )}
            </div>
          </div>

          {setupProvider && (
            <SetupCard
              key={setupProvider.id}
              provider={setupProvider}
              onCancel={() => setSettingUp(null)}
              onSaved={async () => {
                await onChanged();
                setSettingUp(null);
              }}
            />
          )}

          {configured.length === 0 && !setupProvider ? (
            <p className="text-sm text-ink-muted py-6 text-center border border-dashed border-line-strong rounded-xl">
              No providers yet. Choose <span className="font-medium text-ink">Add provider</span> to
              connect one with your API key.
            </p>
          ) : (
            <ul className="space-y-3">
              {configured.map((p) => (
                <li key={p.id}>
                  <ProviderCard provider={p} onChanged={onChanged} />
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </div>
  );
}

/* ---------- Add provider picker ---------- */

function ProviderPicker({
  providers,
  onPick,
  onDismiss,
}: {
  providers: ProviderInfo[];
  onPick: (id: AIProviderId) => void;
  onDismiss: () => void;
}) {
  const listId = useId();
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const rootRef = useRef<HTMLDivElement>(null);

  const matches = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q
      ? providers.filter((p) => `${p.name} ${p.description}`.toLowerCase().includes(q))
      : providers;
  }, [providers, query]);
  const activeIndex = Math.min(active, Math.max(matches.length - 1, 0));

  // A click anywhere else closes the picker.
  useEffect(() => {
    function onDown(e: MouseEvent) {
      if (!rootRef.current?.contains(e.target as Node)) onDismiss();
    }
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [onDismiss]);

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setActive((activeIndex + 1) % Math.max(matches.length, 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActive((activeIndex - 1 + matches.length) % Math.max(matches.length, 1));
    } else if (e.key === "Enter" && matches[activeIndex]) {
      e.preventDefault();
      onPick(matches[activeIndex].id);
    }
  }

  return (
    <div
      ref={rootRef}
      className="absolute right-0 top-full mt-2 z-10 w-[min(26rem,calc(100vw-4rem))] rounded-xl border border-line-strong bg-elevated shadow-xl overflow-hidden"
    >
      <div className="flex items-center gap-2 px-3 border-b border-line">
        <Search className="w-4 h-4 text-ink-muted shrink-0" />
        <input
          autoFocus
          type="text"
          role="combobox"
          aria-expanded="true"
          aria-controls={listId}
          aria-activedescendant={matches[activeIndex] ? `${listId}-${matches[activeIndex].id}` : undefined}
          aria-label="Search providers"
          placeholder="Search providers..."
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setActive(0);
          }}
          onKeyDown={onKeyDown}
          className="w-full py-2.5 bg-transparent text-sm text-ink placeholder:text-ink-muted outline-none"
        />
      </div>
      <ul id={listId} role="listbox" className="max-h-80 overflow-y-auto p-1.5">
        {matches.length === 0 && (
          <li className="px-3 py-4 text-sm text-ink-muted text-center">
            {providers.length === 0 ? "Every provider is already configured." : "No providers match."}
          </li>
        )}
        {matches.map((p, i) => (
          <li
            key={p.id}
            id={`${listId}-${p.id}`}
            role="option"
            aria-selected={i === activeIndex}
            onMouseEnter={() => setActive(i)}
            onMouseDown={(e) => e.preventDefault()}
            onClick={() => onPick(p.id)}
            className={`flex gap-3 px-3 py-2.5 rounded-lg cursor-pointer ${
              i === activeIndex ? "bg-subtle" : ""
            }`}
          >
            <ProviderIcon id={p.id} size={24} />
            <div className="min-w-0">
              <div className="text-sm font-medium text-ink">{p.name}</div>
              <div className="text-xs text-ink-secondary leading-relaxed">{p.description}</div>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}

/* ---------- Setting up a new provider ---------- */

function SetupCard({
  provider,
  onCancel,
  onSaved,
}: {
  provider: ProviderInfo;
  onCancel: () => void;
  onSaved: () => Promise<void>;
}) {
  const [apiKey, setApiKey] = useState("");
  const [models, setModels] = useState<string[]>(provider.suggestedModels);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!apiKey.trim()) {
      setError("Paste an API key for this provider.");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await saveProvider(provider.id, { apiKey: apiKey.trim(), models });
      await onSaved();
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <form
      onSubmit={submit}
      className="rounded-xl border border-accent bg-canvas p-4 space-y-4"
      aria-label={`Set up ${provider.name}`}
    >
      <div className="flex items-start gap-3">
        <ProviderIcon id={provider.id} />
        <div>
          <div className="font-medium text-ink">{provider.name}</div>
          <div className="text-xs text-ink-secondary">{provider.description}</div>
        </div>
      </div>

      <div>
        <label htmlFor={`setup-key-${provider.id}`} className="block text-xs font-medium text-ink-secondary mb-1">
          API key
        </label>
        <input
          id={`setup-key-${provider.id}`}
          type="password"
          autoFocus
          autoComplete="off"
          value={apiKey}
          onChange={(e) => setApiKey(e.target.value)}
          placeholder={`Paste your ${provider.name} API key`}
          className="w-full px-3 py-2 rounded-lg bg-surface border border-line-strong text-sm font-mono text-ink"
        />
      </div>

      <ModelList providerId={provider.id} models={models} onChange={setModels} />

      {error && (
        <p role="alert" className="text-sm" style={{ color: "var(--status-danger)" }}>
          {error}
        </p>
      )}

      <div className="flex justify-end gap-2">
        <button
          type="button"
          onClick={onCancel}
          className="px-3.5 py-2 rounded-lg text-sm font-medium text-ink-secondary hover:bg-subtle cursor-pointer"
        >
          Cancel
        </button>
        <button
          type="submit"
          disabled={busy || models.length === 0}
          className="inline-flex items-center gap-1.5 px-3.5 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-sm font-semibold cursor-pointer disabled:opacity-50"
        >
          {busy && <Loader2 className="w-4 h-4 animate-spin" />}
          Add {provider.name}
        </button>
      </div>
    </form>
  );
}

/* ---------- A configured provider ---------- */

function ProviderCard({
  provider,
  onChanged,
}: {
  provider: ProviderInfo;
  onChanged: () => Promise<void> | void;
}) {
  const [replacing, setReplacing] = useState(false);
  const [newKey, setNewKey] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const envOnly = provider.hasEnvKey && !provider.hasStoredKey;

  async function run(action: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await action();
      await onChanged();
      return true;
    } catch (err) {
      setError((err as Error).message);
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function saveKey(e: React.FormEvent) {
    e.preventDefault();
    if (!newKey.trim()) return;
    if (await run(() => saveProvider(provider.id, { apiKey: newKey.trim() }))) {
      setNewKey("");
      setReplacing(false);
    }
  }

  return (
    <div className="rounded-xl border border-line bg-canvas overflow-hidden">
      <div className="flex items-center gap-3 px-4 py-3 border-b border-line">
        <ProviderIcon id={provider.id} />
        <div className="min-w-0 flex-1">
          <div className="font-medium text-ink">{provider.name}</div>
          {envOnly && (
            <div className="text-xs text-ink-secondary">
              Included with your Escribot plan. Add your own key to remove plan limits.
            </div>
          )}
        </div>
        {busy && <Loader2 className="w-4 h-4 animate-spin text-ink-muted" />}
        {provider.hasStoredKey && (
          <button
            type="button"
            disabled={busy}
            onClick={() => {
              if (window.confirm(`Remove ${provider.name} and its saved API key?`)) {
                void run(() => removeProvider(provider.id));
              }
            }}
            aria-label={`Remove ${provider.name}`}
            className="p-1.5 rounded-lg text-ink-muted hover:text-ink hover:bg-subtle cursor-pointer disabled:opacity-50"
          >
            <Trash2 className="w-4 h-4" />
          </button>
        )}
      </div>

      <div className="px-4 py-3 space-y-4">
        <div>
          <div className="text-xs font-medium text-ink-secondary mb-1">API key</div>
          {replacing ? (
            <form onSubmit={saveKey} className="flex flex-col sm:flex-row gap-2">
              <input
                type="password"
                autoFocus
                autoComplete="off"
                aria-label={`New ${provider.name} API key`}
                value={newKey}
                onChange={(e) => setNewKey(e.target.value)}
                placeholder={`Paste a new ${provider.name} API key`}
                className="flex-1 min-w-0 px-3 py-2 rounded-lg bg-surface border border-line-strong text-sm font-mono text-ink"
              />
              <div className="flex gap-2 justify-end">
                <button
                  type="button"
                  onClick={() => {
                    setReplacing(false);
                    setNewKey("");
                  }}
                  className="px-3 py-2 rounded-lg text-sm text-ink-secondary hover:bg-subtle cursor-pointer"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={busy || !newKey.trim()}
                  className="px-3 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-sm font-semibold cursor-pointer disabled:opacity-50"
                >
                  Save key
                </button>
              </div>
            </form>
          ) : (
            <div className="flex items-center justify-between gap-3">
              <span className="font-mono text-sm text-ink truncate">
                {provider.maskedStoredKey ?? "Escribot's key"}
              </span>
              <button
                type="button"
                onClick={() => setReplacing(true)}
                className="shrink-0 text-sm font-medium text-accent hover:underline cursor-pointer"
              >
                {provider.hasStoredKey ? "Replace" : "Add your key"}
              </button>
            </div>
          )}
        </div>

        <ModelList
          providerId={provider.id}
          models={provider.models}
          disabled={busy}
          onChange={(models) => void run(() => saveProvider(provider.id, { models }))}
        />

        {error && (
          <p role="alert" className="text-sm" style={{ color: "var(--status-danger)" }}>
            {error}
          </p>
        )}
      </div>
    </div>
  );
}

/* ---------- A provider's model list ---------- */

function ModelList({
  providerId,
  models,
  disabled = false,
  onChange,
}: {
  providerId: AIProviderId;
  models: string[];
  disabled?: boolean;
  onChange: (models: string[]) => void;
}) {
  const inputId = useId();
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);

  function add() {
    const model = draft.trim();
    if (!model) return;
    const problem =
      modelShapeError(providerId, model) ??
      (models.includes(model) ? "That model is already listed." : null) ??
      (models.length >= MAX_MODELS ? `A provider can list up to ${MAX_MODELS} models.` : null);
    if (problem) {
      setError(problem);
      return;
    }
    setError(null);
    setDraft("");
    onChange([...models, model]);
  }

  return (
    <div>
      <div className="text-xs font-medium text-ink-secondary mb-1.5">Models ({models.length})</div>
      <ul className="space-y-1.5">
        {models.map((m) => (
          <li
            key={m}
            className="flex items-center justify-between gap-2 pl-3 pr-1.5 py-1.5 rounded-lg bg-subtle"
          >
            <span className="font-mono text-sm text-ink truncate">{m}</span>
            <button
              type="button"
              disabled={disabled || models.length <= 1}
              title={models.length <= 1 ? "A provider needs at least one model" : undefined}
              onClick={() => onChange(models.filter((x) => x !== m))}
              aria-label={`Remove ${m}`}
              className="p-1 rounded-md text-ink-muted hover:text-ink hover:bg-surface cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
            >
              <X className="w-3.5 h-3.5" />
            </button>
          </li>
        ))}
      </ul>
      <div className="flex gap-2 mt-2">
        <label htmlFor={inputId} className="sr-only">
          Add a model
        </label>
        <input
          id={inputId}
          type="text"
          list={providerId === "openrouter" ? "provider-models-openrouter" : undefined}
          value={draft}
          disabled={disabled}
          onChange={(e) => {
            setDraft(e.target.value);
            setError(null);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              add();
            }
          }}
          placeholder={providerId === "openrouter" ? "vendor/model" : "Model name"}
          className="flex-1 min-w-0 px-3 py-1.5 rounded-lg bg-surface border border-line-strong text-sm font-mono text-ink"
        />
        <button
          type="button"
          disabled={disabled || !draft.trim()}
          onClick={add}
          className="px-3 py-1.5 rounded-lg border border-line-strong text-sm font-medium text-ink hover:bg-subtle cursor-pointer disabled:opacity-50"
        >
          Add
        </button>
      </div>
      {error && (
        <p className="text-xs mt-1.5" style={{ color: "var(--status-danger)" }}>
          {error}
        </p>
      )}
    </div>
  );
}
