using System.Text;
using Discord;
using Discord.WebSocket;

namespace ServerComms.Bot;

// Player lists (public and admin), the crosschat channel topic and the bot's presence, all built
// from the servers / players_online tables the plugins keep current.
//
//  - Each list is one message the bot edits in place; its ID is kept in bot_state, so restarts edit
//    the same message instead of posting a new one. If it was deleted, a new one is posted.
//  - Maps show "N Players" with a code block of players, "Online - No Players", or "Offline" once
//    their heartbeat is older than OfflineAfterSeconds.
//  - The topic changes at most once per 10 minutes and only when the text changed (Discord allows
//    about two topic edits per 10 minutes per channel).
public sealed class StatusBoard(
    BotOptions options, Database db, BotState state, DatabaseStartup dbStartup,
    DiscordConnection discord, ILogger<StatusBoard> logger) : BackgroundService
{
    private readonly StatusOptions _status = options.Status;
    private readonly Dictionary<string, DateTime> _lastRefresh = new();
    private readonly Dictionary<string, string> _lastStates = new();
    private string? _lastTopic;
    private DateTime _lastTopicAt = DateTime.MinValue;
    private int _presenceIndex;
    private DateTime _lastPresenceAt = DateTime.MinValue;
    private bool _failing;

    private sealed record Server(string Key, string Name, int Players, double FpsCur, double FpsAvg, long LastHeartbeat, bool Starting);
    private sealed record Player(string ServerKey, string Key, long PlayerId, string Name, string SteamName, long TribeId,
        string Tribe, string Platform, string Tag, long JoinedAt, ulong DiscordId);
    private sealed record Snapshot(List<Server> Servers, List<Player> Players, long Now)
    {
        public bool IsFresh(Server s, int offlineAfter) => Now - s.LastHeartbeat <= offlineAfter;
        public bool IsOnline(Server s, int offlineAfter) => IsFresh(s, offlineAfter) && !s.Starting;
        public bool IsStarting(Server s, int offlineAfter) => IsFresh(s, offlineAfter) && s.Starting;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.WhenAll(dbStartup.Ready, discord.Ready).WaitAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = await LoadAsync(ct);
                await RefreshListAsync("playerlist", _status.PlayerList, snapshot, admin: false, ct);
                await RefreshListAsync("adminlist", _status.AdminList, snapshot, admin: true, ct);
                await RefreshTopicAsync(snapshot);
                await RefreshPresenceAsync(snapshot);
                if (_failing)
                    logger.LogInformation("Player lists updating again");
                _failing = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                if (!_failing)
                    logger.LogWarning("Player list update failed: {Error}. Retrying.", e.Message);
                _failing = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var forget = now - (long)_status.ForgetAfterHours * 3600;
        var hidden = new HashSet<string>(_status.HideServers, StringComparer.OrdinalIgnoreCase);
        var servers = new List<Server>();
        var players = new List<Player>();

        await using var connection = await db.OpenAsync(ct);
        await using (var command = Database.Command(connection,
            "SELECT server_key, display_name, players, fps_cur, fps_avg, last_heartbeat, state FROM servers WHERE last_heartbeat >= @forget",
            ("@forget", forget)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                if (hidden.Contains(key))
                    continue;
                var name = reader.GetString(1);
                servers.Add(new Server(key, name.Length > 0 ? name : key, reader.GetInt32(2),
                    reader.GetFloat(3), reader.GetFloat(4), reader.GetInt64(5), reader.GetString(6) == "starting"));
            }
        }
        await using (var command = Database.Command(connection,
            "SELECT p.server_key, p.player_key, p.player_id, p.name, p.steam_name, p.tribe_id, p.tribe_name, p.platform, " +
            "p.discord_tag, p.joined_at, COALESCE(l.discord_id, 0) FROM players_online p " +
            "LEFT JOIN links l ON l.player_key = p.player_key ORDER BY p.name"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                players.Add(new Player(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                    reader.GetInt64(9), Convert.ToUInt64(reader.GetValue(10))));
        }

        var order = _status.ServerOrder.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.OrdinalIgnoreCase);
        servers = servers
            .OrderBy(s => order.TryGetValue(s.Key, out var i) ? i : int.MaxValue)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Snapshot(servers, players, now);
    }

    private (int Maps, int Players) Counts(Snapshot s)
    {
        var online = s.Servers.Where(x => s.IsOnline(x, _status.OfflineAfterSeconds)).ToList();
        var keys = online.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (online.Count, s.Players.Count(p => keys.Contains(p.ServerKey)));
    }

    private string Fill(string text, Snapshot s)
    {
        var (maps, players) = Counts(s);
        return text.Replace("{cluster}", _status.ClusterName)
            .Replace("{maps}", maps.ToString()).Replace("{maps_s}", maps == 1 ? "" : "s")
            .Replace("{players}", players.ToString()).Replace("{players_s}", players == 1 ? "" : "s");
    }

    // ---------------------------------------------------------------------------------------------

    private async Task RefreshListAsync(string key, PlayerListOptions list, Snapshot snapshot, bool admin, CancellationToken ct)
    {
        if (!list.Enabled)
            return;
        // Map state changes (Offline / Starting / Online) refresh within ~15 s; player changes wait for
        // the normal interval.
        var states = string.Join('|', snapshot.Servers.Select(x =>
            $"{x.Key}:{(snapshot.IsStarting(x, _status.OfflineAfterSeconds) ? "s" : snapshot.IsOnline(x, _status.OfflineAfterSeconds) ? "o" : "x")}"));
        var stateChanged = !_lastStates.TryGetValue(key, out var lastStates) || lastStates != states;
        if (_lastRefresh.TryGetValue(key, out var at))
        {
            var since = DateTime.UtcNow - at;
            var due = since >= TimeSpan.FromSeconds(Math.Max(30, list.RefreshSeconds)) || (stateChanged && since >= TimeSpan.FromSeconds(15));
            if (!due)
                return;
        }
        if (discord.Client.GetChannel(list.ChannelId) is not ITextChannel channel)
            throw new InvalidOperationException($"{key} channel {list.ChannelId} not found or not a text channel");

        var embed = BuildEmbed(list, snapshot, admin);
        var stateKey = key + ".message";
        IUserMessage? message = null;
        if (await state.GetAsync(stateKey, ct) is { } saved && ulong.TryParse(saved, out var messageId))
            message = await channel.GetMessageAsync(messageId) as IUserMessage;

        if (message is not null && message.Author.Id == discord.Client.CurrentUser.Id)
            await message.ModifyAsync(m => { m.Embed = embed; m.Content = ""; });
        else
        {
            message = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
            await state.SetAsync(stateKey, message.Id.ToString(), ct);
            logger.LogInformation("Posted the {List} in #{Channel}", key, channel.Name);
        }
        _lastRefresh[key] = DateTime.UtcNow;
        _lastStates[key] = states;
    }

    private Embed BuildEmbed(PlayerListOptions list, Snapshot s, bool admin)
    {
        var builder = new EmbedBuilder()
            .WithTitle(Truncate(Fill(list.Title, s), 256))
            .WithColor(ParseColor(list.Color))
            .WithCurrentTimestamp();
        if (list.Footer.Length > 0)
            builder.WithFooter(Fill(list.Footer, s));

        var byServer = s.Players.ToLookup(p => p.ServerKey, StringComparer.OrdinalIgnoreCase);
        var guild = discord.Guild;
        var total = builder.Title.Length;
        foreach (var server in s.Servers.Take(25))   // Discord: at most 25 fields per embed
        {
            var online = s.IsOnline(server, _status.OfflineAfterSeconds);
            var players = online ? byServer[server.Key].ToList() : new List<Player>();
            var title = list.Server
                .Replace("{map}", server.Name)
                .Replace("{count}", players.Count.ToString())
                .Replace("{s}", players.Count == 1 ? "" : "s")
                .Replace("{fps_cur}", server.FpsCur.ToString("0.0"))
                .Replace("{fps_avg}", server.FpsAvg.ToString("0.0"));

            string body;
            if (s.IsStarting(server, _status.OfflineAfterSeconds))
            {
                title = server.Name;
                body = list.Starting;
            }
            else if (!online)
            {
                title = server.Name;
                body = list.Offline;
            }
            else if (players.Count == 0)
            {
                title = server.Name;
                body = list.NoPlayers;
            }
            else
            {
                var lines = players.Select(p => PlayerLine(list, p, s.Now, guild)).ToList();
                body = string.Join('\n', lines);
                // A field holds 1024 characters including the code fences.
                while (body.Length > 1010 && lines.Count > 1)
                {
                    lines.RemoveAt(lines.Count - 1);
                    body = string.Join('\n', lines) + $"\n… and {players.Count - lines.Count} more";
                }
            }
            var value = "```\n" + Truncate(body, 1010) + "\n```";
            title = Truncate(title, 256);
            if (total + title.Length + value.Length > 5800)  // Discord: 6000 characters per embed
                break;
            total += title.Length + value.Length;
            builder.AddField(title, value);
        }
        if (s.Servers.Count == 0)
            builder.WithDescription(list.Offline);
        return builder.Build();
    }

    private static string PlayerLine(PlayerListOptions list, Player p, long now, SocketGuild? guild)
    {
        var discordName = p.DiscordId != 0 ? guild?.GetUser(p.DiscordId)?.DisplayName ?? "" : "";
        var line = list.Player
            .Replace("{platform}", p.Platform)
            .Replace("{tag}", p.Tag)
            .Replace("{name}", p.Name)
            .Replace("{tribe}", p.Tribe.Length > 0 ? list.TribeFormat.Replace("{tribe}", p.Tribe) : "")
            .Replace("{steam_name}", p.SteamName)
            .Replace("{online}", Duration(now - p.JoinedAt))
            .Replace("{discord}", discordName.Length > 0 ? list.DiscordFormat.Replace("{discord}", discordName) : "")
            .Replace("{key}", p.Key)
            .Replace("{player_id}", p.PlayerId.ToString())
            .Replace("{tribe_id}", p.TribeId.ToString());
        // Tidy gaps left by empty fields ("[Steam]  Name" when there's no tag), and keep code fences intact.
        while (line.Contains("  "))
            line = line.Replace("  ", " ");
        return line.Replace("`", "'").Trim();
    }

    // ---------------------------------------------------------------------------------------------

    private async Task RefreshTopicAsync(Snapshot s)
    {
        var topic = _status.Topic;
        if (!topic.Enabled)
            return;
        var text = Truncate(Fill(topic.Format, s), 1024);
        if (text == _lastTopic || DateTime.UtcNow - _lastTopicAt < TimeSpan.FromMinutes(10))
            return;
        if (discord.Client.GetChannel(topic.ChannelId) is not ITextChannel channel)
            throw new InvalidOperationException($"topic channel {topic.ChannelId} not found or not a text channel");
        if (channel.Topic != text)
            await channel.ModifyAsync(c => c.Topic = text);
        _lastTopic = text;
        _lastTopicAt = DateTime.UtcNow;
    }

    private async Task RefreshPresenceAsync(Snapshot s)
    {
        var presence = _status.Presence;
        if (!presence.Enabled || DateTime.UtcNow - _lastPresenceAt < TimeSpan.FromSeconds(Math.Max(20, presence.RotateSeconds)))
            return;
        var statuses = presence.EffectiveStatuses;
        var entry = statuses[_presenceIndex++ % statuses.Count];
        if (entry.Length < 2)
            return;
        var type = entry[0].ToLowerInvariant() switch
        {
            "watching" => ActivityType.Watching,
            "listening" => ActivityType.Listening,
            "competing" => ActivityType.Competing,
            _ => ActivityType.Playing,
        };
        await discord.Client.SetActivityAsync(new Game(Fill(entry[1], s), type));
        _lastPresenceAt = DateTime.UtcNow;
    }

    // ---------------------------------------------------------------------------------------------

    private static string Duration(long seconds)
    {
        if (seconds < 0) seconds = 0;
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        return h > 0 ? $"{h}h {m}m" : $"{m}m";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static Color ParseColor(string text)
    {
        var hex = text.Trim().TrimStart('#');
        return hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? new Color(rgb)
            : Color.Green;
    }
}
