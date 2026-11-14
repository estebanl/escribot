import { sqliteTable, text, integer, real, index } from "drizzle-orm/sqlite-core";

// ---------------------------------------------------------------------------
// Authentication tables (managed by Better Auth)
// ---------------------------------------------------------------------------

export const user = sqliteTable("user", {
  id: text("id").primaryKey(),
  name: text("name").notNull(),
  email: text("email").notNull().unique(),
  emailVerified: integer("email_verified", { mode: "boolean" }).notNull().default(false),
  image: text("image"),
  createdAt: integer("created_at", { mode: "timestamp_ms" }).notNull(),
  updatedAt: integer("updated_at", { mode: "timestamp_ms" }).notNull(),
});

export const session = sqliteTable(
  "session",
  {
    id: text("id").primaryKey(),
    expiresAt: integer("expires_at", { mode: "timestamp_ms" }).notNull(),
    token: text("token").notNull().unique(),
    createdAt: integer("created_at", { mode: "timestamp_ms" }).notNull(),
    updatedAt: integer("updated_at", { mode: "timestamp_ms" }).notNull(),
    ipAddress: text("ip_address"),
    userAgent: text("user_agent"),
    userId: text("user_id")
      .notNull()
      .references(() => user.id, { onDelete: "cascade" }),
  },
  (t) => [index("idx_session_user").on(t.userId)]
);

export const account = sqliteTable(
  "account",
  {
    id: text("id").primaryKey(),
    accountId: text("account_id").notNull(),
    providerId: text("provider_id").notNull(),
    userId: text("user_id")
      .notNull()
      .references(() => user.id, { onDelete: "cascade" }),
    accessToken: text("access_token"),
    refreshToken: text("refresh_token"),
    idToken: text("id_token"),
    accessTokenExpiresAt: integer("access_token_expires_at", { mode: "timestamp_ms" }),
    refreshTokenExpiresAt: integer("refresh_token_expires_at", { mode: "timestamp_ms" }),
    scope: text("scope"),
    password: text("password"),
    createdAt: integer("created_at", { mode: "timestamp_ms" }).notNull(),
    updatedAt: integer("updated_at", { mode: "timestamp_ms" }).notNull(),
  },
  (t) => [index("idx_account_user").on(t.userId)]
);

export const verification = sqliteTable(
  "verification",
  {
    id: text("id").primaryKey(),
    identifier: text("identifier").notNull(),
    value: text("value").notNull(),
    expiresAt: integer("expires_at", { mode: "timestamp_ms" }).notNull(),
    createdAt: integer("created_at", { mode: "timestamp_ms" }).notNull(),
    updatedAt: integer("updated_at", { mode: "timestamp_ms" }).notNull(),
  },
  (t) => [index("idx_verification_identifier").on(t.identifier)]
);

/**
 * Stripe billing state, one row per user (one Stripe customer per user).
 * Written only by the Stripe webhook, except stripe_customer_id which is set
 * when the user first starts a checkout.
 */
export const subscriptions = sqliteTable("subscriptions", {
  userId: text("user_id").primaryKey(),
  stripeCustomerId: text("stripe_customer_id").notNull().unique(),
  stripeSubscriptionId: text("stripe_subscription_id"),
  status: text("status"), // Stripe subscription status; NULL until the first subscription
  priceId: text("price_id"),
  currentPeriodEnd: text("current_period_end"),
  cancelAtPeriodEnd: integer("cancel_at_period_end", { mode: "boolean" })
    .notNull()
    .default(false),
  // `created` (unix seconds) of the last Stripe event applied, to ignore stale or out-of-order deliveries.
  lastEventAt: integer("last_event_at").notNull().default(0),
  updatedAt: text("updated_at").notNull(),
});

/** Better Auth's rate-limit counters for the /api/auth endpoints. */
export const rateLimit = sqliteTable("rate_limit", {
  id: text("id").primaryKey(),
  key: text("key").notNull().unique(),
  count: integer("count").notNull(),
  lastRequest: integer("last_request").notNull(),
});

/** Fixed-window counters for our own API rate limits (src/lib/rate-limit.ts). */
export const apiRateLimits = sqliteTable("api_rate_limits", {
  key: text("key").primaryKey(),
  windowStart: integer("window_start").notNull(),
  count: integer("count").notNull(),
});

