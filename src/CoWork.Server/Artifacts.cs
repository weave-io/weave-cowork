using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>Where artifacts live and where they are served from. <see cref="Origin"/> (COWORK_ARTIFACT_ORIGIN, e.g.
/// https://artifacts.cowork.example) puts them on a host name of their own; unset, they are served from this server's <c>/a/…</c>, still with an
/// opaque origin (a sandboxed frame and a CSP sandbox). <see cref="AppOrigin"/> (COWORK_APP_ORIGIN) is who may frame them when they're elsewhere.</summary>
public sealed record ArtifactOptions(string Dir, string? Origin, string? AppOrigin)
{
    public static ArtifactOptions From(IConfiguration cfg, string dataDir) =>
        new(cfg["COWORK_ARTIFACTS"] ?? Path.Combine(dataDir, "artifacts"),
            string.IsNullOrWhiteSpace(cfg["COWORK_ARTIFACT_ORIGIN"]) ? null : cfg["COWORK_ARTIFACT_ORIGIN"]!.TrimEnd('/'),
            string.IsNullOrWhiteSpace(cfg["COWORK_APP_ORIGIN"]) ? null : cfg["COWORK_APP_ORIGIN"]!.TrimEnd('/'));
}

/// <summary>What kind of thing a file is, as the canvas shows it, and the type it is served with.</summary>
public static class ArtifactKinds
{
    public static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "html",
        ".md" or ".markdown" => "markdown",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".avif" => "image",
        ".svg" => "svg",
        ".csv" => "csv",
        ".json" => "json",
        ".pdf" => "pdf",
        _ => "file",
    };

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".md" or ".markdown" => "text/markdown; charset=utf-8",
        ".csv" => "text/csv; charset=utf-8",
        ".txt" or ".log" => "text/plain; charset=utf-8",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".avif" => "image/avif",
        ".svg" => "image/svg+xml", ".ico" => "image/x-icon",
        ".pdf" => "application/pdf",
        ".woff" => "font/woff", ".woff2" => "font/woff2", ".ttf" => "font/ttf", ".otf" => "font/otf",
        ".mp4" => "video/mp4", ".webm" => "video/webm", ".mp3" => "audio/mpeg", ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    /// <summary>A path inside an artifact: relative, forward slashes, no "." or ".." segments, nothing hidden.</summary>
    public static bool SafePath(string? p) =>
        !string.IsNullOrEmpty(p) && p.Length <= 400 && !p.StartsWith('/') && !p.Contains('\\') && !p.Contains('\0') && !p.Contains(':')
        && p.Split('/').All(s => s.Length > 0 && s != "." && s != "..");
}

/// <summary>The bytes: a content-addressed folder under the data directory (<c>ab/abcdef…</c> by SHA-256). Events and tables only name them.</summary>
public sealed class ArtifactStore
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, int> _pinned = new();      // blobs an upload has written and not yet published: a sweep keeps them
    public ArtifactStore(ArtifactOptions o) { _dir = o.Dir; Directory.CreateDirectory(_dir); }
    public string Dir => _dir;

    public string PathOf(string sha) => Path.Combine(_dir, sha[..2], sha);
    public bool Has(string sha) => File.Exists(PathOf(sha));

    /// <summary>Writes a file's bytes (once per content) and pins it until <see cref="Unpin"/>. Returns its SHA-256 and size.</summary>
    public async Task<(string Sha, long Size)> Save(Stream s, long maxBytes, CancellationToken ct = default)
    {
        var tmp = Path.Combine(_dir, ".incoming-" + Guid.NewGuid().ToString("N"));
        long size = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var f = File.Create(tmp))
            {
                var buf = new byte[81920]; int n;
                while ((n = await s.ReadAsync(buf, ct)) > 0)
                {
                    size += n;
                    if (size > maxBytes) throw new InvalidDataException("too big");
                    sha.AppendData(buf, 0, n);
                    await f.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            var hex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            _pinned.AddOrUpdate(hex, 1, (_, c) => c + 1);
            var to = PathOf(hex);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to)) File.Delete(tmp); else File.Move(tmp, to, overwrite: true);
            return (hex, size);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    public void Unpin(IEnumerable<string> shas) { foreach (var s in shas) _pinned.AddOrUpdate(s, 0, (_, c) => c - 1); foreach (var (k, v) in _pinned) if (v <= 0) _pinned.TryRemove(k, out _); }

    /// <summary>Deletes every blob no artifact version names any more (a channel was deleted, a version redacted). Returns how many went.</summary>
    public int Sweep(SqliteConnection c)
    {
        var keep = c.Query<string>("SELECT DISTINCT sha256 FROM artifact_files").ToHashSet();
        var gone = 0;
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith('.') || name.Length != 64 || keep.Contains(name) || _pinned.ContainsKey(name)) continue;
            try { File.Delete(f); gone++; } catch { /* in use: the next sweep */ }
        }
        return gone;
    }
}

