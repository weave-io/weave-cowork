using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CoWork.Contracts;

namespace CoWork.Runner;

/// <summary>Where a thread's session works, as prepared on this machine. Place: worktree | folder | scratch | agent.</summary>
public sealed record PreparedWorkspace(string Cwd, string Place, string? Branch, string Location, long? WorkspaceId);

/// <summary>
/// Makes the place a thread works in, under the agent's own folder on this runner (<c>&lt;runner data&gt;/&lt;server&gt;-&lt;agent&gt;</c>):
/// <list type="bullet">
/// <item>a repository is cloned once per agent and repository (<c>repos/&lt;name&gt;-&lt;hash&gt;</c>), fetched before each new thread, and every thread
/// gets its own worktree on its own branch (<c>worktrees/&lt;name&gt;-&lt;hash&gt;/&lt;slug&gt;</c> on <c>cowork/&lt;slug&gt;</c>) from the project's branch;</item>
/// <item>a git folder gets a worktree per thread too (from what is checked out there); any other folder is used as it is;</item>
/// <item>no workspace: an empty folder per thread (<c>projects/&lt;id&gt;/threads/&lt;slug&gt;</c>);</item>
/// <item>a channel outside any project: the agent's own folder, as before.</item>
/// </list>
/// The same thread finds the same place again (after a runner restart too), so <c>session/load</c> gets the cwd it had. Each worktree has a
/// pre-push hook that refuses a push to the project's branch. Worktrees are kept: removing them is a later job.
/// </summary>
public sealed class Workspaces
{
    private readonly string _root;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    public Action<string>? Log;

    public Workspaces(string agentRoot) { _root = agentRoot; }
    public string Root => _root;

    public async Task<PreparedWorkspace> Prepare(DeliverMessage m)
    {
        if (m.ThreadWorkspace is not { } w)
        {
            var own = Path.GetFullPath(m.Workspace);
            Directory.CreateDirectory(own);
            return new(own, "agent", null, own, null);
        }
        var slug = Slug(w.Branch);
        switch (w.Kind)
        {
            case "repository": return await Repository(m.ThreadId, w, slug);
            case "folder": return await Folder(m.ThreadId, w, slug);
            default:
                var d = Path.Combine(_root, "projects", (m.ProjectId ?? 0).ToString(), "threads", slug);
                Directory.CreateDirectory(d);
                return new(d, "scratch", null, d, null);
        }
    }

    /// <summary>The folder a harness process for this project starts in (sessions each have their own cwd).</summary>
    public string ProjectFolder(long projectId)
    {
        var d = Path.Combine(_root, "projects", projectId.ToString());
        Directory.CreateDirectory(d);
        return d;
    }

