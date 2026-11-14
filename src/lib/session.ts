import { cache } from "react";
import { headers } from "next/headers";
import { redirect } from "next/navigation";
import { NextResponse } from "next/server";
import { getAuth } from "@/lib/auth";
import { getDb } from "@/lib/db";
import { getEffectivePlan } from "@/lib/billing/subscription";
import { claimLegacyDataIfOwner } from "@/lib/db/scope";
import { isBillingEnabled } from "@/lib/billing/stripe";
import type { PlanId } from "@/lib/plans";

export interface SessionUser {
  id: string;
  name: string;
  email: string;
  emailVerified: boolean;
}

/** Validates the session cookie against the database (deduped per request). */
export const getCurrentUser = cache(async (): Promise<SessionUser | null> => {
  const auth = await getAuth();
  const session = await auth.api.getSession({ headers: await headers() });
  if (!session) return null;
  return {
    id: session.user.id,
    name: session.user.name,
    email: session.user.email,
    emailVerified: session.user.emailVerified,
  };
});

export interface PageUser extends SessionUser {
  plan: PlanId;
  /** Show upgrade prompts: billing is configured and the user is on Free. */
  canUpgrade: boolean;
}

/** For server pages: returns the signed-in user (with plan) or redirects to /login. */
export async function requireUser(): Promise<PageUser> {
  const user = await getCurrentUser();
  if (!user) redirect("/login");
  const db = await getDb();
  // Pre-multi-user data moves to the owner once their address is verified.
  await claimLegacyDataIfOwner(db, user);
  const plan = await getEffectivePlan(db, user.id);
  return { ...user, plan, canUpgrade: plan === "free" && isBillingEnabled() };
}

/** For API routes: `const user = await getCurrentUser(); if (!user) return unauthorized();` */
export function unauthorized() {
  return NextResponse.json({ error: "Unauthorized — please sign in." }, { status: 401 });
}
