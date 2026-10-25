// ServerComms - cross-server global chat with Discord relay for ARK: Survival Ascended (AsaApi).
//
//  - Global chat is styled (tag, icon, colors by Permissions group / tribe / player), checked (mute,
//    word filter, anti-spam) and delivered to this map, then written to the shared database. Other maps
//    and ServerComms.Bot (Discord) pick it up from there; Discord messages come back the same way.
//  - Local, tribe and alliance chat are never relayed. They are restyled per recipient in
//    ClientChatMessage, so the game still decides who receives them.
//  - Heartbeat + online-player sync for the bot's player list; MultiHome/RCONPort published for its
//    RCON fallback.
//
// Threading: every SQL statement runs on one background worker thread (DbWorker). Results come back
// to the game thread through GameQueue, drained by the 1 s game-thread timer. Nothing touches the
// database until the world is loaded and the server is ready. All periodic work runs from one OnTimer
// callback that is removed in Plugin_Unload.

#include <API/ARK/Ark.h>
#include <Logger/Logger.h>
#include <json.hpp>

#include <Windows.h>

#include <atomic>
#include <chrono>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <memory>
#include <condition_variable>
#include <mutex>
#include <thread>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "ChatLogic.h"
#include "Database.h"
#include "Schema.h"
#include "PermissionsApi.h"

namespace fs = std::filesystem;
using namespace ServerComms;

namespace
{
	// -----------------------------------------------------------------------------------------
	// Config and state
	// -----------------------------------------------------------------------------------------

	struct Config
	{
		DbConfig db;
		std::string server_key; // empty = map name
		double slow_tick_ms = 2.0; // log when one second's work takes longer than this
		std::map<int, std::string> platforms{ { 1, "Steam" } }; // senderPlatform -> label (1 = Steam)
		Settings chat;
	};

	struct PlayerInfo
	{
		std::string name;       // character name, UTF-8
		std::string steam_name;
		std::string tribe;
		std::string platform;   // from the player's chat (senderPlatform), see PlatformName
		long long tribe_id = 0;
		long long player_id = 0;
		std::vector<std::string> groups;
		bool admin = false;
		std::set<std::string> hidden_tags;
		std::unordered_map<std::string, std::string> ignores; // key -> name
		long long joined_at = 0;
		bool prefs_loaded = false;
		std::vector<Speaker> last_pick;   // /ignore or /unignore list shown last
		bool last_pick_unignore = false;
		long long last_pick_at = 0;
	};

	struct OutRow
	{
		long long created_at = 0;
		std::string player_key, name, tribe, discord_tag, style_json, text, platform;
	};

	Config g_cfg;
	bool g_alive = false;
	bool g_world_ready = false;
	long long g_tick = 0;
	std::string g_server_key;
	ServerAddress g_address;
	long long g_started_at = 0;

	std::unique_ptr<DbWorker> g_worker;
	GameQueue g_queue;
	std::atomic<long long> g_cursor{ 0 };
	std::atomic<bool> g_cursor_ready{ false };
	std::atomic<bool> g_poll_busy{ false };
	std::atomic<bool> g_flush_busy{ false };
	std::atomic<bool> g_db_ok{ true };
	std::atomic<bool> g_roster_full{ true }; // next heartbeat rewrites this map's online list in full

	std::mutex g_outbox_mutex;
	std::deque<OutRow> g_outbox;

	std::unordered_map<std::string, PlayerInfo> g_players;     // game thread, keyed by EOS ID (lower-case)
	std::unordered_map<std::string, Mute> g_mutes;             // game thread
	SpamTracker g_spam;
	RecentSpeakers g_recent(5);
	std::vector<float> g_fps_samples;
	std::vector<std::wstring> g_chat_commands;                 // registered chat command names

	// Set while the game delivers a non-global message we are restyling.
	struct Restyle
	{
		ChatMode mode = ChatMode::Global;
		std::string sender_key;
		ResolvedStyle style;
	};
	std::optional<Restyle> g_restyle;
	bool g_sending_own = false;

	// -----------------------------------------------------------------------------------------
	// Helpers
	// -----------------------------------------------------------------------------------------

	long long Now() { return static_cast<long long>(std::time(nullptr)); }

	long long Steady()
	{
		return std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
	}

	FString ToF(const std::string& s) { return FString::FromStringUTF8(s); }
	std::string ToStd(const FString& s) { return s.ToString(); }
	std::wstring ToW(const FString& s) { return s.IsEmpty() ? std::wstring() : std::wstring(*s); }

	std::string IdOf(AController* pc)
	{
		if (!pc)
			return {};
		return Lower(ToStd(AsaApi::IApiUtils::GetEOSIDFromController(pc)));
	}

	fs::path PluginDir() { return fs::path(AsaApi::Tools::GetCurrentDir()) / "ArkApi" / "Plugins" / PROJECT_NAME; }

	std::string Csv(const std::set<std::string>& items)
	{
		std::string out;
		for (const auto& i : items) { if (!out.empty()) out += ","; out += i; }
		return out;
	}

	std::set<std::string> SplitCsv(const std::string& text)
	{
		std::set<std::string> out;
		std::string cur;
		for (char c : text + ",")
		{
			if (c == ',') { if (!cur.empty()) out.insert(cur); cur.clear(); }
			else cur.push_back(c);
		}
		return out;
	}

	std::string Opt(const DbRow& r, size_t i) { return i < r.size() && r[i] ? *r[i] : std::string(); }

	long long OptI(const DbRow& r, size_t i)
	{
		try { return i < r.size() && r[i] ? std::stoll(*r[i]) : 0; }
		catch (...) { return 0; }
	}

	void Tell(AShooterPlayerController* pc, const std::string& text, FLinearColor color = FLinearColor(1.0f, 0.85f, 0.3f, 1.0f))
	{
		if (!pc || text.empty())
			return;
		const FString msg = ToF(text);
		AsaApi::GetApiUtils().SendServerMessage(pc, color, L"{}", *msg);
	}

	void TellError(AShooterPlayerController* pc, const std::string& text) { Tell(pc, text, FLinearColor(1.0f, 0.35f, 0.35f, 1.0f)); }

	template <typename Fn>
	void ForEachPlayer(Fn&& fn)
	{
		UWorld* world = AsaApi::GetApiUtils().GetWorld();
		if (!world)
			return;
		for (TWeakObjectPtr<APlayerController> weak : world->PlayerControllerListField())
		{
			auto* pc = static_cast<AShooterPlayerController*>(weak.Get());
			if (pc)
				fn(pc);
		}
	}

	AShooterPlayerController* FindOnline(const std::string& key)
	{
		return AsaApi::GetApiUtils().FindPlayerFromEOSID(ToF(key));
	}

	// Online player by EOS ID or exact character name (case-insensitive).
	AShooterPlayerController* FindPlayer(const std::string& who)
	{
		if (auto* pc = FindOnline(who))
			return pc;
		AShooterPlayerController* found = nullptr;
		const std::string want = Lower(who);
		ForEachPlayer([&](AShooterPlayerController* pc)
		{
			if (!found && Lower(ToStd(AsaApi::IApiUtils::GetCharacterName(pc))) == want)
				found = pc;
		});
		return found;
	}

	// EOS ID: 32 hex characters.
	bool IsPlayerKey(const std::string& s)
	{
		return s.size() == 32 && std::all_of(s.begin(), s.end(), [](unsigned char c) { return std::isxdigit(c) != 0; });
	}

