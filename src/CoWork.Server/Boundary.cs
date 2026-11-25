using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>
/// An agent's boundary in a project, in plain sentences (the design's "boundary of an agent"): what it sees, who it takes work from, what it
/// works on, what it reaches, who it asks, what it spends, what it remembers, when it stops. Every sentence is computed from the data the server
/// enforces (the project's channels and members, the grant, the workspaces and the agent's runner), so if a rule changes, so does its sentence.
/// Text between backticks is code (a branch, a path).
/// </summary>
public static class Boundary
{
    public sealed record Line(string Key, string Label, string Text);

    public static object For(SqliteConnection c, ProjectRow project, MemberRow agent, string takesWorkFrom, bool online)
    {
        var owner = agent.OwnerId is { } o ? Q.Member(c, o) : null;
        var ownerName = owner?.DisplayName ?? "its owner";
        var host = agent.RunnerHost;
        var machine = host ?? "its owner's machine";
        var lines = new List<Line>();

        // sees: the project's public channels, the private ones it was added to, the threads it's in; and anything it was added to outside
        var inProject = c.Query<ChannelRow>(@"SELECT * FROM channels WHERE project_id=@p AND (is_private=0 OR id IN (SELECT channel_id FROM channel_members WHERE member_id=@a))
            ORDER BY kind='voice', name", new { p = project.Id, a = agent.Id }).ToList();
        var elsewhere = c.Query<ChannelRow>(@"SELECT * FROM channels WHERE (project_id IS NULL OR project_id<>@p) AND id IN (SELECT channel_id FROM channel_audience WHERE member_id=@a)
            ORDER BY name", new { p = project.Id, a = agent.Id }).ToList();
        string Ch(ChannelRow ch) => ch.Kind == "voice" ? "🔊 " + ch.Name : "#" + ch.Name;
        var sees = inProject.Count == 0 ? $"{project.Name} has no channels yet." : $"{project.Name}'s channels ({Projects.List(inProject.Select(Ch).ToList())}) and the threads it's in.";
        sees += elsewhere.Count == 0 ? " Not other projects or channels." : $" Outside this project, only {Projects.List(elsewhere.Select(Ch).ToList())}, where it was added.";
        lines.Add(new("sees", "Sees", sees));

        // takes work from
        var people = c.Query<string>(@"SELECT m.display_name FROM project_members pm JOIN members m ON m.id=pm.member_id WHERE pm.project_id=@p AND m.removed=0
            ORDER BY pm.role='lead' DESC, m.display_name", new { p = project.Id }).ToList();
        lines.Add(new("takesWorkFrom", "Takes work from", takesWorkFrom == "owner"
            ? $"Only {ownerName}. Anyone else who mentions it gets a short reply: \"{agent.Handle} only takes work from {ownerName}.\""
            : $"Members of {project.Name}: {Projects.List(people)}."));

        // works on
        var all = Projects.Workspaces(c, project.Id);
        var chosen = Projects.WorkspaceFor(all, host);
        var unreachable = all.Where(w => w.Kind == "folder" && !w.ReachableFrom(host)).ToList();
        string works;
        if (chosen is null) works = $"No workspace it can use: each thread gets an empty folder on {machine}.";
        else if (chosen.Kind == "repository")
            works = $"`{chosen.Name}` (`{chosen.Url}`, branch `{chosen.Branch}`), cloned on {machine}, in a worktree per thread on its own `cowork/<thread>` branch. A hook refuses pushes to `{chosen.Branch}`.";
        else works = $"`{chosen.Name}`, the folder `{chosen.Path}` on {chosen.Host}: a worktree per thread on its own `cowork/<thread>` branch if it's a git folder, else the folder itself.";
        foreach (var u in unreachable) works += $" Can't reach `{u.Name}`: that folder is on {u.Host}.";
        lines.Add(new("worksOn", "Works on", works));

        lines.Add(new("reaches", "Reaches", $"Anything {machine} can. Its runner has no sandbox."));
        lines.Add(new("asks", "Asks", $"{ownerName}, its owner, whenever the harness asks permission. Everyone else sees that it's waiting for {ownerName}."));
        lines.Add(new("spends", "Spends", $"{ownerName}'s own model access on {machine}. CoWork doesn't count it."));
        lines.Add(new("remembers", "Remembers", $"A data folder of its own for {project.Name} on {machine}: what its harness keeps here doesn't mix with its other projects, and its sign-in carries over."));
        lines.Add(new("stops", "Stops", $"When its turn ends; agent-to-agent turns stop after 3 without a person. While {machine} is offline, requests wait for it."));

        return new
        {
            project = new { id = project.Id, name = project.Name },
            agent = new { id = agent.Id, handle = agent.Handle, displayName = agent.DisplayName, about = agent.About ?? "", ownerId = agent.OwnerId, ownerName, runnerHost = host, online,
                harnessCommand = System.Text.Json.JsonSerializer.Deserialize<string[]>(agent.HarnessCommand ?? "[]") },
            takesWorkFrom,
            inProject = c.ExecuteScalar<int>("SELECT COUNT(*) FROM project_agents WHERE project_id=@p AND agent_id=@a", new { p = project.Id, a = agent.Id }) > 0,
            sandboxed = false,
            lines,
            warning = $"Runs unsandboxed on {machine}. It can read and change anything the runner's account can there. Threads show this to everyone.",
        };
    }
}
