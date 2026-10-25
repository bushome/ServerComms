// Stand-alone tests for src/ChatLogic.h. No game, server API DLL or Windows needed.
//
// Build and run from the project folder (any C++20 compiler):
//   cl /std:c++20 /EHsc /utf-8 /I extern\AseApi\version\Core\Public /I src tests\logic_tests.cpp && logic_tests.exe configs\config.json
//   clang++ -std=c++20 -I extern/AseApi/version/Core/Public -I src tests/logic_tests.cpp -o logic_tests && ./logic_tests configs/config.json
// This ASA copy: use /I extern\AsaApi\AsaApi\Core\Public (or -I extern/AsaApi/AsaApi/Core/Public) instead.
//
// The optional argument is a config.json to validate (parses with no warnings). Add --shipped after it
// to also check the file against the expectations for the shipped config at the bottom of this file;
// configs\config.commented.json holds the same values, so it must pass the same check.

#include <fstream>
#include <iostream>
#include <string>

#include "ChatLogic.h"

using namespace ServerComms;

static int g_failures = 0;
static int g_checks = 0;

#define CHECK(condition) \
	do { \
		++g_checks; \
		if (!(condition)) { \
			++g_failures; \
			std::cerr << "FAILED line " << __LINE__ << ": " #condition "\n"; \
		} \
	} while (false)

static Settings Parse(const char* text)
{
	return ParseSettings(nlohmann::json::parse(text, nullptr, true, true));
}

static bool HasWarning(const Settings& s, const std::string& part)
{
	for (const auto& w : s.warnings) if (w.find(part) != std::string::npos) return true;
	return false;
}

static void TestUtf8()
{
	const std::string s = "Bulk \xF0\x9F\x92\xAA Frostycepts \xC3\xA9";
	CHECK(WideToUtf8(Utf8ToWide(s)) == s);
	CHECK(Utf8ToWide("abc") == L"abc");
	CHECK(WideToUtf8(Utf8ToWide("\xC3")) == "\xEF\xBF\xBD"); // truncated sequence -> U+FFFD
}

static void TestEscapeAndDuration()
{
	const std::wstring e = EscapeRich(L"<RichColor Color=\"1,0,0,1\">admin</>");
	CHECK(e.find(L'<') == std::wstring::npos);
	CHECK(FormatDuration(45) == "45s");
	CHECK(FormatDuration(192) == "3m 12s");
	CHECK(FormatDuration(300) == "5m");
	CHECK(FormatDuration(3900) == "1h 5m");
	CHECK(FormatDuration(3600) == "1h");
	CHECK(FormatDuration(97200) == "1d 3h");
	CHECK(FormatDuration(-5) == "0s");
}

static void TestColors()
{
	CHECK(NormalizeColor("0.8, 0.8, 0.8, 1") == std::optional<std::string>("0.8, 0.8, 0.8, 1"));
	CHECK(NormalizeColor("1,0,0") == std::optional<std::string>("1, 0, 0, 1"));
	std::string w;
	CHECK(NormalizeColor("0.6156, 0, 255, 1", &w) == std::optional<std::string>("0.6156, 0, 1, 1")); // 0-255 channel where 0-1 is expected
	CHECK(!w.empty());
	CHECK(!NormalizeColor("red"));
	CHECK(!NormalizeColor("1, 2"));
	CHECK(!NormalizeColor("1, x, 0, 1"));
}

static const char* kStyles = R"({
	"Icons": true,
	"SafeIcons": [ "/Game/PrimalEarth/Icons/Patron.Patron", "/Game/PrimalEarth/Icons/Crash.Crash" ],
	"StyleRules": [
		{ "Name": "Default", "Priority": 0, "NameColor": "0.8, 0.8, 0.8, 1", "TribeColor": "0.8, 0.8, 0.8, 1" },
		{ "Name": "Patrons", "Priority": 1, "Match": { "Groups": ["Patrons"] }, "Tag": "Patron", "DiscordTag": "<Patron>",
		  "Icon": "/Game/PrimalEarth/Icons/Patron.Patron", "TagColor": "0.745, 1, 0, 1", "NameColor": "1, 0.47, 0, 1", "Hideable": true },
		{ "Name": "Crashmaster", "Priority": 9999, "Match": { "Groups": ["BulkBiceps"] }, "Tag": "Crashmaster",
		  "Icon": "/Game/PrimalEarth/Icons/Crash.Crash", "NameColor": "0.33, 0.69, 0.77, 1" },
		{ "Name": "Bunny", "Priority": 50, "Match": { "PlayerKeys": ["76561198241090422"] }, "Icon": "/Game/Unsafe/Bunny.Bunny" },
		{ "Name": "TribeColor", "Priority": 5, "Match": { "TribeIds": ["12345"] }, "TribeColor": "1, 0, 0, 1", "HideTribe": true },
		{ "Name": "Admins", "Priority": 100, "Match": { "AdminOnly": true }, "MessageColor": "0.27, 1, 0.48, 1" }
	]
})";

