using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace CoWork.Server.Tests;

/// <summary>Brief 7, round 2: locked down. The network list and asks to reach a host, the setup command, budgets in tokens and dollars,
/// Pause all agents, and one restart when a sandbox dies mid-turn. A fake pool and sandbox; phase10 runs the real ones.</summary>
public class LockedTests
{
    static async Task<(long Thread, CreateSandbox Create, FakeSandbox Box)> Started(SandboxTests.World w, Person who, string text)
    {
        var thread = (await who.Post(w.Channel, text)).GetProperty("id").GetInt64();
        var create = await w.Pool.Next<CreateSandbox>();
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Running);
        var box = await FakeSandbox.Connect(w.App, create.Spec);
        return (thread, create, box);
    }

    static async Task Until(Func<bool> done, string what, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done()) { if (DateTime.UtcNow > end) throw new Xunit.Sdk.XunitException("timed out waiting for " + what); await Task.Delay(25); }
    }

    static int Count(TestApp app, string sql, object? args = null) => app.Sql().ExecuteScalar<int>(sql, args);

    [Fact]
    public async Task A_host_off_the_list_is_asked_of_whoever_started_the_thread_and_allowing_it_tells_the_proxy_and_the_agent()
    {
        await using var w = await SandboxTests.Setup();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.PutAsJsonAsync($"/api/projects/{w.ProjectId}/network", new { hosts = new[] { "example.com" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Alex.Http.PutAsJsonAsync($"/api/projects/{w.ProjectId}/network", new { hosts = new[] { "not a host" } })).StatusCode);
        (await w.Alex.Http.PutAsJsonAsync($"/api/projects/{w.ProjectId}/network", new { hosts = new[] { "github.com", "*.npmjs.org", "GitHub.com" } })).EnsureSuccessStatusCode();

        var (thread, create, box) = await Started(w, w.Rosa, "@researcher what does example.com say?");
        await using var _ = box;
        // the list travels in the command, as Substrate-shaped rules: the server and gateway, then the project's hosts
        Assert.Equal(new[] { "*.npmjs.org", "github.com" }, create.Spec.Network.Rules[1].Hosts);
        Assert.Equal(new[] { "cowork-server", "cowork-gateway" }, create.Spec.Network.Rules[0].Hosts);
        var d = await box.NextDelivery();

        // the proxy refused example.com: an ask card to Rosa (twice refused, asked once); another pool can't report for this sandbox
        await w.Pool.Refused(create.Spec.SandboxId, "Example.com.");
        await w.Pool.Refused(create.Spec.SandboxId, "example.com");
        await using (var other = await FakePool.Enrol(w.App, w.Alex)) await other.Refused(create.Spec.SandboxId, "evil.example");
        await Until(() => Count(w.App, "SELECT COUNT(*) FROM events WHERE type='EgressRefused'") == 1, "the ask");
        var ask = w.App.Sql().QueryFirst<(long Id, string Meta, string Body)>("SELECT id, meta, body FROM posts WHERE kind='ask'");
        var meta = JsonDocument.Parse(ask.Meta).RootElement;
        Assert.Equal(("reach example.com", "example.com", w.Rosa.Id), (ask.Body, meta.GetProperty("host").GetString(), meta.GetProperty("ownerId").GetInt64()));
        Assert.Equal("It isn't on Checkout app's network list. Asked by researcher, for Rosa's thread.", meta.GetProperty("why").GetString());
        Assert.Equal(new[] { "once", "thread", "deny" }, meta.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
        Assert.Equal(w.Rosa.Id, w.App.Sql().ExecuteScalar<long>("SELECT on_behalf_of FROM events WHERE type='EgressRefused'"));
        Assert.Equal(1, Count(w.App, "SELECT COUNT(*) FROM posts WHERE kind='ask'"));

        // Alex leads the project but it's Rosa's to answer; she allows it once: the proxy is told, and the agent is told to try again
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Alex.Http.PostAsJsonAsync($"/api/posts/{ask.Id}/answer", new { optionId = "once" })).StatusCode);
        (await w.Rosa.Http.PostAsJsonAsync($"/api/posts/{ask.Id}/answer", new { optionId = "once" })).EnsureSuccessStatusCode();
        var allow = await w.Pool.Next<AllowEgress>();
        Assert.Equal((create.Spec.SandboxId, "example.com", true), (allow.SandboxId, allow.Host, allow.Once));
        Assert.Equal(w.Rosa.Id, w.App.Sql().ExecuteScalar<long>("SELECT actor_id FROM events WHERE type='EgressAllowed'"));
        await box.Say("I couldn't reach example.com."); await box.End();
        var again = await box.NextDelivery();                     // after its turn: a system message in the session
        Assert.Equal(thread, again.ThreadId);
        Assert.Equal($"<cowork-system thread=\"{thread}\">Rosa allowed example.com once. Try again.</cowork-system>", again.Text);
        await box.End();

        // "for this thread" is remembered: the next start of the sandbox has it in its rules
        await w.Pool.Refused(create.Spec.SandboxId, "docs.levy.example");
        await Until(() => Count(w.App, "SELECT COUNT(*) FROM posts WHERE kind='ask'") == 2, "the second ask");
        var ask2 = w.App.Sql().ExecuteScalar<long>("SELECT MAX(id) FROM posts WHERE kind='ask'");
        (await w.Rosa.Http.PostAsJsonAsync($"/api/posts/{ask2}/answer", new { optionId = "thread" })).EnsureSuccessStatusCode();
        Assert.False((await w.Pool.Next<AllowEgress>()).Once);
        await box.NextDelivery(); await box.End();
        await w.App.Services.GetRequiredService<Sandboxes>().Suspend(Sandboxes.Parse(create.Spec.SandboxId)!.Value, "idle", 0);
        await w.Pool.Report((await w.Pool.Next<SuspendSandbox>()).SandboxId, SandboxStates.Suspended);
        await w.Rosa.Post(w.Channel, "@researcher once more", thread);
        var resume = await w.Pool.Next<ResumeSandbox>();
        Assert.Contains("docs.levy.example", resume.Spec.Network.Rules[1].Hosts);
        Assert.DoesNotContain("example.com", resume.Spec.Network.Rules[1].Hosts);           // "once" was once
        // denying: recorded, nothing for the proxy
        await w.Pool.Refused(create.Spec.SandboxId, "tracker.example");
        await Until(() => Count(w.App, "SELECT COUNT(*) FROM posts WHERE kind='ask'") == 3, "the third ask");
        var ask3 = w.App.Sql().ExecuteScalar<long>("SELECT MAX(id) FROM posts WHERE kind='ask'");
        (await w.Rosa.Http.PostAsJsonAsync($"/api/posts/{ask3}/answer", new { optionId = "deny" })).EnsureSuccessStatusCode();
        Assert.Equal("denied", JsonDocument.Parse(w.App.Sql().ExecuteScalar<string>("SELECT meta FROM posts WHERE id=@ask3", new { ask3 })!).RootElement.GetProperty("status").GetString());
        Assert.Equal(1, Count(w.App, "SELECT COUNT(*) FROM events WHERE type='EgressDenied'"));
    }

    [Fact]
    public async Task The_gateway_stops_a_thread_at_its_cap_in_tokens_or_dollars_says_why_once_and_a_lead_can_raise_it()
    {
        await using var up = await FakeUpstream.Start();                    // 127 tokens and $0.0012 a call
        await using var w = await SandboxTests.Setup();
        (await w.Alex.Http.PutAsJsonAsync("/api/settings/models", new { kind = "openai-compatible", baseUrl = up.BaseUrl, apiKey = "sk-x" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.PutAsJsonAsync($"/api/projects/{w.ProjectId}/budget", new { threadTokens = 1 })).StatusCode);
        (await w.Alex.Http.PutAsJsonAsync($"/api/projects/{w.ProjectId}/budget", new { threadTokens = 200, monthTokens = 100_000 })).EnsureSuccessStatusCode();
        var (thread, create, box) = await Started(w, w.Rosa, "@researcher compare three tax APIs");
        await using var _ = box;
        await box.NextDelivery();
        var gw = w.App.CreateClient(); gw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", create.Spec.ThreadToken);
        async Task<HttpResponseMessage> Call() => await gw.PostAsJsonAsync("/gateway/v1/chat/completions", new { model = "m", stream = true, messages = new[] { new { role = "user", content = "hi" } } });
        async Task Ok() { var r = await Call(); Assert.Equal(HttpStatusCode.OK, r.StatusCode); await r.Content.ReadAsStringAsync(); }
        var usages = 0;
        async Task Counted() { usages++; await Until(() => Count(w.App, "SELECT COUNT(*) FROM events WHERE type='ModelUsageRecorded'") == usages, "the usage"); }
        await Ok(); await Counted();                // 127 of 200
        await Ok(); await Counted();                // 254: at the cap now
        var stopped = await Call();
        Assert.Equal((HttpStatusCode)402, stopped.StatusCode);
        var err = await stopped.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reached this thread's cap of 200 tokens. A lead can raise it.", err.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal((HttpStatusCode)402, (await Call()).StatusCode);
        Assert.Equal(2, up.Seen.Count);                                     // nothing more reached the provider
        Assert.Equal(1, Count(w.App, "SELECT COUNT(*) FROM events WHERE type='BudgetReached'"));        // said once in this turn
        var notice = w.App.Sql().QueryFirst<(string Body, string Meta, long Author)>("SELECT body, meta, author_id FROM posts WHERE kind='notice'");
        Assert.Equal(("Reached this thread's cap of 200 tokens. A lead can raise it.", w.ResearcherId), (notice.Body, notice.Author));
        Assert.Equal(new[] { w.Alex.Id }, JsonDocument.Parse(notice.Meta).RootElement.GetProperty("leadIds").EnumerateArray().Select(x => x.GetInt64()));

        // a member can't raise it; the lead can: the thread gets the per-thread cap again
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.PostAsync($"/api/threads/{thread}/budget/raise", null)).StatusCode);
        (await w.Alex.Http.PostAsync($"/api/threads/{thread}/budget/raise", null)).EnsureSuccessStatusCode();
        Assert.Equal("raised this thread's cap to 400 tokens.", w.App.Sql().ExecuteScalar<string>("SELECT body FROM posts WHERE kind='notice' ORDER BY id DESC LIMIT 1"));
        await Ok(); await Counted();                // 381 of 400

        // the month: the project's cap for this month holds every thread
        (await w.Alex.Http.PutAsJsonAsync($"/api/projects/{w.ProjectId}/budget", new { threadTokens = 100_000, monthTokens = 400 })).EnsureSuccessStatusCode();
        await Ok(); await Counted();                // 508 this month
        var month = await (await Call()).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches(@"^Reached Checkout app's cap of 400 tokens for \w+\. A lead can raise it in the project's settings\.$", month.GetProperty("error").GetProperty("message").GetString()!);

        // dollars, when the provider reports cost: $0.0012 a call, so four calls ($0.0048) are past a $0.004 cap
        (await w.Alex.Http.PutAsJsonAsync($"/api/projects/{w.ProjectId}/budget", new { threadUsd = 0.004 })).EnsureSuccessStatusCode();
        var cents = await (await Call()).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reached this thread's cap of $0.004. A lead can raise it.", cents.GetProperty("error").GetProperty("message").GetString());
        var settings = await w.Alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{w.ProjectId}");
        var budget = settings.GetProperty("locked").GetProperty("budget");
        Assert.Equal((508L, true), (budget.GetProperty("usedTokens").GetInt64(), budget.GetProperty("costReported").GetBoolean()));
        Assert.Equal("researcher", budget.GetProperty("byAgent")[0].GetProperty("handle").GetString());
    }

    [Fact]
    public async Task Pause_all_agents_stops_the_projects_sandboxes_holds_new_messages_and_resume_lets_them_go()
    {
        await using var w = await SandboxTests.Setup();
        var (thread, create, box) = await Started(w, w.Rosa, "@researcher read the cart");
        await box.NextDelivery();                     // a turn is running when Alex pauses
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.PostAsync($"/api/projects/{w.ProjectId}/pause", null)).StatusCode);
        (await w.Alex.Http.PostAsync($"/api/projects/{w.ProjectId}/pause", null)).EnsureSuccessStatusCode();
        Assert.Equal(create.Spec.SandboxId, (await w.Pool.Next<SuspendSandbox>()).SandboxId);
        await box.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Suspended);
        Assert.Equal("Paused by Alex. researcher picks this up again when the agents are resumed.", w.App.Sql().ExecuteScalar<string>("SELECT body FROM posts WHERE kind='notice'"));
        Assert.Equal(1, Count(w.App, Deliveries.QueuedIn, new { agentId = w.ResearcherId, stream = $"thread-{thread}" }));      // back in line
        // a new message waits: no sandbox, and the thread says who paused it
        var second = (await w.Sam.Post(w.Channel, "@researcher and the refunds?")).GetProperty("id").GetInt64();
        Assert.False(w.Pool.HasMore);
        var t = await w.Sam.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{second}");
        Assert.Equal("Alex", t.GetProperty("agents")[0].GetProperty("pausedBy").GetString());
        Assert.Equal("Alex", (await w.Alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{w.ProjectId}")).GetProperty("locked").GetProperty("paused").GetProperty("by").GetString());

        // resume: both go
        (await w.Alex.Http.PostAsync($"/api/projects/{w.ProjectId}/resume", null)).EnsureSuccessStatusCode();
        var cmds = new[] { await w.Pool.Next<object>(), await w.Pool.Next<object>() };
        Assert.Contains(cmds, c => c is ResumeSandbox r && r.Spec.ThreadId == thread);
        Assert.Contains(cmds, c => c is CreateSandbox r && r.Spec.ThreadId == second);
        var resumed = cmds.OfType<ResumeSandbox>().Single();
        await w.Pool.Report(resumed.Spec.SandboxId, SandboxStates.Running);
        await using var again = await FakeSandbox.Connect(w.App, resumed.Spec);
        Assert.Equal("@researcher read the cart", ExtractText((await again.NextDelivery()).Text));
    }

    static string ExtractText(string wrapped) => wrapped[(wrapped.IndexOf('>') + 1)..wrapped.LastIndexOf("</cowork-message>", StringComparison.Ordinal)];

    [Fact]
    public async Task A_sandbox_that_dies_mid_turn_is_started_again_once_and_the_second_time_the_turn_fails()
    {
        await using var w = await SandboxTests.Setup();
        var (thread, create, box) = await Started(w, w.Rosa, "@researcher read src/tax.ts");
        await box.NextDelivery();
        await box.Say("Reading…");
        await box.DisposeAsync();                                                      // the container died: its runner's connection is gone
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Suspended, "its container stopped");
        await Until(() => Count(w.App, "SELECT COUNT(*) FROM posts WHERE kind='notice'") == 1, "the notice");
        Assert.Equal("researcher's sandbox stopped unexpectedly. Trying again.", w.App.Sql().ExecuteScalar<string>("SELECT body FROM posts WHERE kind='notice'"));
        var resume = await w.Pool.Next<ResumeSandbox>();                               // the same sandbox, its volume, a new token
        Assert.Equal(create.Spec.SandboxId, resume.Spec.SandboxId);
        await w.Pool.Report(resume.Spec.SandboxId, SandboxStates.Running);
        var box2 = await FakeSandbox.Connect(w.App, resume.Spec);
        Assert.Equal("@researcher read src/tax.ts", ExtractText((await box2.NextDelivery()).Text));      // the same message again
        Assert.Equal(0, Count(w.App, "SELECT COUNT(*) FROM events WHERE type='TurnFailed'"));
        Assert.Equal(0, Count(w.App, "SELECT COUNT(*) FROM posts WHERE kind='text' AND streaming=1"));     // the cut-off answer was closed
        // it dies again: the turn ends
        await box2.DisposeAsync();
        await w.Pool.Report(resume.Spec.SandboxId, SandboxStates.Suspended, "its container stopped");
        await Until(() => Count(w.App, "SELECT COUNT(*) FROM events WHERE type='TurnFailed'") == 1, "the failure");
        Assert.Equal("researcher's sandbox stopped unexpectedly again, so this turn ended.", w.App.Sql().ExecuteScalar<string>("SELECT body FROM posts WHERE kind='notice' ORDER BY id DESC LIMIT 1"));
        Assert.Equal(0, Count(w.App, Deliveries.QueuedIn, new { agentId = w.ResearcherId, stream = $"thread-{thread}" }));
        Assert.Equal(0, Count(w.App, "SELECT COUNT(*) FROM open_turns"));
    }

    [Fact]
    public async Task The_setup_command_goes_with_each_message_to_a_sandbox_and_its_output_is_kept()
    {
        await using var w = await SandboxTests.Setup();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Sam.Http.PutAsJsonAsync($"/api/projects/{w.ProjectId}/setup", new { command = "x" })).StatusCode);
        (await w.Alex.Http.PutAsJsonAsync($"/api/projects/{w.ProjectId}/setup", new { command = "sh scripts/agent-setup.sh" })).EnsureSuccessStatusCode();
        var (thread, _, box) = await Started(w, w.Rosa, "@researcher hello");
        await using var b = box;
        Assert.Equal("sh scripts/agent-setup.sh", (await box.NextDelivery()).SetupCommand);
        await box.Conn.InvokeAsync("Stream", new StreamEvent(thread, StreamKinds.ToolStart, null, "cowork-setup", "setup: sh scripts/agent-setup.sh", "execute", "running"));
        await box.Conn.InvokeAsync("Stream", new StreamEvent(thread, StreamKinds.ToolEnd, null, "cowork-setup", "setup: sh scripts/agent-setup.sh", null, "completed", 22, "abcd", "installed 3 packages\n"));
        await box.End();
        await Until(() => Count(w.App, "SELECT COUNT(*) FROM events WHERE type='TurnEnded'") == 1, "the turn");
        Assert.Equal("installed 3 packages\n", w.App.Sql().ExecuteScalar<string>("SELECT json_extract(data,'$.output') FROM events WHERE type='ToolCallFinished'"));      // kept, though the template doesn't record tool output
        var settings = await w.Alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{w.ProjectId}");
        Assert.Equal("sh scripts/agent-setup.sh", settings.GetProperty("locked").GetProperty("setupCommand").GetString());
    }
}
