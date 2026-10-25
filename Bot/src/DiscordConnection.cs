using Discord;
using Discord.WebSocket;

namespace ServerComms.Bot;

// Owns the Discord gateway connection. Discord's routine reconnects ("Disconnected / Connecting /
// Connected / Resumed", every hour or so) are logged at Debug; a warning is logged only when the bot
// was actually offline for more than 30 seconds, since that is when events could have been missed.
public sealed class DiscordConnection : IHostedService
{
    private static readonly TimeSpan LongOutage = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> RoutineGatewayMessages =
        ["Disconnected", "Connecting", "Connected", "Resumed", "Disconnecting", "Ready"];

    private readonly BotOptions _options;
    private readonly ILogger<DiscordConnection> _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? _disconnectedAt;

    public DiscordSocketClient Client { get; }

    /// <summary>Completes the first time the guild is available.</summary>
    public Task Ready => _ready.Task;

    public SocketGuild? Guild => Client.GetGuild(_options.Discord.GuildId);

    public DiscordConnection(BotOptions options, ILogger<DiscordConnection> logger)
    {
        _options = options;
        _logger = logger;
        Client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages |
                             GatewayIntents.MessageContent | GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = true,         // member list for linking, role sync and nicknames
            MessageCacheSize = 0,
            LogLevel = LogSeverity.Info,
        });
        Client.Log += OnLog;
        Client.Disconnected += _ =>
        {
            _disconnectedAt ??= DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        };
        Client.Connected += () =>
        {
            if (_disconnectedAt is { } since)
            {
                var gap = DateTimeOffset.UtcNow - since;
                if (gap > LongOutage)
                    _logger.LogWarning("Discord connection was down for {Seconds:F0}s; events in that window may have been missed", gap.TotalSeconds);
                _disconnectedAt = null;
            }
            return Task.CompletedTask;
        };
        Client.GuildAvailable += guild =>
        {
            if (guild.Id == _options.Discord.GuildId && _ready.TrySetResult())
                _logger.LogInformation("Discord ready: {Bot} in {Guild}", Client.CurrentUser?.Username, guild.Name);
            return Task.CompletedTask;
        };
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await Client.LoginAsync(TokenType.Bot, _options.Discord.Token);
        await Client.StartAsync();
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await Client.StopAsync();
        await Client.LogoutAsync();
    }

    private Task OnLog(LogMessage m)
    {
        var level = m.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        // Discord's routine reconnects: "Connected", "Resumed previous session", and the reconnect request
        // itself (a warning carrying GatewayReconnectException). A real outage still warns, via the
        // 30-second check in the Connected handler.
        if (m.Source == "Gateway" && level <= LogLevel.Information &&
            RoutineGatewayMessages.Any(r => (m.Message ?? "").StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            level = LogLevel.Debug;
        if (m.Exception is GatewayReconnectException)
            level = LogLevel.Debug;
        // Rate-limit notices are expected and handled by the library.
        if (m.Message?.Contains("Rate limit", StringComparison.OrdinalIgnoreCase) == true && level == LogLevel.Warning)
            level = LogLevel.Debug;
        _logger.Log(level, m.Exception, "[{Source}] {Message}", m.Source, m.Message ?? m.Exception?.Message);
        return Task.CompletedTask;
    }
}
