using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Routing;

namespace CoWork.Server;

public record NetworkBody(string[]? Hosts);
public record SetupBody2(string? Command);
public record BudgetBody(long? MonthTokens, double? MonthUsd, long? ThreadTokens, double? ThreadUsd);

/// <summary>Brief 7, round 2: a project's network list, setup command, budget and Pause all agents (leads and admins), and raising one
/// thread's cap (a lead of its project, or an admin).</summary>
public static partial class Api
{
    static readonly Regex HostRx = new(@"^(\*\.)?[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$");

    static void MapLocked(RouteGroupBuilder authed)
    {
        authed.MapPut("/projects/{id:long}/network", async (long id, NetworkBody b, HttpContext ctx, Db db, IEventStore store, Realtime rt) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me)) return Err(403, "Only the project's leads or an admin can change its network list.");
            }
            var hosts = (b.Hosts ?? Array.Empty<string>()).Select(h => h.Trim().TrimEnd('.').ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToArray();
            if (hosts.FirstOrDefault(h => !HostRx.IsMatch(h)) is { } bad) return Err(400, $"“{bad}” isn't a host name (like github.com, or *.npmjs.org for every name under it).");
            store.Append(new[] { new NewEvent($"project-{id}", "ProjectNetworkChanged", new { projectId = id, hosts }, me) });
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapPut("/projects/{id:long}/setup", async (long id, SetupBody2 b, HttpContext ctx, Db db, IEventStore store, Realtime rt) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me)) return Err(403, "Only the project's leads or an admin can change its setup command.");
            }
            var command = (b.Command ?? "").Trim();
            if (command.Length > 500) return Err(400, "Keep the setup command to 500 characters; put longer steps in a script in the repository.");
            store.Append(new[] { new NewEvent($"project-{id}", "SetupCommandChanged", new { projectId = id, command = command.Length == 0 ? null : command }, me) });
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapPut("/projects/{id:long}/budget", async (long id, BudgetBody b, HttpContext ctx, Db db, IEventStore store, Realtime rt) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me)) return Err(403, "Only the project's leads or an admin can change its budget.");
            }
            if (new double?[] { b.MonthTokens, b.MonthUsd, b.ThreadTokens, b.ThreadUsd }.Any(v => v is < 0)) return Err(400, "A cap can't be below zero.");
            store.Append(new[] { new NewEvent($"project-{id}", "BudgetSet", new { projectId = id, monthTokens = b.MonthTokens, monthUsd = b.MonthUsd, threadTokens = b.ThreadTokens, threadUsd = b.ThreadUsd }, me) });
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // a lead raises one thread's cap: it gets the project's per-thread cap again
        authed.MapPost("/threads/{id:long}/budget/raise", async (long id, HttpContext ctx, Db db, IEventStore store, Dispatcher d) =>
        {
            var me = Me(ctx);
            long project;
            using (var c = db.Open())
            {
                var p = c.ExecuteScalar<long?>("SELECT ch.project_id FROM threads t JOIN channels ch ON ch.id=t.channel_id WHERE t.root_id=@id", new { id });
                var ch = c.ExecuteScalar<long?>("SELECT channel_id FROM threads WHERE root_id=@id", new { id });
                if (p is null || ch is null || !Access.CanSee(c, ch.Value, me)) return Err(404, "No such thread.");
                if (!Access.CanEditProject(c, p.Value, me)) return Err(403, "Only the project's leads or an admin can raise a thread's cap.");
                project = p.Value;
            }
            string? refused = null;
            store.Append(x =>
            {
                refused = null;
                var caps = x.Query<Budgets.Caps>("SELECT month_tokens, month_usd, thread_tokens, thread_usd FROM budgets WHERE project_id=@project", new { project }).FirstOrDefault();
                if (caps is null || (caps.ThreadTokens is null && caps.ThreadUsd is null)) { refused = "This project has no cap per thread to raise."; return Array.Empty<NewEvent>(); }
                var extra = x.Query<(long Tokens, double Usd)>("SELECT extra_tokens, extra_usd FROM thread_budgets WHERE thread_id=@id", new { id }).FirstOrDefault();
                var parts = new List<string>();
                if (caps.ThreadTokens is { } t) parts.Add(Budgets.Count(t * 2 + extra.Tokens, "tokens"));
                if (caps.ThreadUsd is { } u) parts.Add(Budgets.Count(u * 2 + extra.Usd, "usd"));
                return new[] { new NewEvent($"thread-{id}", "ThreadBudgetRaised", new
                {
                    projectId = project, tokens = caps.ThreadTokens ?? 0, usd = caps.ThreadUsd ?? 0, postId = x.NextId("posts"),
                    channelId = x.Scalar<long>("SELECT channel_id FROM threads WHERE root_id=@id", new { id }), text = $"raised this thread's cap to {string.Join(" and ", parts)}.",
                }, me) };
            });
            if (refused is not null) return Err(400, refused);
            return Results.Ok(new { ok = true });
        });

        // Reopen a done thread: its outcome stays as what was done then, and the thread isn't done any more
        authed.MapPost("/threads/{id:long}/reopen", async (long id, HttpContext ctx, Db db, IEventStore store, Dispatcher d) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                var ch = c.ExecuteScalar<long?>("SELECT channel_id FROM threads WHERE root_id=@id", new { id });
                if (ch is null || !Access.CanSee(c, ch.Value, me)) return Err(404, "No such thread.");
                if (c.ExecuteScalar<long?>("SELECT resolved_post FROM threads WHERE root_id=@id", new { id }) is null) return Results.Ok(new { ok = true });
            }
            store.Append(new[] { new NewEvent($"thread-{id}", "ThreadReopened", new { threadId = id }, me) });
            await d.BroadcastThread(id);
            return Results.Ok(new { ok = true });
        });

        authed.MapPost("/projects/{id:long}/pause", async (long id, HttpContext ctx, Db db, IEventStore store, Realtime rt, Sandboxes sandboxes, Dispatcher d) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me)) return Err(403, "Only the project's leads or an admin can pause its agents.");
                if (c.ExecuteScalar<long?>("SELECT paused_at FROM projects WHERE id=@id", new { id }) is not null) return Results.Ok(new { ok = true });
            }
            store.Append(new[] { new NewEvent($"project-{id}", "ProjectAgentsPaused", new { projectId = id }, me) });
            await sandboxes.PauseProject(id, me);
            await Threads(db, id, d);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapPost("/projects/{id:long}/resume", async (long id, HttpContext ctx, Db db, IEventStore store, Realtime rt, Dispatcher d) =>
        {
            var me = Me(ctx);
            List<long> agents;
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me)) return Err(403, "Only the project's leads or an admin can resume its agents.");
                if (c.ExecuteScalar<long?>("SELECT paused_at FROM projects WHERE id=@id", new { id }) is null) return Results.Ok(new { ok = true });
                agents = c.Query<long>(@"SELECT agent_id FROM project_agents WHERE project_id=@id UNION SELECT ta.agent_id FROM thread_agents ta JOIN threads t ON t.root_id=ta.thread_id
                    JOIN channels ch ON ch.id=t.channel_id WHERE ch.project_id=@id", new { id }).ToList();
            }
            store.Append(new[] { new NewEvent($"project-{id}", "ProjectAgentsResumed", new { projectId = id }, me) });
            foreach (var a in agents) await d.Pump(a);           // what waited goes out
            await Threads(db, id, d);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>Every thread of the project shows its agents' new state.</summary>
    static async Task Threads(Db db, long project, Dispatcher d)
    {
        List<long> roots;
        using (var c = db.Open()) roots = c.Query<long>("SELECT t.root_id FROM threads t JOIN channels ch ON ch.id=t.channel_id WHERE ch.project_id=@project", new { project }).ToList();
        foreach (var r in roots) await d.BroadcastThread(r);
    }

    /// <summary>What the project's settings page shows of round 2: its network list, setup command, budget with this month's use, and pause.</summary>
    static object Locked(Microsoft.Data.Sqlite.SqliteConnection c, long id)
    {
        var caps = Budgets.Of(c, id);
        var month = Budgets.Month(Db.Now());
        var used = Budgets.MonthUse(c, id, month);
        var byAgent = c.Query<(string Handle, long Tokens, double? Cost)>(@"SELECT m.handle, u.tokens, u.cost FROM project_usage u JOIN members m ON m.id=u.agent_id
            WHERE u.project_id=@id AND u.month=@month ORDER BY u.tokens DESC", new { id, month }).Select(x => new { handle = x.Handle, tokens = x.Tokens, cost = x.Cost }).ToList();
        var paused = c.QueryFirstOrDefault<(long? By, long? At)>("SELECT paused_by, paused_at FROM projects WHERE id=@id", new { id });
        return new
        {
            network = c.Query<string>("SELECT host FROM project_network WHERE project_id=@id ORDER BY host", new { id }).ToList(),
            setupCommand = c.ExecuteScalar<string?>("SELECT setup_command FROM projects WHERE id=@id", new { id }),
            budget = new
            {
                monthTokens = caps.MonthTokens, monthUsd = caps.MonthUsd, threadTokens = caps.ThreadTokens, threadUsd = caps.ThreadUsd,
                month = Budgets.MonthName(month), usedTokens = used.Tokens, usedCost = used.Cost, costReported = used.Cost is not null, byAgent,
            },
            paused = paused.At is null ? null : new { by = Q.Member(c, paused.By ?? 0)?.DisplayName, at = paused.At },
        };
    }
}
