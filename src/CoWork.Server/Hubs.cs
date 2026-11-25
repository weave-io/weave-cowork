using System.Security.Claims;
using CoWork.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CoWork.Server;

/// <summary>Browsers. A connection hears a channel only if its person may see it.</summary>
[Authorize]
public sealed class AppHub : Hub
{
    private readonly Realtime _rt;
    private readonly Db _db;
    private readonly VoicePresence _voice;
    private readonly VoiceOptions _vo;
    private readonly IEventStore _store;
    public AppHub(Realtime rt, Db db, VoicePresence voice, VoiceOptions vo, IEventStore store) { _rt = rt; _db = db; _voice = voice; _vo = vo; _store = store; }

    /// <summary>The most events one catch-up replays; further behind than that, the browser reloads what it shows.</summary>
    public const int MaxCatchUp = 5000;

    /// <summary>A browser that reconnects says the last position it saw and gets what it missed, for the channels it may see.
    /// Each message is what the app already applies live (a post to add or replace, a thread header), with the position it came from.</summary>
    public object CatchUp(long after)
    {
        var me = MemberId(Context.User!);
        var head = _store.Head();
        if (after > head) return new { head, truncated = true, messages = Array.Empty<object>() };       // this server is behind the browser: start again
        var events = _store.ReadAll(after, MaxCatchUp + 1);
        if (events.Count > MaxCatchUp) return new { head, truncated = true, messages = Array.Empty<object>() };
        using var c = _db.Open();
        var seen = new Dictionary<long, bool>();
        bool Can(long ch) => seen.TryGetValue(ch, out var v) ? v : seen[ch] = Access.CanSee(c, ch, me);     // checked for every channel an event belongs to
        var posts = new Dictionary<long, Effect>(); var threads = new Dictionary<long, Effect>();
        foreach (var ef in Effects.From(c, events).Where(e => Can(e.ChannelId)))
            (ef.Kind == EffectKind.ThreadUpdated ? threads : posts)[ef.Id] = ef;
        var views = _rt.Services.GetRequiredService<ThreadViews>();
        // posts oldest first (the order they were created in), then the thread headers
        var messages = posts.Values.OrderBy(e => e.Id).Select(e => (object)new { name = "post-upsert", payload = (object)ThreadViews.PostPayload(c, e.Id, e.Position) })
            .Concat(threads.Values.OrderBy(e => e.Position).Select(e => (object)new { name = "thread-updated", payload = views.ThreadUpdated(c, e.Id, e.Position)! }))
            .ToList();
        return new { head = events.Count == 0 ? head : events[^1].Position, truncated = false, messages };
    }

    public static long MemberId(ClaimsPrincipal u) => long.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public override async Task OnConnectedAsync()
    {
        await _rt.Connected(MemberId(Context.User!), Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _rt.Disconnected(MemberId(Context.User!), Context.ConnectionId);
        await Announce(_voice.Leave(Context.ConnectionId));
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>The browser says it joined a voice channel's room. Refused unless the caller may see a voice channel on a server with voice.</summary>
    public async Task JoinVoice(long channelId)
    {
        var me = MemberId(Context.User!);
        using (var c = _db.Open())
        {
            if (!_vo.Enabled) throw new HubException("Voice isn't set up on this server.");
            if (!Access.CanSee(c, channelId, me) || Q.Channel(c, channelId)!.Kind != "voice") throw new HubException("You can't join that channel.");
        }
        await Announce(_voice.Join(Context.ConnectionId, channelId, me));
    }

    public async Task LeaveVoice() => await Announce(_voice.Leave(Context.ConnectionId));

    private Task Announce(IEnumerable<long> channels) =>
        Task.WhenAll(channels.Select(ch => _rt.ToChannel(ch, "voice-presence", new { channelId = ch, memberIds = _voice.Members(ch) })));

    /// <summary>Ask to hear a channel. Refused unless the caller may see it.</summary>
    public async Task Subscribe(long channelId)
    {
        using var c = _db.Open();
        if (!Access.CanSee(c, channelId, MemberId(Context.User!))) throw new HubException("You can't see that channel.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Realtime.ChannelGroup(channelId));
    }
}

/// <summary>Runners. The token was checked in middleware; the agent id is in the connection's items.</summary>
public sealed class RunnerHub : Hub
{
    private readonly Dispatcher _d;
    public RunnerHub(Dispatcher d) { _d = d; }
    private long AgentId => (long)Context.GetHttpContext()!.Items["agentId"]!;

    public override async Task OnConnectedAsync()
    {
        var host = Context.GetHttpContext()!.Request.Headers["X-Cowork-Host"].ToString();
        await _d.RunnerConnected(AgentId, Context.ConnectionId, string.IsNullOrWhiteSpace(host) ? null : host);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _d.RunnerDisconnected(AgentId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task Stream(StreamEvent e) => _d.OnStream(AgentId, e);
    public Task RequestPermission(PermissionRequestMsg r) => _d.OnPermission(AgentId, r);
}
