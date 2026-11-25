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
