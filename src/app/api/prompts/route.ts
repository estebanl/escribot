import { NextResponse } from "next/server";
import { and, desc, eq } from "drizzle-orm";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { visiblePrompts } from "@/lib/db/scope";
import { ImageUploadError, storeImage } from "@/lib/storage";
import { getEffectivePlan } from "@/lib/billing/subscription";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { reportError } from "@/lib/errors";

export async function GET(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const db = await getDb();
    const { searchParams } = new URL(request.url);
    const taskType = searchParams.get("taskType");

    const rows = taskType
      ? await db
          .select()
          .from(schema.prompts)
          .where(and(eq(schema.prompts.taskType, taskType), visiblePrompts(user.id)))
          .orderBy(desc(schema.prompts.createdAt))
      : await db
          .select()
          .from(schema.prompts)
          .where(visiblePrompts(user.id))
          .orderBy(desc(schema.prompts.createdAt));

    return NextResponse.json({ prompts: rows });
  } catch (err) {
    await reportError({ source: "server", error: err, path: "/api/prompts" });
    return NextResponse.json(
      { error: "Failed to load prompts." },
      { status: 500 }
    );
  }
}

export async function POST(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const body = (await request.json()) as {
      taskType?: "task1" | "task2";
      category?: string;
      topic?: string;
      title?: string;
      promptText?: string;
      imageDataUrl?: string | null;
    };

    if (!body.taskType || !body.promptText?.trim()) {
      return NextResponse.json(
        { error: "Task type and prompt instructions are required." },
        { status: 400 }
      );
    }

    const db = await getDb();
    const id = `custom-${crypto.randomUUID().slice(0, 8)}`;
    const now = new Date().toISOString();

    // Chart screenshots go to object storage; the prompt keeps only a link.
    let imageRef: string | null = null;
    if (body.taskType === "task1" && body.imageDataUrl) {
      try {
        imageRef = await storeImage(
          db,
          user.id,
          await getEffectivePlan(db, user.id),
          body.imageDataUrl
        );
      } catch (err) {
        if (err instanceof ImageUploadError) {
          return NextResponse.json({ error: err.message }, { status: err.status });
        }
        throw err;
      }
    }

    const newPrompt = {
      id,
      userId: user.id,
      taskType: body.taskType,
      category:
        body.category || (body.taskType === "task1" ? "line" : "opinion"),
      topic: body.topic?.trim() || "Custom Practice",
      title:
        body.title?.trim() ||
        `Custom ${body.taskType === "task1" ? "Task 1 Visual" : "Task 2 Essay"}`,
      promptText: body.promptText.trim(),
      chartSpecJson: null,
      imageDataUrl: imageRef,
      extractedVisualSpec: null,
      source: "custom",
      createdAt: now,
    };

    await db.insert(schema.prompts).values(newPrompt);

    return NextResponse.json({ prompt: newPrompt });
  } catch (err) {
    await reportError({ source: "server", error: err, path: "/api/prompts" });
    return NextResponse.json(
      { error: "Failed to save custom prompt." },
      { status: 500 }
    );
  }
}
