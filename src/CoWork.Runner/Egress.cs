using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace CoWork.Runner;

/// <summary>
/// A sandbox's only ways out (docs/architecture/provisioning.html, "the egress proxy"). The sandbox runs with no network at all; the pool gives
/// it a folder with two unix sockets:
/// <list type="bullet">
/// <item><c>server.sock</c>: a plain pipe to the CoWork server (its hub and its model gateway are always allowed);</item>
/// <item><c>proxy.sock</c>: an HTTP proxy (CONNECT for https, absolute URLs for http) that lets through only the hosts on the sandbox's list,
/// refuses anything else with 403 and reports the refusal, and opens a host once or for the rest of the thread when someone allows it.</item>
/// </list>
/// Each sandbox has its own sockets, so the proxy knows which sandbox is asking without trusting anything the sandbox says.
/// </summary>
public sealed class EgressProxy : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _serverHost; private readonly int _serverPort;
    private readonly Func<string, int, Task> _refused;
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _once = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Socket> _listeners = new();
    public Action<string>? Log;

    /// <param name="dir">The folder mounted into the sandbox (made here, 0755; the sockets are 0666 so the sandbox's user can connect).</param>
    /// <param name="hosts">Hosts it may reach: exact names, or <c>*.example.com</c> for any name under it.</param>
    /// <param name="refused">Called (at most once a minute per host) when a host is refused.</param>
    public EgressProxy(string dir, string serverHost, int serverPort, IEnumerable<string> hosts, Func<string, int, Task> refused)
    {
        _dir = dir; _serverHost = serverHost; _serverPort = serverPort; _refused = refused;
        lock (_hosts) foreach (var h in hosts) _hosts.Add(h.Trim().TrimEnd('.'));
    }

    public string Folder => _dir;
    public const string ServerSocket = "server.sock", ProxySocket = "proxy.sock";

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Listen(Path.Combine(_dir, ServerSocket), ToServer);
        Listen(Path.Combine(_dir, ProxySocket), Proxy);
    }

    /// <summary>Someone allowed a host: for one connection, or for the rest of the thread.</summary>
    public void Allow(string host, bool once)
    {
        host = host.Trim().TrimEnd('.');
        if (once) _once.AddOrUpdate(host, 1, (_, n) => n + 1);
        else lock (_hosts) _hosts.Add(host);
        _reported.TryRemove(host, out _);
    }

    /// <summary>Whether the host is on the list (exact, or under a <c>*.</c> entry).</summary>
    public bool Listed(string host)
    {
        host = host.TrimEnd('.');
        lock (_hosts) return _hosts.Contains(host) || _hosts.Any(h => h.StartsWith("*.") && host.EndsWith(h[1..], StringComparison.OrdinalIgnoreCase));
    }

    private bool Admit(string host)
    {
        if (Listed(host)) return true;
        while (_once.TryGetValue(host, out var n) && n > 0)
            if (_once.TryUpdate(host, n - 1, n)) return true;
        return false;
    }

    private void Listen(string path, Func<Socket, Task> handle)
    {
        if (File.Exists(path)) File.Delete(path);
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Bind(new UnixDomainSocketEndPoint(path));
        s.Listen(64);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)0b110_110_110);
        lock (_listeners) _listeners.Add(s);
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket c;
                try { c = await s.AcceptAsync(_stop.Token); } catch { return; }
                _ = Task.Run(async () => { try { await handle(c); } catch { } finally { c.Dispose(); } });
            }
        });
    }

    private async Task ToServer(Socket c)
    {
        using var up = new TcpClient();
        await up.ConnectAsync(_serverHost, _serverPort, _stop.Token);
        await Pipe(new NetworkStream(c, false), up.GetStream(), ReadOnlyMemory<byte>.Empty);
    }

    private async Task Proxy(Socket c)
    {
        var down = new NetworkStream(c, false);
        var (head, rest) = await ReadHead(down);
        if (head is null) return;
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3) { await Reply(down, 400, "Bad request"); return; }
        string host; int port; string? requestLine = null;
        if (first[0] == "CONNECT")
        {
            (host, port) = HostPort(first[1], 443);
        }
        else if (Uri.TryCreate(first[1], UriKind.Absolute, out var u) && u.Scheme is "http")
        {
            host = u.Host; port = u.Port;
            requestLine = $"{first[0]} {u.PathAndQuery} {first[2]}";
        }
        else { await Reply(down, 400, "This proxy takes CONNECT and http:// requests."); return; }

        if (!Admit(host))
        {
            Log?.Invoke($"refused {host}:{port}");
            // the report goes first: a client that has already hung up makes the 403 throw, and the ask must still reach the server
            var report = Task.CompletedTask;
            if (!_reported.TryGetValue(host, out var at) || DateTime.UtcNow - at > TimeSpan.FromMinutes(1))
            {
                _reported[host] = DateTime.UtcNow;
                report = Task.Run(async () => { try { await _refused(host, port); } catch (Exception e) { Log?.Invoke($"couldn't report {host}: {e.Message}"); } });
            }
            try { await Reply(down, 403, $"CoWork: {host} isn't on this project's network list. Whoever started the thread has been asked to allow it; try again once they have."); }
            catch (IOException) { Log?.Invoke($"{host}: the client hung up before the refusal reached it"); }
            await report;
            return;
        }
        using var up = new TcpClient();
        try { await up.ConnectAsync(host, port, _stop.Token); }
        catch (Exception e) { await Reply(down, 502, $"CoWork: couldn't reach {host}:{port} ({e.Message})."); return; }
        var upStream = up.GetStream();
        if (requestLine is null)
        {
            await down.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));
            await Pipe(down, upStream, rest);
            return;
        }
        // a plain http request: send it on in origin form, one request per connection (a client can't hop hosts on it)
        var upgrade = lines.Any(l => l.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase));
        var headers = lines.Skip(1).Where(l => l.Length > 0 && !l.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
            && (upgrade || !l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)));
        var sb = new StringBuilder(requestLine).Append("\r\n");
        foreach (var h in headers) sb.Append(h).Append("\r\n");
        if (!upgrade) sb.Append("Connection: close\r\n");
        sb.Append("\r\n");
        await upStream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
        await Pipe(down, upStream, rest);
    }

    private static (string Host, int Port) HostPort(string s, int defaultPort)
    {
        if (s.StartsWith('[')) { var end = s.IndexOf(']'); var h = s[1..end]; return (h, s.Length > end + 2 && int.TryParse(s[(end + 2)..], out var p6) ? p6 : defaultPort); }
        var i = s.LastIndexOf(':');
        return i > 0 && int.TryParse(s[(i + 1)..], out var p) ? (s[..i], p) : (s, defaultPort);
    }

    /// <summary>The request's head (up to the blank line, at most 32 kB) and whatever arrived after it.</summary>
    private static async Task<(string? Head, ReadOnlyMemory<byte> After)> ReadHead(Stream s)
    {
        var buf = new byte[32 * 1024]; var n = 0;
        while (n < buf.Length)
        {
            var r = await s.ReadAsync(buf.AsMemory(n));
            if (r == 0) return (null, default);
            n += r;
            var end = buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8);
            if (end >= 0) return (Encoding.ASCII.GetString(buf, 0, end), buf.AsMemory(end + 4, n - end - 4));
        }
        return (null, default);
    }

    private static async Task Reply(Stream s, int code, string text)
    {
        var body = Encoding.UTF8.GetBytes(text + "\n");
        var reason = code switch { 403 => "Forbidden", 502 => "Bad Gateway", _ => "Bad Request" };
        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        await s.WriteAsync(body);
    }

    private static async Task Pipe(Stream a, Stream b, ReadOnlyMemory<byte> first)
    {
        if (!first.IsEmpty) await b.WriteAsync(first);
        var t1 = a.CopyToAsync(b); var t2 = b.CopyToAsync(a);
        await Task.WhenAny(t1, t2);
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_listeners) foreach (var s in _listeners) s.Dispose();
        try { foreach (var f in new[] { ServerSocket, ProxySocket }) File.Delete(Path.Combine(_dir, f)); } catch { }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Inside the sandbox: the harness and the runner speak TCP, so the runner offers the pool's two sockets on loopback ports: 127.0.0.1:3129 is
/// the server (COWORK_SERVER), 127.0.0.1:3128 is the proxy (HTTP_PROXY / HTTPS_PROXY). Nothing else leaves: the container has no network.
/// </summary>
public static class LoopbackForwarder
{
    public static void Start(int port, string socketPath, CancellationToken ct)
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, port);
        l.Start(128);
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(ct); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    using (var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                    {
                        try
                        {
                            await s.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                            using var us = new NetworkStream(s, false);
                            var down = c.GetStream();
                            var t1 = down.CopyToAsync(us, ct); var t2 = us.CopyToAsync(down, ct);
                            await Task.WhenAny(t1, t2);
                        }
                        catch { }
                    }
                });
            }
        });
    }
}
