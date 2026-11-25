using Dapper;

namespace CoWork.Server;

/// <summary>What an agent has been doing: the threads it is busy in, its turns (with how many tool calls and how each ended), its failures
/// and when its runner was connected. Built from the turn events and the runner events.</summary>
public sealed class ActivityProjection : IAsyncProjection
{
    public string Name => "agent-activity";
    public string Title => "Agent activity";
    public string[] Tables => new[] { "activity_connections", "activity_turns", "activity_roots", "activity_threads" };

    public void Apply(ProjectionContext x, StoredEvent e)
    {
        var thread = ThreadProjection.ThreadOf(e);
        switch (e.Type)
        {
            case "ThreadStarted":
                x.Execute("INSERT OR REPLACE INTO activity_threads VALUES (@t,@ch)", new { t = e.Long("rootId"), ch = e.Long("channelId") });
                break;
            case "MessagePosted" when e.LongOrNull("threadId") is null:
            {
                var body = e.Str("body") ?? "";
                x.Execute("INSERT OR REPLACE INTO activity_roots VALUES (@p,@ch,@excerpt)", new { p = e.Long("postId"), ch = e.Long("channelId"), excerpt = body.Length > 100 ? body[..100] + "…" : body });
                break;
            }
            case "MessageRedacted":
                x.Execute("UPDATE activity_roots SET excerpt='[redacted]' WHERE post_id=@p", new { p = e.Long("postId") });
                break;
            case "ChannelDeleted":
            {
                var ch = e.Long("channelId");
                x.Execute("DELETE FROM activity_roots WHERE channel_id=@ch", new { ch });
                x.Execute("UPDATE activity_turns SET channel_id=NULL WHERE channel_id=@ch", new { ch });
                break;
            }
            case "TurnStarted" when e.Correlation is not null && e.ActorId is { } agent:
                x.Execute(@"INSERT INTO activity_turns(turn, agent_id, thread_id, channel_id, behalf_id, started_position, started_at, outcome, host)
                    VALUES (@turn,@agent,@thread,(SELECT channel_id FROM activity_threads WHERE thread_id=@thread),@behalf,@pos,@at,'running',@host)
                    ON CONFLICT(turn) DO UPDATE SET outcome='running', ended_at=NULL, reason=NULL, started_position=@pos, started_at=@at, host=@host",
                    new { turn = e.Correlation, agent, thread, behalf = e.OnBehalfOf, pos = e.Position, at = e.OccurredAt, host = e.Str("host") });
                break;
            case "ToolCallStarted" when e.Correlation is not null:
                x.Execute("UPDATE activity_turns SET tool_calls=tool_calls+1 WHERE turn=@turn", new { turn = e.Correlation });
                break;
            case "TurnEnded" when e.Correlation is not null:
                x.Execute("UPDATE activity_turns SET ended_at=@at, outcome=CASE outcome WHEN 'running' THEN 'finished' ELSE outcome END WHERE turn=@turn", new { turn = e.Correlation, at = e.OccurredAt });
                break;
            case "TurnFailed" when e.Correlation is not null:
                // a runner's error is followed by its TurnEnded; a failure that says "ended" is the last word
                x.Execute("UPDATE activity_turns SET outcome='failed', reason=@reason, ended_at=CASE WHEN @ended=1 THEN @at ELSE ended_at END WHERE turn=@turn",
                    new { turn = e.Correlation, reason = e.Str("reason"), ended = e.Bool("ended") ? 1 : 0, at = e.OccurredAt });
                break;
            case "TurnInterrupted" when e.Correlation is not null:
                x.Execute("UPDATE activity_turns SET outcome='interrupted', reason=@reason, ended_at=@at WHERE turn=@turn", new { turn = e.Correlation, reason = e.Str("reason"), at = e.OccurredAt });
                break;
            case "RunnerConnected":
            {
                var agent = e.Long("agentId");
                x.Execute("UPDATE activity_connections SET disconnected_at=@at WHERE agent_id=@agent AND disconnected_at IS NULL", new { agent, at = e.OccurredAt });       // an earlier one that was never closed (a crash)
                x.Execute("INSERT INTO activity_connections(agent_id, host, connected_at) VALUES (@agent,@host,@at)", new { agent, host = e.Str("host"), at = e.OccurredAt });
                break;
            }
            case "RunnerDisconnected":
                x.Execute("UPDATE activity_connections SET disconnected_at=@at WHERE agent_id=@agent AND disconnected_at IS NULL", new { agent = e.Long("agentId"), at = e.OccurredAt });
                break;
        }
    }
}
