using System.Text.Json;
using Dapper;

namespace CoWork.Server;

/// <summary>
/// Unread counts and the mentions inbox. Built from the messages people and agents write (`MessagePosted`, the first `AgentReplied` of an answer)
/// and what each person has read (`ChannelRead`, on `person-{id}`). A count is "messages by someone else in a channel, after the person's marker".
/// Nothing before a person joined, was added to a private channel, or before this view existed (`UnreadBaselineSet`) is unread.
/// </summary>
public sealed class UnreadProjection : IAsyncProjection
{
    public string Name => "unread";
    public string Title => "Unread counts and mentions";
    public string[] Tables => new[] { "unread_mentions", "unread_activity", "unread_markers", "unread_handles", "unread_settings" };

    public void Apply(ProjectionContext x, StoredEvent e)
    {
        switch (e.Type)
        {
            case "PersonJoined":
            {
                var id = e.Long("memberId");
                x.Execute("INSERT OR REPLACE INTO unread_handles VALUES (@h,@id)", new { h = (e.Str("handle") ?? "").ToLowerInvariant(), id });
                Mark(x, id, 0, e.Position);
                break;
            }
            case "MemberAdded": Mark(x, e.Long("memberId"), e.Long("channelId"), e.Position); break;     // a private channel's earlier messages were not written for them
            case "UnreadBaselineSet": x.Execute("INSERT OR REPLACE INTO unread_settings VALUES ('baseline',@p)", new { p = e.Position }); break;
            case "ChannelRead": Mark(x, e.ActorId ?? 0, e.Long("channelId"), e.Long("position")); break;
            case "MessagePosted":
            {
                var post = e.Long("postId"); var channel = e.Long("channelId"); var author = e.ActorId ?? 0;
                x.Execute("INSERT OR IGNORE INTO unread_activity VALUES (@pos,@channel,@post,@author)", new { pos = e.Position, channel, post, author });
                Mentioned(x, e, post, channel, e.LongOrNull("threadId"), author, e.Str("body") ?? "");
                break;
            }
            case "AgentReplied":
            {
                var post = e.Long("postId"); var channel = e.Long("channelId"); var author = e.ActorId ?? 0;
                if (e.Long("segment") == 0) x.Execute("INSERT OR IGNORE INTO unread_activity VALUES (@pos,@channel,@post,@author)", new { pos = e.Position, channel, post, author });
                var thread = ThreadProjection.ThreadOf(e);
                Mentioned(x, e, post, channel, thread == 0 ? null : thread, author, e.Str("text") ?? "");        // an agent that says "@alex" lands in Alex's inbox
                break;
            }
            case "MessageDeleted":
                x.Execute("DELETE FROM unread_activity WHERE post_id=@p", new { p = e.Long("postId") });
                x.Execute("DELETE FROM unread_mentions WHERE post_id=@p", new { p = e.Long("postId") });
                break;
            case "MessageRedacted":
                x.Execute("UPDATE unread_mentions SET excerpt='[redacted]' WHERE post_id=@p", new { p = e.Long("postId") });
                break;
            case "ChannelDeleted":
            {
                var ch = e.Long("channelId");
                x.Execute("DELETE FROM unread_activity WHERE channel_id=@ch", new { ch });
                x.Execute("DELETE FROM unread_mentions WHERE channel_id=@ch", new { ch });
                x.Execute("DELETE FROM unread_markers WHERE channel_id=@ch", new { ch });
                break;
            }
        }
    }

    /// <summary>Read markers only move forward.</summary>
    private static void Mark(ProjectionContext x, long person, long channel, long position) =>
        x.Execute(@"INSERT INTO unread_markers VALUES (@person,@channel,@position)
            ON CONFLICT(person_id, channel_id) DO UPDATE SET position=MAX(position, @position)", new { person, channel, position });

    private static void Mentioned(ProjectionContext x, StoredEvent e, long post, long channel, long? thread, long author, string text)
    {
        foreach (var handle in Dispatcher.Mentions(text))
        {
            var person = x.Scalar<long?>("SELECT member_id FROM unread_handles WHERE handle=@handle", new { handle });
            if (person is null || person == author) continue;
            var excerpt = text.Length > 160 ? text[..160] + "…" : text;
            x.Execute("INSERT OR IGNORE INTO unread_mentions VALUES (@pos,@person,@post,@channel,@thread,@author,@excerpt,@at)",
                new { pos = e.Position, person, post, channel, thread, author, excerpt, at = e.OccurredAt });
        }
    }

    /// <summary>The position up to which a person has read a channel: the latest of their marker, their own baseline and the view's.</summary>
    public const string Effective = @"MAX(COALESCE((SELECT position FROM unread_markers WHERE person_id=@me AND channel_id=X.channel_id), 0),
        COALESCE((SELECT position FROM unread_markers WHERE person_id=@me AND channel_id=0), 0),
        COALESCE((SELECT value FROM unread_settings WHERE key='baseline'), 0))";
}
