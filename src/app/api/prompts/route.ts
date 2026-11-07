import { NextResponse } from "next/server";
import { desc, eq } from "drizzle-orm";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";

export async function GET(request: Request) {
  try {
    const db = await getDb();
    const { searchParams } = new URL(request.url);
    const taskType = searchParams.get("taskType");

    const rows = taskType
      ? await db
          .select()
          .from(schema.prompts)
          .where(eq(schema.prompts.taskType, taskType))
          .orderBy(desc(schema.prompts.createdAt))
      : await db
          .select()
          .from(schema.prompts)
          .orderBy(desc(schema.prompts.createdAt));

    return NextResponse.json({ prompts: rows });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to load prompts." },
      { status: 500 }
    );
  }
}

export async function POST(request: Request) {
  try {
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

    const newPrompt = {
      id,
      taskType: body.taskType,
      category:
        body.category || (body.taskType === "task1" ? "line" : "opinion"),
      topic: body.topic?.trim() || "Custom Practice",
      title:
        body.title?.trim() ||
        `Custom ${body.taskType === "task1" ? "Task 1 Visual" : "Task 2 Essay"}`,
      promptText: body.promptText.trim(),
      chartSpecJson: null,
      imageDataUrl: body.imageDataUrl || null,
      extractedVisualSpec: null,
      source: "custom",
      createdAt: now,
    };

    await db.insert(schema.prompts).values(newPrompt);

    return NextResponse.json({ prompt: newPrompt });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to save custom prompt." },
      { status: 500 }
    );
  }
}
