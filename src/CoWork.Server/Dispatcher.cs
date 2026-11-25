using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR;

namespace CoWork.Server;

/// <summary>
/// Decides what happens when someone posts or a runner reports, and writes it down as events (the thread's history).
/// The read tables the app shows follow from the events inline. One turn at a time per (thread, agent).
/// </summary>
public sealed class Dispatcher
{
    /// <summary>How long streamed text may go unstored. A crash loses at most this much of an answer.</summary>
    public static int FlushIntervalMs = 5000;
    public const int MaxToolOutputBytes = 64 * 1024;

    private readonly Db _db;
    private readonly IEventStore _store;
    private readonly Realtime _rt;
    private readonly RunnerRegistry _runners;
    private readonly IHubContext<RunnerHub> _runnerHub;
    private readonly LiveQueue _live;
    private readonly ThreadViews _views;
    private readonly SandboxRegistry _sandboxConns;
    private readonly Sandboxes _sandboxes;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<(long, long), TurnState> _turns = new();

    /// <summary>One agent's turn in one thread, in memory: the text not yet stored, the open tool rows, who started it.</summary>
    private sealed class TurnState
    {
        public readonly object Gate = new();
        public long ThreadId, AgentId, ChannelId;
        public string Turn = ""; public long? Behalf, StartedAt;
        public long? PostId;                         // the text post being written
        public StringBuilder Pending = new(), Said = new();
        public int Segment, RunLen; public long LastFlush;
        public Dictionary<string, long> Tools = new();
        public Timer? Timer; public bool Closed;
    }

    private sealed class InFlight { public long ThreadId { get; set; } public long ChannelId { get; set; } public string? Turn { get; set; } public long? Behalf { get; set; } public long? Cause { get; set; } }

    public Dispatcher(Db db, IEventStore store, Realtime rt, RunnerRegistry runners, IHubContext<RunnerHub> runnerHub, LiveQueue live, ThreadViews views,
        SandboxRegistry sandboxConns, Sandboxes sandboxes)
    { _db = db; _store = store; _rt = rt; _runners = runners; _runnerHub = runnerHub; _live = live; _views = views; _sandboxConns = sandboxConns; _sandboxes = sandboxes; }

    /// <summary>Where a message for this agent in this thread goes: a template's sandbox for that thread, else the agent's runner.</summary>
    private string? ConnectionFor(MemberRow agent, long threadId) => agent.IsTemplate ? _sandboxConns.ConnectionOf(agent.Id, threadId) : _runners.ConnectionOf(agent.Id);

    private static readonly Regex MentionRx = new(@"(?<![\w@])@([a-zA-Z0-9][a-zA-Z0-9_-]*)", RegexOptions.Compiled);
    private static string NewTurn() => "t-" + Guid.NewGuid().ToString("N")[..12];

    public static IEnumerable<string> Mentions(string body) => MentionRx.Matches(body).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct();

    /// <summary>AGENTS.md rule 4: say who's speaking.</summary>
    public static string Wrap(string handle, string name, bool owner, string channel, long threadId, string text) =>
        $"<cowork-message from=\"@{Esc(handle)}\" name=\"{Esc(name)}\" owner=\"{(owner ? "true" : "false")}\" channel=\"#{Esc(channel)}\" thread=\"{threadId}\">{text.Replace("</cowork-message", "&lt;/cowork-message")}</cowork-message>";
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");

    /// <summary>Why an agent won't take work from this person in this channel, or null if it will. Only a channel in a project has a grant:
    /// "owner" (its owner only) or "members" (the project's members, and its owner). Returns the grant that refused.</summary>
    private static string? Refusal(IDecisionContext ctx, ChannelRow channel, MemberRow agent, long? personId)
    {
        if (channel.ProjectId is not { } project) return null;
        var grant = ctx.Scalar<string>("SELECT takes_work_from FROM project_agents WHERE project_id=@project AND agent_id=@a", new { project, a = agent.Id });
        if (grant is null || personId == agent.OwnerId) return null;          // not in the project (added to the channel by hand): as before
        if (grant == "owner") return "owner";
        return ctx.Scalar<int>("SELECT COUNT(*) FROM project_members WHERE project_id=@project AND member_id=@personId", new { project, personId }) > 0 ? null : "members";
    }

    private static string RefusalReply(IDecisionContext ctx, ChannelRow channel, MemberRow agent, long? personId)
    {
        var owner = ctx.Scalar<string>("SELECT display_name FROM members WHERE id=@o", new { o = agent.OwnerId }) ?? "its owner";
        var project = ctx.Scalar<string>("SELECT name FROM projects WHERE id=@p", new { p = channel.ProjectId }) ?? "this project";
        return Refusal(ctx, channel, agent, personId) == "owner"
            ? $"{agent.Handle} only takes work from {owner}."
            : $"{agent.Handle} only takes work from members of {project}.";
    }

