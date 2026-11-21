import { useState } from "react";
import {
  Layers,
  ListFilter,
  Plus,
  Trash2,
  RotateCw,
  CheckCircle2,
  X,
} from "lucide-react";
import { TEXT_LIMITS } from "@/lib/limits";

export interface VaultItemRecord {
  id: string;
  itemType: string;
  criterion: string;
  originalText: string;
  upgradedText: string;
  explanation: string;
  exampleSentence: string;
  masteryLevel: number;
  reviewCount: number;
  createdAt: string;
}

const MASTERY_LABELS = ["New", "Learning", "Reviewing", "Mastered"];

export function VaultClient({
  initialItems,
}: {
  initialItems: VaultItemRecord[];
}) {
  const [items, setItems] = useState<VaultItemRecord[]>(initialItems);
  const [filterCriterion, setFilterCriterion] = useState<string>("ALL");
  const [mode, setMode] = useState<"list" | "flashcards">("list");
  const [cardIndex, setCardIndex] = useState(0);
  const [flipped, setFlipped] = useState(false);

  // Add manual item modal
  const [addOpen, setAddOpen] = useState(false);
  const [newOriginal, setNewOriginal] = useState("");
  const [newUpgraded, setNewUpgraded] = useState("");
  const [newCriterion, setNewCriterion] = useState("LR");
  const [newExplanation, setNewExplanation] = useState("");

  const filtered = items.filter(
    (item) => filterCriterion === "ALL" || item.criterion === filterCriterion
  );

  async function updateMastery(id: string, nextLevel: number) {
    setItems((prev) =>
      prev.map((it) => (it.id === id ? { ...it, masteryLevel: nextLevel } : it))
    );
    await fetch("/api/vault", {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id, masteryLevel: nextLevel }),
    });
    if (mode === "flashcards") {
      setFlipped(false);
      setCardIndex((idx) => (filtered.length > 1 ? (idx + 1) % filtered.length : 0));
    }
  }

  async function handleDelete(id: string) {
    setItems((prev) => prev.filter((it) => it.id !== id));
    await fetch(`/api/vault?id=${encodeURIComponent(id)}`, { method: "DELETE" });
  }

  async function handleCreateManual(e: React.FormEvent) {
    e.preventDefault();
    const res = await fetch("/api/vault", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        itemType: newCriterion === "L1" || newCriterion === "GRA" ? "error" : "collocation",
        criterion: newCriterion,
        originalText: newOriginal,
        upgradedText: newUpgraded,
        explanation: newExplanation,
        exampleSentence: newUpgraded,
      }),
    });
    const data = await res.json();
    if (res.ok && data.item) {
      setItems((prev) => [data.item, ...prev]);
      setNewOriginal("");
      setNewUpgraded("");
      setNewExplanation("");
      setAddOpen(false);
    }
  }

  const activeCard = filtered[cardIndex % Math.max(1, filtered.length)];

  return (
    <div className="max-w-6xl mx-auto px-4 sm:px-6 pt-7 space-y-6">
      <header className="flex flex-col sm:flex-row sm:items-end justify-between gap-4 pb-5 border-b border-line">
        <div className="space-y-1.5">
          <span className="text-xs font-mono uppercase tracking-wider text-accent font-semibold">
            Personal Retention Deck · {items.length} Saved Entries
          </span>
          <h1 className="font-serif text-3xl font-semibold text-ink">
            Error &amp; Academic Collocation Vault
          </h1>
          <p className="text-sm text-ink-secondary max-w-2xl">
            Every graded essay automatically populates your Vault with recurring grammar
            slips, L1 transfer traps, and Band 8.0+ academic collocations.
          </p>
        </div>

        <div className="flex items-center gap-2">
          <button
            id="toggle-flashcard-mode-btn"
            type="button"
            onClick={() => {
              setMode((m) => (m === "list" ? "flashcards" : "list"));
              setFlipped(false);
              setCardIndex(0);
            }}
            className="inline-flex items-center gap-2 px-4 py-2.5 rounded-xl bg-accent hover:bg-accent-hover text-accent-fg text-xs font-semibold cursor-pointer"
          >
            <Layers className="w-4 h-4" />
            <span>{mode === "list" ? "Flashcard Review" : "Back to Vault List"}</span>
          </button>

          <button
            type="button"
            onClick={() => setAddOpen(true)}
            className="inline-flex items-center gap-1.5 px-3.5 py-2.5 rounded-xl border border-line-strong bg-surface hover:bg-subtle text-xs font-semibold text-ink cursor-pointer"
          >
            <Plus className="w-4 h-4" />
            <span>Add Card</span>
          </button>
        </div>
      </header>

      {/* Criterion Filter Pills */}
      <div className="flex flex-wrap items-center gap-2">
        <ListFilter className="w-4 h-4 text-ink-muted mr-1" />
        {(["ALL", "L1", "CC", "LR", "GRA", "TA_TR"] as const).map((crit) => (
          <button
            key={crit}
            type="button"
            onClick={() => {
              setFilterCriterion(crit);
              setCardIndex(0);
              setFlipped(false);
            }}
            className={`px-3 py-1.5 rounded-lg text-xs font-mono font-semibold transition cursor-pointer ${
              filterCriterion === crit
                ? "bg-ink text-canvas"
                : "bg-surface border border-line text-ink-secondary hover:text-ink"
            }`}
          >
            {crit === "ALL" ? "All Categories" : crit.replace("_", "/")}
          </button>
        ))}
      </div>

      {/* FLASHCARD REVIEW DECK (Great on Mobile & Desktop) */}
      {mode === "flashcards" && activeCard ? (
        <div className="max-w-xl mx-auto py-4 space-y-4">
          <div className="flex items-center justify-between text-xs font-mono text-ink-muted">
            <span>
              Card {(cardIndex % filtered.length) + 1} of {filtered.length}
            </span>
            <span>Mastery: {MASTERY_LABELS[activeCard.masteryLevel]}</span>
          </div>

          <div
            onClick={() => setFlipped((f) => !f)}
            className="editorial-surface rounded-2xl p-8 min-h-[270px] flex flex-col justify-between cursor-pointer select-none transition hover:border-line-strong"
          >
            <div className="flex items-center justify-between">
              <span
                className={`px-2.5 py-0.5 rounded text-xs font-mono font-bold span-${activeCard.criterion}`}
              >
                {activeCard.criterion} ·{" "}
                {flipped ? "Band 8.0+ Upgrade" : "Upgrade This Band 6 Phrasing"}
              </span>
              <RotateCw className="w-4 h-4 text-ink-muted" />
            </div>

            {!flipped ? (
              <div className="my-6 space-y-2">
                <p className="editorial-prose text-xl text-ink">
                  &ldquo;{activeCard.originalText}&rdquo;
                </p>
                <p className="text-xs text-ink-muted">
                  How would you express this cleanly at Band 8.0+? (Tap card to reveal)
                </p>
              </div>
            ) : (
              <div className="my-4 space-y-3">
                <p className="text-xs line-through text-ink-muted">
                  {activeCard.originalText}
                </p>
                <p className="editorial-prose text-xl font-medium text-accent">
                  &ldquo;{activeCard.upgradedText}&rdquo;
                </p>
                <p className="text-xs text-ink-secondary leading-relaxed">
                  {activeCard.explanation}
                </p>
              </div>
            )}

            <div className="text-[11px] font-mono text-ink-muted">
              {flipped
                ? "Rate your recall below to schedule next review"
                : "Tap anywhere on card to flip"}
            </div>
          </div>

          {flipped && (
            <div className="grid grid-cols-3 gap-2.5">
              <button
                type="button"
                onClick={() => updateMastery(activeCard.id, 1)}
                className="py-2.5 px-3 rounded-xl border border-line-strong bg-surface hover:bg-subtle text-xs font-semibold text-warm cursor-pointer"
              >
                Still Slipping (1d)
              </button>
              <button
                type="button"
                onClick={() => updateMastery(activeCard.id, 2)}
                className="py-2.5 px-3 rounded-xl border border-line-strong bg-surface hover:bg-subtle text-xs font-semibold text-ink cursor-pointer"
              >
                Solid Recall (3d)
              </button>
              <button
                type="button"
                onClick={() => updateMastery(activeCard.id, 3)}
                className="py-2.5 px-3 rounded-xl bg-accent hover:bg-accent-hover text-accent-fg text-xs font-semibold cursor-pointer"
              >
                Mastered (7d)
              </button>
            </div>
          )}
        </div>
      ) : (
        /* VAULT LIST GRID */
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          {filtered.map((item) => (
            <article
              key={item.id}
              className="editorial-surface rounded-2xl p-5 flex flex-col justify-between gap-4"
            >
              <div className="space-y-2.5">
                <div className="flex items-center justify-between gap-2">
                  <div className="flex items-center gap-2">
                    <span
                      className={`px-2 py-0.5 rounded text-[11px] font-mono font-bold span-${item.criterion}`}
                    >
                      {item.criterion.replace("_", "/")}
                    </span>
                    <span className="text-[11px] font-mono text-ink-muted uppercase">
                      {item.itemType}
                    </span>
                  </div>

                  <button
                    type="button"
                    onClick={() => handleDelete(item.id)}
                    title="Remove from Vault"
                    className="p-1 text-ink-muted hover:text-red-500 cursor-pointer"
                  >
                    <Trash2 className="w-3.5 h-3.5" />
                  </button>
                </div>

                <div className="space-y-1">
                  <p className="text-xs line-through text-ink-muted">
                    {item.originalText}
                  </p>
                  <p className="editorial-prose text-base font-medium text-ink">
                    → {item.upgradedText}
                  </p>
                </div>

                <p className="text-xs text-ink-secondary leading-relaxed">
                  {item.explanation}
                </p>
              </div>

              <div className="pt-3 border-t border-line flex items-center justify-between text-xs">
                <span className="font-mono text-[11px] text-ink-muted">
                  Status: {MASTERY_LABELS[item.masteryLevel] || "New"}
                </span>
                <button
                  type="button"
                  onClick={() =>
                    updateMastery(item.id, item.masteryLevel >= 3 ? 1 : 3)
                  }
                  className={`inline-flex items-center gap-1 font-medium cursor-pointer ${
                    item.masteryLevel >= 3 ? "text-accent" : "text-ink-secondary hover:text-ink"
                  }`}
                >
                  <CheckCircle2 className="w-3.5 h-3.5" />
                  <span>
                    {item.masteryLevel >= 3 ? "Mastered" : "Mark Mastered"}
                  </span>
                </button>
              </div>
            </article>
          ))}
        </div>
      )}

      {/* Manual Add Modal */}
      {addOpen && (
        <div className="fixed inset-0 z-50 bg-black/60 backdrop-blur-xs flex items-center justify-center p-4">
          <div className="w-full max-w-md editorial-surface rounded-2xl p-6 space-y-4">
            <div className="flex items-center justify-between">
              <h2 className="font-serif text-lg font-semibold text-ink">
                Save Phrase or Error to Vault
              </h2>
              <button
                type="button"
                onClick={() => setAddOpen(false)}
                className="p-1 text-ink-muted hover:text-ink cursor-pointer"
              >
                <X className="w-4 h-4" />
              </button>
            </div>

            <form onSubmit={handleCreateManual} className="space-y-3">
              <div>
                <label className="block text-xs font-medium text-ink-secondary mb-1">
                  Category
                </label>
                <select
                  value={newCriterion}
                  onChange={(e) => setNewCriterion(e.target.value)}
                  className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
                >
                  <option value="L1">L1 Transfer (Spanish → English)</option>
                  <option value="LR">LR · Academic Collocation / Hedging</option>
                  <option value="CC">CC · Cohesive Linking</option>
                  <option value="GRA">GRA · Clause &amp; Punctuation</option>
                  <option value="TA_TR">TA/TR · Task Framing</option>
                </select>
              </div>

              <div>
                <label className="block text-xs font-medium text-ink-secondary mb-1">
                  Band 6 / Flawed Phrasing *
                </label>
                <input
                  required
                  type="text"
                  value={newOriginal}
                  maxLength={TEXT_LIMITS.vaultText}
                  onChange={(e) => setNewOriginal(e.target.value)}
                  placeholder="e.g., Actually, governments spend a lot of money..."
                  className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-ink-secondary mb-1">
                  Band 8.0+ Upgraded Phrasing *
                </label>
                <input
                  required
                  type="text"
                  value={newUpgraded}
                  maxLength={TEXT_LIMITS.vaultText}
                  onChange={(e) => setNewUpgraded(e.target.value)}
                  placeholder="e.g., Currently, municipal authorities allocate substantial capital..."
                  className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-ink-secondary mb-1">
                  Rule / Note
                </label>
                <input
                  type="text"
                  value={newExplanation}
                  maxLength={TEXT_LIMITS.vaultText}
                  onChange={(e) => setNewExplanation(e.target.value)}
                  placeholder="Why this upgrade scores higher..."
                  className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
                />
              </div>

              <div className="flex justify-end gap-2 pt-2">
                <button
                  type="button"
                  onClick={() => setAddOpen(false)}
                  className="px-4 py-2 rounded-lg border border-line text-xs text-ink-secondary cursor-pointer"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  className="px-4 py-2 rounded-lg bg-accent text-accent-fg text-xs font-semibold cursor-pointer"
                >
                  Save Card
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
