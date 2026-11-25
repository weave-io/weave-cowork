using Dapper;
using Microsoft.AspNetCore.Routing;

namespace CoWork.Server;

public static partial class Api
{
    static void MapChannels(RouteGroupBuilder authed)
    {
        MapVoice(authed);
        authed.MapPost("/channels", async (ChannelBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            var name = NormaliseChannel(b.Name);
            if (!ChannelRx.IsMatch(name)) return Err(400, "Channel names are lowercase letters, numbers, - and _.");
            var kind = b.Kind ?? "text";
            if (kind is not ("text" or "voice")) return Err(400, "A channel is text or voice.");
            if (b.ProjectId is { } pid)
                using (var c = db.Open())
                    if (!Access.CanSeeProject(c, pid, me) || Access.ProjectRole(c, pid, me) is null && !Access.IsAdmin(c, me)) return Err(404, "No such project.");
            long id = 0; var taken = false;
            store.Append(d =>
            {
                taken = d.Scalar<int>("SELECT COUNT(*) FROM channels WHERE name=@name", new { name }) > 0;
                if (taken) return Array.Empty<NewEvent>();
                id = d.NextId("channels");
                return new[]
                {
                    new NewEvent($"channel-{id}", "ChannelCreated", new { channelId = id, name, topic = (b.Topic ?? "").Trim(), isPrivate = b.IsPrivate == true, kind, creatorId = me, projectId = b.ProjectId }, me),
                    new NewEvent($"channel-{id}", "MemberAdded", new { channelId = id, memberId = me }, me, CausedByIndex: 0),
                };
            });
            if (taken) return Err(409, "There's already a channel with that name.");
            await rt.RefreshAll();
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { id });
        });

        authed.MapPatch("/channels/{id:long}", async (long id, ChannelBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            string? name = null;
            ChannelRow ch;
            using (var c = db.Open())
            {
                ch = Q.Channel(c, id)!;
                if (ch is null || !Access.CanSee(c, id, me)) return Err(404, "No such channel.");
                if (!Access.CanManage(c, ch, me)) return Err(403, "Only an admin or the channel's creator can change this.");
                if (b.Name is not null)
                {
                    name = NormaliseChannel(b.Name);
                    if (!ChannelRx.IsMatch(name)) return Err(400, "Channel names are lowercase letters, numbers, - and _.");
                    if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM channels WHERE name=@name AND id<>@id", new { name, id }) > 0) return Err(409, "There's already a channel with that name.");
                }
            }
            var taken = false;
            store.Append(d =>
            {
                var now = d.Query<ChannelRow>("SELECT * FROM channels WHERE id=@id", new { id }).FirstOrDefault();
                if (now is null) return Array.Empty<NewEvent>();
                taken = name is not null && d.Scalar<int>("SELECT COUNT(*) FROM channels WHERE name=@name AND id<>@id", new { name, id }) > 0;
                if (taken) return Array.Empty<NewEvent>();
                var evs = new List<NewEvent>();
                if (name is not null && name != now.Name) evs.Add(new NewEvent($"channel-{id}", "ChannelRenamed", new { channelId = id, name, from = now.Name }, me));
                if (b.Topic is not null && b.Topic.Trim() != now.Topic) evs.Add(new NewEvent($"channel-{id}", "TopicChanged", new { channelId = id, topic = b.Topic.Trim() }, me));
                if (b.IsPrivate is { } priv && priv != now.IsPrivate) evs.Add(new NewEvent($"channel-{id}", priv ? "ChannelMadePrivate" : "ChannelMadePublic", new { channelId = id }, me));
                return evs;
            });
            if (taken) return Err(409, "There's already a channel with that name.");
            await rt.RefreshAll();
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/channels/{id:long}", async (long id, HttpContext ctx, Db db, Realtime rt, VoicePresence voice, IEventStore store) =>
        {
            var me = Me(ctx);
            string name;
            using (var c = db.Open())
            {
                var ch = Q.Channel(c, id);
                if (ch is null || !Access.CanSee(c, id, me)) return Err(404, "No such channel.");
                if (!Access.CanManage(c, ch, me)) return Err(403, "Only an admin or the channel's creator can delete it.");
                name = ch.Name;
            }
            store.Append(new[] { new NewEvent($"channel-{id}", "ChannelDeleted", new { channelId = id, name }, me) });      // the projections drop the channel, its posts and threads
            voice.ClearChannel(id);
            await rt.RefreshAll();
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapPut("/channels/{id:long}/members/{memberId:long}", async (long id, long memberId, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            bool already;
            using (var c = db.Open())
            {
                var ch = Q.Channel(c, id);
                if (ch is null || !Access.CanSee(c, id, me)) return Err(404, "No such channel.");
                var target = Q.Member(c, memberId);
                if (target is null || target.Removed) return Err(404, "No such member.");
                // managers add anyone; an agent's owner may bring their own agent into a channel they can see
                var ownAgent = target.Kind == "agent" && target.OwnerId == me;
                if (!Access.CanManage(c, ch, me) && !ownAgent) return Err(403, "Only an admin or the channel's creator can add people.");
                already = Access.IsChannelMember(c, id, memberId);
            }
            if (!already) store.Append(new[] { new NewEvent($"channel-{id}", "MemberAdded", new { channelId = id, memberId }, me) });
            await rt.Refresh(memberId);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/channels/{id:long}/members/{memberId:long}", async (long id, long memberId, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            bool was;
            using (var c = db.Open())
            {
                var ch = Q.Channel(c, id);
                if (ch is null || !Access.CanSee(c, id, me)) return Err(404, "No such channel.");
                var target = Q.Member(c, memberId);
                if (target is null) return Err(404, "No such member.");
                var ownAgent = target.Kind == "agent" && target.OwnerId == me;
                if (!Access.CanManage(c, ch, me) && !ownAgent && memberId != me) return Err(403, "Only an admin or the channel's creator can remove people.");
                if (memberId == ch.CreatorId && target.Kind == "person" && ch.IsPrivate) return Err(400, "The creator can't be removed from their private channel.");
                was = Access.IsChannelMember(c, id, memberId);
            }
            if (was) store.Append(new[] { new NewEvent($"channel-{id}", "MemberRemoved", new { channelId = id, memberId }, me) });
            await rt.Refresh(memberId);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });
    }

    static string NormaliseChannel(string? n) => (n ?? "").Trim().TrimStart('#').Replace(' ', '-').ToLowerInvariant();
}
