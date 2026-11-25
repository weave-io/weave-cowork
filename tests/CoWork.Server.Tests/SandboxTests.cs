using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace CoWork.Server.Tests;

/// <summary>
/// Brief 7, round 1: a sandbox per thread (docs/architecture/provisioning.html). A fake pool and a fake runner inside the sandbox stand in for
/// podman and OpenCode (the live check, phase9, uses the real ones); the tests cover what the server decides and enforces.
/// </summary>
public class SandboxTests
{
    public sealed record World(TestApp App, Person Alex, Person Rosa, Person Sam, FakePool Pool, long ResearcherId, long ProjectId, long Channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Pool.DisposeAsync(); App.Dispose(); }
    }

    /// <summary>Alex (admin) connects a pool, makes the researcher template and enables it in Checkout app (a repository workspace) with Rosa and Sam.</summary>
    public static async Task<World> Setup(int slots = 2, TestApp? existing = null)
    {
        var app = existing ?? new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa"); var sam = await app.Join(alex, "sam", "Sam");
        var pool = await FakePool.Enrol(app, alex, slots);
        var t = await alex.Http.PostAsJsonAsync("/api/agents", new { handle = "researcher", about = "Reads code and the web", instructions = "Be brief.", harnessCommand = "opencode acp",
            placement = "pool", image = "cowork-sandbox-opencode:2.4.1", model = "claude-haiku-4.5" });
        t.EnsureSuccessStatusCode();
        var researcher = (await t.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentId").GetInt64();
        var p = await alex.Http.PostAsJsonAsync("/api/projects", new { name = "Checkout app", about = "Cart and payment", memberIds = new[] { rosa.Id, sam.Id },
            workspace = new { kind = "repository", name = "app", url = "/srv/git/checkout-app.git", branch = "main" }, channels = new[] { new { name = "checkout-app", kind = "text" } } });
        p.EnsureSuccessStatusCode();
        var project = (await p.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{researcher}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
        var channel = app.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='checkout-app'");
        return new World(app, alex, rosa, sam, pool, researcher, project, channel);
    }

    static async Task<(long Thread, CreateSandbox Create)> Ask(World w, Person who, string text)
    {
        var thread = (await who.Post(w.Channel, text)).GetProperty("id").GetInt64();
        var create = await w.Pool.Next<CreateSandbox>();
        Assert.Equal(thread, create.Spec.ThreadId);
        return (thread, create);
    }

    static async Task<FakeSandbox> Start(World w, SandboxSpec spec)
    {
        await w.Pool.Report(spec.SandboxId, SandboxStates.Running);
        return await FakeSandbox.Connect(w.App, spec);
    }

    static async Task Until(Func<bool> done, string what, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done()) { if (DateTime.UtcNow > end) throw new Xunit.Sdk.XunitException("timed out waiting for " + what); await Task.Delay(25); }
    }

    static JsonElement Card(TestApp app, long postId) => JsonDocument.Parse(app.Sql().ExecuteScalar<string>("SELECT meta FROM posts WHERE id=@postId", new { postId })!).RootElement;
    static string Step(JsonElement card, string key) => card.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("key").GetString() == key).GetProperty("text").GetString()!;
    static string StepState(JsonElement card, string key) => card.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("key").GetString() == key).GetProperty("state").GetString()!;

    [Fact]
    public async Task A_template_mention_starts_a_sandbox_on_a_pool_and_its_runner_answers_for_that_thread()
    {
        await using var w = await Setup();
        // only an admin makes a template (it spends the server's model access)
        var refused = await w.Rosa.Http.PostAsJsonAsync("/api/agents", new { handle = "sneaky", harnessCommand = "opencode acp", placement = "pool", model = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var (thread, create) = await Ask(w, w.Rosa, "@researcher what does src/tax.ts do?");
        var s = create.Spec;
        // the command carries the template, the thread's workspace, limits, network rules as data, and a fresh token
        Assert.Equal("cowork-sandbox-opencode:2.4.1", s.Template.Image);
        Assert.Equal(new[] { "opencode", "acp" }, s.Template.HarnessCommand);
        Assert.Equal("claude-haiku-4.5", s.Template.Model);
        Assert.Equal(("repository", "/srv/git/checkout-app.git", "main"), (s.Workspace.Kind, s.Workspace.Url, s.Workspace.BaseBranch));
        Assert.StartsWith("cowork/what-does-src-tax-ts-", s.Workspace.Branch);
        Assert.Equal(1500, s.Limits.MemoryMb);
        Assert.NotEmpty(s.Network.Rules);
        Assert.Equal(w.ResearcherId, s.AgentId); Assert.Equal(w.ProjectId, s.ProjectId);
        Assert.Equal(64, s.ThreadToken.Length);
        using (var c = w.App.Sql())
        {
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM thread_tokens WHERE token_hash=@h AND thread_id=@thread AND agent_id=@a AND project_id=@p AND revoked_at IS NULL",
                new { h = Tokens.Hash(s.ThreadToken), thread, a = w.ResearcherId, p = w.ProjectId }));
            Assert.DoesNotContain(s.ThreadToken, string.Concat(c.Query<string>("SELECT data FROM events")));        // the token is never in the log
            var types = c.Query<string>("SELECT type FROM events WHERE stream_id=@s ORDER BY position", new { s = $"thread-{thread}" }).ToList();
            Assert.Equal(new[] { "ThreadStarted", "AgentJoinedThread", "MessageDelivered", "SandboxRequested", "SandboxPlaced" }, types);
            Assert.Equal(w.Rosa.Id, c.ExecuteScalar<long>("SELECT on_behalf_of FROM events WHERE type='SandboxRequested'"));       // for whom
        }
        var cardId = w.App.Sql().ExecuteScalar<long>("SELECT id FROM posts WHERE kind='sandbox' AND thread_id=@thread", new { thread });
        Assert.Equal("pool-01 has room (2 of 2 sandboxes free)", Step(Card(w.App, cardId), "pool"));

        // the pool starts it; the runner inside connects with the token and gets the message that waited
        await using var box = await Start(w, s);
        var d = await box.NextDelivery();
        Assert.Equal(thread, d.ThreadId);
        Assert.Contains("from=\"@rosa\"", d.Text);
        Assert.StartsWith("cowork/", d.ThreadWorkspace!.Branch);
        await box.Conn.InvokeAsync("Stream", new StreamEvent(thread, StreamKinds.Workspace, "/work/repo", null, d.ThreadWorkspace.Branch, null, "sandbox"));
        await box.Say("It rounds tax to cents."); await box.End();
        await Until(() => w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='TurnEnded'") == 1, "the turn to end");
        using (var c = w.App.Sql())
        {
            Assert.Equal("pool-01", c.ExecuteScalar<string>("SELECT json_extract(data,'$.host') FROM events WHERE type='TurnStarted'"));
            Assert.Equal(("sandbox", "pool-01", 1L), c.QueryFirst<(string, string, long)>("SELECT place, host, sandboxed FROM thread_agents WHERE thread_id=@thread", new { thread }));
            Assert.Equal("It rounds tax to cents.", c.ExecuteScalar<string>("SELECT body FROM posts WHERE thread_id=@thread AND kind='text' AND author_id=@a", new { thread, a = w.ResearcherId }));
            Assert.Equal("running", c.ExecuteScalar<string>("SELECT state FROM sandboxes"));
        }
        var card = Card(w.App, cardId);
        Assert.Equal(new[] { "done", "done", "done", "done" }, new[] { "pool", "container", "workspace", "harness" }.Select(k => StepState(card, k)));
        Assert.Equal("Started a sandbox from `cowork-sandbox-opencode:2.4.1`, 1500 MB", Step(card, "container"));

        // the thread's header says where it runs
        var t = await w.Rosa.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{thread}");
        var agent = t.GetProperty("agents")[0];
        Assert.Equal("pool", agent.GetProperty("placement").GetString());
        Assert.Equal(("running", "pool-01"), (agent.GetProperty("sandbox").GetProperty("state").GetString(), agent.GetProperty("sandbox").GetProperty("pool").GetString()));
        Assert.True(agent.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task A_thread_token_opens_one_door_for_one_thread_and_a_pool_token_another()
    {
        await using var w = await Setup();
        var (a, createA) = await Ask(w, w.Rosa, "@researcher look at the cart");
        var (b, createB) = await Ask(w, w.Sam, "@researcher look at refunds");
        Assert.NotEqual(createA.Spec.ThreadToken, createB.Spec.ThreadToken);
        await using var boxA = await Start(w, createA.Spec);
        Assert.Equal(a, (await boxA.NextDelivery()).ThreadId);

        // it hears only its thread: Sam's message waits for Sam's sandbox
        await w.Pool.Report(createB.Spec.SandboxId, SandboxStates.Running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boxA.NextDelivery(1));
        // it may speak only for its thread
        var before = w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE stream_id=@s", new { s = $"thread-{b}" });
        await Assert.ThrowsAsync<HubException>(() => boxA.Conn.InvokeAsync("Stream", new StreamEvent(b, StreamKinds.Text, "I'm in Sam's thread")));
        await Assert.ThrowsAsync<HubException>(() => boxA.Conn.InvokeAsync("RequestPermission", new PermissionRequestMsg(b, "r1", "run rm -rf", Array.Empty<PermissionOption>())));
        Assert.Equal(before, w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE stream_id=@s", new { s = $"thread-{b}" }));
        await using var boxB = await Start(w, createB.Spec);
        Assert.Equal(b, (await boxB.NextDelivery()).ThreadId);

        // a thread token isn't a person: no API; and it isn't a pool
        var api = w.App.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", createA.Spec.ThreadToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/bootstrap")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsync("/hubs/pool/negotiate?negotiateVersion=1", null)).StatusCode);
        // a pool token: no runner hub, no gateway, no API
        var pool = w.App.CreateClient();
        pool.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", w.Pool.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pool.PostAsync("/hubs/runner/negotiate?negotiateVersion=1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pool.PostAsJsonAsync("/gateway/v1/chat/completions", new { model = "x", messages = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pool.GetAsync("/api/bootstrap")).StatusCode);
        // a pinned agent's runner token doesn't open the gateway either (its owner pays for its models)
        await using var reviewer = await FakeRunner.Enrol(w.App, w.Alex, "reviewer");
        var runner = w.App.CreateClient();
        runner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reviewer.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await runner.GetAsync("/gateway/v1/models")).StatusCode);
        // a pool can't report on a sandbox it wasn't given
        await using var other = await FakePool.Enrol(w.App, w.Alex);
        await other.Report(createA.Spec.SandboxId, SandboxStates.Suspended);
        Assert.Equal("running", w.App.Sql().ExecuteScalar<string>("SELECT state FROM sandboxes WHERE thread_id=@a", new { a }));

        // suspended: the token stops working at once, and the connection is closed
        var sandboxA = Sandboxes.Parse(createA.Spec.SandboxId)!.Value;
        await w.App.Services.GetRequiredService<Sandboxes>().Suspend(sandboxA, "idle", 0);
        await boxA.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(createA.Spec.SandboxId, (await w.Pool.Next<SuspendSandbox>()).SandboxId);
        var gw = w.App.CreateClient();
        gw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", createA.Spec.ThreadToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.GetAsync("/gateway/v1/models")).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => FakeSandbox.Connect(w.App, createA.Spec));
        // removing the template revokes every token it has
        await (await w.Alex.Http.DeleteAsync($"/api/agents/{w.ResearcherId}")).Content.ReadAsStringAsync();
        await boxB.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM thread_tokens WHERE revoked_at IS NULL"));
    }

    [Fact]
    public async Task A_full_pool_queues_in_order_and_the_next_starts_when_a_slot_frees()
    {
        await using var w = await Setup(slots: 1);
        var (first, create) = await Ask(w, w.Rosa, "@researcher first");
        var second = (await w.Sam.Post(w.Channel, "@researcher second")).GetProperty("id").GetInt64();
        var third = (await w.Alex.Post(w.Channel, "@researcher third")).GetProperty("id").GetInt64();
        Assert.False(w.Pool.HasMore);                     // one slot: the others wait
        long CardOf(long t) => w.App.Sql().ExecuteScalar<long>("SELECT post_id FROM sandboxes WHERE thread_id=@t", new { t });
        Assert.Equal("Waiting for room on pool-01 (1 of 1 busy), 1st in line", Step(Card(w.App, CardOf(second)), "pool"));
        Assert.Equal("Waiting for room on pool-01 (1 of 1 busy), 2nd in line", Step(Card(w.App, CardOf(third)), "pool"));
        await using var box = await Start(w, create.Spec);
        await box.NextDelivery(); await box.End();

        // the first one stops: the oldest waiting one gets the slot, and the last moves up
        await w.App.Services.GetRequiredService<Sandboxes>().Suspend(Sandboxes.Parse(create.Spec.SandboxId)!.Value, "idle", 0);
        var suspend = await w.Pool.Next<SuspendSandbox>();
        await w.Pool.Report(suspend.SandboxId, SandboxStates.Suspended);
        var next = await w.Pool.Next<CreateSandbox>();
        Assert.Equal(second, next.Spec.ThreadId);
        Assert.Equal("pool-01 has room (1 of 1 sandboxes free)", Step(Card(w.App, CardOf(second)), "pool"));
        Assert.Equal("Waiting for room on pool-01 (1 of 1 busy), 1st in line", Step(Card(w.App, CardOf(third)), "pool"));
        Assert.False(w.Pool.HasMore);
        Assert.Equal("suspended", w.App.Sql().ExecuteScalar<string>("SELECT state FROM sandboxes WHERE thread_id=@first", new { first }));
    }

    [Fact]
    public async Task An_idle_sandbox_is_suspended_and_a_reply_resumes_it_on_the_same_volume_with_a_new_token()
    {
        await using var w = await Setup();
        (await w.Alex.Http.PutAsJsonAsync("/api/settings/sandboxes", new { idleSeconds = 5 })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.PutAsJsonAsync("/api/settings/sandboxes", new { idleSeconds = 5 })).StatusCode);
        var (thread, create) = await Ask(w, w.Rosa, "@researcher read src/tax.ts");
        await using (var box = await Start(w, create.Spec)) { await box.NextDelivery(); await box.Say("roundTax rounds to cents."); await box.End(); }

        // the server's idle clock (5 s here) asks the pool to stop it: no message is queued and no turn is open
        var suspend = await w.Pool.Next<SuspendSandbox>(seconds: 30);
        Assert.Equal(create.Spec.SandboxId, suspend.SandboxId);
        await w.Pool.Report(suspend.SandboxId, SandboxStates.Suspended);
        using (var c = w.App.Sql())
        {
            Assert.Equal("suspended", c.ExecuteScalar<string>("SELECT state FROM sandboxes"));
            Assert.Equal(new[] { "SandboxSuspending", "SandboxSuspended" }, c.Query<string>("SELECT type FROM events WHERE type LIKE 'SandboxSuspend%' ORDER BY position"));
            Assert.Equal(5, c.ExecuteScalar<int>("SELECT json_extract(data,'$.idleSeconds') FROM events WHERE type='SandboxSuspending'"));
            Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM thread_tokens WHERE revoked_at IS NULL"));
        }

        // Rosa replies: the same sandbox resumes (its volume), with a fresh token; a new card says it is resuming
        await w.Rosa.Post(w.Channel, "@researcher what was the function called?", thread);
        var resume = await w.Pool.Next<ResumeSandbox>();
        Assert.Equal(create.Spec.SandboxId, resume.Spec.SandboxId);
        Assert.NotEqual(create.Spec.ThreadToken, resume.Spec.ThreadToken);
        await Assert.ThrowsAnyAsync<Exception>(() => FakeSandbox.Connect(w.App, create.Spec));          // the old token is dead
        await using var again = await Start(w, resume.Spec);
        Assert.Contains("what was the function called?", (await again.NextDelivery()).Text);
        using (var c = w.App.Sql())
        {
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='SandboxResumed'"));
            Assert.Equal(new[] { "Starting a sandbox", "Resuming the sandbox" }, c.Query<string>("SELECT body FROM posts WHERE kind='sandbox' ORDER BY id"));
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM sandboxes"));
        }
        var card = Card(w.App, w.App.Sql().ExecuteScalar<long>("SELECT MAX(id) FROM posts WHERE kind='sandbox'"));
        Assert.True(card.GetProperty("resume").GetBoolean());
        Assert.StartsWith("Resumed the sandbox from `cowork-sandbox-opencode:2.4.1` on its volume", Step(card, "container"));
    }

    [Fact]
    public async Task A_sandbox_that_cant_start_says_why_and_the_next_message_tries_again()
    {
        await using var w = await Setup();
        var (thread, create) = await Ask(w, w.Rosa, "@researcher hello");
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Failed, "podman run failed: no such image");
        await Until(() => w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM posts WHERE kind='notice'") == 1, "the notice");
        using (var c = w.App.Sql())
        {
            Assert.Equal("researcher's sandbox couldn't start: podman run failed: no such image", c.ExecuteScalar<string>("SELECT body FROM posts WHERE kind='notice'"));
            Assert.Equal(0, c.ExecuteScalar<int>(Deliveries.QueuedIn, new { agentId = w.ResearcherId, stream = $"thread-{thread}" }));       // nothing retries by itself
            Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM thread_tokens WHERE revoked_at IS NULL"));
        }
        Assert.False(w.Pool.HasMore);
        await w.Rosa.Post(w.Channel, "@researcher hello again", thread);
        Assert.Equal(create.Spec.SandboxId, (await w.Pool.Next<CreateSandbox>()).Spec.SandboxId);       // never started, so it's created again
    }

    [Fact]
    public async Task The_gateway_forwards_with_the_servers_key_streams_back_and_counts_the_call_against_the_thread()
    {
        await using var up = await FakeUpstream.Start();
        await using var w = await Setup();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.PutAsJsonAsync("/api/settings/models", new { kind = "openai-compatible", baseUrl = up.BaseUrl, apiKey = "x" })).StatusCode);
        (await w.Alex.Http.PutAsJsonAsync("/api/settings/models", new { kind = "openai-compatible", baseUrl = up.BaseUrl, apiKey = "sk-server-secret-123" })).EnsureSuccessStatusCode();
        var (thread, create) = await Ask(w, w.Rosa, "@researcher what does roundTax do?");
        await using var box = await Start(w, create.Spec);
        await box.NextDelivery();

        var gw = w.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.PostAsJsonAsync("/gateway/v1/chat/completions", new { model = "claude-haiku-4.5" })).StatusCode);
        gw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.PostAsJsonAsync("/gateway/v1/chat/completions", new { model = "claude-haiku-4.5" })).StatusCode);
        gw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", create.Spec.ThreadToken);
        var models = await gw.GetFromJsonAsync<JsonElement>("/gateway/v1/models");
        Assert.Equal("claude-haiku-4.5", models.GetProperty("data")[0].GetProperty("id").GetString());

        var res = await gw.PostAsJsonAsync("/gateway/v1/chat/completions", new { model = "claude-haiku-4.5", stream = true, messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/event-stream", res.Content.Headers.ContentType!.ToString());
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"content\":\"round\"", text); Assert.Contains("\"content\":\"Tax\"", text); Assert.Contains("data: [DONE]", text);
        // upstream got the server's key (not the thread token) and was asked for the usage
        var (auth, body) = Assert.Single(up.Seen);
        Assert.Equal("Bearer sk-server-secret-123", auth);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.DoesNotContain(create.Spec.ThreadToken, body);

        await Until(() => w.App.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='ModelUsageRecorded'") == 1, "the usage");
        using var c = w.App.Sql();
        var ev = c.QueryFirst<(string Data, string Correlation, long Behalf)>("SELECT data, correlation, on_behalf_of FROM events WHERE type='ModelUsageRecorded'");
        var turn = c.ExecuteScalar<string>("SELECT correlation FROM events WHERE type='TurnStarted'");
        Assert.Equal(turn, ev.Correlation);                     // counted in the turn it belongs to
        Assert.Equal(w.Rosa.Id, ev.Behalf);                     // for whoever asked
        var data = JsonDocument.Parse(ev.Data).RootElement;
        Assert.Equal((120, 7, 0.0012), (data.GetProperty("promptTokens").GetInt32(), data.GetProperty("completionTokens").GetInt32(), data.GetProperty("cost").GetDouble()));
        Assert.Equal((1L, 120L, 7L), c.QueryFirst<(long, long, long)>("SELECT calls, prompt_tokens, completion_tokens FROM model_usage WHERE thread_id=@thread AND turn=@turn", new { thread, turn }));
        // the provider's key is a secret: in the secrets table, nowhere in the log
        Assert.DoesNotContain("sk-server-secret-123", string.Concat(c.Query<string>("SELECT data FROM events")));
        Assert.Equal("sk-server-secret-123", c.ExecuteScalar<string>("SELECT value FROM server_secrets WHERE key='model_api_key'"));
        var settings = await w.Alex.Http.GetFromJsonAsync<JsonElement>("/api/settings/server");
        Assert.True(settings.GetProperty("models").GetProperty("hasKey").GetBoolean());
        Assert.DoesNotContain("sk-server", settings.GetRawText());
    }

    [Fact]
    public async Task A_templates_ask_goes_to_whoever_started_the_thread_and_a_lead_only_after_the_wait()
    {
        await using var w = await Setup();
        var (thread, create) = await Ask(w, w.Rosa, "@researcher run the tests");
        await using var box = await Start(w, create.Spec);
        await box.NextDelivery();
        await box.Conn.InvokeAsync("RequestPermission", new PermissionRequestMsg(thread, "r1", "run bun test", new[] { new PermissionOption("allow", "Allow once", "allow_once") }));
        long AskCard() => w.App.Sql().ExecuteScalar<long>("SELECT MAX(id) FROM posts WHERE kind='ask'");
        var meta = JsonDocument.Parse(w.App.Sql().ExecuteScalar<string>("SELECT meta FROM posts WHERE id=@id", new { id = AskCard() })!).RootElement;
        Assert.Equal(w.Rosa.Id, meta.GetProperty("ownerId").GetInt64());                  // Rosa started the thread: she answers
        Assert.Equal(w.Alex.Id, meta.GetProperty("agentOwnerId").GetInt64());
        Assert.Equal(new[] { w.Alex.Id }, meta.GetProperty("leadIds").EnumerateArray().Select(x => x.GetInt64()));
        // Alex owns the template and leads the project, but it isn't his to answer yet
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Alex.Http.PostAsJsonAsync($"/api/posts/{AskCard()}/answer", new { optionId = "allow" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Sam.Http.PostAsJsonAsync($"/api/posts/{AskCard()}/answer", new { optionId = "allow" })).StatusCode);
        (await w.Rosa.Http.PostAsJsonAsync($"/api/posts/{AskCard()}/answer", new { optionId = "allow" })).EnsureSuccessStatusCode();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) Assert.Equal("allow", (await box.Answers.Reader.ReadAsync(cts.Token)).OptionId);
        Assert.Equal(w.Rosa.Id, w.App.Sql().ExecuteScalar<long>("SELECT actor_id FROM events WHERE type='PermissionGranted'"));

        // once Rosa has had her time, the lead may answer
        var wait = Dispatcher.LeadAfterMs;
        try
        {
            Dispatcher.LeadAfterMs = 0;
            await box.Conn.InvokeAsync("RequestPermission", new PermissionRequestMsg(thread, "r2", "run bun build", new[] { new PermissionOption("allow", "Allow once", "allow_once") }));
            (await w.Alex.Http.PostAsJsonAsync($"/api/posts/{AskCard()}/answer", new { optionId = (string?)null })).EnsureSuccessStatusCode();
            Assert.Equal(w.Alex.Id, w.App.Sql().ExecuteScalar<long>("SELECT actor_id FROM events WHERE type='PermissionDenied'"));
        }
        finally { Dispatcher.LeadAfterMs = wait; }
    }

    [Fact]
    public async Task Where_agents_run_lists_runners_and_pools_for_admins_and_a_template_is_enabled_like_any_agent()
    {
        await using var w = await Setup();
        await using var reviewer = await FakeRunner.Enrol(w.App, w.Alex, "reviewer");
        await reviewer.Connect();
        JsonElement where = default;
        for (var i = 0; i < 200; i++)          // the runner is registered just after its handshake
        {
            where = await w.Alex.Http.GetFromJsonAsync<JsonElement>("/api/where");
            if (where.GetProperty("runners")[0].GetProperty("online").GetBoolean()) break;
            await Task.Delay(25);
        }
        var pool = where.GetProperty("pools")[0];
        Assert.Equal(("pool-01", "pool-box", 2, true), (pool.GetProperty("name").GetString(), pool.GetProperty("host").GetString(), pool.GetProperty("slots").GetInt32(), pool.GetProperty("online").GetBoolean()));
        Assert.Equal(("reviewer", "testbox", true), (where.GetProperty("runners")[0].GetProperty("handle").GetString(), where.GetProperty("runners")[0].GetProperty("host").GetString(), where.GetProperty("runners")[0].GetProperty("online").GetBoolean()));
        Assert.Equal(1, where.GetProperty("runners").GetArrayLength());                 // a template isn't a runner
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.GetAsync("/api/where")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rosa.Http.PostAsJsonAsync("/api/pools", new { })).StatusCode);
        // a used code is used
        var e = await w.App.CreateClient().PostAsJsonAsync("/api/pools/enrol", new PoolEnrolRequest("AAAA-BBBB", "x", 1, 100));
        Assert.Equal(HttpStatusCode.BadRequest, e.StatusCode);

        // the boundary of a template: a sandbox per thread, the gateway, asks go to whoever started the thread
        var b = await w.Alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{w.ProjectId}/agents/{w.ResearcherId}/boundary");
        Assert.True(b.GetProperty("sandboxed").GetBoolean());
        var lines = b.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("key").GetString()!, l => l.GetProperty("text").GetString()!);
        Assert.Equal(new[] { "sees", "takesWorkFrom", "worksOn", "reaches", "asks", "spends", "remembers", "stops" }, lines.Keys);
        Assert.StartsWith("Whoever started the thread; Alex if they haven't answered in 30 minutes.", lines["asks"]);
        Assert.Contains("pool-01 keeps a mirror", lines["worksOn"]);
        Assert.Contains("gateway", lines["spends"]);
        var settings = await w.Alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{w.ProjectId}");
        var row = settings.GetProperty("agents")[0];
        Assert.Equal(("pool", true), (row.GetProperty("placement").GetString(), row.GetProperty("sandboxed").GetBoolean()));
        // in the member list, a template is available while a pool is online
        var boot = await w.Rosa.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        var m = boot.GetProperty("members").EnumerateArray().First(x => x.GetProperty("handle").GetString() == "researcher");
        Assert.Equal(("pool", true, "claude-haiku-4.5"), (m.GetProperty("placement").GetString(), m.GetProperty("online").GetBoolean(), m.GetProperty("model").GetString()));
    }
}
