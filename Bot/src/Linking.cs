using System.Security.Cryptography;
using Discord;
using Discord.WebSocket;

namespace ServerComms.Bot;

// Discord <-> game account linking.
//
//  1. /link or the panel's Link button: the bot stores a one-time code (link_codes) and shows it only
//     to that member.
//  2. In game the player types /linkDiscord <code>; the plugin checks the code and writes the links
//     row (one Discord account per player, one player per Discord account).
//  3. The bot notices new links rows, gives the verified roles and renames the member to their
//     character ("Name [Tribe]").
//
// Unlink (with confirmation) removes the row and the roles. Leaving the Discord server unlinks.
// Links unseen in game for AutoUnlinkDays are removed, except for members of KeepGroups.
public sealed class Linking : BackgroundService, IInteractionModule
{
    private const string CursorKey = "links.cursor";
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I

    private readonly BotOptions _options;
    private readonly LinkingOptions _link;
    private readonly Database _db;
    private readonly BotState _state;
    private readonly Lookups _lookups;
    private readonly DatabaseStartup _dbStartup;
    private readonly DiscordConnection _discord;
    private readonly ILogger<Linking> _logger;
    private readonly HashSet<(ulong, long)> _handled = [];
    private bool _startupDone;

    public Linking(BotOptions options, Database db, BotState state, Lookups lookups, DatabaseStartup dbStartup,
        DiscordConnection discord, ILogger<Linking> logger)
    {
        _options = options;
        _link = options.Linking;
        _db = db;
        _state = state;
        _lookups = lookups;
        _dbStartup = dbStartup;
        _discord = discord;
        _logger = logger;
        _discord.Client.UserLeft += OnUserLeft;
    }

    // ---------------------------------------------------------------------------------------------
    // Interactions
    // ---------------------------------------------------------------------------------------------

    public IEnumerable<SlashCommandProperties> Commands
    {
        get
        {
            if (!_link.Enabled)
                yield break;
            yield return new SlashCommandBuilder().WithName("link").WithDescription("Link your Discord account to your game character").Build();
            yield return new SlashCommandBuilder().WithName("unlink").WithDescription("Unlink your Discord account from your game character").Build();
        }
    }

