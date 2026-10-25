using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Discord;
using Discord.WebSocket;

namespace ServerComms.Bot;

// Two-way crosschat between the cluster's global chat and one Discord channel.
//
//  Game -> Discord: polls messages (source 'game') by id cursor, formats each line, and posts lines
//  that arrive within BatchSeconds as one Discord message. The cursor (bot_state 'crosschat.cursor')
//  only moves after a successful post, so a Discord or database outage delays chat but never loses
//  or repeats it. A long backlog is trimmed to the last MaxCatchUpLines lines plus a count.
//
//  Discord -> game: user messages in the channel are written as source 'discord' rows with the
//  author's display name and role IDs; each map styles them with its DiscordStyles and shows them.
//
// Mentions never ping anyone (AllowedMentions.None) and player text is markdown-escaped.
public sealed class Crosschat : BackgroundService
{
    private const string CursorKey = "crosschat.cursor";
    private const int DiscordLimit = 2000;

    private readonly BotOptions _options;
    private readonly CrosschatOptions _chat;
    private readonly Database _db;
    private readonly BotState _state;
    private readonly DatabaseStartup _dbStartup;
    private readonly DiscordConnection _discord;
    private readonly ILogger<Crosschat> _logger;
    private readonly Channel<SocketMessage> _toGame = Channel.CreateUnbounded<SocketMessage>(new() { SingleReader = true });

    private readonly Dictionary<string, string> _mapNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _mapNamesAt = DateTime.MinValue;

    public Crosschat(BotOptions options, Database db, BotState state, DatabaseStartup dbStartup,
        DiscordConnection discord, ILogger<Crosschat> logger)
    {
        _options = options;
        _chat = options.Crosschat;
        _db = db;
        _state = state;
        _dbStartup = dbStartup;
        _discord = discord;
        _logger = logger;
        _discord.Client.MessageReceived += OnDiscordMessage;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_chat.Enabled)
            return;
        await Task.WhenAll(_dbStartup.Ready, _discord.Ready).WaitAsync(ct);
        _logger.LogInformation("Crosschat running (channel {Channel})", _chat.ChannelId);
        await Task.WhenAll(GameToDiscordAsync(ct), DiscordToGameAsync(ct), PurgeLoopAsync(ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Game -> Discord
    // ---------------------------------------------------------------------------------------------

    private sealed record Line(long Id, string Text);

    private async Task GameToDiscordAsync(CancellationToken ct)
    {
        long cursor = await InitialCursorAsync(ct);
        var pending = new List<Line>();
        DateTime firstPendingAt = default;
        var backoff = TimeSpan.FromSeconds(_chat.PollSeconds);
        var failing = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var fetchFrom = pending.Count > 0 ? pending[^1].Id : cursor;
                var rows = await FetchAsync(fetchFrom, ct);
                if (rows.Count > 0 && pending.Count == 0)
                    firstPendingAt = DateTime.UtcNow;
                pending.AddRange(rows);

                var due = pending.Count > 0 &&
                          (DateTime.UtcNow - firstPendingAt >= TimeSpan.FromSeconds(_chat.BatchSeconds) ||
                           pending.Sum(l => l.Text.Length + 1) > DiscordLimit - 100);
                if (due)
                {
                    await PostAsync(pending, ct);
                    cursor = pending[^1].Id;
                    await _state.SetLongAsync(CursorKey, cursor, ct);
                    pending.Clear();
                }
                if (failing)
                    _logger.LogInformation("Crosschat to Discord resumed");
                failing = false;
                backoff = TimeSpan.FromSeconds(_chat.PollSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                if (!failing)
                    _logger.LogWarning("Crosschat to Discord paused: {Error}. Retrying; nothing is lost.", e.Message);
                failing = true;
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
            await Task.Delay(backoff, ct);
        }
    }

    private async Task<long> InitialCursorAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                if (await _state.GetLongAsync(CursorKey, ct) is { } saved)
                    return saved;
                // First run: start at the newest message instead of posting all history.
                var max = await _db.ScalarAsync<long?>("SELECT MAX(id) FROM messages", ct) ?? 0;
                await _state.SetLongAsync(CursorKey, max, ct);
                return max;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Crosschat waiting for the database: {Error}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task<List<Line>> FetchAsync(long after, CancellationToken ct)
    {
        await RefreshMapNamesAsync(ct);
        var lines = new List<Line>();
        await using var connection = await _db.OpenAsync(ct);
        await using var command = Database.Command(connection,
            "SELECT id, created_at, source, server_key, name, tribe, platform, discord_tag, text " +
            "FROM messages WHERE id > @after ORDER BY id LIMIT 500", ("@after", after));
        await using var reader = await command.ExecuteReaderAsync(ct);
        long lastId = after;
        while (await reader.ReadAsync(ct))
        {
            lastId = reader.GetInt64(0);
            if (reader.GetString(2) != "game")
                continue; // Discord rows came from Discord; don't echo them back
            lines.Add(new Line(lastId, FormatLine(
                reader.GetInt64(1), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8))));
        }
        // Discord-only stretches still move the cursor forward (as an empty marker line).
        if (lines.Count == 0 && lastId > after)
            lines.Add(new Line(lastId, ""));
        else if (lines.Count > 0 && lastId > lines[^1].Id)
            lines.Add(new Line(lastId, ""));
        return lines;
    }

    private string FormatLine(long createdAt, string serverKey, string name, string tribe, string platform, string tag, string text)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(createdAt).ToLocalTime()
            .ToString(_chat.TimeFormat, CultureInfo.InvariantCulture);
        var map = _mapNames.TryGetValue(serverKey, out var label) && label.Length > 0 ? label : serverKey;
        var tribePart = tribe.Length > 0 ? _chat.TribeFormat.Replace("{tribe}", Format.Sanitize(tribe)) : "";
        return _chat.LineFormat
            .Replace("{time}", time)
            .Replace("{platform}", Format.Sanitize(platform))
            .Replace("{map}", Format.Sanitize(map))
            .Replace("{tag}", Format.Sanitize(tag))
            .Replace("{name}", Format.Sanitize(name))
            .Replace("{tribe}", tribePart)
            .Replace("{message}", Format.Sanitize(text));
    }

