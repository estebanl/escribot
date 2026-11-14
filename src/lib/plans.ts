/**
 * Plans decide how much of the platform's AI budget an account may use.
 * Limits only apply to requests paid for by the platform's API keys; a user
 * who saves their own key in Settings is never metered.
 */
export type PlanId = "free" | "pro" | "owner";

export interface PlanLimits {
  label: string;
  /** Graded essays per calendar month (UTC). null = unlimited. */
  evaluationsPerMonth: number | null;
  /** Outline checks, toolkit runs, drills and prompt generation per month. */
  assistsPerMonth: number | null;
  /** Whether the provider/model chosen in Settings is honoured on platform keys. */
  canChooseModels: boolean;
}

export const PLANS: Record<PlanId, PlanLimits> = {
  free: {
    label: "Free",
    evaluationsPerMonth: 5,
    assistsPerMonth: 60,
    canChooseModels: false,
  },
  pro: {
    label: "Pro",
    evaluationsPerMonth: 100,
    assistsPerMonth: 1500,
    canChooseModels: true,
  },
  // The deployment owner (OWNER_EMAIL) pays for the platform keys.
  owner: {
    label: "Owner",
    evaluationsPerMonth: null,
    assistsPerMonth: null,
    canChooseModels: true,
  },
};

/**
 * @param storedPlan     user_settings.plan, a manual override ("pro" = complimentary Pro)
 * @param hasPaidPro     whether the user has a Stripe subscription that grants Pro
 */
export function resolvePlan(
  storedPlan: string | null | undefined,
  email: string,
  hasPaidPro = false
): PlanId {
  const ownerEmail = process.env.OWNER_EMAIL?.trim().toLowerCase();
  if (ownerEmail && email.trim().toLowerCase() === ownerEmail) return "owner";
  return hasPaidPro || storedPlan === "pro" ? "pro" : "free";
}