    public async Task<bool> HandleSlashAsync(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "link":
                await command.DeferAsync(ephemeral: true);
                await command.FollowupAsync(await StartLinkAsync(command.User.Id), ephemeral: true);
                return true;
            case "unlink":
                await command.DeferAsync(ephemeral: true);
                await AskUnlinkAsync(command);
                return true;
            default:
                return false;
        }
    }

    public async Task<bool> HandleComponentAsync(SocketMessageComponent component)
    {
        switch (component.Data.CustomId)
        {
            case "sc:link":
                await component.DeferAsync(ephemeral: true);
                await component.FollowupAsync(await StartLinkAsync(component.User.Id), ephemeral: true);
                return true;
            case "sc:unlink":
                await component.DeferAsync(ephemeral: true);
                await AskUnlinkAsync(component);
                return true;
            case "sc:unlink:yes":
                await component.DeferAsync(ephemeral: true);
                var done = await UnlinkAsync(component.User.Id, "unlinked by the player");
                await component.ModifyOriginalResponseAsync(m =>
                {
                    m.Content = done ? _link.UnlinkedMessage : _link.NotLinkedMessage;
                    m.Components = new ComponentBuilder().Build();
                });
                return true;
            case "sc:unlink:no":
                await component.UpdateAsync(m => { m.Content = "Cancelled."; m.Components = new ComponentBuilder().Build(); });
                return true;
            default:
                return false;
        }
    }

    public async Task<string> StartLinkAsync(ulong discordId)
    {
        var ct = CancellationToken.None;
        var linked = await _db.ScalarAsync<string>("SELECT player_key FROM links WHERE discord_id = @d", ct, ("@d", discordId));
        await _db.ExecuteAsync("DELETE FROM link_codes WHERE discord_id = @d OR expires_at < UNIX_TIMESTAMP()", ct, ("@d", discordId));

        string code = "";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            code = new string(Enumerable.Range(0, 6).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray());
            try
            {
                await _db.ExecuteAsync("INSERT INTO link_codes (code, discord_id, expires_at) VALUES (@c, @d, UNIX_TIMESTAMP() + @s)",
                    ct, ("@c", code), ("@d", discordId), ("@s", _link.CodeMinutes * 60));
                break;
            }
            catch (MySqlConnector.MySqlException e) when (e.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry) { }
        }
        var text = _link.CodeMessage
            .Replace("{code}", code)
            .Replace("{command}", _link.GameCommand)
            .Replace("{minutes}", _link.CodeMinutes.ToString());
        return linked is null ? text : text + "\n" + _link.CodeReplacesMessage;
    }

    public async Task AskUnlinkAsync(SocketInteraction interaction)
    {
        var playerKey = await _db.ScalarAsync<string>("SELECT player_key FROM links WHERE discord_id = @d",
            CancellationToken.None, ("@d", interaction.User.Id));
        if (playerKey is null)
        {
            await interaction.FollowupAsync(_link.NotLinkedMessage, ephemeral: true);
            return;
        }
        var name = await PlayerNameAsync(playerKey) ?? playerKey;
        var buttons = new ComponentBuilder()
            .WithButton("Yes, unlink", "sc:unlink:yes", ButtonStyle.Danger)
            .WithButton("Cancel", "sc:unlink:no", ButtonStyle.Secondary)
            .Build();
        await interaction.FollowupAsync(_link.UnlinkConfirmMessage.Replace("{name}", name), components: buttons, ephemeral: true);
    }

    // ---------------------------------------------------------------------------------------------
    // Link / unlink effects
    // ---------------------------------------------------------------------------------------------

    /// <summary>Deletes the member's link and takes the verified roles. False if they weren't linked.</summary>
    public async Task<bool> UnlinkAsync(ulong discordId, string reason)
    {
        var removed = await _db.ExecuteAsync("DELETE FROM links WHERE discord_id = @d", CancellationToken.None, ("@d", discordId));
        if (removed == 0)
            return false;
        IGuildUser? member = _discord.Guild?.GetUser(discordId);
        if (member is null && _discord.Guild is { } g && _link.VerifiedRoleIds.Count > 0)
            member = await ((IGuild)g).GetUserAsync(discordId, CacheMode.AllowDownload);
        if (member is not null && _link.VerifiedRoleIds.Count > 0)
        {
            try { await member.RemoveRolesAsync(_link.VerifiedRoleIds); }
            catch (Exception e) { _logger.LogWarning("Couldn't remove roles from {Member}: {Error}", member.Username, e.Message); }
        }
        _logger.LogInformation("Unlinked Discord {Id} ({Reason})", discordId, reason);
        return true;
    }

    private async Task OnLinkedAsync(ulong discordId, string playerKey, string? name, string? tribe)
    {
        var guild = _discord.Guild;
        if (guild is null)
            return;
        // The member cache may still be downloading right after a (re)connect: ask Discord directly.
        IGuildUser? member = guild.GetUser(discordId);
        member ??= await ((IGuild)guild).GetUserAsync(discordId, CacheMode.AllowDownload);
        if (member is null)
        {
            _logger.LogInformation("Linked {Key} to Discord {Id} (member not in the server)", playerKey, discordId);
            return;
        }
        _logger.LogInformation("Linked {Key} ({Name}) to {Member}", playerKey, name ?? "?", member.Username);

        var missing = _link.VerifiedRoleIds.Where(id => !member.RoleIds.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            try { await member.AddRolesAsync(missing); }
            catch (Exception e) { _logger.LogWarning("Couldn't give roles to {Member}: {Error} (is the bot's role above them?)", member.Username, e.Message); }
        }

        if (_link.RenameMembers && name is { Length: > 0 } && member.Id != guild.OwnerId)
        {
            var nickname = Nickname(name, tribe ?? "");
            if (nickname != member.Nickname)
            {
                try { await member.ModifyAsync(p => p.Nickname = nickname); }
                catch (Exception e) { _logger.LogWarning("Couldn't rename {Member}: {Error}", member.Username, e.Message); }
            }
        }
    }

    // Discord nicknames are at most 32 characters: drop the tribe first, then cut the name.
    private string Nickname(string name, string tribe)
    {
        var withTribe = _link.NicknameFormat.Replace("{name}", name)
            .Replace("{tribe}", tribe.Length > 0 ? _link.NicknameTribeFormat.Replace("{tribe}", tribe) : "").Trim();
        if (withTribe.Length <= 32)
            return withTribe;
        var withoutTribe = _link.NicknameFormat.Replace("{name}", name).Replace("{tribe}", "").Trim();
        return withoutTribe.Length <= 32 ? withoutTribe : withoutTribe[..32];
    }

    private async Task<string?> PlayerNameAsync(string playerKey) =>
        await _db.ScalarAsync<string>("SELECT name FROM players_online WHERE player_key = @k LIMIT 1", CancellationToken.None, ("@k", playerKey));

    private async Task OnUserLeft(SocketGuild guild, SocketUser user)
    {
        if (!_link.Enabled || !_link.UnlinkOnLeave || guild.Id != _options.Discord.GuildId)
            return;
        try { await UnlinkAsync(user.Id, "left the Discord server"); }
        catch (Exception e) { _logger.LogWarning("Unlink of {User} after leaving failed: {Error}", user.Username, e.Message); }
    }

    // ---------------------------------------------------------------------------------------------
    // Background: watch for new links, auto-unlink inactive players
    // ---------------------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_link.Enabled)
            return;
        await Task.WhenAll(_dbStartup.Ready, _discord.Ready).WaitAsync(ct);
        var nextPurge = DateTime.UtcNow.AddMinutes(1);
        var failing = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessNewLinksAsync(ct);
                if (DateTime.UtcNow >= nextPurge)
                {
                    await AutoUnlinkAsync(ct);
                    nextPurge = DateTime.UtcNow.AddHours(1);
                }
                failing = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                if (!failing)
                    _logger.LogWarning("Link processing paused: {Error}. Retrying.", e.Message);
                failing = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task ProcessNewLinksAsync(CancellationToken ct)
    {
        var since = await _state.GetLongAsync(CursorKey, ct);
        if (since is null)
        {
            // First run: existing (migrated) links are already set up.
            await _state.SetLongAsync(CursorKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
            return;
        }

        var found = new List<(ulong DiscordId, string Key, long LinkedAt, string? Name, string? Tribe)>();
        await using (var connection = await _db.OpenAsync(ct))
        await using (var command = Database.Command(connection,
            "SELECT l.discord_id, l.player_key, l.linked_at, " +
            "(SELECT p.name FROM players_online p WHERE p.player_key = l.player_key LIMIT 1), " +
            "(SELECT p.tribe_name FROM players_online p WHERE p.player_key = l.player_key LIMIT 1) " +
            "FROM links l WHERE l.linked_at >= @since ORDER BY l.linked_at", ("@since", since.Value)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                found.Add((Convert.ToUInt64(reader.GetValue(0)), reader.GetString(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        if (!_startupDone)
        {
            // After a restart, links in the cursor's own second were already handled last run.
            foreach (var link in found.Where(l => l.LinkedAt == since.Value))
                _handled.Add((link.DiscordId, link.LinkedAt));
            _startupDone = true;
        }
        foreach (var link in found)
        {
            if (!_handled.Add((link.DiscordId, link.LinkedAt)))
                continue;
            await OnLinkedAsync(link.DiscordId, link.Key, link.Name, link.Tribe);
        }
        if (found.Count > 0)
        {
            var newest = found.Max(l => l.LinkedAt);
            await _state.SetLongAsync(CursorKey, newest, ct);   // ">=" keeps same-second links; _handled skips repeats
            _handled.RemoveWhere(h => h.Item2 < newest);
        }
    }

    private async Task AutoUnlinkAsync(CancellationToken ct)
    {
        if (_link.AutoUnlinkDays <= 0)
            return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_link.AutoUnlinkDays).ToUnixTimeSeconds();
        var stale = new List<(ulong DiscordId, string Key)>();
        await using (var connection = await _db.OpenAsync(ct))
        await using (var command = Database.Command(connection,
            "SELECT discord_id, player_key FROM links WHERE last_seen < @cutoff", ("@cutoff", cutoff)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                stale.Add((Convert.ToUInt64(reader.GetValue(0)), reader.GetString(1)));
        }

        foreach (var (discordId, key) in stale)
        {
            if (_link.KeepGroups.Count > 0)
            {
                var groups = await _lookups.GetGroupsAsync(key, ct);
                if (groups is null)
                {
                    _logger.LogWarning("Auto-unlink skipped: Permissions lookup failed (check Lookups:Permissions)");
                    return;   // never unlink a possible Vacation member on a failed lookup
                }
                if (_link.KeepGroups.Any(groups.Contains))
                    continue;
            }
            await UnlinkAsync(discordId, $"not seen in game for {_link.AutoUnlinkDays} days");
        }
    }
}
