using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace CoWork.Server;

public record PoolBody(string? Name);
public record ModelsBody(string? Kind, string? BaseUrl, string? ApiKey);
public record SandboxSettingsBody(int? IdleSeconds, int? RetentionSeconds = null);

public static partial class Api
{
    static void MapPools(RouteGroupBuilder api, RouteGroupBuilder authed)
    {
        // Where agents run → Add a pool: a one-time code and the command to run on the pool's machine
        authed.MapPost("/pools", (PoolBody b, HttpContext ctx, Db db, IEventStore store) =>
        {
            var me = Me(ctx);
            using (var c = db.Open()) if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
            var code = Tokens.NewCode();
            long id = 0; string name = ""; string? refused = null;
            store.Append(d =>
            {
                refused = null;
                id = d.NextId("pools");
                name = string.IsNullOrWhiteSpace(b.Name) ? $"pool-{id:00}" : b.Name.Trim().ToLowerInvariant();
                if (!HandleRx.IsMatch(name)) { refused = "Pool names are 2–31 characters: lowercase letters, numbers, - and _."; return Array.Empty<NewEvent>(); }
                if (d.Scalar<int>("SELECT COUNT(*) FROM pools WHERE name=@name", new { name }) > 0) { refused = "There's already a pool with that name."; return Array.Empty<NewEvent>(); }
                d.Execute("INSERT OR REPLACE INTO pool_secrets(pool_id, token_hash, code_hash) VALUES (@id, NULL, @h)", new { id, h = Tokens.Hash(code) });
                return new[] { new NewEvent($"pool-{id}", "PoolCreated", new { poolId = id, name, expiresAt = Db.Now() + 10 * 60_000 }, me) };
            });
            if (refused is not null) return Err(400, refused);
            return Results.Ok(PoolCode(ctx, id, name, code));
        });

        authed.MapPost("/pools/{id:long}/code", (long id, HttpContext ctx, Db db, IEventStore store) =>
        {
            var me = Me(ctx);
            string? name;
            using (var c = db.Open())
            {
                if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
                name = c.ExecuteScalar<string?>("SELECT name FROM pools WHERE id=@id AND removed=0", new { id });
            }
            if (name is null) return Err(404, "No such pool.");
            var code = Tokens.NewCode();
            store.Append(d =>
            {
                d.Execute("INSERT INTO pool_secrets(pool_id, code_hash) VALUES (@id,@h) ON CONFLICT(pool_id) DO UPDATE SET code_hash=@h", new { id, h = Tokens.Hash(code) });
                return new[] { new NewEvent($"pool-{id}", "PoolCodeIssued", new { poolId = id, expiresAt = Db.Now() + 10 * 60_000 }, me) };
            });
            return Results.Ok(PoolCode(ctx, id, name, code));
        });

        // a pool trades its one-time code for a token that can only receive sandbox commands and report state
        api.MapPost("/pools/enrol", async (PoolEnrolRequest r, IEventStore store, Realtime rt) =>
        {
            var token = Tokens.NewToken();
            var codeHash = Tokens.Hash((r.Code ?? "").Trim().ToUpperInvariant());
            string? error = null; long poolId = 0; var name = ""; string serverId = "";
            var slots = Math.Clamp(r.Slots, 1, 64);
            store.Append(d =>
            {
                error = null;
                var row = d.Query<(long Id, string Name, long? Exp)>("SELECT p.id, p.name, p.code_expires FROM pool_secrets ps JOIN pools p ON p.id=ps.pool_id WHERE ps.code_hash=@codeHash AND p.removed=0", new { codeHash }).FirstOrDefault();
                if (row.Name is null) { error = "That code isn't valid. It may have been used already."; return Array.Empty<NewEvent>(); }
                if ((row.Exp ?? 0) < Db.Now()) { error = "That code has expired. Ask for a new one."; return Array.Empty<NewEvent>(); }
                poolId = row.Id; name = row.Name;
                d.Execute("UPDATE pool_secrets SET token_hash=@t, code_hash=NULL WHERE pool_id=@poolId", new { t = Tokens.Hash(token), poolId });
                var evs = new List<NewEvent>();
                // the server's id, made once: a pool names what it makes after it, so a new server on the same address never meets an old one's volumes
                serverId = d.Scalar<string?>("SELECT value FROM settings WHERE key='server_id'") ?? "";
                if (serverId.Length == 0) { serverId = Guid.NewGuid().ToString("N")[..12]; evs.Add(new NewEvent("server", "ServerIdAssigned", new { serverId })); }
                evs.Add(new NewEvent($"pool-{poolId}", "PoolEnrolled", new { poolId, host = r.Host, slots, memoryMb = r.MemoryMb > 0 ? r.MemoryMb : (long?)null }));
                return evs;
            });
            if (error is not null) return Err(400, error);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new PoolEnrolResponse(poolId, name, token, serverId));
        });

