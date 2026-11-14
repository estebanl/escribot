CREATE TABLE IF NOT EXISTS `ai_usage` (
	`id` text PRIMARY KEY NOT NULL,
	`feature` text NOT NULL,
	`provider` text NOT NULL,
	`model_used` text NOT NULL,
	`prompt_tokens` integer,
	`completion_tokens` integer,
	`total_tokens` integer,
	`cost_usd` real,
	`created_at` text NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS `drafts` (
	`id` text PRIMARY KEY NOT NULL,
	`session_id` text NOT NULL,
	`prompt_id` text NOT NULL,
	`task_type` text NOT NULL,
	`version` integer DEFAULT 1 NOT NULL,
	`outline_json` text,
	`outline_feedback_json` text,
	`essay_text` text NOT NULL,
	`word_count` integer DEFAULT 0 NOT NULL,
	`seconds_spent` integer DEFAULT 0 NOT NULL,
	`prompt_highlights_json` text,
	`scratch_notes` text,
	`created_at` text NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS `evaluations` (
	`id` text PRIMARY KEY NOT NULL,
	`draft_id` text NOT NULL,
	`overall_band` real NOT NULL,
	`ta_tr_band` real NOT NULL,
	`cc_band` real NOT NULL,
	`lr_band` real NOT NULL,
	`gra_band` real NOT NULL,
	`examiner_summary` text NOT NULL,
	`criterion_feedback_json` text NOT NULL,
	`inline_annotations_json` text NOT NULL,
	`target_band_rewrite` text NOT NULL,
	`rewrite_key_changes_json` text DEFAULT '[]' NOT NULL,
	`top_priorities_json` text NOT NULL,
	`provider_used` text NOT NULL,
	`model_used` text NOT NULL,
	`cost_usd` real,
	`created_at` text NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS `micro_drills` (
	`id` text PRIMARY KEY NOT NULL,
	`drill_type` text NOT NULL,
	`target_criterion` text NOT NULL,
	`prompt_payload_json` text NOT NULL,
	`user_response` text,
	`score_json` text,
	`created_at` text NOT NULL,
	`completed_at` text
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS `prompts` (
	`id` text PRIMARY KEY NOT NULL,
	`task_type` text NOT NULL,
	`category` text NOT NULL,
	`topic` text NOT NULL,
	`title` text NOT NULL,
	`prompt_text` text NOT NULL,
	`chart_spec_json` text,
	`image_data_url` text,
	`extracted_visual_spec` text,
	`source` text DEFAULT 'curated' NOT NULL,
	`created_at` text NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS `user_settings` (
	`id` integer PRIMARY KEY NOT NULL,
	`current_band` real DEFAULT 6.5 NOT NULL,
	`target_band` real DEFAULT 7.5 NOT NULL,
	`exam_date` text,
	`native_language` text DEFAULT 'es' NOT NULL,
	`l1_coaching_enabled` integer DEFAULT true NOT NULL,
	`active_provider` text DEFAULT 'openrouter' NOT NULL,
	`grader_model` text DEFAULT 'anthropic/claude-sonnet-5.5' NOT NULL,
	`fast_model` text DEFAULT 'deepseek/deepseek-v4.1-flash-1017' NOT NULL,
	`vision_model` text DEFAULT 'deepseek/deepseek-v4.1-flash-1017' NOT NULL,
	`encrypted_api_keys` text DEFAULT '{}' NOT NULL,
	`updated_at` text NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS `vault_items` (
	`id` text PRIMARY KEY NOT NULL,
	`source_draft_id` text,
	`item_type` text NOT NULL,
	`criterion` text NOT NULL,
	`original_text` text NOT NULL,
	`upgraded_text` text NOT NULL,
	`explanation` text NOT NULL,
	`example_sentence` text NOT NULL,
	`mastery_level` integer DEFAULT 0 NOT NULL,
	`review_count` integer DEFAULT 0 NOT NULL,
	`next_review_at` text,
	`created_at` text NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS `writing_sessions` (
	`id` text PRIMARY KEY NOT NULL,
	`mode` text NOT NULL,
	`status` text DEFAULT 'in_progress' NOT NULL,
	`task1_prompt_id` text,
	`task2_prompt_id` text,
	`started_at` text NOT NULL,
	`completed_at` text,
	`total_seconds_spent` integer DEFAULT 0 NOT NULL,
	`weighted_band` real
);
--> statement-breakpoint
CREATE INDEX IF NOT EXISTS `idx_drafts_session` ON `drafts` (`session_id`);
--> statement-breakpoint
CREATE INDEX IF NOT EXISTS `idx_evals_draft` ON `evaluations` (`draft_id`);
--> statement-breakpoint
CREATE INDEX IF NOT EXISTS `idx_prompts_task` ON `prompts` (`task_type`);
--> statement-breakpoint
CREATE INDEX IF NOT EXISTS `idx_ai_usage_created` ON `ai_usage` (`created_at`);
