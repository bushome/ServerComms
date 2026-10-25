#pragma once

// Pure chat logic for ServerComms: config parsing, chat styling and formatting, rich-text escaping,
// command detection, anti-spam, word filter, mute messages and server command-line parsing.
//
// This header has no dependency on the game or on server API game types (only on the nlohmann
// json.hpp that ships with AsaApi and AseApi), so it compiles and is unit-tested on any machine and is
// shared unchanged by the ASA and ASE builds. See tests/logic_tests.cpp.
//
// Chat text is std::wstring (the game's FString is wide). Config, keys and database values are UTF-8
// std::string; Utf8ToWide / WideToUtf8 convert between them.

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cwctype>
#include <deque>
#include <map>
#include <memory>
#include <optional>
#include <regex>
#include <set>
#include <string>
#include <unordered_map>
#include <vector>

#include "json.hpp"

namespace ServerComms
{
	// ------------------------------------------------------------------------------------------
	// UTF-8 <-> wide (UTF-16 on Windows, UTF-32 elsewhere)
	// ------------------------------------------------------------------------------------------

	inline std::wstring Utf8ToWide(const std::string& in)
	{
		std::wstring out;
		out.reserve(in.size());
		size_t i = 0;
		while (i < in.size())
		{
			const unsigned char c = static_cast<unsigned char>(in[i]);
			uint32_t cp = 0;
			size_t len = 0;
			if (c < 0x80) { cp = c; len = 1; }
			else if ((c >> 5) == 0x6) { cp = c & 0x1F; len = 2; }
			else if ((c >> 4) == 0xE) { cp = c & 0x0F; len = 3; }
			else if ((c >> 3) == 0x1E) { cp = c & 0x07; len = 4; }
			else { cp = 0xFFFD; len = 1; }
			if (i + len > in.size()) { cp = 0xFFFD; len = in.size() - i; }
			else
			{
				for (size_t k = 1; k < len; ++k)
				{
					const unsigned char cc = static_cast<unsigned char>(in[i + k]);
					if ((cc >> 6) != 0x2) { cp = 0xFFFD; len = k; break; }
					cp = (cp << 6) | (cc & 0x3F);
				}
			}
			i += len;
			if constexpr (sizeof(wchar_t) == 2)
			{
				if (cp >= 0x10000)
				{
					cp -= 0x10000;
					out.push_back(static_cast<wchar_t>(0xD800 + (cp >> 10)));
					out.push_back(static_cast<wchar_t>(0xDC00 + (cp & 0x3FF)));
					continue;
				}
			}
			out.push_back(static_cast<wchar_t>(cp));
		}
		return out;
	}

