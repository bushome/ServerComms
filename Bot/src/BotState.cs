namespace ServerComms.Bot;

// Durable key/value state for the bot (bot_state table), e.g. the crosschat cursor.
public sealed class BotState(Database db)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct) =>
        await db.ScalarAsync<string>("SELECT v FROM bot_state WHERE k = @k", ct, ("@k", key));

    public async Task SetAsync(string key, string value, CancellationToken ct) =>
        await db.ExecuteAsync(
            "INSERT INTO bot_state (k, v, updated_at) VALUES (@k, @v, UNIX_TIMESTAMP()) " +
            "ON DUPLICATE KEY UPDATE v = VALUES(v), updated_at = VALUES(updated_at)",
            ct, ("@k", key), ("@v", value));

    public async Task<long?> GetLongAsync(string key, CancellationToken ct) =>
        long.TryParse(await GetAsync(key, ct), out var value) ? value : null;

    public Task SetLongAsync(string key, long value, CancellationToken ct) => SetAsync(key, value.ToString(), ct);
}
