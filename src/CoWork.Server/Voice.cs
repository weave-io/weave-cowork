using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoWork.Server;

/// <summary>Voice is on when the server knows a LiveKit API key and secret (env LIVEKIT_API_KEY / LIVEKIT_API_SECRET).
/// LIVEKIT_URL is the address browsers use to reach LiveKit (ws:// or wss://); when unset the web app uses its own host on port 7880.</summary>
public sealed record VoiceOptions(string? ApiKey, string? ApiSecret, string? Url)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
    public static VoiceOptions From(IConfiguration c) => new(c["LIVEKIT_API_KEY"], c["LIVEKIT_API_SECRET"], string.IsNullOrWhiteSpace(c["LIVEKIT_URL"]) ? null : c["LIVEKIT_URL"]!.Trim());
}

/// <summary>A LiveKit access token: an HS256 JWT signed with the API secret, carrying the video grant. Written by hand; no LiveKit SDK.</summary>
public static class LiveKitToken
{
    public static string Create(string apiKey, string apiSecret, string room, string identity, string name, TimeSpan ttl, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = apiKey, sub = identity, name,
            nbf = t.AddSeconds(-5).ToUnixTimeSeconds(), exp = t.Add(ttl).ToUnixTimeSeconds(),
            video = new { room, roomJoin = true, canPublish = true, canSubscribe = true, canPublishData = true },
        }));
        var sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiSecret), Encoding.ASCII.GetBytes(header + "." + payload));
        return $"{header}.{payload}.{B64(sig)}";
    }
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Who is in which voice channel, as the browsers report it over the hub. A person with two tabs counts once.
/// A connection that drops leaves on its own.</summary>
public sealed class VoicePresence
{
    private readonly object _l = new();
    private readonly Dictionary<string, (long Channel, long Member)> _byConn = new();

    /// <summary>Puts a connection in a channel (leaving any other). Returns the channels whose roster changed.</summary>
    public List<long> Join(string conn, long channel, long member)
    {
        lock (_l)
        {
            var changed = new List<long>();
            if (_byConn.TryGetValue(conn, out var old)) { _byConn.Remove(conn); if (old.Channel != channel || old.Member != member) changed.Add(old.Channel); }
            var before = Members(channel);
            _byConn[conn] = (channel, member);
            if (!Members(channel).SequenceEqual(before) || !before.Contains(member)) changed.Add(channel);
            return changed.Distinct().ToList();
        }
    }

    public List<long> Leave(string conn)
    {
        lock (_l)
        {
            if (!_byConn.Remove(conn, out var old)) return new();
            return new() { old.Channel };
        }
    }

    public List<long> Members(long channel) { lock (_l) return _byConn.Values.Where(v => v.Channel == channel).Select(v => v.Member).Distinct().Order().ToList(); }

    public Dictionary<long, List<long>> Snapshot()
    {
        lock (_l) return _byConn.Values.GroupBy(v => v.Channel).ToDictionary(g => g.Key, g => g.Select(v => v.Member).Distinct().Order().ToList());
    }

    public void ClearChannel(long channel) { lock (_l) foreach (var k in _byConn.Where(kv => kv.Value.Channel == channel).Select(kv => kv.Key).ToList()) _byConn.Remove(k); }
    public void RemoveMember(long member) { lock (_l) foreach (var k in _byConn.Where(kv => kv.Value.Member == member).Select(kv => kv.Key).ToList()) _byConn.Remove(k); }
}
