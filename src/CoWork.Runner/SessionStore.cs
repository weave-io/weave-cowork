using System.Text.Json;

namespace CoWork.Runner;

/// <summary>Which harness session belongs to which thread, saved so a restarted runner can session/load them. One file per agent.</summary>
public sealed class SessionStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<long, string> _map;

    public SessionStore(string path)
    {
        _path = path;
        try { _map = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<long, string>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception) { _map = new(); }
    }

    public string? Get(long thread) { lock (_lock) return _map.GetValueOrDefault(thread); }

    public void Set(long thread, string sessionId)
    {
        lock (_lock)
        {
            _map[thread] = sessionId;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_map));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
