using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CoWork.Contracts;

namespace CoWork.Runner.Acp;

/// <summary>Where the adapter sends what the agent does: the runner's server connection, or a test's recorder.</summary>
public interface IRunnerSink
{
    Task Stream(StreamEvent e);
    Task<string?> AskPermission(PermissionRequestMsg r);      // the chosen option id, or null for deny
}

/// <summary>
/// The ACP adapter (AGENTS.md rule 1: harness specifics stop here). One harness process per agent, spawned on first use;
/// one ACP session per thread; one session/prompt per delivered message. Maps session/update to <see cref="StreamEvent"/>s.
/// </summary>
public sealed class AcpAdapter : IAsyncDisposable
{
    private readonly IRunnerSink _sink;
    private readonly SemaphoreSlim _spawn = new(1, 1);
    private AcpAgentProcess? _proc;
    private readonly ConcurrentDictionary<long, string> _sessions = new();           // thread → ACP session id
    private readonly ConcurrentDictionary<string, long> _threads = new();            // ACP session id → thread
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _threadLocks = new();
    private readonly ConcurrentDictionary<long, string> _workspaces = new();         // thread → its folder, so paths read relative to it
    public Action<string>? Log;
    // events go to the server one at a time, in the order the agent produced them
    private readonly System.Threading.Channels.Channel<StreamEvent> _outbox = System.Threading.Channels.Channel.CreateUnbounded<StreamEvent>();
    private readonly Task _pump;

    private readonly SessionStore? _store;
    private bool _canLoad;

    public AcpAdapter(IRunnerSink sink, SessionStore? store = null)
    {
        _sink = sink; _store = store;
        _pump = Task.Run(async () =>
        {
            await foreach (var e in _outbox.Reader.ReadAllAsync())
                try { await _sink.Stream(e); } catch (Exception ex) { Log?.Invoke("couldn't send an event: " + ex.Message); }
        });
    }
    private void Emit(StreamEvent e) => _outbox.Writer.TryWrite(e);

