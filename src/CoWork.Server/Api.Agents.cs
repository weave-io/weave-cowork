using System.Text.Json;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace CoWork.Server;

public static partial class Api
{
    /// <summary>Splits "opencode acp" into ["opencode","acp"]; double quotes keep an argument together.</summary>
    public static string[] ParseCommand(string s)
    {
        var parts = new List<string>(); var cur = new System.Text.StringBuilder(); var q = false; var any = false;
        foreach (var ch in s)
        {
            if (ch == '"') { q = !q; any = true; }
            else if (char.IsWhiteSpace(ch) && !q) { if (any || cur.Length > 0) parts.Add(cur.ToString()); cur.Clear(); any = false; }
            else cur.Append(ch);
        }
        if (any || cur.Length > 0) parts.Add(cur.ToString());
        return parts.ToArray();
    }

    static void MapAgents(RouteGroupBuilder api, RouteGroupBuilder authed)
    {
        authed.MapPost("/agents", async (AgentBody b, HttpContext ctx, Db db, Realtime rt) =>
        {
            var me = Me(ctx);
            var handle = (b.Handle ?? "").Trim().TrimStart('@').ToLowerInvariant();
            if (!HandleRx.IsMatch(handle)) return Err(400, "Agent names are 2–31 characters: lowercase letters, numbers, - and _.");
            var cmd = ParseCommand(string.IsNullOrWhiteSpace(b.HarnessCommand) ? "opencode acp" : b.HarnessCommand);
            if (cmd.Length == 0) return Err(400, "Add the harness command.");
            if (string.IsNullOrWhiteSpace(b.Workspace)) return Err(400, "Add the workspace folder the agent works in.");
            var code = Tokens.NewCode();
            long id;
            using (var c = db.Open())
            {
                if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM members WHERE handle=@handle", new { handle }) > 0) return Err(409, "That name is taken.");
                using var tx = c.BeginTransaction();
                id = c.ExecuteScalar<long>("INSERT INTO members(kind, handle, display_name, created_at) VALUES ('agent',@handle,@n,@now); SELECT last_insert_rowid();",
                    new { handle, n = string.IsNullOrWhiteSpace(b.DisplayName) ? handle : b.DisplayName.Trim(), now = Db.Now() }, tx);
                c.Execute(@"INSERT INTO agents(member_id, owner_id, about, instructions, harness_command, workspace, code_hash, code_expires)
                            VALUES (@id,@me,@about,@ins,@cmd,@ws,@ch,@exp)",
                    new { id, me, about = b.About ?? "", ins = b.Instructions ?? "", cmd = JsonSerializer.Serialize(cmd), ws = b.Workspace.Trim(), ch = Tokens.Hash(code), exp = Db.Now() + 10 * 60_000 }, tx);
                tx.Commit();
            }
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(CodeResponse(ctx, id, code, handle));
        });

        // an agent's settings, for its owner or an admin (the instructions aren't in the member list)
        authed.MapGet("/agents/{id:long}", (long id, HttpContext ctx, Db db) =>
        {
            using var c = db.Open();
            if (!CanAdminAgent(c, id, Me(ctx))) return Err(403, "Only the agent's owner or an admin can do that.");
            var m = Q.Member(c, id)!;
            return Results.Ok(new { id, handle = m.Handle, displayName = m.DisplayName, about = m.About ?? "", instructions = m.Instructions ?? "",
                harnessCommand = string.Join(' ', JsonSerializer.Deserialize<string[]>(m.HarnessCommand!)!.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a)), workspace = m.Workspace,
                recordToolOutput = c.ExecuteScalar<int>("SELECT record_tool_output FROM agents WHERE member_id=@id", new { id }) == 1 });
        });

