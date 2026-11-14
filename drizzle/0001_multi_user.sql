-- Multi-user support: Better Auth tables + per-user ownership on every app table.
-- Rows that existed before this migration are assigned to the placeholder owner
-- 'legacy' and are handed to the OWNER_EMAIL account when it signs up.

CREATE TABLE `user` (
	`id` text PRIMARY KEY NOT NULL,
	`name` text NOT NULL,
	`email` text NOT NULL,
	`email_verified` integer DEFAULT false NOT NULL,
	`image` text,
	`created_at` integer NOT NULL,
	`updated_at` integer NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `user_email_unique` ON `user` (`email`);
--> statement-breakpoint
CREATE TABLE `session` (
	`id` text PRIMARY KEY NOT NULL,
	`expires_at` integer NOT NULL,
	`token` text NOT NULL,
	`created_at` integer NOT NULL,
	`updated_at` integer NOT NULL,
	`ip_address` text,
	`user_agent` text,
	`user_id` text NOT NULL,
	FOREIGN KEY (`user_id`) REFERENCES `user`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE UNIQUE INDEX `session_token_unique` ON `session` (`token`);
--> statement-breakpoint
CREATE INDEX `idx_session_user` ON `session` (`user_id`);
--> statement-breakpoint
CREATE TABLE `account` (
	`id` text PRIMARY KEY NOT NULL,
	`account_id` text NOT NULL,
	`provider_id` text NOT NULL,
	`user_id` text NOT NULL,
	`access_token` text,
	`refresh_token` text,
	`id_token` text,
	`access_token_expires_at` integer,
	`refresh_token_expires_at` integer,
	`scope` text,
	`password` text,
	`created_at` integer NOT NULL,
	`updated_at` integer NOT NULL,
	FOREIGN KEY (`user_id`) REFERENCES `user`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `idx_account_user` ON `account` (`user_id`);
--> statement-breakpoint
CREATE TABLE `verification` (
	`id` text PRIMARY KEY NOT NULL,
	`identifier` text NOT NULL,
	`value` text NOT NULL,
	`expires_at` integer NOT NULL,
	`created_at` integer NOT NULL,
	`updated_at` integer NOT NULL
);
--> statement-breakpoint
CREATE INDEX `idx_verification_identifier` ON `verification` (`identifier`);
--> statement-breakpoint
ALTER TABLE `prompts` ADD `user_id` text;
--> statement-breakpoint
UPDATE `prompts` SET `user_id` = 'legacy' WHERE `source` <> 'curated';
--> statement-breakpoint
ALTER TABLE `writing_sessions` ADD `user_id` text DEFAULT 'legacy' NOT NULL;
--> statement-breakpoint
ALTER TABLE `drafts` ADD `user_id` text DEFAULT 'legacy' NOT NULL;
--> statement-breakpoint
ALTER TABLE `evaluations` ADD `user_id` text DEFAULT 'legacy' NOT NULL;
--> statement-breakpoint
ALTER TABLE `vault_items` ADD `user_id` text DEFAULT 'legacy' NOT NULL;
--> statement-breakpoint
ALTER TABLE `micro_drills` ADD `user_id` text DEFAULT 'legacy' NOT NULL;
--> statement-breakpoint
ALTER TABLE `ai_usage` ADD `user_id` text DEFAULT 'legacy' NOT NULL;
--> statement-breakpoint
CREATE TABLE `__new_user_settings` (
	`user_id` text PRIMARY KEY NOT NULL,
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
INSERT INTO `__new_user_settings` (
	`user_id`, `current_band`, `target_band`, `exam_date`, `native_language`,
	`l1_coaching_enabled`, `active_provider`, `grader_model`, `fast_model`,
	`vision_model`, `encrypted_api_keys`, `updated_at`
)
SELECT
	'legacy', `current_band`, `target_band`, `exam_date`, `native_language`,
	`l1_coaching_enabled`, `active_provider`, `grader_model`, `fast_model`,
	COALESCE(NULLIF(`vision_model`, ''), 'deepseek/deepseek-v4.1-flash-1017'),
	`encrypted_api_keys`, `updated_at`
FROM `user_settings`
WHERE `id` = 1;
--> statement-breakpoint
DROP TABLE `user_settings`;
--> statement-breakpoint
ALTER TABLE `__new_user_settings` RENAME TO `user_settings`;
--> statement-breakpoint
CREATE INDEX `idx_prompts_user` ON `prompts` (`user_id`);
--> statement-breakpoint
CREATE INDEX `idx_sessions_user` ON `writing_sessions` (`user_id`, `started_at`);
--> statement-breakpoint
CREATE INDEX `idx_drafts_user` ON `drafts` (`user_id`);
--> statement-breakpoint
CREATE INDEX `idx_evals_user` ON `evaluations` (`user_id`, `created_at`);
--> statement-breakpoint
CREATE INDEX `idx_vault_user` ON `vault_items` (`user_id`, `created_at`);
--> statement-breakpoint
CREATE INDEX `idx_drills_user` ON `micro_drills` (`user_id`, `created_at`);
--> statement-breakpoint
CREATE INDEX `idx_ai_usage_user` ON `ai_usage` (`user_id`, `created_at`);
