import { NextResponse } from "next/server";
import { getDb } from "@/lib/db";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { getOwnedImage } from "@/lib/storage";

/** Streams an uploaded image to its owner only. */
export async function GET(_request: Request, context: { params: Promise<{ id: string }> }) {
  const user = await getCurrentUser();
  if (!user) return unauthorized();

  const { id } = await context.params;
  const image = await getOwnedImage(await getDb(), user.id, id);
  if (!image) {
    return NextResponse.json({ error: "Image not found." }, { status: 404 });
  }

  return new Response(new Uint8Array(image.data), {
    headers: {
      "Content-Type": image.contentType,
      "Content-Length": String(image.data.length),
      // Private to this browser; ids are immutable so caching is safe.
      "Cache-Control": "private, max-age=86400, immutable",
      "X-Content-Type-Options": "nosniff",
    },
  });
}
