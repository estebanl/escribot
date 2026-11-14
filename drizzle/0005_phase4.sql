CREATE TABLE `error_events` (
	`id` text PRIMARY KEY NOT NULL,
	`source` text NOT NULL,
	`message` text NOT NULL,
	`stack` text,
	`path` text,
	`digest` text,
	`user_id` text,
	`created_at` text NOT NULL
);
--> statement-breakpoint
CREATE INDEX `idx_error_events_created` ON `error_events` (`created_at`);--> statement-breakpoint
CREATE TABLE `images` (
	`id` text PRIMARY KEY NOT NULL,
	`user_id` text NOT NULL,
	`content_type` text NOT NULL,
	`bytes` integer NOT NULL,
	`storage` text NOT NULL,
	`blob_pathname` text,
	`data_base64` text,
	`created_at` text NOT NULL
);
--> statement-breakpoint
CREATE INDEX `idx_images_user` ON `images` (`user_id`);--> statement-breakpoint
ALTER TABLE `user_settings` ADD `onboarded_at` text;--> statement-breakpoint
-- Everyone who already has settings has effectively been through onboarding.
UPDATE `user_settings` SET `onboarded_at` = `updated_at` WHERE `onboarded_at` IS NULL;
