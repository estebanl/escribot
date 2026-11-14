import { requireUser } from "@/lib/session";
import { AppShell } from "@/components/AppShell";
import { SettingsClient } from "@/components/SettingsClient";

export const dynamic = "force-dynamic";

export default async function SettingsPage({
  searchParams,
}: {
  searchParams: Promise<{ billing?: string }>;
}) {
  const user = await requireUser();
  const { billing } = await searchParams;
  return (
    <AppShell user={user}>
      <SettingsClient billingReturn={billing === "success" ? "success" : null} />
    </AppShell>
  );
}
