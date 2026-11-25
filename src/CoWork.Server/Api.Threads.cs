using Dapper;

namespace CoWork.Server;

public record ThreadTitleBody(string? Title);

/// <summary>Brief 8: threads you can find. The channel list's threads, a channel's "All threads", renaming a thread and reading it.</summary>
public static partial class Api
{
    static void MapThreads(RouteGroupBuilder authed)
    {
        // the channel list: per channel the caller sees, its most recently active threads that belong in the list, and how many it has
        authed.MapGet("/thread-list", async (HttpContext ctx, Db db, ProjectionRunner runner) =>
        {
            var me = Me(ctx);
            await runner.CatchUpAsync();          // unread counts include what the person just read or wrote
            using var c = db.Open();
            return Results.Ok(ThreadList.Sidebar(c, me, Db.Now()));
        });

        // "All threads": every thread in a channel, with a filter (open, waiting for me, done)
        authed.MapGet("/channels/{id:long}/threads", async (long id, string? filter, HttpContext ctx, Db db, ProjectionRunner runner) =>
        {
            var me = Me(ctx);
            await runner.CatchUpAsync();
            using var c = db.Open();
            if (!Access.CanSee(c, id, me)) return Err(404, "No such channel.");
            if (filter is not null && filter is not ("open" or "waiting" or "done" or "all")) return Err(400, "Filter by open, waiting or done.");
            return Results.Ok(ThreadList.InChannel(c, id, me, filter));
        });

        // anyone who may post in the thread may rename it; a thread always has a title, so an empty one is refused
        authed.MapPut("/threads/{id:long}/title", (long id, ThreadTitleBody b, HttpContext ctx, Db db, IEventStore store) =>
        {
            var me = Me(ctx);
            var title = System.Text.RegularExpressions.Regex.Replace(b.Title ?? "", @"\s+", " ").Trim();
            using (var c = db.Open())
            {
                var channel = c.ExecuteScalar<long?>("SELECT channel_id FROM threads WHERE root_id=@id", new { id });
                if (channel is null || !Access.CanSee(c, channel.Value, me)) return Err(404, "No such thread.");
                if (!ThreadList.CanRename(c, id, me)) return Err(403, "Only people who can post in this thread can rename it.");
                if (title.Length == 0) return Err(400, "A thread needs a title.");
                if (title.Length > 120) return Err(400, "That's too long (120 characters at most).");
            }
            using (var c = db.Open())
                store.Append(new[] { new NewEvent($"thread-{id}", "ThreadRenamed", new { threadId = id, channelId = c.ExecuteScalar<long>("SELECT channel_id FROM threads WHERE root_id=@id", new { id }), title }, me) });
            using var c2 = db.Open();
            return Results.Ok(ThreadList.Of(c2, id));
        });

        // a person opened a thread: its replies so far are read. Markers only move forward; an event is written only when something is new.
        authed.MapPost("/threads/{id:long}/read", async (long id, HttpContext ctx, Db db, IEventStore store, ProjectionRunner runner) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                var channel = c.ExecuteScalar<long?>("SELECT channel_id FROM threads WHERE root_id=@id", new { id });
                if (channel is null || !Access.CanSee(c, channel.Value, me)) return Err(404, "No such thread.");
            }
            long latest = 0;
            store.Append(d =>
            {
                latest = d.Scalar<long?>("SELECT MAX(position) FROM posts WHERE thread_id=@id AND kind='text' AND deleted=0", new { id }) ?? 0;
                var marker = d.Scalar<long?>("SELECT MAX(json_extract(data,'$.position')) FROM events WHERE stream_id=@s AND type='ThreadRead' AND json_extract(data,'$.threadId')=@id", new { s = $"person-{me}", id }) ?? 0;
                return latest > marker ? new[] { new NewEvent($"person-{me}", "ThreadRead", new { threadId = id, position = latest }, me) } : Array.Empty<NewEvent>();
            });
            await runner.CatchUpAsync();          // the list a browser asks for next includes this
            return Results.Ok(new { position = latest });
        });
    }
}
