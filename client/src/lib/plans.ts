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

export const PLANS: Record<PlanId, PlanLimits> = {
  free: {
    label: "Free",
    evaluationsPerMonth: 5,
    assistsPerMonth: 60,
    canChooseModels: false,
    canTransformSvg: false,
    storageBytes: 25 * 1024 * 1024,
  },
  pro: {
    label: "Pro",
    evaluationsPerMonth: 100,
    assistsPerMonth: 1500,
    canChooseModels: true,
    canTransformSvg: true,
    storageBytes: 250 * 1024 * 1024,
  },
  // The deployment owner (OWNER_EMAIL) pays for the platform keys.
  owner: {
    label: "Owner",
    evaluationsPerMonth: null,
    assistsPerMonth: null,
    canChooseModels: true,
    canTransformSvg: true,
    storageBytes: null,
  },
};
