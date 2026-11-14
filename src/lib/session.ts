import { cache } from "react";
import { headers } from "next/headers";
import { redirect } from "next/navigation";
import { NextResponse } from "next/server";
import { getAuth } from "@/lib/auth";

export interface SessionUser {
  id: string;
  name: string;
  email: string;
}

/** Validates the session cookie against the database (deduped per request). */
export const getCurrentUser = cache(async (): Promise<SessionUser | null> => {
  const auth = await getAuth();
  const session = await auth.api.getSession({ headers: await headers() });
  if (!session) return null;
  return { id: session.user.id, name: session.user.name, email: session.user.email };
});

/** For server pages: returns the signed-in user or redirects to /login. */
export async function requireUser(): Promise<SessionUser> {
  const user = await getCurrentUser();
  if (!user) redirect("/login");
  return user;
}

/** For API routes: `const user = await getCurrentUser(); if (!user) return unauthorized();` */
export function unauthorized() {
  return NextResponse.json({ error: "Unauthorized — please sign in." }, { status: 401 });
}