	// Icons are loaded each time they are sent: an already-loaded texture is found in memory, and a
	// texture the engine garbage-collected is simply loaded again (never a dangling pointer).
	UTexture2D* LoadIcon(const std::string& path)
	{
		if (path.empty())
			return nullptr;
		const FString fpath = ToF(path);
		UObject* obj = UVictoryCore::BPLoadObject(&fpath);
		// AsaApi exposes no UObject::IsA; chat icons are plain UTexture2D, so the exact class is checked.
		if (!obj || obj->ClassPrivateField() != UTexture2D::GetPrivateStaticClass())
			return nullptr;
		return static_cast<UTexture2D*>(obj);
	}

	// Drops icons that are listed but don't load as a texture on this server.
	void ValidateIcons(Settings& s)
	{
		for (auto it = s.safe_icons.begin(); it != s.safe_icons.end();)
		{
			if (!LoadIcon(*it))
			{
				Log::GetLog()->error("ServerComms: SafeIcons entry is not a loadable texture, skipped: {}", *it);
				it = s.safe_icons.erase(it);
			}
			else
				++it;
		}
	}

	void SendLine(AShooterPlayerController* pc, const FormattedLine& line, const std::wstring& system_sender = L"")
	{
		if (!pc)
			return;
		FPrimalChatMessage m;
		const bool split = g_cfg.chat.use_sender_field && !line.sender.empty();
		m.SenderName = FString(split ? line.sender.c_str() : system_sender.c_str());
		m.Message = FString(split ? line.body.c_str() : line.text.c_str());
		m.SendMode = EChatSendMode::GlobalChat;
		m.SenderIcon = line.icon.empty() ? nullptr : LoadIcon(line.icon);
		g_sending_own = true;
		pc->ClientChatMessage(m);
		g_sending_own = false;
	}

	std::string StyleJson(const ResolvedStyle& s)
	{
		nlohmann::json j = {
			{ "tag", s.tag }, { "icon", s.icon }, { "tag_color", s.tag_color }, { "name_color", s.name_color },
			{ "tribe_color", s.tribe_color }, { "message_color", s.message_color }, { "hide_tribe", s.hide_tribe } };
		return j.dump();
	}

	ResolvedStyle StyleFromJson(const std::string& text)
	{
		ResolvedStyle s;
		try
		{
			const auto j = nlohmann::json::parse(text);
			s.tag = j.value("tag", "");
			s.icon = j.value("icon", "");
			s.tag_color = j.value("tag_color", "");
			s.name_color = j.value("name_color", "");
			s.tribe_color = j.value("tribe_color", "");
			s.message_color = j.value("message_color", "");
			s.hide_tribe = j.value("hide_tribe", false);
		}
		catch (...) {}
		return s;
	}

	// -----------------------------------------------------------------------------------------
	// Players
	// -----------------------------------------------------------------------------------------

	std::vector<std::string> GroupsOf(const std::string& key)
	{
		std::vector<std::string> out;
		const auto perms = PermissionsApi::Resolve();
		if (perms.status != PermissionsApi::Status::Ready)
			return out;
		try
		{
			const TArray<FString> groups = perms.functions->get_player_groups(ToF(key));
			for (const FString& g : groups)
				out.push_back(ToStd(g));
		}
		catch (...) {}
		return out;
	}

	void Refresh(PlayerInfo& p, AShooterPlayerController* pc, const std::string& key)
	{
		p.name = ToStd(AsaApi::IApiUtils::GetCharacterName(pc));
		p.admin = pc->bIsAdmin().Get();
		p.player_id = static_cast<long long>(AsaApi::IApiUtils::GetPlayerID(pc));
		p.tribe = ToStd(pc->GetTribeName());
		p.tribe_id = 0;
		if (APlayerState* ps = pc->PlayerStateField().Get())
		{
			p.steam_name = ToStd(ps->PlayerNamePrivateField()); // platform account name
			p.tribe_id = static_cast<AShooterPlayerState*>(ps)->GetTribeId();
		}
		p.groups = GroupsOf(key);
	}

	Sender SenderOf(const PlayerInfo& p, const std::string& key)
	{
		Sender s;
		s.player_key = key;
		s.tribe_id = p.tribe_id ? std::to_string(p.tribe_id) : std::string();
		s.groups = p.groups;
		s.is_admin = p.admin;
		s.hidden_tags = p.hidden_tags;
		return s;
	}

	void LoadPrefs(const std::string& key)
	{
		if (!g_worker)
			return;
		g_worker->Post([key](Db& db)
		{
			const std::string k = db.Esc(key);
			auto prefs = db.Query("SELECT hidden_tags FROM player_prefs WHERE player_key='" + k + "'");
			auto ignores = db.Query("SELECT ignored_key, ignored_name FROM ignores WHERE player_key='" + k + "'");
			if (!prefs || !ignores)
				return; // retried on the next player scan
			std::set<std::string> hidden;
			if (!prefs->empty())
				hidden = SplitCsv(Opt(prefs->front(), 0));
			std::unordered_map<std::string, std::string> ign;
			for (const auto& r : *ignores)
				ign[Opt(r, 0)] = Opt(r, 1);
			g_queue.Post([key, hidden, ign]
			{
				auto it = g_players.find(key);
				if (it == g_players.end())
					return;
				it->second.hidden_tags = hidden;
				it->second.ignores = ign;
				it->second.prefs_loaded = true;
			});
		});
	}

	// Finds joins and leaves (once per second; cheap for a full server).
	void ScanPlayers()
	{
		std::unordered_set<std::string> online;
		ForEachPlayer([&](AShooterPlayerController* pc)
		{
			const std::string key = IdOf(pc);
			if (key.empty())
				return;
			online.insert(key);
			auto [it, added] = g_players.try_emplace(key);
			if (added)
			{
				it->second.joined_at = Now();
				Refresh(it->second, pc, key);
				LoadPrefs(key);
				if (g_worker)
				{
					const PlayerInfo p = it->second;
					g_worker->Post([key, p](Db& db)
					{
						db.Exec("UPDATE links SET player_id=" + std::to_string(p.player_id) + ", tribe_id=" + std::to_string(p.tribe_id) +
							", steam_name='" + db.Esc(p.steam_name) + "', last_seen=UNIX_TIMESTAMP() WHERE player_key='" + db.Esc(key) + "'");
					});
				}
			}
			else if (!it->second.prefs_loaded && g_tick % 15 == 0)
				LoadPrefs(key);
		});
		for (auto it = g_players.begin(); it != g_players.end();)
		{
			if (!online.count(it->first))
			{
				g_spam.Forget(it->first);
				it = g_players.erase(it);
			}
			else
				++it;
		}
	}

	// -----------------------------------------------------------------------------------------
	// Database work
	// -----------------------------------------------------------------------------------------

	void ReportDb(bool ok, Db& db, const char* what)
	{
		const bool was = g_db_ok.exchange(ok);
		if (!ok && was)
			Log::GetLog()->error("ServerComms: database unavailable ({}): {}. Chat relay paused; retrying.", what, db.LastError());
		else if (ok && !was)
			Log::GetLog()->info("ServerComms: database reachable again; chat relay resumed.");
	}