static void TestStyles()
{
	const Settings s = Parse(kStyles);
	CHECK(s.styles.size() == 6);
	CHECK(s.styles.front().name == "Crashmaster"); // sorted by priority, highest first
	CHECK(HasWarning(s, "icon not in SafeIcons")); // Bunny's unsafe icon dropped

	Sender plain;
	plain.groups = { "Default" };
	auto st = ResolveStyle(s.styles, plain);
	CHECK(st.tag.empty() && st.icon.empty());
	CHECK(st.name_color == "0.8, 0.8, 0.8, 1");

	Sender patron;
	patron.groups = { "Default", "Patrons" };
	st = ResolveStyle(s.styles, patron);
	CHECK(st.tag == "Patron" && st.discord_tag == "<Patron>");
	CHECK(st.icon == "/Game/PrimalEarth/Icons/Patron.Patron");
	CHECK(st.name_color == "1, 0.47, 0, 1");
	CHECK(st.tribe_color == "0.8, 0.8, 0.8, 1"); // falls through to Default
	CHECK(HideableTag(s.styles, patron) == "Patron");

	patron.hidden_tags = { "Patron" };
	st = ResolveStyle(s.styles, patron);
	CHECK(st.tag.empty() && st.icon.empty());
	CHECK(st.name_color == "0.8, 0.8, 0.8, 1"); // hiding drops the Patron colors too
	CHECK(HideableTag(s.styles, patron).empty());
	CHECK(ShowableTags(s.styles, patron) == std::vector<std::string>{ "Patron" });

	Sender boss;
	boss.groups = { "Default", "Patrons", "BulkBiceps" };
	st = ResolveStyle(s.styles, boss);
	CHECK(st.tag == "Crashmaster");
	CHECK(st.discord_tag == "<Patron>"); // Crashmaster sets no DiscordTag; next match supplies it
	CHECK(HideableTag(s.styles, boss).empty()); // top tag is not hideable

	Sender bunny;
	bunny.player_key = "76561198241090422";
	st = ResolveStyle(s.styles, bunny);
	CHECK(st.icon.empty()); // unsafe icon never resolves

	Sender tribe;
	tribe.tribe_id = "12345";
	st = ResolveStyle(s.styles, tribe);
	CHECK(st.tribe_color == "1, 0, 0, 1" && st.hide_tribe);

	Sender admin;
	admin.is_admin = true;
	st = ResolveStyle(s.styles, admin);
	CHECK(st.message_color == "0.27, 1, 0.48, 1");
	st = ResolveStyle(s.styles, plain);
	CHECK(st.message_color.empty()); // AdminOnly does not match non-admins
}

static void TestDiscordStyles()
{
	const Settings s = Parse(R"({
		"DiscordStyles": [
			{ "Name": "Owners", "Priority": 10, "Match": { "RoleIds": ["399785395983745039"] }, "Tag": "FireFlyOwners", "TagColor": "0.6157, 0, 1, 1" },
			{ "Name": "Patrons", "Priority": 5, "Match": { "RoleIds": [872266859372744745] }, "Tag": "Patrons" }
		]
	})");
	Sender d;
	d.role_ids = { "872266859372744745" };
	CHECK(ResolveStyle(s.discord_styles, d).tag == "Patrons"); // numeric role IDs in JSON read as strings
	d.role_ids.push_back("399785395983745039");
	CHECK(ResolveStyle(s.discord_styles, d).tag == "FireFlyOwners");
	Sender none;
	CHECK(ResolveStyle(s.discord_styles, none).tag.empty());
}

