using Dapper;
using Microsoft.AspNetCore.Routing;

namespace CoWork.Server;

public static partial class Api
{
    static void MapPeople(RouteGroupBuilder authed)
    {
        authed.MapPut("/people/{id:long}/role", async (long id, RoleBody b, HttpContext ctx, Db db, Realtime rt) =>
        {
            using (var c = db.Open())
            {
                var me = Me(ctx);
                if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
                if (b.Role is not ("admin" or "member")) return Err(400, "Role must be admin or member.");
                if (!Access.IsActivePerson(c, id)) return Err(404, "No such person.");
                if (b.Role == "member" && c.ExecuteScalar<int>("SELECT COUNT(*) FROM people p JOIN members m ON m.id=p.member_id WHERE p.role='admin' AND m.removed=0 AND p.member_id<>@id", new { id }) == 0)
                    return Err(400, "There has to be at least one admin.");
                c.Execute("UPDATE people SET role=@role WHERE member_id=@id", new { role = b.Role, id });
            }
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/people/{id:long}", async (long id, HttpContext ctx, Db db, Realtime rt, VoicePresence voice) =>
        {
            var agentsToDrop = new List<long>();
            using (var c = db.Open())
            {
                var me = Me(ctx);
                if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
                if (id == me) return Err(400, "You can't remove yourself.");
                if (!Access.IsActivePerson(c, id)) return Err(404, "No such person.");
                using var tx = c.BeginTransaction();
                c.Execute("UPDATE members SET removed=1 WHERE id=@id", new { id }, tx);
                c.Execute("DELETE FROM channel_members WHERE member_id=@id", new { id }, tx);
                // their agents go with them
                agentsToDrop = c.Query<long>("SELECT member_id FROM agents WHERE owner_id=@id", new { id }, tx).ToList();
                tx.Commit();
            }
            voice.RemoveMember(id);
            foreach (var a in agentsToDrop) await RemoveAgent(a, db, rt);
            await rt.Refresh(id);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });
    }
}
