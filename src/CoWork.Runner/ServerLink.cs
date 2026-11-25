using CoWork.Contracts;
using CoWork.Runner.Acp;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoWork.Runner;

/// <summary>One agent's connection to the server: receives deliveries, runs them through the ACP adapter, streams events back.</summary>
public sealed class ServerLink : IRunnerSink, IAsyncDisposable
{
    private readonly SavedAgent _agent;
    private readonly string _host;
    private readonly AcpAdapter _adapter;
    private readonly HubConnection _hub;
    private readonly Dictionary<string, TaskCompletionSource<string?>> _asks = new();
    public event Action? Revoked;
    public Action<string> Log = Console.WriteLine;

    public ServerLink(SavedAgent agent, string host, Func<HttpMessageHandler>? handler = null, SessionStore? sessions = null, string? agentRoot = null, string? sandboxWorkspace = null)
    {
        _agent = agent; _host = host;
        _adapter = new AcpAdapter(this, sessions, agentRoot, sandboxWorkspace) { Log = m => Log($"[{agent.Handle}] {m}"), Mcp = new McpEndpoint(agent.Server.TrimEnd('/') + "/mcp", agent.Token) };
        _hub = new HubConnectionBuilder()
            .WithUrl(agent.Server.TrimEnd('/') + "/hubs/runner", o =>
            {
                o.Headers["Authorization"] = "Bearer " + agent.Token;
                o.Headers["X-Cowork-Host"] = host;
                if (handler is not null) o.HttpMessageHandlerFactory = _ => handler();
            })
            .WithAutomaticReconnect(new Backoff())
            .Build();
        _hub.ServerTimeout = TimeSpan.FromSeconds(30);
        _hub.KeepAliveInterval = TimeSpan.FromSeconds(3);      // the server drops a runner it hasn't heard from in 10 s
        _hub.On<DeliverMessage>("Deliver", m => { _ = Task.Run(() => _adapter.Deliver(m)); });
        _hub.On<PermissionAnswerMsg>("PermissionAnswer", a =>
        {
            lock (_asks) if (_asks.Remove(a.RequestId, out var t)) t.TrySetResult(a.OptionId);
        });
        _hub.On("Revoked", () => Revoked?.Invoke());
        _hub.Reconnecting += e => { Log($"[{agent.Handle}] connection lost, reconnecting…"); return Task.CompletedTask; };
        _hub.Reconnected += _ => { Log($"[{agent.Handle}] reconnected"); return Task.CompletedTask; };
    }

    /// <summary>Connects, retrying with backoff until it works. Returns false if the server says the token is no longer valid.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        var delay = 1;
        while (!ct.IsCancellationRequested)
        {
            try { await _hub.StartAsync(ct); Log($"[{_agent.Handle}] connected to {_agent.Server}"); return true; }
            catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.Unauthorized) { return false; }
            catch (OperationCanceledException) { return true; }
            catch (Exception e)
            {
                Log($"[{_agent.Handle}] can't reach {_agent.Server} ({e.Message}); trying again in {delay}s");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { }
                delay = Math.Min(delay * 2, 30);
            }
        }
        return true;
    }

    public Task Stream(StreamEvent e) => _hub.InvokeAsync("Stream", e);

    public async Task<string?> AskPermission(PermissionRequestMsg r)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_asks) _asks[r.RequestId] = tcs;
        await _hub.InvokeAsync("RequestPermission", r);
        return await tcs.Task;      // waits for the owner, as long as it takes
    }

    public async ValueTask DisposeAsync()
    {
        await _adapter.DisposeAsync();
        await _hub.DisposeAsync();
    }

    private sealed class Backoff : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext c) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(c.PreviousRetryCount, 5))));
    }
}
