import { useLoaderData } from "react-router";
import { AppShell } from "@/components/AppShell";
import {
  CoachWorkspace,
  type PreviousFocus,
  type RestoredCoachDraft,
} from "@/components/CoachWorkspace";
import { InsperaSimulator, type RestoredSimulatorPart } from "@/components/InsperaSimulator";
import type { PromptRecord } from "@/components/PromptModals";
import { loadJson, requireUser } from "@/lib/api";
import type { SessionMode } from "@/lib/types";

interface WriteData {
  sessionId: string;
  mode: SessionMode;
  coach?: {
    prompt: PromptRecord;
    previousFocus: PreviousFocus | null;
    initialDraft: RestoredCoachDraft | null;
  };
  simulator?: {
    task1Prompt: PromptRecord | null;
    task2Prompt: PromptRecord | null;
    initialDrafts: {
      task1: RestoredSimulatorPart | null;
      task2: RestoredSimulatorPart | null;
    };
  };
}

export async function loader({ request, params }: { request: Request; params: { sessionId?: string } }) {
  const [user, page] = await Promise.all([
    requireUser(request),
    loadJson<WriteData>(`/api/pages/write/${encodeURIComponent(params.sessionId ?? "")}`, request),
  ]);
  return { user, page };
}

export function WritePage() {
  const { user, page } = useLoaderData<typeof loader>();

  if (page.coach) {
    return (
      <AppShell user={user}>
        <CoachWorkspace
          sessionId={page.sessionId}
          prompt={page.coach.prompt}
          previousFocus={page.coach.previousFocus}
          initialDraft={page.coach.initialDraft}
        />
      </AppShell>
    );
  }

  // Authentic Full-Screen Computer-Delivered IELTS (Inspera) Simulator
  return (
    <InsperaSimulator
      sessionId={page.sessionId}
      mode={page.mode}
      task1Prompt={page.simulator?.task1Prompt ?? null}
      task2Prompt={page.simulator?.task2Prompt ?? null}
      initialDrafts={page.simulator?.initialDrafts}
    />
  );
}
