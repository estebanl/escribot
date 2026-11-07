import { desc } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { isPasscodeRequired } from "@/lib/auth";
import { AppShell } from "@/components/AppShell";
import { VaultClient } from "@/components/VaultClient";

export const dynamic = "force-dynamic";

export default async function VaultPage() {
  const db = await getDb();
  const items = await db
    .select()
    .from(schema.vaultItems)
    .orderBy(desc(schema.vaultItems.createdAt));

  return (
    <AppShell passcodeActive={isPasscodeRequired()}>
      <VaultClient initialItems={items} />
    </AppShell>
  );
}
