ALTER TABLE `ai_usage` ADD `plan` text;--> statement-breakpoint
CREATE INDEX `idx_ai_usage_plan` ON `ai_usage` (`plan`,`key_source`,`created_at`);