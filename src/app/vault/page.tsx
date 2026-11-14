import { desc, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { requireUser } from "@/lib/session";
import { AppShell } from "@/components/AppShell";
import { VaultClient } from "@/components/VaultClient";

export const dynamic = "force-dynamic";

export default async function VaultPage() {
  const user = await requireUser();
  const db = await getDb();
  const items = await db
    .select()
    .from(schema.vaultItems)
    .where(eq(schema.vaultItems.userId, user.id))
    .orderBy(desc(schema.vaultItems.createdAt));

  return (
    <AppShell user={user}>
      <VaultClient initialItems={items} />
    </AppShell>
  );
}
