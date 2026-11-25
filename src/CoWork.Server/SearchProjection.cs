namespace CoWork.Server;

/// <summary>
/// The search index: an SQLite FTS5 table (`porter unicode61`, so "rounds" finds "rounding") of what people and agents wrote.
/// One document per post (`MessagePosted`) and one per agent answer (`AgentReplied` segments are appended to it as they come).
/// A deleted or redacted message leaves the index for good; a deleted channel takes its documents with it.
/// Who may see a document is NOT stored here: it follows from its channel, checked in the query (<see cref="Search"/>).
/// </summary>
public sealed class SearchProjection : IAsyncProjection
{
    public string Name => "search";
    public string Title => "Search";
    public string[] Tables => new[] { "search_fts", "search_docs" };

    public void Apply(ProjectionContext x, StoredEvent e)
    {
        switch (e.Type)
        {
            case "MessagePosted":
            {
                var post = e.Long("postId");
                var id = Add(x, "message", post, e.Long("channelId"), e.LongOrNull("threadId"), e.ActorId, e);
                if (id is not null) x.Execute("INSERT INTO search_fts(rowid, title, body) VALUES (@id,'',@body)", new { id, body = e.Str("body") ?? "" });
                break;
            }
            case "AgentReplied":
            {
                var post = e.Long("postId"); var text = e.Str("text") ?? "";
                var doc = x.Query<(long Id, bool Gone)>("SELECT id, gone FROM search_docs WHERE kind='message' AND ref_id=@post", new { post }).FirstOrDefault();
                if (doc.Id == 0)
                {
                    var thread = ThreadProjection.ThreadOf(e);
                    var id = Add(x, "message", post, e.Long("channelId"), thread == 0 ? null : thread, e.ActorId, e);
                    if (id is not null) x.Execute("INSERT INTO search_fts(rowid, title, body) VALUES (@id,'',@text)", new { id, text });
                }
                else if (!doc.Gone && text.Length > 0)
                    x.Execute("UPDATE search_fts SET body = body || @text WHERE rowid=@id", new { id = doc.Id, text });
                break;
            }
            case "ThreadStarted":
                // the opening post belongs to its thread from now on (a thread's id is its root post's id)
                x.Execute("UPDATE search_docs SET thread_id=@r WHERE kind='message' AND ref_id=@r", new { r = e.Long("rootId") });
                break;
            case "MessageDeleted":
            case "MessageRedacted":
            {
                // a redacted message is found by nothing it used to say: its words leave the index, not just the projection's copy
                var post = e.Long("postId");
                x.Execute("DELETE FROM search_fts WHERE rowid IN (SELECT id FROM search_docs WHERE kind='message' AND ref_id=@post)", new { post });
                x.Execute("UPDATE search_docs SET gone=1 WHERE kind='message' AND ref_id=@post", new { post });
                break;
            }
            case "ChannelDeleted":
            {
                var ch = e.Long("channelId");
                x.Execute("DELETE FROM search_fts WHERE rowid IN (SELECT id FROM search_docs WHERE channel_id=@ch)", new { ch });
                x.Execute("DELETE FROM search_docs WHERE channel_id=@ch", new { ch });
                break;
            }
        }
    }

    /// <summary>A new document; null when it already exists (an event seen twice adds nothing).</summary>
    private static long? Add(ProjectionContext x, string kind, long refId, long channel, long? thread, long? author, StoredEvent e)
    {
        var n = x.Execute(@"INSERT OR IGNORE INTO search_docs(kind, ref_id, channel_id, thread_id, author_id, position, occurred_at)
            VALUES (@kind,@refId,@channel,@thread,@author,@pos,@at)", new { kind, refId, channel, thread, author, pos = e.Position, at = e.OccurredAt });
        return n == 0 ? null : x.Scalar<long>("SELECT id FROM search_docs WHERE kind=@kind AND ref_id=@refId", new { kind, refId });
    }
}