static void TestFormatAsa()
{
	Settings s = Parse(R"({
		"Chat": { "HideOwnMapName": true },
		"Maps": { "Ragnarok": { "Label": "Ragnarok" }, "GenesisSimulation": { "Label": "Genesis Sim" } },
		"DefaultMapColor": "1, 0.84, 0, 1"
	})");
	LineInput in;
	in.source_map = "Ragnarok";
	in.viewer_map = "Island";
	in.name = L"Ragnar";
	in.tribe = L"Vikings";
	in.message = L"on rag..nothing important";
	auto out = FormatLine(s, in);
	CHECK(out.text == L"<RichColor Color=\"1, 0.84, 0, 1\">[Ragnarok]: </>Ragnar [Vikings]: on rag..nothing important");
	CHECK(out.icon.empty());

	in.viewer_map = "Ragnarok"; // own map hidden
	out = FormatLine(s, in);
	CHECK(out.text == L"Ragnar [Vikings]: on rag..nothing important");

	in.tribe.clear();
	out = FormatLine(s, in);
	CHECK(out.text == L"Ragnar: on rag..nothing important");

	in.mode = ChatMode::Tribe;
	in.tribe = L"Vikings";
	out = FormatLine(s, in);
	CHECK(out.text == L"(TRIBE) Ragnar [Vikings]: on rag..nothing important");

	in.mode = ChatMode::Local;
	in.viewer_map = "Island"; // local chat never shows a map, even from elsewhere
	out = FormatLine(s, in);
	CHECK(out.text == L"(LOCAL) Ragnar [Vikings]: on rag..nothing important");

	in.mode = ChatMode::Global;
	in.source_map = "Unknown";
	out = FormatLine(s, in);
	CHECK(out.text.find(L"[Unknown]: ") != std::wstring::npos); // falls back to the server key
}

static void TestFormatAse()
{
	Settings s = Parse(R"({
		"Chat": { "Formats": { "Line": "{mode}{map}{tag}{name}{tribe} : {message}", "Map": "({label}) " } },
		"Maps": { "LostIsland": { "Label": "LostIsland", "Color": "0.5098, 0.6392, 0.3725, 1" } },
		"DiscordMap": { "Label": "Discord", "Color": "0, 1, 1, 0.8" },
		"SafeIcons": [ "/Game/X.X" ],
		"StyleRules": [ { "Name": "Script", "Match": { "Groups": ["Scriptologist"] }, "Tag": "Scriptologist",
			"Icon": "/Game/X.X", "TagColor": "0.6157, 0, 1, 1", "NameColor": "0.09, 0.81, 0.9, 1", "MessageColor": "0.27, 1, 0.48, 1" } ]
	})");
	Sender me;
	me.groups = { "Scriptologist" };
	LineInput in;
	in.source_map = "LostIsland";
	in.viewer_map = "Ragnarok";
	in.name = L"Bulk Biceps";
	in.tribe = L"FireFly Commune";
	in.message = L"Evening people";
	in.style = ResolveStyle(s.styles, me);
	auto out = FormatLine(s, in);
	CHECK(out.text ==
		L"<RichColor Color=\"0.5098, 0.6392, 0.3725, 1\">(LostIsland) </>"
		L"<RichColor Color=\"0.6157, 0, 1, 1\">[Scriptologist] </>"
		L"<RichColor Color=\"0.09, 0.81, 0.9, 1\">Bulk Biceps</> [FireFly Commune] : "
		L"<RichColor Color=\"0.27, 1, 0.48, 1\">Evening people</>");
	CHECK(out.icon == "/Game/X.X");

	s.icons_enabled = false; // kill switch
	CHECK(FormatLine(s, in).icon.empty());
	s.icons_enabled = true;

	LineInput d;
	d.from_discord = true;
	d.viewer_map = "Ragnarok";
	d.name = L"Sean";
	d.message = L"hi <RichColor Color=\"1,0,0,1\">fake</>";
	out = FormatLine(s, d);
	CHECK(out.text.rfind(L"<RichColor Color=\"0, 1, 1, 0.8\">(Discord) </>Sean : hi ", 0) == 0);
	CHECK(out.text.find(L"<RichColor Color=\"1,0,0,1\">") == std::wstring::npos); // injection escaped
}

