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
    public AppHub(Realtime rt, Db db, VoicePresence voice, VoiceOptions vo) { _rt = rt; _db = db; _voice = voice; _vo = vo; }

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
