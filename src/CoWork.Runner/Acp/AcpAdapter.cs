using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CoWork.Contracts;

namespace CoWork.Runner.Acp;

/// <summary>Where the adapter sends what the agent does: the runner's server connection, or a test's recorder.</summary>
/// <summary>CoWork's own tools for agents: an MCP server on the CoWork server, reached with this runner's token (a sandbox's: its thread token).</summary>
public sealed record McpEndpoint(string Url, string Token);

public interface IRunnerSink
{
    Task Stream(StreamEvent e);
    Task<string?> AskPermission(PermissionRequestMsg r);      // the chosen option id, or null for deny
}

/// <summary>
/// The ACP adapter (AGENTS.md rule 1: harness specifics stop here). One harness process per agent and project (threads outside any project
/// share the agent's own), spawned on first use, each with its own data folder (<see cref="HarnessHome"/>); one ACP session per thread, in the
/// thread's own workspace (<see cref="Workspaces"/>); one session/prompt per delivered message. Maps session/update to <see cref="StreamEvent"/>s.
/// </summary>
public sealed class AcpAdapter : IAsyncDisposable
{
    private readonly IRunnerSink _sink;
    private readonly SemaphoreSlim _spawn = new(1, 1);
    private readonly Dictionary<string, AcpAgentProcess> _procs = new();           // "agent" or "project-<id>" → its harness process
    private readonly ConcurrentDictionary<long, string> _procOf = new();            // thread → the key of the process its session is in
    private readonly Workspaces _places;
    private readonly ConcurrentDictionary<long, string> _sessions = new();           // thread → ACP session id
    private readonly ConcurrentDictionary<string, long> _threads = new();            // ACP session id → thread
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _threadLocks = new();
    private readonly ConcurrentDictionary<long, string> _workspaces = new();         // thread → its folder, so paths read relative to it
    private readonly ConcurrentDictionary<long, bool> _recordOutput = new();         // thread → whether its agent records full tool output
    public Action<string>? Log;
    // events go to the server one at a time, in the order the agent produced them
    private readonly System.Threading.Channels.Channel<(StreamEvent? Event, TaskCompletionSource? Mark)> _outbox = System.Threading.Channels.Channel.CreateUnbounded<(StreamEvent?, TaskCompletionSource?)>();
    private readonly Task _pump;

    private readonly SessionStore? _store;
    private readonly ConcurrentDictionary<string, bool> _canLoad = new();

    private readonly bool _sandbox;
    private readonly ConcurrentDictionary<string, bool> _mcpHttp = new();         // process key → the harness takes MCP servers over HTTP

    /// <summary>CoWork's tools, given to every session (null: none).</summary>
    public McpEndpoint? Mcp { get; init; }

    /// <param name="agentRoot">This agent's own folder on the runner: clones, worktrees and per-project harness data live under it.</param>
    /// <param name="sandboxWorkspace">Inside a sandbox: the one workspace every message works in. The sandbox is the thread's, so the harness
    /// keeps its data in the sandbox's own home (its volume), with no folder per project.</param>
    public AcpAdapter(IRunnerSink sink, SessionStore? store = null, string? agentRoot = null, string? sandboxWorkspace = null)
    {
        _sink = sink; _store = store; _sandbox = sandboxWorkspace is not null;
        _places = new Workspaces(agentRoot ?? Path.Combine(Path.GetTempPath(), "cowork-runner", "agent")) { Fixed = sandboxWorkspace };
        _places.Log = m => Log?.Invoke(m);
        _pump = Task.Run(async () =>
        {
            await foreach (var (e, mark) in _outbox.Reader.ReadAllAsync())
            {
                if (e is not null) try { await _sink.Stream(e); } catch (Exception ex) { Log?.Invoke("couldn't send an event: " + ex.Message); }
                mark?.TrySetResult();
            }
        });
    }
    private void Emit(StreamEvent e) => _outbox.Writer.TryWrite((e, null));

    /// <summary>The folder a thread works in (a sandbox's one workspace), or null if this runner has no session for it.</summary>
    public string? WorkspaceOf(long threadId) => _workspaces.GetValueOrDefault(threadId) ?? _places.Fixed;
    /// <summary>Completes once everything emitted so far has gone to the server (so an ask never overtakes the tool calls before it).</summary>
    private Task Sent() { var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _outbox.Writer.TryWrite((null, t)); return t.Task; }

