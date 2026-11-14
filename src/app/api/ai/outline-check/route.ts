import { NextResponse } from "next/server";
import { and, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { getVisiblePrompt } from "@/lib/db/scope";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { guardAiRequest } from "@/lib/ai/access";
import { resolveImageForAI } from "@/lib/storage";
import { checkOutlineWithAI, extractVisualSpecFromImage } from "@/lib/ai/examiner";
import type { OutlineInput, TaskType } from "@/lib/types";
import { reportError } from "@/lib/errors";

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

    const blocked = await guardAiRequest(user.id, "assist");
    if (blocked) return blocked;

    let visualContext: string | null = null;
    if (body.promptId) {
      const db = await getDb();
      const prompt = await getVisiblePrompt(db, user.id, body.promptId);

      if (prompt) {
        if (prompt.chartSpecJson) {
          visualContext = prompt.chartSpecJson;
        } else if (prompt.extractedVisualSpec) {
          visualContext = prompt.extractedVisualSpec;
        } else if (prompt.userId === user.id && prompt.imageDataUrl) {
          const imageForAI = await resolveImageForAI(db, user.id, prompt.imageDataUrl);
          if (imageForAI) {
            visualContext = await extractVisualSpecFromImage(
              imageForAI,
              prompt.promptText,
              user.id
            );
            // Cache on the user's own prompt (never on shared curated rows).
            await db
              .update(schema.prompts)
              .set({ extractedVisualSpec: visualContext })
              .where(and(eq(schema.prompts.id, prompt.id), eq(schema.prompts.userId, user.id)));
          }
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
    await reportError({ source: "server", error: err, path: "/api/ai/outline-check" });
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Outline validation failed." },
      { status: 500 }
    );
  }
}