        // edit an agent; new threads use the new settings, running sessions keep theirs
        authed.MapPut("/agents/{id:long}", async (long id, AgentEditBody b, HttpContext ctx, Db db, Realtime rt) =>
        {
            var cmd = ParseCommand(b.HarnessCommand ?? "");
            if (cmd.Length == 0) return Err(400, "Add the harness command.");
            if (string.IsNullOrWhiteSpace(b.Workspace)) return Err(400, "Add the workspace folder the agent works in.");
            using (var c = db.Open())
            {
                if (!CanAdminAgent(c, id, Me(ctx))) return Err(403, "Only the agent's owner or an admin can do that.");
                c.Execute("UPDATE agents SET about=@about, instructions=@ins, harness_command=@cmd, workspace=@ws WHERE member_id=@id",
                    new { about = b.About ?? "", ins = b.Instructions ?? "", cmd = JsonSerializer.Serialize(cmd), ws = b.Workspace.Trim(), id });
                if (b.RecordToolOutput is { } rec) c.Execute("UPDATE agents SET record_tool_output=@rec WHERE member_id=@id", new { rec = rec ? 1 : 0, id });
                if (!string.IsNullOrWhiteSpace(b.DisplayName)) c.Execute("UPDATE members SET display_name=@n WHERE id=@id", new { n = b.DisplayName.Trim(), id });
            }
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // a fresh one-time code for an agent whose runner needs to (re)connect
        authed.MapPost("/agents/{id:long}/code", (long id, HttpContext ctx, Db db) =>
        {
            using var c = db.Open();
            if (!CanAdminAgent(c, id, Me(ctx))) return Err(403, "Only the agent's owner or an admin can do that.");
            var code = Tokens.NewCode();
            c.Execute("UPDATE agents SET code_hash=@h, code_expires=@exp WHERE member_id=@id", new { h = Tokens.Hash(code), exp = Db.Now() + 10 * 60_000, id });
            return Results.Ok(CodeResponse(ctx, id, code, Q.Member(c, id)!.Handle));
        });

        authed.MapPost("/agents/{id:long}/disconnect", async (long id, HttpContext ctx, Db db, Realtime rt, RunnerRegistry runners, IHubContext<RunnerHub> hub) =>
        {
            using (var c = db.Open())
            {
                if (!CanAdminAgent(c, id, Me(ctx))) return Err(403, "Only the agent's owner or an admin can do that.");
                c.Execute("UPDATE agents SET token_hash=NULL, runner_host=NULL WHERE member_id=@id", new { id });
            }
            await CutOff(id, runners, hub);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/agents/{id:long}", async (long id, HttpContext ctx, Db db, Realtime rt) =>
        {
            using (var c = db.Open())
                if (!CanAdminAgent(c, id, Me(ctx))) return Err(403, "Only the agent's owner or an admin can do that.");
            await RemoveAgent(id, db, rt);
            return Results.Ok(new { ok = true });
        });

        // a runner trades its one-time code for a token
        api.MapPost("/runner/enrol", async (EnrolRequest r, Db db, Realtime rt) =>
        {
            var token = Tokens.NewToken();
            using var c = db.Open();
            var row = c.QueryFirstOrDefault<(long Id, string Handle, long Exp)?>(
                @"SELECT a.member_id, m.handle, COALESCE(a.code_expires,0) FROM agents a JOIN members m ON m.id=a.member_id WHERE a.code_hash=@h AND m.removed=0",
                new { h = Tokens.Hash((r.Code ?? "").Trim().ToUpperInvariant()) });
            if (row is null) return Err(400, "That code isn't valid. It may have been used already.");
            if (row.Value.Exp < Db.Now()) return Err(400, "That code has expired. Ask for a new one.");
            c.Execute("UPDATE agents SET token_hash=@t, code_hash=NULL, code_expires=NULL, runner_host=@host WHERE member_id=@id",
                new { t = Tokens.Hash(token), host = r.Host, id = row.Value.Id });
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new EnrolResponse(row.Value.Id, row.Value.Handle, token));
        });
    }

    static object CodeResponse(HttpContext ctx, long id, string code, string? handle = null) =>
        new { agentId = id, handle, code, expiresInMinutes = 10, command = $"cowork-runner connect {ctx.Request.Scheme}://{ctx.Request.Host} --code {code}" };

    static bool CanAdminAgent(Microsoft.Data.Sqlite.SqliteConnection c, long agentId, long me)
    {
        var owner = c.QueryFirstOrDefault<long?>("SELECT owner_id FROM agents WHERE member_id=@agentId", new { agentId });
        return owner is not null && Access.IsActivePerson(c, me) && (owner == me || Access.IsAdmin(c, me));
    }

    static async Task CutOff(long agentId, RunnerRegistry runners, IHubContext<RunnerHub> hub)
    {
        var conn = runners.ConnectionOf(agentId);
        if (conn is not null) { await hub.Clients.Client(conn).SendAsync("Revoked"); runners.ForceRemove(agentId); }
    }

    static async Task RemoveAgent(long id, Db db, Realtime rt)
    {
        var runners = rt.Services.GetRequiredService<RunnerRegistry>();
        var hub = rt.Services.GetRequiredService<IHubContext<RunnerHub>>();
        rt.Services.GetRequiredService<Dispatcher>().AbandonAgent(id, "the agent was removed");
        using (var c = db.Open())
        {
            using var tx = c.BeginTransaction();
            c.Execute("UPDATE members SET removed=1 WHERE id=@id", new { id }, tx);
            c.Execute("UPDATE agents SET token_hash=NULL, code_hash=NULL, runner_host=NULL WHERE member_id=@id", new { id }, tx);
            c.Execute("DELETE FROM channel_members WHERE member_id=@id", new { id }, tx);
            c.Execute("UPDATE deliveries SET status='failed' WHERE agent_id=@id AND status IN ('queued','sent')", new { id }, tx);
            tx.Commit();
        }
        await CutOff(id, runners, hub);
        await rt.ToAll("bootstrap-changed");
    }
}
