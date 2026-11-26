namespace CoWork.Server;

/// <summary>The tables the inline projections keep. A rebuild empties them (children first) and replays the events.
/// Not in the list, on purpose: `events`, and the tables of secrets and sign-ins (`logins`, `runner_secrets`, `invite_codes`), which no event holds.</summary>
public static class Projections
{
    public static readonly string[] Tables =
    {
        "artifact_files", "artifact_versions", "artifacts", "open_turns", "runner_positions", "thread_agents", "threads", "posts", "channel_members", "channels",
        "invites", "agents", "people", "members", "settings", "projects", "project_members", "project_agents", "workspaces",
        "pools", "sandboxes", "model_usage", "project_network", "thread_egress", "budgets", "thread_budgets", "project_usage",
    };

    public static void Clear(ProjectionContext x)
    {
        foreach (var t in Tables) x.Execute($"DELETE FROM {t}");
    }
}
