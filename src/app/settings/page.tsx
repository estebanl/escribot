import { isPasscodeRequired } from "@/lib/auth";
import { AppShell } from "@/components/AppShell";
import { SettingsClient } from "@/components/SettingsClient";

export const dynamic = "force-dynamic";

export default function SettingsPage() {
  return (
    <AppShell passcodeActive={isPasscodeRequired()}>
      <SettingsClient />
    </AppShell>
  );
}
