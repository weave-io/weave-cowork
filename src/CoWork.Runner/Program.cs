using System.Net.Http.Json;
using CoWork.Contracts;
using CoWork.Runner;

// cowork-runner: connects agents' harnesses to a Weave CoWork server.
//   cowork-runner connect <server> --code XXXX-XXXX   enrol this machine for an agent, then keep serving
//   cowork-runner run                                 serve every agent saved on this machine
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
        Console.Error.WriteLine("cowork-runner connect <server> --code XXXX-XXXX\ncowork-runner run");
        return cmd is null or "--help" or "-h" ? 0 : 2;
    }

    static string? Opt(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static async Task<int> Serve(RunnerConfig config, string host)
    {
        var agents = config.Load();
        if (agents.Count == 0) { Console.Error.WriteLine("No agents saved here yet. Use: cowork-runner connect <server> --code XXXX-XXXX"); return 1; }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
        var links = new List<ServerLink>();
        foreach (var a in agents)
        {
            var link = new ServerLink(a, host);
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
