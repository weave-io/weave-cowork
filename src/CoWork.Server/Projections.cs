namespace CoWork.Server;

/// <summary>The tables the inline projections keep. A rebuild empties them (children first) and replays the events.
/// Not in the list, on purpose: `events`, and the tables of secrets (`credentials`, `runner_secrets`, `invite_codes`), which no event holds.</summary>
public static class Projections
{
    public static readonly string[] Tables =
    {
        "open_turns", "runner_positions", "thread_agents", "threads", "posts", "channel_members", "channels",
        "invites", "agents", "people", "members", "settings",
    };

    public static void Clear(ProjectionContext x)
    {
        foreach (var t in Tables) x.Execute($"DELETE FROM {t}");
    }
}
