using MySqlConnector;

namespace ServerComms.Bot;

// Read-only queries against other plugins' databases: Permissions (groups), ArkShop (points),
// TokenBank (tokens) and PlayerLogin (last map and time). Database, table and column names come from
// config and are checked to be plain identifiers (letters, digits, _) at startup; values are always
// parameters.
public sealed class Lookups
{
    private readonly BotOptions _options;
    private readonly string _connectionString;
    private bool Asa => _options.Game == "ASA";

    public Lookups(BotOptions options)
    {
        _options = options;
        var main = options.Database;
        var l = options.Lookups;
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = l.Host ?? main.Host,
            Port = (uint)(l.Port ?? main.Port),
            UserID = l.User ?? main.User,
            Password = l.Password ?? main.Password,
            SslMode = MySqlSslMode.None,
            CharacterSet = "utf8mb4",
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 10,
        }.ConnectionString;
    }

    private static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    public bool HasPermissions => _options.Lookups.Permissions.Database.Length > 0;
    public bool HasArkShop => _options.Lookups.ArkShop.Database.Length > 0;
    public bool HasTokenBank => _options.Lookups.TokenBank.Database.Length > 0;
    public bool HasPlayerLogin => _options.Lookups.PlayerLogin.Database.Length > 0;

    /// <summary>The player's Permissions groups, or null when the lookup failed.</summary>
    public async Task<HashSet<string>?> GetGroupsAsync(string playerKey, CancellationToken ct)
    {
        var p = _options.Lookups.Permissions;
        if (!HasPermissions)
            return null;
        try
        {
            var value = await ScalarAsync(
                $"SELECT `{p.GroupsColumn}` FROM `{p.Database}`.`{p.Table}` WHERE `{Or(p.KeyColumn, Asa ? "EOS_Id" : "SteamId")}` = @k",
                ct, ("@k", playerKey));
            return (value as string ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ArkShop points; 0 when the player has no shop row yet.</summary>
    public async Task<long> GetPointsAsync(string playerKey, CancellationToken ct)
    {
        var a = _options.Lookups.ArkShop;
        var value = await ScalarAsync(
            $"SELECT `{a.PointsColumn}` FROM `{a.Database}`.`{a.Table}` WHERE `{Or(a.KeyColumn, Asa ? "EosId" : "SteamId")}` = @k",
            ct, ("@k", playerKey));
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>TokenBank tokens of one type; 0 when there is no row.</summary>
    public async Task<long> GetTokensAsync(string playerKey, string tokenType, CancellationToken ct)
    {
        var t = _options.Lookups.TokenBank;
        var value = await ScalarAsync(
            $"SELECT `{t.TokensColumn}` FROM `{t.Database}`.`{Or(t.Table, Asa ? "tokenbank_players" : "players")}` " +
            $"WHERE `{Or(t.KeyColumn, Asa ? "EosId" : "SteamId")}` = @k AND `{t.TypeColumn}` = @t",
            ct, ("@k", playerKey), ("@t", tokenType));
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>PlayerLogin's last map and time for the player, or null when they have no record.</summary>
    public async Task<(string ServerKey, long LastSeen)?> GetLocationAsync(string playerKey, CancellationToken ct)
    {
        var l = _options.Lookups.PlayerLogin;
        if (!HasPlayerLogin)
            return null;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = Database.Command(connection,
            $"SELECT `{l.ServerColumn}`, `{l.LastSeenColumn}` FROM `{l.Database}`.`{l.Table}` " +
            $"WHERE `{Or(l.KeyColumn, Asa ? "eos_id" : "steam_id")}` = @k ORDER BY `{l.LastSeenColumn}` DESC LIMIT 1",
            ("@k", playerKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return (reader.GetValue(0)?.ToString() ?? "", Convert.ToInt64(reader.GetValue(1)));
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = Database.Command(connection, sql, args);
        return await command.ExecuteScalarAsync(ct);
    }

    /// <summary>All configured identifiers, for the startup check.</summary>
    public static IEnumerable<(string What, string? Value)> Identifiers(LookupOptions l) =>
    [
        ("Permissions:Database", l.Permissions.Database), ("Permissions:Table", l.Permissions.Table),
        ("Permissions:KeyColumn", l.Permissions.KeyColumn), ("Permissions:GroupsColumn", l.Permissions.GroupsColumn),
        ("ArkShop:Database", l.ArkShop.Database), ("ArkShop:Table", l.ArkShop.Table),
        ("ArkShop:KeyColumn", l.ArkShop.KeyColumn), ("ArkShop:PointsColumn", l.ArkShop.PointsColumn),
        ("TokenBank:Database", l.TokenBank.Database), ("TokenBank:Table", l.TokenBank.Table),
        ("TokenBank:KeyColumn", l.TokenBank.KeyColumn), ("TokenBank:TokensColumn", l.TokenBank.TokensColumn),
        ("TokenBank:TypeColumn", l.TokenBank.TypeColumn),
        ("PlayerLogin:Database", l.PlayerLogin.Database), ("PlayerLogin:Table", l.PlayerLogin.Table),
        ("PlayerLogin:KeyColumn", l.PlayerLogin.KeyColumn), ("PlayerLogin:ServerColumn", l.PlayerLogin.ServerColumn),
        ("PlayerLogin:LastSeenColumn", l.PlayerLogin.LastSeenColumn),
    ];
}
