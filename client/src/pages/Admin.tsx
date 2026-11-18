import { Form, useLoaderData } from "react-router";
import { CompPlanToggle } from "@/components/AdminActions";
import { AppShell } from "@/components/AppShell";
import { loadJson, requireAdmin } from "@/lib/api";
import type { PlanId } from "@/lib/plans";

interface AdminOverview {
  users: { total: number; verified: number; createdLast7Days: number; createdLast30Days: number };
  subscriptions: { paying: number; pastDue: number; canceled: number };
  aiCost: { platformUsd: number; freeUsd: number; proUsd: number };
  essaysGraded: number;
  errorsLast24Hours: number;
  accounts: Array<{
    id: string;
    name: string;
    email: string;
    emailVerified: boolean;
    createdAt: string;
    plan: PlanId;
    subscriptionStatus: string | null;
    complimentaryPro: boolean;
    essays: number;
    platformCostUsd: number;
  }>;
  recentErrors: Array<{
    id: string;
    source: string;
    message: string;
    stack: string | null;
    path: string | null;
    createdAt: string;
  }>;
}

export async function loader({ request }: { request: Request }) {
  const q = new URL(request.url).searchParams.get("q") ?? "";
  const admin = await requireAdmin(request);
  const overview = await loadJson<AdminOverview>(
    `/api/admin/overview${q.trim() ? `?q=${encodeURIComponent(q)}` : ""}`,
    request
  );
  return { admin, overview, q };
}

function usd(n: number) {
  return `$${n.toFixed(2)}`;
}

function Stat({ label, value, note }: { label: string; value: string | number; note?: string }) {
  return (
    <div className="editorial-surface rounded-xl p-4">
      <p className="text-xs text-ink-secondary">{label}</p>
      <p className="font-serif text-2xl font-semibold text-ink mt-1">{value}</p>
      {note && <p className="text-xs text-ink-muted mt-0.5">{note}</p>}
    </div>
  );
}

export function AdminPage() {
  const { admin, overview, q } = useLoaderData<typeof loader>();
  const { users, subscriptions, aiCost, accounts, recentErrors } = overview;

  return (
    <AppShell user={admin}>
      <div className="max-w-6xl mx-auto px-4 sm:px-6 pt-7 pb-12 space-y-8">
        <header>
          <h1 className="font-serif text-3xl font-semibold text-ink">Admin</h1>
          <p className="text-sm text-ink-secondary">
            Month to date (UTC) unless noted. AI cost is only reported by OpenRouter.
          </p>
        </header>

        <section className="grid grid-cols-2 lg:grid-cols-4 gap-3">
          <Stat label="Users" value={users.total} note={`${users.verified} verified`} />
          <Stat
            label="New users"
            value={users.createdLast7Days}
            note={`last 7 days · ${users.createdLast30Days} in 30`}
          />
          <Stat
            label="Paying Pro"
            value={subscriptions.paying}
            note={`${subscriptions.pastDue} past due · ${subscriptions.canceled} cancelled`}
          />
          <Stat label="Errors" value={overview.errorsLast24Hours} note="last 24 hours" />
          <Stat label="Platform AI cost" value={usd(aiCost.platformUsd)} note="your API keys" />
          <Stat label="Cost from Free" value={usd(aiCost.freeUsd)} />
          <Stat label="Cost from Pro" value={usd(aiCost.proUsd)} />
          <Stat label="Essays graded" value={overview.essaysGraded} note="all key sources" />
        </section>

        <section className="space-y-3">
          <div className="flex flex-col sm:flex-row sm:items-end justify-between gap-3">
            <h2 className="font-serif text-xl font-semibold text-ink">Users</h2>
            <Form className="flex gap-2" action="/admin">
              <input
                // A new search starts from what the address says.
                key={q}
                name="q"
                defaultValue={q}
                placeholder="Search by email"
                aria-label="Search users by email"
                className="px-3 py-1.5 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
              />
              <button className="px-3 py-1.5 rounded-lg border border-line-strong text-sm cursor-pointer">
                Search
              </button>
            </Form>
          </div>
          <div className="editorial-surface rounded-xl overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-left text-xs text-ink-secondary border-b border-line">
                <tr>
                  <th className="px-3 py-2 font-medium">User</th>
                  <th className="px-3 py-2 font-medium">Joined</th>
                  <th className="px-3 py-2 font-medium">Plan</th>
                  <th className="px-3 py-2 font-medium text-right">Essays</th>
                  <th className="px-3 py-2 font-medium text-right">AI cost</th>
                  <th className="px-3 py-2 font-medium"></th>
                </tr>
              </thead>
              <tbody>
                {accounts.map((u) => (
                  <tr key={u.id} className="border-b border-line last:border-0">
                    <td className="px-3 py-2">
                      <div className="text-ink">{u.name}</div>
                      <div className="text-xs text-ink-muted">
                        {u.email}
                        {!u.emailVerified && " · unverified"}
                      </div>
                    </td>
                    <td className="px-3 py-2 text-ink-secondary whitespace-nowrap">
                      {u.createdAt.slice(0, 10)}
                    </td>
                    <td className="px-3 py-2 whitespace-nowrap">
                      {u.plan}
                      {u.subscriptionStatus && (
                        <span className="text-xs text-ink-muted"> · {u.subscriptionStatus}</span>
                      )}
                      {u.complimentaryPro && <span className="text-xs text-ink-muted"> · comp</span>}
                    </td>
                    <td className="px-3 py-2 text-right font-mono">{u.essays}</td>
                    <td className="px-3 py-2 text-right font-mono">{usd(u.platformCostUsd)}</td>
                    <td className="px-3 py-2 text-right">
                      {u.plan !== "owner" && (
                        <CompPlanToggle userId={u.id} compPro={u.complimentaryPro} />
                      )}
                    </td>
                  </tr>
                ))}
                {accounts.length === 0 && (
                  <tr>
                    <td colSpan={6} className="px-3 py-6 text-center text-ink-secondary">
                      No users found.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          <p className="text-xs text-ink-muted">Showing up to 100, newest first.</p>
        </section>

        <section className="space-y-3">
          <h2 className="font-serif text-xl font-semibold text-ink">Recent errors</h2>
          {recentErrors.length === 0 ? (
            <p className="text-sm text-ink-secondary">No errors recorded in the last 30 days.</p>
          ) : (
            <ul className="editorial-surface rounded-xl divide-y divide-line">
              {recentErrors.map((e) => (
                <li key={e.id} className="px-4 py-3 text-sm">
                  <details>
                    <summary className="cursor-pointer">
                      <span className="font-mono text-xs text-ink-muted">
                        {e.createdAt.slice(0, 19).replace("T", " ")} · {e.source}
                        {e.path ? ` · ${e.path}` : ""}
                      </span>
                      <span className="block text-ink mt-0.5 break-words">{e.message}</span>
                    </summary>
                    {e.stack && (
                      <pre className="mt-2 text-xs text-ink-secondary whitespace-pre-wrap break-words max-h-64 overflow-auto">
                        {e.stack}
                      </pre>
                    )}
                  </details>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="space-y-3">
          <h2 className="font-serif text-xl font-semibold text-ink">Image storage</h2>
          {/* The .NET server keeps every uploaded image in Blob Storage, never in the database. */}
          <p className="text-sm text-ink-secondary">All uploaded chart images are in storage.</p>
        </section>
      </div>
    </AppShell>
  );
}
