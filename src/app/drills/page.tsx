import { eq } from "drizzle-orm";
import { isPasscodeRequired } from "@/lib/auth";
import { getDb, schema } from "@/lib/db";
import { isDrillLevel, levelFromBand } from "@/lib/ielts-utils";
import { AppShell } from "@/components/AppShell";
import { DrillsClient } from "@/components/DrillsClient";
import type { DrillLevel, DrillType } from "@/lib/types";

export const dynamic = "force-dynamic";

export default async function DrillsPage({
  searchParams,
}: {
  searchParams: Promise<{ focus?: string; level?: string }>;
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

  const db = await getDb();
  const [settingsRow] = await db
    .select()
    .from(schema.userSettings)
    .where(eq(schema.userSettings.id, 1));

  const bandLevel = levelFromBand(settingsRow?.currentBand);
  const forcedLevel: DrillLevel | null = isDrillLevel(sp.level) ? sp.level : null;

  return (
    <AppShell passcodeActive={isPasscodeRequired()}>
      <DrillsClient
        recommendedDrill={recommendedDrill}
        defaultLevel={forcedLevel ?? bandLevel}
        levelForcedByUrl={forcedLevel !== null}
      />
    </AppShell>
  );
}
