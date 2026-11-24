using System.Net.Http.Json;
using System.Text.Json;
using CoWork.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Dapper;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CoWork.Server.Tests;

/// <summary>A throwaway server on a temp SQLite file, with helpers to act as people and fake runners.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public string DbPath { get; }
    public const string VoiceKey = "testkey", VoiceSecret = "test-secret-test-secret-test-secret-1234";
    public TestApp(bool voice = false)
    {
        DbPath = Path.Combine(Path.GetTempPath(), $"cowork-test-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("COWORK_DB", DbPath);
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
        foreach (var f in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" }) try { File.Delete(f); } catch { }
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
