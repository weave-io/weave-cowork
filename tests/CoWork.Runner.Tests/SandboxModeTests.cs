using System.Diagnostics;
using System.Text.Json;
using CoWork.Contracts;
using CoWork.Runner;
using CoWork.Runner.Acp;

namespace CoWork.Runner.Tests;

/// <summary>Brief 7: the pool and the runner inside a sandbox. Podman itself is checked by the live check (phase9).</summary>
public class SandboxModeTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"cowork-sb-{Guid.NewGuid():N}");
    public SandboxModeTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void Container_names_and_hosts_from_the_command()
    {
        Assert.Equal("cowork-ab12cd34-p1-sb-7", PoolLink.ContainerName("ab12cd34-p1", "sb-7"));
        var spec = new SandboxSpec("sb-7", 42, 7, "researcher", 9, new SandboxTemplate("img", new[] { "opencode", "acp" }, "m"), new SandboxWorkspace("none", null, null, null, "cowork/x"),
            new SandboxLimits(1500), new EgressPolicy(new[] { new EgressRule(new[] { "cowork-server", "cowork-gateway" }), new EgressRule(new[] { "github.com", "*.npmjs.org", "GitHub.com" }) }), "t");
        Assert.Equal(new[] { "github.com", "*.npmjs.org" }, PoolLink.Hosts(spec));
    }

    [Theory]
    [InlineData("1500M", 1500L)] [InlineData("2G", 2048L)] [InlineData("2gb", 2048L)] [InlineData("512", 512L)] [InlineData("lots", null)] [InlineData("-1M", null)]
    public void Memory_sizes(string s, long? mb) => Assert.Equal(mb, PoolConfig.MemoryMb(s));

    [Fact]
    public void A_sandbox_home_gets_CoWork_s_skills_where_Claude_Code_Codex_and_OpenCode_look_and_a_changed_text_is_written_again()
    {
        var files = HarnessSkills.Install(_dir);
        Assert.Equal(new[] { Path.Combine(_dir, ".claude", "skills", "cowork-design", "SKILL.md"), Path.Combine(_dir, ".agents", "skills", "cowork-design", "SKILL.md") }, files);
        Assert.All(files, f => Assert.Equal(Skills.Design.Text, File.ReadAllText(f)));
        Assert.StartsWith("---\nname: cowork-design\n", File.ReadAllText(files[0]));
        File.WriteAllText(files[1], "an older text");
        HarnessSkills.Install(_dir);
        Assert.Equal(Skills.Design.Text, File.ReadAllText(files[1]));
    }

    [Fact]
    public void OpenCode_gets_the_gateway_as_its_only_provider_and_the_token_by_name_not_by_value()
    {
        var path = GatewayConfig.WriteOpenCode(_dir, "http://host.containers.internal:5884/gateway/v1/", "claude-haiku-4.5");
        var cfg = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal("cowork/claude-haiku-4.5", cfg.GetProperty("model").GetString());
        Assert.Equal(new[] { "cowork" }, cfg.GetProperty("enabled_providers").EnumerateArray().Select(x => x.GetString()));
        var p = cfg.GetProperty("provider").GetProperty("cowork");
        Assert.Equal("@ai-sdk/openai-compatible", p.GetProperty("npm").GetString());
        Assert.Equal("http://host.containers.internal:5884/gateway/v1", p.GetProperty("options").GetProperty("baseURL").GetString());
        Assert.Equal("{env:COWORK_THREAD_TOKEN}", p.GetProperty("options").GetProperty("apiKey").GetString());
        Assert.True(p.GetProperty("models").TryGetProperty("claude-haiku-4.5", out _));
    }

    sealed class Sink : IRunnerSink
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<StreamEvent> Events = new();
        public Task Stream(StreamEvent e) { Events.Enqueue(e); return Task.CompletedTask; }
        public Task<string?> AskPermission(PermissionRequestMsg r) => Task.FromResult<string?>("allow");
    }

    [Fact]
    public async Task Inside_a_sandbox_every_message_works_in_the_workspace_the_pool_made_and_says_so()
    {
        var repo = Path.Combine(_dir, "work", "repo"); Directory.CreateDirectory(repo);
        void Git(params string[] a) { var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardError = true, RedirectStandardOutput = true }; foreach (var x in a) psi.ArgumentList.Add(x); using var pr = Process.Start(psi)!; pr.WaitForExit(); }
        Git("init", "-q", "-b", "cowork/compare-tax-apis-42");
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink, new SessionStore(Path.Combine(_dir, "sessions.json")), Path.Combine(_dir, "runner"), sandboxWorkspace: repo);
        var ws = new ThreadWorkspace(3, "app", "repository", "/srv/git/checkout-app.git", "main", null, "cowork/compare-tax-apis-42");
        await adapter.Deliver(new DeliverMessage(1, 42, 7, "researcher", "", new[] { "python3", "-I", Path.Combine(AppContext.BaseDirectory, "fake_acp_agent.py") }, "", "WHERE", false, 9, ws));
        for (var i = 0; i < 300 && !sink.Events.Any(e => e.Kind == StreamKinds.TurnEnded); i++) await Task.Delay(20);
        var where = sink.Events.Single(e => e.Kind == StreamKinds.Workspace);
        Assert.Equal(("sandbox", "cowork/compare-tax-apis-42", repo), (where.Status, where.Title, where.Text));
        var said = string.Concat(sink.Events.Where(e => e.Kind == StreamKinds.Text).Select(e => e.Text));
        Assert.Contains($"cwd={repo}", said);
        Assert.DoesNotContain("/projects/9/", said);          // no per-project data folder: the sandbox is the thread's own
        Assert.False(Directory.Exists(Path.Combine(_dir, "runner", "worktrees")));
    }
}