	inline std::string WideToUtf8(const std::wstring& in)
	{
		std::string out;
		out.reserve(in.size());
		for (size_t i = 0; i < in.size(); ++i)
		{
			uint32_t cp = static_cast<uint32_t>(in[i]);
			if constexpr (sizeof(wchar_t) == 2)
			{
				if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < in.size())
				{
					const uint32_t lo = static_cast<uint32_t>(in[i + 1]);
					if (lo >= 0xDC00 && lo <= 0xDFFF) { cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00); ++i; }
				}
			}
			if (cp < 0x80) out.push_back(static_cast<char>(cp));
			else if (cp < 0x800) { out.push_back(static_cast<char>(0xC0 | (cp >> 6))); out.push_back(static_cast<char>(0x80 | (cp & 0x3F))); }
			else if (cp < 0x10000) { out.push_back(static_cast<char>(0xE0 | (cp >> 12))); out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F))); out.push_back(static_cast<char>(0x80 | (cp & 0x3F))); }
			else { out.push_back(static_cast<char>(0xF0 | (cp >> 18))); out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F))); out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F))); out.push_back(static_cast<char>(0x80 | (cp & 0x3F))); }
		}
		return out;
	}

	// ------------------------------------------------------------------------------------------
	// Small string helpers
	// ------------------------------------------------------------------------------------------

	inline std::string Lower(std::string s)
	{
		for (auto& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
		return s;
	}

	inline std::wstring LowerW(std::wstring s)
	{
		for (auto& c : s) c = static_cast<wchar_t>(std::towlower(static_cast<wint_t>(c)));
		return s;
	}

	inline std::wstring TrimW(const std::wstring& s)
	{
		const auto b = s.find_first_not_of(L" \t\r\n");
		if (b == std::wstring::npos) return L"";
		const auto e = s.find_last_not_of(L" \t\r\n");
		return s.substr(b, e - b + 1);
	}

	inline void ReplaceAll(std::wstring& s, const std::wstring& from, const std::wstring& to)
	{
		if (from.empty()) return;
		size_t pos = 0;
		while ((pos = s.find(from, pos)) != std::wstring::npos)
		{
			s.replace(pos, from.size(), to);
			pos += to.size();
		}
	}

	// Player and Discord text must never be able to open a rich-text tag (<RichColor ...>) and fake a
	// colored or system message. '<' is replaced with a look-alike that the chat renders but does not
	// parse (U+2039 single left-pointing angle quotation mark).
	inline std::wstring EscapeRich(std::wstring s)
	{
		std::replace(s.begin(), s.end(), L'<', static_cast<wchar_t>(0x2039));
		return s;
	}

	// Formats seconds as "45s", "3m 12s", "1h 5m", "2d 3h".
	inline std::string FormatDuration(long long seconds)
	{
		if (seconds < 0) seconds = 0;
		const long long d = seconds / 86400, h = (seconds % 86400) / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
		char buf[48];
		if (d > 0) std::snprintf(buf, sizeof(buf), h ? "%lldd %lldh" : "%lldd", d, h);
		else if (h > 0) std::snprintf(buf, sizeof(buf), m ? "%lldh %lldm" : "%lldh", h, m);
		else if (m > 0) std::snprintf(buf, sizeof(buf), s ? "%lldm %llds" : "%lldm", m, s);
		else std::snprintf(buf, sizeof(buf), "%llds", s);
		return buf;
	}

	// Replaces {key} placeholders in a UTF-8 template.
	inline std::string Fill(std::string text, const std::map<std::string, std::string>& values)
	{
		for (const auto& [key, value] : values)
		{
			const std::string token = "{" + key + "}";
			size_t pos = 0;
			while ((pos = text.find(token, pos)) != std::string::npos)
			{
				text.replace(pos, token.size(), value);
				pos += value.size();
			}
		}
		return text;
	}

	// ------------------------------------------------------------------------------------------
	// Colors
	// ------------------------------------------------------------------------------------------

	// Parses "R, G, B, A" (0..1 floats, alpha optional). Out-of-range values are clamped and reported.
	inline std::optional<std::string> NormalizeColor(const std::string& text, std::string* warning = nullptr)
	{
		std::vector<double> parts;
		std::string cur;
		for (char c : text + ",")
		{
			if (c == ',')
			{
				const auto b = cur.find_first_not_of(" \t");
				const auto e = cur.find_last_not_of(" \t");
				if (b == std::string::npos) return std::nullopt;
				try { size_t used = 0; const std::string t = cur.substr(b, e - b + 1); const double v = std::stod(t, &used); if (used != t.size()) return std::nullopt; parts.push_back(v); }
				catch (...) { return std::nullopt; }
				cur.clear();
			}
			else cur.push_back(c);
		}
		if (parts.size() == 3) parts.push_back(1.0);
		if (parts.size() != 4) return std::nullopt;
		bool clamped = false;
		for (auto& v : parts) { if (v < 0) { v = 0; clamped = true; } if (v > 1) { v = 1; clamped = true; } }
		if (clamped && warning) *warning = "color \"" + text + "\" has values outside 0-1 and was clamped";
		char buf[96];
		std::snprintf(buf, sizeof(buf), "%g, %g, %g, %g", parts[0], parts[1], parts[2], parts[3]);
		return std::string(buf);
	}

	inline std::wstring Colored(const std::wstring& text, const std::string& color)
	{
		if (text.empty() || color.empty()) return text;
		return L"<RichColor Color=\"" + Utf8ToWide(color) + L"\">" + text + L"</>";
	}

	// ------------------------------------------------------------------------------------------
	// Settings
	// ------------------------------------------------------------------------------------------

	enum class ChatMode { Global, Local, Tribe, Alliance };

	struct StyleMatch
	{
		std::set<std::string> groups;       // Permissions groups (any)
		std::set<std::string> tribe_ids;
		std::set<std::string> player_keys;  // EOS ID (ASA) or SteamID64 (ASE)
		std::set<std::string> role_ids;     // Discord role IDs (Discord -> game styles only)
		bool admin_only = false;
		bool Empty() const { return groups.empty() && tribe_ids.empty() && player_keys.empty() && role_ids.empty() && !admin_only; }
	};

	struct StyleRule
	{
		std::string name;
		int priority = 0;
		StyleMatch match;
		std::optional<std::string> tag, discord_tag, icon;
		std::optional<std::string> tag_color, name_color, tribe_color, message_color;
		std::optional<bool> hide_tribe;
		bool hideable = false; // players may hide this rule with /hidetag
	};

	struct MapStyle
	{
		std::string label;
		std::string color;
	};

	struct Formats
	{
		std::string line = "{mode}{map}{tag}{name}{tribe}: {message}";
		std::string mode = "{label} ";
		std::string map = "[{label}]: ";
		std::string tag = "[{tag}] ";
		std::string name = "{name}";
		std::string tribe = " [{tribe}]";
	};

	struct AntiSpam
	{
		int burst_messages = 5;
		int burst_seconds = 10;
		int repeat_count = 3;
		int repeat_seconds = 30;
	};

	struct WordFilter
	{
		bool enabled = true;
		int mute_minutes = 5;
		std::vector<std::string> patterns;
		std::shared_ptr<std::vector<std::wregex>> compiled = std::make_shared<std::vector<std::wregex>>();
	};

	struct Commands
	{
		std::string link = "/linkDiscord";
		std::string hide_tag = "/hidetag";
		std::string show_tag = "/showtag";
		std::string ignore = "/ignore";
		std::string unignore = "/unignore";
	};

	struct Messages
	{
		std::string sender = "ServerComms";
		std::string spam = "Slow down, you can chat again in {time}.";
		std::string blocked_word = "Your message wasn't sent: it contained a blocked word. You're muted for {time}.";
		std::string muted = "You're muted (reason: {reason}). {time} left.";
		std::string muted_permanent = "You're muted (reason: {reason}).";
		std::string link_syntax = "Usage: {command} <code>. Get a code from the Link button in Discord.";
		std::string link_checking = "Checking your code...";
		std::string link_success = "Your Discord account is now linked.";
		std::string link_invalid = "That code is invalid or has expired. Get a new one from Discord.";
		std::string link_already = "You're already linked.";
		std::string tag_hidden = "Your {tag} tag is now hidden.";
		std::string tag_shown = "Your {tag} tag is now shown.";
		std::string tag_none = "You don't have a tag that can be hidden.";
		std::string ignore_pick = "Pick a player with {command} #:";
		std::string ignore_none = "Nobody has talked recently.";
		std::string ignore_done = "You're now ignoring {name}.";
		std::string unignore_done = "You're no longer ignoring {name}.";
		std::string unignore_none = "You're not ignoring anyone.";
		std::string ignore_bad_choice = "'{choice}' isn't one of the listed numbers.";
	};

	struct Settings
	{
		bool relay_global = true;
		std::vector<std::string> command_prefixes{ "/" };
		bool hide_own_map_name = false;
		bool nonglobal_full = true;      // local/tribe/alliance: true = restyle whole line, false = message part only
		bool use_sender_field = false;   // plain map label in the chat sender field (no leading ": ")
		int max_message_length = 250;
		std::map<ChatMode, std::string> mode_labels{
			{ ChatMode::Local, "(LOCAL)" }, { ChatMode::Tribe, "(TRIBE)" }, { ChatMode::Alliance, "(ALLY)" } };
		Formats formats;
		std::map<std::string, MapStyle> maps;   // server key -> label/color
		std::string default_map_color;
		MapStyle discord_map{ "Discord", "" };
		bool icons_enabled = true;
		std::set<std::string> safe_icons;
		std::vector<StyleRule> styles;          // game players, sorted by priority (highest first)
		std::vector<StyleRule> discord_styles;  // Discord authors by role, sorted by priority
		AntiSpam anti_spam;
		WordFilter word_filter;
		Commands commands;
		Messages messages;
		int poll_seconds = 1;
		int heartbeat_seconds = 15;
		int outbox_capacity = 500;
		std::vector<std::string> warnings;
	};

	namespace detail
	{
		inline std::set<std::string> StringSet(const nlohmann::json& j, const char* key)
		{
			std::set<std::string> out;
			if (!j.contains(key)) return out;
			const auto& v = j.at(key);
			if (v.is_array())
				for (const auto& e : v)
				{
					if (e.is_string()) out.insert(e.get<std::string>());
					else if (e.is_number_integer()) out.insert(std::to_string(e.get<long long>()));
					else if (e.is_number_unsigned()) out.insert(std::to_string(e.get<unsigned long long>()));
				}
			return out;
		}

		template <typename T>
		inline void Read(const nlohmann::json& j, const char* key, T& out)
		{
			if (j.is_object() && j.contains(key) && !j.at(key).is_null())
				out = j.at(key).get<T>();
		}

		inline std::optional<std::string> OptString(const nlohmann::json& j, const char* key)
		{
			if (j.contains(key) && j.at(key).is_string()) return j.at(key).get<std::string>();
			return std::nullopt;
		}

		inline void ReadColor(const nlohmann::json& j, const char* key, std::optional<std::string>& out,
			const std::string& where, std::vector<std::string>& warnings)
		{
			const auto raw = OptString(j, key);
			if (!raw) return;
			std::string warning;
			const auto c = NormalizeColor(*raw, &warning);
			if (!c) { warnings.push_back(where + ": " + key + " \"" + *raw + "\" is not a color (R, G, B, A); ignored"); return; }
			if (!warning.empty()) warnings.push_back(where + ": " + key + " " + warning);
			out = *c;
		}

		inline StyleRule ParseRule(const nlohmann::json& j, size_t index, const char* section,
			const std::set<std::string>& safe_icons, bool icons_enabled, std::vector<std::string>& warnings)
		{
			StyleRule r;
			Read(j, "Name", r.name);
			if (r.name.empty()) r.name = std::string(section) + " #" + std::to_string(index + 1);
			Read(j, "Priority", r.priority);
			const nlohmann::json match = j.contains("Match") ? j.at("Match") : nlohmann::json::object();
			r.match.groups = StringSet(match, "Groups");
			r.match.tribe_ids = StringSet(match, "TribeIds");
			r.match.player_keys = StringSet(match, "PlayerKeys");
			r.match.role_ids = StringSet(match, "RoleIds");
			Read(match, "AdminOnly", r.match.admin_only);
			r.tag = OptString(j, "Tag");
			r.discord_tag = OptString(j, "DiscordTag");
			r.icon = OptString(j, "Icon");
			if (r.icon && r.icon->empty()) r.icon.reset();
			if (r.icon && !safe_icons.count(*r.icon))
			{
				if (icons_enabled)
					warnings.push_back(r.name + ": icon not in SafeIcons, skipped: " + *r.icon);
				r.icon.reset();
			}
			const std::string where = std::string(section) + " \"" + r.name + "\"";
			ReadColor(j, "TagColor", r.tag_color, where, warnings);
			ReadColor(j, "NameColor", r.name_color, where, warnings);
			ReadColor(j, "TribeColor", r.tribe_color, where, warnings);
			ReadColor(j, "MessageColor", r.message_color, where, warnings);
			if (j.contains("HideTribe") && j.at("HideTribe").is_boolean()) r.hide_tribe = j.at("HideTribe").get<bool>();
			Read(j, "Hideable", r.hideable);
			return r;
		}

		inline void SortRules(std::vector<StyleRule>& rules)
		{
			std::stable_sort(rules.begin(), rules.end(), [](const StyleRule& a, const StyleRule& b) { return a.priority > b.priority; });
		}
	}

	// Parses the plugin config. Never throws for bad values: problems go to Settings::warnings and the
	// default for that value is kept. (A JSON syntax error is thrown by the caller's json::parse.)
	inline Settings ParseSettings(const nlohmann::json& root)
	{
		using namespace detail;
		Settings s;
		auto& w = s.warnings;
		try
		{
			if (root.contains("Chat"))
			{
				const auto& c = root.at("Chat");
				Read(c, "RelayGlobal", s.relay_global);
				if (c.contains("CommandPrefixes"))
				{
					s.command_prefixes.clear();
					for (const auto& p : StringSet(c, "CommandPrefixes")) if (!p.empty()) s.command_prefixes.push_back(p);
				}
				Read(c, "HideOwnMapName", s.hide_own_map_name);
				Read(c, "UseSenderField", s.use_sender_field);
				if (c.contains("NonGlobalStyle"))
				{
					const std::string v = Lower(c.at("NonGlobalStyle").get<std::string>());
					if (v == "full") s.nonglobal_full = true;
					else if (v == "message") s.nonglobal_full = false;
					else w.push_back("Chat.NonGlobalStyle must be \"full\" or \"message\"; using \"full\"");
				}
				Read(c, "MaxMessageLength", s.max_message_length);
				if (c.contains("ModeLabels"))
				{
					const auto& m = c.at("ModeLabels");
					if (m.contains("Local")) s.mode_labels[ChatMode::Local] = m.at("Local").get<std::string>();
					if (m.contains("Tribe")) s.mode_labels[ChatMode::Tribe] = m.at("Tribe").get<std::string>();
					if (m.contains("Alliance")) s.mode_labels[ChatMode::Alliance] = m.at("Alliance").get<std::string>();
				}
				if (c.contains("Formats"))
				{
					const auto& f = c.at("Formats");
					Read(f, "Line", s.formats.line);
					Read(f, "Mode", s.formats.mode);
					Read(f, "Map", s.formats.map);
					Read(f, "Tag", s.formats.tag);
					Read(f, "Name", s.formats.name);
					Read(f, "Tribe", s.formats.tribe);
				}
				if (s.formats.line.find("{message}") == std::string::npos)
				{
					w.push_back("Chat.Formats.Line has no {message}; using the default line format");
					s.formats.line = Formats{}.line;
				}
			}

			if (root.contains("Maps") && root.at("Maps").is_object())
				for (const auto& [key, v] : root.at("Maps").items())
				{
					MapStyle m;
					m.label = key;
					Read(v, "Label", m.label);
					std::optional<std::string> color;
					ReadColor(v, "Color", color, "Maps \"" + key + "\"", w);
					if (color) m.color = *color;
					s.maps[key] = m;
				}
			{
				std::optional<std::string> color;
				ReadColor(root, "DefaultMapColor", color, "DefaultMapColor", w);
				if (color) s.default_map_color = *color;
			}
			if (root.contains("DiscordMap"))
			{
				const auto& d = root.at("DiscordMap");
				Read(d, "Label", s.discord_map.label);
				std::optional<std::string> color;
				ReadColor(d, "Color", color, "DiscordMap", w);
				if (color) s.discord_map.color = *color;
			}

			Read(root, "Icons", s.icons_enabled);
			s.safe_icons = StringSet(root, "SafeIcons");

			if (root.contains("StyleRules") && root.at("StyleRules").is_array())
			{
				size_t i = 0;
				for (const auto& r : root.at("StyleRules"))
				{
					auto rule = ParseRule(r, i++, "StyleRules", s.safe_icons, s.icons_enabled, w);
					if (!rule.match.role_ids.empty())
						w.push_back("StyleRules \"" + rule.name + "\": RoleIds only apply in DiscordStyles; ignored here");
					s.styles.push_back(std::move(rule));
				}
				SortRules(s.styles);
			}
			if (root.contains("DiscordStyles") && root.at("DiscordStyles").is_array())
			{
				size_t i = 0;
				for (const auto& r : root.at("DiscordStyles"))
					s.discord_styles.push_back(ParseRule(r, i++, "DiscordStyles", s.safe_icons, s.icons_enabled, w));
				SortRules(s.discord_styles);
			}

			if (root.contains("AntiSpam"))
			{
				const auto& a = root.at("AntiSpam");
				Read(a, "BurstMessages", s.anti_spam.burst_messages);
				Read(a, "BurstSeconds", s.anti_spam.burst_seconds);
				Read(a, "RepeatCount", s.anti_spam.repeat_count);
				Read(a, "RepeatSeconds", s.anti_spam.repeat_seconds);
				if (s.anti_spam.burst_messages < 1 || s.anti_spam.burst_seconds < 1 || s.anti_spam.repeat_count < 2 || s.anti_spam.repeat_seconds < 1)
				{
					w.push_back("AntiSpam values out of range; using defaults");
					s.anti_spam = AntiSpam{};
				}
			}

			if (root.contains("WordFilter"))
			{
				const auto& f = root.at("WordFilter");
				Read(f, "Enabled", s.word_filter.enabled);
				Read(f, "MuteMinutes", s.word_filter.mute_minutes);
				if (f.contains("Patterns") && f.at("Patterns").is_array())
					for (const auto& p : f.at("Patterns"))
						if (p.is_string()) s.word_filter.patterns.push_back(p.get<std::string>());
			}
			for (const auto& p : s.word_filter.patterns)
			{
				try
				{
					s.word_filter.compiled->emplace_back(Utf8ToWide(p), std::regex::ECMAScript | std::regex::icase | std::regex::optimize);
				}
				catch (const std::regex_error&)
				{
					w.push_back("WordFilter pattern is not a valid regex, skipped: " + p);
				}
			}

			if (root.contains("Commands"))
			{
				const auto& c = root.at("Commands");
				Read(c, "Link", s.commands.link);
				Read(c, "HideTag", s.commands.hide_tag);
				Read(c, "ShowTag", s.commands.show_tag);
				Read(c, "Ignore", s.commands.ignore);
				Read(c, "Unignore", s.commands.unignore);
			}

			if (root.contains("Messages"))
			{
				const auto& m = root.at("Messages");
				Read(m, "Sender", s.messages.sender);
				Read(m, "Spam", s.messages.spam);
				Read(m, "BlockedWord", s.messages.blocked_word);
				Read(m, "Muted", s.messages.muted);
				Read(m, "MutedPermanent", s.messages.muted_permanent);
				Read(m, "LinkSyntax", s.messages.link_syntax);
				Read(m, "LinkChecking", s.messages.link_checking);
				Read(m, "LinkSuccess", s.messages.link_success);
				Read(m, "LinkInvalid", s.messages.link_invalid);
				Read(m, "LinkAlready", s.messages.link_already);
				Read(m, "TagHidden", s.messages.tag_hidden);
				Read(m, "TagShown", s.messages.tag_shown);
				Read(m, "TagNone", s.messages.tag_none);
				Read(m, "IgnorePick", s.messages.ignore_pick);
				Read(m, "IgnoreNone", s.messages.ignore_none);
				Read(m, "IgnoreDone", s.messages.ignore_done);
				Read(m, "UnignoreDone", s.messages.unignore_done);
				Read(m, "UnignoreNone", s.messages.unignore_none);
				Read(m, "IgnoreBadChoice", s.messages.ignore_bad_choice);
			}

			if (root.contains("Intervals"))
			{
				const auto& i = root.at("Intervals");
				Read(i, "PollSeconds", s.poll_seconds);
				Read(i, "HeartbeatSeconds", s.heartbeat_seconds);
				Read(i, "OutboxCapacity", s.outbox_capacity);
				s.poll_seconds = (std::max)(1, s.poll_seconds);
				s.heartbeat_seconds = (std::max)(5, s.heartbeat_seconds);
				s.outbox_capacity = (std::max)(10, s.outbox_capacity);
			}
			s.max_message_length = (std::max)(1, s.max_message_length);
		}
		catch (const std::exception& e)
		{
			w.push_back(std::string("config value has the wrong type: ") + e.what());
		}
		return s;
	}

	// ------------------------------------------------------------------------------------------
	// Styling
	// ------------------------------------------------------------------------------------------

	struct Sender
	{
		std::string player_key;
		std::string tribe_id;
		std::vector<std::string> groups;     // Permissions groups (game players)
		std::vector<std::string> role_ids;   // Discord roles (Discord authors)
		bool is_admin = false;
		std::set<std::string> hidden_tags;   // tags this player hid with /hidetag
	};

	struct ResolvedStyle
	{
		std::string tag, discord_tag, icon;
		std::string tag_color, name_color, tribe_color, message_color;
		bool hide_tribe = false;
	};

	inline bool Matches(const StyleRule& r, const Sender& s)
	{
		const auto& m = r.match;
		if (m.admin_only && !s.is_admin) return false;
		if (m.Empty() || (m.admin_only && m.groups.empty() && m.tribe_ids.empty() && m.player_keys.empty() && m.role_ids.empty()))
			return true;
		for (const auto& g : s.groups) if (m.groups.count(g)) return true;
		for (const auto& id : s.role_ids) if (m.role_ids.count(id)) return true;
		if (!s.tribe_id.empty() && m.tribe_ids.count(s.tribe_id)) return true;
		if (!s.player_key.empty() && m.player_keys.count(s.player_key)) return true;
		return false;
	}

	// Each field comes from the highest-priority matching rule that sets it. A hideable rule whose tag
	// the player hid is skipped entirely, so hiding a Patron tag also drops the Patron colors and icon.
	inline ResolvedStyle ResolveStyle(const std::vector<StyleRule>& rules, const Sender& s)
	{
		ResolvedStyle out;
		bool tag = false, dtag = false, icon = false, tc = false, nc = false, trc = false, mc = false, ht = false;
		for (const auto& r : rules)
		{
			if (!Matches(r, s)) continue;
			if (r.hideable && r.tag && s.hidden_tags.count(*r.tag)) continue;
			if (!tag && r.tag) { out.tag = *r.tag; tag = true; }
			if (!dtag && r.discord_tag) { out.discord_tag = *r.discord_tag; dtag = true; }
			if (!icon && r.icon) { out.icon = *r.icon; icon = true; }
			if (!tc && r.tag_color) { out.tag_color = *r.tag_color; tc = true; }
			if (!nc && r.name_color) { out.name_color = *r.name_color; nc = true; }
			if (!trc && r.tribe_color) { out.tribe_color = *r.tribe_color; trc = true; }
			if (!mc && r.message_color) { out.message_color = *r.message_color; mc = true; }
			if (!ht && r.hide_tribe) { out.hide_tribe = *r.hide_tribe; ht = true; }
		}
		return out;
	}

	// The hideable tag a player currently shows (for /hidetag), or empty.
	inline std::string HideableTag(const std::vector<StyleRule>& rules, const Sender& s)
	{
		for (const auto& r : rules)
		{
			if (!Matches(r, s) || !r.tag) continue;
			if (r.hideable && s.hidden_tags.count(*r.tag)) continue;
			return r.hideable ? *r.tag : std::string{};
		}
		return {};
	}

	// The hidden tags a player could show again (for /showtag).
	inline std::vector<std::string> ShowableTags(const std::vector<StyleRule>& rules, const Sender& s)
	{
		std::vector<std::string> out;
		for (const auto& r : rules)
			if (Matches(r, s) && r.hideable && r.tag && s.hidden_tags.count(*r.tag) &&
				std::find(out.begin(), out.end(), *r.tag) == out.end())
				out.push_back(*r.tag);
		return out;
	}

	// ------------------------------------------------------------------------------------------
	// Formatting
	// ------------------------------------------------------------------------------------------

	struct LineInput
	{
		ChatMode mode = ChatMode::Global;
		std::string source_map;     // server key the message came from; empty or "discord" for Discord
		std::string viewer_map;     // server key of the server rendering it (for HideOwnMapName)
		bool from_discord = false;
		std::wstring name, tribe, message;
		ResolvedStyle style;
	};

	struct FormattedLine
	{
		std::wstring text;    // the whole line, for the message field
		std::wstring sender;  // with UseSenderField: the plain map label for the sender field (else empty)
		std::wstring body;    // with UseSenderField: the rest of the line, for the message field
		std::string icon; // asset path, empty for none; only ever a SafeIcons entry
	};

	inline std::wstring FillPart(const std::string& format, const char* token, const std::wstring& value)
	{
		std::wstring f = Utf8ToWide(format);
		ReplaceAll(f, Utf8ToWide(std::string("{") + token + "}"), value);
		return f;
	}

	inline FormattedLine FormatLine(const Settings& s, const LineInput& in)
	{
		FormattedLine out;
		const auto& st = in.style;

		std::wstring mode;
		if (in.mode != ChatMode::Global)
		{
			const auto it = s.mode_labels.find(in.mode);
			if (it != s.mode_labels.end() && !it->second.empty())
				mode = FillPart(s.formats.mode, "label", Utf8ToWide(it->second));
		}

		std::wstring map;
		const bool own_map = !in.from_discord && in.source_map == in.viewer_map;
		const bool show_map = in.mode == ChatMode::Global && !(s.hide_own_map_name && own_map);
		if (show_map)
		{
			std::string label, color;
			if (in.from_discord) { label = s.discord_map.label; color = s.discord_map.color; }
			else
			{
				const auto it = s.maps.find(in.source_map);
				label = it != s.maps.end() ? it->second.label : in.source_map;
				color = it != s.maps.end() && !it->second.color.empty() ? it->second.color : s.default_map_color;
			}
			if (!label.empty())
				map = Colored(FillPart(s.formats.map, "label", Utf8ToWide(label)), color);
		}

		std::wstring tag;
		if (!st.tag.empty())
			tag = Colored(FillPart(s.formats.tag, "tag", Utf8ToWide(st.tag)), st.tag_color);

		const std::wstring name = Colored(FillPart(s.formats.name, "name", EscapeRich(in.name)), st.name_color);

		std::wstring tribe;
		if (!in.tribe.empty() && !st.hide_tribe)
			tribe = Colored(FillPart(s.formats.tribe, "tribe", EscapeRich(in.tribe)), st.tribe_color);

		const std::wstring message = Colored(EscapeRich(in.message), st.message_color);

		std::wstring line = Utf8ToWide(s.formats.line);
		ReplaceAll(line, L"{mode}", mode);
		ReplaceAll(line, L"{map}", map);
		ReplaceAll(line, L"{tag}", tag);
		ReplaceAll(line, L"{name}", name);
		ReplaceAll(line, L"{tribe}", tribe);
		ReplaceAll(line, L"{message}", message);
		out.text = line;
		// The game prints "SenderName: Message" and does not parse rich text in SenderName. With
		// UseSenderField the plain map label (e.g. "[Ragnarok]", drawn in the game's sender color) goes
		// in the sender field and the rest of the line in the message, so there is no leading ": ".
		// Lines without a map label (own map hidden, local/tribe/alliance) keep the whole line.
		if (s.use_sender_field && !map.empty())
		{
			std::string label;
			if (in.from_discord) label = s.discord_map.label;
			else
			{
				const auto it = s.maps.find(in.source_map);
				label = it != s.maps.end() ? it->second.label : in.source_map;
			}
			std::wstring sender = FillPart(s.formats.map, "label", Utf8ToWide(label));
			for (const wchar_t* sep : { L": ", L":", L" " })
			{
				const std::wstring w(sep);
				while (sender.size() >= w.size() && sender.compare(sender.size() - w.size(), w.size(), w) == 0) sender.resize(sender.size() - w.size());
			}
			std::wstring body = Utf8ToWide(s.formats.line);
			ReplaceAll(body, L"{mode}", mode);
			ReplaceAll(body, L"{map}", L"");
			ReplaceAll(body, L"{tag}", tag);
			ReplaceAll(body, L"{name}", name);
			ReplaceAll(body, L"{tribe}", tribe);
			ReplaceAll(body, L"{message}", message);
			out.sender = sender;
			out.body = body;
		}
		if (s.icons_enabled && !st.icon.empty() && s.safe_icons.count(st.icon))
			out.icon = st.icon;
		return out;
	}

	// Local/tribe/alliance with NonGlobalStyle "message": the game keeps its own header (name, tribe, mode)
	// and only the message part is styled: [Tag] in its color, then the message in the message color.
	inline std::wstring StyleMessageOnly(const Settings& s, const ResolvedStyle& st, const std::wstring& message)
	{
		std::wstring out;
		if (!st.tag.empty())
			out = Colored(FillPart(s.formats.tag, "tag", Utf8ToWide(st.tag)), st.tag_color);
		return out + Colored(EscapeRich(message), st.message_color);
	}

	// ------------------------------------------------------------------------------------------
	// Commands and message checks
	// ------------------------------------------------------------------------------------------

	inline bool IsCommand(const Settings& s, const std::wstring& text)
	{
		const std::wstring t = TrimW(text);
		for (const auto& p : s.command_prefixes)
		{
			const std::wstring wp = Utf8ToWide(p);
			if (!wp.empty() && t.compare(0, wp.size(), wp) == 0) return true;
		}
		return false;
	}

	struct ParsedCommand
	{
		std::wstring name;              // lower-cased, includes the prefix ("/linkdiscord")
		std::vector<std::wstring> args;
	};

	inline ParsedCommand ParseCommand(const std::wstring& text)
	{
		ParsedCommand c;
		std::wstring cur;
		bool first = true;
		for (wchar_t ch : TrimW(text) + L" ")
		{
			if (ch == L' ' || ch == L'\t')
			{
				if (cur.empty()) continue;
				if (first) { c.name = LowerW(cur); first = false; }
				else c.args.push_back(cur);
				cur.clear();
			}
			else cur.push_back(ch);
		}
		return c;
	}

	inline bool IsCommandName(const ParsedCommand& c, const std::string& configured)
	{
		return !configured.empty() && c.name == LowerW(Utf8ToWide(configured));
	}

	// Link codes are 4-12 letters/digits; returned upper-cased, or empty when invalid.
	inline std::string NormalizeLinkCode(const std::wstring& code)
	{
		const std::wstring t = TrimW(code);
		if (t.size() < 4 || t.size() > 12) return {};
		std::string out;
		for (wchar_t ch : t)
		{
			if (ch > 127 || !std::isalnum(static_cast<unsigned char>(ch))) return {};
			out.push_back(static_cast<char>(std::toupper(static_cast<unsigned char>(ch))));
		}
		return out;
	}

	// Cuts a message to the configured length (in characters).
	inline std::wstring ClampLength(const Settings& s, const std::wstring& text)
	{
		const size_t max = static_cast<size_t>(s.max_message_length);
		return text.size() <= max ? text : text.substr(0, max);
	}

	inline bool ContainsBlockedWord(const Settings& s, const std::wstring& text)
	{
		if (!s.word_filter.enabled) return false;
		for (const auto& re : *s.word_filter.compiled)
			if (std::regex_search(text, re)) return true;
		return false;
	}

	// ------------------------------------------------------------------------------------------
	// Anti-spam: burst limit + repeat check, global chat only. Times are seconds (any monotonic clock).
	// ------------------------------------------------------------------------------------------

	inline std::wstring NormalizeForRepeat(const std::wstring& text)
	{
		std::wstring out;
		for (wchar_t ch : text)
			if (std::iswalnum(static_cast<wint_t>(ch))) out.push_back(static_cast<wchar_t>(std::towlower(static_cast<wint_t>(ch))));
		return out;
	}

	struct SpamResult
	{
		bool allowed = true;
		long long retry_after = 0; // seconds until the player may send again
	};

	class SpamTracker
	{
	public:
		SpamResult Check(const AntiSpam& cfg, const std::string& player_key, const std::wstring& text, long long now)
		{
			auto& h = history_[player_key];
			while (!h.sent.empty() && now - h.sent.front() >= cfg.burst_seconds) h.sent.pop_front();
			while (!h.recent.empty() && now - h.recent.front().first >= cfg.repeat_seconds) h.recent.pop_front();

			SpamResult r;
			if (static_cast<int>(h.sent.size()) >= cfg.burst_messages)
			{
				r.allowed = false;
				r.retry_after = (std::max)(1LL, cfg.burst_seconds - (now - h.sent.front()));
				return r;
			}
			const std::wstring norm = NormalizeForRepeat(text);
			if (!norm.empty())
			{
				int same = 0;
				long long oldest = now;
				for (const auto& [t, m] : h.recent)
					if (m == norm) { ++same; oldest = (std::min)(oldest, t); }
				if (same >= cfg.repeat_count - 1)
				{
					r.allowed = false;
					r.retry_after = (std::max)(1LL, cfg.repeat_seconds - (now - oldest));
					return r;
				}
			}
			h.sent.push_back(now);
			if (!norm.empty()) h.recent.emplace_back(now, norm);
			return r;
		}

		void Forget(const std::string& player_key) { history_.erase(player_key); }

	private:
		struct History
		{
			std::deque<long long> sent;
			std::deque<std::pair<long long, std::wstring>> recent;
		};
		std::unordered_map<std::string, History> history_;
	};

	// ------------------------------------------------------------------------------------------
	// Mutes
	// ------------------------------------------------------------------------------------------

	struct Mute
	{
		std::string reason;
		long long until = 0;      // unix seconds; 0 = permanent
		std::string muted_by;
	};

	inline bool IsActive(const Mute& m, long long now) { return m.until == 0 || m.until > now; }

	inline std::string MuteNotice(const Settings& s, const Mute& m, long long now)
	{
		if (m.until == 0) return Fill(s.messages.muted_permanent, { { "reason", m.reason } });
		return Fill(s.messages.muted, { { "reason", m.reason }, { "time", FormatDuration(m.until - now) } });
	}

	inline std::string BlockedWordNotice(const Settings& s)
	{
		return Fill(s.messages.blocked_word, { { "time", FormatDuration(static_cast<long long>(s.word_filter.mute_minutes) * 60) } });
	}

	inline std::string SpamNotice(const Settings& s, const SpamResult& r)
	{
		return Fill(s.messages.spam, { { "time", FormatDuration(r.retry_after) } });
	}

	// Parses "<minutes>|perm" for ServerComms.Mute. Returns seconds (0 = permanent) or nullopt.
	inline std::optional<long long> ParseMuteLength(const std::string& text)
	{
		const std::string t = Lower(text);
		if (t == "perm" || t == "permanent") return 0LL;
		if (t.empty() || t.size() > 7) return std::nullopt;
		for (char c : t) if (!std::isdigit(static_cast<unsigned char>(c))) return std::nullopt;
		const long long minutes = std::stoll(t);
		if (minutes <= 0) return std::nullopt;
		return minutes * 60;
	}

	// ------------------------------------------------------------------------------------------
	// Recent speakers (for the /ignore picker)
	// ------------------------------------------------------------------------------------------

	struct Speaker
	{
		std::string player_key;
		std::wstring name;
	};

	class RecentSpeakers
	{
	public:
		explicit RecentSpeakers(size_t capacity = 5) : capacity_(capacity) {}

		void Add(const Speaker& s)
		{
			list_.erase(std::remove_if(list_.begin(), list_.end(), [&](const Speaker& x) { return x.player_key == s.player_key; }), list_.end());
			list_.insert(list_.begin(), s);
			if (list_.size() > capacity_) list_.resize(capacity_);
		}

		// Most recent first, excluding the viewer.
		std::vector<Speaker> For(const std::string& viewer_key) const
		{
			std::vector<Speaker> out;
			for (const auto& s : list_) if (s.player_key != viewer_key) out.push_back(s);
			return out;
		}

	private:
		size_t capacity_;
		std::vector<Speaker> list_;
	};

	// "/ignore 2" -> index 1 into a list of `count`; nullopt when not a listed number.
	inline std::optional<size_t> ParseChoice(const std::wstring& arg, size_t count)
	{
		if (arg.empty() || arg.size() > 3) return std::nullopt;
		size_t n = 0;
		for (wchar_t ch : arg)
		{
			if (ch < L'0' || ch > L'9') return std::nullopt;
			n = n * 10 + static_cast<size_t>(ch - L'0');
		}
		if (n < 1 || n > count) return std::nullopt;
		return n - 1;
	}

	// ------------------------------------------------------------------------------------------
	// Server command line (MultiHome / RCON port, published for the bot's RCON fallback)
	// ------------------------------------------------------------------------------------------

	struct ServerAddress
	{
		std::string multihome;
		int rcon_port = 0;
		std::string server_key; // -ServerKey=, shared with other plugins on the same launch line
	};

	// Reads "?MultiHome=x" / "?RCONPort=n" / "?ServerKey=x" from the map URL and the same names as
	// "-MultiHome=x" / "-RCONPort=n" / "-ServerKey=x" flags.
	// Key matching is case-insensitive; later values win.
	inline ServerAddress ParseCommandLine(const std::string& cmd)
	{
		ServerAddress a;
		auto take = [&](const std::string& key, const std::string& value)
		{
			const std::string k = Lower(key);
			if (k == "multihome") a.multihome = value;
			else if (k == "serverkey") a.server_key = value;
			else if (k == "rconport")
			{
				try { const int p = std::stoi(value); if (p > 0 && p < 65536) a.rcon_port = p; }
				catch (...) {}
			}
		};
		size_t i = 0;
		while (i < cmd.size())
		{
			const char c = cmd[i];
			if (c == '?' || ((c == '-') && (i == 0 || cmd[i - 1] == ' ' || cmd[i - 1] == '"')))
			{
				size_t j = i + 1;
				while (j < cmd.size() && cmd[j] != '=' && cmd[j] != '?' && cmd[j] != ' ' && cmd[j] != '"') ++j;
				if (j < cmd.size() && cmd[j] == '=')
				{
					size_t k = j + 1;
					while (k < cmd.size() && cmd[k] != '?' && cmd[k] != ' ' && cmd[k] != '"') ++k;
					take(cmd.substr(i + 1, j - i - 1), cmd.substr(j + 1, k - j - 1));
					i = k;
					continue;
				}
				i = j;
				continue;
			}
			++i;
		}
		return a;
	}

	// Reads Key=Value from [Section] of an ini file's text (case-insensitive section and key); empty when
	// missing. Used for RCONPort in GameUserSettings.ini when the launch line doesn't set it.
	inline std::string IniValue(const std::string& text, const std::string& section, const std::string& key)
	{
		const std::string want_section = Lower(section), want_key = Lower(key);
		bool in_section = false;
		size_t pos = 0;
		while (pos <= text.size())
		{
			size_t end = text.find('\n', pos);
			if (end == std::string::npos) end = text.size();
			std::string line = text.substr(pos, end - pos);
			pos = end + 1;
			while (!line.empty() && (line.back() == '\r' || line.back() == ' ' || line.back() == '\t')) line.pop_back();
			const size_t first = line.find_first_not_of(" \t");
			if (first == std::string::npos) continue;
			line = line.substr(first);
			if (line[0] == ';' || line[0] == '#') continue;
			if (line[0] == '[')
			{
				const size_t close = line.find(']');
				in_section = close != std::string::npos && Lower(line.substr(1, close - 1)) == want_section;
				continue;
			}
			if (!in_section) continue;
			const size_t eq = line.find('=');
			if (eq == std::string::npos) continue;
			std::string k = line.substr(0, eq);
			while (!k.empty() && (k.back() == ' ' || k.back() == '\t')) k.pop_back();
			if (Lower(k) != want_key) continue;
			std::string v = line.substr(eq + 1);
			const size_t vb = v.find_first_not_of(" \t");
			return vb == std::string::npos ? std::string() : v.substr(vb);
		}
		return {};
	}
}
