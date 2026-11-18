import { useLoaderData, useSearchParams } from "react-router";
import { AccountPanel } from "@/components/AccountPanel";
import { AppShell } from "@/components/AppShell";
import { SettingsClient } from "@/components/SettingsClient";
import { loadJson, requireUser } from "@/lib/api";

export async function loader({ request }: { request: Request }) {
  const [user, { hasSubscription }] = await Promise.all([
    requireUser(request),
    loadJson<{ hasSubscription: boolean }>("/api/pages/settings", request),
  ]);
  return { user, hasSubscription };
}

export function SettingsPage() {
  const { user, hasSubscription } = useLoaderData<typeof loader>();
  const [searchParams] = useSearchParams();

  return (
    <AppShell user={user}>
      <SettingsClient billingReturn={searchParams.get("billing") === "success" ? "success" : null} />
      <AccountPanel
        name={user.name}
        email={user.email}
        hasPassword={user.hasPassword}
        hasSubscription={hasSubscription}
      />
    </AppShell>
  );
}
