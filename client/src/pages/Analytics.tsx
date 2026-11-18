import type { ComponentProps } from "react";
import { useLoaderData } from "react-router";
import { AnalyticsClient } from "@/components/AnalyticsClient";
import { AppShell } from "@/components/AppShell";
import { loadJson, requireUser } from "@/lib/api";

type AnalyticsData = ComponentProps<typeof AnalyticsClient>;

export async function loader({ request }: { request: Request }) {
  const [user, analytics] = await Promise.all([
    requireUser(request),
    loadJson<AnalyticsData>("/api/pages/analytics", request),
  ]);
  return { user, analytics };
}

export function AnalyticsPage() {
  const { user, analytics } = useLoaderData<typeof loader>();
  return (
    <AppShell user={user}>
      <AnalyticsClient {...analytics} />
    </AppShell>
  );
}
