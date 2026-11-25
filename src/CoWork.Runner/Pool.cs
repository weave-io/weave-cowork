using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoWork.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoWork.Runner;

public record SavedPool(string Server, long PoolId, string Name, string Token, int Slots, long MemoryMb, string? ServerId = null);

/// <summary>The pools this machine serves: {server, poolId, token, slots, memory} in ~/.config/cowork-runner/pools.json (0600).</summary>
public sealed class PoolConfig
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string Path { get; }
    public PoolConfig(RunnerConfig agents) { Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(agents.Path)!, "pools.json"); }
    public List<SavedPool> Load() => File.Exists(Path) ? JsonSerializer.Deserialize<List<SavedPool>>(File.ReadAllText(Path), Json) ?? new() : new();
    public void Save(List<SavedPool> pools)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(pools, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);      // it holds the pool's token
        File.Move(tmp, Path, overwrite: true);
    }
    public void Add(SavedPool p) { var all = Load().Where(x => !(x.Server == p.Server && x.PoolId == p.PoolId)).ToList(); all.Add(p); Save(all); }
    public void Remove(SavedPool p) => Save(Load().Where(x => !(x.Server == p.Server && x.PoolId == p.PoolId)).ToList());

    /// <summary>"1500M" → 1500, "2G" → 2048 (megabytes); null if it isn't a size.</summary>
    public static long? MemoryMb(string s)
    {
        s = s.Trim().ToUpperInvariant();
        var mult = s.EndsWith('G') || s.EndsWith("GB") ? 1024 : 1;
        var num = s.TrimEnd('B').TrimEnd('M', 'G');
        return long.TryParse(num, out var n) && n > 0 ? n * mult : null;
    }
}

/// <summary>
/// A pool: a runner that serves no agent itself and starts a sandbox (a rootless podman container) per thread when the server asks
/// (docs/architecture/provisioning.html, steps 4 to 7). For each sandbox it keeps one mirror per repository and fetches it, makes a volume with a
/// copy of the repository on the thread's branch and a home for the harness, and runs the image with the memory limit; the runner inside
/// connects out with the thread's token. Everything it makes carries its labels (`cowork.pool`, `cowork.sandbox`, `cowork.thread`), and it
/// only ever removes what carries them.
///
/// The network: a sandbox runs with `--network none`. Its only ways out are two unix sockets of its own <see cref="EgressProxy"/>, mounted at
/// /run/cowork: one to the server (hub and gateway), one to an HTTP proxy that applies the project's network list. The runner inside offers
/// them on loopback ports (<see cref="LoopbackForwarder"/>).
/// </summary>
public sealed class PoolLink : IAsyncDisposable
{
    private readonly SavedPool _pool;
    private readonly HubConnection _hub;
    private readonly string _key;                 // <server id>-p<pool id>: names and labels are unique per server and pool
    private readonly string _root;
    private readonly string _podman;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, byte> _started = new();       // sandboxes this process started and hasn't stopped
    private readonly ConcurrentDictionary<string, EgressProxy> _proxies = new();  // each running sandbox's way out
    private readonly CancellationTokenSource _stop = new();
    public event Action? Revoked;
    public Action<string> Log = Console.WriteLine;

    public PoolLink(SavedPool pool, string dataRoot, string podman = "podman")
    {
        _pool = pool; _podman = podman;
        _key = $"{(string.IsNullOrEmpty(pool.ServerId) ? Hash8(pool.Server.TrimEnd('/')) : Safe(pool.ServerId)[..Math.Min(8, pool.ServerId.Length)])}-p{pool.PoolId}";
        _root = Path.Combine(dataRoot, "pools", _key);
        _hub = new HubConnectionBuilder()
            .WithUrl(pool.Server.TrimEnd('/') + "/hubs/pool", o => o.Headers["Authorization"] = "Bearer " + pool.Token)
            .WithAutomaticReconnect(new Backoff())
            .Build();
        _hub.ServerTimeout = TimeSpan.FromSeconds(30);
        _hub.KeepAliveInterval = TimeSpan.FromSeconds(3);
        _hub.On<CreateSandbox>("CreateSandbox", c => { _ = Task.Run(() => Start(c.Spec, resume: false)); });
        _hub.On<ResumeSandbox>("ResumeSandbox", c => { _ = Task.Run(() => Start(c.Spec, resume: true)); });
        _hub.On<SuspendSandbox>("SuspendSandbox", c => { _ = Task.Run(() => Suspend(c.SandboxId)); });
        _hub.On<DeleteSandbox>("DeleteSandbox", c => { _ = Task.Run(() => Delete(c)); });
        _hub.On<AllowEgress>("AllowEgress", a =>
        {
            if (_proxies.TryGetValue(a.SandboxId, out var p)) { p.Allow(a.Host, a.Once); Log($"[{pool.Name}] {a.SandboxId}: {a.Host} allowed {(a.Once ? "once" : "for the thread")}"); }
        });
        _hub.On("Revoked", () => Revoked?.Invoke());
        _hub.Reconnected += async _ => { Log($"[{pool.Name}] reconnected"); await Hello(); };
        _hub.Reconnecting += _ => { Log($"[{pool.Name}] connection lost, reconnecting…"); return Task.CompletedTask; };
    }

