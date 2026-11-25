using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

public abstract record LiveItem;
public sealed record LiveEvents(IReadOnlyList<StoredEvent> Events) : LiveItem;
public sealed record LiveDelta(long ChannelId, object Payload) : LiveItem;

/// <summary>
/// One ordered line to the browsers: every committed event batch and every streamed text delta goes through it,
/// so a delta never overtakes the post it belongs to.
/// </summary>
public sealed class LiveQueue
{
    private readonly Channel<LiveItem> _ch = Channel.CreateUnbounded<LiveItem>(new UnboundedChannelOptions { SingleReader = true });
    public ChannelReader<LiveItem> Reader => _ch.Reader;
    public LiveQueue(IEventStore store) { store.Subscribe(batch => _ch.Writer.TryWrite(new LiveEvents(batch))); }
    public void Delta(long channelId, object payload) => _ch.Writer.TryWrite(new LiveDelta(channelId, payload));
}

public enum EffectKind { PostCreated, PostUpdated, ThreadUpdated, PostDeleted }

/// <summary>What a screen has to change because of an event: a post appears, a post changes, a thread's header changes.</summary>
public sealed record Effect(long ChannelId, EffectKind Kind, long Id, long Position);

public static class Effects
{
    public static List<Effect> From(SqliteConnection c, IEnumerable<StoredEvent> events)
    {
        var list = new List<Effect>();
        long? ChannelOfThread(long t) => c.ExecuteScalar<long?>("SELECT channel_id FROM threads WHERE root_id=@t", new { t });
        long? ChannelOfPost(long p) => c.ExecuteScalar<long?>("SELECT channel_id FROM posts WHERE id=@p AND deleted=0", new { p });
        void Post(EffectKind kind, long id, long pos) { if (ChannelOfPost(id) is { } ch) list.Add(new Effect(ch, kind, id, pos)); }
        void Thread(long root, long pos) { if (root != 0 && ChannelOfThread(root) is { } ch) list.Add(new Effect(ch, EffectKind.ThreadUpdated, root, pos)); }
        foreach (var e in events)
        {
            var t = ThreadProjection.ThreadOf(e);
            switch (e.Type)
            {
                case "MessagePosted":
                    Post(EffectKind.PostCreated, e.Long("postId"), e.Position);
                    if (e.LongOrNull("threadId") is { } root) Thread(root, e.Position);
                    break;
                case "ThreadStarted": Thread(e.Long("rootId"), e.Position); break;
                case "MessageDelivered": case "TurnStarted": case "TurnEnded": case "ThreadWorkspaceReady": Thread(t, e.Position); break;
                case "WorkRefused": Post(EffectKind.PostCreated, e.Long("postId"), e.Position); Thread(t, e.Position); break;
                case "ToolCallStarted": case "PermissionRequested":
                    Post(EffectKind.PostCreated, e.Long("postId"), e.Position); Thread(t, e.Position); break;
                case "MessageDeleted":
                    list.Add(new Effect(e.Long("channelId"), EffectKind.PostDeleted, e.Long("postId"), e.Position));
                    if (e.LongOrNull("threadId") is { } inThread) Thread(inThread, e.Position);        // the thread's reply count changed
                    break;
                case "ToolCallDetailed": case "ToolCallFinished": case "PermissionGranted": case "PermissionDenied": case "MessageRedacted":
                    Post(EffectKind.PostUpdated, e.Long("postId"), e.Position); break;
                case "AgentReplied":
                    if (e.Long("segment") == 0) { Post(EffectKind.PostCreated, e.Long("postId"), e.Position); Thread(t, e.Position); }
                    else Post(EffectKind.PostUpdated, e.Long("postId"), e.Position);
                    break;
                case "TurnFailed":
                    Post(EffectKind.PostCreated, e.Long("postId"), e.Position); Thread(t, e.Position); break;
                // a sandbox's starting card appears, then its rows change; the session line follows its state
                case "SandboxRequested":
                    Post(EffectKind.PostCreated, e.Long("postId"), e.Position); Thread(t, e.Position); break;
                case "SandboxPlaced":
                    if (e.LongOrNull("postId") is { } placed) Post(EffectKind.PostUpdated, placed, e.Position);
                    foreach (var w in c.Query<long>("SELECT post_id FROM sandboxes WHERE state='waiting' AND post_id IS NOT NULL")) Post(EffectKind.PostUpdated, w, e.Position);     // the queue moved up
                    Thread(t, e.Position); break;
                case "SandboxStarted": case "SandboxResumed": case "SandboxConnected": case "SandboxSuspending": case "SandboxSuspended": case "SandboxFailed": case "SandboxDeleted":
                    if (e.LongOrNull("postId") is { } card) Post(EffectKind.PostUpdated, card, e.Position);
                    Thread(t, e.Position); break;
                case "TurnInterrupted":
                    if (e.LongOrNull("postId") is { } notice) Post(EffectKind.PostCreated, notice, e.Position);
                    if (e.Data.TryGetProperty("finalizedPostIds", out var ids)) foreach (var id in ids.EnumerateArray()) Post(EffectKind.PostUpdated, id.GetInt64(), e.Position);
                    Thread(t, e.Position);
                    break;
            }
        }
        return list;
    }
}

