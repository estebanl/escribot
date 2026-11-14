import { and, eq } from "drizzle-orm";
import { requireUser } from "@/lib/session";
import { getDb, schema } from "@/lib/db";
import { getSubscription, hasProAccess } from "@/lib/billing/subscription";
import { AppShell } from "@/components/AppShell";
import { SettingsClient } from "@/components/SettingsClient";
import { AccountPanel } from "@/components/AccountPanel";

export const dynamic = "force-dynamic";

export default async function SettingsPage({
  searchParams,
}: {
  searchParams: Promise<{ billing?: string }>;
}) {
  const user = await requireUser();
  const { billing } = await searchParams;

  const db = await getDb();
  const [credential] = await db
    .select({ id: schema.account.id })
    .from(schema.account)
    .where(and(eq(schema.account.userId, user.id), eq(schema.account.providerId, "credential")));
  const subscription = await getSubscription(db, user.id);

  return (
    <AppShell user={user}>
      <SettingsClient billingReturn={billing === "success" ? "success" : null} />
      <AccountPanel
        name={user.name}
        email={user.email}
        hasPassword={Boolean(credential)}
        hasSubscription={hasProAccess(subscription)}
      />
    </AppShell>
  );
}
