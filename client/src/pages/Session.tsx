import { useLoaderData } from "react-router";
import { AppShell } from "@/components/AppShell";
import { EvaluationWorkbench, type HydratedDraft } from "@/components/EvaluationWorkbench";
import { loadJson, requireUser } from "@/lib/api";

interface SessionData {
  session: {
    id: string;
    mode: string;
    weightedBand: number | null;
    totalSecondsSpent: number;
    drafts: HydratedDraft[];
  };
}

export async function loader({ request, params }: { request: Request; params: { sessionId?: string } }) {
  const [user, { session }] = await Promise.all([
    requireUser(request),
    loadJson<SessionData>(`/api/sessions/${encodeURIComponent(params.sessionId ?? "")}`, request),
  ]);
  return { user, session };
}

export function SessionPage() {
  const { user, session } = useLoaderData<typeof loader>();
  return (
    <AppShell user={user}>
      <EvaluationWorkbench
        initialSession={{
          id: session.id,
          mode: session.mode,
          weightedBand: session.weightedBand,
          totalSecondsSpent: session.totalSecondsSpent,
          drafts: session.drafts,
        }}
      />
    </AppShell>
  );
}
