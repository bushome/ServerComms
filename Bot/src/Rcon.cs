using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace ServerComms.Bot;

// Source RCON client for the cluster's maps.
//
// Address per map: an override in Rcon:Servers wins; otherwise 127.0.0.1 is tried first, then the
// MultiHome address the map's plugin publishes (servers.rcon_host/rcon_port). Whichever answers is
// remembered, so later commands go straight there. Always the literal 127.0.0.1, never "localhost"
// (which Windows may resolve to ::1 while ARK listens on IPv4).
//
// A reply that is empty or ARK's "Server received, But no response!!" means the command was accepted
// (many native commands work that way); only connection, auth or timeout problems are failures.
public sealed class Rcon(Database db, BotOptions options, ILogger<Rcon> logger)
{
    public sealed record Result(bool Ok, string Text, bool NoOutput, string? Error, string ServerKey);

    public sealed record ServerInfo(string Key, string Name, string RconHost, int RconPort, long LastHeartbeat);

    private readonly ConcurrentDictionary<string, string> _workingHost = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All known maps (not hidden, seen within ForgetAfterHours), with online state.</summary>
    public async Task<List<(ServerInfo Server, bool Online)>> ServersAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var forget = now - (long)options.Status.ForgetAfterHours * 3600;
        var list = new List<(ServerInfo, bool)>();
        await using var connection = await db.OpenAsync(ct);
        await using var command = Database.Command(connection,
            "SELECT server_key, display_name, rcon_host, rcon_port, last_heartbeat, state FROM servers WHERE last_heartbeat >= @f ORDER BY display_name",
            ("@f", forget));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var info = new ServerInfo(reader.GetString(0), reader.GetString(1) is { Length: > 0 } n ? n : reader.GetString(0),
                reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4));
            // A map still loading its world doesn't answer RCON reliably yet: not "online".
            list.Add((info, now - info.LastHeartbeat <= options.Status.OfflineAfterSeconds && reader.GetString(5) != "starting"));
        }
        return list;
    }

    public async Task<Result> SendAsync(string serverKey, string command, CancellationToken ct)
    {
        var servers = await ServersAsync(ct);
        var server = servers.FirstOrDefault(s => string.Equals(s.Server.Key, serverKey, StringComparison.OrdinalIgnoreCase)).Server;
        options.Rcon.Servers.TryGetValue(serverKey, out var over);

        var port = over?.Port ?? server?.RconPort ?? 0;
        if (port == 0)
            return new Result(false, "", false, "no RCON port known for this map (is its plugin running?)", serverKey);
        var password = over?.Password ?? options.Rcon.Password;

        var hosts = new List<string>();
        if (!string.IsNullOrEmpty(over?.Host))
            hosts.Add(over.Host);
        else
        {
            if (_workingHost.TryGetValue(serverKey, out var known)) hosts.Add(known);
            hosts.Add("127.0.0.1");
            if (!string.IsNullOrEmpty(server?.RconHost)) hosts.Add(server.RconHost);
        }

        string? lastError = null;
        foreach (var host in hosts.Distinct())
        {
            try
            {
                var reply = await ExecuteAsync(host, port, password, command, ct);
                _workingHost[serverKey] = host;
                var trimmed = reply.Trim();
                var noOutput = trimmed.Length == 0 || trimmed.StartsWith("Server received, But no response", StringComparison.OrdinalIgnoreCase);
                return new Result(true, noOutput ? "" : trimmed, noOutput, null, serverKey);
            }
            catch (RconAuthException)
            {
                return new Result(false, "", false, "RCON password rejected", serverKey);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                lastError = e is OperationCanceledException ? "no response (timeout)" : e.Message;
                logger.LogDebug("RCON {Server} via {Host}:{Port} failed: {Error}", serverKey, host, port, lastError);
            }
        }
        return new Result(false, "", false, lastError ?? "not reachable", serverKey);
    }

    /// <summary>Tries online maps in turn until one answers (for commands any map can run).</summary>
    public async Task<Result> SendAnyAsync(string command, CancellationToken ct)
    {
        var hidden = new HashSet<string>(options.Status.HideServers, StringComparer.OrdinalIgnoreCase);
        var online = (await ServersAsync(ct)).Where(s => s.Online && !hidden.Contains(s.Server.Key)).Select(s => s.Server.Key).ToList();
        if (online.Count == 0)
            return new Result(false, "", false, "no map is online", "");
        Result? last = null;
        foreach (var key in online)
        {
            last = await SendAsync(key, command, ct);
            if (last.Ok)
                return last;
        }
        return last!;
    }

    // ---------------------------------------------------------------------------------------------

    private sealed class RconAuthException : Exception;

    private async Task<string> ExecuteAsync(string host, int port, string password, string command, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Rcon.TimeoutSeconds)));
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, timeout.Token);
        var stream = tcp.GetStream();

        await WriteAsync(stream, 1, 3, password, timeout.Token);           // SERVERDATA_AUTH
        while (true)
        {
            var (id, type, _) = await ReadAsync(stream, timeout.Token);
            if (id == -1) throw new RconAuthException();
            if (type == 2) break;                                           // SERVERDATA_AUTH_RESPONSE
        }

        await WriteAsync(stream, 2, 2, command, timeout.Token);            // SERVERDATA_EXECCOMMAND
        while (true)
        {
            var (id, _, body) = await ReadAsync(stream, timeout.Token);
            if (id == 2) return body;
        }
    }

    private static async Task WriteAsync(NetworkStream stream, int id, int type, string body, CancellationToken ct)
    {
        var text = Encoding.UTF8.GetBytes(body);
        var packet = new byte[4 + 4 + 4 + text.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0), packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        text.CopyTo(packet, 12);
        await stream.WriteAsync(packet, ct);
    }

    private static async Task<(int Id, int Type, string Body)> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 10 || size > 1 << 20)
            throw new IOException($"bad RCON packet size {size}");
        var data = new byte[size];
        await stream.ReadExactlyAsync(data, ct);
        var id = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0));
        var type = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var body = Encoding.UTF8.GetString(data, 8, Math.Max(0, size - 10));
        return (id, type, body);
    }
}
