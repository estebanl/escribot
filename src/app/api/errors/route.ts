import { NextResponse } from "next/server";
import { headers } from "next/headers";
import { reportError } from "@/lib/errors";
import { getCurrentUser } from "@/lib/session";
import { checkRateLimit } from "@/lib/rate-limit";

/** Receives errors caught by the browser error boundaries. */
export async function POST(request: Request) {
  const user = await getCurrentUser().catch(() => null);
  const h = await headers();
  const ip = h.get("x-forwarded-for")?.split(",")[0]?.trim() || h.get("x-real-ip") || "unknown";

  const rate = await checkRateLimit(`client-error:${user?.id ?? ip}`, 10, 60);
  if (!rate.allowed) return new NextResponse(null, { status: 204 });

  const body = (await request.json().catch(() => null)) as {
    message?: unknown;
    stack?: unknown;
    digest?: unknown;
    path?: unknown;
  } | null;
  if (!body || typeof body.message !== "string") {
    return NextResponse.json({ error: "Invalid report." }, { status: 400 });
  }

  const error = new Error(body.message.slice(0, 1000));
  error.stack = typeof body.stack === "string" ? body.stack.slice(0, 4000) : undefined;
  await reportError({
    source: "client",
    error,
    path: typeof body.path === "string" ? body.path : null,
    digest: typeof body.digest === "string" ? body.digest : null,
    userId: user?.id ?? null,
  });
  return new NextResponse(null, { status: 204 });
}