	// Sends everything in the outbox, in order. On a database error the unsent rows go back to the
	// front of the outbox (capacity still applies) and are retried from Tick.
	void FlushOutbox()
	{
		if (!g_worker || g_flush_busy.exchange(true))
			return;
		g_worker->Post([](Db& db)
		{
			std::deque<OutRow> rows;
			{
				std::lock_guard lock(g_outbox_mutex);
				rows.swap(g_outbox);
			}
			while (!rows.empty())
			{
				const OutRow& r = rows.front();
				const long long n = db.Exec(
					"INSERT INTO messages (created_at, source, server_key, player_key, name, tribe, platform, discord_tag, style_json, text) VALUES (" +
					std::to_string(r.created_at) + ",'game','" + db.Esc(g_server_key) + "','" + db.Esc(r.player_key) + "','" + db.Esc(r.name) +
					"','" + db.Esc(r.tribe) + "','" + db.Esc(r.platform) + "','" + db.Esc(r.discord_tag) + "','" + db.Esc(r.style_json) + "','" + db.Esc(r.text) + "')");
				if (n < 0)
				{
					ReportDb(false, db, "send");
					std::lock_guard lock(g_outbox_mutex);
					for (auto it = rows.rbegin(); it != rows.rend(); ++it)
						g_outbox.push_front(*it);
					while (static_cast<int>(g_outbox.size()) > g_cfg.chat.outbox_capacity)
						g_outbox.pop_front();
					break;
				}
				ReportDb(true, db, "send");
				rows.pop_front();
			}
			g_flush_busy = false;
		});
	}

	void QueueOut(OutRow row)
	{
		{
			std::lock_guard lock(g_outbox_mutex);
			g_outbox.push_back(std::move(row));
			while (static_cast<int>(g_outbox.size()) > g_cfg.chat.outbox_capacity)
				g_outbox.pop_front();
		}
		FlushOutbox();
	}

	bool OutboxPending()
	{
		std::lock_guard lock(g_outbox_mutex);
		return !g_outbox.empty();
	}

	// Creates the plugin's tables if they don't exist yet. Runs before the first query; if the database
	// is unreachable it is simply tried again with the next InitCursor.
	void EnsureSchema()
	{
		g_worker->Post([](Db& db)
		{
			for (const char* sql : kSchema)
				if (db.Exec(sql) < 0)
				{
					Log::GetLog()->error("ServerComms: creating tables failed: {}", db.LastError());
					return;
				}
		});
	}

	void InitCursor()
	{
		EnsureSchema();
		g_worker->Post([](Db& db)
		{
			auto r = db.Query("SELECT COALESCE(MAX(id),0) FROM messages");
			if (!r)
			{
				ReportDb(false, db, "start");
				return; // retried by Tick while !g_cursor_ready
			}
			ReportDb(true, db, "start");
			g_cursor = r->empty() ? 0 : OptI(r->front(), 0);
			g_cursor_ready = true;
		});
	}

	void Deliver(const std::vector<DbRow>& rows)
	{
		for (const auto& r : rows)
		{
			const std::string source = Opt(r, 1);
			const std::string server = Opt(r, 2);
			if (source == "game" && server == g_server_key)
				continue; // our own message, already shown here
			const std::string sender_key = Opt(r, 3);

			LineInput in;
			in.mode = ChatMode::Global;
			in.source_map = server;
			in.viewer_map = g_server_key;
			in.from_discord = source == "discord";
			in.name = Utf8ToWide(Opt(r, 4));
			in.tribe = Utf8ToWide(Opt(r, 5));
			in.message = ClampLength(g_cfg.chat, Utf8ToWide(Opt(r, 8)));
			if (in.from_discord)
			{
				Sender d;
				const auto roles = SplitCsv(Opt(r, 7));
				d.role_ids.assign(roles.begin(), roles.end());
				in.style = ResolveStyle(g_cfg.chat.discord_styles, d);
			}
			else
				in.style = StyleFromJson(Opt(r, 6));
			const FormattedLine line = FormatLine(g_cfg.chat, in);

			if (!in.from_discord && !sender_key.empty())
				g_recent.Add({ sender_key, in.name });
			ForEachPlayer([&](AShooterPlayerController* pc)
			{
				const auto it = g_players.find(IdOf(pc));
				if (it != g_players.end() && !sender_key.empty() && it->second.ignores.count(sender_key))
					return;
				SendLine(pc, line);
			});
		}
	}

	void Poll()
	{
		if (!g_worker || !g_cursor_ready || g_poll_busy.exchange(true))
			return;
		g_worker->Post([](Db& db)
		{
			auto rows = db.Query("SELECT id, source, server_key, player_key, name, tribe, style_json, discord_roles, text FROM messages WHERE id > " +
				std::to_string(g_cursor.load()) + " ORDER BY id LIMIT 200");
			if (!rows)
			{
				ReportDb(false, db, "poll");
				g_poll_busy = false;
				return;
			}
			ReportDb(true, db, "poll");
			if (!rows->empty())
			{
				g_cursor = OptI(rows->back(), 0);
				g_queue.Post([rows = std::move(*rows)] { Deliver(rows); });
			}
			g_poll_busy = false;
		});
	}

	struct OnlineRow
	{
		std::string key, name, steam_name, tribe, discord_tag, platform;
		long long player_id = 0, tribe_id = 0, joined_at = 0;
	};

	void Heartbeat()
	{
		if (!g_worker)
			return;
		std::vector<OnlineRow> online;
		for (auto& [key, p] : g_players)
		{
			if (auto* pc = FindOnline(key))
				Refresh(p, pc, key);
			const ResolvedStyle st = ResolveStyle(g_cfg.chat.styles, SenderOf(p, key));
			online.push_back({ key, p.name, p.steam_name, p.tribe, st.discord_tag, p.platform, p.player_id, p.tribe_id, p.joined_at });
		}
		float fps_cur = 0, fps_avg = 0;
		if (!g_fps_samples.empty())
		{
			fps_cur = g_fps_samples.back();
			for (float f : g_fps_samples) fps_avg += f;
			fps_avg /= static_cast<float>(g_fps_samples.size());
			g_fps_samples.clear();
		}
		const auto map = g_cfg.chat.maps.find(g_server_key);
		const std::string display = map != g_cfg.chat.maps.end() ? map->second.label : g_server_key;

		g_worker->Post([online, fps_cur, fps_avg, display](Db& db)
		{
			const std::string k = db.Esc(g_server_key);
			char fps[64];
			std::snprintf(fps, sizeof(fps), "%.1f, %.1f", fps_cur, fps_avg);
			const long long n = db.Exec(
				"INSERT INTO servers (server_key, display_name, players, fps_cur, fps_avg, rcon_host, rcon_port, started_at, last_heartbeat, state) VALUES ('" +
				k + "','" + db.Esc(display) + "'," + std::to_string(online.size()) + "," + fps + ",'" + db.Esc(g_address.multihome) + "'," +
				std::to_string(g_address.rcon_port) + "," + std::to_string(g_started_at) + ",UNIX_TIMESTAMP(),'online') ON DUPLICATE KEY UPDATE " +
				"display_name=VALUES(display_name), players=VALUES(players), fps_cur=VALUES(fps_cur), fps_avg=VALUES(fps_avg), " +
				"rcon_host=VALUES(rcon_host), rcon_port=VALUES(rcon_port), started_at=VALUES(started_at), last_heartbeat=VALUES(last_heartbeat), state='online'");
			if (n < 0)
			{
				ReportDb(false, db, "heartbeat");
				return;
			}
			ReportDb(true, db, "heartbeat");

			// Only joins, leaves and changed rows are written (worker-thread state). After a database
			// error the next heartbeat rewrites this map's list in full.
			// A full rewrite also runs every 20 heartbeats (5 min by default): the MEMORY table empties
			// when MariaDB restarts, and the reconnect alone wouldn't tell us.
			static std::unordered_map<std::string, std::string> last; // player_key -> row values
			static int beats = 0;
			const bool full = g_roster_full.exchange(false) || ++beats % 20 == 0;
			std::unordered_map<std::string, std::string> now;
			for (const auto& o : online)
				now[o.key] = "('" + k + "','" + db.Esc(o.key) + "'," + std::to_string(o.player_id) + ",'" + db.Esc(o.name) + "','" + db.Esc(o.steam_name) +
					"'," + std::to_string(o.tribe_id) + ",'" + db.Esc(o.tribe) + "','" + db.Esc(o.platform) + "','" + db.Esc(o.discord_tag) + "'," + std::to_string(o.joined_at) + ")";
			bool ok = true;
			if (full)
				ok = db.Exec("DELETE FROM players_online WHERE server_key='" + k + "'") >= 0;
			std::string gone, upsert;
			for (const auto& [key, row] : (full ? decltype(last){} : last))
				if (!now.count(key)) gone += (gone.empty() ? "'" : ",'") + db.Esc(key) + "'";
			for (const auto& [key, row] : now)
			{
				const auto it = last.find(key);
				if (full || it == last.end() || it->second != row)
					upsert += (upsert.empty() ? "" : ",") + row;
			}
			if (ok && !gone.empty())
				ok = db.Exec("DELETE FROM players_online WHERE server_key='" + k + "' AND player_key IN (" + gone + ")") >= 0;
			if (ok && !upsert.empty())
				ok = db.Exec("REPLACE INTO players_online (server_key, player_key, player_id, name, steam_name, tribe_id, tribe_name, platform, discord_tag, joined_at) VALUES " + upsert) >= 0;
			if (ok) last = std::move(now);
			else g_roster_full = true;

			if (auto rows = db.Query("SELECT player_key, reason, until, muted_by FROM mutes WHERE until IS NULL OR until > UNIX_TIMESTAMP()"))
			{
				std::unordered_map<std::string, Mute> mutes;
				for (const auto& r : *rows)
					mutes[Opt(r, 0)] = Mute{ Opt(r, 1), OptI(r, 2), Opt(r, 3) };
				g_queue.Post([mutes] { g_mutes = mutes; });
			}
		});
	}

