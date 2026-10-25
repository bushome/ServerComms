using MySqlConnector;

namespace ServerComms.Bot;

// Opens pooled connections to the servercomms database. Every call opens, runs and returns its
// connection to the pool, so a MariaDB restart only fails the calls made while it is down; the next
// call reconnects by itself.
public sealed class Database
{
    private readonly string _connectionString;

    public Database(BotOptions options)
    {
        var db = options.Database;
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = db.Host,
            Port = (uint)db.Port,
            UserID = db.User,
            Password = db.Password,
            Database = db.Name,
            SslMode = MySqlSslMode.None,      // matches the plugins (ssl=0 in my.ini)
            CharacterSet = "utf8mb4",
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 15,
            Pooling = true,
            MaximumPoolSize = 20,
        }.ConnectionString;
    }

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, sql, args);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, sql, args);
        var value = await command.ExecuteScalarAsync(ct);
        if (value is null or DBNull)
            return default;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public static MySqlCommand Command(MySqlConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
