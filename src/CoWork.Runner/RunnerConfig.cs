using System.Text.Json;

namespace CoWork.Runner;

public record SavedAgent(string Server, long AgentId, string Handle, string Token);

/// <summary>The agents this runner serves: {server, agentId, token} in ~/.config/cowork-runner/agents.json (respects XDG_CONFIG_HOME).</summary>
public sealed class RunnerConfig
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string Path { get; }
    public RunnerConfig(string? path = null)
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var dir = string.IsNullOrEmpty(xdg) ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
        Path = path ?? System.IO.Path.Combine(dir, "cowork-runner", "agents.json");
    }

    /// <summary>Where the runner keeps agents' clones, worktrees and per-project harness data: COWORK_RUNNER_DATA, else XDG_DATA_HOME/cowork-runner,
    /// else ~/.local/share/cowork-runner.</summary>
    public static string DataRoot()
    {
        var own = Environment.GetEnvironmentVariable("COWORK_RUNNER_DATA");
        if (!string.IsNullOrEmpty(own)) return own;
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return System.IO.Path.Combine(string.IsNullOrEmpty(xdg) ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : xdg, "cowork-runner");
    }

    public List<SavedAgent> Load()
    {
        if (!File.Exists(Path)) return new();
        return JsonSerializer.Deserialize<List<SavedAgent>>(File.ReadAllText(Path), Json) ?? new();
    }

    public void Save(List<SavedAgent> agents)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(agents, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // it holds tokens
        File.Move(tmp, Path, overwrite: true);
    }

    public void Add(SavedAgent a)
    {
        var all = Load().Where(x => !(x.Server == a.Server && x.AgentId == a.AgentId)).ToList();
        all.Add(a); Save(all);
    }
    public void Remove(SavedAgent a) => Save(Load().Where(x => !(x.Server == a.Server && x.AgentId == a.AgentId)).ToList());
}
