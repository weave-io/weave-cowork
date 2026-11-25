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

    public Dispatcher(Db db, IEventStore store, Realtime rt, RunnerRegistry runners, IHubContext<RunnerHub> runnerHub, LiveQueue live, ThreadViews views)
    { _db = db; _store = store; _rt = rt; _runners = runners; _runnerHub = runnerHub; _live = live; _views = views; }

    private static readonly Regex MentionRx = new(@"(?<![\w@])@([a-zA-Z0-9][a-zA-Z0-9_-]*)", RegexOptions.Compiled);
    private static string NewTurn() => "t-" + Guid.NewGuid().ToString("N")[..12];

    public static IEnumerable<string> Mentions(string body) => MentionRx.Matches(body).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct();

    /// <summary>AGENTS.md rule 4: say who's speaking.</summary>
    public static string Wrap(string handle, string name, bool owner, string channel, long threadId, string text) =>
        $"<cowork-message from=\"@{Esc(handle)}\" name=\"{Esc(name)}\" owner=\"{(owner ? "true" : "false")}\" channel=\"#{Esc(channel)}\" thread=\"{threadId}\">{text.Replace("</cowork-message", "&lt;/cowork-message")}</cowork-message>";
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");

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
                var mentioned = ctx.Query<MemberRow>(Q.MemberSelect + @" WHERE m.kind='agent' AND m.removed=0 AND rs.token_hash IS NOT NULL
                    AND m.id IN (SELECT member_id FROM channel_members WHERE channel_id=@channelId)", new { channelId })
                    .Where(a => a.Id != author.Id && mentions.Contains(a.Handle.ToLowerInvariant())).ToList();
                var posted = new { postId, channelId, threadId, body = body, mentions };
                long root;
                int causeIdx;
                if (threadId is null)
                {
                    evs.Add(new NewEvent($"channel-{channelId}", "MessagePosted", posted, authorId));
                    if (mentioned.Count == 0) return evs;
                    root = postId; causeIdx = 0;
                    evs.Add(new NewEvent($"thread-{root}", "ThreadStarted", new { rootId = root, channelId, startedBy = authorId }, null, authorId, CausedByIndex: 0));
                }
                else
                {
                    root = threadId.Value;
                    if (ctx.Scalar<int>("SELECT COUNT(*) FROM threads WHERE root_id=@root", new { root }) == 0)
                        evs.Add(new NewEvent($"thread-{root}", "ThreadStarted", new { rootId = root, channelId, startedBy = authorId }, null, authorId));
                    causeIdx = evs.Count;
                    evs.Add(new NewEvent($"thread-{root}", "MessagePosted", posted, authorId));
                }

                var targets = new Dictionary<long, MemberRow>();
                if (threadId is not null)
                    foreach (var a in ctx.Query<MemberRow>(Q.MemberSelect + @" JOIN thread_agents ta ON ta.agent_id = m.id AND ta.thread_id=@root
                        WHERE m.removed=0 AND rs.token_hash IS NOT NULL AND m.id IN (SELECT member_id FROM channel_members WHERE channel_id=@channelId)", new { root, channelId }))
                        targets[a.Id] = a;
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
        await _lock.WaitAsync();
        try
        {
            var conn = _runners.ConnectionOf(agentId);
            if (conn is null) return;
            var sends = new List<(Deliveries.Row Row, string Turn, DeliverMessage Msg)>();
            var stored = _store.Append(ctx =>
            {
                sends.Clear();
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
                    var turn = r.Turn ?? NewTurn();
                    evs.Add(new NewEvent($"thread-{r.ThreadId}", "TurnStarted", new { agentId, deliveryId = r.Position, host = agent.RunnerHost }, agentId, r.Behalf, turn, r.Position));
                    sends.Add((r, turn, new DeliverMessage(r.Position, r.ThreadId, agentId, agent.Handle, agent.Instructions ?? "",
                        JsonSerializer.Deserialize<string[]>(agent.HarnessCommand!)!, agent.Workspace!, r.Text, record)));
                }
                return evs;
            });
            for (var i = 0; i < sends.Count; i++)
            {
                var (r, turn, msg) = sends[i];
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
        }
        finally { _lock.Release(); }
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

    private void FailInFlight(long agentId, string why)
    {
        List<InFlight> stuck;
        using (var c = _db.Open())
            stuck = c.Query<InFlight>(@"SELECT o.thread_id, t.channel_id, o.turn, o.behalf, o.cause FROM open_turns o JOIN threads t ON t.root_id=o.thread_id
                WHERE o.agent_id=@agentId", new { agentId }).ToList();
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
                var targets = ctx.Query<MemberRow>(Q.MemberSelect + @" WHERE m.kind='agent' AND m.removed=0 AND rs.token_hash IS NOT NULL
                    AND m.id IN (SELECT member_id FROM channel_members WHERE channel_id=@channelId)", new { channelId })
                    .Where(a => a.Id != agentId && wanted.Contains(a.Handle.ToLowerInvariant()));
                var chain = ctx.Scalar<int>("SELECT agent_chain FROM threads WHERE root_id=@threadId", new { threadId });
                foreach (var a in targets)
                {
                    if (chain >= 3) break;      // AGENTS.md rule 6: limit loops
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
                return new[] { Ev(st, "PermissionRequested", new { postId = ctx.NextId("posts"), channelId = st.ChannelId, requestId = r.RequestId, agentId, ownerId = owner, title = r.Title, options = r.Options }) };
            });
        return Task.CompletedTask;
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
            if ((long)meta["ownerId"]! != callerId) { error = "forbidden"; return Array.Empty<NewEvent>(); }
            if ((string?)meta["status"] != "pending") { error = "already answered"; return Array.Empty<NewEvent>(); }
            var canSee = ctx.Scalar<int>(@"SELECT COUNT(*) FROM channels ch WHERE ch.id=@ch AND (ch.is_private=0 OR ch.id IN (SELECT channel_id FROM channel_members WHERE member_id=@callerId))
                AND EXISTS (SELECT 1 FROM people pe JOIN members m ON m.id=pe.member_id WHERE pe.member_id=@callerId AND m.removed=0)", new { ch = p.ChannelId, callerId });
            if (canSee == 0) { error = "forbidden"; return Array.Empty<NewEvent>(); }
            agentId = (long)meta["agentId"]!; requestId = (string)meta["requestId"]!;
            var req = ctx.Query<(long Position, string? Correlation, long? Behalf)>(
                "SELECT position, correlation, on_behalf_of FROM events WHERE stream_id=@s AND type='PermissionRequested' AND json_extract(data,'$.postId')=@postId", new { s = $"thread-{p.ThreadId}", postId }).FirstOrDefault();
            return new[] { new NewEvent($"thread-{p.ThreadId}", optionId is null ? "PermissionDenied" : "PermissionGranted", new { postId, requestId, agentId, optionId }, callerId, req.Behalf, req.Correlation, req.Position == 0 ? null : req.Position) };
        });
        if (error is not null) return (false, error);
        var conn = _runners.ConnectionOf(agentId);
        if (conn is not null)
            await _runnerHub.Clients.Client(conn).SendAsync("PermissionAnswer", new PermissionAnswerMsg(requestId, optionId));
        return (true, null);
    }
}
