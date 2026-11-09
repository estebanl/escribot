import { sqliteTable, text, integer, real } from "drizzle-orm/sqlite-core";

export const userSettings = sqliteTable("user_settings", {
  id: integer("id").primaryKey(),
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
  updatedAt: text("updated_at").notNull(),
});

export const prompts = sqliteTable("prompts", {
  id: text("id").primaryKey(),
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
});

export const writingSessions = sqliteTable("writing_sessions", {
  id: text("id").primaryKey(),
  mode: text("mode").notNull(), // "coach" | "simulator_t1" | "simulator_t2" | "simulator_full_60m"
  status: text("status").notNull().default("in_progress"), // "in_progress" | "completed"
  task1PromptId: text("task1_prompt_id"),
  task2PromptId: text("task2_prompt_id"),
  startedAt: text("started_at").notNull(),
  completedAt: text("completed_at"),
  totalSecondsSpent: integer("total_seconds_spent").notNull().default(0),
  weightedBand: real("weighted_band"),
});

export const drafts = sqliteTable("drafts", {
  id: text("id").primaryKey(),
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
});

export const evaluations = sqliteTable("evaluations", {
  id: text("id").primaryKey(),
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
});

export const vaultItems = sqliteTable("vault_items", {
  id: text("id").primaryKey(),
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
});

export const microDrills = sqliteTable("micro_drills", {
  id: text("id").primaryKey(),
  drillType: text("drill_type").notNull(),
  targetCriterion: text("target_criterion").notNull(),
  promptPayloadJson: text("prompt_payload_json").notNull(),
  userResponse: text("user_response"),
  scoreJson: text("score_json"),
  createdAt: text("created_at").notNull(),
  completedAt: text("completed_at"),
});

export const aiUsage = sqliteTable("ai_usage", {
  id: text("id").primaryKey(),
  feature: text("feature").notNull(), // "rubric_score" | "annotations_rewrite" | "outline_check" | "toolkit" | "prompt_generation" | "drill_generate" | "drill_score" | "vision_extract"
  provider: text("provider").notNull(),
  modelUsed: text("model_used").notNull(),
  promptTokens: integer("prompt_tokens"),
  completionTokens: integer("completion_tokens"),
  totalTokens: integer("total_tokens"),
  costUsd: real("cost_usd"),
  createdAt: text("created_at").notNull(),
});
