using System.Net;
using System.Text.Json;
using CoWork.Contracts;
using CoWork.Runner;
using CoWork.Runner.Acp;

namespace CoWork.Runner.Tests;

/// <summary>Brief 7, round 3: every session gets CoWork's tools as an MCP server, over HTTP when the harness can take it, else through a stdio
/// bridge (this runner's own binary).</summary>
public class McpTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"cowork-mcp-{Guid.NewGuid():N}");
    public McpTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    sealed class Sink : IRunnerSink
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<StreamEvent> Events = new();
        public Task Stream(StreamEvent e) { Events.Enqueue(e); return Task.CompletedTask; }
        public Task<string?> AskPermission(PermissionRequestMsg r) => Task.FromResult<string?>("allow");
    }

    async Task<JsonElement> ServersGiven(bool http)
    {
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink, null, Path.Combine(_dir, "runner")) { Mcp = new McpEndpoint("http://127.0.0.1:5884/mcp", "tok-123") };
        var cmd = http ? new[] { "env", "FAKE_MCP_HTTP=1", "python3", "-I", Path.Combine(AppContext.BaseDirectory, "fake_acp_agent.py") } : new[] { "python3", "-I", Path.Combine(AppContext.BaseDirectory, "fake_acp_agent.py") };
        await adapter.Deliver(new DeliverMessage(1, 42, 7, "reviewer", "", cmd, Path.Combine(_dir, "ws"), "MCPS"));
        for (var i = 0; i < 300 && !sink.Events.Any(e => e.Kind == StreamKinds.TurnEnded); i++) await Task.Delay(20);
        return JsonDocument.Parse(string.Concat(sink.Events.Where(e => e.Kind == StreamKinds.Text).Select(e => e.Text))).RootElement;
    }

    [Fact]
    public async Task A_harness_that_takes_http_gets_coworks_endpoint_with_the_token_and_the_thread()
    {
        var s = (await ServersGiven(http: true))[0];
        Assert.Equal(("http", "cowork", "http://127.0.0.1:5884/mcp"), (s.GetProperty("type").GetString(), s.GetProperty("name").GetString(), s.GetProperty("url").GetString()));
        var headers = s.GetProperty("headers").EnumerateArray().ToDictionary(h => h.GetProperty("name").GetString()!, h => h.GetProperty("value").GetString());
        Assert.Equal(("Bearer tok-123", "42"), (headers["Authorization"], headers["X-Cowork-Thread"]));
    }

    [Fact]
    public async Task Any_other_harness_gets_the_stdio_bridge_with_the_token_in_its_environment()
    {
        var s = (await ServersGiven(http: false))[0];
        Assert.Equal("cowork", s.GetProperty("name").GetString());
        var args = s.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(new[] { "mcp", "http://127.0.0.1:5884/mcp" }, args.TakeLast(2));
        var env = s.GetProperty("env").EnumerateArray().ToDictionary(h => h.GetProperty("name").GetString()!, h => h.GetProperty("value").GetString());
        Assert.Equal(("tok-123", "42"), (env["COWORK_MCP_TOKEN"], env["COWORK_MCP_THREAD"]));
    }

    sealed class Endpoint : HttpMessageHandler
    {
        public readonly List<(string? Auth, string? Thread, string Body)> Seen = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = await r.Content!.ReadAsStringAsync(ct);
            Seen.Add((r.Headers.Authorization?.ToString(), r.Headers.TryGetValues("X-Cowork-Thread", out var t) ? t.First() : null, body));
            if (!body.Contains("\"id\"")) return new HttpResponseMessage(HttpStatusCode.Accepted);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\n\"result\":{}}") };
        }
    }

    [Fact]
    public async Task The_bridge_posts_each_line_and_writes_each_answer_on_one_line()
    {
        Environment.SetEnvironmentVariable("COWORK_MCP_TOKEN", "tok-9"); Environment.SetEnvironmentVariable("COWORK_MCP_THREAD", "7");
        try
        {
            var endpoint = new Endpoint(); var output = new StringWriter();
            await McpBridge.Run("http://server/mcp", new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n"), output, endpoint);
            Assert.Equal(2, endpoint.Seen.Count);
            Assert.All(endpoint.Seen, s => Assert.Equal(("Bearer tok-9", "7"), (s.Auth, s.Thread)));
            Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":1, \"result\":{}}" + Environment.NewLine, output.ToString());      // the notification has no answer
        }
        finally { Environment.SetEnvironmentVariable("COWORK_MCP_TOKEN", null); Environment.SetEnvironmentVariable("COWORK_MCP_THREAD", null); }
    }
}
