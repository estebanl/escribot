import { NextResponse } from "next/server";
import { and, eq, gte, inArray, sql } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { checkRateLimit } from "@/lib/rate-limit";
import { PLANS, isInvitedEmail, type PlanId } from "@/lib/plans";
import { resolveUserAndAIConfig, type AIKeySource } from "@/lib/ai/providers";
import { isBillingEnabled } from "@/lib/billing/stripe";

/** What a request spends: a graded essay, or one of the lighter AI helpers. */
export type AIRequestKind = "evaluation" | "assist";

// ai_usage.feature values that mark one billable unit of each kind.
const EVALUATION_FEATURES = ["rubric_score", "rubric_score_fallback"];
const ASSIST_FEATURES = [
  "outline_check",
  "toolkit",
  "prompt_generation",
  "drill_generate",
  "drill_score",
];

const AI_REQUESTS_PER_MINUTE = 20;

export interface UsageSummary {
  plan: PlanId;
  planLabel: string;
  keySource: AIKeySource;
  canChooseModels: boolean;
  evaluations: { used: number; limit: number | null };
  assists: { used: number; limit: number | null };
  /** Free plan on platform keys is blocked until the email is verified. */
  verificationRequired: boolean;
  /** ISO timestamp of the next monthly reset (UTC). */
  resetsAt: string;
}

function monthBounds(now = new Date()) {
  const start = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
  const next = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1));
  return { start: start.toISOString(), next: next.toISOString() };
}

/**
 * Free AI on the platform's keys needs an account tied to a real address:
 * verified, or invited via ALLOWED_SIGNUP_EMAILS. Otherwise one person could
 * multiply the Free allowance with throwaway sign-ups.
 */
async function isTrustedForFreeUse(userId: string): Promise<boolean> {
  // Local development has no email delivery and no abuse to prevent.
  if (process.env.NODE_ENV !== "production") return true;
  const db = await getDb();
  const [account] = await db
    .select({ email: schema.user.email, emailVerified: schema.user.emailVerified })
    .from(schema.user)
    .where(eq(schema.user.id, userId));
  return Boolean(account && (account.emailVerified || isInvitedEmail(account.email)));
}

/** Optional ceiling on graded essays + coaching requests per UTC day across all Free accounts. */
function getFreeDailyCap(): number | null {
  const raw = Number(process.env.FREE_PLAN_DAILY_AI_CAP);
  return Number.isFinite(raw) && raw > 0 ? Math.floor(raw) : null;
}

async function countFreePlatformUsageToday(): Promise<number> {
  const now = new Date();
  const dayStart = new Date(
    Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate())
  ).toISOString();
  const db = await getDb();
  const [row] = await db
    .select({ value: sql<number>`count(*)` })
    .from(schema.aiUsage)
    .where(
      and(
        eq(schema.aiUsage.plan, "free"),
        eq(schema.aiUsage.keySource, "platform"),
        gte(schema.aiUsage.createdAt, dayStart),
        inArray(schema.aiUsage.feature, [...EVALUATION_FEATURES, ...ASSIST_FEATURES])
      )
    );
  return Number(row?.value ?? 0);
}

async function countPlatformUsage(userId: string, features: string[], since: string) {
  const db = await getDb();
  const [row] = await db
    .select({ value: sql<number>`count(*)` })
    .from(schema.aiUsage)
    .where(
      and(
        eq(schema.aiUsage.userId, userId),
        eq(schema.aiUsage.keySource, "platform"),
        gte(schema.aiUsage.createdAt, since),
        inArray(schema.aiUsage.feature, features)
      )
    );
  return Number(row?.value ?? 0);
}

