using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>An event as it is stored. <see cref="Data"/> is the JSON the event was written with.</summary>
public sealed record StoredEvent(
    long Position, string StreamId, int Version, string Type, int Schema, JsonElement Data,
    long? ActorId, long? OnBehalfOf, string? Correlation, long? Causation, long OccurredAt)
{
    public long Long(string name) => Data.GetProperty(name).GetInt64();
    public long? LongOrNull(string name) => Data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    public string? Str(string name) => Data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public bool Bool(string name) => Data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>An event about to be written. Causation is a position, or the index of an earlier event in the same batch.</summary>
public sealed record NewEvent(
    string StreamId, string Type, object Data, long? ActorId = null, long? OnBehalfOf = null,
    string? Correlation = null, long? Causation = null, int? CausedByIndex = null, int? ExpectedVersion = null, long? OccurredAt = null);

public sealed class ConcurrencyException(string stream, int expected, int actual)
    : Exception($"Stream {stream} is at version {actual}, not {expected}.");

/// <summary>What a decider sees while it chooses the events to write: the state as of this transaction, and new ids.</summary>
public interface IDecisionContext
{
    /// <summary>The next id of an id-allocating table ("posts", "members", "channels" …: tables with an AUTOINCREMENT id). Calls in one decision return 1, 2, 3 … in turn.</summary>
    long NextId(string table);
    T? Scalar<T>(string sql, object? args = null);
    IReadOnlyList<T> Query<T>(string sql, object? args = null);
    /// <summary>Writes to a table that isn't a projection (a secret: hashes stay out of the log), in the same transaction as the events.
    /// A decision may run again after a version conflict, so what it writes has to be repeatable.</summary>
    int Execute(string sql, object? args = null);
}

/// <summary>The event store. SQLite is behind it; the same table works on Postgres.</summary>
public interface IEventStore
{
    /// <summary>Writes the events in one transaction (and runs the inline projections in it). Returns them as stored.</summary>
    IReadOnlyList<StoredEvent> Append(IReadOnlyList<NewEvent> events);
    /// <summary>Like <see cref="Append(IReadOnlyList{NewEvent})"/>, but the events are chosen inside the transaction, from its state.</summary>
    IReadOnlyList<StoredEvent> Append(Func<IDecisionContext, IReadOnlyList<NewEvent>> decide);
    IReadOnlyList<StoredEvent> ReadStream(string streamId, int afterVersion = 0);
    IReadOnlyList<StoredEvent> ReadAll(long afterPosition, int limit = 1000);
    long Head();
    /// <summary>Empties every projection table and replays all events through the inline projections, in one transaction.</summary>
    void RebuildProjections();
    /// <summary>The importer's write: one transaction that runs <paramref name="before"/> (it may empty the projections), appends the events
    /// with their own times through the projections WITHOUT telling subscribers, and runs <paramref name="verify"/> (it throws to undo it all).</summary>
    IReadOnlyList<StoredEvent> Import(Action<ProjectionContext> before, IReadOnlyList<NewEvent> events, Action<ProjectionContext> verify);
    /// <summary>Called after each commit, in position order, with the events that commit wrote.</summary>
    IDisposable Subscribe(Action<IReadOnlyList<StoredEvent>> onAppended);
}

/// <summary>Updates read tables from an event, inside the transaction that appended it, so a writer reads its own writes.</summary>
public interface IInlineProjection
{
    void Apply(ProjectionContext ctx, StoredEvent e);
}

public sealed record ProjectionContext(SqliteConnection Conn, SqliteTransaction Tx)
{
    public int Execute(string sql, object? args = null) => Conn.Execute(sql, args, Tx);
    public T? Scalar<T>(string sql, object? args = null) => Conn.ExecuteScalar<T>(sql, args, Tx);
    public IEnumerable<T> Query<T>(string sql, object? args = null) => Conn.Query<T>(sql, args, Tx);
}

public sealed class SqliteEventStore : IEventStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Db _db;
    private readonly IReadOnlyList<IInlineProjection> _projections;
    private readonly object _write = new();      // one writer at a time in this process: commit order is position order, and so is notification order
    private readonly List<Action<IReadOnlyList<StoredEvent>>> _subs = new();

    public SqliteEventStore(Db db, IEnumerable<IInlineProjection> projections) { _db = db; _projections = projections.ToList(); }

    public IReadOnlyList<StoredEvent> Append(IReadOnlyList<NewEvent> events) => Append(_ => events);

    public IReadOnlyList<StoredEvent> Append(Func<IDecisionContext, IReadOnlyList<NewEvent>> decide)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return AppendOnce(decide); }
            catch (SqliteException e) when (e.SqliteExtendedErrorCode == 2067 && attempt < 5) { /* SQLITE_CONSTRAINT_UNIQUE: another writer took the version; decide again on fresh state */ }
        }
    }

    private IReadOnlyList<StoredEvent> AppendOnce(Func<IDecisionContext, IReadOnlyList<NewEvent>> decide)
    {
        lock (_write)
        {
            IReadOnlyList<StoredEvent> stored;
            using (var c = _db.Open()) stored = InTransaction(c, () => AppendCore(c, decide));
            Notify(stored);
            return stored;
        }
    }

    private void Notify(IReadOnlyList<StoredEvent> stored)
    {
        if (stored.Count == 0) return;
        Action<IReadOnlyList<StoredEvent>>[] subs;
        lock (_subs) subs = _subs.ToArray();
        foreach (var s in subs) try { s(stored); } catch { /* a subscriber's failure must not undo a commit */ }
    }

    private static T InTransaction<T>(SqliteConnection c, Func<T> body)
    {
        c.Execute("BEGIN IMMEDIATE");
        try { var r = body(); c.Execute("COMMIT"); return r; }
        catch { try { c.Execute("ROLLBACK"); } catch { /* already rolled back */ } throw; }
    }

    /// <summary>Chooses the events, writes them with the next versions and runs the inline projections on each. Call inside a transaction.</summary>
    private IReadOnlyList<StoredEvent> AppendCore(SqliteConnection c, Func<IDecisionContext, IReadOnlyList<NewEvent>> decide, IReadOnlyList<NewEvent>? given = null)
    {
        var stored = new List<StoredEvent>();
        var batch = given ?? decide(new Decision(c));
        var pc = new ProjectionContext(c, null!);
        var versions = new Dictionary<string, int>();
        foreach (var (n, i) in batch.Select((n, i) => (n, i)))
        {
            if (!versions.TryGetValue(n.StreamId, out var v))
                v = c.ExecuteScalar<int>("SELECT COALESCE(MAX(version), 0) FROM events WHERE stream_id=@s", new { s = n.StreamId });
            if (n.ExpectedVersion is { } exp && exp != v) throw new ConcurrencyException(n.StreamId, exp, v);
            versions[n.StreamId] = ++v;
            var cause = n.CausedByIndex is { } ci ? stored[ci].Position : n.Causation;
            var now = n.OccurredAt ?? Db.Now();
            var json = JsonSerializer.Serialize(n.Data, Json);
            var schema = Upcasting.CurrentSchema(n.Type);
            var pos = c.ExecuteScalar<long>(@"INSERT INTO events(stream_id, version, type, schema, data, actor_id, on_behalf_of, correlation, causation, occurred_at)
                VALUES (@s,@v,@t,@schema,@d,@a,@b,@corr,@cause,@now); SELECT last_insert_rowid();",
                new { s = n.StreamId, v, t = n.Type, schema, d = json, a = n.ActorId, b = n.OnBehalfOf, corr = n.Correlation, cause, now });
            var e = new StoredEvent(pos, n.StreamId, v, n.Type, schema, JsonDocument.Parse(json).RootElement.Clone(), n.ActorId, n.OnBehalfOf, n.Correlation, cause, now);
            stored.Add(e);
            foreach (var p in _projections) p.Apply(pc, e);
        }
        return stored;
    }

    public IReadOnlyList<StoredEvent> Import(Action<ProjectionContext> before, IReadOnlyList<NewEvent> events, Action<ProjectionContext> verify)
    {
        lock (_write)
        {
            using var c = _db.Open();
            return InTransaction(c, () =>
            {
                var pc = new ProjectionContext(c, null!);
                before(pc);
                var stored = AppendCore(c, _ => events, events);
                verify(pc);
                return stored;
            });
        }
    }

    public void RebuildProjections()
    {
        lock (_write)
        {
            using var c = _db.Open();
            InTransaction(c, () =>
            {
                var pc = new ProjectionContext(c, null!);
                Projections.Clear(pc);
                long after = 0;
                while (true)
                {
                    var rows = c.Query<Row>(Select + " WHERE position>@after ORDER BY position LIMIT 1000", new { after }).Select(ToEvent).ToList();
                    if (rows.Count == 0) break;
                    foreach (var e in rows) foreach (var p in _projections) p.Apply(pc, e);
                    after = rows[^1].Position;
                }
                return 0;
            });
        }
    }

    public IReadOnlyList<StoredEvent> ReadStream(string streamId, int afterVersion = 0)
    {
        using var c = _db.Open();
        return c.Query<Row>(Select + " WHERE stream_id=@streamId AND version>@afterVersion ORDER BY version", new { streamId, afterVersion }).Select(ToEvent).ToList();
    }

    public IReadOnlyList<StoredEvent> ReadAll(long afterPosition, int limit = 1000)
    {
        using var c = _db.Open();
        return c.Query<Row>(Select + " WHERE position>@afterPosition ORDER BY position LIMIT @limit", new { afterPosition, limit }).Select(ToEvent).ToList();
    }

    public long Head()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("SELECT COALESCE(MAX(position), 0) FROM events");
    }

    public IDisposable Subscribe(Action<IReadOnlyList<StoredEvent>> onAppended)
    {
        lock (_subs) _subs.Add(onAppended);
        return new Unsub(() => { lock (_subs) _subs.Remove(onAppended); });
    }

    private sealed class Unsub(Action a) : IDisposable { public void Dispose() => a(); }

    private const string Select = "SELECT position, stream_id AS StreamId, version, type, schema, data, actor_id AS ActorId, on_behalf_of AS OnBehalfOf, correlation, causation, occurred_at AS OccurredAt FROM events";
    private sealed class Row
    {
        public long Position { get; set; } public string StreamId { get; set; } = ""; public int Version { get; set; } public string Type { get; set; } = "";
        public int Schema { get; set; } public string Data { get; set; } = "{}"; public long? ActorId { get; set; } public long? OnBehalfOf { get; set; }
        public string? Correlation { get; set; } public long? Causation { get; set; } public long OccurredAt { get; set; }
    }
    /// <summary>Every read goes through here: an event written with an older shape is converted to the current one (see <see cref="Upcasting"/>).</summary>
    private static StoredEvent ToEvent(Row r)
    {
        var (schema, data) = Upcasting.ToCurrent(r.Type, r.Schema, JsonDocument.Parse(r.Data).RootElement.Clone());
        return new(r.Position, r.StreamId, r.Version, r.Type, schema, data, r.ActorId, r.OnBehalfOf, r.Correlation, r.Causation, r.OccurredAt);
    }

    private sealed class Decision(SqliteConnection c) : IDecisionContext
    {
        private readonly Dictionary<string, long> _ids = new();
        public long NextId(string table)
        {
            if (!_ids.TryGetValue(table, out var last))
                last = c.ExecuteScalar<long?>("SELECT seq FROM sqlite_sequence WHERE name=@table", new { table }) ?? 0;
            _ids[table] = ++last;
            return last;
        }
        public T? Scalar<T>(string sql, object? args = null) => c.ExecuteScalar<T>(sql, args);
        public IReadOnlyList<T> Query<T>(string sql, object? args = null) => c.Query<T>(sql, args).ToList();
        public int Execute(string sql, object? args = null) => c.Execute(sql, args);
    }
}
