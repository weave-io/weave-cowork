using System.Net;
using System.Text.Json;
using CoWork.Contracts;

namespace CoWork.Runner.Tests;

/// <summary>What cowork_show uploads from a workspace (brief 9): a page and its folder, a single file otherwise, never anything outside.</summary>
public class ArtifactFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cowork-artifact-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _ws;
    public ArtifactFilesTests() { _ws = Path.Combine(_root, "ws"); Directory.CreateDirectory(_ws); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string Write(string rel, string text = "x")
    {
        var p = Path.Combine(_ws, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void A_page_brings_its_folder_but_nothing_hidden_and_no_node_modules()
    {
        Write("reports/tax-apis/index.html", "<title>T</title>");
        Write("reports/tax-apis/style.css"); Write("reports/tax-apis/img/chart.png");
        Write("reports/tax-apis/.env", "SECRET=1"); Write("reports/tax-apis/node_modules/x/index.js"); Write("reports/tax-apis/.cache/a");
        Write("reports/other.html");         // beside the folder, not in it
        var (picked, error) = ArtifactFiles.Pick(_ws, "reports/tax-apis/index.html");
        Assert.Null(error);
        Assert.Equal(("reports/tax-apis/index.html", "index.html"), (picked!.Path, picked.Entry));
        Assert.Equal(new[] { "index.html", "style.css", "img/chart.png" }, picked.Files.Select(f => f.Rel));
        // the same, asked for with an absolute path inside the workspace or a roundabout relative one
        Assert.Equal(picked.Files.Select(f => f.Rel), ArtifactFiles.Pick(_ws, Path.Combine(_ws, "reports/tax-apis/index.html")).Picked!.Files.Select(f => f.Rel));
        Assert.Equal("reports/tax-apis/index.html", ArtifactFiles.Pick(_ws, "./reports/../reports/tax-apis/index.html").Picked!.Path);
    }

    [Fact]
    public void A_page_at_the_top_and_anything_that_isnt_a_page_bring_only_themselves()
    {
        Write("README.md", "# Read me"); Write("src/tax.ts"); Write("data/providers.csv", "a,b"); Write("data/other.csv");
        var (top, _) = ArtifactFiles.Pick(_ws, "README.md");
        Assert.Equal(new[] { "README.md" }, top!.Files.Select(f => f.Rel));
        Assert.Equal("README.md", top.Entry);
        var (csv, _) = ArtifactFiles.Pick(_ws, "data/providers.csv");
        Assert.Equal(("data/providers.csv", "providers.csv"), (csv!.Path, csv.Entry));
        Assert.Equal(new[] { "providers.csv" }, csv.Files.Select(f => f.Rel));
    }

    [Fact]
    public void Nothing_outside_the_workspace_and_nothing_missing_hidden_or_too_big()
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "no");
        Write("ok/index.html");
        Assert.Contains("isn't in your workspace", ArtifactFiles.Pick(_ws, "../secret.txt").Error);
        Assert.Contains("isn't in your workspace", ArtifactFiles.Pick(_ws, "/etc/hostname").Error);
        File.CreateSymbolicLink(Path.Combine(_ws, "link.txt"), Path.Combine(_root, "secret.txt"));
        Assert.Contains("points outside your workspace", ArtifactFiles.Pick(_ws, "link.txt").Error);
        File.CreateSymbolicLink(Path.Combine(_ws, "ok", "leak.txt"), Path.Combine(_root, "secret.txt"));     // a link beside a page isn't followed out
        Assert.Equal(new[] { "index.html" }, ArtifactFiles.Pick(_ws, "ok/index.html").Picked!.Files.Select(f => f.Rel));
        Assert.Contains("There's no file at nope.html", ArtifactFiles.Pick(_ws, "nope.html").Error);
        Assert.Contains("is a folder", ArtifactFiles.Pick(_ws, "ok").Error);
        Write(".env", "SECRET=1");
        Assert.Contains("is hidden", ArtifactFiles.Pick(_ws, ".env").Error);
        Write("big/index.html");
        using (var f = File.Create(Path.Combine(_ws, "big", "video.bin"))) f.SetLength(ArtifactLimits.MaxBytes + 1);
        var big = ArtifactFiles.Pick(_ws, "big/index.html").Error;
        Assert.StartsWith("The folder big is 25.0 MB; cowork_show takes at most 25 MB.", big);
    }

    [Fact]
    public async Task A_file_outside_the_workspace_gets_an_answer_that_says_exactly_where_to_write_it()
    {
        // brief 10: in the user's run an agent wrote its report to /tmp/opencode/… and called cowork_show on it
        var ws = Path.GetFullPath(_ws);
        var answer = ArtifactFiles.Pick(_ws, "/tmp/opencode/tax-report.html").Error;
        Assert.Equal($"/tmp/opencode/tax-report.html isn't in your workspace ({ws}); cowork_show only shows files inside it. " +
            $"Write the file inside your workspace ({ws}), for example {ws}/reports/tax-report.html, then call cowork_show with that path.", answer);
        Assert.EndsWith($"for example {ws}/reports/secret.txt, then call cowork_show with that path.", ArtifactFiles.Pick(_ws, "../secret.txt").Error);
        // and the agent hears it as the tool's answer: the runner sends it in place of the files
        var seen = new Recorder();
        await ArtifactFiles.Upload(new HttpClient(seen), "http://cowork.test", "tok-1", 42, "req-11", null, answer);
        Assert.Equal(answer, JsonDocument.Parse(seen.Parts["manifest"]).RootElement.GetProperty("error").GetString());
        // following it works on the next call
        Write("reports/tax-report.html", "<title>Tax</title>");
        Assert.Null(ArtifactFiles.Pick(_ws, $"{ws}/reports/tax-report.html").Error);
    }

    [Fact]
    public async Task The_upload_carries_a_manifest_the_files_and_the_runners_token_and_thread()
    {
        Write("reports/r/index.html", "<p>hi</p>"); Write("reports/r/a.css", "p{}");
        var (picked, _) = ArtifactFiles.Pick(_ws, "reports/r/index.html");
        var seen = new Recorder();
        await ArtifactFiles.Upload(new HttpClient(seen), "http://cowork.test/", "tok-1", 42, "req-9", picked, null);
        Assert.Equal("http://cowork.test/api/runner/artifacts/req-9", seen.Url);
        Assert.Equal(("Bearer tok-1", "42"), (seen.Auth, seen.Thread));
        var manifest = JsonDocument.Parse(seen.Parts["manifest"]).RootElement;
        Assert.Equal(("reports/r/index.html", "index.html"), (manifest.GetProperty("path").GetString(), manifest.GetProperty("entry").GetString()));
        Assert.Equal(new[] { "a.css", "index.html" }, manifest.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
        Assert.Equal(("p{}", "<p>hi</p>"), (seen.Parts["file0"], seen.Parts["file1"]));
        // why nothing could be sent goes the same way
        await ArtifactFiles.Upload(new HttpClient(seen), "http://cowork.test", "tok-1", 42, "req-10", null, "There's no file at x.");
        Assert.Equal("There's no file at x.", JsonDocument.Parse(seen.Parts["manifest"]).RootElement.GetProperty("error").GetString());
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public string? Url, Auth, Thread; public Dictionary<string, string> Parts = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Url = r.RequestUri!.ToString(); Auth = r.Headers.Authorization!.ToString(); Thread = r.Headers.GetValues("X-Cowork-Thread").Single();
            Parts.Clear();
            foreach (var part in (MultipartFormDataContent)r.Content!)
                Parts[part.Headers.ContentDisposition!.Name!.Trim('"')] = await part.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
