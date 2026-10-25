namespace ServerComms.Bot;

// Panels and their buttons, read fresh from appsettings.json on every sync, so /servercomms reload
// applies edits without restarting the bot.
public sealed class PanelSettings
{
    public List<PanelConfig> Panels { get; set; } = [];
    public Dictionary<string, ButtonConfig> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static PanelSettings Load(IConfiguration configuration)
    {
        var settings = new PanelSettings
        {
            Panels = configuration.GetSection("ServerComms:Panels").Get<List<PanelConfig>>() ?? [],
        };
        var buttons = configuration.GetSection("ServerComms:Buttons").Get<Dictionary<string, ButtonConfig>>() ?? new Dictionary<string, ButtonConfig>();
        foreach (var (id, button) in buttons)
            settings.Buttons[id] = button;
        return settings;
    }

    /// <summary>Readable problems with the panel and button settings.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var panel in Panels)
        {
            var where = $"Panels \"{panel.Name}\"";
            if (string.IsNullOrWhiteSpace(panel.Name)) problems.Add("a panel has no Name");
            else if (!names.Add(panel.Name)) problems.Add($"{where}: the name is used twice");
            if (panel.ChannelId == 0) problems.Add($"{where}: ChannelId is missing");
            var rows = 1;
            var inRow = 0;
            foreach (var id in panel.Buttons)
            {
                if (id == "-") { rows++; inRow = 0; continue; }
                if (!Buttons.ContainsKey(id)) problems.Add($"{where}: button \"{id}\" isn't defined under Buttons");
                if (++inRow > 5) { rows++; inRow = 1; }
            }
            if (rows > 5) problems.Add($"{where}: more than 5 rows of buttons (Discord allows 5 rows of 5)");
            if (panel.Embed is null && string.IsNullOrWhiteSpace(panel.Content)) problems.Add($"{where}: needs an Embed or Content");
        }
        foreach (var (id, b) in Buttons)
        {
            var where = $"Buttons \"{id}\"";
            if (string.IsNullOrWhiteSpace(b.Label) && string.IsNullOrWhiteSpace(b.Emoji)) problems.Add($"{where}: needs a Label or Emoji");
            switch (b.Action.ToLowerInvariant())
            {
                case "link" or "unlink" or "ids" or "points" or "location":
                    break;
                case "tokens":
                    if (string.IsNullOrWhiteSpace(b.TokenType)) problems.Add($"{where}: tokens needs a TokenType");
                    break;
                case "rcon":
                    if (string.IsNullOrWhiteSpace(b.Command)) problems.Add($"{where}: rcon needs a Command");
                    break;
                case "url":
                    if (string.IsNullOrWhiteSpace(b.Url)) problems.Add($"{where}: url needs a Url");
                    break;
                default:
                    problems.Add($"{where}: Action \"{b.Action}\" isn't one of link, unlink, ids, points, tokens, location, rcon, url");
                    break;
            }
        }
        return problems;
    }
}

public sealed class PanelConfig
{
    /// <summary>Unique name; other panels link to this one with {panel:Name}.</summary>
    public string Name { get; set; } = "";
    public ulong ChannelId { get; set; }

    /// <summary>An existing message by this bot to take over instead of posting a new one (e.g. the
    /// old bot's panel, when the same bot token is reused). Used only the first time.</summary>
    public ulong AdoptMessageId { get; set; }

    public string Content { get; set; } = "";
    public EmbedConfig? Embed { get; set; }

    /// <summary>Button IDs from Buttons, in order; 5 per row, "-" starts a new row.</summary>
    public List<string> Buttons { get; set; } = [];
}

public sealed class EmbedConfig
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "";
    public string Thumbnail { get; set; } = "";
    public string Image { get; set; } = "";
    public string Footer { get; set; } = "";
    public string FooterIcon { get; set; } = "";
    public List<EmbedFieldConfig> Fields { get; set; } = [];
}

public sealed class EmbedFieldConfig
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Inline { get; set; }
}

public sealed class ButtonConfig
{
    public string Label { get; set; } = "";
    /// <summary>Primary (blurple), Secondary (grey), Success (green), Danger (red). Url buttons are always link style.</summary>
    public string Style { get; set; } = "Primary";
    /// <summary>A unicode emoji, or a custom one as &lt;:name:id&gt;.</summary>
    public string Emoji { get; set; } = "";

    /// <summary>link, unlink, ids, points, tokens, location, rcon or url.</summary>
    public string Action { get; set; } = "";

    /// <summary>url: the address; {panel:Name} links to another panel's message.</summary>
    public string Url { get; set; } = "";

    /// <summary>rcon: the command. {player_key} {player_id} {tribe_id}.</summary>
    public string Command { get; set; } = "";
    /// <summary>rcon: a server key, "any" (first online map that answers) or "player" (the map the player is on).</summary>
    public string Target { get; set; } = "any";

    /// <summary>tokens: the TokenBank token type.</summary>
    public string TokenType { get; set; } = "";

    /// <summary>The reply. {response} (rcon output), {points}, {tokens}, {map}, {ago}, {player_key},
    /// {player_id}, {tribe_id}, {name}. Empty = a sensible default per action.</summary>
    public string Reply { get; set; } = "";
    /// <summary>rcon: reply when the game sent no output (accepted). Empty = "Done."</summary>
    public string SuccessReply { get; set; } = "";

    /// <summary>Per-member cooldown.</summary>
    public int CooldownMinutes { get; set; }
    /// <summary>One cooldown for everyone, per target map (e.g. a world save).</summary>
    public int SharedCooldownMinutes { get; set; }

    /// <summary>With Target "player": only works while the player is on one of these server keys
    /// (e.g. ["GenesisSimulation"] for the hexagon save). Empty = any map.</summary>
    public List<string> OnlyOnMaps { get; set; } = [];

    /// <summary>Default true for everything except link and url.</summary>
    public bool? RequireLinked { get; set; }
    public bool RequireOnline { get; set; }
    /// <summary>Member must have at least one of these roles.</summary>
    public List<ulong> RequireRoles { get; set; } = [];
}
