namespace ServerComms.Bot;

// Settings from appsettings.json (section "ServerComms"). One file per machine: the ASE box and the
// ASA box each run their own copy of the bot with their own token, channels and database.
public sealed class BotOptions
{
    /// <summary>"ASA" or "ASE": decides the player key (EOS ID or SteamID64) and the lookup defaults.</summary>
    public string Game { get; set; } = "";

    public DiscordOptions Discord { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public CrosschatOptions Crosschat { get; set; } = new();
    public StatusOptions Status { get; set; } = new();
    public LinkingOptions Linking { get; set; } = new();
    public LookupOptions Lookups { get; set; } = new();
    public RconOptions Rcon { get; set; } = new();

    /// <summary>Roles allowed to use /servercomms (reload, repost). Discord administrators always can.</summary>
    public List<ulong> AdminRoleIds { get; set; } = [];

    /// <summary>Optional private channel for bot notices: broken panel links, RCON audit.</summary>
    public ulong AdminLogChannelId { get; set; }

    /// <summary>Returns readable problems; the bot refuses to start while there are any.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (Game is not ("ASA" or "ASE"))
            problems.Add("ServerComms:Game must be \"ASA\" or \"ASE\"");
        if (string.IsNullOrWhiteSpace(Discord.Token))
            problems.Add("ServerComms:Discord:Token is empty (the bot token from the Discord developer portal)");
        if (Discord.GuildId == 0)
            problems.Add("ServerComms:Discord:GuildId is missing (right-click the server in Discord > Copy Server ID)");
        if (string.IsNullOrWhiteSpace(Database.Host))
            problems.Add("ServerComms:Database:Host is empty");
        if (string.IsNullOrWhiteSpace(Database.User))
            problems.Add("ServerComms:Database:User is empty");
        if (string.IsNullOrWhiteSpace(Database.Name))
            problems.Add("ServerComms:Database:Name is empty");
        if (Database.Port is <= 0 or > 65535)
            problems.Add("ServerComms:Database:Port must be 1-65535");
        if (Linking.KeepGroups.Count > 0 && string.IsNullOrEmpty(Lookups.Permissions.Database))
            problems.Add("ServerComms:Linking:KeepGroups needs ServerComms:Lookups:Permissions:Database (or empty KeepGroups)");
        if (Crosschat.Enabled && Crosschat.ChannelId == 0)
            problems.Add("ServerComms:Crosschat:ChannelId is missing (right-click the crosschat channel > Copy Channel ID), or set Enabled to false");
        if (Status.PlayerList.Enabled && Status.PlayerList.ChannelId == 0)
            problems.Add("ServerComms:Status:PlayerList:ChannelId is missing, or set Enabled to false");
        if (Status.AdminList.Enabled && Status.AdminList.ChannelId == 0)
            problems.Add("ServerComms:Status:AdminList:ChannelId is missing, or set Enabled to false");
        if (Status.Topic.Enabled && Status.Topic.ChannelId == 0)
            problems.Add("ServerComms:Status:Topic:ChannelId is missing, or set Enabled to false");
        foreach (var (what, value) in global::ServerComms.Bot.Lookups.Identifiers(Lookups))
            if (!string.IsNullOrEmpty(value) && !LookupOptions.IsIdentifier(value))
                problems.Add($"ServerComms:Lookups:{what} \"{value}\" may only contain letters, digits and _");
        if (Crosschat.Enabled && !Crosschat.LineFormat.Contains("{message}"))
            problems.Add("ServerComms:Crosschat:LineFormat must contain {message}");
        return problems;
    }
}

public sealed class DiscordOptions
{
    public string Token { get; set; } = "";
    public ulong GuildId { get; set; }
}

public sealed class DatabaseOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "servercomms";
    public string Password { get; set; } = "";
    public string Name { get; set; } = "servercomms";
}

public sealed class CrosschatOptions
{
    public bool Enabled { get; set; } = true;
    public ulong ChannelId { get; set; }

    /// <summary>Game chat as posted to Discord. {time} {platform} {map} {name} {tribe} {tag} {message}.
    /// {tribe} expands with TribeFormat, or to nothing when the player has no tribe.</summary>
    public string LineFormat { get; set; } = "[{time}] {platform} **[{map}] {name}{tribe}**: {message}";
    public string TribeFormat { get; set; } = " [{tribe}]";
    public string TimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Lines arriving within this many seconds go out as one Discord message.</summary>
    public int BatchSeconds { get; set; } = 2;
    public int PollSeconds { get; set; } = 1;

    /// <summary>Discord messages longer than this are cut before they go in game.</summary>
    public int MaxLengthToGame { get; set; } = 250;