	void SaveMute(const std::string& key, const Mute& m)
	{
		g_mutes[key] = m;
		if (!g_worker)
			return;
		g_worker->Post([key, m](Db& db)
		{
			db.Exec("INSERT INTO mutes (player_key, reason, until, muted_by, created_at) VALUES ('" + db.Esc(key) + "','" + db.Esc(m.reason) + "'," +
				(m.until == 0 ? std::string("NULL") : std::to_string(m.until)) + ",'" + db.Esc(m.muted_by) + "',UNIX_TIMESTAMP()) " +
				"ON DUPLICATE KEY UPDATE reason=VALUES(reason), until=VALUES(until), muted_by=VALUES(muted_by), created_at=VALUES(created_at)");
		});
	}

	void DeleteMute(const std::string& key)
	{
		g_mutes.erase(key);
		if (g_worker)
			g_worker->Post([key](Db& db) { db.Exec("DELETE FROM mutes WHERE player_key='" + db.Esc(key) + "'"); });
	}

	void SavePrefs(const std::string& key, const std::set<std::string>& hidden)
	{
		if (!g_worker)
			return;
		const std::string csv = Csv(hidden);
		g_worker->Post([key, csv](Db& db)
		{
			db.Exec("INSERT INTO player_prefs (player_key, hidden_tags) VALUES ('" + db.Esc(key) + "','" + db.Esc(csv) +
				"') ON DUPLICATE KEY UPDATE hidden_tags=VALUES(hidden_tags)");
		});
	}

	// -----------------------------------------------------------------------------------------
	// Chat
	// -----------------------------------------------------------------------------------------

	// Label for the chat's senderPlatform number. Unknown numbers are logged once so they can be
	// named under "Platforms" in config.json.
	std::string PlatformName(int platform)
	{
		const auto it = g_cfg.platforms.find(platform);
		if (it != g_cfg.platforms.end())
			return it->second;
		static std::set<int> logged;
		if (logged.insert(platform).second)
			Log::GetLog()->info("ServerComms: chat from platform number {}; name it under \"Platforms\" in config.json", platform);
		return "Platform " + std::to_string(platform);
	}

	ChatMode ModeOf(EChatSendMode::Type mode)
	{
		switch (mode)
		{
		case EChatSendMode::GlobalTribeChat: return ChatMode::Tribe;
		case EChatSendMode::LocalChat: return ChatMode::Local;
		case EChatSendMode::AllianceChat: return ChatMode::Alliance;
		default: return ChatMode::Global;
		}
	}

	// Mute / word filter (every mode). Returns false when the message must not be sent.
	bool Allowed(AShooterPlayerController* pc, const std::string& key, PlayerInfo& p, const std::wstring& text)
	{
		const long long now = Now();
		if (auto it = g_mutes.find(key); it != g_mutes.end())
		{
			if (IsActive(it->second, now))
			{
				TellError(pc, MuteNotice(g_cfg.chat, it->second, now));
				return false;
			}
			g_mutes.erase(it);
		}
		if (ContainsBlockedWord(g_cfg.chat, text))
		{
			const Mute m{ "blocked word", now + static_cast<long long>(g_cfg.chat.word_filter.mute_minutes) * 60, "filter" };
			SaveMute(key, m);
			Log::GetLog()->info("ServerComms: {} ({}) muted by the word filter", p.name, key);
			TellError(pc, BlockedWordNotice(g_cfg.chat));
			return false;
		}
		return true;
	}

	void SendGlobal(AShooterPlayerController* pc, const std::string& key, PlayerInfo& p, const std::wstring& raw)
	{
		if (!p.admin)
		{
			const SpamResult spam = g_spam.Check(g_cfg.chat.anti_spam, key, raw, Steady());
			if (!spam.allowed)
			{
				TellError(pc, SpamNotice(g_cfg.chat, spam));
				return;
			}
		}
		const std::wstring text = ClampLength(g_cfg.chat, raw);
		const ResolvedStyle style = ResolveStyle(g_cfg.chat.styles, SenderOf(p, key));

		LineInput in;
		in.source_map = g_server_key;
		in.viewer_map = g_server_key;
		in.name = Utf8ToWide(p.name);
		in.tribe = Utf8ToWide(p.tribe);
		in.message = text;
		in.style = style;
		const FormattedLine line = FormatLine(g_cfg.chat, in);

		g_recent.Add({ key, in.name });
		ForEachPlayer([&](AShooterPlayerController* to)
		{
			const auto it = g_players.find(IdOf(to));
			if (it != g_players.end() && it->second.ignores.count(key))
				return;
			SendLine(to, line);
		});

		if (g_cfg.chat.relay_global)
			QueueOut({ Now(), key, p.name, style.hide_tribe ? std::string() : p.tribe, style.discord_tag, StyleJson(style), WideToUtf8(text), p.platform });
	}
}

// ---------------------------------------------------------------------------------------------
// Hooks
// ---------------------------------------------------------------------------------------------

DECLARE_HOOK(AShooterPlayerController_ServerSendChatMessage_Implementation, void, AShooterPlayerController*, FString*, EChatSendMode::Type, int);
DECLARE_HOOK(AShooterPlayerController_ClientChatMessage, void, AShooterPlayerController*, FPrimalChatMessage);

