using System.Data;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

public sealed class Db
{
    private readonly string _cs;
    public Db(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, ForeignKeys = true }.ToString();
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>Applies numbered .sql migrations (embedded resources) that haven't run yet.</summary>
    public void Migrate()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL");
        c.Execute("CREATE TABLE IF NOT EXISTS schema_migrations (name TEXT PRIMARY KEY, applied_at INTEGER NOT NULL)");
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM schema_migrations WHERE name=@res", new { res }) > 0) continue;
            using var tx = c.BeginTransaction();
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            c.Execute(r.ReadToEnd(), transaction: tx);
            c.Execute("INSERT INTO schema_migrations VALUES (@res, @now)", new { res, now = Now() }, tx);
            tx.Commit();
        }
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