    /// <summary>After an outage, at most this many backlog lines are posted; older ones are summarised.</summary>
    public int MaxCatchUpLines { get; set; } = 50;

    public int PurgeAfterDays { get; set; } = 7;
}

public sealed class StatusOptions
{
    /// <summary>Shown as {cluster} in titles and the topic.</summary>
    public string ClusterName { get; set; } = "Cluster";

    /// <summary>A map counts as Offline after this long without a heartbeat from its plugin.</summary>
    public int OfflineAfterSeconds { get; set; } = 60;

    /// <summary>Maps silent for longer than this drop off the lists entirely (retired or renamed).</summary>
    public int ForgetAfterHours { get; set; } = 24;

    /// <summary>Server keys never shown (e.g. a test server).</summary>
    public List<string> HideServers { get; set; } = [];

    /// <summary>Server keys in display order; maps not listed follow alphabetically.</summary>
    public List<string> ServerOrder { get; set; } = [];

    public PlayerListOptions PlayerList { get; set; } = new();
    public PlayerListOptions AdminList { get; set; } = new()
    {
        Enabled = false,
        Server = "{map}: {count} player{s}. FPS {fps_cur} (avg {fps_avg})",
        Player = "({online}) {name}{tribe} ({key}, player {player_id}, tribe {tribe_id}){discord}",
        DiscordFormat = " Discord: {discord}",
    };
    public TopicOptions Topic { get; set; } = new();
    public PresenceOptions Presence { get; set; } = new();
}

public sealed class PlayerListOptions
{
    public bool Enabled { get; set; } = true;
    public ulong ChannelId { get; set; }
    public int RefreshSeconds { get; set; } = 120;
    public string Color { get; set; } = "#00FF00";
    public string Title { get; set; } = "{cluster}: {maps} Map{maps_s}, {players} Player{players_s}";

    /// <summary>Per-map heading. {map} {count} {fps_cur} {fps_avg}.</summary>
    public string Server { get; set; } = "{map}: {count} Player{s}";

    /// <summary>Per-player line. {platform} {tag} {name} {tribe} {steam_name} {online} {discord}
    /// {key} {player_id} {tribe_id}. {tribe} uses TribeFormat; {discord} is the linked member's
    /// display name or empty.</summary>
    public string Player { get; set; } = "[{platform}] {tag} {name}{tribe}";
    public string TribeFormat { get; set; } = " [{tribe}]";

    /// <summary>What {discord} becomes for a linked player; unlinked players get nothing.</summary>
    public string DiscordFormat { get; set; } = "{discord}";
    public string NoPlayers { get; set; } = "Online - No Players";
    public string Offline { get; set; } = "Offline";
    /// <summary>Shown while a map's world is still loading (its plugin reports "starting").</summary>
    public string Starting { get; set; } = "Starting";
    public string Footer { get; set; } = "";
}

public sealed class TopicOptions
{
    public bool Enabled { get; set; } = true;
    public ulong ChannelId { get; set; }
    public string Format { get; set; } = "{cluster}: {maps} Map{maps_s}, {players} Player{players_s}";
}

public sealed class PresenceOptions
{
    public bool Enabled { get; set; } = true;
    public int RotateSeconds { get; set; } = 60;

    /// <summary>Each entry: "Playing", "Watching", "Listening" or "Competing", then text with {maps}
    /// {players}. Empty = a built-in pair. (Left empty here because the config binder appends to a
    /// pre-filled list instead of replacing it.)</summary>
    public List<string[]> Statuses { get; set; } = [];

    public IReadOnlyList<string[]> EffectiveStatuses => Statuses.Count > 0
        ? Statuses
        : new List<string[]> { new[] { "Playing", "with {players} player{players_s}" }, new[] { "Watching", "{maps} map{maps_s}, {players} player{players_s}" } };
}

public sealed class LinkingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>The in-game command players type; must match Commands.Link in the plugins' config.json.</summary>
    public string GameCommand { get; set; } = "/linkDiscord";
    public int CodeMinutes { get; set; } = 10;

    /// <summary>Roles given on link and taken away on unlink (e.g. Verified / Survivor).</summary>
    public List<ulong> VerifiedRoleIds { get; set; } = [];

    /// <summary>Renames the member to their character on link. The server owner can't be renamed by bots.</summary>
    public bool RenameMembers { get; set; } = true;
    public string NicknameFormat { get; set; } = "{name}{tribe}";
    public string NicknameTribeFormat { get; set; } = " [{tribe}]";

    public bool UnlinkOnLeave { get; set; } = true;

    /// <summary>Unlink players not seen in game for this many days (0 = never). Members of
    /// KeepGroups (Permissions groups, e.g. Vacation) are never auto-unlinked.</summary>
    public int AutoUnlinkDays { get; set; } = 30;
    public List<string> KeepGroups { get; set; } = [];   // needs Lookups:Permissions

    public string CodeMessage { get; set; } =
        "Your link code is **{code}**. In game, type `{command} {code}` in chat within {minutes} minutes.";
    public string CodeReplacesMessage { get; set; } = "Linking again replaces your current link.";
    public string LinkedMessage { get; set; } = "Linked to **{name}**.";
    public string NotLinkedMessage { get; set; } = "You aren't linked. Use the Link button (or /link) first.";
    public string UnlinkConfirmMessage { get; set; } = "Unlink your Discord account from **{name}**?";
    public string UnlinkedMessage { get; set; } = "You've been unlinked.";
}