void Hook_AShooterPlayerController_ServerSendChatMessage_Implementation(AShooterPlayerController* pc, FString* message, EChatSendMode::Type mode, int platform)
{
	if (!g_alive || !g_world_ready || !pc || !message)
	{
		AShooterPlayerController_ServerSendChatMessage_Implementation_original(pc, message, mode, platform);
		return;
	}
	try
	{
		const std::wstring text = ToW(*message);
		if (IsCommand(g_cfg.chat, text))
		{
			AShooterPlayerController_ServerSendChatMessage_Implementation_original(pc, message, mode, platform);
			return;
		}
		const std::string key = IdOf(pc);
		auto it = g_players.find(key);
		if (key.empty() || it == g_players.end())
		{
			AShooterPlayerController_ServerSendChatMessage_Implementation_original(pc, message, mode, platform);
			return;
		}
		PlayerInfo& p = it->second;
		p.platform = PlatformName(platform);
		if (!Allowed(pc, key, p, text))
			return;

		const ChatMode chat_mode = ModeOf(mode);
		if (chat_mode == ChatMode::Global && g_cfg.chat.relay_global)
		{
			SendGlobal(pc, key, p, text);
			return;
		}

		// Non-global (or relay off): the game routes it; ClientChatMessage restyles each copy.
		g_restyle = Restyle{ chat_mode, key, ResolveStyle(g_cfg.chat.styles, SenderOf(p, key)) };
		AShooterPlayerController_ServerSendChatMessage_Implementation_original(pc, message, mode, platform);
		g_restyle.reset();
	}
	catch (const std::exception& e)
	{
		g_restyle.reset();
		Log::GetLog()->error("ServerComms: chat hook error: {}", e.what());
		AShooterPlayerController_ServerSendChatMessage_Implementation_original(pc, message, mode, platform);
	}
}

void Hook_AShooterPlayerController_ClientChatMessage(AShooterPlayerController* pc, FPrimalChatMessage chat)
{
	if (g_sending_own || !g_restyle || !pc)
	{
		AShooterPlayerController_ClientChatMessage_original(pc, chat);
		return;
	}
	try
	{
		const auto it = g_players.find(IdOf(pc));
		if (it != g_players.end() && it->second.ignores.count(g_restyle->sender_key))
			return;

		if (!g_cfg.chat.nonglobal_full)
		{
			// Game header (name, tribe, mode) untouched; only the message part is styled.
			const std::wstring styled = StyleMessageOnly(g_cfg.chat, g_restyle->style, ToW(chat.Message));
			chat.Message = FString(styled.c_str());
			if (g_cfg.chat.icons_enabled && !g_restyle->style.icon.empty() && g_cfg.chat.safe_icons.count(g_restyle->style.icon))
				chat.SenderIcon = LoadIcon(g_restyle->style.icon);
			AShooterPlayerController_ClientChatMessage_original(pc, chat);
			return;
		}

		LineInput in;
		in.mode = g_restyle->mode;
		in.source_map = g_server_key;
		in.viewer_map = g_server_key;
		in.name = ToW(chat.SenderName);
		in.tribe = ToW(chat.SenderTribeName);
		in.message = ToW(chat.Message);
		in.style = g_restyle->style;
		const FormattedLine line = FormatLine(g_cfg.chat, in);

		const bool split = g_cfg.chat.use_sender_field && !line.sender.empty();
		chat.SenderName = FString(split ? line.sender.c_str() : L"");
		chat.SenderTribeName = FString(L"");
		chat.Message = FString(split ? line.body.c_str() : line.text.c_str());
		chat.SenderIcon = line.icon.empty() ? nullptr : LoadIcon(line.icon);
	}
	catch (...) {}
	AShooterPlayerController_ClientChatMessage_original(pc, chat);
}

// ---------------------------------------------------------------------------------------------
// Player chat commands
// ---------------------------------------------------------------------------------------------

namespace
{
	PlayerInfo* InfoOf(AShooterPlayerController* pc, std::string* key_out = nullptr)
	{
		const std::string key = IdOf(pc);
		if (key_out) *key_out = key;
		const auto it = g_players.find(key);
		return it == g_players.end() ? nullptr : &it->second;
	}

	void CmdLink(AShooterPlayerController* pc, FString* message, int, int)
	{
		std::string key;
		PlayerInfo* p = InfoOf(pc, &key);
		if (!p || !message)
			return;
		const ParsedCommand c = ParseCommand(ToW(*message));
		const std::string code = c.args.empty() ? std::string() : NormalizeLinkCode(c.args[0]);
		if (code.empty())
		{
			Tell(pc, Fill(g_cfg.chat.messages.link_syntax, { { "command", g_cfg.chat.commands.link } }));
			return;
		}
		if (!g_worker)
			return;
		Tell(pc, g_cfg.chat.messages.link_checking);
		const PlayerInfo info = *p;
		g_worker->Post([key, code, info](Db& db)
		{
			std::string reply;
			auto r = db.Query("SELECT discord_id FROM link_codes WHERE code='" + db.Esc(code) + "' AND expires_at > UNIX_TIMESTAMP()");
			if (!r)
				reply = "Linking is unavailable right now, try again shortly.";
			else if (r->empty())
				reply = g_cfg.chat.messages.link_invalid;
			else
			{
				const std::string discord = Opt(r->front(), 0);
				auto existing = db.Query("SELECT discord_id FROM links WHERE player_key='" + db.Esc(key) + "'");
				if (existing && !existing->empty() && Opt(existing->front(), 0) == discord)
					reply = g_cfg.chat.messages.link_already;
				else
				{
					db.Exec("START TRANSACTION");
					db.Exec("DELETE FROM links WHERE player_key='" + db.Esc(key) + "' OR discord_id=" + discord);
					const long long n = db.Exec("INSERT INTO links (discord_id, player_key, player_id, tribe_id, steam_name, linked_at, last_seen) VALUES (" +
						discord + ",'" + db.Esc(key) + "'," + std::to_string(info.player_id) + "," + std::to_string(info.tribe_id) + ",'" +
						db.Esc(info.steam_name) + "',UNIX_TIMESTAMP(),UNIX_TIMESTAMP())");
					db.Exec("DELETE FROM link_codes WHERE code='" + db.Esc(code) + "'");
					db.Exec(n < 0 ? "ROLLBACK" : "COMMIT");
					reply = n < 0 ? "Linking failed, try again shortly." : g_cfg.chat.messages.link_success;
					if (n >= 0)
						Log::GetLog()->info("ServerComms: {} linked to Discord {}", key, discord);
				}
			}
			g_queue.Post([key, reply] { if (auto* pc = FindOnline(key)) Tell(pc, reply); });
		});
	}

	void CmdHideTag(AShooterPlayerController* pc, FString*, int, int)
	{
		std::string key;
		PlayerInfo* p = InfoOf(pc, &key);
		if (!p)
			return;
		const std::string tag = HideableTag(g_cfg.chat.styles, SenderOf(*p, key));
		if (tag.empty())
		{
			Tell(pc, g_cfg.chat.messages.tag_none);
			return;
		}
		p->hidden_tags.insert(tag);
		SavePrefs(key, p->hidden_tags);
		Tell(pc, Fill(g_cfg.chat.messages.tag_hidden, { { "tag", tag } }));
	}

	void CmdShowTag(AShooterPlayerController* pc, FString*, int, int)
	{
		std::string key;
		PlayerInfo* p = InfoOf(pc, &key);
		if (!p)
			return;
		const auto tags = ShowableTags(g_cfg.chat.styles, SenderOf(*p, key));
		if (tags.empty())
		{
			Tell(pc, g_cfg.chat.messages.tag_none);
			return;
		}
		for (const auto& t : tags)
			p->hidden_tags.erase(t);
		SavePrefs(key, p->hidden_tags);
		Tell(pc, Fill(g_cfg.chat.messages.tag_shown, { { "tag", tags.front() } }));
	}

	void ShowPick(AShooterPlayerController* pc, PlayerInfo& p, const std::vector<Speaker>& list, bool unignore, const std::string& command)
	{
		p.last_pick = list;
		p.last_pick_unignore = unignore;
		p.last_pick_at = Now();
		std::string text = Fill(g_cfg.chat.messages.ignore_pick, { { "command", command } });
		for (size_t i = 0; i < list.size(); ++i)
			text += "\n" + std::to_string(i + 1) + ". " + WideToUtf8(list[i].name);
		Tell(pc, text);
	}

