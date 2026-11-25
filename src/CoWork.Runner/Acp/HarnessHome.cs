namespace CoWork.Runner.Acp;

/// <summary>
/// "Remembers": what a harness keeps for an agent in one project stays apart from what it keeps in others. A harness process serves one
/// (agent, project), and gets its own data and state folders: <c>&lt;agent folder&gt;/projects/&lt;id&gt;/harness/{data,state}</c> as
/// <c>XDG_DATA_HOME</c> and <c>XDG_STATE_HOME</c>.
///
/// What OpenCode 2.4.1 keeps where (checked on this machine, 25 November 2026):
/// <list type="bullet">
/// <item><c>XDG_DATA_HOME/opencode</c>: <c>opencode.db</c> (sessions and their messages, projects, remembered "always allow" permissions, worktrees),
/// <c>auth.json</c> (the sign-in: provider keys and OAuth tokens from <c>opencode auth login</c>), <c>log/</c>, <c>snapshot/</c>, <c>tool-output/</c>;</item>
/// <item><c>XDG_STATE_HOME/opencode</c>: recent models and prompt history;</item>
/// <item><c>XDG_CONFIG_HOME/opencode</c>: <c>opencode.json</c> (settings, permission rules) and the person's own global instructions;</item>
/// <item><c>XDG_CACHE_HOME/opencode</c>: downloaded tools and the model list.</item>
/// </list>
/// So data and state move per project (memory, history and remembered permissions stay in the project), while config and cache stay shared
/// (the owner's settings apply everywhere; downloads aren't repeated). The sign-in lives beside the memory, so <c>auth.json</c> is linked from the
/// runner's own data folder into each project's: signing in once (or again, when a token is refreshed) works for every project. HOME isn't
/// changed: other harnesses keep their sign-in under it.
/// </summary>
public static class HarnessHome
{
    /// <summary>Sign-in files to share from the runner's own data folder, relative to <c>XDG_DATA_HOME</c>.</summary>
    public static readonly string[] SignIns = { "opencode/auth.json", "opencode/mcp-auth.json" };

    public static Dictionary<string, string> For(string agentRoot, long projectId, Action<string>? log = null, string? ownDataHome = null)
    {
        var home = Path.Combine(agentRoot, "projects", projectId.ToString(), "harness");
        var data = Path.Combine(home, "data"); var state = Path.Combine(home, "state");
        Directory.CreateDirectory(data); Directory.CreateDirectory(state);
        var own = ownDataHome ?? RunnerDataHome();
        foreach (var rel in SignIns)
        {
            var src = Path.Combine(own, rel); var dst = Path.Combine(data, rel);
            if (!File.Exists(src) || File.Exists(dst) || Directory.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.CreateSymbolicLink(dst, src); }
            catch (Exception e) { log?.Invoke($"couldn't link the sign-in {rel} into project {projectId}'s data folder ({e.Message})"); }
        }
        return new() { ["XDG_DATA_HOME"] = data, ["XDG_STATE_HOME"] = state };
    }

    /// <summary>The runner's own XDG data folder: where the harness keeps its sign-in when it runs as the runner's user.</summary>
    public static string RunnerDataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : xdg;
    }
}
