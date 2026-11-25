using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>
/// CoWork's tools for agents, as an MCP server (Streamable HTTP, JSON responses) at <c>/mcp</c>. Every agent's runner passes it to the harness
/// in <c>session/new</c> (brief 7, round 3; AGENTS.md rule 3: agents pull context, we don't inject it). A sandbox authenticates with its
/// thread token; a pinned runner with its own token and the thread in <c>X-Cowork-Thread</c> (a thread its agent is in). Every call is for that
/// one thread and is checked against the agent's boundary: it reads and searches only the channels it takes part in, and the only thing it
/// writes is its own thread's outcome. Each call is recorded (`CoworkToolCalled`), refused ones too.
/// </summary>
public sealed class Mcp
{
    public const string Protocol = "2025-06-18";
    private readonly Db _db;
    private readonly IEventStore _store;
    private readonly ProjectionRunner _views;
    private readonly ILogger<Mcp> _log;
    public Mcp(Db db, IEventStore store, ProjectionRunner views, ILogger<Mcp> log) { _db = db; _store = store; _views = views; _log = log; }

    public sealed record Caller(long AgentId, long ThreadId, string Handle);

    public static void Map(WebApplication app)
    {
        app.MapPost("/mcp", (HttpContext ctx, Mcp m) => m.Handle(ctx));
        app.MapGet("/mcp", () => Results.StatusCode(405));            // no server-sent stream: every answer comes back on its request
        app.MapDelete("/mcp", () => Results.Ok());
    }

    /// <summary>Who is calling, for which thread: a thread token, or a runner's token and a thread its agent is in. Null: refused.</summary>
    public static Caller? Authenticate(SqliteConnection c, string? authorization, string? threadHeader)
    {
        var token = authorization is { } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Tokens.Hash(token);
        if (ThreadTokens.Find(c, hash) is { } t) return new(t.AgentId, t.ThreadId, Q.Member(c, t.AgentId)!.Handle);
        var agent = c.QueryFirstOrDefault<long?>("SELECT rs.agent_id FROM runner_secrets rs JOIN members m ON m.id=rs.agent_id WHERE rs.token_hash=@hash AND m.removed=0", new { hash });
        if (agent is null || !long.TryParse(threadHeader, out var thread)) return null;
        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM thread_agents WHERE thread_id=@thread AND agent_id=@agent", new { thread, agent }) == 0) return null;
        return new(agent.Value, thread, Q.Member(c, agent.Value)!.Handle);
    }