	void PickIgnore(AShooterPlayerController* pc, FString* message, bool unignore)
	{
		std::string key;
		PlayerInfo* p = InfoOf(pc, &key);
		if (!p || !message)
			return;
		if (!g_worker)
			return;
		const std::string& command = unignore ? g_cfg.chat.commands.unignore : g_cfg.chat.commands.ignore;
		const ParsedCommand c = ParseCommand(ToW(*message));
		if (c.args.empty())
		{
			std::vector<Speaker> list;
			if (unignore)
				for (const auto& [k, name] : p->ignores) list.push_back({ k, Utf8ToWide(name) });
			else
				for (const auto& s : g_recent.For(key)) if (!p->ignores.count(s.player_key)) list.push_back(s);
			if (list.empty())
			{
				Tell(pc, unignore ? g_cfg.chat.messages.unignore_none : g_cfg.chat.messages.ignore_none);
				return;
			}
			ShowPick(pc, *p, list, unignore, command);
			return;
		}
		const bool fresh = p->last_pick_unignore == unignore && Now() - p->last_pick_at < 300;
		const auto choice = fresh ? ParseChoice(c.args[0], p->last_pick.size()) : std::nullopt;
		if (!choice)
		{
			TellError(pc, Fill(g_cfg.chat.messages.ignore_bad_choice, { { "choice", WideToUtf8(c.args[0]) } }));
			return;
		}
		const Speaker target = p->last_pick[*choice];
		const std::string name = WideToUtf8(target.name);
		if (unignore)
		{
			p->ignores.erase(target.player_key);
			g_worker->Post([key, target](Db& db)
			{
				db.Exec("DELETE FROM ignores WHERE player_key='" + db.Esc(key) + "' AND ignored_key='" + db.Esc(target.player_key) + "'");
			});
			Tell(pc, Fill(g_cfg.chat.messages.unignore_done, { { "name", name } }));
		}
		else
		{
			p->ignores[target.player_key] = name;
			g_worker->Post([key, target, name](Db& db)
			{
				db.Exec("INSERT INTO ignores (player_key, ignored_key, ignored_name, created_at) VALUES ('" + db.Esc(key) + "','" +
					db.Esc(target.player_key) + "','" + db.Esc(name) + "',UNIX_TIMESTAMP()) ON DUPLICATE KEY UPDATE ignored_name=VALUES(ignored_name)");
			});
			Tell(pc, Fill(g_cfg.chat.messages.ignore_done, { { "name", name } }));
		}
		p->last_pick.clear();
	}

	void CmdIgnore(AShooterPlayerController* pc, FString* message, int, int) { PickIgnore(pc, message, false); }
	void CmdUnignore(AShooterPlayerController* pc, FString* message, int, int) { PickIgnore(pc, message, true); }

	void RegisterChatCommands()
	{
		auto& commands = AsaApi::GetCommands();
		const auto& c = g_cfg.chat.commands;
		const std::pair<std::string, void (*)(AShooterPlayerController*, FString*, int, int)> list[] = {
			{ c.link, &CmdLink }, { c.hide_tag, &CmdHideTag }, { c.show_tag, &CmdShowTag }, { c.ignore, &CmdIgnore }, { c.unignore, &CmdUnignore } };
		for (const auto& [name, fn] : list)
		{
			if (name.empty())
				continue;
			const std::wstring w = Utf8ToWide(name);
			commands.AddChatCommand(FString(w.c_str()), fn);
			g_chat_commands.push_back(w);
		}
	}

	void UnregisterChatCommands()
	{
		auto& commands = AsaApi::GetCommands();
		for (const auto& w : g_chat_commands)
			commands.RemoveChatCommand(FString(w.c_str()));
		g_chat_commands.clear();
	}
}

// ---------------------------------------------------------------------------------------------
// Admin commands (RCON + admin console)
// ---------------------------------------------------------------------------------------------

namespace
{
	std::vector<std::string> Words(const std::string& text)
	{
		std::vector<std::string> out;
		std::string cur;
		for (char c : text + " ")
		{
			if (c == ' ' || c == '\t') { if (!cur.empty()) out.push_back(cur); cur.clear(); }
			else cur.push_back(c);
		}
		return out;
	}

	// Player argument: EOS ID (online or not) or an online character name.
	std::string ResolveKey(const std::string& who)
	{
		if (IsPlayerKey(Lower(who)))
			return Lower(who);
		if (auto* pc = FindPlayer(who))
			return IdOf(pc);
		return {};
	}

	std::string CmdMute(const std::vector<std::string>& a, const std::string& by)
	{
		if (a.size() < 4)
			return "Usage: ServerComms.Mute <EOS ID|character name> <minutes|perm> <reason>";
		const std::string key = ResolveKey(a[1]);
		if (key.empty())
			return "Player not found: " + a[1] + " (use an EOS ID for offline players)";
		const auto length = ParseMuteLength(a[2]);
		if (!length)
			return "Length must be a number of minutes or perm";
		std::string reason;
		for (size_t i = 3; i < a.size(); ++i) reason += (i > 3 ? " " : "") + a[i];
		const Mute m{ reason, *length == 0 ? 0 : Now() + *length, by };
		SaveMute(key, m);
		if (auto* pc = FindOnline(key))
			TellError(pc, MuteNotice(g_cfg.chat, m, Now()));
		Log::GetLog()->info("ServerComms: {} muted by {} ({}): {}", key, by, a[2], reason);
		auto* online = FindOnline(key);
		const std::string who = online ? ToStd(AsaApi::IApiUtils::GetCharacterName(online)) + " (" + key + ")" : key;
		return "Muted " + who + (*length == 0 ? " permanently" : " for " + FormatDuration(*length)) + ": " + reason;
	}

	std::string CmdUnmute(const std::vector<std::string>& a)
	{
		if (a.size() < 2)
			return "Usage: ServerComms.Unmute <EOS ID|character name>";
		const std::string key = ResolveKey(a[1]);
		if (key.empty())
			return "Player not found: " + a[1];
		DeleteMute(key);
		auto* online = FindOnline(key);
		return "Unmuted " + (online ? ToStd(AsaApi::IApiUtils::GetCharacterName(online)) + " (" + key + ")" : key);
	}

	std::string CmdTestIcon(const std::vector<std::string>& a)
	{
		if (a.size() < 3)
			return "Usage: ServerComms.TestIcon <icon path> <EOS ID|character name>";
		auto* pc = FindPlayer(a[2]);
		if (!pc)
			return "Player not online: " + a[2];
		if (!LoadIcon(a[1]))
			return "Not a loadable texture on this server: " + a[1];
		FormattedLine line;
		line.text = L"ServerComms icon test: if you can read this, the icon is safe on your client.";
		line.icon = a[1];
		SendLine(pc, line, Utf8ToWide(g_cfg.chat.messages.sender));
		return "Sent a test line with that icon to " + ToStd(AsaApi::IApiUtils::GetCharacterName(pc)) + " only";
	}

	std::string Reload();

	std::string Dispatch(const std::string& body, const std::string& by)
	{
		const auto a = Words(body);
		if (a.empty())
			return {};
		const std::string cmd = Lower(a[0]);
		try
		{
			if (cmd == "servercomms.reload") return Reload();
			if (!g_alive) return "ServerComms is not running (check ArkApi.log, fix config.json, then ServerComms.Reload)";
			if (cmd == "servercomms.mute") return CmdMute(a, by);
			if (cmd == "servercomms.unmute") return CmdUnmute(a);
			if (cmd == "servercomms.testicon") return CmdTestIcon(a);
		}
		catch (const std::exception& e)
		{
			return std::string("Error: ") + e.what();
		}
		return {};
	}

