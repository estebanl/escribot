import { NextResponse } from "next/server";
import { and, eq, isNotNull, like } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { getAdminUser } from "@/lib/session";
import { migrateInlineImage } from "@/lib/storage";

const BATCH = 25;

/**
 * Moves chart images saved inline (base64 in prompts.image_data_url, before
 * object storage) into storage, a batch at a time. Call until `remaining` is 0.
 */
export async function POST() {
  const admin = await getAdminUser();
  if (!admin) return NextResponse.json({ error: "Not found." }, { status: 404 });

  const db = await getDb();
  const inline = and(
    isNotNull(schema.prompts.userId),
    like(schema.prompts.imageDataUrl, "data:%")
  );
  const rows = await db
    .select({
      id: schema.prompts.id,
      userId: schema.prompts.userId,
      imageDataUrl: schema.prompts.imageDataUrl,
    })
    .from(schema.prompts)
    .where(inline)
    .limit(BATCH);

  let moved = 0;
  let failed = 0;
  for (const row of rows) {
    const ref = await migrateInlineImage(db, row.userId!, row.imageDataUrl!);
    if (!ref) {
      failed++;
      continue;
    }
    await db
      .update(schema.prompts)
      .set({ imageDataUrl: ref })
      .where(eq(schema.prompts.id, row.id));
    moved++;
  }

  const remaining = (await db.select({ id: schema.prompts.id }).from(schema.prompts).where(inline))
    .length;
  return NextResponse.json({ moved, failed, remaining });
}
