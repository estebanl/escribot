import { NextResponse } from "next/server";
import { desc, eq } from "drizzle-orm";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { generateMicroDrillWithAI, scoreMicroDrillWithAI } from "@/lib/ai/examiner";
import type { DrillType, MicroDrillChallenge } from "@/lib/types";

export async function GET() {
  try {
    const db = await getDb();
    const drills = await db
      .select()
      .from(schema.microDrills)
      .orderBy(desc(schema.microDrills.createdAt))
      .limit(20);

    return NextResponse.json({ drills });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to load drills." },
      { status: 500 }
    );
  }
}

export async function POST(request: Request) {
  try {
    const body = (await request.json()) as
      | { action: "generate"; drillType: DrillType }
      | {
          action: "score";
          drillId: string;
          challenge: MicroDrillChallenge;
          userResponse: string;
        };

    const db = await getDb();

    if (body.action === "generate") {
      const vaultRows = await db
        .select()
        .from(schema.vaultItems)
        .orderBy(desc(schema.vaultItems.createdAt))
        .limit(5);

      const vaultHighlights = vaultRows.map(
        (v) => `${v.originalText} -> ${v.upgradedText}`
      );

      const challenge = await generateMicroDrillWithAI({
        drillType: body.drillType || "cohesion",
        vaultHighlights,
      });

      const id = `drill-${crypto.randomUUID().slice(0, 9)}`;
      const now = new Date().toISOString();

      await db.insert(schema.microDrills).values({
        id,
        drillType: challenge.drillType,
        targetCriterion: challenge.targetCriterion,
        promptPayloadJson: JSON.stringify(challenge),
        userResponse: null,
        scoreJson: null,
        createdAt: now,
        completedAt: null,
      });

      return NextResponse.json({ drillId: id, challenge });
    }

    if (body.action === "score") {
      if (!body.userResponse?.trim()) {
        return NextResponse.json(
          { error: "Write your drill response before checking." },
          { status: 400 }
        );
      }

      const score = await scoreMicroDrillWithAI({
        challenge: body.challenge,
        userResponse: body.userResponse.trim(),
      });

      const now = new Date().toISOString();
      if (body.drillId) {
        await db
          .update(schema.microDrills)
          .set({
            userResponse: body.userResponse.trim(),
            scoreJson: JSON.stringify(score),
            completedAt: now,
          })
          .where(eq(schema.microDrills.id, body.drillId));
      }

      return NextResponse.json({ score });
    }

    return NextResponse.json({ error: "Unknown drill action." }, { status: 400 });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Micro-drill request failed." },
      { status: 500 }
    );
  }
}
