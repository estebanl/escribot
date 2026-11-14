import { NextResponse } from "next/server";
import { eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { getOrCreateSettings } from "@/lib/db/scope";
import { getAdminUser } from "@/lib/session";

/** Grants or removes complimentary Pro (user_settings.plan). Paid subscriptions are unaffected. */
export async function PATCH(request: Request, context: { params: Promise<{ id: string }> }) {
  const admin = await getAdminUser();
  if (!admin) return NextResponse.json({ error: "Not found." }, { status: 404 });

  const { id } = await context.params;
  const body = (await request.json().catch(() => ({}))) as { plan?: string };
  if (body.plan !== "pro" && body.plan !== "free") {
    return NextResponse.json({ error: "plan must be 'pro' or 'free'." }, { status: 400 });
  }

  const db = await getDb();
  const [target] = await db
    .select({ id: schema.user.id })
    .from(schema.user)
    .where(eq(schema.user.id, id));
  if (!target) return NextResponse.json({ error: "User not found." }, { status: 404 });

  await getOrCreateSettings(db, id);
  await db
    .update(schema.userSettings)
    .set({ plan: body.plan, updatedAt: new Date().toISOString() })
    .where(eq(schema.userSettings.userId, id));

  return NextResponse.json({ ok: true, plan: body.plan });
}