    private async Task RefreshMapNamesAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _mapNamesAt < TimeSpan.FromMinutes(1))
            return;
        await using var connection = await _db.OpenAsync(ct);
        await using var command = Database.Command(connection, "SELECT server_key, display_name FROM servers");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            _mapNames[reader.GetString(0)] = reader.GetString(1);
        _mapNamesAt = DateTime.UtcNow;
    }

    private async Task PostAsync(List<Line> pending, CancellationToken ct)
    {
        var texts = pending.Where(l => l.Text.Length > 0).Select(l => l.Text).ToList();
        if (texts.Count == 0)
            return;
        if (_discord.Client.GetChannel(_chat.ChannelId) is not IMessageChannel channel)
            throw new InvalidOperationException($"crosschat channel {_chat.ChannelId} not found or not a text channel");

        if (texts.Count > _chat.MaxCatchUpLines)
        {
            var skipped = texts.Count - _chat.MaxCatchUpLines;
            texts = texts.Skip(skipped).ToList();
            texts.Insert(0, $"*({skipped} earlier messages from while the bot was offline are not shown)*");
        }

        var batch = new StringBuilder();
        foreach (var text in texts)
        {
            var line = text.Length > DiscordLimit ? text[..(DiscordLimit - 1)] + "…" : text;
            if (batch.Length + line.Length + 1 > DiscordLimit)
            {
                await SendAsync(channel, batch.ToString(), ct);
                batch.Clear();
            }
            if (batch.Length > 0)
                batch.Append('\n');
            batch.Append(line);
        }
        if (batch.Length > 0)
            await SendAsync(channel, batch.ToString(), ct);
    }

    private static Task SendAsync(IMessageChannel channel, string text, CancellationToken ct) =>
        channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None,
            options: new RequestOptions { CancelToken = ct });

    // ---------------------------------------------------------------------------------------------
    // Discord -> game
    // ---------------------------------------------------------------------------------------------

    // Gateway handler: only queue, so a slow database never holds up Discord events.
    private Task OnDiscordMessage(SocketMessage message)
    {
        if (_chat.Enabled && message.Channel.Id == _chat.ChannelId && message.Source == MessageSource.User)
            _toGame.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    private async Task DiscordToGameAsync(CancellationToken ct)
    {
        await foreach (var message in _toGame.Reader.ReadAllAsync(ct))
        {
            var text = message is IUserMessage um
                ? um.Resolve(TagHandling.NameNoPrefix, TagHandling.NameNoPrefix, TagHandling.NameNoPrefix,
                    TagHandling.NameNoPrefix, TagHandling.NameNoPrefix)
                : message.Content;
            text = text.Replace('\n', ' ').Trim();
            if (message.Attachments.Count > 0)
                text = (text + " [attachment]").Trim();
            if (text.Length == 0)
                continue;
            if (text.Length > _chat.MaxLengthToGame)
                text = text[.._chat.MaxLengthToGame];

            var name = message.Author is SocketGuildUser member ? member.DisplayName : message.Author.Username;
            var roles = message.Author is SocketGuildUser m
                ? string.Join(',', m.Roles.Where(r => !r.IsEveryone).Select(r => r.Id))
                : "";

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _db.ExecuteAsync(
                        "INSERT INTO messages (created_at, source, name, discord_roles, text) " +
                        "VALUES (UNIX_TIMESTAMP(), 'discord', @name, @roles, @text)",
                        ct, ("@name", name), ("@roles", roles), ("@text", text));
                    break;
                }
                catch (Exception e) when (!ct.IsCancellationRequested && attempt < 10)
                {
                    if (attempt == 1)
                        _logger.LogWarning("Discord message to game delayed: {Error}. Retrying.", e.Message);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 15)), ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    _logger.LogError("Discord message from {Name} could not be sent to the game: {Error}", name, e.Message);
                    break;
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Purge
    // ---------------------------------------------------------------------------------------------

    private async Task PurgeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-_chat.PurgeAfterDays).ToUnixTimeSeconds();
                int removed, total = 0;
                do
                {
                    removed = await _db.ExecuteAsync("DELETE FROM messages WHERE created_at < @cutoff LIMIT 5000", ct, ("@cutoff", cutoff));
                    total += removed;
                } while (removed == 5000);
                if (total > 0)
                    _logger.LogInformation("Purged {Count} chat messages older than {Days} days", total, _chat.PurgeAfterDays);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("Chat purge skipped: {Error}", e.Message);
            }
            await Task.Delay(TimeSpan.FromHours(6), ct);
        }
    }
}