/// <summary>Short-lived signed addresses for one artifact version: <c>/a/{id}-{version}-{expires}-{signature}/{path}</c>. A page's own files are
/// relative to it, so they're covered by the same token. The key is the server's own (a file in the artifacts folder, never in an event).</summary>
public sealed class ArtifactSigner
{
    private readonly byte[] _key;
    public static TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    public ArtifactSigner(ArtifactStore store)
    {
        var path = Path.Combine(store.Dir, ".key");
        if (!File.Exists(path))
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(tmp, RandomNumberGenerator.GetBytes(32));
            try { File.Move(tmp, path); } catch (IOException) { File.Delete(tmp); }      // another start wrote it first
        }
        _key = File.ReadAllBytes(path);
    }

    public string Token(long id, int version, DateTimeOffset? now = null)
    {
        var exp = (now ?? DateTimeOffset.UtcNow).Add(Lifetime).ToUnixTimeSeconds();
        var body = $"{id}-{version}-{exp}";
        return body + "-" + Sig(body);
    }

    public (long Id, int Version)? Verify(string token, DateTimeOffset? now = null)
    {
        var parts = token.Split('-');
        if (parts.Length != 4 || !long.TryParse(parts[0], out var id) || !int.TryParse(parts[1], out var v) || !long.TryParse(parts[2], out var exp)) return null;
        var expected = Sig($"{parts[0]}-{parts[1]}-{parts[2]}");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[3]))) return null;
        if ((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() > exp) return null;
        return (id, v);
    }

    private string Sig(string body) => Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(body)))[..32].Replace('+', '_').Replace('/', '_');
}

/// <summary>
/// Artifacts (brief 9): what an agent shows people. The agent writes a file in its workspace and calls <c>cowork_show</c> on CoWork's MCP server;
/// the server asks the agent's runner (its sandbox's, or its pinned runner) for the file, the runner reads it and the folder it's in (a page brings
/// its CSS and images; at most 25 MB) and uploads them with its own token. The server keeps the bytes content-addressed and writes
/// <c>ArtifactPublished</c> on the thread's stream: the same path in the same thread is the next version. People see a card; the canvas shows it
/// from a short-lived signed address in a sandboxed frame with a strict CSP, so the page can't read CoWork's cookies, call its API or reach
/// anywhere else.
/// </summary>
public sealed class Artifacts
{
    private readonly Db _db;
    private readonly IEventStore _store;
    private readonly ArtifactStore _blobs;
    private readonly ArtifactSigner _signer;
    private readonly ArtifactOptions _opts;
    private readonly RunnerRegistry _runners;
    private readonly SandboxRegistry _sandboxes;
    private readonly IHubContext<RunnerHub> _hub;
    private readonly ILogger<Artifacts> _log;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    public static TimeSpan CollectTimeout = TimeSpan.FromMinutes(2);

    private sealed record Pending(Mcp.Caller Who, string Path, string? Title, string Open, TaskCompletionSource<(string Text, bool Error, object Detail)> Done);

    public Artifacts(Db db, IEventStore store, ArtifactStore blobs, ArtifactSigner signer, ArtifactOptions opts, RunnerRegistry runners, SandboxRegistry sandboxes,
        IHubContext<RunnerHub> hub, ILogger<Artifacts> log)
    {
        _db = db; _store = store; _blobs = blobs; _signer = signer; _opts = opts; _runners = runners; _sandboxes = sandboxes; _hub = hub; _log = log;
        // a deleted channel or a redaction leaves bytes nobody names: they go at once (the fact stays in the events)
        store.Subscribe(batch =>
        {
            if (!batch.Any(e => e.Type is "ChannelDeleted" or "ArtifactRedacted")) return;
            try { using var c = _db.Open(); var n = _blobs.Sweep(c); if (n > 0) _log.LogInformation("removed {Count} artifact files nobody names any more", n); }
            catch (Exception e) { _log.LogWarning(e, "couldn't sweep artifact files"); }
        });
    }