    private async Task Handle(HttpContext ctx)
    {
        Caller? who;
        using (var c = _db.Open()) who = Authenticate(c, ctx.Request.Headers.Authorization.ToString(), ctx.Request.Headers["X-Cowork-Thread"].ToString());
        if (who is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "CoWork's tools take a thread token, or a runner's token and its thread." }); return; }
        JsonNode? body;
        try { body = await JsonNode.ParseAsync(ctx.Request.Body); } catch { ctx.Response.StatusCode = 400; return; }
        var batch = body as JsonArray;
        var answers = new JsonArray();
        foreach (var msg in batch?.ToList() ?? new List<JsonNode?> { body })
            if (msg is JsonObject o && await Dispatch(who, o) is { } a) answers.Add(a);
        if (answers.Count == 0) { ctx.Response.StatusCode = 202; return; }       // notifications only
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync((batch is null ? answers[0]!.DeepClone() : answers).ToJsonString());
    }

    private async Task<JsonObject?> Dispatch(Caller who, JsonObject m)
    {
        var id = m["id"]?.DeepClone();
        var method = (string?)m["method"];
        if (id is null) return null;                                             // a notification (notifications/initialized …)
        JsonNode? Result(JsonNode r) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = r };
        switch (method)
        {
            case "initialize":
                return (JsonObject)Result(new JsonObject
                {
                    ["protocolVersion"] = (string?)m["params"]?["protocolVersion"] ?? Protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "cowork", ["version"] = "0.7" },
                    ["instructions"] = "CoWork's tools for this thread: read a channel or a thread you take part in, search what you may see, and resolve this thread with an outcome when the work is done.",
                })!;
            case "ping": return (JsonObject)Result(new JsonObject())!;
            case "tools/list": return (JsonObject)Result(new JsonObject { ["tools"] = Tools() })!;
            case "tools/call":
            {
                var name = (string?)m["params"]?["name"] ?? "";
                var args = m["params"]?["arguments"] as JsonObject ?? new JsonObject();
                var (text, error) = await Call(who, name, args);
                return (JsonObject)Result(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = error })!;
            }
            default:
                return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found: " + method } };
        }
    }

    static JsonArray Tools() => new(
        Tool("cowork_read_channel", "Read the latest messages of a channel you take part in (by default this thread's channel), oldest first, with who wrote each and which have threads. Give threadId to read one thread instead.",
            new JsonObject { ["channel"] = Prop("string", "The channel's name, like #checkout-app. Default: this thread's channel."), ["limit"] = Prop("integer", "How many messages, 1 to 100 (default 30)."),
                ["threadId"] = Prop("integer", "Read this thread (its opening post and replies) instead.") }),
        Tool("cowork_search", "Search the conversations you may see: outcomes of resolved threads first, then threads and messages. Words, \"a phrase\", in:#channel, from:@someone.",
            new JsonObject { ["query"] = Prop("string", "What to look for."), ["limit"] = Prop("integer", "At most this many of each kind (default 8).") }, "query"),
        Tool("cowork_resolve_thread", "Mark this thread done, with an outcome people see on the post, in the thread and in search: a short title and a summary of what was found or done. Use it when the work is finished.",
            new JsonObject { ["summary"] = Prop("string", "What was found or done, in a few sentences or bullet lines."), ["title"] = Prop("string", "A short title for the outcome (default: the summary's first line).") }, "summary"));

    static JsonObject Tool(string name, string description, JsonObject props, params string[] required) => new()
    {
        ["name"] = name, ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()) },
    };
    static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    /// <summary>Runs a tool for the caller's thread. Returns the text and whether it's an error (a refusal is an error the agent can read).</summary>
    public async Task<(string Text, bool Error)> Call(Caller who, string name, JsonObject args)
    {
        (string Text, bool Error, object Detail) r;
        try
        {
            switch (name)
            {
                case "cowork_read_channel": r = Read(who, args); break;
                case "cowork_search": await _views.CatchUpAsync(); r = SearchFor(who, args); break;
                case "cowork_resolve_thread": r = Resolve(who, args); break;
                default: return ($"There's no tool called {name}.", true);
            }
        }
        catch (Exception e) { _log.LogWarning(e, "cowork tool {Tool} failed", name); r = ($"{name} failed: {e.Message}", true, new { }); }
        Record(who, name, r.Error, r.Detail);
        return (r.Text, r.Error);
    }

    private void Record(Caller who, string tool, bool refused, object detail)
    {
        try
        {
            _store.Append(ctx =>
            {
                var turn = ctx.Query<(string? Turn, long? Behalf)>("SELECT turn, behalf FROM open_turns WHERE thread_id=@t AND agent_id=@a", new { t = who.ThreadId, a = who.AgentId }).FirstOrDefault();
                return new[] { new NewEvent($"thread-{who.ThreadId}", "CoworkToolCalled", new { agentId = who.AgentId, tool, refused, detail }, who.AgentId, turn.Behalf, turn.Turn) };
            });
        }
        catch (Exception e) { _log.LogWarning(e, "couldn't record a cowork tool call"); }
    }

    /// <summary>The channel, if the agent takes part in it (its boundary: added to it, or in the project it belongs to).</summary>
    static ChannelRow? Visible(SqliteConnection c, long agentId, long channelId) =>
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM channel_audience WHERE channel_id=@channelId AND member_id=@agentId", new { channelId, agentId }) > 0 ? Q.Channel(c, channelId) : null;

    static string When(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private (string, bool, object) Read(Caller who, JsonObject args)
    {
        using var c = _db.Open();
        var limit = Math.Clamp((int?)args["limit"] ?? 30, 1, 100);
        var sb = new StringBuilder();
        string Name(long id) => Q.Member(c, id)?.DisplayName ?? "someone";
        if ((long?)args["threadId"] is { } threadId)
        {
            var root = Q.Post(c, threadId);
            if (root is null || root.ThreadId is not null || Visible(c, who.AgentId, root.ChannelId) is not { } tch)
                return ($"{who.Handle} can't read thread {threadId}: it isn't in a channel {who.Handle} takes part in.", true, new { threadId });
            sb.AppendLine($"Thread {threadId} in #{tch.Name}, oldest first:");
            sb.AppendLine($"[{When(root.CreatedAt)}] {Name(root.AuthorId)}: {root.Body}");
            foreach (var p in c.Query<PostRow>(Q.PostSelect + " WHERE p.thread_id=@threadId AND p.kind IN ('text','outcome') ORDER BY p.id", new { threadId }))
                sb.AppendLine($"[{When(p.CreatedAt)}] {Name(p.AuthorId)}: {(p.Kind == "outcome" ? "(outcome) " : "")}{p.Body}");
            return (sb.ToString().TrimEnd(), false, new { threadId, channel = tch.Name });
        }
        var asked = ((string?)args["channel"])?.Trim().TrimStart('#').ToLowerInvariant();
        var channelId = string.IsNullOrEmpty(asked) ? c.ExecuteScalar<long>("SELECT channel_id FROM threads WHERE root_id=@t", new { t = who.ThreadId })
            : c.ExecuteScalar<long?>("SELECT id FROM channels WHERE name=@asked", new { asked }) ?? 0;
        if (Visible(c, who.AgentId, channelId) is not { } ch)
            return ($"{who.Handle} can't read #{asked}: it isn't a channel {who.Handle} takes part in, so it's outside its boundary.", true, new { channel = asked });
        var posts = c.Query<PostRow>(Q.PostSelect + " WHERE p.channel_id=@channelId AND p.thread_id IS NULL AND p.kind='text' ORDER BY p.id DESC LIMIT @limit", new { channelId, limit }).Reverse().ToList();
        var project = ch.ProjectId is { } pid ? Projects.Get(c, pid)?.Name : null;
        sb.AppendLine($"#{ch.Name}{(project is null ? "" : $" ({project})")}, the last {posts.Count} messages, oldest first:");
        foreach (var p in posts)
        {
            sb.Append($"[{When(p.CreatedAt)}] {Name(p.AuthorId)}: {p.Body}");
            if (p.ReplyCount > 0 || p.HasThread) sb.Append($"  (thread {p.Id}, {p.ReplyCount} {(p.ReplyCount == 1 ? "reply" : "replies")}{(ThreadStatus.Of(c, p.Id)?.State is "done" ? ", done" : "")})");
            sb.AppendLine();
        }
        return (sb.ToString().TrimEnd(), false, new { channel = ch.Name, count = posts.Count });
    }

    private (string, bool, object) SearchFor(Caller who, JsonObject args)
    {
        var query = (string?)args["query"] ?? "";
        var limit = Math.Clamp((int?)args["limit"] ?? 8, 1, 30);
        using var c = _db.Open();
        var q = SearchQuery.Parse(query);
        if (q.IsEmpty) return ("Say what to search for.", true, new { query });
        var res = System.Text.Json.JsonSerializer.SerializeToNode(Search.Run(c, who.AgentId, q, limit, 0, Search.AgentSees))!;
        string Clean(string? s) => (s ?? "").Replace(Search.HitStart.ToString(), "").Replace(Search.HitEnd.ToString(), "");
        var sb = new StringBuilder();
        var outcomes = res["outcomes"]!["items"]!.AsArray(); var threads = res["threads"]!["items"]!.AsArray(); var messages = res["messages"]!["items"]!.AsArray();
        if (outcomes.Count + threads.Count + messages.Count == 0) return ($"Nothing found for \"{query}\" in what {who.Handle} may see.", false, new { query, found = 0 });
        if (outcomes.Count > 0) { sb.AppendLine("Outcomes:"); foreach (var o in outcomes) sb.AppendLine($"- {Clean((string?)o!["title"])} (thread {o["threadId"]} in #{o["channelName"]}): {Clean((string?)o["snippet"])}"); }
        if (threads.Count > 0) { sb.AppendLine("Threads:"); foreach (var t in threads) sb.AppendLine($"- thread {t!["rootId"]} in #{t["channelName"]}: {Clean((string?)t["title"])}{(t["match"] is JsonObject mm ? " … " + Clean((string?)mm["snippet"]) : "")}"); }
        if (messages.Count > 0) { sb.AppendLine("Messages:"); foreach (var m in messages) sb.AppendLine($"- #{m!["channelName"]}{(m["threadId"] is JsonValue tv ? $" (thread {tv})" : "")}: {Clean((string?)m["snippet"])}"); }
        return (sb.ToString().TrimEnd(), false, new { query, found = outcomes.Count + threads.Count + messages.Count });
    }

    /// <summary>The thread is done: an outcome with what was found or done, how long it took, what it used and what became of its branch.</summary>
    private (string, bool, object) Resolve(Caller who, JsonObject args)
    {
        var summary = ((string?)args["summary"] ?? "").Trim();
        if (summary.Length == 0) return ("Give a summary of what was found or done.", true, new { });
        if (summary.Length > 4000) summary = summary[..4000] + "…";
        var title = ((string?)args["title"])?.Trim();
        if (string.IsNullOrEmpty(title)) title = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].TrimStart('-', '*', ' ');
        if (title.Length > 120) title = title[..120].TrimEnd() + "…";
        long postId = 0;
        _store.Append(ctx =>
        {
            var t = who.ThreadId;
            var channelId = ctx.Scalar<long>("SELECT channel_id FROM threads WHERE root_id=@t", new { t });
            var started = ctx.Scalar<long>("SELECT created_at FROM posts WHERE id=@t", new { t });
            var usage = ctx.Query<(long Tokens, double? Cost, long Calls)>("SELECT COALESCE(SUM(prompt_tokens+completion_tokens),0), SUM(cost), COALESCE(SUM(calls),0) FROM model_usage WHERE thread_id=@t", new { t }).First();
            var stream = $"thread-{t}";
            var tools = ctx.Scalar<int>("SELECT COUNT(*) FROM events WHERE stream_id=@stream AND type='ToolCallStarted'", new { stream });
            var asks = ctx.Scalar<int>("SELECT COUNT(*) FROM events WHERE stream_id=@stream AND type IN ('PermissionRequested','EgressRefused')", new { stream });
            var place = ctx.Query<(string? Branch, string? Place, string? Host)>("SELECT branch, place, host FROM thread_agents WHERE thread_id=@t AND agent_id=@a", new { t, a = who.AgentId }).FirstOrDefault();
            var sandbox = ctx.Query<(long Id, string? Pool, string? Branch)>("SELECT s.id, p.name, s.branch FROM sandboxes s LEFT JOIN pools p ON p.id=s.pool_id WHERE s.thread_id=@t AND s.agent_id=@a", new { t, a = who.AgentId }).FirstOrDefault();
            var project = ctx.Scalar<long?>("SELECT project_id FROM channels WHERE id=@channelId", new { channelId });
            var caps = project is { } pj ? ctx.Query<(long? Tokens, double? Usd)>("SELECT thread_tokens, thread_usd FROM budgets WHERE project_id=@pj", new { pj }).FirstOrDefault() : default;
            var turn = ctx.Query<(string? Turn, long? Behalf)>("SELECT turn, behalf FROM open_turns WHERE thread_id=@t AND agent_id=@a", new { t, a = who.AgentId }).FirstOrDefault();
            postId = ctx.NextId("posts");
            var branch = sandbox.Id != 0 ? sandbox.Branch ?? place.Branch : place.Branch;
            return new[] { new NewEvent(stream, "ThreadResolved", new
            {
                agentId = who.AgentId, postId, channelId, title, summary, startedBy = ctx.Scalar<long>("SELECT started_by FROM threads WHERE root_id=@t", new { t }),
                elapsedMs = Db.Now() - started, tokens = usage.Tokens, cost = usage.Cost, calls = usage.Calls, capTokens = caps.Tokens, capUsd = caps.Usd, toolCalls = tools, asks,
                branch = branch is null ? null : new { name = branch, state = sandbox.Id != 0 ? "volume" : place.Place ?? "worktree", host = sandbox.Id != 0 ? sandbox.Pool : place.Host },
                sandbox = sandbox.Id == 0 ? null : new { id = sandbox.Id, pool = sandbox.Pool },
            }, who.AgentId, turn.Behalf, turn.Turn) };
        });
        return ($"Thread {who.ThreadId} is done: \"{title}\". The outcome shows on the post, in the thread and in search.", false, new { postId, title });
    }
}
