import { NextResponse } from "next/server";
import { runToolkitWithAI } from "@/lib/ai/examiner";
import type { ToolkitRequest } from "@/lib/types";

export async function POST(request: Request) {
  try {
    const body = (await request.json()) as Partial<ToolkitRequest>;

    if (!body.tool || !body.selectedText?.trim() || !body.promptText) {
      return NextResponse.json(
        { error: "Select a phrase, sentence, or paragraph to inspect first." },
        { status: 400 }
      );
    }

    const result = await runToolkitWithAI({
      tool: body.tool,
      taskType: body.taskType === "task1" ? "task1" : "task2",
      promptText: body.promptText,
      selectedText: body.selectedText.trim(),
      fullEssayContext: body.fullEssayContext,
    });

    return NextResponse.json({ result });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Toolkit check failed." },
      { status: 500 }
    );
  }
}