	const wchar_t* kCommands[] = { L"ServerComms.Reload", L"ServerComms.Mute", L"ServerComms.Unmute", L"ServerComms.TestIcon" };

	void OnRcon(RCONClientConnection* conn, RCONPacket* packet, UWorld*)
	{
		FString reply = ToF(Dispatch(ToStd(packet->Body), "rcon"));
		conn->SendMessageW(packet->Id, 0, &reply);
	}

	void OnConsole(APlayerController* player, FString* cmd, bool)
	{
		auto* pc = static_cast<AShooterPlayerController*>(player);
		if (!pc || !cmd || !pc->bIsAdmin().Get())
			return;
		Tell(pc, Dispatch(ToStd(*cmd), ToStd(AsaApi::IApiUtils::GetCharacterName(pc))), FLinearColor(0.4f, 1.0f, 0.4f, 1.0f));
	}
}

// ---------------------------------------------------------------------------------------------
// Timer, config, load / unload
// ---------------------------------------------------------------------------------------------

namespace
{
	const char* kHookSend = "AShooterPlayerController.ServerSendChatMessage_Implementation(FString&,EChatSendMode::Type,int)";
	const char* kHookClient = "AShooterPlayerController.ClientChatMessage(FPrimalChatMessage)";

	std::string MapName()
	{
		UWorld* world = AsaApi::GetApiUtils().GetWorld();
		if (!world)
			return {};
		FString name;
		world->GetMapName(&name);
		std::string map = ToStd(name);
		if (map.size() > 3 && map.compare(map.size() - 3, 3, "_WP") == 0)
			map.resize(map.size() - 3); // Ragnarok_WP -> Ragnarok
		return map;
	}

	std::string DisplayName()
	{
		const auto map = g_cfg.chat.maps.find(g_server_key);
		return map != g_cfg.chat.maps.end() ? map->second.label : g_server_key;
	}

	// Launch line and GameUserSettings.ini: RCON address/port and -ServerKey=. Works before the world
	// is loaded (no game calls).
	void ResolveAddress()
	{
		g_address = ParseCommandLine(WideToUtf8(GetCommandLineW()));
		if (g_address.rcon_port == 0)
		{
			// Not on the launch line: read [ServerSettings] RCONPort from GameUserSettings.ini.
			std::ifstream ini(fs::path(AsaApi::Tools::GetCurrentDir()) / ".." / ".." / "Saved" / "Config" / "WindowsServer" / "GameUserSettings.ini");
			const std::string text((std::istreambuf_iterator<char>(ini)), std::istreambuf_iterator<char>());
			try { g_address.rcon_port = std::stoi(IniValue(text, "ServerSettings", "RCONPort")); }
			catch (...) {}
		}
	}

	// Boot heartbeat: while the world is still loading (ASE can take minutes), a background thread
	// marks this map "starting" every HeartbeatSeconds so Discord shows Starting instead of Offline,
	// and clears its stale online-player rows from before the restart. It runs off the game thread
	// and only queues work for the database worker. Needs the server key before the world exists, so
	// it only runs when ServerKey is set in config or on the launch line.
	std::thread g_boot_thread;
	std::mutex g_boot_mutex;
	std::condition_variable g_boot_cv;
	bool g_booting = false; // guarded by g_boot_mutex
	std::string g_boot_display;    // fixed at boot start: the boot thread never reads g_cfg
	int g_boot_interval = 15;

	void PostBootHeartbeat()
	{
		if (!g_worker)
			return;
		g_worker->Post([](Db& db)
		{
			const std::string k = db.Esc(g_server_key);
			db.Exec("INSERT INTO servers (server_key, display_name, players, rcon_host, rcon_port, started_at, last_heartbeat, state) VALUES ('" +
				k + "','" + db.Esc(g_boot_display) + "',0,'" + db.Esc(g_address.multihome) + "'," + std::to_string(g_address.rcon_port) + "," +
				std::to_string(g_started_at) + ",UNIX_TIMESTAMP(),'starting') ON DUPLICATE KEY UPDATE display_name=VALUES(display_name), " +
				"players=0, rcon_host=VALUES(rcon_host), rcon_port=VALUES(rcon_port), started_at=VALUES(started_at), " +
				"last_heartbeat=VALUES(last_heartbeat), state='starting'");
			db.Exec("DELETE FROM players_online WHERE server_key='" + k + "'");
		});
	}

	void StartBootHeartbeat()
	{
		{
			std::lock_guard lock(g_boot_mutex);
			g_booting = true;
		}
		g_boot_thread = std::thread([]
		{
			std::unique_lock lock(g_boot_mutex);
			while (g_booting)
			{
				lock.unlock();
				PostBootHeartbeat();
				lock.lock();
				g_boot_cv.wait_for(lock, std::chrono::seconds(g_boot_interval), [] { return !g_booting; });
			}
		});
	}

	void StopBootHeartbeat()
	{
		{
			std::lock_guard lock(g_boot_mutex);
			g_booting = false;
		}
		g_boot_cv.notify_all();
		if (g_boot_thread.joinable())
			g_boot_thread.join();
	}

	void StartWorker()
	{
		if (g_worker)
			return;
		g_worker = std::make_unique<DbWorker>(g_cfg.db);
		g_worker->Start();
		EnsureSchema();
	}

	// Called from Start(): if the server key is already known, connect now and report "starting".
	void BeginBoot()
	{
		ResolveAddress();
		const std::string key = !g_cfg.server_key.empty() ? g_cfg.server_key : g_address.server_key;
		if (key.empty())
			return;   // key comes from the map name once the world exists
		g_server_key = key;
		g_started_at = Now();
		g_boot_display = DisplayName();
		g_boot_interval = g_cfg.chat.heartbeat_seconds;
		StartWorker();
		StartBootHeartbeat();
	}

	void OnWorldReady()
	{
		StopBootHeartbeat();
		if (g_server_key.empty())
		{
			ResolveAddress();
			g_server_key = MapName();
			g_started_at = Now();
		}
		ValidateIcons(g_cfg.chat);
		Log::GetLog()->info("ServerComms: running as \"{}\" (RCON {}:{})", g_server_key,
			g_address.multihome.empty() ? "127.0.0.1" : g_address.multihome, g_address.rcon_port);
		StartWorker();
		InitCursor();
	}

	long long g_last_slow_log = 0;

	void TickWork();

	// Times the plugin's per-second work and logs (at most once a minute) when it takes longer than
	// Diagnostics.SlowTickMs, so its share of the frame budget can be checked on a busy server.
	void Tick()
	{
		if (!g_alive)
			return;
		const auto start = std::chrono::steady_clock::now();
		TickWork();
		const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
		// The first tick after the world is ready does one-time startup work (icons, ini, worker) and
		// is not counted.
		if (g_tick > 1 && ms > g_cfg.slow_tick_ms && Now() - g_last_slow_log >= 60)
		{
			g_last_slow_log = Now();
			Log::GetLog()->warn("ServerComms: per-second work took {:.2f} ms ({} players online, limit {:.1f} ms)", ms, g_players.size(), g_cfg.slow_tick_ms);
		}
	}

