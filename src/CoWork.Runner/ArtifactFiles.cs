using System.Net.Http.Headers;
using System.Text.Json;
using CoWork.Contracts;

namespace CoWork.Runner;

/// <summary>
/// What <c>cowork_show</c> uploads (brief 9): the file the agent named, in the thread's workspace, and for a page (HTML or markdown) the folder it is
/// in, so its CSS, scripts and images come with it. Never anything outside the workspace (symlinks are followed and checked), nothing hidden
/// (".git", ".env") and no node_modules; at most 25 MB and 500 files. A page at the workspace's top level brings only itself: the whole repository
/// isn't its folder.
/// </summary>
public static class ArtifactFiles
{
    public sealed record Picked(string Path, string Entry, IReadOnlyList<(string Rel, string Full, long Size)> Files);

    public static (Picked? Picked, string? Error) Pick(string workspace, string path)
    {
        var root = System.IO.Path.GetFullPath(workspace).TrimEnd('/');
        string full;
        try { full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(root, path)); }
        catch { return (null, $"{path} isn't a path in your workspace."); }
        if (!Inside(root, full)) return (null, $"{path} isn't in your workspace ({root}); cowork_show only shows files there.");
        if (Directory.Exists(full)) return (null, $"{path} is a folder; give the file to show (like {path.TrimEnd('/')}/index.html).");
        if (!File.Exists(full)) return (null, $"There's no file at {path} in your workspace. Write it first, then call cowork_show.");
        var realRoot = Real(root);
        if (!Inside(realRoot, Real(full))) return (null, $"{path} points outside your workspace; cowork_show only shows files there.");
        var rel = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
        if (rel.Split('/').Any(s => s.StartsWith('.'))) return (null, $"{path} is hidden; cowork_show doesn't show hidden files.");
        var folder = System.IO.Path.GetDirectoryName(full)!;
        var kind = System.IO.Path.GetExtension(full).ToLowerInvariant();
        var page = kind is ".html" or ".htm" or ".md" or ".markdown";
        var files = new List<(string Rel, string Full, long Size)>();
        if (page && folder.TrimEnd('/') != root)
        {
            foreach (var f in Walk(folder, realRoot))
            {
                files.Add((System.IO.Path.GetRelativePath(folder, f).Replace('\\', '/'), f, new FileInfo(f).Length));
                if (files.Count > ArtifactLimits.MaxFiles) return (null, $"The folder {System.IO.Path.GetRelativePath(root, folder)} has more than {ArtifactLimits.MaxFiles} files; cowork_show takes at most that. Put the page in a folder of its own.");
            }
        }
        else files.Add((System.IO.Path.GetFileName(full), full, new FileInfo(full).Length));
        var total = files.Sum(f => f.Size);
        if (total > ArtifactLimits.MaxBytes)
            return (null, $"{(files.Count > 1 ? $"The folder {System.IO.Path.GetRelativePath(root, folder)} is" : $"{path} is")} {total / 1024d / 1024d:0.0} MB; cowork_show takes at most {ArtifactLimits.MaxBytes / 1024 / 1024} MB.{(files.Count > 1 ? " Put the page in a folder of its own." : "")}");
        var entry = files.Count > 1 || page && folder.TrimEnd('/') != root ? System.IO.Path.GetRelativePath(folder, full).Replace('\\', '/') : System.IO.Path.GetFileName(full);
        return (new Picked(rel, entry, files), null);
    }

    static IEnumerable<string> Walk(string dir, string realRoot)
    {
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(f);
            if (name.StartsWith('.')) continue;
            if (!Inside(realRoot, Real(f))) continue;           // a link out of the workspace isn't followed
            yield return f;
        }
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(d);
            if (name.StartsWith('.') || name == "node_modules") continue;
            if (new DirectoryInfo(d).LinkTarget is not null) continue;
            foreach (var f in Walk(d, realRoot)) yield return f;
        }
    }

    static bool Inside(string root, string p) => p == root || p.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>The path with every symlink on the way resolved.</summary>
    static string Real(string p)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(p);
            var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cur = "/";
            foreach (var part in parts)
            {
                var next = System.IO.Path.Combine(cur, part);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                for (var hops = 0; info.LinkTarget is { } target && hops < 32; hops++)
                {
                    next = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(target) ? target : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(next)!, target));
                    info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                }
                cur = next;
            }
            return cur;
        }
        catch { return p; }
    }

    /// <summary>Uploads what was picked (or why nothing was) for one cowork_show call: a manifest and the files as file0, file1 … .</summary>
    public static async Task Upload(HttpClient http, string server, string token, long threadId, string requestId, Picked? picked, string? error, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var manifest = picked is null
            ? JsonSerializer.Serialize(new { error })
            : JsonSerializer.Serialize(new { path = picked.Path, entry = picked.Entry, files = picked.Files.Select(f => new { path = f.Rel, size = f.Size }) });
        form.Add(new StringContent(manifest), "manifest");
        var streams = new List<Stream>();
        try
        {
            if (picked is not null)
                for (var i = 0; i < picked.Files.Count; i++)
                {
                    var s = File.OpenRead(picked.Files[i].Full); streams.Add(s);
                    var part = new StreamContent(s); part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(part, "file" + i, "file" + i);
                }
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{server.TrimEnd('/')}/api/runner/artifacts/{requestId}") { Content = form };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("X-Cowork-Thread", threadId.ToString());
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"the server answered {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync(ct)}");
        }
        finally { foreach (var s in streams) s.Dispose(); }
    }
}
