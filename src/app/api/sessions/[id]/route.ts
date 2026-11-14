import { NextResponse } from "next/server";
import { and, asc, eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { getOwnedSession, getVisiblePrompt } from "@/lib/db/scope";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { reportError } from "@/lib/errors";

export async function GET(
  _request: Request,
  context: { params: Promise<{ id: string }> }
) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();

    const { id } = await context.params;
    const db = await getDb();

    const session = await getOwnedSession(db, user.id, id);
    if (!session) {
      return NextResponse.json({ error: "Writing session not found." }, { status: 404 });
    }

    const task1Prompt = session.task1PromptId
      ? await getVisiblePrompt(db, user.id, session.task1PromptId)
      : null;
    const task2Prompt = session.task2PromptId
      ? await getVisiblePrompt(db, user.id, session.task2PromptId)
      : null;

    const sessionDrafts = await db
      .select()
      .from(schema.drafts)
      .where(and(eq(schema.drafts.sessionId, id), eq(schema.drafts.userId, user.id)))
      .orderBy(asc(schema.drafts.version));

    const draftEvals = await Promise.all(
      sessionDrafts.map(async (d) => {
        const [ev] = await db
          .select()
          .from(schema.evaluations)
          .where(
            and(
              eq(schema.evaluations.draftId, d.id),
              eq(schema.evaluations.userId, user.id)
            )
          );
        return {
          ...d,
          prompt: await getVisiblePrompt(db, user.id, d.promptId),
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
    await reportError({ source: "server", error: err, path: "/api/sessions/[id]" });
    return NextResponse.json(
      { error: "Failed to fetch session." },
      { status: 500 }
    );
  }
}
