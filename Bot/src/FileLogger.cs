using System.Collections.Concurrent;

namespace ServerComms.Bot;

// Minimal daily log file next to the exe (logs/ServerComms.Bot-yyyyMMdd.log), kept for 14 days.
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.GetFiles(directory, "ServerComms.Bot-*.log")
                     .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)))
        {
            try { File.Delete(old); } catch { /* in use or locked: try again next start */ }
        }
    }

    public ILogger CreateLogger(string category) => _loggers.GetOrAdd(category, c => new FileLogger(this, c));

    internal void Write(string line)
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_writer is null || today != _day)
            {
                _writer?.Dispose();
                _day = today;
                _writer = new StreamWriter(Path.Combine(_directory, $"ServerComms.Bot-{today:yyyyMMdd}.log"), append: true) { AutoFlush = true };
            }
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _writer?.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _short = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level))
                return;
            var tag = level switch
            {
                LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
                LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT",
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{tag}] {_short}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
