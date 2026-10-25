using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;

namespace ServerComms.Bot;

// Posts and maintains the configured panels (embed + buttons), and the /servercomms admin command.
//
// Sync (on start and on /servercomms reload), per panel:
//  - not posted yet: adopt AdoptMessageId if it's this bot's message, else post it;
//  - posted and unchanged (same content hash): leave it alone, so restarts don't edit anything;
//  - posted but the config changed: edit the message in place (same ID, links keep working);
//  - message deleted, or the panel moved channel: post it again.
// Url buttons may use {panel:Name}, resolved to that panel's current message link.
// Afterwards every Discord message link on the panels is checked and broken ones are reported.
public sealed partial class Panels(
    BotOptions options, IConfiguration configuration, Database db, DatabaseStartup dbStartup,
    DiscordConnection discord, ILogger<Panels> logger) : BackgroundService, IInteractionModule
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    [GeneratedRegex(@"\{panel:([^}]+)\}")]
    private static partial Regex PanelRef();

    [GeneratedRegex(@"discord(?:app)?\.com/channels/(\d+)/(\d+)(?:/(\d+))?")]
    private static partial Regex MessageLink();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.WhenAll(dbStartup.Ready, discord.Ready).WaitAsync(ct);
        var report = await SyncAsync(null, ct);
        foreach (var line in report)
            logger.LogInformation("Panels: {Line}", line);
    }

    // ---------------------------------------------------------------------------------------------
    // /servercomms
    // ---------------------------------------------------------------------------------------------

    public IEnumerable<SlashCommandProperties> Commands
    {
        get
        {
            var names = PanelSettings.Load(configuration).Panels.Select(p => p.Name).Where(n => n.Length > 0).Take(25).ToList();
            var panelOption = new SlashCommandOptionBuilder().WithName("panel").WithDescription("Which panel")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true);
            foreach (var name in names)
                panelOption.AddChoice(name, name);
            yield return new SlashCommandBuilder()
                .WithName("servercomms")
                .WithDescription("ServerComms admin")
                .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
                .AddOption(new SlashCommandOptionBuilder().WithName("reload")
                    .WithDescription("Re-read panels and buttons from appsettings.json and update the panels")
                    .WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder().WithName("repost")
                    .WithDescription("Delete a panel's message and post it again (moves it to the bottom)")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(panelOption))
                .Build();
        }
    }

    public async Task<bool> HandleSlashAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "servercomms")
            return false;
        await command.DeferAsync(ephemeral: true);
        if (!IsAdmin(command.User))
        {
            await command.FollowupAsync("You don't have permission to use this.", ephemeral: true);
            return true;
        }
        var sub = command.Data.Options.First();
        var report = sub.Name switch
        {
            "reload" => await SyncAsync(null, CancellationToken.None),
            "repost" => await SyncAsync(sub.Options.First().Value?.ToString(), CancellationToken.None),
            _ => new List<string> { "Unknown subcommand." },
        };
        var text = string.Join('\n', report);
        await command.FollowupAsync(text.Length > 1900 ? text[..1900] + "…" : text, ephemeral: true);
        return true;
    }

    public Task<bool> HandleComponentAsync(SocketMessageComponent component) => Task.FromResult(false);

    public bool IsAdmin(SocketUser user) =>
        user is SocketGuildUser member &&
        (member.GuildPermissions.Administrator || member.Roles.Any(r => options.AdminRoleIds.Contains(r.Id)));

    // ---------------------------------------------------------------------------------------------
    // Sync
    // ---------------------------------------------------------------------------------------------

    private sealed record Stored(ulong ChannelId, ulong MessageId, string Hash);

    /// <summary>Brings every panel up to date. With repost, that panel is deleted and posted fresh.</summary>
    public async Task<List<string>> SyncAsync(string? repost, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var settings = PanelSettings.Load(configuration);
            var problems = settings.Validate();
            if (problems.Count > 0)
                return ["Panels not updated, fix appsettings.json:", .. problems.Select(p => "- " + p)];
            if (repost is not null && settings.Panels.All(p => !p.Name.Equals(repost, StringComparison.OrdinalIgnoreCase)))
                return [$"No panel named \"{repost}\"."];

            var report = new List<string>();
            var stored = await LoadStoredAsync(ct);
            // Two passes: a panel linking to another ({panel:Name}) picks up that panel's new message
            // link if the other panel was (re)posted in the first pass.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var panel in settings.Panels)
                {
                    var forceRepost = pass == 0 && repost is not null && panel.Name.Equals(repost, StringComparison.OrdinalIgnoreCase);
                    var line = await SyncPanelAsync(panel, settings, stored, forceRepost, ct);
                    if (line is not null)
                        report.Add(line);
                }
            }
            report.AddRange(await CheckLinksAsync(settings));
            if (report.Count == 0)
                report.Add("All panels are up to date.");
            return report;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<Dictionary<string, Stored>> LoadStoredAsync(CancellationToken ct)
    {
        var stored = new Dictionary<string, Stored>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await db.OpenAsync(ct);
        await using var command = Database.Command(connection, "SELECT name, channel_id, message_id, content_hash FROM panels");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            stored[reader.GetString(0)] = new Stored(Convert.ToUInt64(reader.GetValue(1)), Convert.ToUInt64(reader.GetValue(2)), reader.GetString(3));
        return stored;
    }

    private async Task<string?> SyncPanelAsync(PanelConfig panel, PanelSettings settings, Dictionary<string, Stored> stored,
        bool forceRepost, CancellationToken ct)
    {
        if (discord.Client.GetChannel(panel.ChannelId) is not ITextChannel channel)
            return $"{panel.Name}: channel {panel.ChannelId} not found or not a text channel";

        var (content, embed, components) = Build(panel, settings, stored);
        var hash = Hash(content, embed, components);
        stored.TryGetValue(panel.Name, out var current);

        IUserMessage? message = null;
        if (current is not null && current.ChannelId == panel.ChannelId)
            message = await channel.GetMessageAsync(current.MessageId) as IUserMessage;
        else if (current is null && panel.AdoptMessageId != 0)
        {
            message = await channel.GetMessageAsync(panel.AdoptMessageId) as IUserMessage;
            if (message is not null && message.Author.Id != discord.Client.CurrentUser.Id)
            {
                logger.LogWarning("Panel {Panel}: message {Id} wasn't posted by this bot, so it can't be adopted; posting a new one",
                    panel.Name, panel.AdoptMessageId);
                message = null;
            }
            else if (message is not null)
                current = new Stored(panel.ChannelId, message.Id, "");   // adopted: force the edit below
        }
        if (message is not null && message.Author.Id != discord.Client.CurrentUser.Id)
            message = null;

        if (forceRepost && message is not null)
        {
            try { await message.DeleteAsync(); } catch { /* already gone */ }
            message = null;
        }

        string? result;
        if (message is null)
        {
            if (current is not null && current.ChannelId != panel.ChannelId &&
                discord.Client.GetChannel(current.ChannelId) is ITextChannel old &&
                await old.GetMessageAsync(current.MessageId) is IUserMessage oldMessage &&
                oldMessage.Author.Id == discord.Client.CurrentUser.Id)
            {
                try { await oldMessage.DeleteAsync(); } catch { /* ignore */ }
            }
            message = await channel.SendMessageAsync(content, embed: embed, components: components, allowedMentions: AllowedMentions.None);
            result = $"{panel.Name}: posted in #{channel.Name}";
        }
        else if (current?.Hash != hash)
        {
            await message.ModifyAsync(m => { m.Content = content; m.Embed = embed; m.Components = components; });
            result = panel.AdoptMessageId == message.Id && current?.Hash == "" ? $"{panel.Name}: took over message {message.Id}" : $"{panel.Name}: updated";
        }
        else
            return null;

        await db.ExecuteAsync(
            "INSERT INTO panels (name, channel_id, message_id, content_hash) VALUES (@n, @c, @m, @h) " +
            "ON DUPLICATE KEY UPDATE channel_id = VALUES(channel_id), message_id = VALUES(message_id), content_hash = VALUES(content_hash)",
            ct, ("@n", panel.Name), ("@c", panel.ChannelId), ("@m", message.Id), ("@h", hash));
        stored[panel.Name] = new Stored(panel.ChannelId, message.Id, hash);
        return result;
    }

    private (string Content, Embed? Embed, MessageComponent Components) Build(PanelConfig panel, PanelSettings settings,
        Dictionary<string, Stored> stored)
    {
        Embed? embed = null;
        if (panel.Embed is { } e)
        {
            var builder = new EmbedBuilder();
            if (e.Title.Length > 0) builder.WithTitle(e.Title);
            if (e.Url.Length > 0) builder.WithUrl(e.Url);
            if (e.Description.Length > 0) builder.WithDescription(e.Description);
            if (e.Color.Length > 0) builder.WithColor(ParseColor(e.Color));
            if (e.Thumbnail.Length > 0) builder.WithThumbnailUrl(e.Thumbnail);
            if (e.Image.Length > 0) builder.WithImageUrl(e.Image);
            if (e.Footer.Length > 0) builder.WithFooter(e.Footer, e.FooterIcon.Length > 0 ? e.FooterIcon : null);
            foreach (var f in e.Fields.Take(25))
                builder.AddField(f.Name.Length > 0 ? f.Name : "​", f.Value.Length > 0 ? f.Value : "​", f.Inline);
            embed = builder.Build();
        }

        var components = new ComponentBuilder();
        int row = 0, inRow = 0;
        foreach (var id in panel.Buttons)
        {
            if (id == "-") { if (inRow > 0) { row++; inRow = 0; } continue; }
            if (inRow == 5) { row++; inRow = 0; }
            var b = settings.Buttons[id];
            var button = new ButtonBuilder().WithLabel(b.Label.Length > 0 ? b.Label : null!);
            if (b.Emoji.Length > 0)
                button.WithEmote(Emote.TryParse(b.Emoji, out var emote) ? emote : new Emoji(b.Emoji));
            if (b.Action.Equals("url", StringComparison.OrdinalIgnoreCase))
                button.WithStyle(ButtonStyle.Link).WithUrl(ResolveUrl(b.Url, stored));
            else
                button.WithStyle(ParseStyle(b.Style)).WithCustomId("sc:btn:" + id);
            components.WithButton(button, row);
            inRow++;
        }
        return (panel.Content, embed, components.Build());
    }

    private string ResolveUrl(string url, Dictionary<string, Stored> stored) =>
        PanelRef().Replace(url, m => stored.TryGetValue(m.Groups[1].Value, out var s)
            ? $"https://discord.com/channels/{options.Discord.GuildId}/{s.ChannelId}/{s.MessageId}"
            : $"https://discord.com/channels/{options.Discord.GuildId}");

    private static string Hash(string content, Embed? embed, MessageComponent components)
    {
        var json = JsonSerializer.Serialize(new
        {
            content,
            embed = embed is null ? null : new
            {
                embed.Title, embed.Url, embed.Description, Color = embed.Color?.RawValue, Thumbnail = embed.Thumbnail?.Url,
                Image = embed.Image?.Url, Footer = embed.Footer?.Text, FooterIcon = embed.Footer?.IconUrl,
                Fields = embed.Fields.Select(f => new { f.Name, f.Value, f.Inline }),
            },
            buttons = components.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>()
                .Select(b => new { b.Label, b.CustomId, b.Url, Style = b.Style.ToString(), Emote = b.Emote?.ToString() }),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32];
    }

    private async Task<List<string>> CheckLinksAsync(PanelSettings settings)
    {
        var broken = new List<string>();
        var guild = discord.Guild;
        foreach (var panel in settings.Panels)
            foreach (var id in panel.Buttons.Where(settings.Buttons.ContainsKey))
            {
                var b = settings.Buttons[id];
                if (!b.Action.Equals("url", StringComparison.OrdinalIgnoreCase) || PanelRef().IsMatch(b.Url))
                    continue;
                var m = MessageLink().Match(b.Url);
                if (!m.Success)
                    continue;
                var ok = false;
                if (ulong.TryParse(m.Groups[2].Value, out var channelId) && guild?.GetChannel(channelId) is ITextChannel channel)
                {
                    ok = !m.Groups[3].Success ||
                         (ulong.TryParse(m.Groups[3].Value, out var messageId) && await TryGetMessage(channel, messageId));
                }
                else if (guild?.GetChannel(channelId) is not null)
                    ok = !m.Groups[3].Success;   // forum/voice/category: channel link only
                if (!ok)
                    broken.Add($"{panel.Name} → \"{b.Label}\" points at a {(m.Groups[3].Success ? "message" : "channel")} that no longer exists");
            }

        if (broken.Count > 0)
        {
            foreach (var line in broken)
                logger.LogWarning("Broken panel link: {Line}", line);
            if (options.AdminLogChannelId != 0 && discord.Client.GetChannel(options.AdminLogChannelId) is IMessageChannel log)
            {
                try { await log.SendMessageAsync("**Broken panel links**\n" + string.Join('\n', broken.Select(b => "• " + b)), allowedMentions: AllowedMentions.None); }
                catch { /* logged above */ }
            }
        }
        return broken;
    }

    private static async Task<bool> TryGetMessage(ITextChannel channel, ulong id)
    {
        try { return await channel.GetMessageAsync(id) is not null; }
        catch { return false; }
    }

    private static ButtonStyle ParseStyle(string style) => style.ToLowerInvariant() switch
    {
        "secondary" or "grey" or "gray" => ButtonStyle.Secondary,
        "success" or "green" => ButtonStyle.Success,
        "danger" or "red" => ButtonStyle.Danger,
        _ => ButtonStyle.Primary,
    };

    public static Color ParseColor(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb) ? new Color(rgb) : Color.Default;
    }
}
