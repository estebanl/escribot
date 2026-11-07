import { NextResponse } from "next/server";
import { getDb, schema } from "@/lib/db";

export async function GET() {
  try {
    const db = await getDb();
    const prompts = await db.select().from(schema.prompts);
    const sessions = await db.select().from(schema.writingSessions);
    const drafts = await db.select().from(schema.drafts);
    const evaluations = await db.select().from(schema.evaluations);
    const vaultItems = await db.select().from(schema.vaultItems);
    const microDrills = await db.select().from(schema.microDrills);

    return NextResponse.json({
      exportedAt: new Date().toISOString(),
      app: "FluentEscritor",
      version: 1,
      data: {
        prompts: prompts.filter((p) => p.source !== "curated"),
        sessions,
        drafts,
        evaluations,
        vaultItems,
        microDrills,
      },
    });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Export failed." },
      { status: 500 }
    );
  }
}

export async function POST(request: Request) {
  try {
    const payload = (await request.json()) as {
      data?: {
        prompts?: Array<typeof schema.prompts.$inferInsert>;
        sessions?: Array<typeof schema.writingSessions.$inferInsert>;
        drafts?: Array<typeof schema.drafts.$inferInsert>;
        evaluations?: Array<typeof schema.evaluations.$inferInsert>;
        vaultItems?: Array<typeof schema.vaultItems.$inferInsert>;
        microDrills?: Array<typeof schema.microDrills.$inferInsert>;
      };
    };

    if (!payload?.data) {
      return NextResponse.json({ error: "Invalid backup JSON format." }, { status: 400 });
    }

    const db = await getDb();
    let importedCount = 0;

    for (const p of payload.data.prompts || []) {
      await db.insert(schema.prompts).values(p).onConflictDoNothing();
      importedCount++;
    }
    for (const s of payload.data.sessions || []) {
      await db.insert(schema.writingSessions).values(s).onConflictDoNothing();
      importedCount++;
    }
    for (const d of payload.data.drafts || []) {
      await db.insert(schema.drafts).values(d).onConflictDoNothing();
      importedCount++;
    }
    for (const e of payload.data.evaluations || []) {
      await db.insert(schema.evaluations).values(e).onConflictDoNothing();
      importedCount++;
    }
    for (const v of payload.data.vaultItems || []) {
      await db.insert(schema.vaultItems).values(v).onConflictDoNothing();
      importedCount++;
    }
    for (const m of payload.data.microDrills || []) {
      await db.insert(schema.microDrills).values(m).onConflictDoNothing();
      importedCount++;
    }

    return NextResponse.json({ ok: true, importedCount });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Import failed." },
      { status: 500 }
    );
  }
}
