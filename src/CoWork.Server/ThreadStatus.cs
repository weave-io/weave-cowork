using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>
/// Where a thread's work stands, as the post and the thread show it (mockups/cowork/projects.html, step 4): <c>waiting</c> for someone to
/// answer an ask, <c>working</c> while a turn runs, <c>starting</c> while a message waits for its agent (a sandbox on its way, a runner coming
/// back), <c>done</c> once an agent resolved it with an outcome. Null when nothing is going on and it isn't done.
/// </summary>
public static class ThreadStatus
{
    public sealed record Status(string State, long? WaitingForId, string? WaitingFor, string? Outcome, long? OutcomePostId);

    public static Status? Of(SqliteConnection c, long rootId)
    {
        var t = c.QueryFirstOrDefault<(long Root, long? Resolved)>("SELECT root_id, resolved_post FROM threads WHERE root_id=@rootId", new { rootId });
        if (t.Root == 0) return null;
        var ask = c.QueryFirstOrDefault<string?>("SELECT meta FROM posts WHERE thread_id=@rootId AND kind='ask' AND deleted=0 AND json_extract(meta,'$.status')='pending' ORDER BY id DESC LIMIT 1", new { rootId });
        if (ask is not null && (long?)JsonNode.Parse(ask)?["ownerId"] is { } owner)
            return new("waiting", owner, Q.Member(c, owner)?.DisplayName, null, null);
        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM open_turns WHERE thread_id=@rootId", new { rootId }) > 0) return new("working", null, null, null, null);
        var agents = c.Query<long>("SELECT agent_id FROM thread_agents WHERE thread_id=@rootId", new { rootId });
        if (agents.Any(a => c.ExecuteScalar<int>(Deliveries.QueuedIn, new { agentId = a, stream = $"thread-{rootId}" }) > 0)
            || c.ExecuteScalar<int>("SELECT COUNT(*) FROM sandboxes WHERE thread_id=@rootId AND state IN ('waiting','starting','resuming')", new { rootId }) > 0)
            return new("starting", null, null, null, null);
        if (t.Resolved is { } post)
            return new("done", null, null, c.ExecuteScalar<string?>("SELECT json_extract(meta,'$.title') FROM posts WHERE id=@post", new { post }), post);
        return null;
    }
}
