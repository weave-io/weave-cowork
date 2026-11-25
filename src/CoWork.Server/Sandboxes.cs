using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR;

namespace CoWork.Server;

/// <summary>
/// The inline projection of pools (`pool-{id}`), sandboxes (the Sandbox* events in a thread's stream), the model gateway's usage and the
/// server's provider and idle settings. A sandbox's starting card in the thread is a post of kind `sandbox` whose meta holds its rows; every
/// later event of that start rewrites them (docs/architecture/provisioning.html, step 4 of mockups/cowork/projects.html).
/// </summary>
public sealed class SandboxProjection : IInlineProjection
{
    public void Apply(ProjectionContext x, StoredEvent e)
    {
        Locked(x, e);
        switch (e.Type)
        {
            // ---- the server's settings for sandboxes and models ----
            case "ModelProviderSet":
                x.Execute("INSERT OR REPLACE INTO settings VALUES ('model_provider', @k)", new { k = e.Str("kind") });
                x.Execute("INSERT OR REPLACE INTO settings VALUES ('model_base_url', @u)", new { u = e.Str("baseUrl") ?? "" });
                break;
            case "ServerIdAssigned":
                x.Execute("INSERT OR REPLACE INTO settings VALUES ('server_id', @s)", new { s = e.Str("serverId") });
                break;
            case "SandboxIdleSet":
                x.Execute("INSERT OR REPLACE INTO settings VALUES ('sandbox_idle_seconds', @s)", new { s = e.Long("seconds").ToString() });
                break;

            // ---- pools ----
            case "PoolCreated":
                x.Execute("INSERT INTO pools(id, name, created_by, created_at, code_expires) VALUES (@id,@name,@by,@at,@exp)",
                    new { id = e.Long("poolId"), name = e.Str("name"), by = e.ActorId, at = e.OccurredAt, exp = e.LongOrNull("expiresAt") });
                break;
            case "PoolCodeIssued":
                x.Execute("UPDATE pools SET code_expires=@exp WHERE id=@id", new { exp = e.Long("expiresAt"), id = e.Long("poolId") });
                break;
            case "PoolEnrolled":
                x.Execute("UPDATE pools SET host=@host, slots=@slots, memory_mb=@mem, enrolled_at=@at, code_expires=NULL WHERE id=@id",
                    new { host = e.Str("host"), slots = e.Long("slots"), mem = e.LongOrNull("memoryMb"), at = e.OccurredAt, id = e.Long("poolId") });
                break;
            case "PoolConnected":
                x.Execute("UPDATE pools SET slots=@slots, memory_mb=COALESCE(@mem, memory_mb), last_seen=@at WHERE id=@id",
                    new { slots = e.Long("slots"), mem = e.LongOrNull("memoryMb"), at = e.OccurredAt, id = e.Long("poolId") });
                break;
            case "PoolDisconnected":
                x.Execute("UPDATE pools SET last_seen=@at WHERE id=@id", new { at = e.OccurredAt, id = e.Long("poolId") });
                break;
            case "PoolRemoved":
                x.Execute("UPDATE pools SET removed=1, code_expires=NULL WHERE id=@id", new { id = e.Long("poolId") });
                break;

            // ---- sandboxes ----
            case "SandboxRequested":
            {
                var id = e.Long("sandboxId");
                var resume = e.Bool("resume");
                x.Execute(@"INSERT INTO sandboxes(id, thread_id, agent_id, project_id, state, image, memory_mb, branch, requested_by, requested_at, post_id)
                            VALUES (@id,@t,@a,@p,'waiting',@image,@mem,@branch,@by,@at,@post)
                            ON CONFLICT(id) DO UPDATE SET state='waiting', pool_id=NULL, image=@image, memory_mb=@mem, requested_by=@by, requested_at=@at, post_id=@post, reason=NULL,
                              starts=CASE WHEN @resume THEN starts ELSE 0 END",            // not a resume (its volume was deleted): it starts afresh
                    new { id, t = ThreadProjection.ThreadOf(e), a = e.Long("agentId"), p = e.LongOrNull("projectId"), image = e.Str("image"), mem = e.LongOrNull("memoryMb"),
                          branch = e.Str("branch"), by = e.OnBehalfOf, at = e.OccurredAt, post = e.Long("postId"), resume });
                var harness = e.Str("harness") ?? "the harness";
                var ws = e.Str("workspace");
                var line = x.Scalar<int>("SELECT COUNT(*) FROM sandboxes WHERE state='waiting'");          // this one included: its place in the queue
                var meta = new JsonObject
                {
                    ["sandboxId"] = id, ["agentId"] = e.Long("agentId"), ["resume"] = resume, ["state"] = "waiting", ["requestedAt"] = e.OccurredAt, ["line"] = line,
                    ["waiting"] = e.Str("waiting") ?? "Waiting for a pool",
                    ["steps"] = new JsonArray(
                        Step("pool", "waiting", WaitingText(e.Str("waiting"), line)),
                        Step("container", "todo", resume ? "Resume the sandbox on its volume" : $"Start a sandbox from `{e.Str("image")}`"),
                        Step("workspace", "todo", resume ? $"Pick up its volume on `{e.Str("branch")}`" : ws is null ? NoRepository(x, e) ?? "Make an empty workspace" : $"Copy `{ws}` onto `{e.Str("branch")}`"),
                        Step("harness", "todo", $"Start `{harness}`")),
                };
                x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, meta, created_at, position) VALUES (@id,@ch,@t,@a,@body,'sandbox',@meta,@at,@pos)",
                    new { id = e.Long("postId"), ch = e.Long("channelId"), t = ThreadProjection.ThreadOf(e), a = e.Long("agentId"), body = resume ? "Resuming the sandbox" : "Starting a sandbox",
                          meta = meta.ToJsonString(), at = e.OccurredAt, pos = e.Position });
                break;
            }
            case "SandboxPlaced":
            {
                var id = e.Long("sandboxId");
                var resume = x.Scalar<int>("SELECT starts FROM sandboxes WHERE id=@id", new { id }) > 0;
                x.Execute("UPDATE sandboxes SET state=@s, pool_id=@pool, memory_mb=@mem WHERE id=@id", new { s = resume ? "resuming" : "starting", pool = e.Long("poolId"), mem = e.LongOrNull("memoryMb"), id });
                var free = e.Long("slots") - e.Long("busy");
                Card(x, e, m =>
                {
                    m["state"] = resume ? "resuming" : "starting"; m["pool"] = e.Str("poolName"); m["line"] = 0;
                    SetStep(m, "pool", "done", $"{e.Str("poolName")} has room ({free} of {e.Long("slots")} sandboxes free)", Secs(m, e));
                    SetStep(m, "container", "running", null, null);
                });
                // everyone still waiting moves up one place
                foreach (var w in x.Query<(long Id, long PostId)>("SELECT id, post_id FROM sandboxes WHERE state='waiting'").ToList())
                    Meta(x, w.PostId, e.Position, m =>
                    {
                        var line = Math.Max(1, ((int?)m["line"] ?? 1) - 1);
                        m["line"] = line;
                        SetStep(m, "pool", "waiting", WaitingText((string?)m["waiting"], line), null);
                    });
                break;
            }
            case "SandboxStarted":
            case "SandboxResumed":
            {
                var id = e.Long("sandboxId");
                x.Execute("UPDATE sandboxes SET state='running', started_at=@at, last_active=@at, starts=starts+1 WHERE id=@id", new { at = e.OccurredAt, id });
                var resumed = e.Type == "SandboxResumed";
                var ws = e.Str("workspace");
                Card(x, e, m =>
                {
                    m["state"] = "running";
                    var wsMs = e.LongOrNull("workspaceMs"); var ctMs = e.LongOrNull("containerMs");
                    SetStep(m, "workspace", "done", resumed ? $"Its volume still has `{e.Str("branch")}` and the session"
                        : ws is null ? (NoRepository(x, e) is { } none ? $"Made an empty workspace. {none}" : "Made an empty workspace on its volume") : $"Made a copy of `{ws}` on `{e.Str("branch")}`", wsMs is { } w ? w / 1000.0 : null);
                    SetStep(m, "container", "done", resumed ? $"Resumed the sandbox from `{e.Str("image")}` on its volume, {e.LongOrNull("memoryMb")} MB"
                        : $"Started a sandbox from `{e.Str("image")}`, {e.LongOrNull("memoryMb")} MB", ctMs is { } c ? c / 1000.0 : null);
                    SetStep(m, "harness", "running", null, null);
                });
                break;
            }
            case "SandboxConnected":
                x.Execute("UPDATE sandboxes SET last_active=@at WHERE id=@id", new { at = e.OccurredAt, id = e.Long("sandboxId") });
                Card(x, e, m =>
                {
                    var harness = ((string?)Find(m, "harness")?["text"] ?? "").Replace("Start `", "").TrimEnd('`');
                    SetStep(m, "harness", "done", $"Connected; starting `{harness}`", Secs(m, e));
                    m["connected"] = true;
                });
                break;
            case "SandboxSuspending":
                x.Execute("UPDATE sandboxes SET state='suspending', reason=@r WHERE id=@id", new { r = e.Str("reason"), id = e.Long("sandboxId") });
                Card(x, e, m => { m["state"] = "suspending"; m["idleSeconds"] = e.LongOrNull("idleSeconds"); m["why"] = e.Str("reason"); m["by"] = e.LongOrNull("by"); });
                break;
            case "SandboxSuspended":
                x.Execute("UPDATE sandboxes SET state='suspended' WHERE id=@id", new { id = e.Long("sandboxId") });
                Card(x, e, m => { m["state"] = "suspended"; m["suspendedAt"] = e.OccurredAt; });
                break;
            case "SandboxFailed":
                x.Execute("UPDATE sandboxes SET state='failed', reason=@r WHERE id=@id", new { r = e.Str("reason"), id = e.Long("sandboxId") });
                Card(x, e, m =>
                {
                    m["state"] = "failed"; m["reason"] = e.Str("reason");
                    foreach (var s in (JsonArray)m["steps"]!) if ((string?)s!["state"] is "running" or "waiting") s["state"] = "failed";
                });
                break;
            case "SandboxDeleted":
                x.Execute("UPDATE sandboxes SET state='deleted' WHERE id=@id", new { id = e.Long("sandboxId") });
                Card(x, e, m => m["state"] = "deleted");
                Branch(x, e, "deleted", null);
                break;

