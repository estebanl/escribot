import { and, desc, eq, gte, inArray, isNotNull, like, sql } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { requireAdmin } from "@/lib/session";
import { resolvePlan } from "@/lib/plans";
import { hasProAccess } from "@/lib/billing/subscription";
import { AppShell } from "@/components/AppShell";
import { CompPlanToggle, MigrateImagesButton } from "@/components/AdminActions";

export const dynamic = "force-dynamic";
export const metadata = { title: "Admin — FluentEscritor" };

const EVALUATION_FEATURES = ["rubric_score", "rubric_score_fallback"];

/** Reporting windows, computed per request (this page is never cached). */
function timeWindows() {
  const now = new Date();
  return {
    now: now.getTime(),
    since: new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1)).toISOString(),
  };
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

export default async function AdminPage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string }>;
}) {
  const admin = await requireAdmin();
  const { q } = await searchParams;
  const db = await getDb();
  const { since, now } = timeWindows();

  const [{ total }] = await db.select({ total: sql<number>`count(*)` }).from(schema.user);
  const [{ verified }] = await db
    .select({ verified: sql<number>`count(*)` })
    .from(schema.user)
    .where(eq(schema.user.emailVerified, true));
  const [{ last7 }] = await db
    .select({ last7: sql<number>`count(*)` })
    .from(schema.user)
    .where(gte(schema.user.createdAt, new Date(now - 7 * 86_400_000)));
  const [{ last30 }] = await db
    .select({ last30: sql<number>`count(*)` })
    .from(schema.user)
    .where(gte(schema.user.createdAt, new Date(now - 30 * 86_400_000)));

  const subStatus = await db
    .select({ status: schema.subscriptions.status, n: sql<number>`count(*)` })
    .from(schema.subscriptions)
    .where(isNotNull(schema.subscriptions.status))
    .groupBy(schema.subscriptions.status);
  const subCount = (s: string) => Number(subStatus.find((r) => r.status === s)?.n ?? 0);

  const spendRows = await db
    .select({
      plan: schema.aiUsage.plan,
      keySource: schema.aiUsage.keySource,
      cost: sql<number>`coalesce(sum(${schema.aiUsage.costUsd}), 0)`,
      calls: sql<number>`count(*)`,
    })
    .from(schema.aiUsage)
    .where(gte(schema.aiUsage.createdAt, since))
    .groupBy(schema.aiUsage.plan, schema.aiUsage.keySource);
  const platform = spendRows.filter((r) => r.keySource === "platform");
  const platformCost = platform.reduce((a, r) => a + Number(r.cost), 0);
  const costFor = (plan: string) =>
    platform.filter((r) => r.plan === plan).reduce((a, r) => a + Number(r.cost), 0);

  const [{ essays }] = await db
    .select({ essays: sql<number>`count(*)` })
    .from(schema.aiUsage)
    .where(
      and(
        gte(schema.aiUsage.createdAt, since),
        inArray(schema.aiUsage.feature, EVALUATION_FEATURES)
      )
    );

  const [{ errors24h }] = await db
    .select({ errors24h: sql<number>`count(*)` })
    .from(schema.errorEvents)
    .where(gte(schema.errorEvents.createdAt, new Date(now - 86_400_000).toISOString()));

  const userFilter = q?.trim() ? like(schema.user.email, `%${q.trim().toLowerCase()}%`) : undefined;
  const users = await db
    .select({
      id: schema.user.id,
      name: schema.user.name,
      email: schema.user.email,
      emailVerified: schema.user.emailVerified,
      createdAt: schema.user.createdAt,
      storedPlan: schema.userSettings.plan,
      subStatus: schema.subscriptions.status,
    })
    .from(schema.user)
    .leftJoin(schema.userSettings, eq(schema.userSettings.userId, schema.user.id))
    .leftJoin(schema.subscriptions, eq(schema.subscriptions.userId, schema.user.id))
    .where(userFilter)
    .orderBy(desc(schema.user.createdAt))
    .limit(100);

  const ids = users.map((u) => u.id);
  const usage = ids.length
    ? await db
        .select({
          userId: schema.aiUsage.userId,
          essays: sql<number>`sum(case when ${schema.aiUsage.feature} in ('rubric_score','rubric_score_fallback') then 1 else 0 end)`,
          cost: sql<number>`coalesce(sum(case when ${schema.aiUsage.keySource} = 'platform' then ${schema.aiUsage.costUsd} end), 0)`,
        })
        .from(schema.aiUsage)
        .where(and(gte(schema.aiUsage.createdAt, since), inArray(schema.aiUsage.userId, ids)))
        .groupBy(schema.aiUsage.userId)
    : [];
  const usageBy = new Map(usage.map((u) => [u.userId, u]));

  const errors = await db
    .select()
    .from(schema.errorEvents)
    .orderBy(desc(schema.errorEvents.createdAt))
    .limit(25);

  const [{ inlineImages }] = await db
    .select({ inlineImages: sql<number>`count(*)` })
    .from(schema.prompts)
    .where(and(isNotNull(schema.prompts.userId), like(schema.prompts.imageDataUrl, "data:%")));

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
          <Stat label="Users" value={Number(total)} note={`${Number(verified)} verified`} />
          <Stat label="New users" value={Number(last7)} note={`last 7 days · ${Number(last30)} in 30`} />
          <Stat
            label="Paying Pro"
            value={subCount("active") + subCount("trialing")}
            note={`${subCount("past_due")} past due · ${subCount("canceled")} cancelled`}
          />
          <Stat label="Errors" value={Number(errors24h)} note="last 24 hours" />
          <Stat label="Platform AI cost" value={usd(platformCost)} note="your API keys" />
          <Stat label="Cost from Free" value={usd(costFor("free"))} />
          <Stat label="Cost from Pro" value={usd(costFor("pro"))} />
          <Stat label="Essays graded" value={Number(essays)} note="all key sources" />
        </section>

        <section className="space-y-3">
          <div className="flex flex-col sm:flex-row sm:items-end justify-between gap-3">
            <h2 className="font-serif text-xl font-semibold text-ink">Users</h2>
            <form className="flex gap-2" action="/admin">
              <input
                name="q"
                defaultValue={q ?? ""}
                placeholder="Search by email"
                aria-label="Search users by email"
                className="px-3 py-1.5 rounded-lg bg-canvas border border-line-strong text-sm text-ink"
              />
              <button className="px-3 py-1.5 rounded-lg border border-line-strong text-sm cursor-pointer">
                Search
              </button>
            </form>
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
                {users.map((u) => {
                  const plan = resolvePlan(
                    u.storedPlan,
                    { email: u.email, emailVerified: u.emailVerified },
                    hasProAccess({ status: u.subStatus })
                  );
                  const use = usageBy.get(u.id);
                  return (
                    <tr key={u.id} className="border-b border-line last:border-0">
                      <td className="px-3 py-2">
                        <div className="text-ink">{u.name}</div>
                        <div className="text-xs text-ink-muted">
                          {u.email}
                          {!u.emailVerified && " · unverified"}
                        </div>
                      </td>
                      <td className="px-3 py-2 text-ink-secondary whitespace-nowrap">
                        {u.createdAt.toISOString().slice(0, 10)}
                      </td>
                      <td className="px-3 py-2 whitespace-nowrap">
                        {plan}
                        {u.subStatus && <span className="text-xs text-ink-muted"> · {u.subStatus}</span>}
                        {u.storedPlan === "pro" && <span className="text-xs text-ink-muted"> · comp</span>}
                      </td>
                      <td className="px-3 py-2 text-right font-mono">{Number(use?.essays ?? 0)}</td>
                      <td className="px-3 py-2 text-right font-mono">{usd(Number(use?.cost ?? 0))}</td>
                      <td className="px-3 py-2 text-right">
                        {plan !== "owner" && (
                          <CompPlanToggle userId={u.id} compPro={u.storedPlan === "pro"} />
                        )}
                      </td>
                    </tr>
                  );
                })}
                {users.length === 0 && (
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
          {errors.length === 0 ? (
            <p className="text-sm text-ink-secondary">No errors recorded in the last 30 days.</p>
          ) : (
            <ul className="editorial-surface rounded-xl divide-y divide-line">
              {errors.map((e) => (
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
          <p className="text-sm text-ink-secondary">
            {Number(inlineImages) === 0
              ? "All uploaded chart images are in storage."
              : `${Number(inlineImages)} chart image(s) uploaded before object storage are still saved inside the database.`}
          </p>
          {Number(inlineImages) > 0 && <MigrateImagesButton count={Number(inlineImages)} />}
        </section>
      </div>
    </AppShell>
  );
}
