import { isPasscodeRequired } from "@/lib/auth";
import { AppShell } from "@/components/AppShell";
import { DrillsClient } from "@/components/DrillsClient";
import type { DrillType } from "@/lib/types";

export const dynamic = "force-dynamic";

export default async function DrillsPage({
  searchParams,
}: {
  searchParams: Promise<{ focus?: string }>;
}) {
  const sp = await searchParams;
  const focus = sp.focus;
  const recommendedDrill: DrillType =
    focus === "TA_TR"
      ? "overview_sprint"
      : focus === "L1" || focus === "GRA"
      ? "l1_surgeon"
      : focus === "LR"
      ? "lexical_hedging"
      : "cohesion";

  return (
    <AppShell passcodeActive={isPasscodeRequired()}>
      <DrillsClient recommendedDrill={recommendedDrill} />
    </AppShell>
  );
}
