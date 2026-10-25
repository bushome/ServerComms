-- ServerComms schema, version 2: small key/value store for the bot's own state (chat cursor and
-- similar), so a restart resumes where it stopped instead of replaying or skipping chat.

CREATE TABLE IF NOT EXISTS bot_state (
  k VARCHAR(64) NOT NULL PRIMARY KEY,
  v VARCHAR(255) NOT NULL DEFAULT '',
  updated_at BIGINT NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
