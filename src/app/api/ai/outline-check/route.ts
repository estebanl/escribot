import { NextResponse } from "next/server";
import { eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { getVisiblePrompt } from "@/lib/db/scope";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { checkOutlineWithAI, extractVisualSpecFromImage } from "@/lib/ai/examiner";
import type { OutlineInput, TaskType } from "@/lib/types";

export async function POST(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const body = (await request.json()) as {
      promptId?: string;
      taskType?: TaskType;
      promptText?: string;
      outline?: OutlineInput;
    };

    if (!body.outline || !body.promptText) {
      return NextResponse.json(
        { error: "Outline fields and prompt text are required." },
        { status: 400 }
      );
    }

    let visualContext: string | null = null;
    if (body.promptId) {
      const db = await getDb();
      const prompt = await getVisiblePrompt(db, user.id, body.promptId);

      if (prompt) {
        if (prompt.chartSpecJson) {
          visualContext = prompt.chartSpecJson;
        } else if (prompt.extractedVisualSpec) {
          visualContext = prompt.extractedVisualSpec;
        } else if (prompt.imageDataUrl) {
          visualContext = await extractVisualSpecFromImage(
            prompt.imageDataUrl,
            prompt.promptText,
            user.id
          );
          await db
            .update(schema.prompts)
            .set({ extractedVisualSpec: visualContext })
            .where(eq(schema.prompts.id, prompt.id));
        }
      }
    }

    const feedback = await checkOutlineWithAI({
      userId: user.id,
      taskType: body.taskType === "task1" ? "task1" : "task2",
      promptText: body.promptText,
      visualContext,
      outline: body.outline,
    });

    return NextResponse.json({ feedback });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Outline validation failed." },
      { status: 500 }
    );
  }
}