    public async Task Deliver(DeliverMessage m)
    {
        var gate = _threadLocks.GetOrAdd(m.ThreadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var proc = await EnsureProcess(m);
            var (sessionId, first) = await EnsureSession(proc, m);
            // the agent's instructions go in the first prompt of each session, before the first message
            var text = first && !string.IsNullOrWhiteSpace(m.Instructions) ? $"{m.Instructions.Trim()}\n\n{m.Text}" : m.Text;
            var result = await proc.Client.Request("session/prompt", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            });
            Log?.Invoke($"thread {m.ThreadId}: turn ended ({(string?)result?["stopReason"]})");
        }
        catch (Exception e)
        {
            var why = e.Message;
            var err = _proc?.Stderr;
            if (!string.IsNullOrEmpty(err)) why += " " + err[^Math.Min(err.Length, 300)..];
            Emit(new StreamEvent(m.ThreadId, StreamKinds.Error, why));
        }
        finally
        {
            Emit(new StreamEvent(m.ThreadId, StreamKinds.TurnEnded));
            gate.Release();
        }
    }

    private async Task<AcpAgentProcess> EnsureProcess(DeliverMessage m)
    {
        await _spawn.WaitAsync();
        try
        {
            if (_proc is { IsRunning: true }) return _proc;
            // a new process knows none of the old sessions
            _sessions.Clear(); _threads.Clear();
            Directory.CreateDirectory(m.Workspace);
            var p = AcpAgentProcess.Start(m.HarnessCommand, m.Workspace);
            Log?.Invoke($"started {string.Join(' ', m.HarnessCommand)} (pid {p.ProcessId})");
            p.Client.OnNotification = OnNotification;
            p.Client.OnRequest = OnRequest;
            var init = await p.Client.Request("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "cowork-runner", ["version"] = "0.1" } });
            _canLoad = (bool?)init?["agentCapabilities"]?["loadSession"] == true;
            _proc = p;
            return p;
        }
        finally { _spawn.Release(); }
    }

    /// <summary>The thread's ACP session: the one in memory, else the one saved before a restart (session/load, when the harness
    /// supports it), else a new one. Returns whether the session is new (so the agent's instructions still need to go in).</summary>
    private async Task<(string Id, bool IsNew)> EnsureSession(AcpAgentProcess proc, DeliverMessage m)
    {
        if (_sessions.TryGetValue(m.ThreadId, out var existing)) return (existing, false);
        var cwd = Path.GetFullPath(m.Workspace);
        _workspaces[m.ThreadId] = cwd;
        var saved = _store?.Get(m.ThreadId);
        if (saved is not null && _canLoad)
        {
            try
            {
                // the harness replays the old conversation as session/update; the session isn't registered yet, so those are dropped
                await proc.Client.Request("session/load", new JsonObject { ["sessionId"] = saved, ["cwd"] = cwd, ["mcpServers"] = new JsonArray() });
                _sessions[m.ThreadId] = saved; _threads[saved] = m.ThreadId;
                Log?.Invoke($"thread {m.ThreadId}: loaded session {saved}");
                return (saved, false);
            }
            catch (Exception e) { Log?.Invoke($"thread {m.ThreadId}: couldn't load session {saved} ({e.Message}); starting a new one"); }
        }
        var res = await proc.Client.Request("session/new", new JsonObject { ["cwd"] = cwd, ["mcpServers"] = new JsonArray() });
        var id = (string?)res?["sessionId"] ?? throw new InvalidOperationException("The harness didn't open a session.");
        _sessions[m.ThreadId] = id; _threads[id] = m.ThreadId;
        _store?.Set(m.ThreadId, id);
        return (id, true);
    }

    // ---- agent → us ----

    public void OnNotification(string method, JsonNode? p)
    {
        if (method != "session/update" || p is null) return;
        var sid = (string?)p["sessionId"];
        if (sid is null || !_threads.TryGetValue(sid, out var thread)) return;
        var u = p["update"]; if (u is null) return;
        try { foreach (var e in Map(thread, u, _workspaces.GetValueOrDefault(thread))) Emit(e); }
        catch (Exception ex) { Log?.Invoke($"thread {thread}: couldn't read an update ({ex.Message}); skipped"); }     // one odd update must not stop the session
    }

    /// <summary>session/update → stream events. Only what a thread shows: the agent's words and its tool calls.</summary>
    public static IEnumerable<StreamEvent> Map(long thread, JsonNode u, string? workspace = null)
    {
        switch ((string?)u["sessionUpdate"])
        {
            case "agent_message_chunk":
                if ((string?)u["content"]?["type"] == "text" && (string?)u["content"]?["text"] is { Length: > 0 } t)
                    yield return new StreamEvent(thread, StreamKinds.Text, t);
                break;
            case "tool_call":
                yield return new StreamEvent(thread, StreamKinds.ToolStart, null, (string?)u["toolCallId"], Describe(u, workspace) ?? "tool", (string?)u["kind"], Norm((string?)u["status"]));
                if (Norm((string?)u["status"]) is "completed" or "failed")
                    yield return new StreamEvent(thread, StreamKinds.ToolEnd, null, (string?)u["toolCallId"], null, null, Norm((string?)u["status"]));
                break;
            case "tool_call_update":
                // OpenCode 2 sends the first tool_call bare (no input, no locations); what the tool touches arrives in the
                // in_progress update that follows, so a non-final update refines the row's title when it carries any detail
                if (Norm((string?)u["status"]) is not ("completed" or "failed") && (string?)u["toolCallId"] is { } uid && Describe(u, workspace) is { } refined)
                    yield return new StreamEvent(thread, StreamKinds.ToolUpdate, null, uid, refined, null, null);
                if (Norm((string?)u["status"]) is "completed" or "failed" && (string?)u["toolCallId"] is { } id)
                    yield return new StreamEvent(thread, StreamKinds.ToolEnd, null, id, (string?)u["title"], null, Norm((string?)u["status"]));
                break;
        }
    }
    /// <summary>What a tool touched: the title, plus the first path, pattern or command from rawInput (or locations) when there is one.
    /// A shell tool's location is only its working folder, so for "execute" the command alone counts. When the title is just the
    /// kind ("read"), the row already shows the kind, so the detail stands alone ("src/Api.cs"). Paths under the workspace are relative.</summary>
    public static string? Describe(JsonNode u, string? workspace = null)
    {
        var title = (string?)u["title"];
        var kind = (string?)u["kind"];
        string? detail = null;
        if (u["rawInput"] is JsonObject raw)
            // a search names its pattern first (its "path" is only where it looked); anything else names the file, command or address
            foreach (var k in kind == "search" ? new[] { "pattern", "query", "filePath", "path" } : new[] { "filePath", "path", "command", "pattern", "query", "url" })
                if (raw[k] is JsonValue v && v.TryGetValue<string>(out var sv) && !string.IsNullOrWhiteSpace(sv)) { detail = sv; break; }
        if (detail is null && kind != "execute" && u["locations"] is JsonArray { Count: > 0 } locs)
            detail = (locs[0]?["path"] as JsonValue)?.GetValue<string>();
        if (detail is null) return string.IsNullOrWhiteSpace(title) ? null : title;
        if (workspace is not null && detail.TrimEnd('/') == workspace.TrimEnd('/')) detail = ".";
        else if (workspace is not null && detail.StartsWith(workspace.TrimEnd('/') + "/", StringComparison.Ordinal)) detail = detail[(workspace.TrimEnd('/').Length + 1)..];
        if (detail.Length > 120) detail = detail[..120] + "…";
        if (string.IsNullOrWhiteSpace(title) || string.Equals(title, kind, StringComparison.OrdinalIgnoreCase)) return detail;
        return title.Contains(detail) ? title : $"{title} {detail}";
    }
    private static string? Norm(string? s) => s switch { "in_progress" or "pending" => "running", null => null, _ => s };

    private async Task<JsonNode?> OnRequest(string method, JsonNode? p)
    {
        if (method != "session/request_permission" || p is null) throw new AcpException("Method not found: " + method, -32601);
        var sid = (string?)p["sessionId"];
        if (sid is null || !_threads.TryGetValue(sid, out var thread)) return Cancelled();
        var options = (p["options"] as JsonArray ?? new())
            .Select(o => new PermissionOption((string?)o?["optionId"] ?? "", (string?)o?["name"] ?? "Allow", (string?)o?["kind"] ?? "allow_once")).ToArray();
        var title = (string?)p["toolCall"]?["title"] ?? "run a tool";
        var chosen = await _sink.AskPermission(new PermissionRequestMsg(thread, Guid.NewGuid().ToString("N"), title, options));
        return chosen is null ? Cancelled()
            : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen } };
    }
    private static JsonNode Cancelled() => new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };

    public async ValueTask DisposeAsync() { _outbox.Writer.TryComplete(); if (_proc is not null) await _proc.DisposeAsync(); }
}
