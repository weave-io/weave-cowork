using System.Net;
using System.Net.Sockets;
using System.Text;
using CoWork.Contracts;
using CoWork.Runner;
using CoWork.Runner.Acp;

namespace CoWork.Runner.Tests;

/// <summary>Brief 7, round 2: a sandbox's ways out. The pool's proxy on real unix sockets, a real TCP "internet" on loopback.</summary>
public class EgressTests : IAsyncDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"cowork-eg-{Guid.NewGuid():N}");
    readonly TcpListener _web = new(IPAddress.Loopback, 0);
    readonly System.Collections.Concurrent.ConcurrentQueue<string> _heads = new();
    readonly List<(string Host, int Port)> _refused = new();
    int WebPort => ((IPEndPoint)_web.LocalEndpoint).Port;

    public EgressTests()
    {
        Directory.CreateDirectory(_dir);
        _web.Start();
        // a tiny web server: remembers each request's head and answers "hello from <path>"
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c; try { c = await _web.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream(); var buf = new byte[8192]; var n = 0;
                        while (!Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) { var r = await s.ReadAsync(buf.AsMemory(n)); if (r == 0) return; n += r; }
                        var head = Encoding.ASCII.GetString(buf, 0, n); _heads.Enqueue(head);
                        var path = head.Split(' ')[1];
                        var body = Encoding.UTF8.GetBytes("hello from " + path);
                        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                        await s.WriteAsync(body);
                    }
                });
            }
        });
    }

    public async ValueTask DisposeAsync() { _web.Stop(); await Task.Yield(); try { Directory.Delete(_dir, true); } catch { } }

    EgressProxy Proxy(params string[] hosts)
    {
        var p = new EgressProxy(Path.Combine(_dir, "sb-1"), "127.0.0.1", WebPort, hosts, (h, port) => { lock (_refused) _refused.Add((h, port)); return Task.CompletedTask; });
        p.Start();
        return p;
    }

    /// <summary>What a client in the sandbox sees: one raw exchange over the proxy socket.</summary>
    async Task<string> Through(EgressProxy p, string request, string? afterConnect = null)
    {
        using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await s.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(p.Folder, EgressProxy.ProxySocket)));
        using var ns = new NetworkStream(s);
        await ns.WriteAsync(Encoding.ASCII.GetBytes(request));
        var sb = new StringBuilder(); var buf = new byte[8192];
        if (afterConnect is not null)
        {
            var r0 = await ns.ReadAsync(buf); sb.Append(Encoding.ASCII.GetString(buf, 0, r0));
            if (!sb.ToString().StartsWith("HTTP/1.1 200")) return sb.ToString();
            await ns.WriteAsync(Encoding.ASCII.GetBytes(afterConnect));
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int r; while ((r = await ns.ReadAsync(buf, cts.Token)) > 0) sb.Append(Encoding.ASCII.GetString(buf, 0, r));
        return sb.ToString();
    }

    [Fact]
    public async Task A_listed_host_is_tunnelled_and_anything_else_is_refused_and_reported_once()
    {
        await using var p = Proxy("localhost", "*.npmjs.org");
        var ok = await Through(p, $"CONNECT localhost:{WebPort} HTTP/1.1\r\nHost: localhost:{WebPort}\r\n\r\n", "GET /tunnel HTTP/1.1\r\nHost: localhost\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 Connection established", ok);
        Assert.EndsWith("hello from /tunnel", ok);

        var no = await Through(p, "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403 Forbidden", no);
        Assert.Contains("example.com isn't on this project's network list", no);
        await Through(p, "CONNECT example.com:443 HTTP/1.1\r\n\r\n");               // tried again at once: refused, not reported again
        Assert.Equal(new[] { ("example.com", 443) }, _refused);
        Assert.True(p.Listed("registry.npmjs.org") && !p.Listed("npmjs.org.evil.example") && !p.Listed("example.com"));
    }

    [Fact]
    public async Task Allowed_once_opens_one_connection_and_for_the_thread_opens_it_for_good()
    {
        await using var p = Proxy();
        var connect = $"CONNECT localhost:{WebPort} HTTP/1.1\r\n\r\n";
        Assert.StartsWith("HTTP/1.1 403", await Through(p, connect));
        p.Allow("localhost", once: true);
        Assert.StartsWith("HTTP/1.1 200", await Through(p, connect, "GET /once HTTP/1.1\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 403", await Through(p, connect));
        p.Allow("localhost", once: false);
        Assert.StartsWith("HTTP/1.1 200", await Through(p, connect, "GET /a HTTP/1.1\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 200", await Through(p, connect, "GET /b HTTP/1.1\r\n\r\n"));
    }

    [Fact]
    public async Task A_plain_http_request_goes_on_in_origin_form_without_proxy_headers_one_per_connection()
    {
        await using var p = Proxy("localhost");
        var res = await Through(p, $"GET http://localhost:{WebPort}/docs?q=1 HTTP/1.1\r\nHost: localhost:{WebPort}\r\nProxy-Connection: keep-alive\r\nProxy-Authorization: Basic eA==\r\nConnection: keep-alive\r\nAccept: */*\r\n\r\n");
        Assert.EndsWith("hello from /docs?q=1", res);
        Assert.True(_heads.TryDequeue(out var head));
        Assert.StartsWith("GET /docs?q=1 HTTP/1.1\r\n", head);
        Assert.DoesNotContain("Proxy-", head);
        Assert.Contains("Connection: close", head); Assert.DoesNotContain("keep-alive", head);
        Assert.Contains("Accept: */*", head);
    }

    [Fact]
    public async Task The_server_socket_is_a_plain_pipe_to_the_server_whatever_the_list_says()
    {
        await using var p = Proxy();           // nothing listed
        using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await s.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(p.Folder, EgressProxy.ServerSocket)));
        using var ns = new NetworkStream(s);
        await ns.WriteAsync(Encoding.ASCII.GetBytes("GET /gateway/v1/models HTTP/1.1\r\nHost: 127.0.0.1:3129\r\n\r\n"));
        var buf = new byte[4096]; var sb = new StringBuilder(); int r;
        while ((r = await ns.ReadAsync(buf)) > 0) sb.Append(Encoding.ASCII.GetString(buf, 0, r));
        Assert.EndsWith("hello from /gateway/v1/models", sb.ToString());
        if (!OperatingSystem.IsWindows())
            Assert.Equal((UnixFileMode)0b110_110_110, File.GetUnixFileMode(Path.Combine(p.Folder, EgressProxy.ServerSocket)) & (UnixFileMode)0b111_111_111);     // the sandbox's user may connect
    }

    sealed class Sink : IRunnerSink
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<StreamEvent> Events = new();
        public Task Stream(StreamEvent e) { Events.Enqueue(e); return Task.CompletedTask; }
        public Task<string?> AskPermission(PermissionRequestMsg r) => Task.FromResult<string?>("allow");
    }

    [Fact]
    public async Task The_projects_setup_command_runs_once_per_thread_before_the_first_prompt_and_its_output_is_a_tool_row()
    {
        var repo = Path.Combine(_dir, "work", "repo"); Directory.CreateDirectory(repo);
        var marker = Path.Combine(_dir, "work", "home", ".cowork", "setup.json");
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink, new SessionStore(Path.Combine(_dir, "sessions.json")), Path.Combine(_dir, "runner"), sandboxWorkspace: repo) { SetupMarker = marker };
        DeliverMessage M(long id, string text) => new(id, 42, 7, "researcher", "", new[] { "python3", "-I", Path.Combine(AppContext.BaseDirectory, "fake_acp_agent.py") }, "", text, false, 9,
            new ThreadWorkspace(3, "app", "repository", "/srv/git/checkout-app.git", "main", null, "cowork/x-42"), "echo installing; echo ok > .setup-done; echo installed 3 packages");
        async Task Turn(DeliverMessage m) { var before = sink.Events.Count(e => e.Kind == StreamKinds.TurnEnded); await adapter.Deliver(m); for (var i = 0; i < 300 && sink.Events.Count(e => e.Kind == StreamKinds.TurnEnded) == before; i++) await Task.Delay(20); }
        await Turn(M(1, "ECHO one"));
        await Turn(M(2, "ECHO two"));
        var setup = sink.Events.Where(e => e.ToolId == AcpAdapter.SetupToolId).ToList();
        Assert.Equal(new[] { StreamKinds.ToolStart, StreamKinds.ToolEnd }, setup.Select(e => e.Kind));           // once, though two messages came
        Assert.Equal(("setup: echo installing; echo ok > .setup-done; echo installed 3 packages", "completed"), (setup[1].Title, setup[1].Status));
        Assert.Equal("installing\ninstalled 3 packages\n", setup[1].Output);
        Assert.True(File.Exists(Path.Combine(repo, ".setup-done")) && File.Exists(marker));                        // its result stays, and the marker says it ran
        var order = sink.Events.ToList();
        Assert.True(order.FindIndex(e => e.ToolId == AcpAdapter.SetupToolId && e.Kind == StreamKinds.ToolEnd) < order.FindIndex(e => e.Kind == StreamKinds.Text), "setup ran before the first prompt");
    }
}
