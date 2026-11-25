using System.Diagnostics;
using CoWork.Contracts;
using CoWork.Runner;
using CoWork.Runner.Acp;

namespace CoWork.Runner.Tests;

/// <summary>Brief 5: a thread in a project works in its own place: a worktree on its own branch of a clone made once per agent and repository,
/// a folder, or an empty folder; its harness keeps its data per project. Real git, a fake ACP agent.</summary>
public class WorkspaceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"cowork-ws-{Guid.NewGuid():N}");
    public WorkspaceTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in new[] { ("GIT_AUTHOR_NAME", "Alex"), ("GIT_AUTHOR_EMAIL", "alex@lumen.example"), ("GIT_COMMITTER_NAME", "Alex"), ("GIT_COMMITTER_EMAIL", "alex@lumen.example") }) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {e}");
        return o.Trim() + e.Trim();
    }
    static (int Code, string Err) TryGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!; p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit();
        return (p.ExitCode, e);
    }

    /// <summary>An invented checkout-app: a bare repository with README.md and src/tax.ts on main.</summary>
    string BareRepo()
    {
        var work = Path.Combine(_dir, "seed"); Directory.CreateDirectory(Path.Combine(work, "src"));
        Git(work, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(work, "README.md"), "# checkout-app\n");
        File.WriteAllText(Path.Combine(work, "src", "tax.ts"), "export const roundTax = (n: number) => Math.round(n * 100) / 100\n");
        Git(work, "add", "."); Git(work, "commit", "-q", "-m", "Cart and tax");
        var bare = Path.Combine(_dir, "checkout-app.git");
        Git(_dir, "clone", "-q", "--bare", work, bare);
        return bare;
    }

    sealed class Sink : IRunnerSink
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<StreamEvent> Events = new();
        public Task Stream(StreamEvent e) { Events.Enqueue(e); return Task.CompletedTask; }
        public Task<string?> AskPermission(PermissionRequestMsg r) => Task.FromResult<string?>("allow");
        public string Said(long thread) => string.Concat(Events.Where(e => e.ThreadId == thread && e.Kind == StreamKinds.Text).Select(e => e.Text));
        public string LastSaid(long thread) => Events.Where(e => e.ThreadId == thread && e.Kind == StreamKinds.Text).Last().Text!;
    }

    static DeliverMessage Msg(long thread, string text, long? project, ThreadWorkspace? ws) =>
        new(thread * 10, thread, 1, "reviewer", "", new[] { "python3", "-I", Path.Combine(AppContext.BaseDirectory, "fake_acp_agent.py") }, Path.GetTempPath(), text, false, project, ws);

    static ThreadWorkspace Repo(string url, string branch) => new(3, "app", "repository", url, "main", null, branch);

    static string Field(string said, string name) => said.Split(' ').First(p => p.StartsWith(name + "=")).Split('=', 2)[1].Replace("LOADED", "");

    [Fact]
    public async Task Two_threads_on_a_repository_work_side_by_side_in_their_own_worktrees_on_their_own_branches()
    {
        var bare = BareRepo();
        var root = Path.Combine(_dir, "runner", "srv-1");
        var sink = new Sink();
        await using (var adapter = new AcpAdapter(sink, agentRoot: root))
        {
            await Task.WhenAll(
                adapter.Deliver(Msg(11, "WRITE:rosa.txt WHERE", 7, Repo(bare, "cowork/rounding-11"))),
                adapter.Deliver(Msg(12, "WRITE:sam.txt WHERE", 7, Repo(bare, "cowork/cart-12"))));
            var cwd11 = Field(sink.Said(11), "cwd"); var cwd12 = Field(sink.Said(12), "cwd");
            Assert.NotEqual(cwd11, cwd12);
            Assert.True(File.Exists(Path.Combine(cwd11, "rosa.txt"))); Assert.False(File.Exists(Path.Combine(cwd11, "sam.txt")));
            Assert.True(File.Exists(Path.Combine(cwd12, "sam.txt"))); Assert.False(File.Exists(Path.Combine(cwd12, "rosa.txt")));
            Assert.True(File.Exists(Path.Combine(cwd11, "src", "tax.ts")));                       // the project's branch, checked out
            Assert.Equal("cowork/rounding-11", Git(cwd11, "rev-parse", "--abbrev-ref", "HEAD"));
            Assert.Equal("cowork/cart-12", Git(cwd12, "rev-parse", "--abbrev-ref", "HEAD"));
            // one clone for the agent and repository; both threads' worktrees belong to it
            Assert.Single(Directory.GetDirectories(Path.Combine(root, "repos")));
            Assert.Equal(Git(cwd11, "rev-parse", "--git-common-dir"), Git(cwd12, "rev-parse", "--git-common-dir"));
            // one harness process for the project, with its own data folder
            Assert.Equal(Field(sink.Said(11), "pid"), Field(sink.Said(12), "pid"));
            Assert.Equal(Path.Combine(root, "projects", "7", "harness", "data"), Field(sink.Said(11), "data"));
            // the runner tells the server where each thread works
            var ws = sink.Events.Where(e => e.Kind == StreamKinds.Workspace).ToDictionary(e => e.ThreadId);
            Assert.Equal(("worktree", "cowork/rounding-11", 3L), (ws[11].Status, ws[11].Title, ws[11].WorkspaceId!.Value));
            Assert.StartsWith(Path.Combine(root, "repos"), ws[11].Text);

            // the pre-push hook: the thread's own branch may be pushed; the project's branch may not
            Git(cwd11, "add", "rosa.txt");
            Git(cwd11, "-c", "user.name=reviewer", "-c", "user.email=reviewer@lumen.example", "commit", "-q", "-m", "Notes");
            var main = TryGit(cwd11, "push", "origin", "HEAD:main");
            Assert.NotEqual(0, main.Code);
            Assert.Contains("pushing to main isn't allowed", main.Err);
            Assert.Equal(0, TryGit(cwd11, "push", "-q", "origin", "cowork/rounding-11").Code);
            Assert.Equal("Cart and tax", Git(_dir, "--git-dir", bare, "log", "-1", "--format=%s", "main"));        // main is as it was
            Assert.Equal("Notes", Git(_dir, "--git-dir", bare, "log", "-1", "--format=%s", "cowork/rounding-11"));
        }

        // a restarted runner: the same thread finds the same worktree, and its session is loaded there
        var store = new SessionStore(Path.Combine(_dir, "sessions.json"));
        var sink2 = new Sink();
        await using (var a2 = new AcpAdapter(sink2, store, root)) await a2.Deliver(Msg(13, "WHERE", 7, Repo(bare, "cowork/later-13")));
        var first = Field(sink2.Said(13), "cwd");
        var sink3 = new Sink();
        await using (var a3 = new AcpAdapter(sink3, store, root)) await a3.Deliver(Msg(13, "WHERE again", 7, Repo(bare, "cowork/later-13")));
        Assert.Contains("LOADED", sink3.LastSaid(13));
        Assert.Equal(first, Field(sink3.LastSaid(13), "cwd"));
    }

    [Fact]
    public async Task A_new_thread_starts_from_the_project_branch_as_it_is_now()
    {
        var bare = BareRepo();
        var root = Path.Combine(_dir, "runner", "srv-1");
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink, agentRoot: root);
        await adapter.Deliver(Msg(1, "WHERE", 7, Repo(bare, "cowork/one-1")));
        // someone pushes to main after the clone
        var seed = Path.Combine(_dir, "seed");
        File.WriteAllText(Path.Combine(seed, "CHANGELOG.md"), "Rounding fixed\n");
        Git(seed, "add", "."); Git(seed, "commit", "-q", "-m", "Changelog"); Git(seed, "push", "-q", bare, "main");
        await adapter.Deliver(Msg(2, "WHERE", 7, Repo(bare, "cowork/two-2")));
        Assert.False(File.Exists(Path.Combine(Field(sink.Said(1), "cwd"), "CHANGELOG.md")));
        Assert.True(File.Exists(Path.Combine(Field(sink.Said(2), "cwd"), "CHANGELOG.md")));
    }

    [Fact]
    public async Task A_git_folder_gets_worktrees_a_plain_folder_is_shared_and_no_workspace_is_an_empty_folder_and_projects_keep_apart()
    {
        var root = Path.Combine(_dir, "runner", "srv-1");
        var gitFolder = Path.Combine(_dir, "specs"); Directory.CreateDirectory(gitFolder);
        Git(gitFolder, "init", "-q", "-b", "trunk"); File.WriteAllText(Path.Combine(gitFolder, "spec.md"), "refunds\n"); Git(gitFolder, "add", "."); Git(gitFolder, "commit", "-q", "-m", "Spec");
        var plain = Path.Combine(_dir, "data-folder"); Directory.CreateDirectory(plain);
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink, agentRoot: root);
        await adapter.Deliver(Msg(1, "WRITE:a.txt WHERE", 7, new ThreadWorkspace(4, "specs", "folder", null, null, gitFolder, "cowork/specs-1")));
        await adapter.Deliver(Msg(2, "WRITE:b.txt WHERE", 7, new ThreadWorkspace(5, "data", "folder", null, null, plain, "cowork/data-2")));
        await adapter.Deliver(Msg(3, "WHERE", 8, new ThreadWorkspace(null, "none", "none", null, null, null, "cowork/talk-3")));
        var cwd1 = Field(sink.Said(1), "cwd");
        Assert.NotEqual(gitFolder, cwd1);
        Assert.Equal("cowork/specs-1", Git(cwd1, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.False(File.Exists(Path.Combine(gitFolder, "a.txt")));                                   // the person's own checkout is untouched
        Assert.Equal("trunk", Git(gitFolder, "rev-parse", "--abbrev-ref", "HEAD"));
        Git(cwd1, "add", "a.txt"); Git(cwd1, "-c", "user.name=reviewer", "-c", "user.email=reviewer@lumen.example", "commit", "-q", "-m", "A note");
        var push = TryGit(cwd1, "push", gitFolder, "HEAD:trunk");
        Assert.NotEqual(0, push.Code); Assert.Contains("pushing to trunk isn't allowed", push.Err);    // protected: the branch it came from
        Assert.Equal(plain, Field(sink.Said(2), "cwd"));
        Assert.True(File.Exists(Path.Combine(plain, "b.txt")));
        Assert.Equal(Path.Combine(root, "projects", "8", "threads", "talk-3"), Field(sink.Said(3), "cwd"));
        // another project: another harness process, another data folder
        Assert.NotEqual(Field(sink.Said(1), "pid"), Field(sink.Said(3), "pid"));
        Assert.Equal(Path.Combine(root, "projects", "8", "harness", "data"), Field(sink.Said(3), "data"));
        var place = sink.Events.Where(e => e.Kind == StreamKinds.Workspace).ToDictionary(e => e.ThreadId, e => e.Status);
        Assert.Equal(("worktree", "folder", "scratch"), (place[1], place[2], place[3]));
    }

    [Fact]
    public async Task Outside_a_project_the_agent_works_in_its_own_folder_as_before()
    {
        var own = Path.Combine(_dir, "own");
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink, agentRoot: Path.Combine(_dir, "runner", "srv-1"));
        await adapter.Deliver(Msg(1, "WHERE", null, null) with { Workspace = own });
        Assert.Equal(own, Field(sink.Said(1), "cwd"));
        Assert.DoesNotContain(sink.Events, e => e.Kind == StreamKinds.Workspace);
        var inherited = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Assert.Equal(string.IsNullOrEmpty(inherited) ? "-" : inherited, Field(sink.Said(1), "data"));      // its harness data where it always was
    }

    [Fact]
    public void The_sign_in_is_shared_into_each_project_s_data_folder_and_the_rest_is_kept_apart()
    {
        var own = Path.Combine(_dir, "own-data"); Directory.CreateDirectory(Path.Combine(own, "opencode"));
        File.WriteAllText(Path.Combine(own, "opencode", "auth.json"), "{\"github-copilot\":{\"type\":\"oauth\"}}");
        var root = Path.Combine(_dir, "runner", "srv-1");
        var a = HarnessHome.For(root, 7, ownDataHome: own); var b = HarnessHome.For(root, 8, ownDataHome: own);
        Assert.NotEqual(a["XDG_DATA_HOME"], b["XDG_DATA_HOME"]); Assert.NotEqual(a["XDG_STATE_HOME"], b["XDG_STATE_HOME"]);
        Assert.False(a.ContainsKey("HOME")); Assert.False(a.ContainsKey("XDG_CONFIG_HOME"));
        var linked = new FileInfo(Path.Combine(a["XDG_DATA_HOME"], "opencode", "auth.json"));
        Assert.Equal(Path.Combine(own, "opencode", "auth.json"), linked.LinkTarget);
        Assert.Contains("github-copilot", File.ReadAllText(linked.FullName));
        Assert.Equal(a, HarnessHome.For(root, 7, ownDataHome: own));          // again: the same folders, no error
    }
}
