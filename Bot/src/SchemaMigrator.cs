using System.Reflection;
using System.Text.RegularExpressions;

namespace ServerComms.Bot;

// Applies the embedded schema/NNN_name.sql files that are newer than the database's recorded
// version, in order. Every statement is written to be safe to repeat (IF NOT EXISTS), so a migration
// interrupted half way simply runs again on the next start.
public static partial class SchemaMigrator
{
    [GeneratedRegex(@"^schema/(\d{3})_[\w-]+\.sql$")]
    private static partial Regex MigrationName();

    public static async Task<int> MigrateAsync(Database db, ILogger logger, CancellationToken ct)
    {
        await db.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS schema_version (version INT NOT NULL PRIMARY KEY, applied_at BIGINT NOT NULL) " +
            "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci", ct);
        var current = await db.ScalarAsync<int?>("SELECT MAX(version) FROM schema_version", ct) ?? 0;

        var assembly = Assembly.GetExecutingAssembly();
        var migrations = assembly.GetManifestResourceNames()
            .Select(name => (Name: name, Match: MigrationName().Match(name)))
            .Where(m => m.Match.Success)
            .Select(m => (m.Name, Version: int.Parse(m.Match.Groups[1].Value)))
            .OrderBy(m => m.Version)
            .ToList();

        foreach (var (name, version) in migrations.Where(m => m.Version > current))
        {
            await using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded migration {name}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);

            await using var connection = await db.OpenAsync(ct);
            foreach (var statement in Split(sql))
            {
                await using var command = Database.Command(connection, statement);
                await command.ExecuteNonQueryAsync(ct);
            }
            await using (var record = Database.Command(connection,
                "INSERT IGNORE INTO schema_version (version, applied_at) VALUES (@v, UNIX_TIMESTAMP())", ("@v", version)))
                await record.ExecuteNonQueryAsync(ct);

            logger.LogInformation("Database schema updated to version {Version} ({Name})", version, name);
            current = version;
        }
        return current;
    }

    // Splits a migration file on ';' at the end of a line, dropping comment-only lines. Migrations are
    // plain DDL with no ';' inside strings, so nothing smarter is needed.
    private static IEnumerable<string> Split(string sql)
    {
        var current = new List<string>();
        foreach (var raw in sql.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("--"))
                continue;
            current.Add(line);
            if (Regex.IsMatch(line, @";\s*(--.*)?$"))
            {
                var statement = string.Join('\n', current).Trim();
                statement = Regex.Replace(statement, @";\s*(--.*)?$", "");
                if (statement.Length > 0)
                    yield return statement;
                current.Clear();
            }
        }
        if (current.Count > 0)
            yield return string.Join('\n', current).Trim();
    }
}