        authed.MapDelete("/pools/{id:long}", async (long id, HttpContext ctx, Db db, IEventStore store, PoolRegistry pools, IHubContext<PoolHub> hub, Realtime rt) =>
        {
            var me = Me(ctx);
            using (var c = db.Open()) if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
            store.Append(d =>
            {
                if (d.Scalar<int>("SELECT COUNT(*) FROM pools WHERE id=@id AND removed=0", new { id }) == 0) return Array.Empty<NewEvent>();
                d.Execute("UPDATE pool_secrets SET token_hash=NULL, code_hash=NULL WHERE pool_id=@id", new { id });
                return new[] { new NewEvent($"pool-{id}", "PoolRemoved", new { poolId = id }, me) };
            });
            if (pools.Get(id)?.Connection is { } conn) { await hub.Clients.Client(conn).SendAsync("Revoked"); pools.Remove(id, conn); }
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // Where agents run: the runners people connected (one per pinned agent) and the pools, with their slots and sandboxes
        authed.MapGet("/where", (HttpContext ctx, Db db, RunnerRegistry runners, PoolRegistry pools) =>
        {
            var me = Me(ctx);
            using var c = db.Open();
            if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
            var pinned = c.Query<MemberRow>(Q.MemberSelect + " WHERE m.kind='agent' AND m.removed=0 AND COALESCE(a.placement,'runner')='runner' ORDER BY m.handle").ToList();
            var runnerRows = pinned.Select(a => new
            {
                agentId = a.Id, handle = a.Handle, host = a.RunnerHost, online = runners.IsOnline(a.Id), connected = a.HasToken,
                owner = a.OwnerId is { } o ? Q.Member(c, o)?.DisplayName : null,
            }).ToList();
            var poolRows = c.Query<PoolRow>("SELECT * FROM pools WHERE removed=0 ORDER BY id").Select(p =>
            {
                var live = pools.Get(p.Id);
                var sandboxes = c.Query(@"SELECT s.id, s.thread_id, s.state, m.handle, ch.name AS channel FROM sandboxes s JOIN members m ON m.id=s.agent_id
                        LEFT JOIN threads t ON t.root_id=s.thread_id LEFT JOIN channels ch ON ch.id=t.channel_id
                        WHERE s.pool_id=@id AND s.state IN ('starting','resuming','running','suspending') ORDER BY s.id", new { id = p.Id })
                    .Select(s => new { id = (long)s.id, threadId = (long)s.thread_id, state = (string)s.state, agent = (string)s.handle, channel = (string?)s.channel }).ToList();
                var waiting = c.ExecuteScalar<int>("SELECT COUNT(*) FROM sandboxes WHERE state='waiting'");
                return new
                {
                    id = p.Id, name = p.Name, host = p.Host, slots = live?.Slots ?? p.Slots, memoryMb = p.MemoryMb, online = live is not null, enrolled = p.EnrolledAt is not null,
                    busy = sandboxes.Count, sandboxes, waiting, lastSeen = p.LastSeen,
                };
            }).ToList();
            return Results.Ok(new { runners = runnerRows, pools = poolRows });
        });

        // Server settings: the model provider the gateway forwards to, and how long a sandbox may sit idle
        authed.MapGet("/settings/server", (HttpContext ctx, Db db) =>
        {
            using var c = db.Open();
            if (!Access.IsAdmin(c, Me(ctx))) return Err(403, "Only admins can do that.");
            string? S(string k) => c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key=@k", new { k });
            return Results.Ok(new
            {
                models = new { kind = S("model_provider"), baseUrl = S("model_base_url") ?? "", hasKey = c.ExecuteScalar<int>("SELECT COUNT(*) FROM server_secrets WHERE key='model_api_key'") > 0,
                    copilotSignedIn = Gateway.CopilotGitHubToken() is not null },
                sandboxes = new { idleSeconds = int.TryParse(S("sandbox_idle_seconds"), out var s) ? s : Sandboxes.DefaultIdleSeconds,
                    retentionSeconds = int.TryParse(S("sandbox_retention_seconds"), out var r) ? r : Sandboxes.DefaultRetentionSeconds },
            });
        });

        authed.MapPut("/settings/models", (ModelsBody b, HttpContext ctx, Db db, IEventStore store) =>
        {
            var me = Me(ctx);
            using (var c = db.Open()) if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
            var kind = b.Kind ?? "";
            if (kind is not (Gateway.OpenAiCompatible or Gateway.Copilot)) return Err(400, "Pick an OpenAI-compatible provider or GitHub Copilot on this machine.");
            var baseUrl = (b.BaseUrl ?? "").Trim().TrimEnd('/');
            if (kind == Gateway.OpenAiCompatible && !Uri.TryCreate(baseUrl, UriKind.Absolute, out _)) return Err(400, "Add the provider's base URL, like https://openrouter.ai/api/v1.");
            if (kind == Gateway.Copilot && Gateway.CopilotGitHubToken() is null) return Err(400, "This machine isn't signed in to GitHub Copilot in OpenCode (opencode auth login).");
            store.Append(d =>
            {
                // the key goes to the server's secrets, never into an event
                if (kind == Gateway.OpenAiCompatible && !string.IsNullOrEmpty(b.ApiKey)) d.Execute("INSERT OR REPLACE INTO server_secrets VALUES ('model_api_key', @k)", new { k = b.ApiKey.Trim() });
                if (kind == Gateway.Copilot) d.Execute("DELETE FROM server_secrets WHERE key='model_api_key'");
                return new[] { new NewEvent("server", "ModelProviderSet", new { kind, baseUrl = kind == Gateway.Copilot ? "" : baseUrl }, me) };
            });
            return Results.Ok(new { ok = true });
        });

        authed.MapPut("/settings/sandboxes", (SandboxSettingsBody b, HttpContext ctx, Db db, IEventStore store) =>
        {
            var me = Me(ctx);
            using (var c = db.Open()) if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
            if (b.IdleSeconds is null && b.RetentionSeconds is null) return Err(400, "Say the idle time, the retention time, or both.");
            if (b.IdleSeconds is < 5 or > 7 * 24 * 3600) return Err(400, "The idle time is between 5 seconds and 7 days.");
            if (b.RetentionSeconds is < 5 or > 365 * 24 * 3600) return Err(400, "A done thread's volume is kept between 5 seconds and a year.");
            var evs = new List<NewEvent>();
            if (b.IdleSeconds is { } idle) evs.Add(new NewEvent("server", "SandboxIdleSet", new { seconds = idle }, me));
            if (b.RetentionSeconds is { } keep) evs.Add(new NewEvent("server", "SandboxRetentionSet", new { seconds = keep }, me));
            store.Append(evs);
            return Results.Ok(new { ok = true });
        });
    }

    static object PoolCode(HttpContext ctx, long id, string name, string code) =>
        new { poolId = id, name, code, expiresInMinutes = 10, command = $"cowork-runner pool connect {ctx.Request.Scheme}://{ctx.Request.Host} --code {code} --slots 2 --memory 1500M" };
}

public class PoolRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Host { get; set; }
    public int Slots { get; set; }
    public long? MemoryMb { get; set; }
    public long? EnrolledAt { get; set; }
    public long? LastSeen { get; set; }
}
