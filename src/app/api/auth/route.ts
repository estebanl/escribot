import { NextResponse } from "next/server";
import { cookies } from "next/headers";
import crypto from "crypto";
import {
  AUTH_COOKIE_NAME,
  createSessionToken,
  isPasscodeRequired,
  verifySessionToken,
} from "@/lib/auth";

function timingSafeMatch(input: string, expected: string): boolean {
  const a = crypto.createHash("sha256").update(input).digest();
  const b = crypto.createHash("sha256").update(expected).digest();
  return crypto.timingSafeEqual(a, b);
}

export async function GET() {
  const cookieStore = await cookies();
  const token = cookieStore.get(AUTH_COOKIE_NAME)?.value;
  const authenticated = await verifySessionToken(token);
  return NextResponse.json({
    passcodeRequired: isPasscodeRequired(),
    authenticated,
  });
}

export async function POST(request: Request) {
  try {
    const body = (await request.json()) as { passcode?: string };
    const configured = process.env.APP_PASSCODE?.trim();

    if (!configured) {
      return NextResponse.json({ ok: true, passcodeRequired: false });
    }

    if (!body.passcode || !timingSafeMatch(body.passcode.trim(), configured)) {
      return NextResponse.json(
        { error: "Incorrect passcode. Check APP_PASSCODE in your environment." },
        { status: 401 }
      );
    }

    const token = await createSessionToken();
    const cookieStore = await cookies();
    cookieStore.set(AUTH_COOKIE_NAME, token, {
      httpOnly: true,
      secure: process.env.NODE_ENV === "production",
      sameSite: "lax",
      path: "/",
      maxAge: 60 * 60 * 24 * 30, // 30 days
    });

    return NextResponse.json({ ok: true });
  } catch {
    return NextResponse.json({ error: "Invalid request payload." }, { status: 400 });
  }
}

export async function DELETE() {
  const cookieStore = await cookies();
  cookieStore.delete(AUTH_COOKIE_NAME);
  return NextResponse.json({ ok: true });
}