            // a turn in a sandbox's thread keeps it busy: the idle clock starts again when a turn ends
            case "TurnStarted": case "TurnEnded": case "TurnFailed": case "TurnInterrupted": case "ModelUsageRecorded":
                if (e.StreamId.StartsWith("thread-") && e.Data.TryGetProperty("agentId", out _))
                    x.Execute("UPDATE sandboxes SET last_active=@at WHERE thread_id=@t AND agent_id=@a AND state='running'", new { at = e.OccurredAt, t = ThreadProjection.ThreadOf(e), a = e.Long("agentId") });
                if (e.Type == "ModelUsageRecorded" && e.LongOrNull("projectId") is { } usedIn)
                    x.Execute(@"INSERT INTO project_usage(project_id, month, agent_id, calls, tokens, cost) VALUES (@p,@month,@a,1,@tokens,@cost)
                                ON CONFLICT(project_id, month, agent_id) DO UPDATE SET calls=calls+1, tokens=tokens+@tokens, cost=CASE WHEN @cost IS NULL THEN cost ELSE COALESCE(cost,0)+@cost END",
                        new { p = usedIn, month = Budgets.Month(e.OccurredAt), a = e.Long("agentId"), tokens = (e.LongOrNull("promptTokens") ?? 0) + (e.LongOrNull("completionTokens") ?? 0),
                              cost = e.Data.TryGetProperty("cost", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : (double?)null });
                if (e.Type == "ModelUsageRecorded")
                    x.Execute(@"INSERT INTO model_usage(thread_id, agent_id, turn, project_id, model, calls, prompt_tokens, completion_tokens, cost) VALUES (@t,@a,@turn,@p,@model,1,@pt,@ct,@cost)
                                ON CONFLICT(thread_id, agent_id, turn) DO UPDATE SET calls=calls+1, prompt_tokens=prompt_tokens+@pt, completion_tokens=completion_tokens+@ct,
                                  cost=CASE WHEN @cost IS NULL THEN cost ELSE COALESCE(cost,0)+@cost END, model=@model",
                        new { t = ThreadProjection.ThreadOf(e), a = e.Long("agentId"), turn = e.Correlation ?? "", p = e.LongOrNull("projectId"), model = e.Str("model"),
                              pt = e.LongOrNull("promptTokens") ?? 0, ct = e.LongOrNull("completionTokens") ?? 0, cost = e.Data.TryGetProperty("cost", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetDouble() : (double?)null });
                break;
        }
    }

