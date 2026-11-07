import { NextResponse } from "next/server";
import { asc, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";

export async function GET(
  _request: Request,
  context: { params: Promise<{ id: string }> }
) {
  try {
    const { id } = await context.params;
    const db = await getDb();

    const [session] = await db
      .select()
      .from(schema.writingSessions)
      .where(eq(schema.writingSessions.id, id));

    if (!session) {
      return NextResponse.json({ error: "Writing session not found." }, { status: 404 });
    }

    const task1Prompt = session.task1PromptId
      ? (
          await db
            .select()
            .from(schema.prompts)
            .where(eq(schema.prompts.id, session.task1PromptId))
        )[0] || null
      : null;

    const task2Prompt = session.task2PromptId
      ? (
          await db
            .select()
            .from(schema.prompts)
            .where(eq(schema.prompts.id, session.task2PromptId))
        )[0] || null
      : null;

    const sessionDrafts = await db
      .select()
      .from(schema.drafts)
      .where(eq(schema.drafts.sessionId, id))
      .orderBy(asc(schema.drafts.version));

    const draftEvals = await Promise.all(
      sessionDrafts.map(async (d) => {
        const [ev] = await db
          .select()
          .from(schema.evaluations)
          .where(eq(schema.evaluations.draftId, d.id));
        const [prompt] = await db
          .select()
          .from(schema.prompts)
          .where(eq(schema.prompts.id, d.promptId));
        return {
          ...d,
          prompt: prompt || null,
          evaluation: ev || null,
        };
      })
    );

    return NextResponse.json({
      session: {
        ...session,
        task1Prompt,
        task2Prompt,
        drafts: draftEvals,
      },
    });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to fetch session." },
      { status: 500 }
    );
  }
}
