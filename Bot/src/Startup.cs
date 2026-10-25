namespace ServerComms.Bot;

// Brings the database up to date on start. If MariaDB isn't reachable yet (e.g. the bot service
// started first at boot) it keeps retrying with backoff instead of exiting. Features that need the
// database wait on Ready.
public sealed class DatabaseStartup(Database db, ILogger<DatabaseStartup> logger) : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _ready.Task;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var warned = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var version = await SchemaMigrator.MigrateAsync(db, logger, ct);
                logger.LogInformation("Database ready (schema version {Version})", version);
                _ready.TrySetResult();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                if (!warned)
                {
                    logger.LogWarning("Database not reachable yet: {Error}. Retrying until it is.", e.Message);
                    warned = true;
                }
                else
                    logger.LogDebug("Database still not reachable: {Error}", e.Message);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }
}
