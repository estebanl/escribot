import { redirect, useLoaderData, useSearchParams } from "react-router";
import { AppShell } from "@/components/AppShell";
import { LandingPage } from "@/components/LandingPage";
import type { PromptRecord } from "@/components/PromptModals";
import { StudioHomeClient } from "@/components/StudioHomeClient";
import { getViewer, loadJson } from "@/lib/api";
import { computeDaysUntilExam } from "@/lib/ielts-utils";
import { PLANS } from "@/lib/plans";

interface StudioData {
  prompts: PromptRecord[];
  recentSessions: Array<{
    id: string;
    mode: string;
    status: string;
    startedAt: string;
    weightedBand: number | null;
    task1Title: string | null;
    task2Title: string | null;
    latestDraftVersion: number;
  }>;
  settingsSummary: {
    currentBand: number;
    targetBand: number;
    examDate: string | null;
    nativeLanguage: string;
    l1CoachingEnabled: boolean;
    activeProvider: string;
    activeProviderReady: boolean;
  };
}

/** Signed-out visitors get the marketing page; signed-in users the studio. */
export async function loader({ request }: { request: Request }) {
  const user = await getViewer(request);
  if (!user) return { user: null, studio: null };
  if (!user.onboarded) throw redirect("/welcome");
  const studio = await loadJson<StudioData>("/api/pages/studio", request);
  return { user, studio };
}

export function HomePage() {
  const { user, studio } = useLoaderData<typeof loader>();
  const [searchParams] = useSearchParams();

  if (!user || !studio) {
    return <LandingPage deleted={searchParams.get("deleted") === "1"} />;
  }

  const { examDate, ...summary } = studio.settingsSummary;
  return (
    <AppShell user={user}>
      <StudioHomeClient
        initialPrompts={studio.prompts}
        recentSessions={studio.recentSessions}
        settingsSummary={{ ...summary, daysUntilExam: computeDaysUntilExam(examDate) }}
        canTransformSvg={PLANS[user.plan].canTransformSvg}
      />
    </AppShell>
  );
}
