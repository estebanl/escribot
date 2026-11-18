import { useLoaderData, useSearchParams } from "react-router";
import { AppShell } from "@/components/AppShell";
import { DrillsClient } from "@/components/DrillsClient";
import { loadJson, requireUser } from "@/lib/api";
import { isDrillLevel, levelFromBand } from "@/lib/ielts-utils";
import type { DrillLevel, DrillType } from "@/lib/types";

export async function loader({ request }: { request: Request }) {
  const [user, { currentBand }] = await Promise.all([
    requireUser(request),
    loadJson<{ currentBand: number }>("/api/pages/drills", request),
  ]);
  return { user, currentBand };
}

export function DrillsPage() {
  const { user, currentBand } = useLoaderData<typeof loader>();
  const [searchParams] = useSearchParams();
  const focus = searchParams.get("focus");
  const recommendedDrill: DrillType =
    focus === "TA_TR"
      ? "overview_sprint"
      : focus === "L1" || focus === "GRA"
      ? "l1_surgeon"
      : focus === "LR"
      ? "lexical_hedging"
      : "cohesion";

  const level = searchParams.get("level");
  const forcedLevel: DrillLevel | null = isDrillLevel(level) ? level : null;

  return (
    <AppShell user={user}>
      <DrillsClient
        recommendedDrill={recommendedDrill}
        defaultLevel={forcedLevel ?? levelFromBand(currentBand)}
        levelForcedByUrl={forcedLevel !== null}
      />
    </AppShell>
  );
}
