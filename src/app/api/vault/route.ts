import { NextResponse } from "next/server";
import { and, desc, eq } from "drizzle-orm";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { reportError } from "@/lib/errors";

export async function GET() {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const db = await getDb();
    const items = await db
      .select()
      .from(schema.vaultItems)
      .where(eq(schema.vaultItems.userId, user.id))
      .orderBy(desc(schema.vaultItems.createdAt));

    return NextResponse.json({ items });
  } catch (err) {
    await reportError({ source: "server", error: err, path: "/api/vault" });
    return NextResponse.json(
      { error: "Failed to load Vault." },
      { status: 500 }
    );
  }
}

export async function POST(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const body = (await request.json()) as {
      itemType?: "error" | "collocation";
      criterion?: string;
      originalText?: string;
      upgradedText?: string;
      explanation?: string;
      exampleSentence?: string;
    };

    if (!body.originalText?.trim() || !body.upgradedText?.trim()) {
      return NextResponse.json(
        { error: "Original phrase and upgraded academic phrasing are required." },
        { status: 400 }
      );
    }

    const db = await getDb();
    const now = new Date().toISOString();
    const record = {
      id: `vault-${crypto.randomUUID().slice(0, 9)}`,
      userId: user.id,
      sourceDraftId: null,
      itemType: body.itemType === "collocation" ? "collocation" : "error",
      criterion: body.criterion || "LR",
      originalText: body.originalText.trim(),
      upgradedText: body.upgradedText.trim(),
      explanation: body.explanation?.trim() || "Saved manually to personal Vault.",
      exampleSentence: body.exampleSentence?.trim() || body.upgradedText.trim(),
      masteryLevel: 0,
      reviewCount: 0,
      nextReviewAt: now,
      createdAt: now,
    };

    await db.insert(schema.vaultItems).values(record);
    return NextResponse.json({ item: record });
  } catch (err) {
    await reportError({ source: "server", error: err, path: "/api/vault" });
    return NextResponse.json(
      { error: "Failed to add Vault item." },
      { status: 500 }
    );
  }
}

export async function PATCH(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const body = (await request.json()) as {
      id?: string;
      masteryLevel?: number;
    };
    if (!body.id || typeof body.masteryLevel !== "number") {
      return NextResponse.json({ error: "Item id and masteryLevel required." }, { status: 400 });
    }

    const clamped = Math.max(0, Math.min(3, Math.round(body.masteryLevel)));
    const db = await getDb();
    const [existing] = await db
      .select()
      .from(schema.vaultItems)
      .where(and(eq(schema.vaultItems.id, body.id), eq(schema.vaultItems.userId, user.id)));

    if (!existing) {
      return NextResponse.json({ error: "Vault item not found." }, { status: 404 });
    }

    const daysAhead = clamped === 0 ? 0 : clamped === 1 ? 1 : clamped === 2 ? 3 : 7;
    const nextDate = new Date(Date.now() + daysAhead * 86_400_000).toISOString();

    await db
      .update(schema.vaultItems)
      .set({
        masteryLevel: clamped,
        reviewCount: existing.reviewCount + 1,
        nextReviewAt: nextDate,
      })
      .where(and(eq(schema.vaultItems.id, body.id), eq(schema.vaultItems.userId, user.id)));

    return NextResponse.json({ ok: true, masteryLevel: clamped, nextReviewAt: nextDate });
  } catch (err) {
    await reportError({ source: "server", error: err, path: "/api/vault" });
    return NextResponse.json(
      { error: "Failed to update Vault item." },
      { status: 500 }
    );
  }
}

export async function DELETE(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const { searchParams } = new URL(request.url);
    const id = searchParams.get("id");
    if (!id) {
      return NextResponse.json({ error: "Missing id." }, { status: 400 });
    }
    const db = await getDb();
    await db
      .delete(schema.vaultItems)
      .where(and(eq(schema.vaultItems.id, id), eq(schema.vaultItems.userId, user.id)));
    return NextResponse.json({ ok: true });
  } catch (err) {
    await reportError({ source: "server", error: err, path: "/api/vault" });
    return NextResponse.json(
      { error: "Failed to delete Vault item." },
      { status: 500 }
    );
  }
}
