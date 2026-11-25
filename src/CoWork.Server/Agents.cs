using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR;

namespace CoWork.Server;

public static class Tokens
{
    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
    public static string NewCode()
    {
        var chars = new char[9];
        for (var i = 0; i < 9; i++) chars[i] = i == 4 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
    public static string NewInviteCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
}

/// <summary>Which agents have a runner connected, and on which connection.</summary>
public sealed class RunnerRegistry
{
    private readonly ConcurrentDictionary<long, string> _conn = new();
    public bool IsOnline(long agentId) => _conn.ContainsKey(agentId);
    public string? ConnectionOf(long agentId) => _conn.TryGetValue(agentId, out var c) ? c : null;
    public void Set(long agentId, string conn) => _conn[agentId] = conn;
    public bool Remove(long agentId, string conn) => _conn.TryRemove(new KeyValuePair<long, string>(agentId, conn));
    public void ForceRemove(long agentId) => _conn.TryRemove(agentId, out _);
}

/// <summary>Turns posts into deliveries and deliveries into runner messages, one turn at a time per (thread, agent).</summary>
public sealed class Dispatcher
{
    private readonly Db _db;
    private readonly Realtime _rt;
    private readonly RunnerRegistry _runners;
    private readonly IHubContext<RunnerHub> _runnerHub;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<(long, long), TurnState> _turns = new();

    private sealed class TurnState { public long? PostId; public StringBuilder Text = new(); public Dictionary<string, long> Tools = new(); }

    public Dispatcher(Db db, Realtime rt, RunnerRegistry runners, IHubContext<RunnerHub> runnerHub)
    { _db = db; _rt = rt; _runners = runners; _runnerHub = runnerHub; }

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex MentionRx = new(@"(?<![\w@])@([a-zA-Z0-9][a-zA-Z0-9_-]*)", RegexOptions.Compiled);

    public static IEnumerable<string> Mentions(string body) => MentionRx.Matches(body).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct();

    /// <summary>AGENTS.md rule 4: say who's speaking.</summary>
    public static string Wrap(string handle, string name, bool owner, string channel, long threadId, string text) =>
        $"<cowork-message from=\"@{Esc(handle)}\" name=\"{Esc(name)}\" owner=\"{(owner ? "true" : "false")}\" channel=\"#{Esc(channel)}\" thread=\"{threadId}\">{text.Replace("</cowork-message", "&lt;/cowork-message")}</cowork-message>";
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");

