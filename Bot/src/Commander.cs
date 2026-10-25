using System.Collections.Concurrent;
using System.Text;
using Discord;
using Discord.WebSocket;

namespace ServerComms.Bot;

// RCON from Discord.
//
//  Commander panel (Rcon:CommanderChannelId): a "New command" button opens a form; the command then
//  appears as a card with a map picker (pick one or several), All Maps, Edit and Delete. Each run
//  fills the card with every map's reply. The card keeps its command, so it can be re-run.
//  /rcon command:<text> server:<map|all>: one-off, replies only to you by default.
//
//  Permissions: Discord administrators and AdminRoleIds may run anything; other roles only the
//  commands listed for them under Rcon:RolePermissions. Destructive commands (Rcon:ConfirmCommands)
//  need a Confirm click. Every run is written to rcon_log and, if set, posted to AdminLogChannelId.
public sealed class Commander(
    BotOptions options, Database db, BotState state, Rcon rcon, DatabaseStartup dbStartup,
    DiscordConnection discord, ILogger<Commander> logger) : BackgroundService, IInteractionModule
{
    private const string PanelKey = "rcon.commander.message";
    private const string NoResponse = "Server received, But no response!!";
    private readonly RconOptions _rcon = options.Rcon;

    // Pending confirmations: short id -> what to run. Kept 2 minutes.
    private sealed record Pending(ulong UserId, string Command, List<string> Targets, ulong? CardMessageId, DateTime At);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    // ---------------------------------------------------------------------------------------------
    // Panel
    // ---------------------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_rcon.CommanderChannelId == 0)
            return;
        await Task.WhenAll(dbStartup.Ready, discord.Ready).WaitAsync(ct);
        try
        {
            if (discord.Client.GetChannel(_rcon.CommanderChannelId) is not ITextChannel channel)
            {
                logger.LogWarning("RCON Commander channel {Channel} not found", _rcon.CommanderChannelId);
                return;
            }
            var embed = new EmbedBuilder()
                .WithTitle("RCON Commander")
                .WithDescription("Press **New command**, type the command, then pick the maps to run it on.\n" +
                                 "Destructive commands ask for confirmation. Every run is logged.")
                .WithColor(Color.Blue)
                .Build();
            var buttons = new ComponentBuilder().WithButton("New command", "sc:rcon:new", ButtonStyle.Primary, new Emoji("⌨️")).Build();

            if (await state.GetAsync(PanelKey, ct) is { } saved && ulong.TryParse(saved, out var id) &&
                await channel.GetMessageAsync(id) is IUserMessage message && message.Author.Id == discord.Client.CurrentUser.Id)
            {
                await message.ModifyAsync(m => { m.Embed = embed; m.Components = buttons; });
                return;
            }
            var posted = await channel.SendMessageAsync(embed: embed, components: buttons);
            await state.SetAsync(PanelKey, posted.Id.ToString(), ct);
            logger.LogInformation("Posted the RCON Commander panel in #{Channel}", channel.Name);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("RCON Commander panel not posted: {Error}", e.Message);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // /rcon
    // ---------------------------------------------------------------------------------------------

    public IEnumerable<SlashCommandProperties> Commands
    {
        get
        {
            yield return new SlashCommandBuilder()
                .WithName("rcon")
                .WithDescription("Run an RCON command on one map or all maps")
                .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
                .AddOption("command", ApplicationCommandOptionType.String, "The command, e.g. ListPlayers", isRequired: true)
                .AddOption("server", ApplicationCommandOptionType.String, "Map, or \"all\"", isRequired: true, isAutocomplete: true)
                .Build();
        }
    }

    public async Task<bool> HandleAutocompleteAsync(SocketAutocompleteInteraction autocomplete)
    {
        if (autocomplete.Data.CommandName != "rcon")
            return false;
        var typed = autocomplete.Data.Current.Value?.ToString() ?? "";
        var results = new List<AutocompleteResult> { new("All online maps", "all") };
        foreach (var (server, online) in await rcon.ServersAsync(CancellationToken.None))
            results.Add(new AutocompleteResult($"{server.Name}{(online ? "" : " (offline)")}", server.Key));
        await autocomplete.RespondAsync(results
            .Where(r => typed.Length == 0 || r.Name.Contains(typed, StringComparison.OrdinalIgnoreCase) ||
                        r.Value.ToString()!.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25));
        return true;
    }

    public async Task<bool> HandleSlashAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "rcon")
            return false;
        await command.DeferAsync(ephemeral: _rcon.EphemeralReplies);
        var text = command.Data.Options.First(o => o.Name == "command").Value?.ToString()?.Trim() ?? "";
        var server = command.Data.Options.First(o => o.Name == "server").Value?.ToString() ?? "";

        if (Denied(command.User, text) is { } why)
        {
            await command.FollowupAsync(why, ephemeral: true);
            return true;
        }
        var targets = await TargetsAsync(server);
        if (targets.Count == 0)
        {
            await command.FollowupAsync(server == "all" ? "No map is online." : $"Unknown map \"{server}\".", ephemeral: true);
            return true;
        }
        if (NeedsConfirm(text))
        {
            await AskConfirmAsync(command, text, targets, null);
            return true;
        }
        var results = await RunAsync(command.User, text, targets);
        await ReplyWithResultsAsync(command, text, results, _rcon.EphemeralReplies);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Commander cards
    // ---------------------------------------------------------------------------------------------

    public async Task<bool> HandleComponentAsync(SocketMessageComponent component)
    {
        var id = component.Data.CustomId;
        if (!id.StartsWith("sc:rcon:", StringComparison.Ordinal))
            return false;

        if (id == "sc:rcon:new" || id == "sc:rcon:edit")
        {
            if (Denied(component.User, null) is { } why)
            {
                await component.RespondAsync(why, ephemeral: true);
                return true;
            }
            var current = id == "sc:rcon:edit" ? CardCommand(component.Message) : "";
            var modal = new ModalBuilder()
                .WithTitle("RCON command")
                .WithCustomId(id == "sc:rcon:edit" ? $"sc:rcon:modal:{component.Message.Id}" : "sc:rcon:modal:new")
                .AddTextInput("Command", "command", TextInputStyle.Paragraph, "e.g. ListPlayers", required: true, value: current);
            await component.RespondWithModalAsync(modal.Build());
            return true;
        }

        if (id == "sc:rcon:delete")
        {
            if (Denied(component.User, null) is { } why)
            {
                await component.RespondAsync(why, ephemeral: true);
                return true;
            }
            await component.DeferAsync();
            await component.Message.DeleteAsync();
            return true;
        }

        if (id == "sc:rcon:all" || id == "sc:rcon:maps")
        {
            var command = CardCommand(component.Message);
            if (Denied(component.User, command) is { } why)
            {
                await component.RespondAsync(why, ephemeral: true);
                return true;
            }
            var targets = id == "sc:rcon:all" ? await TargetsAsync("all") : component.Data.Values.ToList();
            if (targets.Count == 0)
            {
                await component.RespondAsync("No map is online.", ephemeral: true);
                return true;
            }
            if (NeedsConfirm(command))
            {
                await component.DeferAsync(ephemeral: true);
                await AskConfirmAsync(component, command, targets, component.Message.Id);
                // Clear the picker: Discord only reports a selection when it changes, so a kept
                // selection couldn't be picked again after Cancel.
                var fresh = await CardComponentsAsync();
                await component.Message.ModifyAsync(m => m.Components = fresh);
                return true;
            }
            await component.DeferAsync();
            var results = await RunAsync(component.User, command, targets);
            await UpdateCardAsync(component.Message, command, results, component.User);
            await SendFullOutputIfLongAsync(component, command, results);
            return true;
        }

        if (id.StartsWith("sc:rcon:confirm:", StringComparison.Ordinal) || id.StartsWith("sc:rcon:cancel:", StringComparison.Ordinal))
        {
            var key = id[(id.LastIndexOf(':') + 1)..];
            if (!_pending.TryRemove(key, out var pending) || DateTime.UtcNow - pending.At > TimeSpan.FromMinutes(2) ||
                pending.UserId != component.User.Id)
            {
                await component.UpdateAsync(m => { m.Content = "This confirmation expired."; m.Components = new ComponentBuilder().Build(); });
                return true;
            }
            if (id.StartsWith("sc:rcon:cancel:", StringComparison.Ordinal))
            {
                await component.UpdateAsync(m => { m.Content = "Cancelled."; m.Components = new ComponentBuilder().Build(); });
                return true;
            }
            await component.UpdateAsync(m => { m.Content = $"Running `{pending.Command}` on {pending.Targets.Count} map(s)…"; m.Components = new ComponentBuilder().Build(); });
            var results = await RunAsync(component.User, pending.Command, pending.Targets);
            if (pending.CardMessageId is { } cardId && component.Channel is ITextChannel channel &&
                await channel.GetMessageAsync(cardId) is IUserMessage card)
            {
                await UpdateCardAsync(card, pending.Command, results, component.User);
                await component.ModifyOriginalResponseAsync(m => m.Content = "Done: see the card above.");
                await SendFullOutputIfLongAsync(component, pending.Command, results);
            }
            else
                await ReplyWithResultsAsync(component, pending.Command, results, true);
            return true;
        }
        return false;
    }

    public async Task<bool> HandleModalAsync(SocketModal modal)
    {
        var id = modal.Data.CustomId;
        if (!id.StartsWith("sc:rcon:modal:", StringComparison.Ordinal))
            return false;
        var command = modal.Data.Components.First(c => c.CustomId == "command").Value.Trim();
        if (Denied(modal.User, command) is { } why)
        {
            await modal.RespondAsync(why, ephemeral: true);
            return true;
        }

        var embed = CardEmbed(command, [], null);
        var components = await CardComponentsAsync();
        if (id == "sc:rcon:modal:new")
        {
            await modal.RespondAsync(embed: embed, components: components);
            return true;
        }
        // Edit: replace the card's command and clear old results.
        await modal.DeferAsync();
        if (ulong.TryParse(id["sc:rcon:modal:".Length..], out var messageId) && modal.Channel is ITextChannel channel &&
            await channel.GetMessageAsync(messageId) is IUserMessage card)
            await card.ModifyAsync(m => { m.Embed = embed; m.Components = components; });
        return true;
    }

    // ---------------------------------------------------------------------------------------------

    private sealed record Outcome(string ServerKey, string Name, Rcon.Result Result);

    private async Task<List<Outcome>> RunAsync(SocketUser user, string command, List<string> targets)
    {
        var names = (await rcon.ServersAsync(CancellationToken.None)).ToDictionary(s => s.Server.Key, s => s.Server.Name, StringComparer.OrdinalIgnoreCase);
        var results = await Task.WhenAll(targets.Select(async key =>
            new Outcome(key, names.GetValueOrDefault(key, key), await rcon.SendAsync(key, command, CancellationToken.None))));

        foreach (var o in results)
        {
            var text = o.Result.Ok ? (o.Result.NoOutput ? "(sent, no output)" : o.Result.Text) : "FAILED: " + o.Result.Error;
            try
            {
                await db.ExecuteAsync("INSERT INTO rcon_log (at, discord_id, server_key, command, result) VALUES (UNIX_TIMESTAMP(), @u, @s, @c, @r)",
                    CancellationToken.None, ("@u", user.Id), ("@s", o.ServerKey), ("@c", command), ("@r", text.Length > 4000 ? text[..4000] : text));
            }
            catch (Exception e) { logger.LogWarning("rcon_log write failed: {Error}", e.Message); }
        }
        var summary = string.Join(", ", results.Select(o => $"{o.Name} {(o.Result.Ok ? "ok" : "failed")}"));
        logger.LogInformation("RCON by {User}: {Command} -> {Summary}", user.Username, command, summary);
        if (options.AdminLogChannelId != 0 && discord.Client.GetChannel(options.AdminLogChannelId) is IMessageChannel log)
        {
            try { await log.SendMessageAsync($"**RCON** {user.Mention} ran `{Format.Sanitize(command)}`: {summary}", allowedMentions: AllowedMentions.None); }
            catch { /* logged above */ }
        }
        return results.ToList();
    }

    private async Task<List<string>> TargetsAsync(string server)
    {
        var servers = await rcon.ServersAsync(CancellationToken.None);
        if (server.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var hidden = new HashSet<string>(options.Status.HideServers, StringComparer.OrdinalIgnoreCase);
            return servers.Where(s => s.Online && !hidden.Contains(s.Server.Key)).Select(s => s.Server.Key).ToList();
        }
        var match = servers.FirstOrDefault(s => s.Server.Key.Equals(server, StringComparison.OrdinalIgnoreCase) ||
                                                s.Server.Name.Equals(server, StringComparison.OrdinalIgnoreCase));
        return match.Server is null ? new List<string>() : new List<string> { match.Server.Key };
    }

    private string? Denied(SocketUser user, string? command)
    {
        if (user is not SocketGuildUser member)
            return "Use this in the server.";
        if (member.GuildPermissions.Administrator || member.Roles.Any(r => options.AdminRoleIds.Contains(r.Id)))
            return null;
        var allowed = member.Roles
            .SelectMany(r => _rcon.RolePermissions.TryGetValue(r.Id.ToString(), out var list) ? list : [])
            .ToList();
        if (allowed.Count == 0)
            return "You don't have permission to use RCON.";
        if (command is null || allowed.Contains("*"))
            return null;
        var verb = command.Split(' ', 2)[0];
        return allowed.Any(a => a.Equals(verb, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"Your role can't run `{verb}`.";
    }

    private bool NeedsConfirm(string command)
    {
        var verb = command.Split(' ', 2)[0];
        return _rcon.EffectiveConfirmCommands.Any(c => c.Equals(verb, StringComparison.OrdinalIgnoreCase));
    }

    private async Task AskConfirmAsync(SocketInteraction interaction, string command, List<string> targets, ulong? cardId)
    {
        var key = Guid.NewGuid().ToString("N")[..10];
        _pending[key] = new Pending(interaction.User.Id, command, targets, cardId, DateTime.UtcNow);
        foreach (var (k, p) in _pending)
            if (DateTime.UtcNow - p.At > TimeSpan.FromMinutes(5)) _pending.TryRemove(k, out _);
        var names = (await rcon.ServersAsync(CancellationToken.None)).ToDictionary(s => s.Server.Key, s => s.Server.Name, StringComparer.OrdinalIgnoreCase);
        var buttons = new ComponentBuilder()
            .WithButton("Confirm", $"sc:rcon:confirm:{key}", ButtonStyle.Danger)
            .WithButton("Cancel", $"sc:rcon:cancel:{key}", ButtonStyle.Secondary)
            .Build();
        await interaction.FollowupAsync(
            $"⚠️ `{Format.Sanitize(command)}` on **{string.Join(", ", targets.Select(t => names.GetValueOrDefault(t, t)))}**. Are you sure?",
            components: buttons, ephemeral: true);
    }

    private static string CardCommand(IMessage message)
    {
        var description = message.Embeds.FirstOrDefault()?.Description ?? "";
        return description.Replace("```", "").Trim();
    }

    private static Embed CardEmbed(string command, List<Outcome> results, SocketUser? by)
    {
        var builder = new EmbedBuilder()
            .WithTitle("RCON command")
            .WithDescription("```\n" + command.Replace("```", "'''") + "\n```")
            .WithColor(results.Count == 0 ? Color.Blue : results.All(r => r.Result.Ok) ? Color.Green : Color.Orange);
        foreach (var o in results.Take(25))
            builder.AddField(o.Name, ResultText(o, 1000), inline: false);
        if (by is not null)
            builder.WithFooter($"Last run by {(by as SocketGuildUser)?.DisplayName ?? by.Username}").WithCurrentTimestamp();
        return builder.Build();
    }

    private static string ResultText(Outcome o, int max)
    {
        if (!o.Result.Ok) return "❌ " + (o.Result.Error ?? "failed");
        if (o.Result.NoOutput) return "✅ Sent (no output from the game)";
        var text = o.Result.Text.Replace("```", "'''");
        if (text.Length > max - 8) text = text[..(max - 30)] + "\n… (cut, full output sent)";
        return "```\n" + text + "\n```";
    }

    private async Task<MessageComponent> CardComponentsAsync()
    {
        var servers = await rcon.ServersAsync(CancellationToken.None);
        var builder = new ComponentBuilder();
        if (servers.Count > 0)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId("sc:rcon:maps")
                .WithPlaceholder("Pick map(s) to run on")
                .WithMinValues(1)
                .WithMaxValues(Math.Min(25, servers.Count));
            foreach (var (server, online) in servers.Take(25))
                menu.AddOption(server.Name, server.Key, online ? "Online" : "Offline");
            builder.WithSelectMenu(menu, 0);
        }
        builder.WithButton("All Maps", "sc:rcon:all", ButtonStyle.Primary, row: 1)
               .WithButton("Edit", "sc:rcon:edit", ButtonStyle.Secondary, row: 1)
               .WithButton("Delete", "sc:rcon:delete", ButtonStyle.Danger, row: 1);
        return builder.Build();
    }

    private async Task UpdateCardAsync(IUserMessage card, string command, List<Outcome> results, SocketUser by)
    {
        var embed = CardEmbed(command, results, by);
        var components = await CardComponentsAsync();   // refreshes the map list too
        await card.ModifyAsync(m => { m.Embed = embed; m.Components = components; });
    }

    private async Task ReplyWithResultsAsync(SocketInteraction interaction, string command, List<Outcome> results, bool ephemeral)
    {
        var embed = CardEmbed(command, results, interaction.User);
        if (results.Any(o => o.Result.Ok && !o.Result.NoOutput && o.Result.Text.Length > 990))
        {
            using var stream = new MemoryStream(FullOutput(command, results));
            await interaction.FollowupWithFileAsync(stream, "rcon-output.txt", embed: embed, ephemeral: ephemeral);
        }
        else
            await interaction.FollowupAsync(embed: embed, ephemeral: ephemeral);
    }

    private static async Task SendFullOutputIfLongAsync(SocketInteraction interaction, string command, List<Outcome> results)
    {
        if (!results.Any(o => o.Result.Ok && !o.Result.NoOutput && o.Result.Text.Length > 990))
            return;
        using var stream = new MemoryStream(FullOutput(command, results));
        await interaction.FollowupWithFileAsync(stream, "rcon-output.txt", "Full output:", ephemeral: true);
    }

    private static byte[] FullOutput(string command, List<Outcome> results)
    {
        var sb = new StringBuilder().AppendLine("> " + command).AppendLine();
        foreach (var o in results)
        {
            sb.AppendLine("=== " + o.Name + " ===");
            sb.AppendLine(o.Result.Ok ? (o.Result.NoOutput ? "(sent, no output)" : o.Result.Text) : "FAILED: " + o.Result.Error);
            sb.AppendLine();
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
