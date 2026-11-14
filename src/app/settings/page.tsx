import { requireUser } from "@/lib/session";
import { AppShell } from "@/components/AppShell";
import { SettingsClient } from "@/components/SettingsClient";

export const dynamic = "force-dynamic";

export default async function SettingsPage() {
  const user = await requireUser();
  return (
    <AppShell user={user}>
      <SettingsClient />
    </AppShell>
  );
}
