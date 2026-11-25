using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoWork.Server;

/// <summary>
/// The inline projection of the thread stream: keeps `posts`, `threads`, `thread_agents`, `open_turns` and a runner's position (what the app
/// reads) in step with the events, in the transaction that appended them. A post's `position` is the last event that changed it.
/// </summary>
public sealed class ThreadProjection : IInlineProjection
{
    public static long ThreadOf(StoredEvent e) => e.StreamId.StartsWith("thread-") ? long.Parse(e.StreamId[7..]) : 0;

    public void Apply(ProjectionContext x, StoredEvent e)
    {
        switch (e.Type)
        {
            case "MessagePosted":
            {
                var thread = e.LongOrNull("threadId");
                x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, created_at, position) VALUES (@id,@ch,@t,@by,@body,'text',@at,@pos)",
                    new { id = e.Long("postId"), ch = e.Long("channelId"), t = thread, by = e.ActorId, body = e.Str("body"), at = e.OccurredAt, pos = e.Position });
                if (thread is not null) x.Execute("UPDATE threads SET agent_chain=@chain WHERE root_id=@thread", new { thread, chain = e.LongOrNull("agentChain") ?? 0 });      // a person spoke: the agent-to-agent count starts again (an imported thread says where it stood)
                break;
            }
            case "ThreadStarted":
                x.Execute("INSERT OR IGNORE INTO threads(root_id, channel_id, started_by, agent_chain, slug) VALUES (@r,@ch,@by,@chain,@slug)",
                    new { r = e.Long("rootId"), ch = e.Long("channelId"), by = e.Long("startedBy"), chain = e.LongOrNull("agentChain") ?? 0, slug = e.Str("slug") });      // agentChain: only an imported thread starts with a count
                break;
            case "AgentJoinedThread":
                x.Execute("INSERT OR IGNORE INTO thread_agents(thread_id, agent_id) VALUES (@t,@a)", new { t = ThreadOf(e), a = e.Long("agentId") });
                break;
            case "ThreadWorkspaceReady":
                // what the agent's runner made for this thread: a worktree on its own branch, a folder, a scratch folder or the agent's own folder
                x.Execute(@"UPDATE thread_agents SET workspace_id=@ws, place=@place, branch=@branch, location=@loc, host=@host, sandboxed=@sb WHERE thread_id=@t AND agent_id=@a",
                    new { ws = e.LongOrNull("workspaceId"), place = e.Str("place"), branch = e.Str("branch"), loc = e.Str("location"), host = e.Str("host"), sb = e.Bool("sandboxed") ? 1 : 0, t = ThreadOf(e), a = e.Long("agentId") });
                break;
            case "WorkRefused":
                // the agent's grant doesn't let this person give it work: a short reply in the thread instead of a turn
                x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, created_at, position) VALUES (@id,@ch,@t,@a,@body,'notice',@at,@pos)",
                    new { id = e.Long("postId"), ch = e.Long("channelId"), t = ThreadOf(e), a = e.Long("agentId"), body = e.Str("reply"), at = e.OccurredAt, pos = e.Position });
                break;
            case "MessageDelivered":
                // queued work is this event itself: it stays queued until a turn starts for it (see Deliveries)
                if (e.Bool("chained")) x.Execute("UPDATE threads SET agent_chain=agent_chain+1 WHERE root_id=@t", new { t = ThreadOf(e) });
                break;
            case "TurnStarted":
            {
                var agent = e.Long("agentId");
                x.Execute("INSERT OR REPLACE INTO open_turns(thread_id, agent_id, turn, behalf, cause) VALUES (@t,@a,@turn,@behalf,@pos)",
                    new { t = ThreadOf(e), a = agent, turn = e.Correlation, behalf = e.OnBehalfOf, pos = e.Position });
                Deliveries.Advance(x, agent, e.Position, pullBackTo: null);
                break;
            }
            case "TurnEnded":
                x.Execute("DELETE FROM open_turns WHERE thread_id=@t AND agent_id=@a", new { t = ThreadOf(e), a = e.Long("agentId") });
                break;
            case "TurnFailed":
                x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, created_at, position) VALUES (@id,@ch,@t,@a,@body,'notice',@at,@pos)",
                    new { id = e.Long("postId"), ch = e.Long("channelId"), t = ThreadOf(e), a = e.Long("agentId"), body = e.Str("reason"), at = e.OccurredAt, pos = e.Position });
                if (e.Bool("ended")) x.Execute("DELETE FROM open_turns WHERE thread_id=@t AND agent_id=@a", new { t = ThreadOf(e), a = e.Long("agentId") });
                break;
            case "TurnInterrupted":
            {
                var t = ThreadOf(e); var a = e.Long("agentId");
                x.Execute("DELETE FROM open_turns WHERE thread_id=@t AND agent_id=@a", new { t, a });
                if (e.Bool("requeued"))           // the runner couldn't be reached: the message is queued again, so the runner's position goes back before it
                {
                    var delivered = e.Causation is { } started ? x.Scalar<long?>("SELECT causation FROM events WHERE position=@started", new { started }) : null;
                    Deliveries.Advance(x, a, e.Position, pullBackTo: delivered);
                }
                if (e.Data.TryGetProperty("finalizedPostIds", out var ids))
                    foreach (var id in ids.EnumerateArray())
                        x.Execute("UPDATE posts SET streaming=0, position=@pos WHERE id=@id", new { id = id.GetInt64(), pos = e.Position });
                if (e.LongOrNull("postId") is { } notice)
                    x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, created_at, position) VALUES (@id,@ch,@t,@a,@body,'notice',@at,@pos)",
                        new { id = notice, ch = e.Long("channelId"), t, a, body = e.Str("reason"), at = e.OccurredAt, pos = e.Position });
                break;
            }
            case "ToolCallStarted":
            {
                var meta = new JsonObject { ["toolId"] = e.Str("toolId"), ["title"] = e.Str("title"), ["toolKind"] = e.Str("toolKind"), ["status"] = e.Str("status") };
                x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, meta, created_at, position) VALUES (@id,@ch,@t,@a,@title,'tool',@meta,@at,@pos)",
                    new { id = e.Long("postId"), ch = e.Long("channelId"), t = ThreadOf(e), a = e.ActorId, title = e.Str("title") ?? "tool", meta = meta.ToJsonString(), at = e.OccurredAt, pos = e.Position });
                break;
            }
            case "ToolCallDetailed":
            case "ToolCallFinished":
            {
                var id = e.Long("postId");
                var title = e.Str("title");
                Meta(x, id, e.Position, m =>
                {
                    if (e.Type == "ToolCallFinished") m["status"] = e.Str("status") ?? "completed";
                    if (!string.IsNullOrEmpty(title)) m["title"] = title;
                });
                if (!string.IsNullOrEmpty(title)) x.Execute("UPDATE posts SET body=@title WHERE id=@id", new { title, id });
                break;
            }
            case "AgentReplied":
            {
                var id = e.Long("postId"); var complete = e.Bool("complete");
                if (x.Scalar<int>("SELECT COUNT(*) FROM posts WHERE id=@id", new { id }) == 0)
                    x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, streaming, created_at, position) VALUES (@id,@ch,@t,@a,@body,'text',@s,@at,@pos)",
                        new { id, ch = e.Long("channelId"), t = ThreadOf(e), a = e.ActorId, body = e.Str("text") ?? "", s = complete ? 0 : 1, at = e.OccurredAt, pos = e.Position });
                else
                    x.Execute("UPDATE posts SET body = body || @text, streaming=@s, position=@pos WHERE id=@id", new { text = e.Str("text") ?? "", s = complete ? 0 : 1, pos = e.Position, id });
                break;
            }
            case "PermissionRequested":
            {
                var meta = new JsonObject
                {
                    ["requestId"] = e.Str("requestId"), ["agentId"] = e.Long("agentId"), ["ownerId"] = e.Long("ownerId"), ["title"] = e.Str("title"),
                    ["options"] = JsonNode.Parse(e.Data.GetProperty("options").GetRawText()), ["status"] = "pending",
                };
                // a template's asks: whoever started the thread answers, and the project's leads may after a while
                if (e.Data.TryGetProperty("leadIds", out var leads) && leads.ValueKind == System.Text.Json.JsonValueKind.Array && leads.GetArrayLength() > 0)
                { meta["leadIds"] = JsonNode.Parse(leads.GetRawText()); meta["leadAfter"] = e.LongOrNull("leadAfter"); }
                if (e.LongOrNull("agentOwnerId") is { } agentOwner) meta["agentOwnerId"] = agentOwner;
                x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, meta, created_at, position) VALUES (@id,@ch,@t,@a,@title,'ask',@meta,@at,@pos)",
                    new { id = e.Long("postId"), ch = e.Long("channelId"), t = ThreadOf(e), a = e.Long("agentId"), title = e.Str("title"), meta = meta.ToJsonString(), at = e.OccurredAt, pos = e.Position });
                break;
            }
            case "MessageDeleted":
                x.Execute("UPDATE posts SET deleted=1, position=@pos WHERE id=@id", new { pos = e.Position, id = e.Long("postId") });
                break;
            case "MessageRedacted":
                x.Execute("UPDATE posts SET body=@r, position=@pos WHERE id=@id", new { r = Redaction.Text, pos = e.Position, id = e.Long("postId") });
                Redaction.RewriteEvents(x, e.Long("postId"));
                break;
            case "PermissionGranted":
                Meta(x, e.Long("postId"), e.Position, m => { m["status"] = "answered"; m["answer"] = e.Str("optionId"); });
                break;
            case "PermissionDenied":
                Meta(x, e.Long("postId"), e.Position, m => { m["status"] = "denied"; m["answer"] = null; });
                break;
        }
    }

    private static void Meta(ProjectionContext x, long postId, long position, Action<JsonObject> change)
    {
        var old = x.Scalar<string>("SELECT meta FROM posts WHERE id=@postId", new { postId });
        var node = JsonNode.Parse(old ?? "{}")!.AsObject();
        change(node);
        x.Execute("UPDATE posts SET meta=@m, position=@position WHERE id=@postId", new { m = node.ToJsonString(), position, postId });
    }
}