    private async Task<PreparedWorkspace> Repository(long thread, ThreadWorkspace w, string slug)
    {
        var url = w.Url ?? throw new InvalidOperationException("The repository has no address.");
        var baseBranch = string.IsNullOrWhiteSpace(w.BaseBranch) ? "main" : w.BaseBranch;
        var key = $"{Safe(w.Name)}-{Hash8(url)}";
        var clone = Path.Combine(_root, "repos", key);
        var wt = Path.Combine(_root, "worktrees", key, slug);
        var gate = Locks.GetOrAdd(clone, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!Directory.Exists(Path.Combine(clone, ".git")))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(clone)!);
                Log?.Invoke($"cloning {url} into {clone}");
                await Git(null, TimeSpan.FromMinutes(10), "clone", "--no-checkout", "--", url, clone);
            }
            if (!Directory.Exists(wt))
            {
                await Git(clone, TimeSpan.FromMinutes(5), "fetch", "--prune", "origin");          // a new thread starts from the project's branch as it is now
                await AddWorktree(clone, wt, w.Branch, $"origin/{baseBranch}");
                await Protect(clone, wt, baseBranch);
                Log?.Invoke($"thread {thread}: worktree {wt} on {w.Branch} (kept after the thread; removing worktrees is a later job)");
            }
        }
        finally { gate.Release(); }
        return new(wt, "worktree", w.Branch, clone, w.WorkspaceId);
    }

    private async Task<PreparedWorkspace> Folder(long thread, ThreadWorkspace w, string slug)
    {
        var path = Path.GetFullPath(w.Path ?? throw new InvalidOperationException("The folder has no path."));
        if (!Directory.Exists(path)) throw new InvalidOperationException($"The folder {path} isn't on this machine.");
        var top = (await TryGit(path, "rev-parse", "--show-toplevel"))?.Trim();
        if (string.IsNullOrEmpty(top)) return new(path, "folder", null, path, w.WorkspaceId);      // not git: the folder itself, shared by threads
        var key = $"folder-{Safe(w.Name)}-{Hash8(top)}";
        var wt = Path.Combine(_root, "worktrees", key, slug);
        var gate = Locks.GetOrAdd(top, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!Directory.Exists(wt))
            {
                var current = (await TryGit(top, "rev-parse", "--abbrev-ref", "HEAD"))?.Trim();
                var protect = !string.IsNullOrWhiteSpace(w.BaseBranch) ? w.BaseBranch : current is null or "HEAD" ? "main" : current;
                await AddWorktree(top, wt, w.Branch, "HEAD");
                await Protect(top, wt, protect);
                Log?.Invoke($"thread {thread}: worktree {wt} of {top} on {w.Branch} (kept after the thread; removing worktrees is a later job)");
            }
        }
        finally { gate.Release(); }
        var rel = Path.GetRelativePath(top, path);
        return new(rel == "." ? wt : Path.Combine(wt, rel), "worktree", w.Branch, top, w.WorkspaceId);
    }

    /// <summary>A worktree on the thread's own branch: a new branch from <paramref name="from"/>, or the branch again if it is already there.</summary>
    private static async Task AddWorktree(string repo, string wt, string branch, string from)
    {
        await Git(repo, TimeSpan.FromMinutes(1), "worktree", "prune");                   // a worktree folder deleted by hand would block the path
        Directory.CreateDirectory(Path.GetDirectoryName(wt)!);
        if (await TryGit(repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch) is not null)
            await Git(repo, TimeSpan.FromMinutes(2), "worktree", "add", wt, branch);
        else
            await Git(repo, TimeSpan.FromMinutes(2), "worktree", "add", "-b", branch, wt, from);
    }

    /// <summary>The worktree's own hooks folder (a per-worktree setting, so the repository's other worktrees and the person's own checkout keep
    /// theirs) with a pre-push hook that refuses pushing to the project's branch.</summary>
    private async Task Protect(string repo, string wt, string protectedBranch)
    {
        var hooks = Path.Combine(Path.GetDirectoryName(_root)!, "hooks");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "pre-push");
        if (!File.Exists(hook) || File.ReadAllText(hook) != PrePush)
        {
            File.WriteAllText(hook, PrePush);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
        await Git(repo, TimeSpan.FromSeconds(30), "config", "extensions.worktreeConfig", "true");
        await Git(wt, TimeSpan.FromSeconds(30), "config", "--worktree", "core.hooksPath", hooks);
        await Git(wt, TimeSpan.FromSeconds(30), "config", "--worktree", "cowork.protectedBranch", protectedBranch);
    }

    public const string PrePush = """
        #!/bin/sh
        # cowork-runner: a thread's worktree never pushes to the project's branch. Push the thread's own branch instead.
        protected=$(git config --get cowork.protectedBranch || echo main)
        while read local_ref local_sha remote_ref remote_sha; do
          if [ "$remote_ref" = "refs/heads/$protected" ]; then
            echo "cowork: pushing to $protected isn't allowed from a thread's worktree. Push $(git rev-parse --abbrev-ref HEAD) and open a pull request." >&2
            exit 1
          fi
        done
        exit 0

        """;

    private static string Slug(string branch) => Safe(branch.StartsWith("cowork/") ? branch[7..] : branch);
    private static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        var r = sb.ToString().Trim('.', '-');
        return r.Length == 0 ? "x" : r;
    }
    private static string Hash8(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();

    // ---- git ----

    private static async Task<string> Git(string? cwd, TimeSpan timeout, params string[] args)
    {
        var (code, stdout, stderr) = await Run(cwd, timeout, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args.Where(a => a != "--"))} failed: {stderr.Trim()}");
        return stdout;
    }
    private static async Task<string?> TryGit(string cwd, params string[] args)
    {
        try { var (code, stdout, _) = await Run(cwd, TimeSpan.FromSeconds(30), args); return code == 0 ? stdout : null; }
        catch (Exception) { return null; }
    }

    private static async Task<(int Code, string Out, string Err)> Run(string? cwd, TimeSpan timeout, string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (cwd is not null) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";        // a repository that needs a password fails instead of waiting for one
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start git.");
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } throw new InvalidOperationException($"git {args[0]} took longer than {timeout.TotalMinutes:0.#} minutes."); }
        return (p.ExitCode, await o, await e);
    }
}