	void TickWork()
	{
		if (!g_world_ready)
		{
			if (AsaApi::GetApiUtils().GetStatus() != AsaApi::ServerStatus::Ready || !AsaApi::GetApiUtils().GetWorld())
				return;
			g_world_ready = true;
			OnWorldReady();
		}

		++g_tick;
		g_queue.Drain();
		ScanPlayers();
		if (AShooterGameState* gs = AsaApi::GetApiUtils().GetGameState())
			g_fps_samples.push_back(gs->ServerFramerateField());

		if (!g_cursor_ready && g_tick % 10 == 0)
			InitCursor();
		if (g_tick % g_cfg.chat.poll_seconds == 0)
			Poll();
		if (g_tick % g_cfg.chat.heartbeat_seconds == 1 % g_cfg.chat.heartbeat_seconds)
			Heartbeat();
		if (g_tick % 5 == 0 && OutboxPending())
			FlushOutbox(); // retry after a database outage
	}

	Config ReadConfig()
	{
		const fs::path path = PluginDir() / "config.json";
		std::ifstream file(path);
		if (!file.is_open())
			throw std::runtime_error("Can't open " + path.string());
		const nlohmann::json j = nlohmann::json::parse(file, nullptr, true, true);

		Config cfg;
		if (j.contains("Database"))
		{
			const auto& d = j.at("Database");
			cfg.db.host = d.value("Host", "127.0.0.1");
			cfg.db.port = d.value("Port", 3306u);
			cfg.db.user = d.value("User", "");
			cfg.db.password = d.value("Password", "");
			cfg.db.database = d.value("Name", "servercomms");
			cfg.db.ssl = d.value("Ssl", false);
		}
		cfg.server_key = j.value("ServerKey", "");
		if (j.contains("Platforms") && j.at("Platforms").is_object())
		{
			cfg.platforms.clear();
			for (const auto& [id, label] : j.at("Platforms").items())
				try { cfg.platforms[std::stoi(id)] = label.get<std::string>(); } catch (...) {}
		}
		if (j.contains("Diagnostics"))
			cfg.slow_tick_ms = (std::max)(0.1, j.at("Diagnostics").value("SlowTickMs", 2.0));
		cfg.chat = ParseSettings(j);
		for (const auto& w : cfg.chat.warnings)
			Log::GetLog()->warn("ServerComms config: {}", w);
		return cfg;
	}

	bool SameDb(const DbConfig& a, const DbConfig& b)
	{
		return a.host == b.host && a.port == b.port && a.user == b.user && a.password == b.password && a.database == b.database && a.ssl == b.ssl;
	}

	void Start()
	{
		auto& hooks = AsaApi::GetHooks();
		hooks.SetHook(kHookSend, &Hook_AShooterPlayerController_ServerSendChatMessage_Implementation,
			&AShooterPlayerController_ServerSendChatMessage_Implementation_original);
		hooks.SetHook(kHookClient, &Hook_AShooterPlayerController_ClientChatMessage, &AShooterPlayerController_ClientChatMessage_original);

		auto& commands = AsaApi::GetCommands();
		for (const wchar_t* c : kCommands)
		{
			if (std::wstring(c) == L"ServerComms.Reload")
				continue; // registered once in Plugin_Init
			commands.AddRconCommand(c, &OnRcon);
			commands.AddConsoleCommand(c, &OnConsole);
		}
		RegisterChatCommands();

		g_world_ready = false;
		g_tick = 0;
		g_alive = true;
		BeginBoot();
		commands.AddOnTimerCallback(L"ServerComms_Tick", &Tick);
	}

	std::string Reload()
	{
		Config next;
		try
		{
			next = ReadConfig();
		}
		catch (const std::exception& e)
		{
			Log::GetLog()->error("ServerComms: config reload failed: {}", e.what());
			return std::string("ServerComms: reload failed - ") + e.what() + (g_alive ? " (previous settings still active)" : "");
		}

		if (!g_alive)
		{
			g_cfg = std::move(next);
			Start();
			return "ServerComms: config loaded, plugin started";
		}

		const bool db_changed = !SameDb(g_cfg.db, next.db);
		const bool key_changed = next.server_key != g_cfg.server_key;
		UnregisterChatCommands();
		g_cfg = std::move(next);
		if (g_world_ready)
			ValidateIcons(g_cfg.chat);
		RegisterChatCommands();

		if (db_changed && g_world_ready)
		{
			if (g_worker) g_worker->Stop();
			g_cursor_ready = false;
			g_poll_busy = false;
			g_flush_busy = false;
			g_roster_full = true;
			g_worker = std::make_unique<DbWorker>(g_cfg.db);
			g_worker->Start();
			InitCursor();
		}
		const size_t warnings = g_cfg.chat.warnings.size();
		std::string text = "ServerComms: config reloaded";
		if (warnings) text += " with " + std::to_string(warnings) + " warning(s), see ArkApi.log";
		if (key_changed) text += ". ServerKey changes apply after a restart";
		Log::GetLog()->info("{}", text);
		return text;
	}
}

extern "C" __declspec(dllexport) void Plugin_Init()
{
	Log::Get().Init(PROJECT_NAME);

	// Registered first and kept even if config.json is broken, so it can be fixed without a restart.
	auto& commands = AsaApi::GetCommands();
	commands.AddRconCommand(L"ServerComms.Reload", &OnRcon);
	commands.AddConsoleCommand(L"ServerComms.Reload", &OnConsole);

	try
	{
		g_cfg = ReadConfig();
	}
	catch (const std::exception& e)
	{
		Log::GetLog()->critical("ServerComms: config error, plugin not started: {}", e.what());
		Log::GetLog()->critical("ServerComms: fix config.json and run ServerComms.Reload");
		return;
	}
	Start();
}

namespace
{
	// plugins.unload runs inside AsaApi's command loop, which iterates a copy of the command list
	// (Commands.h, CheckCommands). That copy still holds the commands we remove below, and it is only
	// destroyed after AsaApi has freed this DLL. Destroying them then calls std::function code that lived
	// in this DLL, an access violation that takes the server down. So hold one extra reference to our own
	// module and drop it a few seconds later from a thread that ends inside kernel32
	// (FreeLibraryAndExitThread), never returning into the unloaded DLL.
	void DeferModuleRelease()
	{
		HMODULE self = nullptr;
		if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
			reinterpret_cast<LPCWSTR>(&DeferModuleRelease), &self))
			return;
		HANDLE thread = CreateThread(nullptr, 0, [](LPVOID module) -> DWORD
		{
			Sleep(5000);
			FreeLibraryAndExitThread(static_cast<HMODULE>(module), 0);
			return 0; // not reached
		}, self, 0, nullptr);
		// No thread: keep the reference. A DLL that stays mapped is harmless; a crash is not.
		if (thread)
			CloseHandle(thread);
	}
}

extern "C" __declspec(dllexport) void Plugin_Unload()
{
	DeferModuleRelease();

	auto& commands = AsaApi::GetCommands();
	commands.RemoveRconCommand(L"ServerComms.Reload");
	commands.RemoveConsoleCommand(L"ServerComms.Reload");

	if (!g_alive)
		return;
	g_alive = false;

	auto& hooks = AsaApi::GetHooks();
	hooks.DisableHook(kHookSend, &Hook_AShooterPlayerController_ServerSendChatMessage_Implementation);
	hooks.DisableHook(kHookClient, &Hook_AShooterPlayerController_ClientChatMessage);

	for (const wchar_t* c : kCommands)
	{
		if (std::wstring(c) == L"ServerComms.Reload")
			continue;
		commands.RemoveRconCommand(c);
		commands.RemoveConsoleCommand(c);
	}
	UnregisterChatCommands();
	commands.RemoveOnTimerCallback(L"ServerComms_Tick");

	StopBootHeartbeat();
	// Stop() runs the jobs still queued (unsent chat) before returning.
	if (g_worker)
	{
		g_worker->Stop();
		g_worker.reset();
	}
	g_queue.Clear();
	g_players.clear();
	g_restyle.reset();
}