static void TestSenderField()
{
	Settings s = Parse(R"({ "Chat": { "UseSenderField": true, "HideOwnMapName": true },
		"Maps": { "Rag": { "Label": "Ragnarok", "Color": "1, 0, 0, 1" } },
		"StyleRules": [ { "Name": "x", "Tag": "Patron", "TagColor": "0, 1, 0, 1" } ] })");
	LineInput in;
	in.source_map = "Rag";
	in.viewer_map = "Island";
	in.name = L"Ragnar";
	in.tribe = L"Vikings";
	in.message = L"hi";
	in.style = ResolveStyle(s.styles, Sender{});
	auto out = FormatLine(s, in);
	CHECK(out.sender == L"[Ragnarok]");                                   // plain: no rich text in the sender field
	CHECK(out.body == L"<RichColor Color=\"0, 1, 0, 1\">[Patron] </>Ragnar [Vikings]: hi");
	in.viewer_map = "Rag";                                                // own map hidden: whole line in the message
	out = FormatLine(s, in);
	CHECK(out.sender.empty() && out.body.empty());
	in.viewer_map = "Island";
	in.mode = ChatMode::Tribe;                                            // no map on tribe chat either
	CHECK(FormatLine(s, in).sender.empty());

	Settings ase = Parse(R"({ "Chat": { "UseSenderField": true, "Formats": { "Line": "{mode}{map}{tag}{name}{tribe} : {message}", "Map": "({label}) " } } })");
	LineInput a;
	a.source_map = "Rag";
	a.viewer_map = "Island";
	a.name = L"Ragnar";
	a.message = L"hi";
	out = FormatLine(ase, a);
	CHECK(out.sender == L"(Rag)" && out.body == L"Ragnar : hi");
	a.from_discord = true;
	CHECK(FormatLine(ase, a).sender == L"(Discord)");
	CHECK(Parse("{}").use_sender_field == false);
}

static void TestNonGlobalStyle()
{
	Settings s = Parse(R"({ "Chat": { "NonGlobalStyle": "message" },
		"StyleRules": [ { "Name": "x", "Tag": "Crashmaster", "TagColor": "0, 1, 0, 1", "MessageColor": "1, 1, 1, 1" } ] })");
	CHECK(!s.nonglobal_full);
	const auto st = ResolveStyle(s.styles, Sender{});
	CHECK(StyleMessageOnly(s, st, L"hi <b>") == L"<RichColor Color=\"0, 1, 0, 1\">[Crashmaster] </><RichColor Color=\"1, 1, 1, 1\">hi \x2039" L"b></>");
	CHECK(Parse("{}").nonglobal_full);
	CHECK(HasWarning(Parse(R"({ "Chat": { "NonGlobalStyle": "odd" } })"), "NonGlobalStyle"));
}

static void TestCommands()
{
	const Settings s = Parse(R"({ "Chat": { "CommandPrefixes": ["/", "!"] } })");
	CHECK(IsCommand(s, L"/linkDiscord AB12CD"));
	CHECK(IsCommand(s, L"  !kit starter"));
	CHECK(!IsCommand(s, L"hello /there"));
	CHECK(!IsCommand(s, L""));

	const auto c = ParseCommand(L"  /LinkDiscord   ab12cd  extra ");
	CHECK(c.name == L"/linkdiscord");
	CHECK(c.args.size() == 2 && c.args[0] == L"ab12cd");
	CHECK(IsCommandName(c, s.commands.link));
	CHECK(!IsCommandName(c, s.commands.hide_tag));

	CHECK(NormalizeLinkCode(L"ab12cd") == "AB12CD");
	CHECK(NormalizeLinkCode(L"abc").empty());
	CHECK(NormalizeLinkCode(L"ab-12cd").empty());
	CHECK(NormalizeLinkCode(L"ABCDEFGHIJKLM").empty());

	Settings shortMax = Parse(R"({ "Chat": { "MaxMessageLength": 5 } })");
	CHECK(ClampLength(shortMax, L"123456789") == L"12345");
	CHECK(ClampLength(shortMax, L"123") == L"123");
}