/// <summary>The messages browsers get, built from the read tables. `at` is the position of the event that caused the message.</summary>
public sealed class ThreadViews
{
    private readonly Db _db;
    private readonly RunnerRegistry _runners;
    private readonly SandboxRegistry _sandboxes;
    public ThreadViews(Db db, RunnerRegistry runners, SandboxRegistry sandboxes) { _db = db; _runners = runners; _sandboxes = sandboxes; }

    /// <summary>The post as the app shows it, or null if it is gone (deleted since).</summary>
    public static JsonObject? PostPayload(SqliteConnection c, long id, long? at)
    {
        if (Q.Post(c, id) is null) return null;
        var node = JsonSerializer.SerializeToNode(Q.FullPostDto(c, id))!.AsObject();
        node["at"] = at;
        return node;
    }

    public object? ThreadUpdated(SqliteConnection c, long rootId, long? at)
    {
        var p = Q.Post(c, rootId);
        if (p is null) return null;
        return new { rootId, channelId = p.ChannelId, post = Q.FullPostDto(c, rootId), agents = ThreadAgentStatus(rootId), at };
    }

    public List<object> ThreadAgentStatus(long rootId)
    {
        using var c = _db.Open();
        return c.Query<MemberRow>(Q.MemberSelect + " JOIN thread_agents ta ON ta.agent_id=m.id AND ta.thread_id=@rootId WHERE m.removed=0", new { rootId })
            .Select(a => (object)new
            {
                id = a.Id, handle = a.Handle, displayName = a.DisplayName, online = a.IsTemplate ? _sandboxes.ConnectionOf(a.Id, rootId) is not null : _runners.IsOnline(a.Id),
                placement = a.Placement,
                // a template's sandbox for this thread: where it is and what state it's in
                sandbox = a.IsTemplate ? c.QueryFirstOrDefault<ThreadSandbox>("SELECT s.state, p.name AS pool, s.starts FROM sandboxes s LEFT JOIN pools p ON p.id=s.pool_id WHERE s.thread_id=@rootId AND s.agent_id=@id", new { rootId, id = a.Id }) : null,
                busy = c.ExecuteScalar<int>("SELECT COUNT(*) FROM open_turns WHERE thread_id=@rootId AND agent_id=@id", new { rootId, id = a.Id }) > 0,
                queued = c.ExecuteScalar<int>(Deliveries.QueuedIn, new { agentId = a.Id, stream = $"thread-{rootId}" }),
                harnessCommand = JsonSerializer.Deserialize<string[]>(a.HarnessCommand!), runnerHost = a.RunnerHost, ownerId = a.OwnerId,
                // where this thread's work happens, as the runner reported it (null until its first turn there)
                workspace = c.QueryFirstOrDefault<ThreadPlace>("SELECT place, branch, host, sandboxed FROM thread_agents WHERE thread_id=@rootId AND agent_id=@id AND place IS NOT NULL", new { rootId, id = a.Id }),
            }).ToList();
    }
}

public sealed class ThreadSandbox { public string State { get; set; } = ""; public string? Pool { get; set; } public int Starts { get; set; } }

public sealed class ThreadPlace { public string? Place { get; set; } public string? Branch { get; set; } public string? Host { get; set; } public bool Sandboxed { get; set; } }

/// <summary>Turns committed events into the SignalR messages the app listens for, each carrying its position.</summary>
public sealed class LivePublisher : BackgroundService
{
    private readonly LiveQueue _queue;
    private readonly Db _db;
    private readonly Realtime _rt;
    private readonly ThreadViews _views;
    private readonly ILogger<LivePublisher> _log;
    public LivePublisher(LiveQueue queue, Db db, Realtime rt, ThreadViews views, ILogger<LivePublisher> log) { _queue = queue; _db = db; _rt = rt; _views = views; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (item is LiveDelta d) { await _rt.ToChannel(d.ChannelId, "post-delta", d.Payload); continue; }
                var events = ((LiveEvents)item).Events;
                var sends = new List<(long Channel, string Name, object Payload)>();
                using (var c = _db.Open())
                {
                    var effects = Effects.From(c, events);
                    // one header update per thread per commit; posts in the order they happened
                    var lastThread = effects.Where(e => e.Kind == EffectKind.ThreadUpdated).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Last());
                    foreach (var ef in effects)
                    {
                        if (ef.Kind == EffectKind.ThreadUpdated)
                        {
                            if (!ReferenceEquals(lastThread[ef.Id], ef)) continue;
                            if (_views.ThreadUpdated(c, ef.Id, ef.Position) is { } p) sends.Add((ef.ChannelId, "thread-updated", p));
                        }
                        else if (ef.Kind == EffectKind.PostDeleted) sends.Add((ef.ChannelId, "post-deleted", new { id = ef.Id, channelId = ef.ChannelId, at = ef.Position }));
                        else if (ThreadViews.PostPayload(c, ef.Id, ef.Position) is { } payload) sends.Add((ef.ChannelId, ef.Kind == EffectKind.PostCreated ? "post-created" : "post-updated", payload));
                    }
                }
                foreach (var (ch, name, payload) in sends) await _rt.ToChannel(ch, name, payload);
            }
            catch (Exception e) { _log.LogWarning(e, "couldn't push live events"); }
        }
    }
}
