using Discord;
using Discord.WebSocket;

namespace ServerComms.Bot;

// Handles clicks on panel buttons ("sc:btn:<id>"). Every reply is shown only to the member who clicked.
//
// Actions: link / unlink (Linking), ids (from the link), points (ArkShop), tokens (TokenBank),
// location (online map, else PlayerLogin's last map), rcon (a command on "any" online map, the
// "player"'s map, or a named map). Checks run in this order: roles, linked, online, cooldowns.
public sealed class Buttons(
    BotOptions options, IConfiguration configuration, Database db, Lookups lookups, Rcon rcon,
    Linking linking, ILogger<Buttons> logger) : IInteractionModule
{
    public IEnumerable<SlashCommandProperties> Commands => [];
    public Task<bool> HandleSlashAsync(SocketSlashCommand command) => Task.FromResult(false);

    private sealed record Link(string PlayerKey, long PlayerId, long TribeId);
    private sealed record Online(string ServerKey, string Name, string Tribe);

    public async Task<bool> HandleComponentAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        if (!customId.StartsWith("sc:btn:", StringComparison.Ordinal))
            return false;
        var id = customId["sc:btn:".Length..];
        var settings = PanelSettings.Load(configuration);
        if (!settings.Buttons.TryGetValue(id, out var button))
        {
            await component.RespondAsync("This button is no longer configured.", ephemeral: true);
            return true;
        }
        await component.DeferAsync(ephemeral: true);
        string? reply;
        try
        {
            reply = await RunAsync(component, id, button, CancellationToken.None);
        }
        catch (MySqlConnector.MySqlException e)
        {
            // A missing grant, wrong database name or MariaDB outage: one readable log line, a clear reply.
            logger.LogWarning("Button {Button}: database lookup failed: {Error}", id, e.Message);
            reply = "That lookup isn't available right now. Please try again later.";
        }
        if (reply is not null)
            await component.FollowupAsync(reply.Length > 1900 ? reply[..1900] + "…" : reply, ephemeral: true, allowedMentions: AllowedMentions.None);
        return true;
    }

    private async Task<string?> RunAsync(SocketMessageComponent component, string id, ButtonConfig button, CancellationToken ct)
    {
        var user = component.User;
        var action = button.Action.ToLowerInvariant();

        if (action == "link")
            return await linking.StartLinkAsync(user.Id);
        if (action == "unlink")
        {
            await linking.AskUnlinkAsync(component);
            return null;
        }

        if (button.RequireRoles.Count > 0 &&
            (user is not SocketGuildUser member || !member.Roles.Any(r => button.RequireRoles.Contains(r.Id))))
            return "You don't have the role needed for this button.";

        var link = await GetLinkAsync(user.Id, ct);
        if (link is null && (button.RequireLinked ?? true))
            return options.Linking.NotLinkedMessage;

        var online = link is null ? null : await GetOnlineAsync(link.PlayerKey, ct);
        if (button.RequireOnline && online is null)
            return "You need to be online in game for this.";

        // Per-member cooldown.
        if (button.CooldownMinutes > 0 && await CooldownLeftAsync(user.Id, id, ct) is { } left)
            return $"You can use this again in {Duration(left)}.";

        var values = new Dictionary<string, string>
        {
            ["player_key"] = link?.PlayerKey ?? "",
            ["player_id"] = link?.PlayerId.ToString() ?? "",
            ["tribe_id"] = link?.TribeId.ToString() ?? "",
            ["name"] = online?.Name ?? "",
            ["map"] = online is null ? "" : await MapNameAsync(online.ServerKey, ct),
        };

        string reply;
        switch (action)
        {
            case "ids":
                reply = Fill(button.Reply.Length > 0 ? button.Reply
                    : (options.Game == "ASA" ? "Your EOS ID: `{player_key}`" : "Your Steam ID: `{player_key}`") +
                      "\nPlayer ID: `{player_id}`\nTribe ID: `{tribe_id}`", values);
                break;

            case "points" when !lookups.HasArkShop:
            case "tokens" when !lookups.HasTokenBank:
                logger.LogWarning("Button {Button}: action {Action} needs Lookups:{Lookup}:Database, or use an rcon button instead",
                    id, action, action == "points" ? "ArkShop" : "TokenBank");
                return "This button isn't set up yet.";

            case "points":
                values["points"] = (await lookups.GetPointsAsync(link!.PlayerKey, ct)).ToString("N0");
                reply = Fill(button.Reply.Length > 0 ? button.Reply : "Your point balance: **{points}**", values);
                break;

            case "tokens":
                values["tokens"] = (await lookups.GetTokensAsync(link!.PlayerKey, button.TokenType, ct)).ToString("N0");
                reply = Fill(button.Reply.Length > 0 ? button.Reply : "Your token balance: **{tokens}**", values);
                break;

            case "location":
                if (online is not null)
                    reply = Fill(button.Reply.Length > 0 ? button.Reply : "You're online on **{map}** right now.", values);
                else if (await lookups.GetLocationAsync(link!.PlayerKey, ct) is { } last)
                {
                    values["map"] = await MapNameAsync(last.ServerKey, ct);
                    values["ago"] = Ago(last.LastSeen);
                    reply = Fill("You were last on **{map}**, {ago}.", values);
                }
                else
                    reply = lookups.HasPlayerLogin ? "There's no record of you on any map yet." : "You're not online in game right now.";
                break;

            case "rcon":
            {
                string target;
                if (button.Target.Equals("player", StringComparison.OrdinalIgnoreCase))
                {
                    if (online is null)
                        return "You need to be online in game for this.";
                    target = online.ServerKey;
                    if (button.OnlyOnMaps.Count > 0 && !button.OnlyOnMaps.Contains(target, StringComparer.OrdinalIgnoreCase))
                    {
                        var names = new List<string>();
                        foreach (var key in button.OnlyOnMaps)
                            names.Add(await MapNameAsync(key, ct));
                        return $"This only works while you're on {string.Join(" or ", names)}.";
                    }
                }
                else
                    target = button.Target.Equals("any", StringComparison.OrdinalIgnoreCase) ? "" : button.Target;

                var sharedKey = target.Length > 0 ? $"{id}@{target}" : id;
                if (button.SharedCooldownMinutes > 0 && await CooldownLeftAsync(0, sharedKey, ct) is { } sharedLeft)
                    return $"That was done recently for everyone{(target.Length > 0 ? $" on {await MapNameAsync(target, ct)}" : "")}; " +
                           $"available again in {Duration(sharedLeft)}.";

                var command = Fill(button.Command, values);
                var result = target.Length > 0 ? await rcon.SendAsync(target, command, ct) : await rcon.SendAnyAsync(command, ct);
                logger.LogInformation("Button {Button} by {User}: {Command} on {Server} -> {Outcome}", id, user.Username, command,
                    result.ServerKey, result.Ok ? (result.NoOutput ? "sent" : "ok") : "failed: " + result.Error);
                if (!result.Ok)
                    return $"The server isn't responding right now ({result.Error}). Please try again shortly.";

                values["response"] = result.Text;
                if (result.ServerKey.Length > 0)
                    values["map"] = await MapNameAsync(result.ServerKey, ct);
                reply = result.NoOutput
                    ? Fill(button.SuccessReply.Length > 0 ? button.SuccessReply : "Done.", values)
                    : Fill(button.Reply.Length > 0 ? button.Reply : "{response}", values);
                if (button.SharedCooldownMinutes > 0)
                    await SetCooldownAsync(0, sharedKey, button.SharedCooldownMinutes, ct);
                break;
            }

            default:
                return "This button isn't set up correctly.";
        }

        if (button.CooldownMinutes > 0)
            await SetCooldownAsync(user.Id, id, button.CooldownMinutes, ct);
        return reply;
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<Link?> GetLinkAsync(ulong discordId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = Database.Command(connection,
            "SELECT player_key, player_id, tribe_id FROM links WHERE discord_id = @d", ("@d", discordId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new Link(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)) : null;
    }

    private async Task<Online?> GetOnlineAsync(string playerKey, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - options.Status.OfflineAfterSeconds;
        await using var connection = await db.OpenAsync(ct);
        await using var command = Database.Command(connection,
            "SELECT p.server_key, p.name, p.tribe_name FROM players_online p JOIN servers s ON s.server_key = p.server_key " +
            "WHERE p.player_key = @k AND s.last_heartbeat >= @cutoff LIMIT 1", ("@k", playerKey), ("@cutoff", cutoff));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new Online(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private async Task<string> MapNameAsync(string serverKey, CancellationToken ct) =>
        await db.ScalarAsync<string>("SELECT display_name FROM servers WHERE server_key = @k", ct, ("@k", serverKey)) is { Length: > 0 } name
            ? name : serverKey;

    private async Task<long?> CooldownLeftAsync(ulong discordId, string button, CancellationToken ct)
    {
        var until = await db.ScalarAsync<long?>("SELECT until FROM button_cooldowns WHERE discord_id = @d AND button = @b",
            ct, ("@d", discordId), ("@b", button));
        var left = (until ?? 0) - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return left > 0 ? left : null;
    }

    private Task SetCooldownAsync(ulong discordId, string button, int minutes, CancellationToken ct) =>
        db.ExecuteAsync(
            "INSERT INTO button_cooldowns (discord_id, button, until) VALUES (@d, @b, UNIX_TIMESTAMP() + @s) " +
            "ON DUPLICATE KEY UPDATE until = VALUES(until)",
            ct, ("@d", discordId), ("@b", button.Length > 64 ? button[..64] : button), ("@s", minutes * 60));

    private static string Fill(string template, Dictionary<string, string> values)
    {
        foreach (var (key, value) in values)
            template = template.Replace("{" + key + "}", value);
        return template;
    }

    private static string Duration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";
    }

    private static string Ago(long unixSeconds) => $"<t:{unixSeconds}:R>";   // Discord shows "3 hours ago" in the reader's own time
}