    /// <summary>Round 2: the network list, asks to reach a host, the setup command, budgets and pausing.</summary>
    public static void Locked(ProjectionContext x, StoredEvent e)
    {
        switch (e.Type)
        {
            case "ProjectNetworkChanged":
            {
                var p = e.Long("projectId");
                x.Execute("DELETE FROM project_network WHERE project_id=@p", new { p });
                foreach (var h in e.Data.GetProperty("hosts").EnumerateArray()) x.Execute("INSERT OR IGNORE INTO project_network VALUES (@p,@h)", new { p, h = h.GetString() });
                break;
            }
            case "SetupCommandChanged":
                x.Execute("UPDATE projects SET setup_command=@c WHERE id=@p", new { c = e.Str("command"), p = e.Long("projectId") });
                break;
            case "ProjectAgentsPaused":
                x.Execute("UPDATE projects SET paused_by=@by, paused_at=@at WHERE id=@p", new { by = e.ActorId, at = e.OccurredAt, p = e.Long("projectId") });
                break;
            case "ProjectAgentsResumed":
                x.Execute("UPDATE projects SET paused_by=NULL, paused_at=NULL WHERE id=@p", new { p = e.Long("projectId") });
                break;
            case "BudgetSet":
                x.Execute("INSERT OR REPLACE INTO budgets VALUES (@p,@mt,@mu,@tt,@tu)", new { p = e.Long("projectId"), mt = e.LongOrNull("monthTokens"), mu = Num(e, "monthUsd"), tt = e.LongOrNull("threadTokens"), tu = Num(e, "threadUsd") });
                break;
            case "ThreadBudgetRaised":
                x.Execute(@"INSERT INTO thread_budgets VALUES (@t,@tokens,@usd) ON CONFLICT(thread_id) DO UPDATE SET extra_tokens=extra_tokens+@tokens, extra_usd=extra_usd+@usd",
                    new { t = ThreadProjection.ThreadOf(e), tokens = e.LongOrNull("tokens") ?? 0, usd = Num(e, "usd") ?? 0 });
                Notice(x, e, e.ActorId, e.Str("text"), null);
                break;
            case "BudgetReached":
                Notice(x, e, e.LongOrNull("agentId"), e.Str("text"), new JsonObject { ["budget"] = true, ["scope"] = e.Str("scope"), ["leadIds"] = JsonNode.Parse(e.Data.GetProperty("leadIds").GetRawText()) });
                break;
            case "EgressRefused":
            {
                var meta = new JsonObject
                {
                    ["egress"] = true, ["host"] = e.Str("host"), ["port"] = e.LongOrNull("port"), ["requestId"] = e.Str("host"), ["agentId"] = e.Long("agentId"), ["sandboxId"] = e.Long("sandboxId"),
                    ["ownerId"] = e.Long("ownerId"), ["why"] = e.Str("why"), ["status"] = "pending",
                    ["options"] = new JsonArray(
                        new JsonObject { ["id"] = "once", ["name"] = "Allow once", ["kind"] = "allow_once" },
                        new JsonObject { ["id"] = "thread", ["name"] = "Allow for this thread", ["kind"] = "allow_always" },
                        new JsonObject { ["id"] = "deny", ["name"] = "Deny", ["kind"] = "reject_once" }),
                };
                if (e.Data.TryGetProperty("leadIds", out var leads) && leads.GetArrayLength() > 0) { meta["leadIds"] = JsonNode.Parse(leads.GetRawText()); meta["leadAfter"] = e.LongOrNull("leadAfter"); }
                x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, meta, created_at, position) VALUES (@id,@ch,@t,@a,@body,'ask',@meta,@at,@pos)",
                    new { id = e.Long("postId"), ch = e.Long("channelId"), t = ThreadProjection.ThreadOf(e), a = e.Long("agentId"), body = $"reach {e.Str("host")}", meta = meta.ToJsonString(), at = e.OccurredAt, pos = e.Position });
                break;
            }
            case "EgressAllowed":
                if (e.Str("scope") == "thread")
                    x.Execute("INSERT OR IGNORE INTO thread_egress VALUES (@t,@a,@h)", new { t = ThreadProjection.ThreadOf(e), a = e.Long("agentId"), h = e.Str("host") });
                Meta(x, e.Long("postId"), e.Position, m => { m["status"] = "answered"; m["answer"] = e.Str("scope"); }, kind: "ask");
                break;
            // ---- round 3: threads as work ----
            case "ThreadResolved":
            {
                var t = ThreadProjection.ThreadOf(e);
                x.Execute("UPDATE threads SET resolved_post=@post, resolved_at=@at, resolved_by=@a WHERE root_id=@t", new { post = e.Long("postId"), at = e.OccurredAt, a = e.Long("agentId"), t });
                // the outcome names the thread, unless someone already has
                x.Execute("UPDATE threads SET title=@title WHERE root_id=@t AND titled_at IS NULL", new { title = e.Str("title"), t });
                var meta = JsonNode.Parse(e.Data.GetRawText())!.AsObject();
                meta["state"] = "done"; meta["resolvedAt"] = e.OccurredAt;
                x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, meta, created_at, position) VALUES (@id,@ch,@t,@a,@body,'outcome',@meta,@at,@pos)",
                    new { id = e.Long("postId"), ch = e.Long("channelId"), t, a = e.Long("agentId"), body = e.Str("title"), meta = meta.ToJsonString(), at = e.OccurredAt, pos = e.Position });
                break;
            }
            case "ThreadReopened":
            {
                var t = ThreadProjection.ThreadOf(e);
                if (x.Scalar<long?>("SELECT resolved_post FROM threads WHERE root_id=@t", new { t }) is { } post)
                    Meta(x, post, e.Position, m => { m["state"] = "reopened"; m["reopenedBy"] = e.ActorId; }, kind: "outcome");
                x.Execute("UPDATE threads SET resolved_post=NULL, resolved_at=NULL, resolved_by=NULL WHERE root_id=@t", new { t });
                break;
            }
            case "MessageDelivered" when !e.Bool("system"):
                // new work for a done thread opens it again (its outcome stays, as what was done then)
                if (x.Scalar<long?>("SELECT resolved_post FROM threads WHERE root_id=@t", new { t = ThreadProjection.ThreadOf(e) }) is { } open)
                {
                    Meta(x, open, e.Position, m => { m["state"] = "reopened"; m["reopenedBy"] = e.OnBehalfOf; }, kind: "outcome");
                    x.Execute("UPDATE threads SET resolved_post=NULL, resolved_at=NULL, resolved_by=NULL WHERE root_id=@t", new { t = ThreadProjection.ThreadOf(e) });
                }
                break;
            case "SandboxRetentionSet":
                x.Execute("INSERT OR REPLACE INTO settings VALUES ('sandbox_retention_seconds', @s)", new { s = e.Long("seconds").ToString() });
                break;
            case "SandboxDeleteRequested":
                x.Execute("UPDATE sandboxes SET state='deleting' WHERE id=@id", new { id = e.Long("sandboxId") });
                break;
            case "SandboxKept":
                x.Execute("UPDATE sandboxes SET state='kept', reason=@r WHERE id=@id", new { r = e.Str("reason"), id = e.Long("sandboxId") });
                Branch(x, e, "kept", e.Str("reason"));
                break;
            case "EgressDenied":
                Meta(x, e.Long("postId"), e.Position, m => { m["status"] = "denied"; m["answer"] = null; }, kind: "ask");
                break;
        }
    }

    /// <summary>The done thread's outcome says what became of its branch: deleted with the volume (no changes), or kept (it has changes).</summary>
    static void Branch(ProjectionContext x, StoredEvent e, string state, string? reason)
    {
        var t = ThreadProjection.ThreadOf(e);
        var post = x.Scalar<long?>("SELECT id FROM posts WHERE thread_id=@t AND kind='outcome' ORDER BY id DESC LIMIT 1", new { t });
        if (post is null) return;
        Meta(x, post.Value, e.Position, m => { if (m["branch"] is JsonObject b) { b["state"] = state; b["reason"] = reason; } }, kind: "outcome");
    }

    /// <summary>A sandbox for a project with no repository has nothing to work on: its starting card says so, and where to add one.</summary>
    static string? NoRepository(ProjectionContext x, StoredEvent e)
    {
        var sandbox = x.Query<(long? Project, long Agent)>("SELECT project_id, agent_id FROM sandboxes WHERE id=@id", new { id = e.Long("sandboxId") }).FirstOrDefault();
        if (sandbox.Project is null) return null;
        var handle = x.Scalar<string>("SELECT handle FROM members WHERE id=@a", new { a = sandbox.Agent }) ?? "the agent";
        return $"No repository: add one in the project's settings to give {handle} something to work on.";
    }

    static double? Num(StoredEvent e, string k) => e.Data.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static void Notice(ProjectionContext x, StoredEvent e, long? author, string? text, JsonObject? meta)
    {
        if (e.LongOrNull("postId") is not { } id) return;
        x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, meta, created_at, position) VALUES (@id,@ch,@t,@a,@body,'notice',@meta,@at,@pos)",
            new { id, ch = e.Long("channelId"), t = ThreadProjection.ThreadOf(e), a = author, body = text ?? "", meta = meta?.ToJsonString(), at = e.OccurredAt, pos = e.Position });
    }

    static string WaitingText(string? waiting, int line) => $"{waiting ?? "Waiting for a pool"}, {Ordinal(line)} in line";
    public static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    static JsonObject Step(string key, string state, string text) => new() { ["key"] = key, ["state"] = state, ["text"] = text };
    static JsonNode? Find(JsonObject m, string key) => ((JsonArray)m["steps"]!).FirstOrDefault(s => (string?)s!["key"] == key);
    static void SetStep(JsonObject m, string key, string state, string? text, double? secs)
    {
        if (Find(m, key) is not JsonObject s) return;
        s["state"] = state;
        if (text is not null) s["text"] = text;
        if (secs is not null) s["secs"] = Math.Round(secs.Value, 1);
    }
    static double Secs(JsonObject m, StoredEvent e) => (e.OccurredAt - ((long?)m["requestedAt"] ?? e.OccurredAt)) / 1000.0;

    /// <summary>Rewrites the sandbox's current starting card (its post id is in the event).</summary>
    static void Card(ProjectionContext x, StoredEvent e, Action<JsonObject> change)
    {
        if (e.LongOrNull("postId") is { } post) Meta(x, post, e.Position, change);
    }
    static void Meta(ProjectionContext x, long postId, long position, Action<JsonObject> change, string kind = "sandbox")
    {
        var old = x.Scalar<string>("SELECT meta FROM posts WHERE id=@postId AND kind=@kind", new { postId, kind });
        if (old is null) return;
        var node = JsonNode.Parse(old)!.AsObject();
        change(node);
        x.Execute("UPDATE posts SET meta=@m, position=@position WHERE id=@postId", new { m = node.ToJsonString(), position, postId });
    }
}