    // ---- people → agents ---------------------------------------------------

    /// <summary>A person posts. One transaction records the post and, if agents should hear it, the thread, who joined and what was delivered to each.
    /// Returns the post's id (a thread's id is its root post's id).</summary>
    public async Task<long> Post(long authorId, long channelId, long? threadId, string body)
    {
        long postId = 0; var toPump = new List<long>();
        await _lock.WaitAsync();
        try
        {
            _store.Append(ctx =>
            {
                toPump.Clear();
                var evs = new List<NewEvent>();
                var author = ctx.Query<MemberRow>(Q.MemberSelect + " WHERE m.id=@authorId", new { authorId }).First();
                var channel = ctx.Query<ChannelRow>("SELECT * FROM channels WHERE id=@channelId", new { channelId }).First();
                postId = ctx.NextId("posts");
                var mentions = Mentions(body).ToArray();
                var mentioned = ctx.Query<MemberRow>(Q.MemberSelect + @" WHERE m.kind='agent' AND m.removed=0 AND " + Q.CanWork + @"
                    AND m.id IN (SELECT member_id FROM channel_audience WHERE channel_id=@channelId)", new { channelId })
                    .Where(a => a.Id != author.Id && mentions.Contains(a.Handle.ToLowerInvariant())).ToList();
                var posted = new { postId, channelId, threadId, body = body, mentions };
                // the agent's grant in the channel's project says who may give it work; a mention from anyone else gets a short reply instead
                var refused = mentioned.Where(a => Refusal(ctx, channel, a, authorId) is not null).ToList();
                mentioned = mentioned.Except(refused).ToList();
                long root;
                int causeIdx;
                if (threadId is null)
                {
                    evs.Add(new NewEvent($"channel-{channelId}", "MessagePosted", posted, authorId));
                    if (mentioned.Count == 0 && refused.Count == 0) return evs;
                    root = postId; causeIdx = 0;
                    evs.Add(new NewEvent($"thread-{root}", "ThreadStarted", new { rootId = root, channelId, startedBy = authorId, slug = Projects.Slug(body, root) }, null, authorId, CausedByIndex: 0));
                }
                else
                {
                    root = threadId.Value;
                    if (ctx.Scalar<int>("SELECT COUNT(*) FROM threads WHERE root_id=@root", new { root }) == 0)
                    {
                        var rootBody = ctx.Scalar<string>("SELECT body FROM posts WHERE id=@root", new { root }) ?? "";
                        evs.Add(new NewEvent($"thread-{root}", "ThreadStarted", new { rootId = root, channelId, startedBy = authorId, slug = Projects.Slug(rootBody, root) }, null, authorId));
                    }
                    causeIdx = evs.Count;
                    evs.Add(new NewEvent($"thread-{root}", "MessagePosted", posted, authorId));
                }

                foreach (var a in refused)
                    evs.Add(new NewEvent($"thread-{root}", "WorkRefused", new
                    {
                        agentId = a.Id, ownerId = a.OwnerId, channelId, projectId = channel.ProjectId, postId = ctx.NextId("posts"), askedIn = postId,
                        grant = Refusal(ctx, channel, a, authorId), reply = RefusalReply(ctx, channel, a, authorId),
                    }, a.Id, authorId, CausedByIndex: causeIdx));

                var targets = new Dictionary<long, MemberRow>();
                if (threadId is not null)
                    foreach (var a in ctx.Query<MemberRow>(Q.MemberSelect + @" JOIN thread_agents ta ON ta.agent_id = m.id AND ta.thread_id=@root
                        WHERE m.removed=0 AND " + Q.CanWork + @" AND m.id IN (SELECT member_id FROM channel_audience WHERE channel_id=@channelId)", new { root, channelId }))
                        if (Refusal(ctx, channel, a, authorId) is null) targets[a.Id] = a;      // a reply from someone the grant doesn't allow isn't passed on (only a mention gets the short reply)
                foreach (var a in mentioned) targets[a.Id] = a;
                targets.Remove(author.Id);

                foreach (var a in targets.Values)
                {
                    var turn = NewTurn();
                    var joined = threadId is not null && ctx.Scalar<int>("SELECT COUNT(*) FROM thread_agents WHERE thread_id=@root AND agent_id=@a", new { root, a = a.Id }) > 0;
                    if (!joined) evs.Add(new NewEvent($"thread-{root}", "AgentJoinedThread", new { agentId = a.Id, ownerId = a.OwnerId }, null, authorId, turn, CausedByIndex: causeIdx));
                    evs.Add(new NewEvent($"thread-{root}", "MessageDelivered", new
                    {
                        agentId = a.Id, postId, chained = false,
                        text = Wrap(author.Handle, author.DisplayName, a.OwnerId == author.Id, channel.Name, root, body),
                    }, null, authorId, turn, CausedByIndex: causeIdx));
                    toPump.Add(a.Id);
                }
                return evs;
            });
        }
        finally { _lock.Release(); }
        foreach (var a in toPump.Distinct()) await Pump(a);
        return postId;
    }

    /// <summary>Sends each agent's next queued delivery per thread, if the runner is connected and that thread's agent is idle.</summary>
    public async Task Pump(long agentId)
    {
        List<(long Thread, long? Behalf)> pendingSandboxes = new();
        await _lock.WaitAsync();
        try
        {
            bool template;
            using (var c = _db.Open()) template = c.ExecuteScalar<string?>("SELECT placement FROM agents WHERE member_id=@agentId", new { agentId }) == "pool";
            if (!template && _runners.ConnectionOf(agentId) is null) return;
            var sends = new List<(Deliveries.Row Row, string Turn, DeliverMessage Msg, string Conn)>();
            var needSandbox = new List<(long Thread, long? Behalf)>();
            var stored = _store.Append(ctx =>
            {
                sends.Clear(); needSandbox.Clear();
                var agent = ctx.Query<MemberRow>(Q.MemberSelect + " WHERE m.id=@agentId", new { agentId }).FirstOrDefault();
                if (agent is null || agent.Removed) return Array.Empty<NewEvent>();
                var record = ctx.Scalar<int>("SELECT record_tool_output FROM agents WHERE member_id=@agentId", new { agentId }) == 1;
                var evs = new List<NewEvent>();
                // the runner catches up from its position: what is queued after it, the oldest per thread, in threads where this agent is idle
                var busy = ctx.Query<long>("SELECT thread_id FROM open_turns WHERE agent_id=@agentId", new { agentId }).ToHashSet();
                var seen = new HashSet<long>();
                foreach (var r in ctx.Query<Deliveries.Row>(Deliveries.Queued, new { agentId }))
                {
                    if (!seen.Add(r.ThreadId) || busy.Contains(r.ThreadId)) continue;
                    var to = ConnectionFor(agent, r.ThreadId);
                    if (to is null) { if (agent.IsTemplate) needSandbox.Add((r.ThreadId, r.Behalf)); continue; }      // a template's thread with no sandbox connected: ask for one
                    var turn = r.Turn ?? NewTurn();
                    var host = agent.IsTemplate ? ctx.Scalar<string?>("SELECT p.name FROM sandboxes s JOIN pools p ON p.id=s.pool_id WHERE s.thread_id=@t AND s.agent_id=@agentId", new { t = r.ThreadId, agentId }) : agent.RunnerHost;
                    evs.Add(new NewEvent($"thread-{r.ThreadId}", "TurnStarted", new { agentId, deliveryId = r.Position, host }, agentId, r.Behalf, turn, r.Position));
                    var (project, where) = WorkspaceOf(ctx, r.ThreadId, agent, forPool: agent.IsTemplate);
                    sends.Add((r, turn, new DeliverMessage(r.Position, r.ThreadId, agentId, agent.Handle, agent.Instructions ?? "",
                        JsonSerializer.Deserialize<string[]>(agent.HarnessCommand!)!, agent.Workspace ?? "", r.Text, record, project, where), to));
                }
                return evs;
            });
            for (var i = 0; i < sends.Count; i++)
            {
                var (r, turn, msg, conn) = sends[i];
                long channelId;
                using (var c = _db.Open()) channelId = c.ExecuteScalar<long>("SELECT channel_id FROM threads WHERE root_id=@t", new { t = r.ThreadId });
                _turns[(r.ThreadId, agentId)] = new TurnState { ThreadId = r.ThreadId, AgentId = agentId, ChannelId = channelId, Turn = turn, Behalf = r.Behalf, StartedAt = stored[i].Position };
                try { await _runnerHub.Clients.Client(conn).SendAsync("Deliver", msg); }
                catch
                {
                    _turns.TryRemove((r.ThreadId, agentId), out _);
                    _store.Append(new[] { new NewEvent($"thread-{r.ThreadId}", "TurnInterrupted", new { agentId, requeued = true, reason = "the runner couldn't be reached" }, agentId, r.Behalf, turn, stored[i].Position) });
                }
            }
            pendingSandboxes = needSandbox.ToList();
        }
        finally { _lock.Release(); }
        foreach (var (thread, behalf) in pendingSandboxes) await _sandboxes.Ensure(thread, agentId, behalf);
    }

    /// <summary>Where a thread works: outside a project, the agent's own folder (null, null); inside one, the project's first workspace this agent
    /// can reach (a repository anywhere, a folder only on its own machine), or none, with the thread's own branch.</summary>
    internal static (long? Project, ThreadWorkspace? Where) WorkspaceOf(IDecisionContext ctx, long threadId, MemberRow agent, bool forPool = false)
    {
        var t = ctx.Query<(long ChannelId, string? Slug)>("SELECT channel_id, slug FROM threads WHERE root_id=@threadId", new { threadId }).FirstOrDefault();
        var project = ctx.Scalar<long?>("SELECT project_id FROM channels WHERE id=@ch", new { ch = t.ChannelId });
        if (project is null) return (null, null);
        var slug = t.Slug ?? Projects.Slug(ctx.Scalar<string>("SELECT body FROM posts WHERE id=@threadId", new { threadId }) ?? "", threadId);
        var ws = Projects.WorkspaceFor(ctx.Query<WorkspaceRow>("SELECT * FROM workspaces WHERE project_id=@project ORDER BY id", new { project }), forPool ? null : agent.RunnerHost);   // a pool's sandboxes reach repositories, not folders on someone's machine
        var branch = Projects.Branch(slug);
        return (project, ws is null
            ? new ThreadWorkspace(null, "none", "none", null, null, null, branch)
            : new ThreadWorkspace(ws.Id, ws.Name, ws.Kind, ws.Url, ws.Branch, ws.Path, branch));
    }

    public async Task RunnerConnected(long agentId, string conn, string? host)
    {
        _store.Append(new[] { new NewEvent($"agent-{agentId}", "RunnerConnected", new { agentId, host }, agentId) });
        FailInFlight(agentId, "the runner went offline during this turn");     // a turn in flight when the last runner died can't be resumed
        _runners.Set(agentId, conn);                                           // only now can a delivery be sent: it can't be mistaken for one of the old turns
        await _rt.ToAll("bootstrap-changed");
        await BroadcastAgentThreads(agentId);
        await Pump(agentId);
    }

    /// <summary>The runner inside a sandbox connected with its thread token: the thread's queued message can go.</summary>
    public async Task SandboxConnected(long sandboxId, long agentId, long threadId, HubCallerContext context)
    {
        _store.Append(ctx =>
        {
            var s = ctx.Query<SandboxRow>("SELECT * FROM sandboxes WHERE id=@sandboxId", new { sandboxId }).FirstOrDefault();
            return s is null ? Array.Empty<NewEvent>() : new[] { new NewEvent($"thread-{threadId}", "SandboxConnected", new { sandboxId, agentId, postId = s.PostId }, agentId, s.RequestedBy) };
        });
        FailInFlight(agentId, "the sandbox's runner reconnected during this turn", threadId);
        _sandboxConns.Set(agentId, threadId, new SandboxRegistry.Entry(context.ConnectionId, sandboxId, context));
        await BroadcastThread(threadId);
        await Pump(agentId);
    }

    public async Task SandboxDisconnected(long agentId, long threadId, string conn)
    {
        if (!_sandboxConns.Remove(agentId, threadId, conn)) return;
        string handle;
        using (var c = _db.Open()) handle = c.ExecuteScalar<string>("SELECT handle FROM members WHERE id=@agentId", new { agentId }) ?? "the agent";
        FailInFlight(agentId, $"{handle}'s sandbox stopped during this turn", threadId);
        await BroadcastThread(threadId);
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
        _store.Append(new[] { new NewEvent($"agent-{agentId}", "RunnerDisconnected", new { agentId }, agentId) });
        FailInFlight(agentId, "the runner went offline during this turn");
        await _rt.ToAll("bootstrap-changed");
        await BroadcastAgentThreads(agentId);
    }

    /// <summary>Tells a thread's viewers its agents' status changed (online, busy, queued). It isn't an event: it's the runner connection.</summary>
    public async Task BroadcastThread(long rootId)
    {
        object? payload; long ch;
        using (var c = _db.Open())
        {
            var p = Q.Post(c, rootId); if (p is null) return;
            ch = p.ChannelId; payload = _views.ThreadUpdated(c, rootId, null);
        }
        if (payload is not null) await _rt.ToChannel(ch, "thread-updated", payload);
    }

    public List<object> ThreadAgentStatus(long rootId) => _views.ThreadAgentStatus(rootId);

    // ---- turns that ended badly --------------------------------------------

    private void FailInFlight(long agentId, string why, long? onlyThread = null)
    {
        List<InFlight> stuck;
        using (var c = _db.Open())
            stuck = c.Query<InFlight>(@"SELECT o.thread_id, t.channel_id, o.turn, o.behalf, o.cause FROM open_turns o JOIN threads t ON t.root_id=o.thread_id
                WHERE o.agent_id=@agentId AND (@onlyThread IS NULL OR o.thread_id=@onlyThread)", new { agentId, onlyThread }).ToList();
        foreach (var s in stuck)
        {
            _turns.TryGetValue((s.ThreadId, agentId), out var st);
            lock (st?.Gate ?? new object())
            {
                var close = st is null ? null : CloseEvent(st);
                var turn = st?.Turn ?? s.Turn ?? NewTurn();
                _store.Append(ctx =>
                {
                    var evs = new List<NewEvent>();
                    if (close is not null) evs.Add(close);
                    evs.Add(new NewEvent($"thread-{s.ThreadId}", "TurnFailed", new { agentId, channelId = s.ChannelId, postId = ctx.NextId("posts"), reason = why, ended = true }, agentId, s.Behalf, turn, st?.StartedAt ?? s.Cause));
                    return evs;
                });
                if (st is not null) { Retire(st); _turns.TryRemove((s.ThreadId, agentId), out _); }
            }
        }
    }

    /// <summary>At startup: a turn that started and never ended was cut off by a crash or restart. Say so in its thread.</summary>
    public int RecoverInterruptedTurns()
    {
        var n = 0;
        _store.Append(ctx =>
        {
            n = 0;
            var evs = new List<NewEvent>();
            var open = ctx.Query<OpenTurn>(@"SELECT s.stream_id AS StreamId, s.correlation AS Turn, s.actor_id AS AgentId, s.on_behalf_of AS Behalf, s.position AS Position FROM events s
                WHERE s.type='TurnStarted' AND NOT EXISTS (SELECT 1 FROM events x WHERE x.stream_id=s.stream_id AND x.position>s.position AND x.correlation=s.correlation
                    AND (x.type IN ('TurnEnded','TurnInterrupted') OR (x.type='TurnFailed' AND json_extract(x.data,'$.ended')=1)))
                ORDER BY s.position");
            foreach (var t in open)
            {
                var root = long.Parse(t.StreamId[7..]);
                var channelId = ctx.Scalar<long?>("SELECT channel_id FROM threads WHERE root_id=@root", new { root });
                if (channelId is null) continue;                       // the channel was deleted
                var cut = ctx.Query<long>("SELECT id FROM posts WHERE thread_id=@root AND author_id=@a AND streaming=1", new { root, a = t.AgentId }).ToList();
                var reason = cut.Count > 0 ? "The server restarted while this answer was being written, so it was cut off." : "The server restarted during this turn.";
                evs.Add(new NewEvent(t.StreamId, "TurnInterrupted", new { agentId = t.AgentId, channelId, postId = ctx.NextId("posts"), reason, finalizedPostIds = cut },
                    t.AgentId, t.Behalf, t.Turn, t.Position));
                n++;
            }
            return evs;
        });
        return n;
    }
    private sealed class OpenTurn { public string StreamId { get; set; } = ""; public string? Turn { get; set; } public long AgentId { get; set; } public long? Behalf { get; set; } public long Position { get; set; } }

    /// <summary>An agent was removed: its turns in flight end, so recovery doesn't find them open later.</summary>
    public void AbandonAgent(long agentId, string reason)
    {
        _store.Append(ctx =>
        {
            var evs = new List<NewEvent>();
            foreach (var s in ctx.Query<InFlight>("SELECT o.thread_id, 0 AS channel_id, o.turn, o.behalf, o.cause FROM open_turns o WHERE o.agent_id=@agentId", new { agentId }))
                evs.Add(new NewEvent($"thread-{s.ThreadId}", "TurnInterrupted", new { agentId, reason }, agentId, s.Behalf, s.Turn ?? NewTurn(), s.Cause));
            return evs;
        });
        foreach (var k in _turns.Keys.Where(k => k.Item2 == agentId).ToList())
            if (_turns.TryRemove(k, out var st)) lock (st.Gate) Retire(st);
    }

    // ---- runner → server -------------------------------------------------

    private TurnState? GetTurn(long threadId, long agentId)
    {
        if (_turns.TryGetValue((threadId, agentId), out var existing)) return existing;
        using var c = _db.Open();
        var ch = c.QueryFirstOrDefault<long?>("SELECT channel_id FROM threads WHERE root_id=@threadId", new { threadId });
        if (ch is null) return null;
        var d = c.QueryFirstOrDefault<InFlight>("SELECT o.thread_id, 0 AS channel_id, o.turn, o.behalf, o.cause FROM open_turns o WHERE o.thread_id=@threadId AND o.agent_id=@agentId", new { threadId, agentId });
        return _turns.GetOrAdd((threadId, agentId), _ => new TurnState { ThreadId = threadId, AgentId = agentId, ChannelId = ch.Value, Turn = d?.Turn ?? NewTurn(), Behalf = d?.Behalf, StartedAt = d?.Cause });
    }

    private NewEvent Ev(TurnState st, string type, object data) => new($"thread-{st.ThreadId}", type, data, st.AgentId, st.Behalf, st.Turn, st.StartedAt);

    /// <summary>The event that ends the text run being written (the last segment), or null if none is open. Apply with <see cref="Retire"/> or <see cref="RunClosed"/>.</summary>
    private NewEvent? CloseEvent(TurnState st) => st.PostId is null ? null
        : Ev(st, "AgentReplied", new { postId = st.PostId, channelId = st.ChannelId, text = st.Pending.ToString(), segment = st.Segment, complete = true });

    private void RunClosed(TurnState st)
    {
        st.PostId = null; st.Pending.Clear(); st.Segment = 0; st.RunLen = 0;
        st.Timer?.Dispose(); st.Timer = null;
    }
    private void Retire(TurnState st) { RunClosed(st); st.Closed = true; }

    private void FlushLocked(TurnState st)
    {
        if (st.PostId is null || st.Pending.Length == 0 || st.Closed) return;
        _store.Append(new[] { Ev(st, "AgentReplied", new { postId = st.PostId, channelId = st.ChannelId, text = st.Pending.ToString(), segment = st.Segment, complete = false }) });
        st.Pending.Clear(); st.Segment++; st.LastFlush = Environment.TickCount64;
    }

    private void ArmFlush(TurnState st)
    {
        if (st.Timer is not null) return;
        var wait = (int)Math.Max(0, st.LastFlush + FlushIntervalMs - Environment.TickCount64);
        st.Timer = new Timer(_ => { lock (st.Gate) { st.Timer?.Dispose(); st.Timer = null; FlushLocked(st); } }, null, wait, Timeout.Infinite);
    }

    public async Task OnStream(long agentId, StreamEvent e)
    {
        var st = GetTurn(e.ThreadId, agentId);
        if (st is null) return;
        switch (e.Kind)
        {
            case StreamKinds.Text when !string.IsNullOrEmpty(e.Text):
                lock (st.Gate)
                {
                    var offset = st.RunLen;
                    st.Said.Append(e.Text); st.RunLen += e.Text.Length;
                    if (st.PostId is null)
                    {
                        // the first words are stored at once, so the post exists for everyone who catches up
                        long pid = 0;
                        _store.Append(ctx => { pid = ctx.NextId("posts"); return new[] { Ev(st, "AgentReplied", new { postId = pid, channelId = st.ChannelId, text = e.Text, segment = 0, complete = false }) }; });
                        st.PostId = pid; st.Segment = 1; st.LastFlush = Environment.TickCount64;
                    }
                    else
                    {
                        st.Pending.Append(e.Text);
                        _live.Delta(st.ChannelId, new { id = st.PostId, threadId = e.ThreadId, text = e.Text, offset });       // live only: deltas aren't stored
                        if (Environment.TickCount64 - st.LastFlush >= FlushIntervalMs) FlushLocked(st); else ArmFlush(st);
                    }
                }
                break;
            case StreamKinds.ToolStart:
                lock (st.Gate)
                {
                    var close = CloseEvent(st);
                    long pid = 0;
                    _store.Append(ctx =>
                    {
                        pid = ctx.NextId("posts");
                        var evs = new List<NewEvent>();
                        if (close is not null) evs.Add(close);
                        evs.Add(Ev(st, "ToolCallStarted", new { postId = pid, channelId = st.ChannelId, toolId = e.ToolId, title = e.Title ?? "tool", toolKind = e.ToolKind, status = e.Status ?? "running" }));
                        return evs;
                    });
                    RunClosed(st);
                    if (e.ToolId is not null) st.Tools[e.ToolId] = pid;
                }
                break;
            case StreamKinds.ToolEnd or StreamKinds.ToolUpdate when e.ToolId is not null && st.Tools.TryGetValue(e.ToolId, out var tp):
                lock (st.Gate)
                {
                    if (e.Kind == StreamKinds.ToolUpdate)
                        _store.Append(new[] { Ev(st, "ToolCallDetailed", new { postId = tp, toolId = e.ToolId, title = e.Title }) });
                    else
                    {
                        string? output = null; var truncated = false;
                        if (RecordsOutput(agentId) && e.Output is not null) (output, truncated) = Cap(e.Output);
                        _store.Append(new[] { Ev(st, "ToolCallFinished", new { postId = tp, toolId = e.ToolId, status = e.Status ?? "completed", title = e.Title, outputBytes = e.OutputBytes, outputSha256 = e.OutputSha256, output, outputTruncated = truncated }) });
                    }
                }
                break;
            case StreamKinds.Workspace:
                // the runner says where it prepared the thread's work: a worktree on its own branch, a folder, a scratch folder or the agent's own folder
                lock (st.Gate)
                {
                    string? host; var sandboxed = false;
                    using (var c = _db.Open())
                    {
                        host = c.ExecuteScalar<string?>("SELECT runner_host FROM agents WHERE member_id=@agentId", new { agentId });
                        if (c.ExecuteScalar<string?>("SELECT p.name FROM sandboxes s JOIN pools p ON p.id=s.pool_id WHERE s.thread_id=@t AND s.agent_id=@agentId", new { t = e.ThreadId, agentId }) is { } pool)
                        { host = pool; sandboxed = true; }      // a template's sandbox: the place is in a container on the pool
                    }
                    _store.Append(new[] { Ev(st, "ThreadWorkspaceReady", new { agentId, workspaceId = e.WorkspaceId, place = e.Status, branch = e.Title, location = e.Text, host, sandboxed }) });
                }
                break;
            case StreamKinds.Error:
                lock (st.Gate)
                {
                    var close = CloseEvent(st);
                    _store.Append(ctx =>
                    {
                        var evs = new List<NewEvent>();
                        if (close is not null) evs.Add(close);
                        evs.Add(Ev(st, "TurnFailed", new { agentId, channelId = st.ChannelId, postId = ctx.NextId("posts"), reason = e.Text ?? "something went wrong", ended = false }));
                        return evs;
                    });
                    RunClosed(st);
                }
                break;
            case StreamKinds.TurnEnded:
                string said; long endedAt;
                lock (st.Gate)
                {
                    var close = CloseEvent(st);
                    var evs = new List<NewEvent>(); if (close is not null) evs.Add(close);
                    evs.Add(Ev(st, "TurnEnded", new { agentId }));
                    endedAt = _store.Append(evs)[^1].Position;
                    said = st.Said.ToString();
                    Retire(st); _turns.TryRemove(new KeyValuePair<(long, long), TurnState>((e.ThreadId, agentId), st));
                }
                await RouteAgentMentions(e.ThreadId, agentId, st.ChannelId, said, st.Behalf, endedAt);
                await Pump(agentId);
                break;
        }
    }

    private bool RecordsOutput(long agentId)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("SELECT record_tool_output FROM agents WHERE member_id=@agentId", new { agentId }) == 1;
    }

    /// <summary>At most 64 kB of a tool's output, cut on a character boundary.</summary>
    public static (string Text, bool Truncated) Cap(string s)
    {
        if (Encoding.UTF8.GetByteCount(s) <= MaxToolOutputBytes) return (s, false);
        var n = 0; var sb = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
        {
            n += r.Utf8SequenceLength;
            if (n > MaxToolOutputBytes) break;
            sb.Append(r.ToString());
        }
        return (sb.ToString(), true);
    }

    /// <summary>An agent that @mentions another agent in a thread starts a turn for it, up to 3 in a row without a person.</summary>
    private async Task RouteAgentMentions(long threadId, long agentId, long channelId, string said, long? behalf, long cause)
    {
        if (string.IsNullOrWhiteSpace(said) || !Mentions(said).Any()) return;
        var pump = new List<long>();
        await _lock.WaitAsync();
        try
        {
            _store.Append(ctx =>
            {
                pump.Clear();
                var evs = new List<NewEvent>();
                var author = ctx.Query<MemberRow>(Q.MemberSelect + " WHERE m.id=@agentId", new { agentId }).First();
                var channel = ctx.Query<ChannelRow>("SELECT * FROM channels WHERE id=@channelId", new { channelId }).First();
                var lastPost = ctx.Scalar<long?>("SELECT MAX(id) FROM posts WHERE thread_id=@threadId AND author_id=@agentId AND kind='text' AND deleted=0", new { threadId, agentId }) ?? 0;
                var wanted = Mentions(said).ToHashSet();
                var targets = ctx.Query<MemberRow>(Q.MemberSelect + @" WHERE m.kind='agent' AND m.removed=0 AND " + Q.CanWork + @"
                    AND m.id IN (SELECT member_id FROM channel_audience WHERE channel_id=@channelId)", new { channelId })
                    .Where(a => a.Id != agentId && wanted.Contains(a.Handle.ToLowerInvariant()));
                var chain = ctx.Scalar<int>("SELECT agent_chain FROM threads WHERE root_id=@threadId", new { threadId });
                foreach (var a in targets)
                {
                    if (chain >= 3) break;      // AGENTS.md rule 6: limit loops
                    if (Refusal(ctx, channel, a, behalf) is { } grant)      // an agent asking for someone: the grant is checked against that person
                    {
                        evs.Add(new NewEvent($"thread-{threadId}", "WorkRefused", new { agentId = a.Id, ownerId = a.OwnerId, channelId, projectId = channel.ProjectId, postId = ctx.NextId("posts"), askedIn = lastPost,
                            grant, reply = RefusalReply(ctx, channel, a, behalf), askedBy = agentId }, a.Id, behalf, Causation: cause));
                        continue;
                    }
                    chain++;
                    var turn = NewTurn();
                    if (ctx.Scalar<int>("SELECT COUNT(*) FROM thread_agents WHERE thread_id=@threadId AND agent_id=@a", new { threadId, a = a.Id }) == 0)
                        evs.Add(new NewEvent($"thread-{threadId}", "AgentJoinedThread", new { agentId = a.Id, ownerId = a.OwnerId }, null, behalf, turn, cause));
                    evs.Add(new NewEvent($"thread-{threadId}", "MessageDelivered", new
                    {
                        agentId = a.Id, postId = lastPost, chained = true,
                        text = Wrap(author.Handle, author.DisplayName, false, channel.Name, threadId, said),
                    }, null, behalf, turn, cause));
                    pump.Add(a.Id);
                }
                return evs;
            });
        }
        finally { _lock.Release(); }
        foreach (var a in pump) await Pump(a);
    }

    /// <summary>The runner asks permission; only the owner gets buttons (the client decides what to show from ownerId).</summary>
    public Task OnPermission(long agentId, PermissionRequestMsg r)
    {
        var st = GetTurn(r.ThreadId, agentId);
        if (st is null) return Task.CompletedTask;
        lock (st.Gate)
            _store.Append(ctx =>
            {
                var owner = ctx.Scalar<long>("SELECT owner_id FROM agents WHERE member_id=@agentId", new { agentId });
                var (answerer, leads) = Answerers(ctx, agentId, r.ThreadId, owner);
                return new[] { Ev(st, "PermissionRequested", new { postId = ctx.NextId("posts"), channelId = st.ChannelId, requestId = r.RequestId, agentId, ownerId = answerer, agentOwnerId = owner,
                    leadIds = leads, leadAfter = leads.Length == 0 ? (long?)null : Db.Now() + LeadAfterMs, title = r.Title, options = r.Options }) };
            });
        return Task.CompletedTask;
    }

    /// <summary>How long the person who started a template's thread has to answer before the project's leads may (decision 7).</summary>
    public static long LeadAfterMs = 30 * 60_000;

    /// <summary>Who answers an agent's asks: a pinned agent's owner; a template's in a project, whoever started the thread, and its leads as a fallback.</summary>
    private static (long Answerer, long[] Leads) Answerers(IDecisionContext ctx, long agentId, long threadId, long owner)
    {
        if (ctx.Scalar<string?>("SELECT placement FROM agents WHERE member_id=@agentId", new { agentId }) != "pool") return (owner, Array.Empty<long>());
        var started = ctx.Scalar<long?>("SELECT started_by FROM threads WHERE root_id=@threadId", new { threadId }) ?? owner;
        var leads = ctx.Query<long>(@"SELECT pm.member_id FROM project_members pm JOIN channels ch ON ch.project_id=pm.project_id JOIN threads t ON t.channel_id=ch.id
            WHERE t.root_id=@threadId AND pm.role='lead' AND pm.member_id<>@started ORDER BY pm.member_id", new { threadId, started }).ToArray();
        return (started, leads);
    }

    /// <summary>The owner's answer to an ask card. Returns false if the caller may not answer.</summary>
    public async Task<(bool ok, string? error)> Answer(long postId, long callerId, string? optionId)
    {
        string? error = null; string requestId = ""; long agentId = 0;
        _store.Append(ctx =>
        {
            error = null;
            var p = ctx.Query<PostRow>("SELECT * FROM posts WHERE id=@postId", new { postId }).FirstOrDefault();
            if (p is null || p.Kind != "ask" || p.Meta is null) { error = "not found"; return Array.Empty<NewEvent>(); }
            var meta = System.Text.Json.Nodes.JsonNode.Parse(p.Meta)!.AsObject();
            var lead = meta["leadIds"] is System.Text.Json.Nodes.JsonArray ls && ls.Any(l => (long?)l == callerId) && (long?)meta["leadAfter"] <= Db.Now();
            if ((long)meta["ownerId"]! != callerId && !lead) { error = "forbidden"; return Array.Empty<NewEvent>(); }
            if ((string?)meta["status"] != "pending") { error = "already answered"; return Array.Empty<NewEvent>(); }
            var canSee = ctx.Scalar<int>("SELECT COUNT(*) FROM channels ch WHERE ch.id=@ch AND " + Access.SeesChannel, new { ch = p.ChannelId, m = callerId });
            if (canSee == 0) { error = "forbidden"; return Array.Empty<NewEvent>(); }
            agentId = (long)meta["agentId"]!; requestId = (string)meta["requestId"]!;
            var req = ctx.Query<(long Position, string? Correlation, long? Behalf)>(
                "SELECT position, correlation, on_behalf_of FROM events WHERE stream_id=@s AND type='PermissionRequested' AND json_extract(data,'$.postId')=@postId", new { s = $"thread-{p.ThreadId}", postId }).FirstOrDefault();
            return new[] { new NewEvent($"thread-{p.ThreadId}", optionId is null ? "PermissionDenied" : "PermissionGranted", new { postId, requestId, agentId, optionId }, callerId, req.Behalf, req.Correlation, req.Position == 0 ? null : req.Position) };
        });
        if (error is not null) return (false, error);
        string? conn;
        using (var c = _db.Open())
        {
            var agent = Q.Member(c, agentId);
            var thread = c.ExecuteScalar<long>("SELECT thread_id FROM posts WHERE id=@postId", new { postId });
            conn = agent is null ? null : ConnectionFor(agent, thread);
        }
        if (conn is not null)
            await _runnerHub.Clients.Client(conn).SendAsync("PermissionAnswer", new PermissionAnswerMsg(requestId, optionId));
        return (true, null);
    }
}