// Read-only lookups in other plugins' databases. Host/User/Password default to the main database
// login; a separate read-only user is better (SELECT on just these tables).
public sealed class LookupOptions
{
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }

    public PermissionsLookup Permissions { get; set; } = new();
    public ArkShopLookup ArkShop { get; set; } = new();
    public TokenBankLookup TokenBank { get; set; } = new();
    public PlayerLoginLookup PlayerLogin { get; set; } = new();

    public static bool IsIdentifier(string value) => value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

public sealed class PermissionsLookup
{
    /// <summary>Empty = this lookup is off.</summary>
    public string Database { get; set; } = "";
    public string Table { get; set; } = "players";
    /// <summary>Default: EOS_Id on ASA, SteamId on ASE.</summary>
    public string? KeyColumn { get; set; }
    public string GroupsColumn { get; set; } = "PermissionGroups";
}

public sealed class ArkShopLookup
{
    /// <summary>Empty = this lookup is off.</summary>
    public string Database { get; set; } = "";
    public string Table { get; set; } = "arkshopplayers";
    /// <summary>Default: EosId on ASA, SteamId on ASE.</summary>
    public string? KeyColumn { get; set; }
    public string PointsColumn { get; set; } = "Points";
}

public sealed class TokenBankLookup
{
    /// <summary>Empty = this lookup is off.</summary>
    public string Database { get; set; } = "";
    /// <summary>Default: tokenbank_players on ASA, players on ASE.</summary>
    public string? Table { get; set; }
    /// <summary>Default: EosId on ASA, SteamId on ASE.</summary>
    public string? KeyColumn { get; set; }
    public string TokensColumn { get; set; } = "Tokens";
    public string TypeColumn { get; set; } = "TokenType";
}

public sealed class PlayerLoginLookup
{
    /// <summary>Empty = this lookup is off.</summary>
    public string Database { get; set; } = "";
    public string Table { get; set; } = "characters";
    /// <summary>Default: eos_id on ASA, steam_id on ASE.</summary>
    public string? KeyColumn { get; set; }
    public string ServerColumn { get; set; } = "server_key";
    public string LastSeenColumn { get; set; } = "last_seen";
}

public sealed class RconOptions
{
    /// <summary>Password used for every map unless the map has its own under Servers.</summary>
    public string Password { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Channel for the RCON Commander panel (0 = no panel; /rcon still works).</summary>
    public ulong CommanderChannelId { get; set; }

    /// <summary>true: /rcon replies only to the person who ran it. Commander cards are always visible
    /// in their channel, so keep that channel private.</summary>
    public bool EphemeralReplies { get; set; } = true;

    /// <summary>Extra commands that need a Confirm click first (first word, case-insensitive), added to
    /// the built-in list (DoExit, DestroyWildDinos, DestroyAll, ...), e.g. other plugins' wipe commands.</summary>
    public List<string> ConfirmCommands { get; set; } = [];

    /// <summary>Role ID -> commands that role may run (first word; "*" = all). Discord administrators
    /// and AdminRoleIds may run everything; anyone else needs a role here.</summary>
    public Dictionary<string, List<string>> RolePermissions { get; set; } = new();

    public IReadOnlyCollection<string> EffectiveConfirmCommands => ConfirmCommands.Concat(
        new[] { "DoExit", "DestroyWildDinos", "DestroyAll", "DestroyAllEnemies", "DestroyStructures", "DestroyTribeStructures",
                "DestroyTribeDinos", "DestroyTribePlayers", "DestroyTribeIdStructures", "DestroyTribeIdDinos", "DestroyTribeIdPlayers",
                "KillPlayer", "BanPlayer", "Quit", "Exit" }).ToList();

    /// <summary>Per-map overrides by server key: Host, Port and/or Password. Normally not needed.</summary>
    public Dictionary<string, RconServerOverride> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RconServerOverride
{
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Password { get; set; }
}