// ---------------------------------------------------------------------------
// Application tables. Every row is owned by a user via `userId`; the only
// shared rows are curated prompts, which have a NULL `userId`.
// Rows created before multi-user support carry LEGACY_USER_ID until the
// owner account (OWNER_EMAIL) signs up and claims them.
// ---------------------------------------------------------------------------

export const LEGACY_USER_ID = "legacy";

export const userSettings = sqliteTable("user_settings", {
  userId: text("user_id").primaryKey(),
  currentBand: real("current_band").notNull().default(6.5),
  targetBand: real("target_band").notNull().default(7.5),
  examDate: text("exam_date"),
  nativeLanguage: text("native_language").notNull().default("es"),
  l1CoachingEnabled: integer("l1_coaching_enabled", { mode: "boolean" })
    .notNull()
    .default(true),
  activeProvider: text("active_provider").notNull().default("openrouter"),
  graderModel: text("grader_model").notNull().default("anthropic/claude-sonnet-5.5"),
  fastModel: text("fast_model").notNull().default("deepseek/deepseek-v4.1-flash-1017"),
  visionModel: text("vision_model")
    .notNull()
    .default("deepseek/deepseek-v4.1-flash-1017"),
  encryptedApiKeys: text("encrypted_api_keys").notNull().default("{}"),
  // Manual override ("pro" = complimentary Pro). Paid Pro comes from `subscriptions`.
  plan: text("plan").notNull().default("free"),
  onboardedAt: text("onboarded_at"), // NULL until the welcome questions are answered
  updatedAt: text("updated_at").notNull(),
});

export const prompts = sqliteTable("prompts", {
  id: text("id").primaryKey(),
  userId: text("user_id"), // NULL = curated prompt shared by all users
  taskType: text("task_type").notNull(), // "task1" | "task2"
  category: text("category").notNull(),
  topic: text("topic").notNull(),
  title: text("title").notNull(),
  promptText: text("prompt_text").notNull(),
  chartSpecJson: text("chart_spec_json"),
  imageDataUrl: text("image_data_url"),
  extractedVisualSpec: text("extracted_visual_spec"),
  source: text("source").notNull().default("curated"), // "curated" | "ai_generated" | "custom"
  createdAt: text("created_at").notNull(),
}, (t) => [index("idx_prompts_task").on(t.taskType), index("idx_prompts_user").on(t.userId)]);

export const writingSessions = sqliteTable("writing_sessions", {
  id: text("id").primaryKey(),
  userId: text("user_id").notNull(),
  mode: text("mode").notNull(), // "coach" | "simulator_t1" | "simulator_t2" | "simulator_full_60m"
  status: text("status").notNull().default("in_progress"), // "in_progress" | "completed"
  task1PromptId: text("task1_prompt_id"),
  task2PromptId: text("task2_prompt_id"),
  startedAt: text("started_at").notNull(),
  completedAt: text("completed_at"),
  totalSecondsSpent: integer("total_seconds_spent").notNull().default(0),
  weightedBand: real("weighted_band"),
}, (t) => [index("idx_sessions_user").on(t.userId, t.startedAt)]);

export const drafts = sqliteTable("drafts", {
  id: text("id").primaryKey(),
  userId: text("user_id").notNull(),
  sessionId: text("session_id").notNull(),
  promptId: text("prompt_id").notNull(),
  taskType: text("task_type").notNull(),
  version: integer("version").notNull().default(1),
  outlineJson: text("outline_json"),
  outlineFeedbackJson: text("outline_feedback_json"),
  essayText: text("essay_text").notNull(),
  wordCount: integer("word_count").notNull().default(0),
  secondsSpent: integer("seconds_spent").notNull().default(0),
  promptHighlightsJson: text("prompt_highlights_json"),
  scratchNotes: text("scratch_notes"),
  createdAt: text("created_at").notNull(),
}, (t) => [index("idx_drafts_session").on(t.sessionId), index("idx_drafts_user").on(t.userId)]);

