using System.Net.Http.Json;
using CoWork.Contracts;
using CoWork.Runner;

// cowork-runner: connects agents' harnesses to a Weave CoWork server.
//   cowork-runner connect <server> --code XXXX-XXXX   enrol this machine for an agent, then keep serving
//   cowork-runner run                                 serve every agent saved on this machine
//   cowork-runner pool connect <server> --code …      enrol this machine as a pool (sandboxes per thread, rootless podman), then serve
//   cowork-runner pool run                            serve the pools saved on this machine
//   cowork-runner sandbox                             inside a sandbox: serve one thread with the token in COWORK_THREAD_TOKEN
return await Cli.Main(args);

static class Cli
{
    public static async Task<int> Main(string[] args)
    {
        var config = new RunnerConfig();
        var host = Environment.MachineName;
        var cmd = args.FirstOrDefault();
        if (cmd == "connect")
        {
            var server = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
            var code = Opt(args, "--code");
            if (server is null || code is null) { Console.Error.WriteLine("Usage: cowork-runner connect <server> --code XXXX-XXXX"); return 2; }
            server = server.TrimEnd('/');
            using var http = new HttpClient();
            HttpResponseMessage res;
            try { res = await http.PostAsJsonAsync(server + "/api/runner/enrol", new EnrolRequest(code, Opt(args, "--host") ?? host)); }
            catch (Exception e) { Console.Error.WriteLine($"Couldn't reach {server}: {e.Message}"); return 1; }
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                Console.Error.WriteLine(err.TryGetProperty("error", out var m) ? m.GetString() : $"The server said {(int)res.StatusCode}.");
                return 1;
            }
            var er = (await res.Content.ReadFromJsonAsync<EnrolResponse>())!;
            config.Add(new SavedAgent(server, er.AgentId, er.Handle, er.Token));
            Console.WriteLine($"Enrolled {er.Handle}. Saved to {config.Path}. Staying connected; press Ctrl+C to stop (later: cowork-runner run).");
            return await Serve(config, Opt(args, "--host") ?? host);
        }
        if (cmd == "run") return await Serve(config, Opt(args, "--host") ?? host);
        if (cmd == "pool") return await PoolMode(config, args.Skip(1).ToArray(), host);
        if (cmd == "sandbox") return await SandboxMode();
        if (cmd == "mcp" && args.Length > 1) return await CoWork.Runner.Acp.McpBridge.Run(args[1], Console.In, Console.Out);
        Console.Error.WriteLine("cowork-runner connect <server> --code XXXX-XXXX\ncowork-runner run\ncowork-runner pool connect <server> --code XXXX-XXXX --slots 2 --memory 1500M\ncowork-runner pool run");
        return cmd is null or "--help" or "-h" ? 0 : 2;
    }

    /// <summary>`pool connect`: enrol this machine as a pool with a one-time code, save it, then serve. `pool run`: serve the pools saved here.</summary>
    static async Task<int> PoolMode(RunnerConfig agents, string[] args, string host)
    {
        var pools = new PoolConfig(agents);
        var sub = args.FirstOrDefault();
        if (sub == "connect")
        {
            var server = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
            var code = Opt(args, "--code");
            if (server is null || code is null) { Console.Error.WriteLine("Usage: cowork-runner pool connect <server> --code XXXX-XXXX --slots 2 --memory 1500M"); return 2; }
            server = server.TrimEnd('/');
            var slots = int.TryParse(Opt(args, "--slots"), out var sl) && sl > 0 ? sl : 2;
            var memory = PoolConfig.MemoryMb(Opt(args, "--memory") ?? "1500M");
            if (memory is null) { Console.Error.WriteLine("--memory is a size like 1500M or 2G."); return 2; }
            using var http = new HttpClient();
            HttpResponseMessage res;
            try { res = await http.PostAsJsonAsync(server + "/api/pools/enrol", new PoolEnrolRequest(code, Opt(args, "--host") ?? host, slots, memory.Value)); }
            catch (Exception e) { Console.Error.WriteLine($"Couldn't reach {server}: {e.Message}"); return 1; }
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                Console.Error.WriteLine(err.TryGetProperty("error", out var m) ? m.GetString() : $"The server said {(int)res.StatusCode}.");
                return 1;
            }
            var pr = (await res.Content.ReadFromJsonAsync<PoolEnrolResponse>())!;
            pools.Add(new SavedPool(server, pr.PoolId, pr.Name, pr.Token, slots, memory.Value, pr.ServerId));
            Console.WriteLine($"Enrolled {pr.Name}: {slots} slots, {memory} MB each. Saved to {pools.Path}. Staying connected; press Ctrl+C to stop (later: cowork-runner pool run).");
            return await ServePools(pools);
        }
        if (sub == "run") return await ServePools(pools);
        Console.Error.WriteLine("cowork-runner pool connect <server> --code XXXX-XXXX --slots 2 --memory 1500M\ncowork-runner pool run");
        return 2;
    }

    static async Task<int> ServePools(PoolConfig config)
    {
        var saved = config.Load();
        if (saved.Count == 0) { Console.Error.WriteLine("No pools saved here yet. Use: cowork-runner pool connect <server> --code XXXX-XXXX"); return 1; }
        using var stop = new CancellationTokenSource();
        void Stop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; Stop(); });
        var links = new List<PoolLink>();
        foreach (var p in saved)
        {
            var link = new PoolLink(p, RunnerConfig.DataRoot());
            var pool = p;
            link.Revoked += () => { Console.WriteLine($"[{pool.Name}] the server removed this pool; forgetting it."); config.Remove(pool); _ = link.DisposeAsync(); };
            links.Add(link);
            _ = Task.Run(async () => { if (!await link.ConnectAsync(stop.Token)) { Console.Error.WriteLine($"[{pool.Name}] the server no longer accepts this pool's token; forgetting it."); config.Remove(pool); } });
        }
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        foreach (var l in links) await l.DisposeAsync();       // the sandboxes keep running; the server suspends idle ones when the pool is back
        return 0;
    }

    /// <summary>`sandbox`: the runner inside a sandbox (the image's entry point). It serves one agent in one thread, connecting with the thread's
    /// token, with the workspace the pool made and the gateway as the harness's only model provider. Exits when the server revokes the token.</summary>
    static async Task<int> SandboxMode()
    {
        string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";
        var server = Env("COWORK_SERVER").TrimEnd('/');
        var token = Env("COWORK_THREAD_TOKEN");
        if (server.Length == 0 || token.Length == 0 || !long.TryParse(Env("COWORK_AGENT"), out var agentId)) { Console.Error.WriteLine("cowork-runner sandbox needs COWORK_SERVER, COWORK_THREAD_TOKEN and COWORK_AGENT."); return 2; }
        var handle = Env("COWORK_HANDLE"); var workspace = Env("COWORK_WORKSPACE") is { Length: > 0 } w ? w : "/work/repo";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Env("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        if (Env("COWORK_MODEL") is { Length: > 0 } model)
            Console.WriteLine($"[{handle}] the harness's model: {model} through {CoWork.Runner.Acp.GatewayConfig.WriteOpenCode(configHome, server + "/gateway/v1", model)}");
        using var stop = new CancellationTokenSource();
        void Stop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; Stop(); });
        // the sandbox has no network: the pool's two sockets are its ways out, offered here on loopback (the server on 3129, the proxy on 3128)
        if (Env("COWORK_SERVER_SOCKET") is { Length: > 0 } serverSocket) LoopbackForwarder.Start(3129, serverSocket, stop.Token);
        if (Env("COWORK_PROXY_SOCKET") is { Length: > 0 } proxySocket) LoopbackForwarder.Start(3128, proxySocket, stop.Token);
        var link = new ServerLink(new SavedAgent(server, agentId, handle, token), "sandbox",
            sessions: new SessionStore(Path.Combine(configHome, "cowork-runner", "sessions.json")),
            agentRoot: Path.Combine(home, ".local", "share", "cowork-runner"), sandboxWorkspace: workspace);
        var code = 0;
        link.Revoked += () => { Console.WriteLine($"[{handle}] the server revoked this sandbox's token; stopping."); Stop(); };
        if (!await link.ConnectAsync(stop.Token)) { Console.Error.WriteLine($"[{handle}] the server doesn't accept this sandbox's token."); return 3; }
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        await link.DisposeAsync();
        return code;
    }

    static string? Opt(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static async Task<int> Serve(RunnerConfig config, string host)
    {
        var agents = config.Load();
        if (agents.Count == 0) { Console.Error.WriteLine("No agents saved here yet. Use: cowork-runner connect <server> --code XXXX-XXXX"); return 1; }
        using var stop = new CancellationTokenSource();
        // The handlers can fire after Serve returned and disposed stop (ProcessExit always does): cancelling then is a no-op.
        void Stop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        // SIGTERM: stop serving and close the connections properly, so the server sees the runner leave at once
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; Stop(); });
        var links = new List<ServerLink>();
        foreach (var a in agents)
        {
            var stateDir = Path.GetDirectoryName(config.Path)!;
            var key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a.Server.TrimEnd('/')));
            var serverKey = Convert.ToHexString(key)[..8].ToLowerInvariant();
            var agentRoot = Path.Combine(RunnerConfig.DataRoot(), $"{serverKey}-{a.AgentId}");     // its clones, worktrees and per-project harness data
            var link = new ServerLink(a, host, sessions: new SessionStore(Path.Combine(stateDir, $"sessions-{serverKey}-{a.AgentId}.json")), agentRoot: agentRoot);
            Console.WriteLine($"[{a.Handle}] workspaces and harness data in {agentRoot}");
            var agent = a;
            link.Revoked += () => { Console.WriteLine($"[{agent.Handle}] the server removed this agent's access; forgetting it."); config.Remove(agent); _ = link.DisposeAsync(); };
            links.Add(link);
            _ = Task.Run(async () =>
            {
                if (!await link.ConnectAsync(stop.Token)) { Console.Error.WriteLine($"[{agent.Handle}] the server no longer accepts this token; forgetting it."); config.Remove(agent); }
            });
        }
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        foreach (var l in links) await l.DisposeAsync();      // stops each harness we started
        return 0;
    }
}
