using Dapper;
using Microsoft.AspNetCore.Routing;

namespace CoWork.Server;

public static partial class Api
{
    static void MapPosts(RouteGroupBuilder authed)
    {
        authed.MapGet("/channels/{id:long}/posts", (long id, HttpContext ctx, Db db) =>
        {
            using var c = db.Open();
            if (!Access.CanSee(c, id, Me(ctx))) return Err(404, "No such channel.");
            var rows = c.Query<PostRow>(Q.PostSelect + " WHERE p.channel_id=@id AND p.thread_id IS NULL ORDER BY p.id DESC LIMIT 200", new { id }).Reverse().ToList();
            return Results.Ok(rows.Select(p => Q.PostDto(p, Q.ThreadAgentHandles(c, p.Id))));
        });

        authed.MapPost("/channels/{id:long}/posts", async (long id, PostBody b, HttpContext ctx, Db db, Dispatcher d) =>
        {
            var me = Me(ctx);                          // the author is the caller, never the body
            using (var c = db.Open())
            {
                if (!Access.CanSee(c, id, me)) return Err(404, "No such channel.");
                if (Q.Channel(c, id)!.Kind == "voice") return Err(400, "Voice channels have no posts.");
                var body = (b.Body ?? "").Trim();
                if (body.Length == 0) return Err(400, "Write something first.");
                if (body.Length > 8000) return Err(400, "That's too long (8,000 characters at most).");
                if (b.ThreadId is not null)
                {
                    var root = Q.Post(c, b.ThreadId.Value);
                    if (root is null || root.ChannelId != id || root.ThreadId is not null) return Err(400, "That thread doesn't exist.");
                }
            }
            var postId = await d.Post(me, id, b.ThreadId, (b.Body ?? "").Trim());
            using var c2 = db.Open();
            return Results.Ok(Q.FullPostDto(c2, postId));
        });

        authed.MapGet("/threads/{rootId:long}", (long rootId, HttpContext ctx, Db db, Dispatcher d) =>
        {
            using var c = db.Open();
            var root = Q.Post(c, rootId);
            if (root is null || root.ThreadId is not null || !Access.CanSee(c, root.ChannelId, Me(ctx))) return Err(404, "No such thread.");
            var replies = c.Query<PostRow>(Q.PostSelect + " WHERE p.thread_id=@rootId ORDER BY p.id", new { rootId }).Select(p => Q.PostDto(p)).ToList();
            var startedBy = c.ExecuteScalar<long?>("SELECT started_by FROM threads WHERE root_id=@rootId", new { rootId }) ?? Me(ctx);
            return Results.Ok(new { root = Q.FullPostDto(c, rootId), replies, agents = d.ThreadAgentStatus(rootId), startedBy });
        });

        // delete a message: the projections hide it, the events keep the fact. The author may, an admin may, an agent's owner may for its posts.
        authed.MapDelete("/posts/{id:long}", (long id, HttpContext ctx, Db db, IEventStore store) =>
        {
            var (post, error) = Editable(db, id, Me(ctx));
            if (post is null) return error!;
            store.Append(new[] { new NewEvent(Q.StreamOf(post), "MessageDeleted", new { postId = id, channelId = post.ChannelId, threadId = post.ThreadId }, Me(ctx)) });
            return Results.Ok(new { ok = true });
        });

        // redact a message: its words are replaced, in the projections and in the events that hold them; the fact that it was said stays
        authed.MapPost("/posts/{id:long}/redact", (long id, HttpContext ctx, Db db, IEventStore store) =>
        {
            var (post, error) = Editable(db, id, Me(ctx));
            if (post is null) return error!;
            store.Append(new[] { new NewEvent(Q.StreamOf(post), "MessageRedacted", new { postId = id, channelId = post.ChannelId, threadId = post.ThreadId }, Me(ctx)) });
            return Results.Ok(new { ok = true });
        });

        // an agent's permission card: only its owner may answer
        authed.MapPost("/posts/{id:long}/answer", async (long id, AnswerBody b, HttpContext ctx, Dispatcher d) =>
        {
            var (ok, error) = await d.Answer(id, Me(ctx), b.OptionId);
            return ok ? Results.Ok(new { ok = true }) : Err(error == "forbidden" ? 403 : 400, error == "forbidden" ? "Only the agent's owner can answer this." : error!);
        });
    }

    /// <summary>The text post a caller may delete or redact (they can see its channel, and wrote it, own the agent that wrote it, or are an admin), or the error to send.</summary>
    static (PostRow? Post, IResult? Error) Editable(Db db, long id, long me)
    {
        using var c = db.Open();
        var p = Q.Post(c, id);
        if (p is null || !Access.CanSee(c, p.ChannelId, me)) return (null, Err(404, "No such message."));
        if (p.Kind != "text") return (null, Err(400, "Only messages can be deleted."));
        var owner = c.ExecuteScalar<long?>("SELECT owner_id FROM agents WHERE member_id=@a", new { a = p.AuthorId });
        if (p.AuthorId != me && owner != me && !Access.IsAdmin(c, me)) return (null, Err(403, "Only the author or an admin can do that."));
        return (p, null);
    }
}
