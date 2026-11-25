using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

public class ProjectRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string About { get; set; } = "";
    public long CreatedBy { get; set; }
    public long CreatedAt { get; set; }
}

public class WorkspaceRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "none";
    public string? Url { get; set; }
    public string? Branch { get; set; }
    public string? Path { get; set; }
    public string? Host { get; set; }
    public long? AddedBy { get; set; }

    /// <summary>A repository can be cloned anywhere; a folder only by an agent whose runner is on its machine.</summary>
    public bool ReachableFrom(string? runnerHost) => Kind switch
    {
        "repository" => true,
        "folder" => runnerHost is not null && string.Equals(Host, runnerHost, StringComparison.OrdinalIgnoreCase),
        _ => true,
    };
}

public class ProjectAgentRow
{
    public long AgentId { get; set; }
    public string TakesWorkFrom { get; set; } = "members";
}

public static class Projects
{
    public static ProjectRow? Get(SqliteConnection c, long id) => c.QueryFirstOrDefault<ProjectRow>("SELECT * FROM projects WHERE id=@id", new { id });

    public static List<WorkspaceRow> Workspaces(SqliteConnection c, long projectId) =>
        c.Query<WorkspaceRow>("SELECT * FROM workspaces WHERE project_id=@projectId ORDER BY id", new { projectId }).ToList();

    /// <summary>The workspace a thread in the project works in, for an agent whose runner is on <paramref name="runnerHost"/>: the project's first
    /// workspace that agent can reach (a repository always; a folder only on its own machine). Null when the project has none it can reach.</summary>
    public static WorkspaceRow? WorkspaceFor(IEnumerable<WorkspaceRow> all, string? runnerHost) =>
        all.FirstOrDefault(w => w.Kind != "none" && w.ReachableFrom(runnerHost));

    static readonly Regex Words = new("[a-z0-9]+", RegexOptions.Compiled);
    static readonly Regex Mention = new(@"(?<![\w@])@[a-zA-Z0-9][a-zA-Z0-9_-]*", RegexOptions.Compiled);

    /// <summary>A thread's slug, chosen once when it starts: the first words of its root post (without mentions) and its id, so two threads
    /// never share one. Its branch is <c>cowork/&lt;slug&gt;</c>. "@reviewer fix rounding in refunds" in thread 12 → "fix-rounding-in-refunds-12".</summary>
    public static string Slug(string body, long threadId)
    {
        var words = Words.Matches(Mention.Replace(body ?? "", " ").ToLowerInvariant()).Select(m => m.Value).Take(5).ToList();
        var s = "";
        foreach (var w in words) { if ((s.Length + w.Length + 1) > 40) break; s = s.Length == 0 ? w : s + "-" + w; }
        return s.Length == 0 ? $"thread-{threadId}" : $"{s}-{threadId}";
    }

    public static string Branch(string slug) => "cowork/" + slug;

    /// <summary>"Alex", "Alex and Rosa", "Alex, Rosa and Sam".</summary>
    public static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "nobody",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
