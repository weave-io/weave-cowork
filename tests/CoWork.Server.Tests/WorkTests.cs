using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace CoWork.Server.Tests;

/// <summary>Brief 7, round 3: threads as work. CoWork's MCP tools (read a channel, search, resolve the thread) inside the agent's boundary,
/// a thread's status on its post and in the thread, outcomes in search, and a done thread's volume deleted after the retention time.</summary>
public class WorkTests
{
    static async Task Until(Func<bool> done, string what, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done()) { if (DateTime.UtcNow > end) throw new Xunit.Sdk.XunitException("timed out waiting for " + what); await Task.Delay(25); }
    }

    /// <summary>A JSON-RPC call to /mcp as a client would make it.</summary>
    static async Task<(HttpStatusCode Status, JsonNode? Body)> Mcp(TestApp app, string token, string method, object? @params = null, long? thread = null)
    {
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (thread is not null) http.DefaultRequestHeaders.Add("X-Cowork-Thread", thread.ToString());
        var res = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params });
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    static async Task<(string Text, bool Error)> Tool(TestApp app, string token, string name, object args, long? thread = null)
    {
        var (status, body) = await Mcp(app, token, "tools/call", new { name, arguments = args }, thread);
        Assert.Equal(HttpStatusCode.OK, status);
        return ((string)body!["result"]!["content"]![0]!["text"]!, (bool)body["result"]!["isError"]!);
    }

    static JsonElement? StatusOf(JsonElement post) => post.TryGetProperty("threadStatus", out var s) && s.ValueKind == JsonValueKind.Object ? s : null;

    [Fact]
    public async Task A_sandbox_reads_its_channel_and_searches_inside_its_boundary_and_resolving_shows_the_outcome_everywhere()
    {
        await using var w = await SandboxTests.Setup();
        // what's said in the channel; and a private #leads researcher isn't in
        await w.Sam.Post(w.Channel, "Finance wants partial refunds by January. Our tax step rounds per line.");
        var leads = await w.Alex.Channel("leads", priv: true);
        await w.Alex.Post(leads, "The Q4 budget for refunds is 40k.");
        var thread = (await w.Rosa.Post(w.Channel, "@researcher what does finance want? Then resolve this.")).GetProperty("id").GetInt64();
        var create = await w.Pool.Next<CreateSandbox>();
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Running);
        await using var box = await FakeSandbox.Connect(w.App, create.Spec);
        await box.NextDelivery();
        var token = create.Spec.ThreadToken;

        // the MCP handshake and the three tools
        var (st, init) = await Mcp(w.App, token, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "0" } });
        Assert.Equal(("cowork", true), ((string)init!["result"]!["serverInfo"]!["name"]!, init["result"]!["capabilities"]!["tools"] is not null));
        var tools = (await Mcp(w.App, token, "tools/list")).Body!["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToArray();
        Assert.Equal(new[] { "cowork_read_channel", "cowork_search", "cowork_resolve_thread", "cowork_show" }, tools);
        // brief 10: cowork_show says up front that the file must be inside the workspace
        var show = (await Mcp(w.App, token, "tools/list")).Body!["result"]!["tools"]!.AsArray().Single(t => (string)t!["name"]! == "cowork_show")!;
        Assert.StartsWith("Show people something you made, as an artifact in this thread. The file must be inside your workspace (your working directory): " +
            "a file anywhere else, like /tmp, can't be shown", (string)show["description"]!);
        var note = await w.App.CreateClient().PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", method = "notifications/initialized" });
        Assert.Equal(HttpStatusCode.Unauthorized, note.StatusCode);

        // read: its own channel, not #leads
        var (read, readErr) = await Tool(w.App, token, "cowork_read_channel", new { });
        Assert.False(readErr);
        Assert.StartsWith("#checkout-app (Checkout app), the last", read);
        Assert.Contains("Sam: Finance wants partial refunds by January.", read);
        var (refused, refusedErr) = await Tool(w.App, token, "cowork_read_channel", new { channel = "#leads" });
        Assert.True(refusedErr);
        Assert.Equal("researcher can't read #leads: it isn't a channel researcher takes part in, so it's outside its boundary.", refused);
        var (rootRead, _) = await Tool(w.App, token, "cowork_read_channel", new { threadId = thread });
        Assert.Contains("Rosa: @researcher what does finance want?", rootRead);
        // search: what it may see, never #leads
        var (found, _) = await Tool(w.App, token, "cowork_search", new { query = "refunds" });
        Assert.Contains("partial refunds by January", found); Assert.DoesNotContain("40k", found);
        var (nothing, _) = await Tool(w.App, token, "cowork_search", new { query = "Q4 budget" });
        Assert.Equal("Nothing found for \"Q4 budget\" in what researcher may see.", nothing);

        // resolve: the outcome, the thread's status, search
        await box.Say("Finance wants partial refunds by January.");
        var (resolved, resErr) = await Tool(w.App, token, "cowork_resolve_thread", new { title = "What finance wants", summary = "- Partial refunds by January.\n- The tax step rounds per line, so refunds drift by a cent." });
        Assert.False(resErr);
        Assert.Equal($"Thread {thread} is done: \"What finance wants\". The outcome shows on the post, in the thread and in search.", resolved);
        await box.End();
        await Until(() => w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='TurnEnded'") == 1, "the turn");
        using (var c = w.App.Sql())
        {
            var outcome = c.QueryFirst<(string Body, string Meta, long Author)>("SELECT body, meta, author_id FROM posts WHERE kind='outcome'");
            var meta = JsonDocument.Parse(outcome.Meta).RootElement;
            Assert.Equal(("What finance wants", w.ResearcherId), (outcome.Body, outcome.Author));
            Assert.Equal(("done", 0, w.Rosa.Id), (meta.GetProperty("state").GetString(), meta.GetProperty("asks").GetInt32(), meta.GetProperty("startedBy").GetInt64()));
            Assert.Equal(("volume", "pool-01"), (meta.GetProperty("branch").GetProperty("state").GetString(), meta.GetProperty("branch").GetProperty("host").GetString()));
            Assert.True(meta.GetProperty("elapsedMs").GetInt64() >= 0);
            // every tool call is on the record, the refused one too, for whoever asked
            var calls = c.Query<(string Tool, bool Refused, long Behalf)>("SELECT json_extract(data,'$.tool'), json_extract(data,'$.refused'), on_behalf_of FROM events WHERE type='CoworkToolCalled' ORDER BY position").ToList();
            Assert.Equal(6, calls.Count);
            Assert.Equal(new[] { false, true, false, false, false, false }, calls.Select(x => x.Refused));
            Assert.All(calls, x => Assert.Equal(w.Rosa.Id, x.Behalf));
        }
        var feed = await w.Rosa.Http.GetFromJsonAsync<JsonElement[]>($"/api/channels/{w.Channel}/posts");
        var root = feed!.Single(p => p.GetProperty("id").GetInt64() == thread);
        Assert.Equal(("done", "What finance wants"), (StatusOf(root)!.Value.GetProperty("state").GetString(), StatusOf(root)!.Value.GetProperty("outcome").GetString()));
        var search = await w.Rosa.Http.GetFromJsonAsync<JsonElement>("/api/search?q=finance");
        var o = search.GetProperty("outcomes").GetProperty("items")[0];
        Assert.Equal((thread, true, w.Rosa.Id), (o.GetProperty("threadId").GetInt64(), o.GetProperty("done").GetBoolean(), o.GetProperty("askedBy").GetInt64()));
        Assert.Contains("\u0002finance\u0003", o.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
        var jo = await w.App.Join(w.Alex, "jo", "Jo");                 // not in the project: finds no outcome
        Assert.Equal(0, (await jo.Http.GetFromJsonAsync<JsonElement>("/api/search?q=finance")).GetProperty("outcomes").GetProperty("total").GetInt32());
        // an agent's search finds the outcome first
        var (again, _) = await Tool(w.App, token, "cowork_search", new { query = "finance" });
        Assert.StartsWith("Outcomes:\n- What finance wants (thread", again);

        // a new message opens it again; Reopen does the same by hand
        await w.Rosa.Post(w.Channel, "@researcher and what about Sam's rounding point?", thread);
        Assert.NotEqual("done", ThreadStatus(w, thread));
        Assert.Equal("reopened", JsonDocument.Parse(w.App.Sql().ExecuteScalar<string>("SELECT meta FROM posts WHERE kind='outcome'")!).RootElement.GetProperty("state").GetString());
    }

    static string? ThreadStatus(SandboxTests.World w, long thread)
    {
        var t = w.Rosa.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{thread}").Result;
        return StatusOf(t.GetProperty("root"))?.GetProperty("state").GetString();
    }

    [Fact]
    public async Task A_pinned_agent_uses_its_runners_token_for_a_thread_it_is_in_and_nothing_else_opens_the_tools()
    {
        await using var w = await SandboxTests.Setup();
        await using var reviewer = await FakeRunner.Enrol(w.App, w.Alex, "reviewer");
        (await w.Alex.Http.PutAsync($"/api/channels/{w.Channel}/members/{reviewer.AgentId}", null)).EnsureSuccessStatusCode();
        await reviewer.Connect();
        var thread = (await w.Alex.Post(w.Channel, "@reviewer read the channel and resolve this")).GetProperty("id").GetInt64();
        await reviewer.NextDelivery();
        Assert.Equal("working", ThreadStatus(w, thread));
        var (read, err) = await Tool(w.App, reviewer.Token, "cowork_read_channel", new { }, thread);
        Assert.False(err); Assert.Contains("Alex: @reviewer read the channel", read);
        // a thread it isn't in, no thread, a pool's token, nothing: refused
        var other = (await w.Rosa.Post(w.Channel, "just a note")).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Mcp(w.App, reviewer.Token, "tools/list", null, other)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Mcp(w.App, reviewer.Token, "tools/list")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Mcp(w.App, w.Pool.Token, "tools/list", null, thread)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.Alex.Http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" })).StatusCode);       // a person's cookie isn't a token
        await Tool(w.App, reviewer.Token, "cowork_resolve_thread", new { summary = "Read it." }, thread);
        await reviewer.End(thread);
        await Until(() => ThreadStatus(w, thread) == "done", "done");
        var meta = JsonDocument.Parse(w.App.Sql().ExecuteScalar<string>("SELECT meta FROM posts WHERE kind='outcome'")!).RootElement;
        Assert.Equal("Read it.", meta.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("sandbox").ValueKind);
        // Reopen by hand
        (await w.Rosa.Http.PostAsync($"/api/threads/{thread}/reopen", null)).EnsureSuccessStatusCode();
        Assert.Null(ThreadStatus(w, thread));
    }

    [Fact]
    public async Task A_thread_shows_starting_working_waiting_and_done()
    {
        await using var w = await SandboxTests.Setup();
        var thread = (await w.Rosa.Post(w.Channel, "@researcher run the tests")).GetProperty("id").GetInt64();
        var create = await w.Pool.Next<CreateSandbox>();
        Assert.Equal("starting", ThreadStatus(w, thread));
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Running);
        await using var box = await FakeSandbox.Connect(w.App, create.Spec);
        await box.NextDelivery();
        Assert.Equal("working", ThreadStatus(w, thread));
        await box.Conn.InvokeAsync("RequestPermission", new PermissionRequestMsg(thread, "r1", "run bun test", new[] { new PermissionOption("allow", "Allow once", "allow_once") }));
        var t = await w.Sam.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{thread}");
        Assert.Equal(("waiting", "Rosa"), (StatusOf(t.GetProperty("root"))!.Value.GetProperty("state").GetString(), StatusOf(t.GetProperty("root"))!.Value.GetProperty("waitingFor").GetString()));
        var ask = w.App.Sql().ExecuteScalar<long>("SELECT id FROM posts WHERE kind='ask'");
        (await w.Rosa.Http.PostAsJsonAsync($"/api/posts/{ask}/answer", new { optionId = "allow" })).EnsureSuccessStatusCode();
        Assert.Equal("working", ThreadStatus(w, thread));
        await Tool(w.App, create.Spec.ThreadToken, "cowork_resolve_thread", new { summary = "The tests pass." });
        await box.End();
        await Until(() => ThreadStatus(w, thread) == "done", "done");
    }

    [Fact]
    public async Task A_done_threads_volume_is_deleted_after_the_retention_time_unless_its_branch_has_changes()
    {
        await using var w = await SandboxTests.Setup();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.PutAsJsonAsync("/api/settings/sandboxes", new { retentionSeconds = 5 })).StatusCode);
        (await w.Alex.Http.PutAsJsonAsync("/api/settings/sandboxes", new { retentionSeconds = 5 })).EnsureSuccessStatusCode();
        async Task<CreateSandbox> Done(Person who, string text)
        {
            var thread = (await who.Post(w.Channel, text)).GetProperty("id").GetInt64();
            var create = await w.Pool.Next<CreateSandbox>();
            await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Running);
            await using (var box = await FakeSandbox.Connect(w.App, create.Spec))
            {
                await box.NextDelivery();
                await Tool(w.App, create.Spec.ThreadToken, "cowork_resolve_thread", new { summary = "Done: " + text });
                await box.End();
                await Until(() => ThreadStatus(w, thread) == "done", "done");
            }
            await w.App.Services.GetRequiredService<Sandboxes>().Suspend(Sandboxes.Parse(create.Spec.SandboxId)!.Value, "idle", 0);
            await w.Pool.Report((await w.Pool.Next<SuspendSandbox>()).SandboxId, SandboxStates.Suspended);
            return create;
        }
        var first = await Done(w.Rosa, "@researcher look at the cart");
        // after the retention time the pool is asked to delete it, looking for changes first
        var delete = await w.Pool.Next<DeleteSandbox>(seconds: 30);
        Assert.Equal((first.Spec.SandboxId, true, "cowork-sandbox-opencode:2.4.1", "main"), (delete.SandboxId, delete.KeepIfChanged, delete.Image, delete.BaseBranch));
        await w.Pool.Report(delete.SandboxId, SandboxStates.Deleted);
        await Until(() => w.App.Sql().ExecuteScalar<string>("SELECT state FROM sandboxes WHERE id=@id", new { id = Sandboxes.Parse(first.Spec.SandboxId) }) == "deleted", "deleted");
        Assert.Equal("deleted", JsonDocument.Parse(w.App.Sql().ExecuteScalar<string>("SELECT meta FROM posts WHERE kind='outcome' AND thread_id=@t", new { t = first.Spec.ThreadId })!).RootElement.GetProperty("branch").GetProperty("state").GetString());
        Assert.False(w.Pool.HasMore);                                       // asked once

        var second = await Done(w.Sam, "@researcher fix the rounding");
        var delete2 = await w.Pool.Next<DeleteSandbox>(seconds: 30);
        await w.Pool.Conn!.InvokeAsync("Report", new SandboxStateReport(delete2.SandboxId, SandboxStates.Kept, "cowork/fix-the-rounding-3 has 1 commit not on main, so its volume is kept"));
        await Until(() => w.App.Sql().ExecuteScalar<string>("SELECT state FROM sandboxes WHERE id=@id", new { id = Sandboxes.Parse(second.Spec.SandboxId) }) == "kept", "kept");
        var branch = JsonDocument.Parse(w.App.Sql().ExecuteScalar<string>("SELECT meta FROM posts WHERE kind='outcome' AND thread_id=@t", new { t = second.Spec.ThreadId })!).RootElement.GetProperty("branch");
        Assert.Equal(("kept", "cowork/fix-the-rounding-3 has 1 commit not on main, so its volume is kept"), (branch.GetProperty("state").GetString(), branch.GetProperty("reason").GetString()));
        // a reply to the deleted one starts afresh (a new volume), the kept one resumes
        await w.Rosa.Post(w.Channel, "@researcher one more thing", first.Spec.ThreadId);
        Assert.Equal(first.Spec.SandboxId, (await w.Pool.Next<CreateSandbox>()).Spec.SandboxId);
    }
}