/// <summary>Which pools are connected, on which connection, and how much room each said it has.</summary>
public sealed class PoolRegistry
{
    public sealed record Entry(string Connection, int Slots, long MemoryMb);
    private readonly ConcurrentDictionary<long, Entry> _pools = new();
    public Entry? Get(long poolId) => _pools.TryGetValue(poolId, out var e) ? e : null;
    public bool IsOnline(long poolId) => _pools.ContainsKey(poolId);
    public IReadOnlyCollection<long> Online => _pools.Keys.ToList();
    public void Set(long poolId, Entry e) => _pools[poolId] = e;
    public bool Remove(long poolId, string conn) => _pools.TryGetValue(poolId, out var e) && e.Connection == conn && _pools.TryRemove(poolId, out _);
}

/// <summary>The runners inside sandboxes that are connected: one per (agent, thread), each with the sandbox its token is bound to.</summary>
public sealed class SandboxRegistry
{
    public sealed record Entry(string Connection, long SandboxId, HubCallerContext Context);
    private readonly ConcurrentDictionary<(long Agent, long Thread), Entry> _conns = new();
    public Entry? Get(long agentId, long threadId) => _conns.TryGetValue((agentId, threadId), out var e) ? e : null;
    public string? ConnectionOf(long agentId, long threadId) => Get(agentId, threadId)?.Connection;
    public void Set(long agentId, long threadId, Entry e) => _conns[(agentId, threadId)] = e;
    public bool Remove(long agentId, long threadId, string conn) =>
        _conns.TryGetValue((agentId, threadId), out var e) && e.Connection == conn && _conns.TryRemove((agentId, threadId), out _);
    /// <summary>Drops the sandbox's connection (its token was revoked): the runner inside is told, and the connection is closed.</summary>
    public async Task CutOff(long sandboxId, IHubContext<RunnerHub> hub)
    {
        foreach (var (k, e) in _conns.Where(kv => kv.Value.SandboxId == sandboxId).ToList())
        {
            _conns.TryRemove(k, out _);
            try { await hub.Clients.Client(e.Connection).SendAsync("Revoked"); } catch { }
            e.Context.Abort();
        }
    }
}

/// <summary>A sandbox as the scheduler reads it.</summary>
public sealed class SandboxRow
{
    public long Id { get; set; }
    public long ThreadId { get; set; }
    public long AgentId { get; set; }
    public long? ProjectId { get; set; }
    public long? PoolId { get; set; }
    public string State { get; set; } = "";
    public string? Image { get; set; }
    public long? MemoryMb { get; set; }
    public string? Branch { get; set; }
    public long? RequestedBy { get; set; }
    public long? PostId { get; set; }
    public int Starts { get; set; }
    public long? LastActive { get; set; }
}

/// <summary>
/// The scheduler (docs/architecture/provisioning.html, "One request, step by step"). A message for a template with no running sandbox in its
/// thread asks for one: <see cref="Ensure"/> records `SandboxRequested` (it waits in line), then <see cref="PlaceWaiting"/> gives the oldest
/// waiting sandbox a slot on the online pool with the most room (`SandboxPlaced`), mints its thread token and sends the pool
/// <see cref="CreateSandbox"/> or <see cref="ResumeSandbox"/>. The pool reports back (<see cref="OnReport"/>). The delivery stays queued until the
/// runner inside connects with the token. An idle sandbox is suspended (<see cref="Suspend"/>): its token is revoked and the pool stops the
/// container but keeps the volume.
/// </summary>
public sealed class Sandboxes
{
    public const string DefaultImage = "cowork-sandbox-opencode:2.4.1";
    public const int DefaultIdleSeconds = 15 * 60;
    public const int DefaultRetentionSeconds = 7 * 24 * 3600;
    private readonly Db _db;
    private readonly IEventStore _store;
    private readonly PoolRegistry _pools;
    private readonly SandboxRegistry _conns;
    private readonly IHubContext<PoolHub> _poolHub;
    private readonly IHubContext<RunnerHub> _runnerHub;
    private readonly Realtime _rt;
    private readonly ILogger<Sandboxes> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Sandboxes(Db db, IEventStore store, PoolRegistry pools, SandboxRegistry conns, IHubContext<PoolHub> poolHub, IHubContext<RunnerHub> runnerHub, Realtime rt, ILogger<Sandboxes> log)
    { _db = db; _store = store; _pools = pools; _conns = conns; _poolHub = poolHub; _runnerHub = runnerHub; _rt = rt; _log = log; }

    public static string Name(long sandboxId) => $"sb-{sandboxId}";
    public static long? Parse(string sandboxId) => sandboxId.StartsWith("sb-") && long.TryParse(sandboxId[3..], out var id) ? id : null;

