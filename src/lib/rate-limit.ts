import { sql } from "drizzle-orm";
import { getDb } from "@/lib/db";

export interface RateLimitResult {
  allowed: boolean;
  retryAfterSeconds: number;
}

/**
 * Fixed-window rate limiter backed by the database, so the limit holds across
 * serverless instances. One atomic upsert per call.
 */
export async function checkRateLimit(
  key: string,
  limit: number,
  windowSeconds: number
): Promise<RateLimitResult> {
  const nowSeconds = Math.floor(Date.now() / 1000);
  const windowStart = nowSeconds - (nowSeconds % windowSeconds);

  const db = await getDb();
  const result = await db.run(sql`
    INSERT INTO api_rate_limits (key, window_start, count)
    VALUES (${key}, ${windowStart}, 1)
    ON CONFLICT (key) DO UPDATE SET
      count = CASE WHEN window_start = excluded.window_start THEN count + 1 ELSE 1 END,
      window_start = excluded.window_start
    RETURNING count
  `);

  const count = Number(result.rows[0]?.count ?? 1);
  return {
    allowed: count <= limit,
    retryAfterSeconds: Math.max(1, windowStart + windowSeconds - nowSeconds),
  };
}