static void TestWordFilter()
{
	Settings s = Parse(R"({ "WordFilter": { "Patterns": [ "\\bbadword\\b", "[Ss][Ll]+[Uu][Rr]", "(unclosed" ] } })");
	CHECK(HasWarning(s, "not a valid regex"));
	CHECK(s.word_filter.compiled->size() == 2);
	CHECK(ContainsBlockedWord(s, L"you BADWORD you"));
	CHECK(ContainsBlockedWord(s, L"sllllur"));
	CHECK(!ContainsBlockedWord(s, L"badwords are fine as part of another word"));
	CHECK(!ContainsBlockedWord(s, L"can an admin help me tame this"));
	s.word_filter.enabled = false;
	CHECK(!ContainsBlockedWord(s, L"badword"));
}

static void TestSpam()
{
	AntiSpam cfg; // 5 per 10 s, 3 repeats per 30 s
	SpamTracker t;
	// Fast but varied conversation is fine up to the burst limit.
	for (int i = 0; i < 5; ++i) CHECK(t.Check(cfg, "p1", L"msg " + std::to_wstring(i), 100).allowed);
	auto r = t.Check(cfg, "p1", L"msg 6", 101);
	CHECK(!r.allowed && r.retry_after == 9);
	CHECK(t.Check(cfg, "p1", L"later", 110).allowed); // window passed
	CHECK(t.Check(cfg, "p2", L"other player", 101).allowed); // per player

	// Repeats: third near-identical copy within 30 s is blocked even when spaced out.
	SpamTracker u;
	CHECK(u.Check(cfg, "p3", L"WTS rex", 0).allowed);
	CHECK(u.Check(cfg, "p3", L"wts  REX!", 12).allowed);
	r = u.Check(cfg, "p3", L"W.T.S rex", 24);
	CHECK(!r.allowed && r.retry_after == 6);
	CHECK(u.Check(cfg, "p3", L"WTS rex", 31).allowed); // first copy aged out
	CHECK(u.Check(cfg, "p3", L"something new", 32).allowed);

	SpamResult notice{ false, 3 };
	Settings s;
	CHECK(SpamNotice(s, notice) == "Slow down, you can chat again in 3s.");
	t.Forget("p1");
	CHECK(t.Check(cfg, "p1", L"fresh", 101).allowed);
}

static void TestMutes()
{
	Settings s;
	Mute m{ "blocked word", 1000 + 192, "filter" };
	CHECK(IsActive(m, 1000));
	CHECK(!IsActive(m, 1192));
	CHECK(MuteNotice(s, m, 1000) == "You're muted (reason: blocked word). 3m 12s left.");
	Mute perm{ "spamming ads", 0, "Bulk" };
	CHECK(IsActive(perm, 999999999));
	CHECK(MuteNotice(s, perm, 0) == "You're muted (reason: spamming ads).");
	CHECK(BlockedWordNotice(s) == "Your message wasn't sent: it contained a blocked word. You're muted for 5m.");
	CHECK(ParseMuteLength("30") == std::optional<long long>(1800));
	CHECK(ParseMuteLength("PERM") == std::optional<long long>(0));
	CHECK(!ParseMuteLength("0"));
	CHECK(!ParseMuteLength("-5"));
	CHECK(!ParseMuteLength("5m"));
}

static void TestIgnorePicker()
{
	RecentSpeakers r(3);
	r.Add({ "a", L"Alpha" });
	r.Add({ "b", L"Bravo" });
	r.Add({ "a", L"Alpha" }); // moves to front, no duplicate
	r.Add({ "c", L"Charlie" });
	r.Add({ "d", L"Delta" }); // capacity 3 drops the oldest (b)
	const auto list = r.For("c");
	CHECK(list.size() == 2 && list[0].player_key == "d" && list[1].player_key == "a");
	CHECK(ParseChoice(L"2", 2) == std::optional<size_t>(1));
	CHECK(!ParseChoice(L"3", 2));
	CHECK(!ParseChoice(L"0", 2));
	CHECK(!ParseChoice(L"x", 2));
}

static void TestCommandLine()
{
	auto a = ParseCommandLine("ShooterGameServer.exe Ragnarok_WP?listen?SessionName=Fire Fly?MultiHome=192.168.1.20?RCONEnabled=True?RCONPort=27020 -server -log -NoBattlEye");
	CHECK(a.multihome == "192.168.1.20" && a.rcon_port == 27020);
	a = ParseCommandLine("\"C:\\ARK\\ShooterGameServer.exe\" TheIsland?listen -MultiHome=10.0.0.5 -rconport=27030");
	CHECK(a.multihome == "10.0.0.5" && a.rcon_port == 27030);
	a = ParseCommandLine("ShooterGameServer.exe TheIsland?listen?RCONPort=99999");
	CHECK(a.multihome.empty() && a.rcon_port == 0);
	a = ParseCommandLine("ShooterGameServer.exe Ragnarok_WP?listen -ServerKey=Ragnarok -server");
	CHECK(a.server_key == "Ragnarok");
	a = ParseCommandLine("ShooterGameServer.exe Map-Name?listen -server");
	CHECK(a.multihome.empty() && a.rcon_port == 0);
}

static void TestIni()
{
	const std::string ini = "[SessionSettings]\r\nSessionName=Fire Fly\r\n\r\n[ServerSettings]\r\nRCONEnabled=True\r\n RCONPort = 27020 \r\n; RCONPort=1\r\n[Other]\r\nRCONPort=9\r\n";
	CHECK(IniValue(ini, "ServerSettings", "RCONPort") == "27020");
	CHECK(IniValue(ini, "serversettings", "rconenabled") == "True");
	CHECK(IniValue(ini, "ServerSettings", "Missing").empty());
	CHECK(IniValue(ini, "Nope", "RCONPort").empty());
}

static void TestDefaultsAndWarnings()
{
	const Settings d = Parse("{}");
	CHECK(d.warnings.empty());
	CHECK(d.relay_global && d.command_prefixes == std::vector<std::string>{ "/" });
	CHECK(d.commands.link == "/linkDiscord");
	CHECK(d.poll_seconds == 1 && d.heartbeat_seconds == 15);
	CHECK(d.anti_spam.burst_messages == 5 && d.anti_spam.repeat_count == 3);

	const Settings bad = Parse(R"({
		"Chat": { "Formats": { "Line": "{name}: oops" } },
		"AntiSpam": { "BurstMessages": 0 },
		"Maps": { "Island": { "Color": "green" } },
		"Intervals": { "PollSeconds": 0, "HeartbeatSeconds": 1 }
	})");
	CHECK(HasWarning(bad, "no {message}"));
	CHECK(bad.formats.line == Formats{}.line);
	CHECK(HasWarning(bad, "AntiSpam"));
	CHECK(bad.anti_spam.burst_messages == 5);
	CHECK(HasWarning(bad, "is not a color"));
	CHECK(bad.poll_seconds == 1 && bad.heartbeat_seconds == 5);

	const Settings wrongType = Parse(R"({ "Chat": { "HideOwnMapName": "yes" } })");
	CHECK(HasWarning(wrongType, "wrong type"));
}

