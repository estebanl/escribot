import { NextResponse } from "next/server";
import { and, desc, eq } from "drizzle-orm";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { guardAiRequest } from "@/lib/ai/access";
import { generateMicroDrillWithAI, scoreMicroDrillWithAI } from "@/lib/ai/examiner";
import { isDrillLevel } from "@/lib/ielts-utils";
import type { DrillLevel, DrillType, MicroDrillChallenge } from "@/lib/types";

export async function GET() {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const db = await getDb();
    const drills = await db
      .select()
      .from(schema.microDrills)
      .where(eq(schema.microDrills.userId, user.id))
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
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const body = (await request.json()) as
      | { action: "generate"; drillType: DrillType; level?: DrillLevel }
      | {
          action: "score";
          drillId: string;
          challenge: MicroDrillChallenge;
          userResponse: string;
          level?: DrillLevel;
        };

    const db = await getDb();
    const level: DrillLevel = isDrillLevel(body.level) ? body.level : "advanced";

    if (body.action === "generate") {
      const blocked = await guardAiRequest(user.id, "assist");
      if (blocked) return blocked;

      const vaultRows = await db
        .select()
        .from(schema.vaultItems)
        .where(eq(schema.vaultItems.userId, user.id))
        .orderBy(desc(schema.vaultItems.createdAt))
        .limit(5);

      const vaultHighlights = vaultRows.map(
        (v) => `${v.originalText} -> ${v.upgradedText}`
      );

      const challenge = await generateMicroDrillWithAI({
        userId: user.id,
        drillType: body.drillType || "cohesion",
        level,
        // Beginners get drills built from simple texts, not their advanced Vault phrases.
        vaultHighlights: level === "beginner" ? [] : vaultHighlights,
      });

      const id = `drill-${crypto.randomUUID().slice(0, 9)}`;
      const now = new Date().toISOString();

      await db.insert(schema.microDrills).values({
        id,
        userId: user.id,
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

      const blocked = await guardAiRequest(user.id, "assist");
      if (blocked) return blocked;

      const score = await scoreMicroDrillWithAI({
        userId: user.id,
        challenge: body.challenge,
        userResponse: body.userResponse.trim(),
        // Fall back to the level the challenge was generated at.
        level: isDrillLevel(body.level) ? body.level : body.challenge?.level,
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
          .where(
            and(
              eq(schema.microDrills.id, body.drillId),
              eq(schema.microDrills.userId, user.id)
            )
          );
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
