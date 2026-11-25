import { loadJson } from "@/lib/api";

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
  /** Whether Task 1 images can be AI-transformed into clean SVGs, and SVGs uploaded as SVGs. */
  canTransformSvg: boolean;
  /** Total size of uploaded chart images. null = unlimited. */
  storageBytes: number | null;
}

/** Every plan's limits, keyed by plan. */
export type PlanCatalog = Record<PlanId, PlanLimits>;

/**
 * The limits in force, from the server's public config (GET /api/config). The monthly
 * allowances are set per deployment, so they are never hard-coded here; the server
 * enforces them whatever this page shows.
 */
export async function loadPlans(request: Request): Promise<PlanCatalog> {
  const config = await loadJson<{ plans: PlanCatalog }>("/api/config", request);
  return config.plans;
}

/** Plans that can transform Task 1 images into SVGs. Fixed per plan, so it needs no request. */
export function canTransformSvg(plan: PlanId): boolean {
  return plan !== "free";
}