    public async Task Deliver(DeliverMessage m)
    {
        _recordOutput[m.ThreadId] = m.RecordToolOutput;
        var gate = _threadLocks.GetOrAdd(m.ThreadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var key = m.ProjectId is { } project && !_sandbox ? $"project-{project}" : "agent";
            var proc = await EnsureProcess(key, m);
            var (sessionId, first) = await EnsureSession(proc, key, m);
            if (_sandbox && !string.IsNullOrWhiteSpace(m.SetupCommand)) await Setup(m);
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
            var err = _procOf.TryGetValue(m.ThreadId, out var k) && _procs.TryGetValue(k, out var pr) ? pr.Stderr : null;
            if (!string.IsNullOrEmpty(err)) why += " " + err[^Math.Min(err.Length, 300)..];
            Emit(new StreamEvent(m.ThreadId, StreamKinds.Error, why));
        }
        finally
        {
            Emit(new StreamEvent(m.ThreadId, StreamKinds.TurnEnded));
            gate.Release();
        }
    }

    /// <summary>Where a sandbox remembers that its thread's setup ran: in its home, on the thread's volume.</summary>
    public string SetupMarker { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cowork", "setup.json");
    public const string SetupToolId = "cowork-setup";

    /// <summary>The project's setup command, once per thread, before the first prompt: in the thread's workspace, through the sandbox's proxy
    /// like everything else. It shows as a tool row with its output; its result stays in the volume (and the marker says it ran).</summary>
    private async Task Setup(DeliverMessage m)
    {
        var marker = SetupMarker;
        if (File.Exists(marker)) return;
        var cwd = _workspaces.GetValueOrDefault(m.ThreadId) ?? _places.Fixed ?? ".";
        var title = $"setup: {m.SetupCommand}";
        Emit(new StreamEvent(m.ThreadId, StreamKinds.ToolStart, null, SetupToolId, title, "execute", "running"));
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(m.SetupCommand!);
        var output = new System.Text.StringBuilder(); var code = -1;
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            try { await p.WaitForExitAsync(cts.Token); code = p.ExitCode; }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } lock (output) output.AppendLine("cowork: the setup command took longer than 15 minutes and was stopped."); }
        }
        catch (Exception e) { lock (output) output.AppendLine("cowork: couldn't run the setup command: " + e.Message); }
        string text; lock (output) text = output.ToString();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        Emit(new StreamEvent(m.ThreadId, StreamKinds.ToolEnd, null, SetupToolId, title, null, code == 0 ? "completed" : "failed", bytes, sha, Cap(text)));
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, System.Text.Json.JsonSerializer.Serialize(new { command = m.SetupCommand, exitCode = code, at = DateTimeOffset.UtcNow }));
        Log?.Invoke($"thread {m.ThreadId}: setup '{m.SetupCommand}' exited {code}");
    }

    private async Task<AcpAgentProcess> EnsureProcess(string key, DeliverMessage m)
    {
        await _spawn.WaitAsync();
        try
        {
            if (_procs.TryGetValue(key, out var running) && running.IsRunning) return running;
            // a new process knows none of the old sessions
            foreach (var (thread, k) in _procOf.ToList())
                if (k == key && _sessions.TryRemove(thread, out var sid)) { _threads.TryRemove(sid, out _); _procOf.TryRemove(thread, out _); }
            string cwd; Dictionary<string, string>? env = null;
            if (_sandbox) cwd = _places.Fixed!;
            else if (m.ProjectId is { } project)
            {
                cwd = _places.ProjectFolder(project);
                env = HarnessHome.For(_places.Root, project, l => Log?.Invoke(l));
            }
            else { cwd = m.Workspace; Directory.CreateDirectory(cwd); }
            var p = AcpAgentProcess.Start(m.HarnessCommand, cwd, env);
            Log?.Invoke($"started {string.Join(' ', m.HarnessCommand)} (pid {p.ProcessId}){(env is null ? "" : $" for project {m.ProjectId}, its data in {env["XDG_DATA_HOME"]}")}");
            p.Client.OnNotification = OnNotification;
            p.Client.OnRequest = OnRequest;
            var init = await p.Client.Request("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "cowork-runner", ["version"] = "0.1" } });
            _canLoad[key] = (bool?)init?["agentCapabilities"]?["loadSession"] == true;
            _mcpHttp[key] = (bool?)init?["agentCapabilities"]?["mcpCapabilities"]?["http"] == true;
            _procs[key] = p;
            return p;
        }
        finally { _spawn.Release(); }
    }

    /// <summary>The thread's ACP session: the one in memory, else the one saved before a restart (session/load, when the harness
    /// supports it), else a new one. Returns whether the session is new (so the agent's instructions still need to go in).</summary>
    private async Task<(string Id, bool IsNew)> EnsureSession(AcpAgentProcess proc, string key, DeliverMessage m)
    {
        if (_sessions.TryGetValue(m.ThreadId, out var existing) && _procOf.GetValueOrDefault(m.ThreadId) == key) return (existing, false);
        // the thread's own place: a worktree on its branch, a folder, or the agent's own folder. The same thread finds the same place again.
        var place = await _places.Prepare(m);
        var cwd = place.Cwd;
        _workspaces[m.ThreadId] = cwd;
        _procOf[m.ThreadId] = key;
        if (m.ThreadWorkspace is not null || _sandbox)
            Emit(new StreamEvent(m.ThreadId, StreamKinds.Workspace, place.Location, null, place.Branch, null, place.Place, WorkspaceId: place.WorkspaceId));
        var saved = _store?.Get(m.ThreadId);
        if (saved is not null && _canLoad.GetValueOrDefault(key))
        {
            try
            {
                // the harness replays the old conversation as session/update; the session isn't registered yet, so those are dropped
                await proc.Client.Request("session/load", new JsonObject { ["sessionId"] = saved, ["cwd"] = cwd, ["mcpServers"] = McpServers(key, m.ThreadId) });
                _sessions[m.ThreadId] = saved; _threads[saved] = m.ThreadId;
                Log?.Invoke($"thread {m.ThreadId}: loaded session {saved}");
                return (saved, false);
            }
            catch (Exception e) { Log?.Invoke($"thread {m.ThreadId}: couldn't load session {saved} ({e.Message}); starting a new one"); }
        }
        var res = await proc.Client.Request("session/new", new JsonObject { ["cwd"] = cwd, ["mcpServers"] = McpServers(key, m.ThreadId) });
        var id = (string?)res?["sessionId"] ?? throw new InvalidOperationException("The harness didn't open a session.");
        _sessions[m.ThreadId] = id; _threads[id] = m.ThreadId;
        _store?.Set(m.ThreadId, id);
        return (id, true);
    }

    /// <summary>The session's MCP servers: CoWork's, for this thread. Over HTTP when the harness says it can (OpenCode 2.4.1 does); else a stdio
    /// bridge, this runner's own binary (`cowork-runner mcp`), which forwards each message to the same endpoint.</summary>
    public JsonArray McpServers(string key, long threadId)
    {
        if (Mcp is not { } mcp) return new JsonArray();
        if (_mcpHttp.GetValueOrDefault(key))
            return new JsonArray(new JsonObject
            {
                ["type"] = "http", ["name"] = "cowork", ["url"] = mcp.Url,
                ["headers"] = new JsonArray(new JsonObject { ["name"] = "Authorization", ["value"] = "Bearer " + mcp.Token }, new JsonObject { ["name"] = "X-Cowork-Thread", ["value"] = threadId.ToString() }),
            });
        var (command, args) = McpBridge.Command(mcp.Url);
        return new JsonArray(new JsonObject
        {
            ["name"] = "cowork", ["command"] = command, ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()),
            ["env"] = new JsonArray(new JsonObject { ["name"] = "COWORK_MCP_TOKEN", ["value"] = mcp.Token }, new JsonObject { ["name"] = "COWORK_MCP_THREAD", ["value"] = threadId.ToString() }),
        });
    }

    // ---- agent → us ----

    public void OnNotification(string method, JsonNode? p)
    {
        if (method != "session/update" || p is null) return;
        var sid = (string?)p["sessionId"];
        if (sid is null || !_threads.TryGetValue(sid, out var thread)) return;
        var u = p["update"]; if (u is null) return;
        // a tool call this adapter can only name by its bare title: say what it carried, so the next harness update can be taught
        if ((string?)u["sessionUpdate"] == "tool_call_update" && (string?)u["status"] == "in_progress" && Describe(u, _workspaces.GetValueOrDefault(thread)) == (string?)u["title"])
            Log?.Invoke($"thread {thread}: tool {(string?)u["title"]} has no detail; rawInput: {Trim(u["rawInput"]?.ToJsonString())}");
        try { foreach (var e in Map(thread, u, _workspaces.GetValueOrDefault(thread), _recordOutput.GetValueOrDefault(thread))) Emit(e); }
        catch (Exception ex) { Log?.Invoke($"thread {thread}: couldn't read an update ({ex.Message}); skipped"); }     // one odd update must not stop the session
    }

    /// <summary>session/update → stream events. Only what a thread shows: the agent's words and its tool calls.</summary>
    public static IEnumerable<StreamEvent> Map(long thread, JsonNode u, string? workspace = null, bool recordOutput = false)
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
                    yield return Ended(thread, u, (string?)u["toolCallId"], null, recordOutput);
                break;
            case "tool_call_update":
                // OpenCode 2 sends the first tool_call bare (no input, no locations); what the tool touches arrives in the
                // in_progress update that follows, so a non-final update refines the row's title when it carries any detail
                if (Norm((string?)u["status"]) is not ("completed" or "failed") && (string?)u["toolCallId"] is { } uid && Describe(u, workspace) is { } refined)
                    yield return new StreamEvent(thread, StreamKinds.ToolUpdate, null, uid, refined, null, null);
                if (Norm((string?)u["status"]) is "completed" or "failed" && (string?)u["toolCallId"] is { } id)
                    yield return Ended(thread, u, id, (string?)u["title"] ?? Called(u), recordOutput);
                break;
        }
    }
    /// <summary>A tool finished. The server is always told how much it printed and the SHA-256 of that; the text itself only when the agent records full tool output.</summary>
    private static StreamEvent Ended(long thread, JsonNode u, string? toolId, string? title, bool recordOutput)
    {
        var text = OutputText(u);
        var bytes = text is null ? (long?)null : System.Text.Encoding.UTF8.GetByteCount(text);
        var sha = text is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new StreamEvent(thread, StreamKinds.ToolEnd, null, toolId, title, null, Norm((string?)u["status"]), bytes, sha, recordOutput ? Cap(text) : null);
    }

    /// <summary>At most 64 kB of a tool's output goes to the server (the size and hash cover all of it).</summary>
    public static string? Cap(string? s)
    {
        const int Max = 64 * 1024;
        if (s is null || System.Text.Encoding.UTF8.GetByteCount(s) <= Max) return s;
        var n = 0; var sb = new System.Text.StringBuilder();
        foreach (var r in s.EnumerateRunes()) { n += r.Utf8SequenceLength; if (n > Max) break; sb.Append(r.ToString()); }
        return sb.ToString();
    }

    /// <summary>What a tool printed: rawOutput's text (a string, or its "output"), else the update's content text.</summary>
    public static string? OutputText(JsonNode u)
    {
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (Str(u["rawOutput"]) is { } direct) return direct;
        if (Str(u["rawOutput"]?["output"]) is { } o) return o;
        if (Str(u["content"]) is { } cs) return cs;
        if (u["content"] is JsonArray arr)
        {
            var parts = arr.Select(x => Str(x?["content"]?["text"]) ?? Str(x?["text"])).Where(s => s is not null).ToList();
            if (parts.Count > 0) return string.Join("\n", parts);
        }
        return null;
    }

    /// <summary>
    /// The MCP calls a finished OpenCode code tool made, as OpenCode itself records them: <c>rawOutput.metadata.toolCalls</c>, each with
    /// <c>tool</c> ("cowork.cowork_read_channel") and <c>input</c>. Unlike the code the model wrote, this doesn't vary with how the model wrote it
    /// (a 25 Nov 2026 run wrote <c>tools["cowork"].cowork_read_channel(…)</c>). "cowork_read_channel #checkout-app"; null when there are none.
    /// </summary>
    public static string? Called(JsonNode u)
    {
        if (u["rawOutput"]?["metadata"]?["toolCalls"] is not JsonArray { Count: > 0 } calls) return null;
        var names = calls.Select(c =>
        {
            var tool = (string?)c?["tool"]; if (string.IsNullOrEmpty(tool)) return null;
            var name = tool.Contains('.') ? tool[(tool.LastIndexOf('.') + 1)..] : tool;
            var arg = (c!["input"] as JsonObject)?.Select(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var sv) ? sv : null).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (arg is not null) { arg = arg.Split('\n')[0]; if (arg.Length > 60) arg = arg[..60] + "…"; }
            return arg is null ? name : $"{name} {arg}";
        }).Where(n => n is not null).ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>What a tool touched: the title, plus the first path, pattern or command from rawInput (or locations) when there is one.
    /// A shell tool's location is only its working folder, so for "execute" the command alone counts. When the title is just the
    /// kind ("read"), the row already shows the kind, so the detail stands alone ("src/Api.cs"). Paths under the workspace are relative.</summary>
    public static string? Describe(JsonNode u, string? workspace = null)
    {
        var title = (string?)u["title"];
        var kind = (string?)u["kind"];
        // OpenCode 2.4.1 runs MCP tools through a code tool: the row is "execute" and the calls are in rawInput.code, as
        // `tools.<server>.<tool>({ … })`. Name them: "cowork_read_channel #checkout-app".
        if (u["rawInput"]?["code"] is JsonValue cv && cv.TryGetValue<string>(out var code) && McpCall.Matches(code) is { Count: > 0 } calls)
            return string.Join(", ", calls.Select(c =>
            {
                // its arguments: the quoted strings after it, up to the next call (a summary may hold brackets of its own)
                var next = code.IndexOf("tools.", c.Index + c.Length, StringComparison.Ordinal);
                var tail = code[(c.Index + c.Length)..(next < 0 ? code.Length : next)];
                var args = ArgText.Matches(tail).Select(a => a.Groups[1].Success ? a.Groups[1].Value : a.Groups[2].Value).Where(a => a.Length > 0).Take(1)
                    .Select(a => a.Length > 60 ? a[..60] + "…" : a).ToList();
                var tool = c.Groups[1].Success ? c.Groups[1].Value : c.Groups[2].Value;
                var text = args.Count == 0 ? tool : $"{tool} {args[0]}";
                return text.Length > 120 ? text[..120] + "…" : text;
            }));
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
    private static readonly System.Text.RegularExpressions.Regex McpCall = new(@"tools(?:\.[\w-]+|\[\s*[""'][\w-]+[""']\s*\])\s*(?:\.([\w-]+)|\[\s*[""']([\w-]+)[""']\s*\])\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ArgText = new("'([^']*)'|\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static string Trim(string? s) => s is null ? "none" : s.Length > 300 ? s[..300] + "…" : s;
    private static string? Norm(string? s) => s switch { "in_progress" or "pending" => "running", null => null, _ => s };

    private async Task<JsonNode?> OnRequest(string method, JsonNode? p)
    {
        if (method != "session/request_permission" || p is null) throw new AcpException("Method not found: " + method, -32601);
        var sid = (string?)p["sessionId"];
        if (sid is null || !_threads.TryGetValue(sid, out var thread)) return Cancelled();
        var options = (p["options"] as JsonArray ?? new())
            .Select(o => new PermissionOption((string?)o?["optionId"] ?? "", (string?)o?["name"] ?? "Allow", (string?)o?["kind"] ?? "allow_once")).ToArray();
        var title = (string?)p["toolCall"]?["title"] ?? "run a tool";
        await Sent();
        var chosen = await _sink.AskPermission(new PermissionRequestMsg(thread, Guid.NewGuid().ToString("N"), title, options));
        return chosen is null ? Cancelled()
            : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen } };
    }
    private static JsonNode Cancelled() => new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };

    public async ValueTask DisposeAsync() { _outbox.Writer.TryComplete(); foreach (var p in _procs.Values.ToList()) await p.DisposeAsync(); }
}
