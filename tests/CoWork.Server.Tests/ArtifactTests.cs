using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoWork.Contracts;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace CoWork.Server.Tests;

/// <summary>Brief 9: artifacts. An agent shows a file from its workspace with <c>cowork_show</c>; its runner uploads it (a page with the files
/// beside it); the thread gets a card; people who see the channel get it at a short-lived signed address with a strict policy; the same path is
/// the next version; a reply under the canvas quotes it; redaction removes the bytes and keeps the fact; deleting the channel removes it all.</summary>
public class ArtifactTests
{
    static async Task Until(Func<bool> done, string what, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done()) { if (DateTime.UtcNow > end) throw new Xunit.Sdk.XunitException("timed out waiting for " + what); await Task.Delay(25); }
    }

    static async Task<(string Text, bool Error)> Tool(TestApp app, string token, string name, object args, long? thread = null)
    {
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (thread is not null) http.DefaultRequestHeaders.Add("X-Cowork-Thread", thread.ToString());
        var res = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = args } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        return ((string)body["result"]!["content"]![0]!["text"]!, (bool)body["result"]!["isError"]!);
    }

    /// <summary>What a runner sends for a cowork_show call: the manifest and the files as file0, file1 ….</summary>
    static async Task<HttpResponseMessage> Upload(TestApp app, string token, long thread, string requestId, string path, string entry, params (string Path, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(new { path, entry, files = files.Select(f => new { path = f.Path, size = f.Bytes.LongLength }) })), "manifest");
        for (var i = 0; i < files.Length; i++) form.Add(new ByteArrayContent(files[i].Bytes), "file" + i, "file" + i);
        return await Send(app, token, thread, requestId, form);
    }

    static async Task<HttpResponseMessage> UploadError(TestApp app, string token, long thread, string requestId, string error)
    {
        var form = new MultipartFormDataContent { { new StringContent(JsonSerializer.Serialize(new { error })), "manifest" } };
        return await Send(app, token, thread, requestId, form);
    }

    static async Task<HttpResponseMessage> Send(TestApp app, string token, long thread, string requestId, MultipartFormDataContent form)
    {
        var http = app.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/runner/artifacts/{requestId}") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Cowork-Thread", thread.ToString());
        return await http.SendAsync(req);
    }

    static async Task<CollectArtifact> Next(System.Threading.Channels.Channel<CollectArtifact> ch)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await ch.Reader.ReadAsync(cts.Token);
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    static string Page(string extra = "") => $"<!doctype html><html><head><title>Tax APIs compared</title><link rel=stylesheet href=style.css></head><body><h1>Tax APIs</h1>{extra}<img src=img/chart.png></body></html>";

    /// <summary>researcher shows a page from its sandbox: the tool asks the sandbox's runner, which uploads the page and its folder.</summary>
    static async Task<(string Text, bool Error)> ShowPage(SandboxTests.World w, FakeSandbox box, string token, string extra = "", string? title = null)
    {
        var call = Tool(w.App, token, "cowork_show", title is null ? new { path = "reports/tax-apis/index.html" } : new { path = "reports/tax-apis/index.html", title });
        var ask = await Next(box.Collects);
        Assert.Equal(("reports/tax-apis/index.html", box.Spec.ThreadId), (ask.Path, ask.ThreadId));
        var up = await Upload(w.App, token, box.Spec.ThreadId, ask.RequestId, "reports/tax-apis/index.html", "index.html",
            ("index.html", Utf8(Page(extra))), ("style.css", Utf8("h1{color:teal}")), ("img/chart.png", Png));
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        return await call;
    }

    [Fact]
    public async Task A_sandbox_shows_a_page_people_in_the_channel_open_it_at_a_signed_address_and_the_same_path_is_the_next_version()
    {
        await using var w = await SandboxTests.Setup();
        var thread = (await w.Rosa.Post(w.Channel, "@researcher a report I can share with finance")).GetProperty("id").GetInt64();
        var create = await w.Pool.Next<CreateSandbox>();
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Running);
        await using var box = await FakeSandbox.Connect(w.App, create.Spec);
        await box.NextDelivery();
        var token = create.Spec.ThreadToken;
        await box.Say("Here is the report.");

        var (text, error) = await ShowPage(w, box, token);
        Assert.False(error, text);
        Assert.StartsWith("Showing \"Tax APIs compared\" (artifact 1, version 1,", text);
        Assert.Contains("3 files", text);

        long artifact; JsonElement card;
        using (var c = w.App.Sql())
        {
            artifact = c.ExecuteScalar<long>("SELECT id FROM artifacts WHERE thread_id=@thread AND path='reports/tax-apis/index.html'", new { thread });
            var ev = c.QueryFirst<(string Data, long Actor, long Behalf)>("SELECT data, actor_id, on_behalf_of FROM events WHERE type='ArtifactPublished'");
            Assert.Equal((w.ResearcherId, w.Rosa.Id), (ev.Actor, ev.Behalf));
            var data = JsonDocument.Parse(ev.Data).RootElement;
            Assert.Equal(("html", 1, "canvas", "index.html"), (data.GetProperty("kind").GetString(), data.GetProperty("version").GetInt32(), data.GetProperty("open").GetString(), data.GetProperty("entry").GetString()));
            Assert.Equal(w.ProjectId, data.GetProperty("projectId").GetInt64());
            // the bytes are named by SHA-256, never carried in the event
            var files = data.GetProperty("files").EnumerateArray().ToList();
            Assert.Equal(new[] { "index.html", "style.css", "img/chart.png" }, files.Select(f => f.GetProperty("path").GetString()));
            Assert.All(files, f => Assert.True(File.Exists(Path.Combine(w.App.ArtifactsDir, f.GetProperty("sha256").GetString()![..2], f.GetProperty("sha256").GetString()!))));
            Assert.DoesNotContain("Tax APIs</h1>", ev.Data);
            // the card: in the thread, by researcher, after its words
            var post = c.QueryFirst<(long Id, long Author, string Body, string Meta, long Thread)>("SELECT id, author_id, body, meta, thread_id FROM posts WHERE kind='artifact'");
            Assert.Equal((w.ResearcherId, "Tax APIs compared", thread), (post.Author, post.Body, post.Thread));
            card = JsonDocument.Parse(post.Meta).RootElement;
            Assert.Equal((artifact, 1, 1), (card.GetProperty("artifactId").GetInt64(), card.GetProperty("version").GetInt32(), card.GetProperty("latest").GetProperty("version").GetInt32()));
            Assert.True(post.Id > c.ExecuteScalar<long>("SELECT MIN(id) FROM posts WHERE thread_id=@thread AND kind='text'", new { thread }));
        }

        // Rosa sees it; the address is signed and short-lived, and serves the page with a policy that keeps it away from CoWork
        var a = await w.Rosa.Http.GetFromJsonAsync<JsonElement>($"/api/artifacts/{artifact}");
        Assert.Equal(("Tax APIs compared", "html", 1, thread), (a.GetProperty("title").GetString(), a.GetProperty("kind").GetString(), a.GetProperty("version").GetInt32(), a.GetProperty("threadId").GetInt64()));
        var v1 = a.GetProperty("versions")[0];
        var url = v1.GetProperty("url").GetString()!;
        Assert.Matches(@"^/a/1-1-\d+-[\w-]{32}/index\.html$", url);
        var anon = w.App.CreateClient();          // the frame has no cookies: the address alone opens it
        var page = await anon.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp); Assert.Contains("connect-src 'none'", csp); Assert.Contains("frame-ancestors 'self'", csp); Assert.EndsWith("sandbox allow-scripts", csp);
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        var served = await page.Content.ReadAsStringAsync();
        Assert.Contains("Tax APIs</h1>", served);
        // brief 13: the page gets the script that follows CoWork's light or dark, just before </body>; its other files don't, nor does the app's copy
        Assert.EndsWith("</script>\n</body></html>", served);
        Assert.Contains("<img src=img/chart.png><script data-cowork=\"scheme\">", served);
        var css = await anon.GetAsync(v1.GetProperty("base").GetString() + "style.css");
        Assert.Equal(("text/css", "h1{color:teal}"), (css.Content.Headers.ContentType!.MediaType, await css.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(v1.GetProperty("base").GetString() + "img/chart.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(v1.GetProperty("base").GetString() + "nothing.js")).StatusCode);
        // a forged or expired address is a 404
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(url.Replace("/a/1-1-", "/a/1-2-"))).StatusCode);
        var signer = w.App.Services.GetRequiredService<ArtifactSigner>();
        var old = signer.Token(artifact, 1, DateTimeOffset.UtcNow.AddHours(-2));
        Assert.Null(signer.Verify(old));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/a/{old}/index.html")).StatusCode);

        // Jo isn't in the project, so not in #checkout-app: 404 for the artifact, its files and its download
        var jo = await w.App.Join(w.Alex, "jo", "Jo");
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.GetAsync($"/api/artifacts/{artifact}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.GetAsync($"/api/artifacts/{artifact}/versions/1/raw/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.GetAsync($"/api/artifacts/{artifact}/versions/1/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.GetAsync($"/api/artifacts/999")).StatusCode);
        // the app reads a file's text without it ever rendering at the app's origin
        var raw = await w.Rosa.Http.GetAsync($"/api/artifacts/{artifact}/versions/1/raw/style.css");
        Assert.Equal(("text/plain", "attachment"), (raw.Content.Headers.ContentType!.MediaType, raw.Content.Headers.ContentDisposition!.DispositionType));
        Assert.Equal(Page(), await (await w.Rosa.Http.GetAsync($"/api/artifacts/{artifact}/versions/1/raw/index.html")).Content.ReadAsStringAsync());

        // Rosa replies under the canvas: her reply quotes it, and researcher hears which artifact and version, and where it is
        await box.End();
        await Until(() => w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='TurnEnded'") == 1, "the turn");
        var reply = await w.Rosa.Http.PostAsJsonAsync($"/api/channels/{w.Channel}/posts", new { body = "add the cost for 50,000 refunds a month", threadId = thread, quote = new { artifactId = artifact, version = 1 } });
        reply.EnsureSuccessStatusCode();
        var replied = await reply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Tax APIs compared", 1), (replied.GetProperty("meta").GetProperty("quote").GetProperty("title").GetString(), replied.GetProperty("meta").GetProperty("quote").GetProperty("version").GetInt32()));
        var heard = await box.NextDelivery();
        Assert.Contains("artifact=\"Tax APIs compared\" version=\"1\" path=\"reports/tax-apis/index.html\" about=\"a reply about the artifact you showed from reports/tax-apis/index.html; if you change it, call cowork_show with that path again so people see the new version\">add the cost for 50,000 refunds a month</cowork-message>", heard.Text);
        // a quote of an artifact from another thread is refused
        var elsewhere = (await w.Rosa.Post(w.Channel, "a note")).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rosa.Http.PostAsJsonAsync($"/api/channels/{w.Channel}/posts", new { body = "this?", threadId = elsewhere, quote = new { artifactId = artifact, version = 1 } })).StatusCode);

        // the same path again: version 2. Both cards show v2, and v1 is still there, as it was
        var (again, againErr) = await ShowPage(w, box, token, "<p>50,000 refunds a month: €40</p>");
        Assert.False(againErr, again);
        Assert.StartsWith("Showing \"Tax APIs compared\" (artifact 1, version 2,", again);
        using (var c = w.App.Sql())
        {
            var cards = c.Query<string>("SELECT meta FROM posts WHERE kind='artifact' ORDER BY id").Select(m => JsonDocument.Parse(m).RootElement).ToList();
            Assert.Equal(new[] { 1, 2 }, cards.Select(x => x.GetProperty("version").GetInt32()));
            Assert.All(cards, x => Assert.Equal(2, x.GetProperty("latest").GetProperty("version").GetInt32()));
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM artifacts"));
        }
        a = await w.Rosa.Http.GetFromJsonAsync<JsonElement>($"/api/artifacts/{artifact}");
        Assert.Equal(new[] { 2, 1 }, a.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        Assert.Contains("50,000 refunds", await anon.GetStringAsync(a.GetProperty("versions")[0].GetProperty("url").GetString()));
        Assert.DoesNotContain("50,000 refunds", await anon.GetStringAsync(a.GetProperty("versions")[1].GetProperty("url").GetString()));
        // reading the thread, an agent sees what was shown in it
        var (read, _) = await Tool(w.App, token, "cowork_read_channel", new { threadId = thread });
        Assert.Contains("researcher: (showed “Tax APIs compared” v1, from reports/tax-apis/index.html; artifact 1)", read);
        Assert.Contains("researcher: (showed “Tax APIs compared” v2, from reports/tax-apis/index.html; artifact 1)", read);
        // the download: a page and its files as a zip
        var zip = await w.Rosa.Http.GetAsync($"/api/artifacts/{artifact}/versions/2/download");
        Assert.Equal("tax-apis-compared-v2.zip", zip.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        using (var z = new ZipArchive(await zip.Content.ReadAsStreamAsync()))
            Assert.Equal(new[] { "img/chart.png", "index.html", "style.css" }, z.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));

        // every version and who published it is in the audit
        await w.App.Services.GetRequiredService<ProjectionRunner>().CatchUpAsync();
        var audit = w.App.Sql().Query<string>("SELECT sentence FROM audit_entries WHERE sentence LIKE '%showed%' ORDER BY id").ToList();
        Assert.Equal(2, audit.Count);
        Assert.StartsWith("researcher (Alex's agent) showed “Tax APIs compared” v1 (html,", audit[0]);
        Assert.EndsWith("in #checkout-app, for Rosa.", audit[0]);

        // the tables rebuild from the events to the same rows
        var before = RoundTwoTests.Dump(w.App.DbPath);
        w.App.Services.GetRequiredService<IEventStore>().RebuildProjections();
        Assert.Equal(before, RoundTwoTests.Dump(w.App.DbPath));
    }

    [Fact]
    public async Task A_pinned_agent_shows_from_its_worktree_and_what_cant_be_shown_says_why()
    {
        await using var w = await SandboxTests.Setup();
        await using var reviewer = await FakeRunner.Enrol(w.App, w.Alex, "reviewer");
        (await w.Alex.Http.PutAsync($"/api/channels/{w.Channel}/members/{reviewer.AgentId}", null)).EnsureSuccessStatusCode();
        await reviewer.Connect();
        var thread = (await w.Alex.Post(w.Channel, "@reviewer show me the providers as a table")).GetProperty("id").GetInt64();
        await reviewer.NextDelivery();

        // the runner can't read it: the agent hears why
        var call = Tool(w.App, reviewer.Token, "cowork_show", new { path = "data/providers.csv", open = "card" }, thread);
        var ask = await Next(reviewer.Collects);
        (await UploadError(w.App, reviewer.Token, thread, ask.RequestId, "There's no file at data/providers.csv in your workspace. Write it first, then call cowork_show.")).EnsureSuccessStatusCode();
        Assert.Equal(("There's no file at data/providers.csv in your workspace. Write it first, then call cowork_show.", true), await call);

        // with its runner's token, for its own thread: a CSV, only the card
        call = Tool(w.App, reviewer.Token, "cowork_show", new { path = "data/providers.csv", open = "card" }, thread);
        ask = await Next(reviewer.Collects);
        // someone else's token, or another request id, can't answer it
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(w.App, w.Pool.Token, thread, ask.RequestId, "data/providers.csv", "providers.csv", ("providers.csv", Utf8("a,b")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(w.App, reviewer.Token, thread, "nope", "data/providers.csv", "providers.csv", ("providers.csv", Utf8("a,b")))).StatusCode);
        (await Upload(w.App, reviewer.Token, thread, ask.RequestId, "data/providers.csv", "providers.csv", ("providers.csv", Utf8("Provider,Partial refunds\nTallyrate,per line\n")))).EnsureSuccessStatusCode();
        var (text, err) = await call;
        Assert.False(err, text);
        Assert.Contains("\"providers.csv\"", text);
        using (var c = w.App.Sql())
        {
            var meta = JsonDocument.Parse(c.ExecuteScalar<string>("SELECT meta FROM posts WHERE kind='artifact'")!).RootElement;
            Assert.Equal(("card", "csv"), (meta.GetProperty("open").GetString(), meta.GetProperty("latest").GetProperty("kind").GetString()));
            Assert.Equal((reviewer.AgentId, w.Alex.Id), c.QueryFirst<(long, long)>("SELECT actor_id, on_behalf_of FROM events WHERE type='ArtifactPublished'"));
        }
        // a CSV is data on its signed address: never a page
        var a = await w.Rosa.Http.GetFromJsonAsync<JsonElement>("/api/artifacts/1");
        var served = await w.App.CreateClient().GetAsync(a.GetProperty("versions")[0].GetProperty("url").GetString());
        Assert.Equal("text/plain", served.Content.Headers.ContentType!.MediaType);

        // an unsafe path, too much, or nothing: refused, and the agent hears why
        call = Tool(w.App, reviewer.Token, "cowork_show", new { path = "x/index.html" }, thread);
        ask = await Next(reviewer.Collects);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(w.App, reviewer.Token, thread, ask.RequestId, "x/index.html", "index.html", ("index.html", Utf8("hi")), ("../secret", Utf8("no")))).StatusCode);
        Assert.Equal(("The runner's manifest names an unsafe path.", true), await call);
        call = Tool(w.App, reviewer.Token, "cowork_show", new { path = "big/index.html" }, thread);
        ask = await Next(reviewer.Collects);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(w.App, reviewer.Token, thread, ask.RequestId, "big/index.html", "index.html", ("index.html", Utf8("hi")), ("video.bin", new byte[ArtifactLimits.MaxBytes]))).StatusCode);
        Assert.StartsWith("big/index.html and the files beside it are more than 25 MB", (await call).Text);
        Assert.Equal(("Give the path of the file to show, in your workspace.", true), await Tool(w.App, reviewer.Token, "cowork_show", new { }, thread));
        // the refused calls left no bytes behind once anything sweeps
        Assert.Equal(1, w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM artifact_versions"));
    }

    [Fact]
    public async Task Redaction_removes_the_bytes_and_keeps_the_fact_and_deleting_the_channel_removes_the_rest()
    {
        await using var w = await SandboxTests.Setup();
        var thread = (await w.Rosa.Post(w.Channel, "@researcher a report")).GetProperty("id").GetInt64();
        var create = await w.Pool.Next<CreateSandbox>();
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Running);
        await using var box = await FakeSandbox.Connect(w.App, create.Spec);
        await box.NextDelivery();
        Assert.False((await ShowPage(w, box, create.Spec.ThreadToken)).Error);
        Assert.False((await ShowPage(w, box, create.Spec.ThreadToken, "<p>v2</p>")).Error);
        var a = await w.Rosa.Http.GetFromJsonAsync<JsonElement>("/api/artifacts/1");
        var v1url = a.GetProperty("versions")[1].GetProperty("url").GetString()!;
        Assert.False(a.GetProperty("canRedact").GetBoolean());          // Rosa neither owns researcher nor is an admin
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.PostAsJsonAsync("/api/artifacts/1/redact", new { version = 1 })).StatusCode);
        string Sha(string path, int v) => w.App.Sql().ExecuteScalar<string>("SELECT sha256 FROM artifact_files WHERE artifact_id=1 AND version=@v AND path=@path", new { v, path })!;
        var v1page = Sha("index.html", 1); var shared = Sha("style.css", 1);
        Assert.Equal(shared, Sha("style.css", 2));             // the same bytes are kept once

        (await w.Alex.Http.PostAsJsonAsync("/api/artifacts/1/redact", new { version = 1 })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await w.App.CreateClient().GetAsync(v1url)).StatusCode);
        Assert.False(File.Exists(Path.Combine(w.App.ArtifactsDir, v1page[..2], v1page)));
        Assert.True(File.Exists(Path.Combine(w.App.ArtifactsDir, shared[..2], shared)));          // v2 still names it
        using (var c = w.App.Sql())
        {
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT redacted FROM artifact_versions WHERE artifact_id=1 AND version=1"));
            Assert.Equal(2, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='ArtifactPublished'"));        // the fact stays
            Assert.All(c.Query<string>("SELECT meta FROM posts WHERE kind='artifact'"), m => Assert.Equal("[1]", JsonDocument.Parse(m).RootElement.GetProperty("latest").GetProperty("redacted").GetRawText()));
        }
        a = await w.Rosa.Http.GetFromJsonAsync<JsonElement>("/api/artifacts/1");
        Assert.True(a.GetProperty("versions")[1].GetProperty("redacted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("versions")[1].GetProperty("url").ValueKind);
        await w.App.Services.GetRequiredService<ProjectionRunner>().CatchUpAsync();
        Assert.Contains("Alex redacted v1 of “Tax APIs compared” in #checkout-app. Its files are gone; that it was shown stays.", w.App.Sql().Query<string>("SELECT sentence FROM audit_entries"));

        // the channel goes: so do the artifact and every file
        (await w.Alex.Http.DeleteAsync($"/api/channels/{w.Channel}")).EnsureSuccessStatusCode();
        Assert.Equal(0, w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM artifacts") + w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM artifact_files"));
        Assert.Empty(Directory.EnumerateFiles(w.App.ArtifactsDir, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith('.')));
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rosa.Http.GetAsync("/api/artifacts/1")).StatusCode);
    }

    [Fact]
    public async Task With_an_artifact_origin_the_addresses_are_on_that_host_only_and_only_the_app_may_frame_them()
    {
        Environment.SetEnvironmentVariable("COWORK_ARTIFACT_ORIGIN", "https://artifacts.cowork.example");
        Environment.SetEnvironmentVariable("COWORK_APP_ORIGIN", "https://cowork.lumen.example");
        SandboxTests.World w;
        try { w = await SandboxTests.Setup(); }
        finally { Environment.SetEnvironmentVariable("COWORK_ARTIFACT_ORIGIN", null); Environment.SetEnvironmentVariable("COWORK_APP_ORIGIN", null); }
        await using var _ = w;
        var thread = (await w.Rosa.Post(w.Channel, "@researcher a report")).GetProperty("id").GetInt64();
        var create = await w.Pool.Next<CreateSandbox>();
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Running);
        await using var box = await FakeSandbox.Connect(w.App, create.Spec);
        await box.NextDelivery();
        Assert.False((await ShowPage(w, box, create.Spec.ThreadToken)).Error);
        var url = (await w.Rosa.Http.GetFromJsonAsync<JsonElement>("/api/artifacts/1")).GetProperty("versions")[0].GetProperty("url").GetString()!;
        Assert.StartsWith("https://artifacts.cowork.example/a/1-1-", url);
        var path = new Uri(url).PathAndQuery;
        var http = w.App.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode);          // not on the app's own host name
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Host = "artifacts.cowork.example";
        var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("frame-ancestors https://cowork.lumen.example;", res.Headers.GetValues("Content-Security-Policy").Single());
    }
}