export const evaluations = sqliteTable("evaluations", {
  id: text("id").primaryKey(),
  userId: text("user_id").notNull(),
  draftId: text("draft_id").notNull(),
  overallBand: real("overall_band").notNull(),
  taTrBand: real("ta_tr_band").notNull(),
  ccBand: real("cc_band").notNull(),
  lrBand: real("lr_band").notNull(),
  graBand: real("gra_band").notNull(),
  examinerSummary: text("examiner_summary").notNull(),
  criterionFeedbackJson: text("criterion_feedback_json").notNull(),
  inlineAnnotationsJson: text("inline_annotations_json").notNull(),
  targetBandRewrite: text("target_band_rewrite").notNull(),
  rewriteKeyChangesJson: text("rewrite_key_changes_json").notNull().default("[]"),
  topPrioritiesJson: text("top_priorities_json").notNull(),
  providerUsed: text("provider_used").notNull(),
  modelUsed: text("model_used").notNull(),
  costUsd: real("cost_usd"),
  createdAt: text("created_at").notNull(),
}, (t) => [index("idx_evals_draft").on(t.draftId), index("idx_evals_user").on(t.userId, t.createdAt)]);

export const vaultItems = sqliteTable("vault_items", {
  id: text("id").primaryKey(),
  userId: text("user_id").notNull(),
  sourceDraftId: text("source_draft_id"),
  itemType: text("item_type").notNull(), // "error" | "collocation"
  criterion: text("criterion").notNull(), // "TA_TR" | "CC" | "LR" | "GRA" | "L1"
  originalText: text("original_text").notNull(),
  upgradedText: text("upgraded_text").notNull(),
  explanation: text("explanation").notNull(),
  exampleSentence: text("example_sentence").notNull(),
  masteryLevel: integer("mastery_level").notNull().default(0), // 0..3
  reviewCount: integer("review_count").notNull().default(0),
  nextReviewAt: text("next_review_at"),
  createdAt: text("created_at").notNull(),
}, (t) => [index("idx_vault_user").on(t.userId, t.createdAt)]);

export const microDrills = sqliteTable("micro_drills", {
  id: text("id").primaryKey(),
  userId: text("user_id").notNull(),
  drillType: text("drill_type").notNull(),
  targetCriterion: text("target_criterion").notNull(),
  promptPayloadJson: text("prompt_payload_json").notNull(),
  userResponse: text("user_response"),
  scoreJson: text("score_json"),
  createdAt: text("created_at").notNull(),
  completedAt: text("completed_at"),
}, (t) => [index("idx_drills_user").on(t.userId, t.createdAt)]);

export const aiUsage = sqliteTable("ai_usage", {
  id: text("id").primaryKey(),
  userId: text("user_id").notNull(),
  feature: text("feature").notNull(), // "rubric_score" | "annotations_rewrite" | "outline_check" | "toolkit" | "prompt_generation" | "drill_generate" | "drill_score" | "vision_extract"
  provider: text("provider").notNull(),
  keySource: text("key_source").notNull().default("platform"), // "platform" (our key, counts toward quota) | "user" (their own key)
  plan: text("plan"), // the user's plan when the request was made (NULL for rows before this was recorded)
  modelUsed: text("model_used").notNull(),
  promptTokens: integer("prompt_tokens"),
  completionTokens: integer("completion_tokens"),
  totalTokens: integer("total_tokens"),
  costUsd: real("cost_usd"),
  createdAt: text("created_at").notNull(),
}, (t) => [index("idx_ai_usage_created").on(t.createdAt), index("idx_ai_usage_user").on(t.userId, t.createdAt), index("idx_ai_usage_plan").on(t.plan, t.keySource, t.createdAt)]);

/**
 * Uploaded images (Task 1 chart screenshots). Bytes live in Vercel Blob
 * (private) when BLOB_READ_WRITE_TOKEN is set, otherwise in `data_base64`.
 * Served only to their owner through /api/images/[id].
 */
export const images = sqliteTable(
  "images",
  {
    id: text("id").primaryKey(),
    userId: text("user_id").notNull(),
    contentType: text("content_type").notNull(),
    bytes: integer("bytes").notNull(),
    storage: text("storage").notNull(), // "blob" | "db"
    blobPathname: text("blob_pathname"),
    dataBase64: text("data_base64"),
    createdAt: text("created_at").notNull(),
  },
  (t) => [index("idx_images_user").on(t.userId)]
);

/** Server and client errors, kept for the admin view (newest first, pruned). */
export const errorEvents = sqliteTable(
  "error_events",
  {
    id: text("id").primaryKey(),
    source: text("source").notNull(), // "server" | "client"
    message: text("message").notNull(),
    stack: text("stack"),
    path: text("path"),
    digest: text("digest"),
    userId: text("user_id"),
    createdAt: text("created_at").notNull(),
  },
  (t) => [index("idx_error_events_created").on(t.createdAt)]
);
