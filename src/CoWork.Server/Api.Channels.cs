using Dapper;
using Microsoft.AspNetCore.Routing;

namespace CoWork.Server;

public static partial class Api
{
    static void MapChannels(RouteGroupBuilder authed)
    {
        MapVoice(authed);
        authed.MapPost("/channels", async (ChannelBody b, HttpContext ctx, Db db, Realtime rt) =>
        {
            var me = Me(ctx);
            long id;
            using (var c = db.Open())
            {
                var name = NormaliseChannel(b.Name);
                if (!ChannelRx.IsMatch(name)) return Err(400, "Channel names are lowercase letters, numbers, - and _.");
                if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM channels WHERE name=@name", new { name }) > 0) return Err(409, "There's already a channel with that name.");
                var kind = b.Kind ?? "text";
                if (kind is not ("text" or "voice")) return Err(400, "A channel is text or voice.");
                id = c.ExecuteScalar<long>("INSERT INTO channels(name, topic, is_private, kind, creator_id, created_at) VALUES (@name,@topic,@p,@kind,@me,@now); SELECT last_insert_rowid();",
                    new { name, kind, topic = (b.Topic ?? "").Trim(), p = b.IsPrivate == true, me, now = Db.Now() });
                c.Execute("INSERT INTO channel_members VALUES (@id,@me)", new { id, me });
            }
            await rt.RefreshAll();
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { id });
        });

        authed.MapPatch("/channels/{id:long}", async (long id, ChannelBody b, HttpContext ctx, Db db, Realtime rt) =>
        {
            using (var c = db.Open())
            {
                var ch = Q.Channel(c, id);
                if (ch is null || !Access.CanSee(c, id, Me(ctx))) return Err(404, "No such channel.");
                if (!Access.CanManage(c, ch, Me(ctx))) return Err(403, "Only an admin or the channel's creator can change this.");
                if (b.Name is not null)
                {
                    var name = NormaliseChannel(b.Name);
                    if (!ChannelRx.IsMatch(name)) return Err(400, "Channel names are lowercase letters, numbers, - and _.");
                    if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM channels WHERE name=@name AND id<>@id", new { name, id }) > 0) return Err(409, "There's already a channel with that name.");
                    c.Execute("UPDATE channels SET name=@name WHERE id=@id", new { name, id });
                }
                if (b.Topic is not null) c.Execute("UPDATE channels SET topic=@t WHERE id=@id", new { t = b.Topic.Trim(), id });
                if (b.IsPrivate is not null && b.IsPrivate != ch.IsPrivate) c.Execute("UPDATE channels SET is_private=@p WHERE id=@id", new { p = b.IsPrivate.Value, id });
            }
            await rt.RefreshAll();
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/channels/{id:long}", async (long id, HttpContext ctx, Db db, Realtime rt, VoicePresence voice) =>
        {
            using (var c = db.Open())
            {
                var ch = Q.Channel(c, id);
                if (ch is null || !Access.CanSee(c, id, Me(ctx))) return Err(404, "No such channel.");
                if (!Access.CanManage(c, ch, Me(ctx))) return Err(403, "Only an admin or the channel's creator can delete it.");
                using var tx = c.BeginTransaction();
                c.Execute("DELETE FROM deliveries WHERE thread_id IN (SELECT root_id FROM threads WHERE channel_id=@id)", new { id }, tx);
                c.Execute("DELETE FROM thread_agents WHERE thread_id IN (SELECT root_id FROM threads WHERE channel_id=@id)", new { id }, tx);
                c.Execute("DELETE FROM threads WHERE channel_id=@id", new { id }, tx);
                c.Execute("DELETE FROM posts WHERE channel_id=@id", new { id }, tx);
                c.Execute("DELETE FROM channel_members WHERE channel_id=@id", new { id }, tx);
                c.Execute("DELETE FROM channels WHERE id=@id", new { id }, tx);
                tx.Commit();
            }
            voice.ClearChannel(id);
            await rt.RefreshAll();
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapPut("/channels/{id:long}/members/{memberId:long}", async (long id, long memberId, HttpContext ctx, Db db, Realtime rt) =>
        {
            using (var c = db.Open())
            {
                var me = Me(ctx);
                var ch = Q.Channel(c, id);
                if (ch is null || !Access.CanSee(c, id, me)) return Err(404, "No such channel.");
                var target = Q.Member(c, memberId);
                if (target is null || target.Removed) return Err(404, "No such member.");
                // managers add anyone; an agent's owner may bring their own agent into a channel they can see
                var ownAgent = target.Kind == "agent" && target.OwnerId == me;
                if (!Access.CanManage(c, ch, me) && !ownAgent) return Err(403, "Only an admin or the channel's creator can add people.");
                c.Execute("INSERT OR IGNORE INTO channel_members VALUES (@id,@memberId)", new { id, memberId });
            }
            await rt.Refresh(memberId);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/channels/{id:long}/members/{memberId:long}", async (long id, long memberId, HttpContext ctx, Db db, Realtime rt) =>
        {
            using (var c = db.Open())
            {
                var me = Me(ctx);
                var ch = Q.Channel(c, id);
                if (ch is null || !Access.CanSee(c, id, me)) return Err(404, "No such channel.");
                var target = Q.Member(c, memberId);
                if (target is null) return Err(404, "No such member.");
                var ownAgent = target.Kind == "agent" && target.OwnerId == me;
                if (!Access.CanManage(c, ch, me) && !ownAgent && memberId != me) return Err(403, "Only an admin or the channel's creator can remove people.");
                if (memberId == ch.CreatorId && target.Kind == "person" && ch.IsPrivate) return Err(400, "The creator can't be removed from their private channel.");
                c.Execute("DELETE FROM channel_members WHERE channel_id=@id AND member_id=@memberId", new { id, memberId });
            }
            await rt.Refresh(memberId);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });
    }

    static string NormaliseChannel(string? n) => (n ?? "").Trim().TrimStart('#').Replace(' ', '-').ToLowerInvariant();
}