export async function getUsageSummary(userId: string): Promise<UsageSummary> {
  const cfg = await resolveUserAndAIConfig(userId);
  const limits = PLANS[cfg.plan];
  const { start, next } = monthBounds();
  const [evaluationsUsed, assistsUsed, trusted] = await Promise.all([
    countPlatformUsage(userId, EVALUATION_FEATURES, start),
    countPlatformUsage(userId, ASSIST_FEATURES, start),
    isTrustedForFreeUse(userId),
  ]);

  return {
    plan: cfg.plan,
    planLabel: limits.label,
    keySource: cfg.keySource,
    canChooseModels: limits.canChooseModels,
    evaluations: { used: evaluationsUsed, limit: limits.evaluationsPerMonth },
    assists: { used: assistsUsed, limit: limits.assistsPerMonth },
    verificationRequired: cfg.plan === "free" && cfg.keySource === "platform" && !trusted,
    resetsAt: next,
  };
}

function tooManyRequests(
  error: string,
  code: string,
  retryAfterSeconds?: number,
  extra?: Record<string, unknown>
) {
  return NextResponse.json(
    { error, code, ...extra },
    {
      status: 429,
      headers: retryAfterSeconds ? { "Retry-After": String(retryAfterSeconds) } : undefined,
    }
  );
}

/**
 * Call at the top of every AI route. Returns a 429 response when the user is
 * sending requests too fast or has used up their plan's monthly allowance on
 * the platform keys; returns null when the request may proceed.
 */
export async function guardAiRequest(
  userId: string,
  kind: AIRequestKind,
  units = 1
): Promise<NextResponse | null> {
  const rate = await checkRateLimit(`ai:${userId}`, AI_REQUESTS_PER_MINUTE, 60);
  if (!rate.allowed) {
    return tooManyRequests(
      "You are sending AI requests too quickly. Please wait a moment and try again.",
      "rate_limited",
      rate.retryAfterSeconds
    );
  }

  const cfg = await resolveUserAndAIConfig(userId);
  // Own key: the user pays. No key at all: features fall back to local heuristics.
  if (cfg.keySource !== "platform") return null;

  if (cfg.plan === "free") {
    if (!(await isTrustedForFreeUse(userId))) {
      return NextResponse.json(
        {
          error:
            "Please verify your email address to use AI feedback on the Free plan, or add your own API key in Settings.",
          code: "verification_required",
        },
        { status: 403 }
      );
    }

    const dailyCap = getFreeDailyCap();
    if (dailyCap !== null && (await countFreePlatformUsageToday()) + units > dailyCap) {
      return tooManyRequests(
        "Free AI feedback is at capacity for today. Please try again tomorrow, upgrade to Pro, or add your own API key in Settings.",
        "free_capacity_reached",
        undefined,
        isBillingEnabled() ? { upgradeUrl: "/pricing" } : undefined
      );
    }
  }

  const limits = PLANS[cfg.plan];
  const limit = kind === "evaluation" ? limits.evaluationsPerMonth : limits.assistsPerMonth;
  if (limit === null) return null;

  const { start, next } = monthBounds();
  const used = await countPlatformUsage(
    userId,
    kind === "evaluation" ? EVALUATION_FEATURES : ASSIST_FEATURES,
    start
  );
  if (used + units <= limit) return null;

  const resetDate = next.slice(0, 10);
  const what = kind === "evaluation" ? "graded essays" : "AI coaching requests";
  const remaining = Math.max(0, limit - used);
  const detail =
    remaining > 0 && units > 1
      ? `This submission needs ${units} but only ${remaining} of your ${limit} monthly ${what} remain.`
      : `You have used all ${limit} ${what} included in the ${limits.label} plan this month.`;
  const canUpgrade = cfg.plan === "free" && isBillingEnabled();
  const pro = PLANS.pro;
  const nextStep = canUpgrade
    ? `To keep going now, upgrade to Pro (${
        kind === "evaluation" ? pro.evaluationsPerMonth : pro.assistsPerMonth
      } ${what} a month) on the Pricing page, or add your own API key in Settings.`
    : "To keep going now, add your own API key in Settings.";
  return tooManyRequests(
    `${detail} Your allowance resets on ${resetDate}. ${nextStep}`,
    "quota_exceeded",
    undefined,
    canUpgrade ? { upgradeUrl: "/pricing" } : undefined
  );
}
