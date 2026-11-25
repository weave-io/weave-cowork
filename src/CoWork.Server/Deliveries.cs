namespace CoWork.Server;

/// <summary>
/// What an agent's runner still has to be given, read from the events. A `MessageDelivered` to the agent is queued until a `TurnStarted` follows it
/// in the same thread (same turn id); a requeued `TurnInterrupted` after that puts it back. The runner's position (`runner_positions`) is the
/// event position up to which nothing is queued, so a runner that connects (or a server that pumps) reads only what came after it.
/// A message whose thread is gone (channel or root post deleted) is no longer anyone's to hear.
/// </summary>
public static class Deliveries
{
    private const string Core = @"FROM events d
        WHERE d.type='MessageDelivered' AND json_extract(d.data,'$.agentId')=@agentId
          AND d.position > COALESCE((SELECT position FROM runner_positions WHERE agent_id=@agentId), 0)
          AND EXISTS (SELECT 1 FROM threads t JOIN posts p ON p.id=t.root_id AND p.deleted=0 WHERE t.root_id = CAST(substr(d.stream_id, 8) AS INTEGER))
          AND NOT EXISTS (SELECT 1 FROM events s WHERE s.stream_id=d.stream_id AND s.type='TurnStarted' AND s.correlation=d.correlation AND s.position>d.position
              AND NOT EXISTS (SELECT 1 FROM events r WHERE r.stream_id=s.stream_id AND r.type='TurnInterrupted' AND r.correlation=s.correlation AND r.position>s.position
                  AND json_extract(r.data,'$.requeued')=1))";

    /// <summary>The queued messages for @agentId, oldest first.</summary>
    public const string Queued = "SELECT d.position AS Position, d.stream_id AS StreamId, json_extract(d.data,'$.text') AS Text, d.correlation AS Turn, d.on_behalf_of AS Behalf " + Core + " ORDER BY d.position";

    /// <summary>How many are queued for @agentId in the thread stream @stream.</summary>
    public const string QueuedIn = "SELECT COUNT(*) " + Core + " AND d.stream_id=@stream";

    public sealed class Row { public long Position { get; set; } public string StreamId { get; set; } = ""; public string Text { get; set; } = ""; public string? Turn { get; set; } public long? Behalf { get; set; } public long ThreadId => long.Parse(StreamId[7..]); }

    /// <summary>Moves the agent's runner position forward to just before the oldest message still queued, or to <paramref name="at"/> when none is.
    /// <paramref name="pullBackTo"/>: a message at that position is queued again, so the position first goes back before it.</summary>
    public static void Advance(ProjectionContext x, long agentId, long at, long? pullBackTo)
    {
        var current = x.Scalar<long?>("SELECT position FROM runner_positions WHERE agent_id=@agentId", new { agentId }) ?? 0;
        if (pullBackTo is { } p) current = Math.Min(current, p - 1);
        x.Execute("INSERT OR REPLACE INTO runner_positions(agent_id, position) VALUES (@agentId,@current)", new { agentId, current });
        var oldest = x.Scalar<long?>("SELECT MIN(Position) FROM (" + Queued + ")", new { agentId });
        x.Execute("UPDATE runner_positions SET position=@position WHERE agent_id=@agentId", new { agentId, position = oldest is { } o ? o - 1 : at });
    }
}

/// <summary>Redaction: the content of a message is replaced, in the projection and in the events that hold it; the fact that it was said stays.</summary>
public static class Redaction
{
    public const string Text = "[redacted]";

    public static void RewriteEvents(ProjectionContext x, long postId)
    {
        var args = new { postId, r = Text };
        x.Execute("UPDATE events SET data=json_set(data,'$.body',@r,'$.redacted',json('true')) WHERE type='MessagePosted' AND json_extract(data,'$.postId')=@postId", args);
        x.Execute("UPDATE events SET data=json_set(data,'$.text',@r,'$.redacted',json('true')) WHERE type IN ('MessageDelivered','AgentReplied') AND json_extract(data,'$.postId')=@postId", args);
    }
}
