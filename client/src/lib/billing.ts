export type BillingInterval = "month" | "year";

export interface PriceDisplay {
  unitAmount: number; // minor units (cents)
  currency: string;
  interval: BillingInterval;
}
