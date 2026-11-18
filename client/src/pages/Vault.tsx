import { useLoaderData } from "react-router";
import { AppShell } from "@/components/AppShell";
import { VaultClient, type VaultItemRecord } from "@/components/VaultClient";
import { loadJson, requireUser } from "@/lib/api";

export async function loader({ request }: { request: Request }) {
  const [user, { items }] = await Promise.all([
    requireUser(request),
    loadJson<{ items: VaultItemRecord[] }>("/api/vault", request),
  ]);
  return { user, items };
}

export function VaultPage() {
  const { user, items } = useLoaderData<typeof loader>();
  return (
    <AppShell user={user}>
      <VaultClient initialItems={items} />
    </AppShell>
  );
}
