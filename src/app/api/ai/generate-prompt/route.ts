import { NextResponse } from "next/server";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { guardAiRequest } from "@/lib/ai/access";
import { generatePromptWithAI } from "@/lib/ai/examiner";
import type { PromptCategory, TaskType } from "@/lib/types";
import { reportError } from "@/lib/errors";

export const maxDuration = 60;

export async function POST(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const body = (await request.json()) as {
      taskType?: TaskType;
      category?: PromptCategory;
      topic?: string;
    };

    const taskType: TaskType = body.taskType === "task1" ? "task1" : "task2";
    const category: PromptCategory =
      body.category || (taskType === "task1" ? "line" : "opinion");

    const blocked = await guardAiRequest(user.id, "assist");
    if (blocked) return blocked;

    const generated = await generatePromptWithAI({
      userId: user.id,
      taskType,
      category,
      topic: body.topic,
    });

    const db = await getDb();
    const id = `ai-${crypto.randomUUID().slice(0, 8)}`;
    const now = new Date().toISOString();

    const record = {
      id,
      userId: user.id,
      taskType,
      category,
      topic: generated.topic,
      title: generated.title,
      promptText: generated.promptText,
      chartSpecJson: generated.chartSpec ? JSON.stringify(generated.chartSpec) : null,
      imageDataUrl: null,
      extractedVisualSpec: null,
      source: "ai_generated",
      createdAt: now,
    };

    await db.insert(schema.prompts).values(record);

    return NextResponse.json({ prompt: record });
  } catch (err) {
    await reportError({ source: "server", error: err, path: "/api/ai/generate-prompt" });
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to generate prompt." },
      { status: 500 }
    );
  }
}