    public int IdleSeconds()
    {
        using var c = _db.Open();
        return int.TryParse(c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key='sandbox_idle_seconds'"), out var s) && s > 0 ? s : DefaultIdleSeconds;
    }

    /// <summary>How long a done thread's stopped sandbox keeps its volume (Server settings; 7 days unless changed).</summary>
    public int RetentionSeconds()
    {
        using var c = _db.Open();
        return int.TryParse(c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key='sandbox_retention_seconds'"), out var s) && s > 0 ? s : DefaultRetentionSeconds;
    }

    /// <summary>Done threads whose sandbox has been stopped for the retention time: their volumes go (the branch with them, unless it has
    /// changes, which keeps the volume). Asked of the pool once; it reports deleted or kept.</summary>
    public async Task Retire(int retentionSeconds)
    {
        List<SandboxRow> due;
        using (var c = _db.Open())
            due = c.Query<SandboxRow>(@"SELECT s.* FROM sandboxes s JOIN threads t ON t.root_id=s.thread_id
                WHERE s.state='suspended' AND t.resolved_at IS NOT NULL AND t.resolved_at < @cutoff", new { cutoff = Db.Now() - retentionSeconds * 1000L }).ToList();
        foreach (var s in due)
        {
            if (s.PoolId is not { } poolId || _pools.Get(poolId)?.Connection is not { } conn) continue;        // its pool is away: next time
            string? baseBranch;
            using (var c = _db.Open()) baseBranch = c.ExecuteScalar<string?>("SELECT w.branch FROM workspaces w WHERE w.project_id=@p AND w.kind='repository' ORDER BY w.id LIMIT 1", new { p = s.ProjectId });
            var asked = false;
            await _lock.WaitAsync();
            try
            {
                _store.Append(ctx =>
                {
                    asked = false;
                    if (ctx.Scalar<string?>("SELECT state FROM sandboxes WHERE id=@id", new { id = s.Id }) != "suspended") return Array.Empty<NewEvent>();
                    asked = true;
                    return new[] { new NewEvent($"thread-{s.ThreadId}", "SandboxDeleteRequested", new { sandboxId = s.Id, agentId = s.AgentId, poolId, retentionSeconds }, s.AgentId, s.RequestedBy) };
                });
            }
            finally { _lock.Release(); }
            if (asked)
                try { await _poolHub.Clients.Client(conn).SendAsync("DeleteSandbox", new DeleteSandbox(Name(s.Id), true, s.Image, baseBranch ?? "main")); }
                catch (Exception e) { _log.LogWarning(e, "couldn't ask pool {Pool} to delete {Id}", poolId, s.Id); }
        }
    }

    /// <summary>The thread needs the template's sandbox: ask for one (or for the suspended one back) unless it is already on its way.</summary>
    public async Task Ensure(long threadId, long agentId, long? behalf)
    {
        await _lock.WaitAsync();
        try
        {
            _store.Append(ctx =>
            {
                var row = ctx.Query<SandboxRow>("SELECT * FROM sandboxes WHERE thread_id=@threadId AND agent_id=@agentId", new { threadId, agentId }).FirstOrDefault();
                if (row is not null && row.State is "waiting" or "starting" or "resuming" or "running" or "suspending" or "deleting") return Array.Empty<NewEvent>();
                if (ctx.Scalar<int>(@"SELECT COUNT(*) FROM threads t JOIN channels c ON c.id=t.channel_id JOIN projects p ON p.id=c.project_id
                    WHERE t.root_id=@threadId AND p.paused_at IS NOT NULL", new { threadId }) > 0) return Array.Empty<NewEvent>();        // its project's agents are paused: the message waits
                var agent = ctx.Query<MemberRow>(Q.MemberSelect + " WHERE m.id=@agentId", new { agentId }).FirstOrDefault();
                if (agent is null || agent.Removed || !agent.IsTemplate) return Array.Empty<NewEvent>();
                var channelId = ctx.Scalar<long>("SELECT channel_id FROM threads WHERE root_id=@threadId", new { threadId });
                var (project, where) = Dispatcher.WorkspaceOf(ctx, threadId, agent, forPool: true);
                var id = row?.Id ?? ctx.NextId("sandboxes");
                var cmd = JsonSerializer.Deserialize<string[]>(agent.HarnessCommand ?? "[]") ?? Array.Empty<string>();
                return new[]
                {
                    new NewEvent($"thread-{threadId}", "SandboxRequested", new
                    {
                        sandboxId = id, agentId, projectId = project, channelId, postId = ctx.NextId("posts"), resume = row is not null && row.Starts > 0 && row.State != "deleted",       // a deleted volume starts afresh
                        image = agent.Image ?? DefaultImage, memoryMb = (long?)null, branch = where?.Branch ?? Projects.Branch(Projects.Slug("", threadId)),
                        harness = string.Join(' ', cmd.Select(a => a.Split('/').Last())), workspace = where is { Kind: "repository" } ? where.Name : null,
                        waiting = WaitingFor(ctx),
                    }, agentId, behalf),
                };
            });
        }
        finally { _lock.Release(); }
        await PlaceWaiting();
    }

    /// <summary>"Waiting for room on pool-01 (2 of 2 busy)", or why there is no room at all.</summary>
    private string WaitingFor(IDecisionContext ctx)
    {
        var online = Candidates(ctx);
        if (online.Count == 0) return ctx.Scalar<int>("SELECT COUNT(*) FROM pools WHERE removed=0 AND enrolled_at IS NOT NULL") == 0
            ? "No pool is connected to this server yet" : "No pool is online";
        if (online.Count == 1) return $"Waiting for room on {online[0].Name} ({online[0].Busy} of {online[0].Slots} busy)";
        return $"Waiting for room ({online.Sum(p => p.Busy)} of {online.Sum(p => p.Slots)} busy on {online.Count} pools)";
    }

    private sealed record Candidate(long Id, string Name, int Slots, int Busy, long MemoryMb);
    private List<Candidate> Candidates(IDecisionContext ctx)
    {
        var list = new List<Candidate>();
        foreach (var id in _pools.Online)
        {
            var p = ctx.Query<(string Name, long? MemoryMb, bool Removed)>("SELECT name, memory_mb, removed FROM pools WHERE id=@id", new { id }).FirstOrDefault();
            if (p.Name is null || p.Removed) continue;
            var entry = _pools.Get(id); if (entry is null) continue;
            var busy = ctx.Scalar<int>("SELECT COUNT(*) FROM sandboxes WHERE pool_id=@id AND state IN ('starting','resuming','running','suspending')", new { id });
            list.Add(new Candidate(id, p.Name, entry.Slots, busy, entry.MemoryMb > 0 ? entry.MemoryMb : p.MemoryMb ?? 1500));
        }
        return list.OrderByDescending(c => c.Slots - c.Busy).ThenBy(c => c.Id).ToList();
    }

    /// <summary>Gives waiting sandboxes a slot, oldest first, while a pool has room; each one placed gets a fresh token and its command.</summary>
    public async Task PlaceWaiting()
    {
        for (var guard = 0; guard < 100; guard++)
        {
            (SandboxRow Row, long PoolId, string Token, bool Resume, long MemoryMb)? placed = null;
            await _lock.WaitAsync();
            try
            {
                _store.Append(ctx =>
                {
                    placed = null;
                    var w = ctx.Query<SandboxRow>(@"SELECT s.* FROM sandboxes s LEFT JOIN projects p ON p.id=s.project_id WHERE s.state='waiting' AND p.paused_at IS NULL
                        ORDER BY s.requested_at, s.id LIMIT 1").FirstOrDefault();
                    if (w is null) return Array.Empty<NewEvent>();
                    var pool = Candidates(ctx).FirstOrDefault(c => c.Busy < c.Slots);
                    if (pool is null) return Array.Empty<NewEvent>();
                    var token = Tokens.NewToken();
                    // a token for this start only: earlier ones for the sandbox stop working
                    ctx.Execute("UPDATE thread_tokens SET revoked_at=@now WHERE sandbox_id=@id AND revoked_at IS NULL", new { now = Db.Now(), id = w.Id });
                    ctx.Execute("INSERT INTO thread_tokens(token_hash, sandbox_id, agent_id, project_id, thread_id, minted_at) VALUES (@h,@id,@a,@p,@t,@now)",
                        new { h = Tokens.Hash(token), id = w.Id, a = w.AgentId, p = w.ProjectId, t = w.ThreadId, now = Db.Now() });
                    placed = (w, pool.Id, token, w.Starts > 0, pool.MemoryMb);
                    return new[]
                    {
                        new NewEvent($"thread-{w.ThreadId}", "SandboxPlaced", new { sandboxId = w.Id, agentId = w.AgentId, postId = w.PostId, poolId = pool.Id, poolName = pool.Name, busy = pool.Busy, slots = pool.Slots, memoryMb = pool.MemoryMb },
                            w.AgentId, w.RequestedBy),
                    };
                });
            }
            finally { _lock.Release(); }
            if (placed is not { } p) return;
            await Send(p.PoolId, p.Row, p.Token, p.Resume, p.MemoryMb);
        }
    }

    private async Task Send(long poolId, SandboxRow s, string token, bool resume, long memoryMb)
    {
        SandboxSpec spec;
        using (var c = _db.Open())
        {
            var agent = Q.Member(c, s.AgentId)!;
            var ctx = new ReadContext(c);
            var (project, where) = Dispatcher.WorkspaceOf(ctx, s.ThreadId, agent, forPool: true);
            var cmd = JsonSerializer.Deserialize<string[]>(agent.HarnessCommand ?? "[]") ?? Array.Empty<string>();
            spec = new SandboxSpec(Name(s.Id), s.ThreadId, s.AgentId, agent.Handle, project,
                new SandboxTemplate(agent.Image ?? DefaultImage, cmd, agent.Model),
                where is { Kind: "repository" } ? new SandboxWorkspace("repository", where.Name, where.Url, where.BaseBranch, where.Branch) : new SandboxWorkspace("none", null, null, null, where?.Branch ?? s.Branch ?? "cowork/thread"),
                new SandboxLimits(memoryMb),
                Network(c, project, s.ThreadId, s.AgentId),
                token);
        }
        var conn = _pools.Get(poolId)?.Connection;
        try
        {
            if (conn is null) throw new InvalidOperationException("the pool went offline");
            if (resume) await _poolHub.Clients.Client(conn).SendAsync("ResumeSandbox", new ResumeSandbox(spec));
            else await _poolHub.Clients.Client(conn).SendAsync("CreateSandbox", new CreateSandbox(spec));
            _log.LogInformation("sandbox {Id} for thread {Thread}: {What} on pool {Pool}", s.Id, s.ThreadId, resume ? "resume" : "create", poolId);
        }
        catch (Exception e) { await Fail(s.Id, poolId, $"the pool couldn't be reached ({e.Message})"); }
    }

    /// <summary>What the sandbox may reach, as Substrate-shaped rules: the server and its gateway always (the pool gives them their own socket),
    /// then the project's network list and what someone allowed for this thread. The pool's proxy enforces it.</summary>
    public static EgressPolicy Network(Microsoft.Data.Sqlite.SqliteConnection c, long? project, long threadId, long agentId)
    {
        var listed = project is null ? new List<string>() : c.Query<string>("SELECT host FROM project_network WHERE project_id=@project ORDER BY host", new { project }).ToList();
        var allowed = c.Query<string>("SELECT host FROM thread_egress WHERE thread_id=@threadId AND agent_id=@agentId ORDER BY host", new { threadId, agentId }).ToList();
        return new(new[] { new EgressRule(new[] { "cowork-server", "cowork-gateway" }), new EgressRule(listed.Concat(allowed).Distinct().ToArray()) });
    }

    /// <summary>A sandbox stopped while a turn may have been running in it (it died, or was paused): the dispatcher decides what the turn does.</summary>
    public event Func<long, long, Task>? Lost;

    /// <summary>Pool: the sandbox tried a host that isn't on its list. Whoever started the thread is asked (once per host while it's pending).</summary>
    public async Task OnEgressRefused(long poolId, EgressRefusal r)
    {
        if (Parse(r.SandboxId) is not { } id) return;
        var host = r.Host.Trim().TrimEnd('.').ToLowerInvariant();
        var outcome = "asked";
        _store.Append(ctx =>
        {
            var s = ctx.Query<SandboxRow>("SELECT * FROM sandboxes WHERE id=@id", new { id }).FirstOrDefault();
            if (s is null || s.PoolId != poolId || host.Length == 0) { outcome = $"ignored: sandbox {id} isn't on pool {poolId}"; return Array.Empty<NewEvent>(); }
            if (ctx.Scalar<int>(@"SELECT COUNT(*) FROM posts WHERE thread_id=@t AND kind='ask' AND json_extract(meta,'$.egress')=1 AND json_extract(meta,'$.host')=@host
                AND json_extract(meta,'$.status')='pending'", new { t = s.ThreadId, host }) > 0) { outcome = "already asked"; return Array.Empty<NewEvent>(); }
            var owner = ctx.Scalar<long>("SELECT owner_id FROM agents WHERE member_id=@a", new { a = s.AgentId });
            var (answerer, leads) = Dispatcher.Answerers(ctx, s.AgentId, s.ThreadId, owner);
            var project = s.ProjectId is { } pid ? ctx.Scalar<string>("SELECT name FROM projects WHERE id=@pid", new { pid }) : null;
            var who = ctx.Scalar<string>("SELECT display_name FROM members WHERE id=@answerer", new { answerer });
            var handle = ctx.Scalar<string>("SELECT handle FROM members WHERE id=@a", new { a = s.AgentId });
            var turn = ctx.Query<(string? Turn, long? Behalf)>("SELECT turn, behalf FROM open_turns WHERE thread_id=@t AND agent_id=@a", new { t = s.ThreadId, a = s.AgentId }).FirstOrDefault();
            return new[] { new NewEvent($"thread-{s.ThreadId}", "EgressRefused", new
            {
                sandboxId = id, agentId = s.AgentId, host, port = r.Port, postId = ctx.NextId("posts"), channelId = ctx.Scalar<long>("SELECT channel_id FROM threads WHERE root_id=@t", new { t = s.ThreadId }),
                ownerId = answerer, leadIds = leads, leadAfter = leads.Length == 0 ? (long?)null : Db.Now() + Dispatcher.LeadAfterMs,
                why = $"It isn't on {project ?? "the project"}'s network list. Asked by {handle}, for {who}'s thread.",
            }, s.AgentId, turn.Behalf ?? s.RequestedBy, turn.Turn) };
        });
        _log.LogInformation("pool {Pool}: sandbox {Id} was refused {Host}:{Port}; {Outcome}", poolId, id, host, r.Port, outcome);
        await Task.CompletedTask;
    }

    /// <summary>Someone allowed a host: the sandbox's proxy is told at once (a "for this thread" allowance is also in its next start's rules).</summary>
    public async Task AllowEgress(long sandboxId, string host, bool once)
    {
        long? poolId;
        using (var c = _db.Open()) poolId = c.ExecuteScalar<long?>("SELECT pool_id FROM sandboxes WHERE id=@sandboxId AND state IN ('starting','resuming','running')", new { sandboxId });
        if (poolId is { } p && _pools.Get(p)?.Connection is { } conn)
            try { await _poolHub.Clients.Client(conn).SendAsync("AllowEgress", new AllowEgress(Name(sandboxId), host, once)); } catch (Exception e) { _log.LogWarning(e, "couldn't tell pool {Pool}", p); }
    }

    /// <summary>Pause all agents: every sandbox of the project stops (volumes kept); new deliveries wait (the dispatcher holds them).</summary>
    public async Task PauseProject(long projectId, long by)
    {
        List<long> ids;
        using (var c = _db.Open()) ids = c.Query<long>("SELECT id FROM sandboxes WHERE project_id=@projectId AND state='running'", new { projectId }).ToList();
        foreach (var id in ids) await Suspend(id, "paused", 0, by);
    }

    /// <summary>What a pool says about one of its sandboxes. A pool can only report on sandboxes the server placed on it.</summary>
    public async Task OnReport(long poolId, SandboxStateReport r)
    {
        if (Parse(r.SandboxId) is not { } id) return;
        var pump = false; var freed = false; var unexpected = false; var pausedNow = false;
        await _lock.WaitAsync();
        try
        {
            _store.Append(ctx =>
            {
                pump = false; freed = false; unexpected = false; pausedNow = false;
                var s = ctx.Query<SandboxRow>("SELECT * FROM sandboxes WHERE id=@id", new { id }).FirstOrDefault();
                if (s is null || s.PoolId != poolId) return Array.Empty<NewEvent>();
                var stream = $"thread-{s.ThreadId}";
                switch (r.State)
                {
                    case SandboxStates.Running when s.State is "starting" or "resuming":
                    {
                        pausedNow = s.ProjectId is { } sp && ctx.Scalar<long?>("SELECT paused_at FROM projects WHERE id=@sp", new { sp }) is not null;
                        var (_, where) = Dispatcher.WorkspaceOf(ctx, s.ThreadId, ctx.Query<MemberRow>(Q.MemberSelect + " WHERE m.id=@a", new { a = s.AgentId }).First(), forPool: true);
                        var pool = ctx.Scalar<string>("SELECT name FROM pools WHERE id=@poolId", new { poolId });
                        return new[] { new NewEvent(stream, s.State == "resuming" ? "SandboxResumed" : "SandboxStarted", new
                        {
                            sandboxId = id, agentId = s.AgentId, postId = s.PostId, poolId, poolName = pool, image = s.Image, memoryMb = s.MemoryMb, branch = s.Branch,
                            workspace = where is { Kind: "repository" } ? where.Name : null, workspaceMs = r.WorkspaceMs, containerMs = r.ContainerMs,
                        }, s.AgentId, s.RequestedBy) };
                    }
                    case SandboxStates.Suspended when s.State is "suspending" or "running" or "starting" or "resuming":
                        freed = true; pump = true; unexpected = s.State != "suspending";
                        ctx.Execute("UPDATE thread_tokens SET revoked_at=@now WHERE sandbox_id=@id AND revoked_at IS NULL", new { now = Db.Now(), id });
                        return new[] { new NewEvent(stream, "SandboxSuspended", new { sandboxId = id, agentId = s.AgentId, postId = s.PostId, poolId }, s.AgentId, s.RequestedBy) };
                    case SandboxStates.Deleted when s.State != "deleted":
                        freed = s.State != "deleting";
                        ctx.Execute("UPDATE thread_tokens SET revoked_at=@now WHERE sandbox_id=@id AND revoked_at IS NULL", new { now = Db.Now(), id });
                        return new[] { new NewEvent(stream, "SandboxDeleted", new { sandboxId = id, agentId = s.AgentId, postId = s.PostId, poolId, branch = s.Branch, branchDeleted = true }, s.AgentId, s.RequestedBy) };
                    case SandboxStates.Kept when s.State == "deleting":
                        return new[] { new NewEvent(stream, "SandboxKept", new { sandboxId = id, agentId = s.AgentId, poolId, branch = s.Branch, reason = r.Reason ?? "it has changes" }, s.AgentId, s.RequestedBy) };
                    case SandboxStates.Failed when s.State is not ("failed" or "deleted"):
                        freed = true;
                        return FailEvents(ctx, s, r.Reason ?? "the sandbox didn't start");
                }
                return Array.Empty<NewEvent>();
            });
        }
        finally { _lock.Release(); }
        if (pausedNow) await Suspend(id, "paused", 0);
        if (unexpected && Lost is { } lost)
        {
            SandboxRow? gone;
            using (var c = _db.Open()) gone = c.QueryFirstOrDefault<SandboxRow>("SELECT * FROM sandboxes WHERE id=@id", new { id });
            if (gone is not null) await lost(gone.AgentId, gone.ThreadId);        // its container died: the turn in it is tried again once
        }
        if (freed) { await _conns.CutOff(id, _runnerHub); await PlaceWaiting(); }
        if (pump) await ResumeIfQueued(id);
    }

    /// <summary>A sandbox that stopped while messages for it were queued (or arrived while it stopped) starts again.</summary>
    private async Task ResumeIfQueued(long sandboxId)
    {
        SandboxRow? s; int queued;
        using (var c = _db.Open())
        {
            s = c.QueryFirstOrDefault<SandboxRow>("SELECT * FROM sandboxes WHERE id=@sandboxId", new { sandboxId });
            if (s is null) return;
            queued = c.ExecuteScalar<int>(Deliveries.QueuedIn, new { agentId = s.AgentId, stream = $"thread-{s.ThreadId}" });
        }
        if (queued > 0) await Ensure(s.ThreadId, s.AgentId, s.RequestedBy);
    }

    /// <summary>The sandbox couldn't start or died: the card says so, the token stops working, and each message waiting for it ends with the
    /// reason (so nothing retries by itself: the next message from a person tries again).</summary>
    private IReadOnlyList<NewEvent> FailEvents(IDecisionContext ctx, SandboxRow s, string reason)
    {
        ctx.Execute("UPDATE thread_tokens SET revoked_at=@now WHERE sandbox_id=@id AND revoked_at IS NULL", new { now = Db.Now(), id = s.Id });
        var stream = $"thread-{s.ThreadId}";
        var channelId = ctx.Scalar<long>("SELECT channel_id FROM threads WHERE root_id=@t", new { t = s.ThreadId });
        var handle = ctx.Scalar<string>("SELECT handle FROM members WHERE id=@a", new { a = s.AgentId });
        var pool = s.PoolId is { } pid ? ctx.Scalar<string>("SELECT name FROM pools WHERE id=@pid", new { pid }) : null;
        var evs = new List<NewEvent> { new(stream, "SandboxFailed", new { sandboxId = s.Id, agentId = s.AgentId, postId = s.PostId, poolId = s.PoolId, reason }, s.AgentId, s.RequestedBy) };
        foreach (var d in ctx.Query<Deliveries.Row>(Deliveries.Queued, new { agentId = s.AgentId }).Where(d => d.ThreadId == s.ThreadId))
        {
            var turn = d.Turn ?? "t-" + Guid.NewGuid().ToString("N")[..12];
            var at = evs.Count;
            evs.Add(new(stream, "TurnStarted", new { agentId = s.AgentId, deliveryId = d.Position, host = pool }, s.AgentId, d.Behalf, turn, d.Position));
            evs.Add(new(stream, "TurnFailed", new { agentId = s.AgentId, channelId, postId = ctx.NextId("posts"), reason = $"{handle}'s sandbox couldn't start: {reason}", ended = true },
                s.AgentId, d.Behalf, turn, CausedByIndex: at));
        }
        return evs;
    }

    private async Task Fail(long sandboxId, long poolId, string reason)
    {
        await _lock.WaitAsync();
        try
        {
            _store.Append(ctx =>
            {
                var s = ctx.Query<SandboxRow>("SELECT * FROM sandboxes WHERE id=@sandboxId", new { sandboxId }).FirstOrDefault();
                return s is null || s.State is "failed" or "deleted" ? Array.Empty<NewEvent>() : FailEvents(ctx, s, reason);
            });
        }
        finally { _lock.Release(); }
        await _conns.CutOff(sandboxId, _runnerHub);
    }

    /// <summary>An idle sandbox stops: its token is revoked at once, the runner inside is cut off, and the pool is asked to stop the container and
    /// keep the volume. It becomes `suspended` when the pool says the container is gone.</summary>
    public async Task Suspend(long sandboxId, string reason, int idleSeconds, long? by = null)
    {
        long? poolId = null;
        await _lock.WaitAsync();
        try
        {
            _store.Append(ctx =>
            {
                poolId = null;
                var s = ctx.Query<SandboxRow>("SELECT * FROM sandboxes WHERE id=@sandboxId", new { sandboxId }).FirstOrDefault();
                if (s is null || s.State != "running") return Array.Empty<NewEvent>();
                poolId = s.PoolId;
                ctx.Execute("UPDATE thread_tokens SET revoked_at=@now WHERE sandbox_id=@sandboxId AND revoked_at IS NULL", new { now = Db.Now(), sandboxId });
                return new[] { new NewEvent($"thread-{s.ThreadId}", "SandboxSuspending", new { sandboxId, agentId = s.AgentId, postId = s.PostId, poolId = s.PoolId, reason, idleSeconds, by }, by ?? s.AgentId, s.RequestedBy) };
            });
        }
        finally { _lock.Release(); }
        if (poolId is null) return;
        if (reason == "paused" && Lost is { } lost)
        {
            SandboxRow? s;
            using (var c = _db.Open()) s = c.QueryFirstOrDefault<SandboxRow>("SELECT * FROM sandboxes WHERE id=@sandboxId", new { sandboxId });
            if (s is not null) await lost(s.AgentId, s.ThreadId);            // a turn in flight waits for the resume
        }
        await _conns.CutOff(sandboxId, _runnerHub);
        if (_pools.Get(poolId.Value)?.Connection is { } conn)
            try { await _poolHub.Clients.Client(conn).SendAsync("SuspendSandbox", new SuspendSandbox(Name(sandboxId))); } catch (Exception e) { _log.LogWarning(e, "couldn't ask pool {Pool} to suspend {Id}", poolId, sandboxId); }
    }

    /// <summary>A pool connected (or came back): it says what it runs. Anything the server thinks is starting or running there that the pool
    /// doesn't run any more stopped while nobody watched (its volume is kept); anything it runs that the server doesn't expect is stopped.</summary>
    public async Task OnHello(long poolId, PoolHello h)
    {
        var running = h.Running.Select(Parse).Where(x => x is not null).Select(x => x!.Value).ToHashSet();
        List<SandboxRow> mine;
        using (var c = _db.Open()) mine = c.Query<SandboxRow>("SELECT * FROM sandboxes WHERE pool_id=@poolId AND state IN ('starting','resuming','running','suspending')", new { poolId }).ToList();
        foreach (var s in mine)
        {
            if (s.State == "suspending" && running.Contains(s.Id))
            { if (_pools.Get(poolId)?.Connection is { } conn) await _poolHub.Clients.Client(conn).SendAsync("SuspendSandbox", new SuspendSandbox(Name(s.Id))); }
            else if (!running.Contains(s.Id) && s.State is "running" or "suspending")
                await OnReport(poolId, new SandboxStateReport(Name(s.Id), SandboxStates.Suspended, "the pool restarted"));
        }
        using (var c = _db.Open())
            foreach (var id in running.Where(id => c.ExecuteScalar<string?>("SELECT state FROM sandboxes WHERE id=@id AND pool_id=@poolId", new { id, poolId }) is not ("running" or "starting" or "resuming")))
                if (_pools.Get(poolId)?.Connection is { } conn) await _poolHub.Clients.Client(conn).SendAsync("SuspendSandbox", new SuspendSandbox(Name(id)));
        await PlaceWaiting();
    }

    /// <summary>Running sandboxes with no turn in flight, nothing queued, and nothing happening for the idle time.</summary>
    public List<long> Idle(int idleSeconds)
    {
        using var c = _db.Open();
        var cutoff = Db.Now() - idleSeconds * 1000L;
        return c.Query<long>(@"SELECT s.id FROM sandboxes s WHERE s.state='running' AND COALESCE(s.last_active, s.started_at, 0) < @cutoff
            AND NOT EXISTS (SELECT 1 FROM open_turns o WHERE o.thread_id=s.thread_id AND o.agent_id=s.agent_id)", new { cutoff })
            .Where(id =>
            {
                var s = c.QueryFirst<SandboxRow>("SELECT * FROM sandboxes WHERE id=@id", new { id });
                return c.ExecuteScalar<int>(Deliveries.QueuedIn, new { agentId = s.AgentId, stream = $"thread-{s.ThreadId}" }) == 0;
            }).ToList();
    }

    /// <summary>A template left a project or was removed: its tokens stop working and its sandboxes stop.</summary>
    public async Task StopAll(long agentId, long? projectId, string reason)
    {
        List<long> ids;
        using (var c = _db.Open())
        {
            ids = c.Query<long>("SELECT id FROM sandboxes WHERE agent_id=@agentId AND (@projectId IS NULL OR project_id=@projectId) AND state NOT IN ('suspended','failed','deleted')", new { agentId, projectId }).ToList();
            c.Execute("UPDATE thread_tokens SET revoked_at=@now WHERE agent_id=@agentId AND (@projectId IS NULL OR project_id=@projectId) AND revoked_at IS NULL", new { now = Db.Now(), agentId, projectId });
        }
        foreach (var id in ids) { await _conns.CutOff(id, _runnerHub); await Suspend(id, reason, 0); }
    }
}

/// <summary>Read-only decision context over an open connection (to reuse the deciders' helpers outside a write).</summary>
public sealed class ReadContext(Microsoft.Data.Sqlite.SqliteConnection c) : IDecisionContext
{
    public long NextId(string table) => throw new InvalidOperationException("read only");
    public T? Scalar<T>(string sql, object? args = null) => c.ExecuteScalar<T>(sql, args);
    public IReadOnlyList<T> Query<T>(string sql, object? args = null) => c.Query<T>(sql, args).ToList();
    public int Execute(string sql, object? args = null) => throw new InvalidOperationException("read only");
}

/// <summary>Suspends sandboxes that have been idle for the server's idle time (Server settings; 15 minutes unless changed).</summary>
public sealed class SandboxReaper(Sandboxes sandboxes, ILogger<SandboxReaper> log) : BackgroundService
{
    public static TimeSpan Every = TimeSpan.FromSeconds(1);
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var idle = sandboxes.IdleSeconds();
                foreach (var id in sandboxes.Idle(idle)) await sandboxes.Suspend(id, "idle", idle);
                await sandboxes.Retire(sandboxes.RetentionSeconds());
            }
            catch (Exception e) { log.LogWarning(e, "couldn't check idle sandboxes"); }
        }
    }
}

/// <summary>Pools. The pool token was checked in middleware; its pool id is in the connection's items. A pool may only report its own state
/// and the state of sandboxes placed on it; it hears nothing but sandbox commands.</summary>
public sealed class PoolHub : Hub
{
    private readonly PoolRegistry _pools;
    private readonly Sandboxes _sandboxes;
    private readonly IEventStore _store;
    private readonly Realtime _rt;
    public PoolHub(PoolRegistry pools, Sandboxes sandboxes, IEventStore store, Realtime rt) { _pools = pools; _sandboxes = sandboxes; _store = store; _rt = rt; }
    private long PoolId => (long)Context.GetHttpContext()!.Items["poolId"]!;

    public async Task Hello(PoolHello h)
    {
        var slots = Math.Clamp(h.Slots, 0, 64);
        _store.Append(new[] { new NewEvent($"pool-{PoolId}", "PoolConnected", new { poolId = PoolId, slots, memoryMb = h.MemoryMb > 0 ? h.MemoryMb : (long?)null }) });
        _pools.Set(PoolId, new PoolRegistry.Entry(Context.ConnectionId, slots, h.MemoryMb));
        await _rt.ToAll("bootstrap-changed");
        await _sandboxes.OnHello(PoolId, h);
    }

    public Task Report(SandboxStateReport r) => _sandboxes.OnReport(PoolId, r);
    public Task EgressRefused(EgressRefusal r) => _sandboxes.OnEgressRefused(PoolId, r);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_pools.Remove(PoolId, Context.ConnectionId))
        {
            _store.Append(new[] { new NewEvent($"pool-{PoolId}", "PoolDisconnected", new { poolId = PoolId }) });
            await _rt.ToAll("bootstrap-changed");
        }
        await base.OnDisconnectedAsync(exception);
    }
}