// ----------------------------------------------------------------------------------------------
// Config file validation
// ----------------------------------------------------------------------------------------------

static int ValidateConfig(const char* path, bool shipped)
{
	std::ifstream file(path);
	if (!file) { std::cerr << "cannot open " << path << "\n"; return 1; }
	Settings s;
	try { s = ParseSettings(nlohmann::json::parse(file, nullptr, true, true)); }
	catch (const std::exception& e) { std::cerr << path << ": " << e.what() << "\n"; return 1; }
	for (const auto& w : s.warnings) { std::cerr << path << ": warning: " << w << "\n"; ++g_failures; }
	if (shipped)
	{
		CHECK(s.relay_global);
		CHECK(s.commands.link == "/linkDiscord");
		CHECK(!s.styles.empty());
		CHECK(!s.safe_icons.empty());
		CHECK(s.word_filter.enabled && !s.word_filter.compiled->empty());
		CHECK(s.anti_spam.burst_messages == 5 && s.anti_spam.burst_seconds == 10);
		CHECK(s.anti_spam.repeat_count == 3 && s.anti_spam.repeat_seconds == 30);
		CHECK(s.mode_labels.at(ChatMode::Local) == "(LOCAL)");
		for (const auto& r : s.styles) if (r.icon) CHECK(s.safe_icons.count(*r.icon));
		// Everyday words that must never trip the filter.
		for (const wchar_t* ok : { L"peacock", L"cockatiel", L"Scunthorpe", L"can an admin help me", L"admin abuse report",
			L"homemade", L"assassin", L"therapist", L"grape", L"raptor", L"spice", L"night", L"snigger", L"Fagus tree" })
			if (ContainsBlockedWord(s, ok)) { std::wcerr << L"filter false positive: " << ok << L"\n"; ++g_failures; }
	}
	return 0;
}

int main(int argc, char** argv)
{
	TestUtf8();
	TestEscapeAndDuration();
	TestColors();
	TestStyles();
	TestDiscordStyles();
	TestFormatAsa();
	TestFormatAse();
	TestCommands();
	TestNonGlobalStyle();
	TestSenderField();
	TestWordFilter();
	TestSpam();
	TestMutes();
	TestIgnorePicker();
	TestCommandLine();
	TestIni();
	TestDefaultsAndWarnings();

	if (argc > 1 && ValidateConfig(argv[1], argc > 2 && std::string(argv[2]) == "--shipped") != 0) return 1;

	std::cout << g_checks << " checks, " << g_failures << " failures\n";
	return g_failures == 0 ? 0 : 1;
}
