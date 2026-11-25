using Dapper;
using Microsoft.AspNetCore.Routing;

namespace CoWork.Server;

public static partial class Api
{
    static void MapPeople(RouteGroupBuilder authed)
    {
        authed.MapPut("/people/{id:long}/role", async (long id, RoleBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
                if (b.Role is not ("admin" or "member")) return Err(400, "Role must be admin or member.");
                if (!Access.IsActivePerson(c, id)) return Err(404, "No such person.");
            }
            var lastAdmin = false;
            store.Append(d =>
            {
                lastAdmin = b.Role == "member" && d.Scalar<int>("SELECT COUNT(*) FROM people p JOIN members m ON m.id=p.member_id WHERE p.role='admin' AND m.removed=0 AND p.member_id<>@id", new { id }) == 0;
                if (lastAdmin || d.Scalar<string>("SELECT role FROM people WHERE member_id=@id", new { id }) == b.Role) return Array.Empty<NewEvent>();
                return new[] { new NewEvent("server", "RoleChanged", new { memberId = id, role = b.Role }, me) };
            });
            if (lastAdmin) return Err(400, "There has to be at least one admin.");
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/people/{id:long}", async (long id, HttpContext ctx, Db db, Realtime rt, VoicePresence voice, IEventStore store) =>
        {
            List<long> agentsToDrop;
            using (var c = db.Open())
            {
                var me = Me(ctx);
                if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
                if (id == me) return Err(400, "You can't remove yourself.");
                if (!Access.IsActivePerson(c, id)) return Err(404, "No such person.");
                agentsToDrop = c.Query<long>("SELECT member_id FROM agents WHERE owner_id=@id", new { id }).ToList();       // their agents go with them
            }
            store.Append(new[] { new NewEvent("server", "PersonRemoved", new { memberId = id }, Me(ctx)) });
            voice.RemoveMember(id);
            foreach (var a in agentsToDrop) await RemoveAgent(a, Me(ctx), db, rt);
            await rt.Refresh(id);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });
    }
}
