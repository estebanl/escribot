import { NextResponse } from "next/server";
import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { generatePromptWithAI } from "@/lib/ai/examiner";
import type { PromptCategory, TaskType } from "@/lib/types";

export const maxDuration = 60;

export async function POST(request: Request) {
  try {
    const body = (await request.json()) as {
      taskType?: TaskType;
      category?: PromptCategory;
      topic?: string;
    };

    const taskType: TaskType = body.taskType === "task1" ? "task1" : "task2";
    const category: PromptCategory =
      body.category || (taskType === "task1" ? "line" : "opinion");

    const generated = await generatePromptWithAI({
      taskType,
      category,
      topic: body.topic,
    });

    const db = await getDb();
    const id = `ai-${crypto.randomUUID().slice(0, 8)}`;
    const now = new Date().toISOString();

    const record = {
      id,
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
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to generate prompt." },
      { status: 500 }
    );
  }
}