    /// <summary>Called for every new text post. Routes it to the agents that should hear it. Returns the thread id if one now exists for a top-level post.</summary>
    public async Task Route(long postId)
    {
        var toPump = new List<long>();
        long? threadForPost = null, routedThread = null;
        await _lock.WaitAsync();
        try
        {
            using var c = _db.Open();
            var post = Q.Post(c, postId)!;
            var author = Q.Member(c, post.AuthorId)!;
            var channel = Q.Channel(c, post.ChannelId)!;
            var mentioned = c.Query<MemberRow>(Q.MemberSelect + @" WHERE m.kind='agent' AND m.removed=0 AND a.token_hash IS NOT NULL
                AND m.id IN (SELECT member_id FROM channel_members WHERE channel_id=@ch)", new { ch = post.ChannelId })
                .Where(a => a.Id != author.Id && Mentions(post.Body).Contains(a.Handle.ToLowerInvariant())).ToList();

            long threadId;
            var fromPerson = author.Kind == "person";
            if (post.ThreadId is null)
            {
                if (mentioned.Count == 0) return;
                threadId = post.Id;
                c.Execute("INSERT OR IGNORE INTO threads(root_id, channel_id, started_by) VALUES (@id, @ch, @by)", new { id = post.Id, ch = post.ChannelId, by = post.AuthorId });
                threadForPost = threadId;
            }
            else
            {
                threadId = post.ThreadId.Value;
                if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM threads WHERE root_id=@threadId", new { threadId }) == 0)
                    c.Execute("INSERT INTO threads(root_id, channel_id, started_by) VALUES (@threadId, @ch, @by)", new { threadId, ch = post.ChannelId, by = post.AuthorId });
            }
            routedThread = threadId;
            if (fromPerson) c.Execute("UPDATE threads SET agent_chain=0 WHERE root_id=@threadId", new { threadId });

            var targets = new Dictionary<long, MemberRow>();
            if (fromPerson && post.ThreadId is not null)
                foreach (var a in c.Query<MemberRow>(Q.MemberSelect + @" JOIN thread_agents ta ON ta.agent_id = m.id AND ta.thread_id=@threadId
                    WHERE m.removed=0 AND a.token_hash IS NOT NULL AND m.id IN (SELECT member_id FROM channel_members WHERE channel_id=@ch)", new { threadId, ch = post.ChannelId }))
                    targets[a.Id] = a;
            foreach (var a in mentioned) targets[a.Id] = a;
            targets.Remove(author.Id);

            foreach (var a in targets.Values)
            {
                if (!fromPerson)
                {
                    var chain = c.ExecuteScalar<int>("SELECT agent_chain FROM threads WHERE root_id=@threadId", new { threadId });
                    if (chain >= 3) continue;      // AGENTS.md rule 6: limit loops
                    c.Execute("UPDATE threads SET agent_chain=agent_chain+1 WHERE root_id=@threadId", new { threadId });
                }
                c.Execute("INSERT OR IGNORE INTO thread_agents VALUES (@threadId, @a)", new { threadId, a = a.Id });
                var wrapped = Wrap(author.Handle, author.DisplayName, a.OwnerId == author.Id, channel.Name, threadId, post.Body);
                c.Execute("INSERT INTO deliveries(thread_id, agent_id, post_id, text, status, created_at) VALUES (@threadId, @a, @postId, @wrapped, 'queued', @now)",
                    new { threadId, a = a.Id, postId, wrapped, now = Db.Now() });
                toPump.Add(a.Id);
            }
        }
        finally { _lock.Release(); }

        // the thread's agent status (queued, waiting for the runner) changed
        if (threadForPost is not null) await BroadcastThread(threadForPost.Value);
        else if (toPump.Count > 0 && routedThread is not null) await BroadcastThread(routedThread.Value);
        foreach (var a in toPump.Distinct()) await Pump(a);
    }

    /// <summary>Sends each agent's next queued delivery per thread, if the runner is connected and that thread's agent is idle.</summary>
    public async Task Pump(long agentId)
    {
        var affected = new HashSet<long>();
        await _lock.WaitAsync();
        try
        {
            var conn = _runners.ConnectionOf(agentId);
            if (conn is null) return;
            using var c = _db.Open();
            var agent = Q.Member(c, agentId);
            if (agent is null || agent.Removed) return;
            var rows = c.Query<(long Id, long ThreadId, string Text)>(@"
                SELECT d.id, d.thread_id, d.text FROM deliveries d
                WHERE d.agent_id=@agentId AND d.status='queued'
                  AND NOT EXISTS (SELECT 1 FROM deliveries s WHERE s.agent_id=d.agent_id AND s.thread_id=d.thread_id AND s.status='sent')
                  AND d.id = (SELECT MIN(q.id) FROM deliveries q WHERE q.agent_id=d.agent_id AND q.thread_id=d.thread_id AND q.status='queued')
                ORDER BY d.id", new { agentId }).ToList();
            foreach (var r in rows)
            {
                var msg = new DeliverMessage(r.Id, r.ThreadId, agentId, agent.Handle, agent.Instructions ?? "",
                    JsonSerializer.Deserialize<string[]>(agent.HarnessCommand!)!, agent.Workspace!, r.Text);
                c.Execute("UPDATE deliveries SET status='sent' WHERE id=@Id", new { r.Id });
                _turns[(r.ThreadId, agentId)] = new TurnState();
                try { await _runnerHub.Clients.Client(conn).SendAsync("Deliver", msg); affected.Add(r.ThreadId); }
                catch { c.Execute("UPDATE deliveries SET status='queued' WHERE id=@Id", new { r.Id }); }
            }
        }
        finally { _lock.Release(); }
        foreach (var t in affected) await BroadcastThread(t);
    }

    public async Task RunnerConnected(long agentId, string conn, string? host)
    {
        _runners.Set(agentId, conn);
        using (var c = _db.Open())
        {
            c.Execute("UPDATE agents SET runner_host=COALESCE(@host, runner_host), last_seen=@now WHERE member_id=@agentId", new { host, now = Db.Now(), agentId });
            // a turn that was in flight when the last runner died can't be resumed
            await FailInFlight(c, agentId, "the runner went offline during this turn");
        }
        await _rt.ToAll("bootstrap-changed");
        await BroadcastAgentThreads(agentId);
        await Pump(agentId);
    }

    /// <summary>An agent went online or offline: every thread it's in shows its new status.</summary>
    public async Task BroadcastAgentThreads(long agentId)
    {
        List<long> threads;
        using (var c = _db.Open()) threads = c.Query<long>("SELECT thread_id FROM thread_agents WHERE agent_id=@agentId", new { agentId }).ToList();
        foreach (var t in threads) await BroadcastThread(t);
    }

    public async Task RunnerDisconnected(long agentId, string conn)
    {
        if (!_runners.Remove(agentId, conn)) return;
        using (var c = _db.Open()) await FailInFlight(c, agentId, "the runner went offline during this turn");
        await _rt.ToAll("bootstrap-changed");
        await BroadcastAgentThreads(agentId);
    }

    private async Task FailInFlight(Microsoft.Data.Sqlite.SqliteConnection c, long agentId, string why)
    {
        var stuck = c.Query<(long Id, long ThreadId, long ChannelId)>(@"SELECT d.id, d.thread_id, t.channel_id FROM deliveries d JOIN threads t ON t.root_id=d.thread_id
            WHERE d.agent_id=@agentId AND d.status='sent'", new { agentId }).ToList();
        foreach (var s in stuck)
        {
            c.Execute("UPDATE deliveries SET status='failed' WHERE id=@Id", new { s.Id });
            await EndTurn(s.ThreadId, agentId);
            await Notice(s.ThreadId, agentId, why);
        }
    }

    private async Task Notice(long threadId, long agentId, string text)
    {
        long id; long ch;
        using (var c = _db.Open())
        {
            ch = c.ExecuteScalar<long>("SELECT channel_id FROM threads WHERE root_id=@threadId", new { threadId });
            id = c.ExecuteScalar<long>("INSERT INTO posts(channel_id, thread_id, author_id, body, kind, created_at) VALUES (@ch,@threadId,@agentId,@text,'notice',@now); SELECT last_insert_rowid();",
                new { ch, threadId, agentId, text, now = Db.Now() });
        }
        await EmitPost(id);
    }

    public async Task EmitPost(long postId)
    {
        object dto; long ch; long? root;
        using (var c = _db.Open()) { var p = Q.Post(c, postId)!; ch = p.ChannelId; root = p.ThreadId; dto = Q.FullPostDto(c, postId); }
        await _rt.ToChannel(ch, "post-created", dto);
        if (root is not null) await BroadcastThread(root.Value);
    }

    public async Task BroadcastThread(long rootId)
    {
        long ch; object dto;
        using (var c = _db.Open())
        {
            var p = Q.Post(c, rootId); if (p is null) return;
            ch = p.ChannelId; dto = Q.FullPostDto(c, rootId);
        }
        await _rt.ToChannel(ch, "thread-updated", new { rootId, channelId = ch, post = dto, agents = ThreadAgentStatus(rootId) });
    }

    public List<object> ThreadAgentStatus(long rootId)
    {
        using var c = _db.Open();
        return c.Query<MemberRow>(Q.MemberSelect + " JOIN thread_agents ta ON ta.agent_id=m.id AND ta.thread_id=@rootId WHERE m.removed=0", new { rootId })
            .Select(a => (object)new
            {
                id = a.Id, handle = a.Handle, displayName = a.DisplayName, online = _runners.IsOnline(a.Id),
                busy = c.ExecuteScalar<int>("SELECT COUNT(*) FROM deliveries WHERE thread_id=@rootId AND agent_id=@id AND status='sent'", new { rootId, id = a.Id }) > 0,
                queued = c.ExecuteScalar<int>("SELECT COUNT(*) FROM deliveries WHERE thread_id=@rootId AND agent_id=@id AND status='queued'", new { rootId, id = a.Id }),
                harnessCommand = JsonSerializer.Deserialize<string[]>(a.HarnessCommand!), runnerHost = a.RunnerHost, ownerId = a.OwnerId,
            }).ToList();
    }

    // ---- runner → server -------------------------------------------------

    public async Task OnStream(long agentId, StreamEvent e)
    {
        var key = (e.ThreadId, agentId);
        var st = _turns.GetOrAdd(key, _ => new TurnState());
        long channelId;
        using (var c = _db.Open())
        {
            var th = c.QueryFirstOrDefault<long?>("SELECT channel_id FROM threads WHERE root_id=@t", new { t = e.ThreadId });
            if (th is null) return;
            channelId = th.Value;
        }
        switch (e.Kind)
        {
            case StreamKinds.Text when !string.IsNullOrEmpty(e.Text):
                st.Text.Append(e.Text);
                if (st.PostId is null)
                {
                    using (var c = _db.Open())
                        st.PostId = c.ExecuteScalar<long>(@"INSERT INTO posts(channel_id, thread_id, author_id, body, kind, streaming, created_at) VALUES (@channelId,@t,@agentId,@txt,'text',1,@now); SELECT last_insert_rowid();",
                            new { channelId, t = e.ThreadId, agentId, txt = e.Text, now = Db.Now() });
                    await EmitPost(st.PostId.Value);
                }
                else
                {
                    using (var c = _db.Open()) c.Execute("UPDATE posts SET body = body || @txt WHERE id=@id", new { txt = e.Text, id = st.PostId });
                    await _rt.ToChannel(channelId, "post-delta", new { id = st.PostId, threadId = e.ThreadId, text = e.Text });
                }
                break;
            case StreamKinds.ToolStart:
                await FinishTextPost(st, channelId);
                var meta = JsonSerializer.Serialize(new { toolId = e.ToolId, title = e.Title, toolKind = e.ToolKind, status = e.Status ?? "running" });
                long pid;
                using (var c = _db.Open())
                    pid = c.ExecuteScalar<long>(@"INSERT INTO posts(channel_id, thread_id, author_id, body, kind, meta, created_at) VALUES (@channelId,@t,@agentId,@title,'tool',@meta,@now); SELECT last_insert_rowid();",
                        new { channelId, t = e.ThreadId, agentId, title = e.Title ?? "tool", meta, now = Db.Now() });
                if (e.ToolId is not null) st.Tools[e.ToolId] = pid;
                await EmitPost(pid);
                break;
            case StreamKinds.ToolEnd or StreamKinds.ToolUpdate when e.ToolId is not null && st.Tools.TryGetValue(e.ToolId, out var tp):
                using (var c = _db.Open())
                {
                    var old = c.ExecuteScalar<string>("SELECT meta FROM posts WHERE id=@tp", new { tp });
                    var node = System.Text.Json.Nodes.JsonNode.Parse(old ?? "{}")!.AsObject();
                    if (e.Kind == StreamKinds.ToolEnd) node["status"] = e.Status ?? "completed";
                    if (!string.IsNullOrEmpty(e.Title)) { node["title"] = e.Title; c.Execute("UPDATE posts SET body=@t WHERE id=@tp", new { t = e.Title, tp }); }
                    c.Execute("UPDATE posts SET meta=@m WHERE id=@tp", new { m = node.ToJsonString(), tp });
                }
                await _rt.ToChannel(channelId, "post-updated", Q_Full(tp));
                break;
            case StreamKinds.Error:
                await FinishTextPost(st, channelId);
                await Notice(e.ThreadId, agentId, e.Text ?? "something went wrong");
                break;
            case StreamKinds.TurnEnded:
                await FinishTextPost(st, channelId);
                var said = st.Text.ToString();
                await EndTurn(e.ThreadId, agentId);
                using (var c = _db.Open()) c.Execute("UPDATE deliveries SET status='done' WHERE thread_id=@t AND agent_id=@agentId AND status='sent'", new { t = e.ThreadId, agentId });
                await RouteAgentMentions(e.ThreadId, agentId, channelId, said);
                await BroadcastThread(e.ThreadId);
                await Pump(agentId);
                break;
        }
    }

    private object Q_Full(long id) { using var c = _db.Open(); return Q.FullPostDto(c, id); }

    private async Task FinishTextPost(TurnState st, long channelId)
    {
        if (st.PostId is null) return;
        var id = st.PostId.Value; st.PostId = null;
        using (var c = _db.Open()) c.Execute("UPDATE posts SET streaming=0 WHERE id=@id", new { id });
        await _rt.ToChannel(channelId, "post-updated", Q_Full(id));
    }

    private async Task EndTurn(long threadId, long agentId)
    {
        if (_turns.TryRemove((threadId, agentId), out var st) && st.PostId is not null)
        {
            using var c = _db.Open();
            var p = Q.Post(c, st.PostId.Value);
            c.Execute("UPDATE posts SET streaming=0 WHERE id=@id", new { id = st.PostId });
            if (p is not null) await _rt.ToChannel(p.ChannelId, "post-updated", Q.FullPostDto(c, p.Id));
        }
    }

    /// <summary>An agent that @mentions another agent in a thread starts a turn for it, up to 3 in a row without a person.</summary>
    private async Task RouteAgentMentions(long threadId, long agentId, long channelId, string said)
    {
        if (string.IsNullOrWhiteSpace(said) || !Mentions(said).Any()) return;
        var pump = new List<long>();
        await _lock.WaitAsync();
        try
        {
            using var c = _db.Open();
            var author = Q.Member(c, agentId)!;
            var channel = Q.Channel(c, channelId)!;
            var lastPost = c.ExecuteScalar<long>("SELECT MAX(id) FROM posts WHERE thread_id=@threadId AND author_id=@agentId AND kind='text'", new { threadId, agentId });
            var wanted = Mentions(said).ToHashSet();
            var targets = c.Query<MemberRow>(Q.MemberSelect + @" WHERE m.kind='agent' AND m.removed=0 AND a.token_hash IS NOT NULL
                AND m.id IN (SELECT member_id FROM channel_members WHERE channel_id=@channelId)", new { channelId })
                .Where(a => a.Id != agentId && wanted.Contains(a.Handle.ToLowerInvariant()));
            foreach (var a in targets)
            {
                var chain = c.ExecuteScalar<int>("SELECT agent_chain FROM threads WHERE root_id=@threadId", new { threadId });
                if (chain >= 3) break;
                c.Execute("UPDATE threads SET agent_chain=agent_chain+1 WHERE root_id=@threadId", new { threadId });
                c.Execute("INSERT OR IGNORE INTO thread_agents VALUES (@threadId, @a)", new { threadId, a = a.Id });
                c.Execute("INSERT INTO deliveries(thread_id, agent_id, post_id, text, status, created_at) VALUES (@threadId,@a,@lastPost,@w,'queued',@now)",
                    new { threadId, a = a.Id, lastPost, w = Wrap(author.Handle, author.DisplayName, false, channel.Name, threadId, said), now = Db.Now() });
                pump.Add(a.Id);
            }
        }
        finally { _lock.Release(); }
        foreach (var a in pump) await Pump(a);
    }

    /// <summary>The runner asks permission; only the owner gets buttons (the client decides what to show from ownerId).</summary>
    public async Task OnPermission(long agentId, PermissionRequestMsg r)
    {
        long ch, id;
        using (var c = _db.Open())
        {
            var th = c.QueryFirstOrDefault<long?>("SELECT channel_id FROM threads WHERE root_id=@t", new { t = r.ThreadId });
            if (th is null) return;
            ch = th.Value;
            var owner = c.ExecuteScalar<long>("SELECT owner_id FROM agents WHERE member_id=@agentId", new { agentId });
            var meta = JsonSerializer.Serialize(new { requestId = r.RequestId, agentId, ownerId = owner, title = r.Title, options = r.Options, status = "pending" }, Camel);
            id = c.ExecuteScalar<long>(@"INSERT INTO posts(channel_id, thread_id, author_id, body, kind, meta, created_at) VALUES (@ch,@t,@agentId,@title,'ask',@meta,@now); SELECT last_insert_rowid();",
                new { ch, t = r.ThreadId, agentId, title = r.Title, meta, now = Db.Now() });
        }
        await EmitPost(id);
    }

    /// <summary>The owner's answer to an ask card. Returns false if the caller may not answer.</summary>
    public async Task<(bool ok, string? error)> Answer(long postId, long callerId, string? optionId)
    {
        System.Text.Json.Nodes.JsonObject meta; long agentId; long ch;
        using (var c = _db.Open())
        {
            var p = Q.Post(c, postId);
            if (p is null || p.Kind != "ask" || p.Meta is null) return (false, "not found");
            meta = System.Text.Json.Nodes.JsonNode.Parse(p.Meta)!.AsObject();
            if ((long)meta["ownerId"]! != callerId) return (false, "forbidden");
            if ((string?)meta["status"] != "pending") return (false, "already answered");
            if (!Access.CanSee(c, p.ChannelId, callerId)) return (false, "forbidden");
            agentId = (long)meta["agentId"]!; ch = p.ChannelId;
            meta["status"] = optionId is null ? "denied" : "answered";
            meta["answer"] = optionId;
            c.Execute("UPDATE posts SET meta=@m WHERE id=@postId", new { m = meta.ToJsonString(), postId });
        }
        var conn = _runners.ConnectionOf(agentId);
        if (conn is not null)
            await _runnerHub.Clients.Client(conn).SendAsync("PermissionAnswer", new PermissionAnswerMsg((string)meta["requestId"]!, optionId));
        await _rt.ToChannel(ch, "post-updated", Q_Full(postId));
        return (true, null);
    }
}
