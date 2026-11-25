using Dapper;

namespace CoWork.Server;

/// <summary>
/// A projection that runs AFTER the commit, from a checkpoint (the position it has applied up to), so a slow or broken view never holds up a write.
/// It owns its tables: <see cref="Tables"/> are emptied by a rebuild, which replays every event from position 0.
/// </summary>
public interface IAsyncProjection
{
    string Name { get; }
    string Title { get; }
    /// <summary>Children first.</summary>
    string[] Tables { get; }
    void Apply(ProjectionContext x, StoredEvent e);
}

public sealed record ProjectionStatus(string Name, string Title, long Position, long Head, long? RebuiltAt);

/// <summary>
/// Runs the async projections: wakes when events are committed (and every second, as a safety net), applies what is after each checkpoint in
/// batches (one transaction per batch, the checkpoint moves in the same transaction), and tells browsers that their views changed.
/// A rebuild replays everything in ONE transaction, so readers see the old view until the new one is complete.
/// </summary>
public sealed class ProjectionRunner : BackgroundService
{
    private const int Batch = 500;
    private readonly Db _db;
    private readonly IEventStore _store;
    private readonly IReadOnlyList<IAsyncProjection> _projections;
    private readonly Realtime _rt;
    private readonly ILogger<ProjectionRunner> _log;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Dictionary<string, object> _locks;

    public ProjectionRunner(Db db, IEventStore store, IEnumerable<IAsyncProjection> projections, Realtime rt, ILogger<ProjectionRunner> log)
    {
        _db = db; _store = store; _rt = rt; _log = log;
        _projections = projections.ToList();
        _locks = _projections.ToDictionary(p => p.Name, _ => new object());
        store.Subscribe(_ => { try { _wake.Release(); } catch (SemaphoreFullException) { } });
        _db.Open().Dispose();
    }

    public IReadOnlyList<IAsyncProjection> Projections => _projections;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try { await CatchUpAsync(); }
            catch (Exception e) { _log.LogWarning(e, "a projection could not catch up"); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Brings every projection up to the head of the log, now, on the calling thread. The same code the background loop runs.</summary>
    public async Task CatchUpAsync()
    {
        var changed = new List<string>();
        foreach (var p in _projections)
            if (CatchUp(p) > 0) changed.Add(p.Name);
        if (changed.Count > 0) await _rt.ToAll("views-changed", new { projections = changed });
    }

    private long Checkpoint(Microsoft.Data.Sqlite.SqliteConnection c, string name) =>
        c.ExecuteScalar<long?>("SELECT position FROM projection_checkpoints WHERE name=@name", new { name }) ?? 0;

    private int CatchUp(IAsyncProjection p)
    {
        var applied = 0;
        lock (_locks[p.Name])
            while (true)
            {
                using var c = _db.Open();
                var after = Checkpoint(c, p.Name);
                var events = _store.ReadAll(after, Batch);
                if (events.Count == 0) return applied;
                c.Execute("BEGIN IMMEDIATE");
                try
                {
                    // read again inside the transaction: a rebuild may have moved the checkpoint while we were reading
                    if (Checkpoint(c, p.Name) != after) { c.Execute("ROLLBACK"); continue; }
                    var x = new ProjectionContext(c, null!);
                    foreach (var e in events) p.Apply(x, e);
                    Save(c, p.Name, events[^1].Position, rebuilt: false);
                    c.Execute("COMMIT");
                }
                catch { try { c.Execute("ROLLBACK"); } catch { /* already */ } throw; }
                applied += events.Count;
            }
    }

    private static void Save(Microsoft.Data.Sqlite.SqliteConnection c, string name, long position, bool rebuilt) =>
        c.Execute(@"INSERT INTO projection_checkpoints(name, position, updated_at, rebuilt_at) VALUES (@name,@position,@now,@rebuilt)
            ON CONFLICT(name) DO UPDATE SET position=@position, updated_at=@now, rebuilt_at=COALESCE(@rebuilt, rebuilt_at)",
            new { name, position, now = Db.Now(), rebuilt = rebuilt ? Db.Now() : (long?)null });

    /// <summary>Empties the projection's tables and replays every event into them, in one transaction. Returns how many events it replayed.</summary>
    public async Task<int> RebuildAsync(string name)
    {
        var p = _projections.FirstOrDefault(x => x.Name == name) ?? throw new KeyNotFoundException(name);
        var n = 0;
        lock (_locks[name])
        {
            using var c = _db.Open();
            c.Execute("BEGIN IMMEDIATE");
            try
            {
                var x = new ProjectionContext(c, null!);
                foreach (var t in p.Tables) { x.Execute($"DELETE FROM {t}"); x.Execute("DELETE FROM sqlite_sequence WHERE name=@t", new { t }); }       // ids that count up start again, so the rebuilt rows are the same rows
                long after = 0;
                while (true)
                {
                    var events = _store.ReadAll(after, Batch);
                    if (events.Count == 0) break;
                    foreach (var e in events) p.Apply(x, e);
                    after = events[^1].Position; n += events.Count;
                }
                Save(c, name, after, rebuilt: true);
                c.Execute("COMMIT");
            }
            catch { try { c.Execute("ROLLBACK"); } catch { /* already */ } throw; }
        }
        await CatchUpAsync();           // anything committed while it replayed
        await _rt.ToAll("views-changed", new { projections = new[] { name } });
        return n;
    }

    public IReadOnlyList<ProjectionStatus> Status()
    {
        using var c = _db.Open();
        var head = _store.Head();
        return _projections.Select(p => new ProjectionStatus(p.Name, p.Title, Checkpoint(c, p.Name), head,
            c.ExecuteScalar<long?>("SELECT rebuilt_at FROM projection_checkpoints WHERE name=@n", new { n = p.Name }))).ToList();
    }
}
