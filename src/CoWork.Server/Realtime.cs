using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace CoWork.Server;

/// <summary>Browser connections and what they may hear: a connection is in a channel's group only if its person may see the channel.</summary>
public sealed class Realtime
{
    private readonly IHubContext<AppHub> _hub;
    private readonly Db _db;
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, byte>> _conns = new();
    private readonly ConcurrentDictionary<string, HashSet<long>> _groups = new();

    public IServiceProvider Services { get; }
    public Realtime(IHubContext<AppHub> hub, Db db, IServiceProvider sp) { _hub = hub; _db = db; Services = sp; }

    public static string ChannelGroup(long id) => $"ch:{id}";

    public async Task Connected(long memberId, string connectionId)
    {
        _conns.GetOrAdd(memberId, _ => new()).TryAdd(connectionId, 0);
        await _hub.Groups.AddToGroupAsync(connectionId, "all");
        await SyncConnection(memberId, connectionId);
    }

    public void Disconnected(long memberId, string connectionId)
    {
        if (_conns.TryGetValue(memberId, out var set)) set.TryRemove(connectionId, out _);
        _groups.TryRemove(connectionId, out _);
    }

    public bool HasConnection(long memberId, string connectionId) => _conns.TryGetValue(memberId, out var s) && s.ContainsKey(connectionId);

    /// <summary>Puts every connection of a person into exactly the channel groups they may hear.</summary>
    public async Task Refresh(long memberId)
    {
        if (!_conns.TryGetValue(memberId, out var set)) return;
        foreach (var id in set.Keys.ToList()) await SyncConnection(memberId, id);
    }

    public async Task RefreshAll()
    {
        foreach (var m in _conns.Keys.ToList()) await Refresh(m);
    }

    private async Task SyncConnection(long memberId, string connectionId)
    {
        HashSet<long> want;
        using (var c = _db.Open()) want = Access.VisibleChannelIds(c, memberId).ToHashSet();
        var have = _groups.GetOrAdd(connectionId, _ => new());
        lock (have) { }
        foreach (var id in want.Except(have).ToList())
        { await _hub.Groups.AddToGroupAsync(connectionId, ChannelGroup(id)); lock (have) have.Add(id); }
        foreach (var id in have.Except(want).ToList())
        { await _hub.Groups.RemoveFromGroupAsync(connectionId, ChannelGroup(id)); lock (have) have.Remove(id); }
    }

    public Task ToChannel(long channelId, string evt, object payload) =>
        _hub.Clients.Group(ChannelGroup(channelId)).SendAsync(evt, payload);

    public Task ToAll(string evt, object? payload = null) =>
        _hub.Clients.Group("all").SendAsync(evt, payload ?? new { });
}