    public string Key => _key;

    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        var delay = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hub.StartAsync(ct);
                Log($"[{_pool.Name}] connected to {_pool.Server}: {_pool.Slots} slots, {_pool.MemoryMb} MB each; labels cowork.pool={_key}; data in {_root}");
                await Hello();
                _ = Task.Run(() => Watch(_stop.Token));
                return true;
            }
            catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.Unauthorized) { return false; }
            catch (OperationCanceledException) { return true; }
            catch (Exception e)
            {
                Log($"[{_pool.Name}] can't reach {_pool.Server} ({e.Message}); trying again in {delay}s");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { }
                delay = Math.Min(delay * 2, 30);
            }
        }
        return true;
    }

    private bool _swept;
    private async Task Hello()
    {
        // a sandbox's only way out is this process's proxy: containers left running by an earlier pool process can't reach anything, so
        // they stop (their volumes stay) and the server resumes them when their threads need them
        if (!_swept)
        {
            _swept = true;
            foreach (var id in await Running()) { Log($"[{_pool.Name}] {id} was left running by an earlier pool process; stopping it (volume kept)"); await RemoveContainer(id); }
        }
        var running = await Running();
        foreach (var id in running) _started.TryAdd(id, 0);
        await _hub.InvokeAsync("Hello", new PoolHello(_pool.Slots, _pool.MemoryMb, running.ToArray()));
    }

    private Task Report(SandboxStateReport r) => _hub.InvokeAsync("Report", r);

    public static string ContainerName(string key, string sandboxId) => $"cowork-{key}-{sandboxId}";
    private string Name(string sandboxId) => ContainerName(_key, sandboxId);

    /// <summary>The sandboxes (by id) whose containers carry this pool's label and are running.</summary>
    private async Task<List<string>> Running()
    {
        var (code, o, _) = await Run(_podman, null, TimeSpan.FromSeconds(30), "ps", "--filter", $"label=cowork.pool={_key}", "--format", "{{index .Labels \"cowork.sandbox\"}}");
        return code == 0 ? o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList() : new();
    }

    /// <summary>A container that stopped by itself (the runner in it ended, or it ran out of memory) frees its slot: the pool says it's suspended
    /// (the volume stays) and removes the stopped container.</summary>
    private async Task Watch(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
            try
            {
                var running = (await Running()).ToHashSet();
                foreach (var id in _started.Keys.Where(id => !running.Contains(id)).ToList())
                {
                    var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                    if (!await gate.WaitAsync(0)) continue;          // it's being started or stopped right now
                    try
                    {
                        if ((await Running()).Contains(id) || !_started.TryRemove(id, out _)) continue;
                        Log($"[{_pool.Name}] {id}'s container stopped by itself; its volume is kept");
                        await RemoveContainer(id);
                        await CloseWay(id);
                        await Report(new SandboxStateReport(id, SandboxStates.Suspended, "its container stopped"));
                    }
                    finally { gate.Release(); }
                }
            }
            catch (Exception e) { Log($"[{_pool.Name}] couldn't check the containers: {e.Message}"); }
        }
    }

    /// <summary>Create or resume: mirror, volume, a copy of the repository on the thread's branch, then the container.</summary>
    private async Task Start(SandboxSpec s, bool resume)
    {
        var gate = _locks.GetOrAdd(s.SandboxId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var t0 = Stopwatch.StartNew();
            var name = Name(s.SandboxId);
            var labels = Labels(s);
            await RemoveContainer(s.SandboxId);            // a container left from before (it died) goes; its volume stays
            var fresh = !await Ok(_podman, "volume", "exists", name);
            if (fresh)
            {
                await Must(_podman, TimeSpan.FromSeconds(30), new[] { "volume", "create" }.Concat(labels).Append(name).ToArray());
                Log($"[{_pool.Name}] {s.SandboxId}: volume {name} for thread {s.ThreadId}");
            }
            if (fresh || !resume) await Prepare(s, name, labels);
            var workspaceMs = t0.ElapsedMilliseconds;

            var t1 = Stopwatch.StartNew();
            // no network at all: the sandbox's ways out are the two sockets of its own proxy (the server, and the project's list)
            var proxy = await OpenWay(s);
            var args = new List<string> { "run", "-d", "--name", name };
            args.AddRange(labels);
            args.AddRange(new[] { "--memory", $"{s.Limits.MemoryMb}m", "--memory-swap", $"{s.Limits.MemoryMb}m", "--pids-limit", "512" });
            if (s.Limits.Cpus is { } cpus) args.AddRange(new[] { "--cpus", cpus.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "--network", "none", "-v", $"{name}:/work", "-v", $"{proxy.Folder}:/run/cowork:ro" });
            // what the runner inside needs: where the server is (its loopback port onto server.sock) and the thread's token (by name only: the
            // value comes from this process's environment, so it isn't on podman's command line), who it is, and the proxy for everything else.
            // No provider key, no sign-in, not the pool's token.
            const string Proxy = "http://127.0.0.1:3128";
            foreach (var (k, v) in new[] { ("COWORK_SERVER", "http://127.0.0.1:3129"), ("COWORK_SERVER_SOCKET", "/run/cowork/" + EgressProxy.ServerSocket),
                         ("COWORK_PROXY_SOCKET", "/run/cowork/" + EgressProxy.ProxySocket), ("COWORK_AGENT", s.AgentId.ToString()), ("COWORK_HANDLE", s.Handle),
                         ("COWORK_THREAD", s.ThreadId.ToString()), ("COWORK_MODEL", s.Template.Model ?? ""), ("COWORK_WORKSPACE", "/work/repo"),
                         ("HTTP_PROXY", Proxy), ("HTTPS_PROXY", Proxy), ("http_proxy", Proxy), ("https_proxy", Proxy), ("NO_PROXY", "127.0.0.1,localhost"), ("no_proxy", "127.0.0.1,localhost") })
                args.AddRange(new[] { "-e", $"{k}={v}" });
            args.AddRange(new[] { "-e", "COWORK_THREAD_TOKEN" });
            args.Add(s.Template.Image);
            await Must(_podman, TimeSpan.FromMinutes(10), args.ToArray(), new() { ["COWORK_THREAD_TOKEN"] = s.ThreadToken });
            _started[s.SandboxId] = 0;
            Log($"[{_pool.Name}] {s.SandboxId}: {(resume ? "resumed" : "started")} {name} from {s.Template.Image}, {s.Limits.MemoryMb} MB, no network; it may reach {string.Join(", ", Hosts(s).DefaultIfEmpty("only the server"))}");
            await Report(new SandboxStateReport(s.SandboxId, SandboxStates.Running, null, workspaceMs, t1.ElapsedMilliseconds));
        }
        catch (Exception e)
        {
            Log($"[{_pool.Name}] {s.SandboxId}: couldn't start: {e.Message}");
            try { await RemoveContainer(s.SandboxId); } catch { }
            await CloseWay(s.SandboxId);
            try { await Report(new SandboxStateReport(s.SandboxId, SandboxStates.Failed, Short(e.Message))); } catch { }
        }
        finally { gate.Release(); }
    }

    private string[] Labels(SandboxSpec s) => new[]
    {
        "--label", $"cowork.pool={_key}", "--label", $"cowork.sandbox={s.SandboxId}", "--label", $"cowork.thread={s.ThreadId}", "--label", $"cowork.agent={s.AgentId}",
    };

    /// <summary>The thread's volume: a home for the harness and, for a repository, a copy of it from the pool's mirror on the thread's own branch.
    /// Done once, by a short-lived container of the same image (so the files belong to the sandbox's user).</summary>
    private async Task Prepare(SandboxSpec s, string volume, string[] labels)
    {
        var args = new List<string> { "run", "--rm" };
        args.AddRange(labels);
        args.AddRange(new[] { "--network", "none", "--entrypoint", "/bin/sh", "-v", $"{volume}:/work" });
        string script;
        if (s.Workspace is { Kind: "repository", Url: { } url })
        {
            var mirror = await Mirror(s.Workspace.Name ?? "repo", url);
            args.AddRange(new[] { "-v", $"{mirror}:/mirror:ro", "-e", $"BASE={(string.IsNullOrWhiteSpace(s.Workspace.BaseBranch) ? "main" : s.Workspace.BaseBranch)}", "-e", $"BRANCH={s.Workspace.Branch}", "-e", $"URL={url}" });
            // the copy's origin is the real repository (the sandbox holds no credential to push to it); the mirror isn't mounted later
            script = """
                set -e
                mkdir -p /work/home
                if [ ! -d /work/repo/.git ]; then
                  # the mirror belongs to the pool's user, not the sandbox's: trust it for this copy only (a throwaway global config)
                  export HOME=/tmp
                  git config --global --add safe.directory /mirror
                  git clone --quiet --branch "$BASE" /mirror /work/repo
                  cd /work/repo
                  git checkout --quiet -b "$BRANCH"
                  git remote set-url origin "$URL"
                fi
                """;
        }
        else script = "set -e; mkdir -p /work/home /work/repo";
        args.Add(s.Template.Image);
        args.AddRange(new[] { "-c", script });
        await Must(_podman, TimeSpan.FromMinutes(10), args.ToArray());
    }

    /// <summary>One mirror per repository on this pool, fetched before each new thread.</summary>
    private async Task<string> Mirror(string name, string url)
    {
        var dir = Path.Combine(_root, "mirrors", $"{Safe(name)}-{Hash8(url)}.git");
        var gate = _locks.GetOrAdd("mirror:" + dir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                Log($"[{_pool.Name}] mirroring {url} into {dir}");
                await Must("git", TimeSpan.FromMinutes(15), new[] { "clone", "--quiet", "--mirror", "--", url, dir }, new() { ["GIT_TERMINAL_PROMPT"] = "0" });
            }
            else await Must("git", TimeSpan.FromMinutes(5), new[] { "-C", dir, "fetch", "--quiet", "--prune", "origin" }, new() { ["GIT_TERMINAL_PROMPT"] = "0" });
            return dir;
        }
        finally { gate.Release(); }
    }

    private async Task Suspend(string sandboxId)
    {
        var gate = _locks.GetOrAdd(sandboxId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            _started.TryRemove(sandboxId, out _);
            await RemoveContainer(sandboxId);
            await CloseWay(sandboxId);
            Log($"[{_pool.Name}] {sandboxId}: suspended; volume {Name(sandboxId)} kept");
            await Report(new SandboxStateReport(sandboxId, SandboxStates.Suspended));
        }
        catch (Exception e) { Log($"[{_pool.Name}] {sandboxId}: couldn't suspend: {e.Message}"); }
        finally { gate.Release(); }
    }

    private async Task Delete(DeleteSandbox d)
    {
        var sandboxId = d.SandboxId;
        var gate = _locks.GetOrAdd(sandboxId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            _started.TryRemove(sandboxId, out _);
            await RemoveContainer(sandboxId);
            await CloseWay(sandboxId);
            var name = Name(sandboxId);
            if (d.KeepIfChanged && d.Image is { } image && await Changes(name, image, d.BaseBranch ?? "main") is { } changes)
            {
                Log($"[{_pool.Name}] {sandboxId}: kept: {changes}");
                await Report(new SandboxStateReport(sandboxId, SandboxStates.Kept, changes));
                return;
            }
            if (await LabelOf("volume", name) == _key) await Must(_podman, TimeSpan.FromMinutes(2), "volume", "rm", name);
            Log($"[{_pool.Name}] {sandboxId}: deleted with its volume");
            await Report(new SandboxStateReport(sandboxId, SandboxStates.Deleted));
        }
        catch (Exception e) { Log($"[{_pool.Name}] {sandboxId}: couldn't delete: {e.Message}"); }
        finally { gate.Release(); }
    }

    /// <summary>What the thread's branch has that the project's branch doesn't: changes to tracked files, or commits. Null when it has none
    /// (untracked files, such as what a setup command installed, don't count). Looked at by a short-lived container with no network.</summary>
    private async Task<string?> Changes(string volume, string image, string baseBranch)
    {
        if (await LabelOf("volume", volume) != _key) return null;
        const string Script = """
            cd /work/repo 2>/dev/null && [ -d .git ] || { echo "0 0"; exit 0; }
            export HOME=/tmp
            git config --global --add safe.directory /work/repo
            d=$(git status --porcelain --untracked-files=no | wc -l)
            c=$(git rev-list --count "origin/$BASE..HEAD" 2>/dev/null || echo 0)
            echo "$d $c $(git rev-parse --abbrev-ref HEAD)"
            """;
        var (code, o, err) = await Run(_podman, null, TimeSpan.FromMinutes(2), "run", "--rm", "--label", $"cowork.pool={_key}", "--network", "none", "--entrypoint", "/bin/sh",
            "-v", $"{volume}:/work", "-e", $"BASE={baseBranch}", image, "-c", Script);
        if (code != 0) return $"couldn't look at its branch ({err.Trim()}), so its volume is kept";
        var parts = o.Trim().Split(' ');
        var dirty = int.TryParse(parts.ElementAtOrDefault(0), out var x) ? x : 0; var commits = int.TryParse(parts.ElementAtOrDefault(1), out var y) ? y : 0;
        var branch = parts.ElementAtOrDefault(2) ?? "its branch";
        if (dirty == 0 && commits == 0) return null;
        var what = new List<string>();
        if (commits > 0) what.Add($"{commits} commit{(commits == 1 ? "" : "s")} not on {baseBranch}");
        if (dirty > 0) what.Add($"{dirty} changed file{(dirty == 1 ? "" : "s")} not committed");
        return $"{branch} has {string.Join(" and ", what)}, so its volume is kept";
    }

    /// <summary>Stops and removes the sandbox's container, if there is one and it carries this pool's label. Never anything else.</summary>
    private async Task RemoveContainer(string sandboxId)
    {
        var name = Name(sandboxId);
        if (await LabelOf("container", name) != _key) return;
        await Run(_podman, null, TimeSpan.FromSeconds(40), "stop", "-t", "10", name);
        await Must(_podman, TimeSpan.FromSeconds(40), "rm", "-f", name);
    }

    private async Task<string?> LabelOf(string kind, string name)
    {
        var (code, o, _) = await Run(_podman, null, TimeSpan.FromSeconds(20), kind, "inspect", "--format", kind == "volume" ? "{{index .Labels \"cowork.pool\"}}" : "{{index .Config.Labels \"cowork.pool\"}}", name);
        return code == 0 ? o.Trim() : null;
    }

    /// <summary>The hosts a sandbox may reach (the project's list and what was allowed for its thread); the server and gateway always.</summary>
    public static IEnumerable<string> Hosts(SandboxSpec s) => s.Network.Rules.SelectMany(r => r.Hosts).Where(h => h is not ("cowork-server" or "cowork-gateway")).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sandbox's proxy, made fresh for each start with the hosts in its command.</summary>
    private async Task<EgressProxy> OpenWay(SandboxSpec s)
    {
        await CloseWay(s.SandboxId);
        var server = new Uri(_pool.Server);
        var p = new EgressProxy(Path.Combine(_root, "sockets", s.SandboxId), server.Host, server.Port, Hosts(s),
            (host, port) => _hub.InvokeAsync("EgressRefused", new EgressRefusal(s.SandboxId, host, port))) { Log = m => Log($"[{_pool.Name}] {s.SandboxId}: {m}") };
        p.Start();
        _proxies[s.SandboxId] = p;
        return p;
    }

    private async Task CloseWay(string sandboxId)
    {
        if (_proxies.TryRemove(sandboxId, out var p)) await p.DisposeAsync();
    }

    /// <summary>A failure as the thread shows it: its first two lines, at most 300 characters.</summary>
    private static string Short(string s)
    {
        var lines = string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2));
        return lines.Length > 300 ? lines[..300] + "…" : lines;
    }
    private static string Hash8(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
    private static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        return sb.Length == 0 ? "repo" : sb.ToString();
    }

    private static async Task<bool> Ok(string exe, params string[] args) => (await Run(exe, null, TimeSpan.FromSeconds(30), args)).Code == 0;
    private static Task Must(string exe, TimeSpan timeout, params string[] args) => Must(exe, timeout, args, null);
    private static async Task Must(string exe, TimeSpan timeout, string[] args, Dictionary<string, string>? env)
    {
        var (code, _, err) = await Run(exe, env, timeout, args);
        if (code != 0) throw new InvalidOperationException($"{exe} {args[0]} failed: {err.Trim()}");
    }

    private static async Task<(int Code, string Out, string Err)> Run(string exe, Dictionary<string, string>? env, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {exe}.");
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } throw new InvalidOperationException($"{exe} {args[0]} took longer than {timeout.TotalSeconds:0} s."); }
        return (p.ExitCode, await o, await e);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var id in _proxies.Keys.ToList()) await CloseWay(id);
        await _hub.DisposeAsync();
    }

    private sealed class Backoff : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext c) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(c.PreviousRetryCount, 5))));
    }
}
