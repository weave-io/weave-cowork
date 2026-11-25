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
        authed.MapPost("/agents", async (AgentBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            var handle = (b.Handle ?? "").Trim().TrimStart('@').ToLowerInvariant();
            if (!HandleRx.IsMatch(handle)) return Err(400, "Agent names are 2–31 characters: lowercase letters, numbers, - and _.");
            var cmd = ParseCommand(string.IsNullOrWhiteSpace(b.HarnessCommand) ? "opencode acp" : b.HarnessCommand);
            if (cmd.Length == 0) return Err(400, "Add the harness command.");
            if (string.IsNullOrWhiteSpace(b.Workspace)) return Err(400, "Add the workspace folder the agent works in.");
            var code = Tokens.NewCode();
            long id = 0; var taken = false;
            store.Append(d =>
            {
                taken = d.Scalar<int>("SELECT COUNT(*) FROM members WHERE handle=@handle", new { handle }) > 0;
                if (taken) return Array.Empty<NewEvent>();
                id = d.NextId("members");
                var expires = Db.Now() + 10 * 60_000;
                d.Execute("INSERT OR REPLACE INTO runner_secrets(agent_id, token_hash, code_hash) VALUES (@id, NULL, @h)", new { id, h = Tokens.Hash(code) });
                return new[]
                {
                    new NewEvent($"agent-{id}", "AgentCreated", new
                    {
                        agentId = id, handle, displayName = string.IsNullOrWhiteSpace(b.DisplayName) ? handle : b.DisplayName.Trim(), ownerId = me,
                        about = b.About ?? "", instructions = b.Instructions ?? "", harnessCommand = cmd, workspace = b.Workspace.Trim(), recordToolOutput = false,
                    }, me),
                    new NewEvent($"agent-{id}", "EnrolmentCodeIssued", new { agentId = id, expiresAt = expires }, me, CausedByIndex: 0),
                };
            });
            if (taken) return Err(409, "That name is taken.");
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
        authed.MapPut("/agents/{id:long}", async (long id, AgentEditBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var cmd = ParseCommand(b.HarnessCommand ?? "");
            if (cmd.Length == 0) return Err(400, "Add the harness command.");
            if (string.IsNullOrWhiteSpace(b.Workspace)) return Err(400, "Add the workspace folder the agent works in.");
            var me = Me(ctx);
            using (var c = db.Open())
                if (!CanAdminAgent(c, id, me)) return Err(403, "Only the agent's owner or an admin can do that.");
            store.Append(new[] { new NewEvent($"agent-{id}", "AgentEdited", new
            {
                agentId = id, displayName = string.IsNullOrWhiteSpace(b.DisplayName) ? null : b.DisplayName.Trim(), about = b.About ?? "", instructions = b.Instructions ?? "",
                harnessCommand = cmd, workspace = b.Workspace.Trim(), recordToolOutput = b.RecordToolOutput,
            }, me) });
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // a fresh one-time code for an agent whose runner needs to (re)connect
        authed.MapPost("/agents/{id:long}/code", (long id, HttpContext ctx, Db db, IEventStore store) =>
        {
            var me = Me(ctx);
            string handle;
            using (var c = db.Open())
            {
                if (!CanAdminAgent(c, id, me)) return Err(403, "Only the agent's owner or an admin can do that.");
                handle = Q.Member(c, id)!.Handle;
            }
            var code = Tokens.NewCode();
            store.Append(d =>
            {
                d.Execute("INSERT INTO runner_secrets(agent_id, code_hash) VALUES (@id,@h) ON CONFLICT(agent_id) DO UPDATE SET code_hash=@h", new { id, h = Tokens.Hash(code) });
                return new[] { new NewEvent($"agent-{id}", "EnrolmentCodeIssued", new { agentId = id, expiresAt = Db.Now() + 10 * 60_000 }, me) };
            });
            return Results.Ok(CodeResponse(ctx, id, code, handle));
        });

        authed.MapPost("/agents/{id:long}/disconnect", async (long id, HttpContext ctx, Db db, Realtime rt, RunnerRegistry runners, IHubContext<RunnerHub> hub, IEventStore store) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
                if (!CanAdminAgent(c, id, me)) return Err(403, "Only the agent's owner or an admin can do that.");
            store.Append(d =>
            {
                d.Execute("UPDATE runner_secrets SET token_hash=NULL WHERE agent_id=@id", new { id });
                return new[] { new NewEvent($"agent-{id}", "TokenRevoked", new { agentId = id }, me) };
            });
            await CutOff(id, runners, hub);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/agents/{id:long}", async (long id, HttpContext ctx, Db db, Realtime rt) =>
        {
            using (var c = db.Open())
                if (!CanAdminAgent(c, id, Me(ctx))) return Err(403, "Only the agent's owner or an admin can do that.");
            await RemoveAgent(id, Me(ctx), db, rt);
            return Results.Ok(new { ok = true });
        });

        // a runner trades its one-time code for a token
        api.MapPost("/runner/enrol", async (EnrolRequest r, Db db, Realtime rt, IEventStore store) =>
        {
            var token = Tokens.NewToken();
            var codeHash = Tokens.Hash((r.Code ?? "").Trim().ToUpperInvariant());
            string? error = null; long agentId = 0; string handle = "";
            store.Append(d =>
            {
                error = null;
                // "a code is used once and only before it expires" is decided here, inside the write, so two runners can't both use it
                var row = d.Query<EnrolRow>(
                    @"SELECT a.member_id AS id, m.handle, COALESCE(a.code_expires,0) AS exp FROM runner_secrets rs JOIN agents a ON a.member_id=rs.agent_id JOIN members m ON m.id=a.member_id
                      WHERE rs.code_hash=@codeHash AND m.removed=0", new { codeHash }).FirstOrDefault();
                if (row is null) { error = "That code isn't valid. It may have been used already."; return Array.Empty<NewEvent>(); }
                if (row.Exp < Db.Now()) { error = "That code has expired. Ask for a new one."; return Array.Empty<NewEvent>(); }
                agentId = row.Id; handle = row.Handle;
                d.Execute("UPDATE runner_secrets SET token_hash=@t, code_hash=NULL WHERE agent_id=@agentId", new { t = Tokens.Hash(token), agentId });
                return new[] { new NewEvent($"agent-{agentId}", "RunnerEnrolled", new { agentId, host = r.Host }, agentId) };
            });
            if (error is not null) return Err(400, error);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new EnrolResponse(agentId, handle, token));
        });
    }

    sealed class EnrolRow { public long Id { get; set; } public string Handle { get; set; } = ""; public long Exp { get; set; } }

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

    /// <summary>Takes an agent out: its turns end, its secrets are cleared, and one event says so (the projections drop its token, runner and channel places).</summary>
    public static async Task RemoveAgent(long id, long? by, Db db, Realtime rt)
    {
        var runners = rt.Services.GetRequiredService<RunnerRegistry>();
        var hub = rt.Services.GetRequiredService<IHubContext<RunnerHub>>();
        var store = rt.Services.GetRequiredService<IEventStore>();
        rt.Services.GetRequiredService<Dispatcher>().AbandonAgent(id, "the agent was removed");
        store.Append(d =>
        {
            d.Execute("UPDATE runner_secrets SET token_hash=NULL, code_hash=NULL WHERE agent_id=@id", new { id });
            return new[] { new NewEvent($"agent-{id}", "AgentRemoved", new { agentId = id }, by) };
        });
        await CutOff(id, runners, hub);
        await rt.ToAll("bootstrap-changed");
    }
}
