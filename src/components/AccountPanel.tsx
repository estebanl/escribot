"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { AlertTriangle, Download, Trash2 } from "lucide-react";
import { authClient } from "@/lib/auth-client";

export function AccountPanel({
  name,
  email,
  hasPassword,
  hasSubscription,
}: {
  name: string;
  email: string;
  hasPassword: boolean;
  hasSubscription: boolean;
}) {
  const router = useRouter();
  const [confirming, setConfirming] = useState(false);
  const [confirmText, setConfirmText] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleDelete(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    const res = await authClient.deleteUser(hasPassword ? { password } : {});
    if (res.error) {
      const code = res.error.code;
      setError(
        code === "INVALID_PASSWORD"
          ? "That password is not correct."
          : code === "SESSION_EXPIRED"
          ? "For your security, sign out and sign in again, then delete your account within 24 hours."
          : res.error.message || "Could not delete your account. Please try again."
      );
      setBusy(false);
      return;
    }
    router.push("/?deleted=1");
    router.refresh();
  }

  return (
    <section
      id="account-panel"
      className="max-w-3xl mx-auto px-4 sm:px-6 pt-7 pb-10 space-y-6"
      aria-labelledby="account-heading"
    >
      <div className="editorial-surface rounded-2xl p-6 space-y-5">
        <div>
          <h2 id="account-heading" className="font-serif text-lg font-semibold text-ink">
            Account &amp; Privacy
          </h2>
          <p className="text-sm text-ink-secondary mt-1">
            Signed in as <span className="font-medium text-ink">{name}</span> ({email}).
          </p>
        </div>

        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pt-4 border-t border-line">
          <div>
            <p className="text-sm font-medium text-ink">Download all your data</p>
            <p className="text-xs text-ink-secondary">
              Your account details, settings, essays, feedback, Vault, drills and AI usage,
              as one JSON file.
            </p>
          </div>
          <a
            id="export-data-button"
            href="/api/account/export"
            download
            className="shrink-0 inline-flex items-center justify-center gap-2 px-4 py-2 rounded-lg border border-line-strong bg-canvas hover:bg-subtle text-ink text-sm font-medium transition"
          >
            <Download className="w-4 h-4" />
            Download my data
          </a>
        </div>

        <div className="pt-4 border-t border-line space-y-3">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
            <div>
              <p className="text-sm font-medium text-ink">Delete account</p>
              <p className="text-xs text-ink-secondary">
                Permanently erases your account and everything in it. This cannot be undone.
              </p>
            </div>
            {!confirming && (
              <button
                id="delete-account-button"
                type="button"
                onClick={() => setConfirming(true)}
                className="shrink-0 inline-flex items-center justify-center gap-2 px-4 py-2 rounded-lg border text-sm font-medium transition cursor-pointer"
                style={{ borderColor: "var(--status-danger)", color: "var(--status-danger)" }}
              >
                <Trash2 className="w-4 h-4" />
                Delete account
              </button>
            )}
          </div>

          {confirming && (
            <form
              id="delete-account-form"
              onSubmit={handleDelete}
              className="space-y-3 p-4 rounded-lg border"
              style={{
                borderColor: "var(--status-danger)",
                backgroundColor: "var(--status-danger-soft)",
              }}
            >
              <p className="flex items-start gap-2 text-sm text-ink">
                <AlertTriangle
                  className="w-4 h-4 shrink-0 mt-0.5"
                  style={{ color: "var(--status-danger)" }}
                />
                <span>
                  All essays, examiner feedback, Vault items, drills and uploaded images will be
                  deleted.
                  {hasSubscription &&
                    " Your Pro subscription will be cancelled immediately, without a refund for the current period."}{" "}
                  Download your data first if you want a copy.
                </span>
              </p>
              {hasPassword && (
                <div>
                  <label htmlFor="delete-password" className="block text-xs font-medium text-ink-secondary mb-1">
                    Your password
                  </label>
                  <input
                    id="delete-password"
                    type="password"
                    required
                    autoComplete="current-password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-ink focus:outline-none focus:border-accent"
                  />
                </div>
              )}
              <div>
                <label htmlFor="delete-confirm" className="block text-xs font-medium text-ink-secondary mb-1">
                  Type DELETE to confirm
                </label>
                <input
                  id="delete-confirm"
                  type="text"
                  autoComplete="off"
                  value={confirmText}
                  onChange={(e) => setConfirmText(e.target.value)}
                  className="w-full px-3 py-2 rounded-lg bg-canvas border border-line-strong text-ink focus:outline-none focus:border-accent"
                />
              </div>
              {error && (
                <p role="alert" className="text-sm" style={{ color: "var(--status-danger)" }}>
                  {error}
                </p>
              )}
              <div className="flex gap-2 justify-end">
                <button
                  type="button"
                  onClick={() => {
                    setConfirming(false);
                    setError(null);
                    setPassword("");
                    setConfirmText("");
                  }}
                  className="px-4 py-2 rounded-lg border border-line-strong bg-canvas text-ink text-sm font-medium cursor-pointer"
                >
                  Cancel
                </button>
                <button
                  id="confirm-delete-account-button"
                  type="submit"
                  disabled={busy || confirmText !== "DELETE" || (hasPassword && !password)}
                  className="px-4 py-2 rounded-lg text-sm font-medium text-white disabled:opacity-50 cursor-pointer"
                  style={{ backgroundColor: "var(--status-danger)" }}
                >
                  {busy ? "Deleting..." : "Permanently delete"}
                </button>
              </div>
            </form>
          )}
        </div>
      </div>
    </section>
  );
}
