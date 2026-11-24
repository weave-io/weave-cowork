using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoWork.Runner.Acp;

// JSON-RPC 2.0 over a process's stdio, one JSON object per line (the Agent Client Protocol's stdio transport).
// Adapted from the C# ACP client in Weave Fleet (WeaveFleet.Infrastructure/Harnesses/Acp/AcpClient.cs), trimmed to
// what a runner needs: requests with responses, notifications, and requests the agent makes of us.

public sealed class AcpClient : IAsyncDisposable
{
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private Task? _reader;

    /// <summary>Notifications (no id): method, params.</summary>
    public Action<string, JsonNode?>? OnNotification;
    /// <summary>Requests from the agent: method, params → result. Throw to answer with an error.</summary>
    public Func<string, JsonNode?, Task<JsonNode?>>? OnRequest;
    public event Action<Exception?>? Closed;

    public AcpClient(TextReader input, TextWriter output) { _in = input; _out = output; }

    public void Start() => _reader = Task.Run(ReadLoop);

    public async Task<JsonNode?> Request(string method, JsonNode? parameters, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    public Task Notify(string method, JsonNode? parameters) =>
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    private async Task Write(JsonObject msg)
    {
        await _writeLock.WaitAsync();
        try { await _out.WriteLineAsync(msg.ToJsonString()); await _out.FlushAsync(); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoop()
    {
        Exception? error = null;
        try
        {
            string? line;
            while ((line = await _in.ReadLineAsync(_cts.Token)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;     // some agents log plain lines to stdout
                JsonObject? msg;
                try { msg = JsonNode.Parse(line)?.AsObject(); } catch (JsonException) { continue; }
                if (msg is null) continue;
                // notifications and responses are handled in order, right here; requests from the agent may take a while (permissions)
                if (msg["method"] is null || msg["id"] is null) await Handle(msg); else _ = Handle(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { error = e; }
        foreach (var p in _pending.Values) p.TrySetException(new IOException("The harness closed the connection."));
        Closed?.Invoke(error);
    }

    private async Task Handle(JsonObject msg)
    {
        var method = (string?)msg["method"];
        var id = msg["id"];
        if (method is null)
        {
            // a response to one of our requests
            if (id is not null && _pending.TryRemove(id.GetValue<long>(), out var tcs))
            {
                if (msg["error"] is JsonNode err) tcs.TrySetException(new AcpException((string?)err["message"] ?? "ACP error", err["code"]?.GetValue<int>() ?? 0));
                else tcs.TrySetResult(msg["result"]);
            }
            return;
        }
        if (id is null) { OnNotification?.Invoke(method, msg["params"]); return; }
        JsonObject reply = new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        try
        {
            if (OnRequest is null) throw new AcpException("Method not found: " + method, -32601);
            reply["result"] = await OnRequest(method, msg["params"]);
        }
        catch (Exception e) { reply["error"] = new JsonObject { ["code"] = e is AcpException a ? a.Code : -32603, ["message"] = e.Message }; }
        try { await Write(reply); } catch { /* the agent went away */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_reader is not null) { try { await _reader.WaitAsync(TimeSpan.FromSeconds(1)); } catch { } }
    }
}

public sealed class AcpException : Exception
{
    public int Code { get; }
    public AcpException(string message, int code) : base(message) { Code = code; }
}
