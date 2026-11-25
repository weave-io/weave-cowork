using System.Net.Http.Json;
using System.Text.Json;
using CoWork.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CoWork.Server.Tests;

/// <summary>A throwaway server on a temp SQLite file, with helpers to act as people and fake runners.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public string DbPath { get; }
    /// <summary>Where this server keeps artifact files (beside its database, so a restart on the same database finds them).</summary>
    public string ArtifactsDir { get; }
    public const string VoiceKey = "testkey", VoiceSecret = "test-secret-test-secret-test-secret-1234";
    private readonly bool _owns;
    /// <param name="existingDb">Start a second server on a database another one left behind (a restart); it isn't deleted on dispose.</param>
    public TestApp(bool voice = false, string? existingDb = null)
    {
        _owns = existingDb is null;
        DbPath = existingDb ?? Path.Combine(Path.GetTempPath(), $"cowork-test-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("COWORK_DB", DbPath);
        ArtifactsDir = Path.Combine(Path.GetDirectoryName(DbPath)!, Path.GetFileNameWithoutExtension(DbPath) + "-artifacts");
        Environment.SetEnvironmentVariable("COWORK_ARTIFACTS", ArtifactsDir);
        Environment.SetEnvironmentVariable("LIVEKIT_API_KEY", voice ? VoiceKey : null);
        Environment.SetEnvironmentVariable("LIVEKIT_API_SECRET", voice ? VoiceSecret : null);
        Environment.SetEnvironmentVariable("LIVEKIT_URL", voice ? "ws://livekit.test:7880" : null);
        _ = Server;
        Environment.SetEnvironmentVariable("LIVEKIT_API_KEY", null); Environment.SetEnvironmentVariable("LIVEKIT_API_SECRET", null); Environment.SetEnvironmentVariable("LIVEKIT_URL", null);
    }

    public SqliteConnection Sql() { var c = new SqliteConnection($"Data Source={DbPath}"); c.Open(); return c; }

    public async Task<Person> Setup(string user = "alex", string name = "Alex")
    {
        var p = new Person(this, user);
        var r = await p.Http.PostAsJsonAsync("/api/setup", new { serverName = "Lumen Labs", username = user, displayName = name, password = "correct horse" });
        r.EnsureSuccessStatusCode(); p.CaptureCookie(r);
        return p;
    }

    public async Task<string> Invite(Person admin) => (await (await admin.Http.PostAsJsonAsync("/api/invites", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    public async Task<Person> Join(Person admin, string user, string name)
    {
        var code = await Invite(admin);
        var p = new Person(this, user);
        var r = await p.Http.PostAsJsonAsync($"/api/invites/{code}/accept", new { username = user, displayName = name, password = "rosa-password" });
        r.EnsureSuccessStatusCode(); p.CaptureCookie(r);
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_owns) foreach (var f in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" }) try { File.Delete(f); } catch { }
        if (_owns) try { Directory.Delete(ArtifactsDir, recursive: true); } catch { }
    }
}

public sealed class Person
{
    private readonly TestApp _app;
    public HttpClient Http { get; }
    public string Handle { get; }
    public string? Cookie { get; private set; }
    public long Id => _app.Sql().ExecuteScalar<long>("SELECT id FROM members WHERE handle=@Handle", new { Handle });

    public Person(TestApp app, string handle)
    {
        _app = app; Handle = handle;
        Http = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
    }
    public void CaptureCookie(HttpResponseMessage r) { if (r.Headers.TryGetValues("Set-Cookie", out var v)) Cookie = v.First().Split(';')[0]; }

    public async Task<long> Channel(string name, bool priv = false)
    {
        var r = await Http.PostAsJsonAsync("/api/channels", new { name, isPrivate = priv });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }
    public async Task<long> VoiceChannel(string name, bool priv = false)
    {
        var r = await Http.PostAsJsonAsync("/api/channels", new { name, isPrivate = priv, kind = "voice" });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }
    public async Task<JsonElement> Post(long channel, string body, long? thread = null)
    {
        var r = await Http.PostAsJsonAsync($"/api/channels/{channel}/posts", new { body, threadId = thread });
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A SignalR connection to /hubs/app as this person.</summary>
    public HubConnection Hub()
    {
        return new HubConnectionBuilder().WithUrl(new Uri(_app.Server.BaseAddress, "/hubs/app"), o =>
        {
            o.HttpMessageHandlerFactory = _ => _app.Server.CreateHandler();
            o.Headers["Cookie"] = Cookie!;
            o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
        }).Build();
    }
}

/// <summary>A pretend runner for one agent: enrols, connects, records deliveries.</summary>
public sealed class FakeRunner : IAsyncDisposable
{
    public long AgentId; public string Handle = ""; public string Token = "";
    public HubConnection? Conn;
    public readonly System.Threading.Channels.Channel<DeliverMessage> Delivered = System.Threading.Channels.Channel.CreateUnbounded<DeliverMessage>();
    public readonly System.Threading.Channels.Channel<PermissionAnswerMsg> Answers = System.Threading.Channels.Channel.CreateUnbounded<PermissionAnswerMsg>();
    public readonly System.Threading.Channels.Channel<CollectArtifact> Collects = System.Threading.Channels.Channel.CreateUnbounded<CollectArtifact>();
    private readonly TestApp _app;
    public FakeRunner(TestApp app) { _app = app; }

    public static async Task<FakeRunner> Enrol(TestApp app, Person owner, string handle)
    {
        var r = await owner.Http.PostAsJsonAsync("/api/agents", new { handle, instructions = "Be brief.", harnessCommand = "opencode acp", workspace = "/tmp" });
        r.EnsureSuccessStatusCode();
        var code = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var f = new FakeRunner(app);
        var e = await app.CreateClient().PostAsJsonAsync("/api/runner/enrol", new EnrolRequest(code, "testbox"));
        e.EnsureSuccessStatusCode();
        var er = (await e.Content.ReadFromJsonAsync<EnrolResponse>())!;
        f.AgentId = er.AgentId; f.Handle = er.Handle; f.Token = er.Token;
        return f;
    }

    public async Task Connect()
    {
        Conn = new HubConnectionBuilder().WithUrl(new Uri(_app.Server.BaseAddress, "/hubs/runner"), o =>
        {
            o.HttpMessageHandlerFactory = _ => _app.Server.CreateHandler();
            o.Headers["Authorization"] = "Bearer " + Token; o.Headers["X-Cowork-Host"] = "testbox";
            o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
        }).Build();
        Conn.On<DeliverMessage>("Deliver", m => Delivered.Writer.TryWrite(m));
        Conn.On<PermissionAnswerMsg>("PermissionAnswer", m => Answers.Writer.TryWrite(m));
        Conn.On<CollectArtifact>("CollectArtifact", m => Collects.Writer.TryWrite(m));
        await Conn.StartAsync();
    }

    public async Task<DeliverMessage> NextDelivery(int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        return await Delivered.Reader.ReadAsync(cts.Token);
    }
    public Task Say(long thread, string text) => Conn!.InvokeAsync("Stream", new StreamEvent(thread, StreamKinds.Text, text));
    public Task End(long thread) => Conn!.InvokeAsync("Stream", new StreamEvent(thread, StreamKinds.TurnEnded));
    public async ValueTask DisposeAsync() { if (Conn is not null) await Conn.DisposeAsync(); }
}

/// <summary>A pretend pool: enrols with a code, connects to /hubs/pool, records the sandbox commands it is sent and reports state.</summary>
public sealed class FakePool : IAsyncDisposable
{
    public long PoolId; public string Name = ""; public string Token = "";
    public HubConnection? Conn;
    public readonly System.Threading.Channels.Channel<object> Commands = System.Threading.Channels.Channel.CreateUnbounded<object>();
    private readonly TestApp _app;
    private FakePool(TestApp app) { _app = app; }

    public static async Task<FakePool> Enrol(TestApp app, Person admin, int slots = 2, long memoryMb = 1500)
    {
        var r = await admin.Http.PostAsJsonAsync("/api/pools", new { });
        r.EnsureSuccessStatusCode();
        var code = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var e = await app.CreateClient().PostAsJsonAsync("/api/pools/enrol", new PoolEnrolRequest(code, "pool-box", slots, memoryMb));
        e.EnsureSuccessStatusCode();
        var pr = (await e.Content.ReadFromJsonAsync<PoolEnrolResponse>())!;
        var p = new FakePool(app) { PoolId = pr.PoolId, Name = pr.Name, Token = pr.Token };
        p.Conn = HubFor(app, "/hubs/pool", pr.Token);
        p.Conn.On<CreateSandbox>("CreateSandbox", c => p.Commands.Writer.TryWrite(c));
        p.Conn.On<ResumeSandbox>("ResumeSandbox", c => p.Commands.Writer.TryWrite(c));
        p.Conn.On<SuspendSandbox>("SuspendSandbox", c => p.Commands.Writer.TryWrite(c));
        p.Conn.On<DeleteSandbox>("DeleteSandbox", c => p.Commands.Writer.TryWrite(c));
        p.Conn.On<AllowEgress>("AllowEgress", c => p.Commands.Writer.TryWrite(c));
        await p.Conn.StartAsync();
        await p.Conn.InvokeAsync("Hello", new PoolHello(slots, memoryMb, Array.Empty<string>()));
        return p;
    }

    public static HubConnection HubFor(TestApp app, string path, string token) =>
        new HubConnectionBuilder().WithUrl(new Uri(app.Server.BaseAddress, path), o =>
        {
            o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            o.Headers["Authorization"] = "Bearer " + token;
            o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
        }).Build();

    public async Task<T> Next<T>(int seconds = 15)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var c = await Commands.Reader.ReadAsync(cts.Token);
        return c is T t ? t : throw new Xunit.Sdk.XunitException($"expected {typeof(T).Name}, got {c.GetType().Name}");
    }
    public bool HasMore => Commands.Reader.TryPeek(out _);
    public Task Refused(string sandboxId, string host, int port = 443) => Conn!.InvokeAsync("EgressRefused", new EgressRefusal(sandboxId, host, port));
    public Task Report(string sandboxId, string state, string? reason = null) => Conn!.InvokeAsync("Report", new SandboxStateReport(sandboxId, state, reason, 120, 340));
    public async ValueTask DisposeAsync() { if (Conn is not null) await Conn.DisposeAsync(); }
}

/// <summary>A pretend runner inside a sandbox: connects with the thread token and records what it is given.</summary>
public sealed class FakeSandbox : IAsyncDisposable
{
    public HubConnection Conn = null!;
    public SandboxSpec Spec = null!;
    public readonly System.Threading.Channels.Channel<DeliverMessage> Delivered = System.Threading.Channels.Channel.CreateUnbounded<DeliverMessage>();
    public readonly System.Threading.Channels.Channel<PermissionAnswerMsg> Answers = System.Threading.Channels.Channel.CreateUnbounded<PermissionAnswerMsg>();
    public readonly System.Threading.Channels.Channel<CollectArtifact> Collects = System.Threading.Channels.Channel.CreateUnbounded<CollectArtifact>();
    public readonly TaskCompletionSource Revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<FakeSandbox> Connect(TestApp app, SandboxSpec spec)
    {
        var s = new FakeSandbox { Spec = spec, Conn = FakePool.HubFor(app, "/hubs/runner", spec.ThreadToken) };
        s.Conn.On<DeliverMessage>("Deliver", m => s.Delivered.Writer.TryWrite(m));
        s.Conn.On<PermissionAnswerMsg>("PermissionAnswer", m => s.Answers.Writer.TryWrite(m));
        s.Conn.On("Revoked", () => s.Revoked.TrySetResult());
        s.Conn.On<CollectArtifact>("CollectArtifact", m => s.Collects.Writer.TryWrite(m));
        s.Conn.Closed += _ => { s.Closed.TrySetResult(); return Task.CompletedTask; };
        await s.Conn.StartAsync();
        return s;
    }
    public async Task<DeliverMessage> NextDelivery(int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        return await Delivered.Reader.ReadAsync(cts.Token);
    }
    public Task Say(string text) => Conn.InvokeAsync("Stream", new StreamEvent(Spec.ThreadId, StreamKinds.Text, text));
    public Task End() => Conn.InvokeAsync("Stream", new StreamEvent(Spec.ThreadId, StreamKinds.TurnEnded));
    public async ValueTask DisposeAsync() => await Conn.DisposeAsync();
}

/// <summary>A pretend OpenAI-compatible model provider on a real port: streams a short answer with usage, and remembers what it was sent.</summary>
public sealed class FakeUpstream : IAsyncDisposable
{
    private readonly Microsoft.AspNetCore.Builder.WebApplication _app;
    public string BaseUrl { get; }
    public readonly System.Collections.Concurrent.ConcurrentQueue<(string? Authorization, string Body)> Seen = new();

    private FakeUpstream(Microsoft.AspNetCore.Builder.WebApplication app, string url) { _app = app; BaseUrl = url; }

    public static async Task<FakeUpstream> Start()
    {
        var b = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        var app = b.Build();
        FakeUpstream? me = null;
        app.MapPost("/v1/chat/completions", async (Microsoft.AspNetCore.Http.HttpContext ctx) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            me!.Seen.Enqueue((ctx.Request.Headers.Authorization.ToString(), body));
            ctx.Response.ContentType = "text/event-stream";
            foreach (var chunk in new[]
            {
                """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":"round"}}]}""",
                """{"id":"c1","choices":[{"index":0,"delta":{"content":"Tax"}}]}""",
                """{"id":"c1","choices":[{"index":0,"finish_reason":"stop","delta":{}}],"usage":{"prompt_tokens":120,"completion_tokens":7,"total_tokens":127,"cost":0.0012}}""",
            })
            { await ctx.Response.WriteAsync("data: " + chunk + "\n\n"); await ctx.Response.Body.FlushAsync(); }
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        });
        await app.StartAsync();
        var addr = app.Urls.First();
        me = new FakeUpstream(app, addr + "/v1");
        return me;
    }
    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}
