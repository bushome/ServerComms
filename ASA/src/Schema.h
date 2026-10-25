#pragma once

// Tables the plugin uses, created on first start if they don't exist (CREATE TABLE IF NOT EXISTS, so
// running them every start is harmless). Same definitions as Bot/schema/001_initial.sql, which also
// holds the bot-only tables; later schema changes (ALTER TABLE) are applied by ServerComms.Bot.

namespace ServerComms
{
	inline const char* const kSchema[] = {
		// servers
		"CREATE TABLE IF NOT EXISTS servers ( server_key VARCHAR(64) NOT NULL PRIMARY KEY, display_name VARCHAR(64) NOT NULL DEFAULT '', players INT NOT NULL DEFAULT 0, fps_cur FLOAT NOT NULL DEFAULT 0, fps_avg FLOAT NOT NULL DEFAULT 0, rcon_host VARCHAR(64) NOT NULL DEFAULT '', rcon_port INT NOT NULL DEFAULT 0, started_at BIGINT NOT NULL DEFAULT 0, last_heartbeat BIGINT NOT NULL DEFAULT 0, state VARCHAR(16) NOT NULL DEFAULT 'online' ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
		// messages
		"CREATE TABLE IF NOT EXISTS messages ( id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, created_at BIGINT NOT NULL, source VARCHAR(8) NOT NULL, server_key VARCHAR(64) NOT NULL DEFAULT '', player_key VARCHAR(64) NOT NULL DEFAULT '', name VARCHAR(128) NOT NULL DEFAULT '', tribe VARCHAR(128) NOT NULL DEFAULT '', platform VARCHAR(16) NOT NULL DEFAULT '', discord_tag VARCHAR(64) NOT NULL DEFAULT '', style_json TEXT NULL, discord_roles TEXT NULL, text TEXT NOT NULL, KEY idx_created (created_at) ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
		// players_online
		"CREATE TABLE IF NOT EXISTS players_online ( server_key VARCHAR(64) NOT NULL, player_key VARCHAR(64) NOT NULL, player_id BIGINT NOT NULL DEFAULT 0, name VARCHAR(128) NOT NULL DEFAULT '', steam_name VARCHAR(128) NOT NULL DEFAULT '', tribe_id BIGINT NOT NULL DEFAULT 0, tribe_name VARCHAR(128) NOT NULL DEFAULT '', platform VARCHAR(16) NOT NULL DEFAULT '', discord_tag VARCHAR(64) NOT NULL DEFAULT '', joined_at BIGINT NOT NULL DEFAULT 0, PRIMARY KEY (server_key, player_key) ) ENGINE=MEMORY DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
		// links
		"CREATE TABLE IF NOT EXISTS links ( discord_id BIGINT UNSIGNED NOT NULL PRIMARY KEY, player_key VARCHAR(64) NOT NULL, player_id BIGINT NOT NULL DEFAULT 0, tribe_id BIGINT NOT NULL DEFAULT 0, steam_name VARCHAR(128) NOT NULL DEFAULT '', linked_at BIGINT NOT NULL DEFAULT 0, last_seen BIGINT NOT NULL DEFAULT 0, UNIQUE KEY uq_player (player_key) ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
		// link_codes
		"CREATE TABLE IF NOT EXISTS link_codes ( code VARCHAR(12) NOT NULL PRIMARY KEY, discord_id BIGINT UNSIGNED NOT NULL, expires_at BIGINT NOT NULL ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
		// player_prefs
		"CREATE TABLE IF NOT EXISTS player_prefs ( player_key VARCHAR(64) NOT NULL PRIMARY KEY, hidden_tags VARCHAR(512) NOT NULL DEFAULT '' ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
		// ignores
		"CREATE TABLE IF NOT EXISTS ignores ( player_key VARCHAR(64) NOT NULL, ignored_key VARCHAR(64) NOT NULL, ignored_name VARCHAR(128) NOT NULL DEFAULT '', created_at BIGINT NOT NULL DEFAULT 0, PRIMARY KEY (player_key, ignored_key) ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
		// mutes
		"CREATE TABLE IF NOT EXISTS mutes ( player_key VARCHAR(64) NOT NULL PRIMARY KEY, reason VARCHAR(300) NOT NULL DEFAULT '', until BIGINT NULL, muted_by VARCHAR(128) NOT NULL DEFAULT '', created_at BIGINT NOT NULL DEFAULT 0 ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
		// servers.state for databases created before it existed (schema version 3)
		"ALTER TABLE servers ADD COLUMN IF NOT EXISTS state VARCHAR(16) NOT NULL DEFAULT 'online'",
	};
}