    public static JsonObject ToolDefinition() => new()
    {
        ["name"] = "cowork_show",
        ["description"] = "Show people something you made, as an artifact in this thread: an HTML page or report, a markdown document, an image or SVG, a CSV or JSON " +
            "table, or a PDF. Write the file in your workspace first, then call this with its path. A page can bring its CSS, scripts and images in the same " +
            "folder (at most 25 MB; it can't load anything from the internet or call any API, so keep everything it needs beside it). People get a card " +
            "in the thread and read it beside the conversation. Use it whenever you produce a report, a document, a chart or a table people should " +
            "look at, rather than pasting it into your answer. People only see a change when you show it again: after you edit a file you showed, " +
            "call cowork_show again with the same path, and they get the next version.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file in your workspace, like reports/tax-apis/index.html." },
                ["title"] = new JsonObject { ["type"] = "string", ["description"] = "A short title people see on the card (default: the page's <title>, else the file's name)." },
                ["open"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("canvas", "card"), ["description"] = "\"canvas\" (default) opens it beside the thread for whoever started the thread, if they're looking; \"card\" only adds the card." },
            },
            ["required"] = new JsonArray("path"),
        },
    };

    private string? ConnectionFor(SqliteConnection c, Mcp.Caller who)
    {
        var template = c.ExecuteScalar<string?>("SELECT placement FROM agents WHERE member_id=@a", new { a = who.AgentId }) == "pool";
        return template ? _sandboxes.ConnectionOf(who.AgentId, who.ThreadId) : _runners.ConnectionOf(who.AgentId);
    }

    /// <summary>The tool: asks the agent's runner for the file and waits for its upload (or its reason why not).</summary>
    public async Task<(string Text, bool Error, object Detail)> Show(Mcp.Caller who, JsonObject args)
    {
        var path = ((string?)args["path"])?.Trim();
        if (string.IsNullOrEmpty(path)) return ("Give the path of the file to show, in your workspace.", true, new { });
        var open = (string?)args["open"] == "card" ? "card" : "canvas";
        var title = ((string?)args["title"])?.Trim();
        if (title is { Length: > 120 }) title = title[..120].TrimEnd() + "…";
        string? conn;
        using (var c = _db.Open()) conn = ConnectionFor(c, who);
        if (conn is null) return ($"{who.Handle}'s runner isn't connected, so cowork_show can't read {path}.", true, new { path });
        var id = Guid.NewGuid().ToString("N");
        var p = new Pending(who, path, string.IsNullOrEmpty(title) ? null : title, open, new(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[id] = p;
        try
        {
            await _hub.Clients.Client(conn).SendAsync("CollectArtifact", new CollectArtifact(id, who.ThreadId, path));
            return await p.Done.Task.WaitAsync(CollectTimeout);
        }
        catch (TimeoutException) { return ($"The runner didn't send {path} within {CollectTimeout.TotalMinutes:0} minutes.", true, new { path }); }
        finally { _pending.TryRemove(id, out _); }
    }

    public static void Map(WebApplication app)
    {
        // the runner's upload for a cowork_show call, with its token (a sandbox's thread token; a pinned runner's own and the thread)
        app.MapPost("/api/runner/artifacts/{requestId}", (string requestId, HttpContext ctx, Artifacts a) => a.Upload(requestId, ctx)).DisableAntiforgery();
        // the artifact itself, at a signed address, for the canvas's sandboxed frame
        app.MapGet("/a/{token}/{**path}", (string token, string? path, HttpContext ctx, Artifacts a) => a.Serve(token, path, ctx));
        var authed = app.MapGroup("/api").RequireAuthorization();
        authed.MapGet("/artifacts/{id:long}", (long id, HttpContext ctx, Artifacts a) => a.Describe(id, AppHub.MemberId(ctx.User)) is { } d ? Results.Ok(d) : NotFound());
        authed.MapGet("/artifacts/{id:long}/versions/{v:int}/raw/{**path}", (long id, int v, string? path, HttpContext ctx, Artifacts a) => a.Raw(id, v, path, AppHub.MemberId(ctx.User), ctx));
        authed.MapGet("/artifacts/{id:long}/versions/{v:int}/download", (long id, int v, HttpContext ctx, Artifacts a) => a.Download(id, v, AppHub.MemberId(ctx.User), ctx));
        authed.MapPost("/artifacts/{id:long}/redact", (long id, RedactArtifactBody? b, HttpContext ctx, Artifacts a) => a.Redact(id, b?.Version, AppHub.MemberId(ctx.User)));
    }

    static IResult NotFound() => Results.Json(new { error = "No such artifact." }, statusCode: 404);

    // ---- the upload ----

    public sealed record Manifest(string? Path, string? Entry, string? Error, ManifestFile[]? Files);
    public sealed record ManifestFile(string Path, long Size);

    private async Task<IResult> Upload(string requestId, HttpContext ctx)
    {
        Mcp.Caller? who;
        using (var c = _db.Open()) who = Mcp.Authenticate(c, ctx.Request.Headers.Authorization.ToString(), ctx.Request.Headers["X-Cowork-Thread"].ToString());
        if (who is null) return Results.StatusCode(401);
        if (!_pending.TryGetValue(requestId, out var p) || p.Who.AgentId != who.AgentId || p.Who.ThreadId != who.ThreadId) return Results.Json(new { error = "No such request." }, statusCode: 404);
        if (!ctx.Request.HasFormContentType) return Results.Json(new { error = "Send a form." }, statusCode: 400);
        var form = await ctx.Request.ReadFormAsync();
        Manifest? m;
        try { m = JsonSerializer.Deserialize<Manifest>(form["manifest"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch { m = null; }
        if (m is null) return Fail(p, "The runner sent no manifest.");
        if (!string.IsNullOrEmpty(m.Error)) { p.Done.TrySetResult((m.Error, true, new { path = p.Path })); return Results.Ok(new { ok = true }); }
        var files = m.Files ?? Array.Empty<ManifestFile>();
        if (!ArtifactKinds.SafePath(m.Path) || !ArtifactKinds.SafePath(m.Entry) || files.Length == 0) return Fail(p, "The runner's manifest names an unsafe path.");
        if (files.Length > ArtifactLimits.MaxFiles) return Fail(p, $"That's {files.Length} files; cowork_show takes at most {ArtifactLimits.MaxFiles}.");
        if (files.Any(f => !ArtifactKinds.SafePath(f.Path)) || files.Select(f => f.Path).Distinct().Count() != files.Length) return Fail(p, "The runner's manifest names an unsafe path.");
        if (!files.Any(f => f.Path == m.Entry)) return Fail(p, "The runner didn't send the file itself.");
        var saved = new List<(string Path, string Sha, long Size, string Type)>();
        try
        {
            long total = 0;
            for (var i = 0; i < files.Length; i++)
            {
                var part = form.Files.GetFile("file" + i);
                if (part is null) return Fail(p, $"The runner didn't send {files[i].Path}.");
                await using var s = part.OpenReadStream();
                var (sha, size) = await _blobs.Save(s, ArtifactLimits.MaxBytes - total, ctx.RequestAborted);
                total += size;
                saved.Add((files[i].Path, sha, size, ArtifactKinds.ContentType(files[i].Path)));
            }
        }
        catch (InvalidDataException) { _blobs.Unpin(saved.Select(s => s.Sha)); return Fail(p, $"{p.Path} and the files beside it are more than {ArtifactLimits.MaxBytes / 1024 / 1024} MB; cowork_show takes at most that. Put the page in a folder of its own."); }
        try
        {
            var result = Publish(who, m.Path!, m.Entry!, p.Title, p.Open, saved);
            p.Done.TrySetResult(result);
            return Results.Ok(new { ok = true });
        }
        finally { _blobs.Unpin(saved.Select(s => s.Sha)); }
    }

    private static IResult Fail(Pending p, string why) { p.Done.TrySetResult((why, true, new { path = p.Path })); return Results.Json(new { error = why }, statusCode: 400); }

    /// <summary>Writes the version down: the same path in the same thread is the next version of the same artifact.</summary>
    internal (string Text, bool Error, object Detail) Publish(Mcp.Caller who, string path, string entry, string? title, string open, List<(string Path, string Sha, long Size, string Type)> files)
    {
        var kind = ArtifactKinds.Kind(entry);
        var size = files.Sum(f => f.Size);
        var named = title ?? TitleOf(files.First(f => f.Path == entry), entry, path);
        long artifactId = 0; int version = 0; long postId = 0; string finalTitle = named;
        _store.Append(ctx =>
        {
            var t = who.ThreadId;
            var channelId = ctx.Scalar<long>("SELECT channel_id FROM threads WHERE root_id=@t", new { t });
            var projectId = ctx.Scalar<long?>("SELECT project_id FROM channels WHERE id=@channelId", new { channelId });
            var existing = ctx.Query<(long Id, int Version, string Title)>("SELECT id, version, title FROM artifacts WHERE thread_id=@t AND path=@path", new { t, path }).FirstOrDefault();
            artifactId = existing.Id != 0 ? existing.Id : ctx.NextId("artifacts");
            version = existing.Version + 1;
            finalTitle = title ?? (existing.Id != 0 ? existing.Title : named);
            postId = ctx.NextId("posts");
            var turn = ctx.Query<(string? Turn, long? Behalf)>("SELECT turn, behalf FROM open_turns WHERE thread_id=@t AND agent_id=@a", new { t, a = who.AgentId }).FirstOrDefault();
            return new[] { new NewEvent($"thread-{t}", "ArtifactPublished", new
            {
                artifactId, version, agentId = who.AgentId, channelId, projectId, path, entry, title = finalTitle, kind, size, postId, open,
                files = files.Select(f => new { path = f.Path, sha256 = f.Sha, size = f.Size, type = f.Type }).ToArray(),
            }, who.AgentId, turn.Behalf, turn.Turn) };
        });
        var text = $"Showing \"{finalTitle}\" (artifact {artifactId}, version {version}, {Size(size)}{(files.Count > 1 ? $", {files.Count} files" : "")}) in this thread. " +
            (open == "canvas" ? "It opens beside the thread for whoever started it; everyone else sees its card." : "People open it from its card.") +
            " When you change it, call cowork_show again with the same path: that publishes the next version.";
        return (text, false, new { artifactId, version, path, title = finalTitle, kind, size, files = files.Count });
    }

    public static string Size(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{Math.Max(1, b / 1024)} KB" : (b / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static readonly Regex TitleTag = new(@"<title[^>]*>([^<]{1,200})</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^#\s+(.{1,120})$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>A page's &lt;title&gt;, a markdown document's first heading, else the file's (or for index.html, its folder's) name.</summary>
    private string TitleOf((string Path, string Sha, long Size, string Type) file, string entry, string path)
    {
        try
        {
            var kind = ArtifactKinds.Kind(entry);
            if (kind is "html" or "markdown")
            {
                using var f = File.OpenRead(_blobs.PathOf(file.Sha));
                var buf = new byte[Math.Min(file.Size, 64 * 1024)];
                var n = f.Read(buf, 0, buf.Length);
                var head = Encoding.UTF8.GetString(buf, 0, n);
                var m = kind == "html" ? TitleTag.Match(head) : Heading.Match(head);
                if (m.Success && System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() is { Length: > 0 } t) return t.Length > 120 ? t[..120] + "…" : t;
            }
        }
        catch { /* the name will do */ }
        var name = Path.GetFileName(path);
        if (Path.GetFileNameWithoutExtension(name).Equals("index", StringComparison.OrdinalIgnoreCase) && path.Contains('/'))
            return path.Split('/')[^2];
        return name;
    }

    // ---- reading ----

    public sealed class ArtifactRow { public long Id { get; set; } public long ThreadId { get; set; } public long ChannelId { get; set; } public long AgentId { get; set; } public string Path { get; set; } = ""; public string Title { get; set; } = ""; public string Kind { get; set; } = ""; public int Version { get; set; } public long CreatedAt { get; set; } }
    public sealed class VersionRow { public int Version { get; set; } public string Title { get; set; } = ""; public string Kind { get; set; } = ""; public string Entry { get; set; } = ""; public long Size { get; set; } public int Files { get; set; } public long PublishedBy { get; set; } public long? Behalf { get; set; } public long? PostId { get; set; } public long CreatedAt { get; set; } public bool Redacted { get; set; } }
    public sealed class FileRow { public string Path { get; set; } = ""; public string Sha256 { get; set; } = ""; public long Size { get; set; } public string Type { get; set; } = ""; }

    /// <summary>The artifact if this person may see it (they can see its thread's channel), else null: a 404, the same as one that doesn't exist.</summary>
    private static ArtifactRow? Visible(SqliteConnection c, long id, long me)
    {
        var a = c.QueryFirstOrDefault<ArtifactRow>("SELECT * FROM artifacts WHERE id=@id", new { id });
        return a is not null && Access.CanSee(c, a.ChannelId, me) ? a : null;
    }

    public string Address(long id, int version, string path) =>
        $"{_opts.Origin ?? ""}/a/{_signer.Token(id, version)}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    public object? Describe(long id, long me)
    {
        using var c = _db.Open();
        var a = Visible(c, id, me);
        if (a is null) return null;
        var owner = c.ExecuteScalar<long?>("SELECT owner_id FROM agents WHERE member_id=@x", new { x = a.AgentId });
        var versions = c.Query<VersionRow>("SELECT * FROM artifact_versions WHERE artifact_id=@id ORDER BY version DESC", new { id }).Select(v =>
        {
            var files = v.Redacted ? new List<FileRow>() : c.Query<FileRow>("SELECT path, sha256, size, type FROM artifact_files WHERE artifact_id=@id AND version=@v ORDER BY path", new { id, v = v.Version }).ToList();
            var token = _signer.Token(id, v.Version);
            return new
            {
                version = v.Version, title = v.Title, kind = v.Kind, entry = v.Entry, size = v.Size, files = v.Files, at = v.CreatedAt, publishedBy = v.PublishedBy, behalf = v.Behalf,
                postId = v.PostId, redacted = v.Redacted,
                url = v.Redacted ? null : Address(id, v.Version, v.Entry),
                @base = v.Redacted ? null : $"{_opts.Origin ?? ""}/a/{token}/",
                fileList = files.Select(f => new { path = f.Path, size = f.Size, type = f.Type }),
            };
        }).ToList();
        return new
        {
            id = a.Id, threadId = a.ThreadId, channelId = a.ChannelId, agentId = a.AgentId, path = a.Path, title = a.Title, kind = a.Kind, version = a.Version, createdAt = a.CreatedAt,
            canRedact = owner == me || Access.IsAdmin(c, me), versions,
        };
    }

    /// <summary>Security headers for anything an artifact holds. The page runs with an opaque origin (CSP sandbox, and the app's frame is sandboxed
    /// too), so it has no cookies and no storage of CoWork's; it may load only its own files and may not connect anywhere.</summary>
    private void Harden(HttpResponse r, string type)
    {
        var frameAncestors = _opts.Origin is null ? "'self'" : _opts.AppOrigin ?? "*";
        var svg = type.StartsWith("image/svg", StringComparison.Ordinal);
        // a PDF opens in the browser's own viewer, which won't run in a sandboxed document; served as a PDF (nosniff), it can't become a page
        if (type == "application/pdf") r.Headers.ContentSecurityPolicy = $"frame-ancestors {frameAncestors}";
        else r.Headers.ContentSecurityPolicy = svg
            ? "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; sandbox"
            : "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; " +
              $"media-src 'self' data: blob:; connect-src 'none'; worker-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors {frameAncestors}; sandbox allow-scripts";
        r.Headers.XContentTypeOptions = "nosniff";
        r.Headers["Referrer-Policy"] = "no-referrer";
        r.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        r.Headers.CacheControl = "private, max-age=600";
    }

    private IResult Serve(string token, string? path, HttpContext ctx)
    {
        if (_opts.Origin is { } origin && !SameHost(ctx.Request, origin)) return Results.NotFound();       // on their own host name only, once one is set
        if (_signer.Verify(token) is not { } t) return Results.NotFound();
        using var c = _db.Open();
        var v = c.QueryFirstOrDefault<VersionRow>("SELECT * FROM artifact_versions WHERE artifact_id=@Id AND version=@Version AND redacted=0", new { t.Id, t.Version });
        if (v is null) return Results.NotFound();
        var file = c.QueryFirstOrDefault<FileRow>("SELECT path, sha256, size, type FROM artifact_files WHERE artifact_id=@Id AND version=@Version AND path=@p",
            new { t.Id, t.Version, p = string.IsNullOrEmpty(path) ? v.Entry : path });
        if (file is null || !_blobs.Has(file.Sha256)) return Results.NotFound();
        // a markdown, CSV or JSON file is data here (the app shows it); only pages and their own files render
        var type = file.Type.StartsWith("text/markdown") || file.Type.StartsWith("text/csv") ? "text/plain; charset=utf-8" : file.Type;
        Harden(ctx.Response, type);
        return Results.File(_blobs.PathOf(file.Sha256), type);
    }

    static bool SameHost(HttpRequest r, string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var u) && string.Equals(r.Host.Host, u.Host, StringComparison.OrdinalIgnoreCase) && (u.IsDefaultPort || r.Host.Port == u.Port);

    /// <summary>A file's bytes for the app itself (a CSV or JSON table, a markdown document): never rendered at the app's origin.</summary>
    private IResult Raw(long id, int version, string? path, long me, HttpContext ctx)
    {
        using var c = _db.Open();
        if (Visible(c, id, me) is null) return NotFound();
        var v = c.QueryFirstOrDefault<VersionRow>("SELECT * FROM artifact_versions WHERE artifact_id=@id AND version=@version AND redacted=0", new { id, version });
        if (v is null) return NotFound();
        var file = c.QueryFirstOrDefault<FileRow>("SELECT path, sha256, size, type FROM artifact_files WHERE artifact_id=@id AND version=@version AND path=@p", new { id, version, p = string.IsNullOrEmpty(path) ? v.Entry : path });
        if (file is null || !_blobs.Has(file.Sha256)) return NotFound();
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers.ContentDisposition = "attachment";
        return Results.File(_blobs.PathOf(file.Sha256), file.Type.StartsWith("text/") || file.Type.StartsWith("application/json") ? "text/plain; charset=utf-8" : "application/octet-stream");
    }

    /// <summary>One file as itself; a page and its files as a zip.</summary>
    private IResult Download(long id, int version, long me, HttpContext ctx)
    {
        using var c = _db.Open();
        var a = Visible(c, id, me);
        if (a is null) return NotFound();
        var v = c.QueryFirstOrDefault<VersionRow>("SELECT * FROM artifact_versions WHERE artifact_id=@id AND version=@version AND redacted=0", new { id, version });
        if (v is null) return NotFound();
        var files = c.Query<FileRow>("SELECT path, sha256, size, type FROM artifact_files WHERE artifact_id=@id AND version=@version ORDER BY path", new { id, version }).ToList();
        if (files.Count == 0 || files.Any(f => !_blobs.Has(f.Sha256))) return NotFound();
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        if (files.Count == 1)
            return Results.File(_blobs.PathOf(files[0].Sha256), "application/octet-stream", Path.GetFileName(files[0].Path));
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var f in files)
            {
                var e = zip.CreateEntry(f.Path, CompressionLevel.Fastest);
                using var to = e.Open(); using var from = File.OpenRead(_blobs.PathOf(f.Sha256)); from.CopyTo(to);
            }
        ms.Position = 0;
        var slug = Regex.Replace(a.Title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return Results.File(ms, "application/zip", $"{(slug.Length == 0 ? "artifact" : slug)}-v{version}.zip");
    }

    /// <summary>Redaction removes the bytes (of one version, or of all) and keeps the fact that it was shown. The agent's owner or an admin may.</summary>
    private IResult Redact(long id, int? version, long me)
    {
        using (var c = _db.Open())
        {
            var a = Visible(c, id, me);
            if (a is null) return NotFound();
            var owner = c.ExecuteScalar<long?>("SELECT owner_id FROM agents WHERE member_id=@x", new { x = a.AgentId });
            if (owner != me && !Access.IsAdmin(c, me)) return Results.Json(new { error = "Only the agent's owner or an admin can redact it." }, statusCode: 403);
            if (version is { } vv && c.ExecuteScalar<int>("SELECT COUNT(*) FROM artifact_versions WHERE artifact_id=@id AND version=@vv", new { id, vv }) == 0) return NotFound();
            _store.Append(new[] { new NewEvent($"thread-{a.ThreadId}", "ArtifactRedacted", new { artifactId = id, version, channelId = a.ChannelId, title = a.Title }, me) });
        }
        return Results.Ok(new { ok = true });
    }

    /// <summary>The artifact a reply quotes, if it is in that thread: its title and path, for the post and for the agent.</summary>
    public static (string Title, string Path)? Quotable(SqliteConnection c, long threadId, long artifactId, int version)
    {
        var a = c.QueryFirstOrDefault<(string Title, string Path)?>("SELECT a.title, a.path FROM artifacts a JOIN artifact_versions v ON v.artifact_id=a.id AND v.version=@version WHERE a.id=@artifactId AND a.thread_id=@threadId",
            new { artifactId, version, threadId });
        return a;
    }
}

public record RedactArtifactBody(int? Version);

/// <summary>The inline projection of artifacts: the artifact, its versions and files, and the card post in the thread (every card of an artifact
/// shows its latest version).</summary>
public sealed class ArtifactProjection : IInlineProjection
{
    public void Apply(ProjectionContext x, StoredEvent e)
    {
        switch (e.Type)
        {
            case "ArtifactPublished":
            {
                var id = e.Long("artifactId"); var version = (int)e.Long("version"); var t = ThreadProjection.ThreadOf(e);
                var title = e.Str("title") ?? ""; var kind = e.Str("kind") ?? "file";
                if (version == 1)
                    x.Execute(@"INSERT INTO artifacts(id, thread_id, channel_id, project_id, agent_id, path, title, kind, version, created_at, updated_at)
                        VALUES (@id,@t,@ch,@pj,@a,@path,@title,@kind,1,@at,@at)",
                        new { id, t, ch = e.Long("channelId"), pj = e.LongOrNull("projectId"), a = e.Long("agentId"), path = e.Str("path"), title, kind, at = e.OccurredAt });
                else x.Execute("UPDATE artifacts SET version=@version, title=@title, kind=@kind, updated_at=@at WHERE id=@id", new { id, version, title, kind, at = e.OccurredAt });
                var files = e.Data.GetProperty("files").EnumerateArray().ToList();
                x.Execute(@"INSERT INTO artifact_versions(artifact_id, version, title, kind, entry, size, files, published_by, behalf, post_id, created_at)
                    VALUES (@id,@version,@title,@kind,@entry,@size,@n,@by,@behalf,@post,@at)",
                    new { id, version, title, kind, entry = e.Str("entry"), size = e.Long("size"), n = files.Count, by = e.ActorId, behalf = e.OnBehalfOf, post = e.Long("postId"), at = e.OccurredAt });
                foreach (var f in files)
                    x.Execute("INSERT INTO artifact_files(artifact_id, version, path, sha256, size, type) VALUES (@id,@version,@p,@sha,@size,@type)",
                        new { id, version, p = f.GetProperty("path").GetString(), sha = f.GetProperty("sha256").GetString(), size = f.GetProperty("size").GetInt64(), type = f.GetProperty("type").GetString() });
                var latest = Latest(x, id);
                var meta = new JsonObject { ["artifactId"] = id, ["version"] = version, ["path"] = e.Str("path"), ["open"] = e.Str("open") ?? "card", ["latest"] = latest };
                x.Execute("INSERT INTO posts(id, channel_id, thread_id, author_id, body, kind, meta, created_at, position) VALUES (@pid,@ch,@t,@a,@title,'artifact',@meta,@at,@pos)",
                    new { pid = e.Long("postId"), ch = e.Long("channelId"), t, a = e.ActorId, title, meta = meta.ToJsonString(), at = e.OccurredAt, pos = e.Position });
                Cards(x, id, e.Position, except: e.Long("postId"));
                break;
            }
            case "ArtifactRedacted":
            {
                var id = e.Long("artifactId"); var version = e.LongOrNull("version");
                x.Execute("UPDATE artifact_versions SET redacted=1 WHERE artifact_id=@id AND (@version IS NULL OR version=@version)", new { id, version });
                x.Execute("DELETE FROM artifact_files WHERE artifact_id=@id AND (@version IS NULL OR version=@version)", new { id, version });
                Cards(x, id, e.Position);
                break;
            }
            case "ChannelDeleted":
            {
                var ch = e.Long("channelId");
                x.Execute("DELETE FROM artifact_files WHERE artifact_id IN (SELECT id FROM artifacts WHERE channel_id=@ch)", new { ch });
                x.Execute("DELETE FROM artifact_versions WHERE artifact_id IN (SELECT id FROM artifacts WHERE channel_id=@ch)", new { ch });
                x.Execute("DELETE FROM artifacts WHERE channel_id=@ch", new { ch });
                break;
            }
        }
    }

    /// <summary>What every card of the artifact shows: its latest version, and which versions were redacted.</summary>
    private static JsonObject Latest(ProjectionContext x, long id)
    {
        var v = x.Query<Artifacts.VersionRow>("SELECT * FROM artifact_versions WHERE artifact_id=@id ORDER BY version DESC LIMIT 1", new { id }).First();
        var redacted = x.Query<int>("SELECT version FROM artifact_versions WHERE artifact_id=@id AND redacted=1 ORDER BY version", new { id }).ToList();
        return new JsonObject
        {
            ["version"] = v.Version, ["title"] = v.Title, ["kind"] = v.Kind, ["entry"] = v.Entry, ["size"] = v.Size, ["files"] = v.Files, ["at"] = v.CreatedAt,
            ["redacted"] = new JsonArray(redacted.Select(r => (JsonNode)r).ToArray()),
        };
    }

    private static void Cards(ProjectionContext x, long id, long position, long? except = null)
    {
        var latest = Latest(x, id).ToJsonString();
        x.Execute("UPDATE posts SET meta=json_set(meta,'$.latest',json(@latest)), position=@position WHERE kind='artifact' AND json_extract(meta,'$.artifactId')=@id AND (@except IS NULL OR id<>@except)",
            new { latest, position, id, except });
    }

    /// <summary>The cards of an artifact (for live updates when a new version or a redaction changes them).</summary>
    public static IEnumerable<long> CardsOf(SqliteConnection c, long artifactId) =>
        c.Query<long>("SELECT id FROM posts WHERE kind='artifact' AND deleted=0 AND json_extract(meta,'$.artifactId')=@artifactId", new { artifactId });
}
