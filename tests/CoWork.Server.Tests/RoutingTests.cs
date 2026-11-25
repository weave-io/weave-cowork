using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoWork.Server.Tests;

public class EnrolmentTests
{
    [Fact]
    public async Task A_code_works_once_and_the_token_is_stored_only_as_a_hash()
    {
        using var app = new TestApp();
        var alex = await app.Setup();
        var r = await alex.Http.PostAsJsonAsync("/api/agents", new { handle = "reviewer", harnessCommand = "opencode acp", workspace = "/tmp" });
        var code = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var anon = app.CreateClient();
        var ok = await anon.PostAsJsonAsync("/api/runner/enrol", new EnrolRequest(code, "box"));
        var enrol = (await ok.Content.ReadFromJsonAsync<EnrolResponse>())!;
        Assert.Equal("reviewer", enrol.Handle);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/runner/enrol", new EnrolRequest(code, "box"))).StatusCode);
        using var c = app.Sql();
        var stored = c.ExecuteScalar<string>("SELECT token_hash FROM runner_secrets");
        Assert.NotEqual(enrol.Token, stored);
        Assert.Equal(Tokens.Hash(enrol.Token), stored);
        Assert.Null(c.ExecuteScalar<string?>("SELECT code_hash FROM runner_secrets"));
    }

    [Fact]
    public async Task An_expired_code_is_refused_and_a_bad_token_cannot_connect()
    {
        using var app = new TestApp();
        var alex = await app.Setup();
        var r = await alex.Http.PostAsJsonAsync("/api/agents", new { handle = "reviewer", workspace = "/tmp" });
        var code = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        using (var c = app.Sql()) c.Execute("UPDATE agents SET code_expires=1");
        Assert.Equal(HttpStatusCode.BadRequest, (await app.CreateClient().PostAsJsonAsync("/api/runner/enrol", new EnrolRequest(code, "box"))).StatusCode);
        var f = new FakeRunner(app) { Token = "not-a-token" };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Connect());
    }

    [Fact]
    public async Task Removing_an_agent_cuts_off_its_token()
    {
        using var app = new TestApp();
        var alex = await app.Setup();
        await using var runner = await FakeRunner.Enrol(app, alex, "reviewer");
        await runner.Connect();
        var revoked = new TaskCompletionSource();
        runner.Conn!.On("Revoked", () => revoked.TrySetResult());
        (await alex.Http.DeleteAsync($"/api/agents/{runner.AgentId}")).EnsureSuccessStatusCode();
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var again = new FakeRunner(app) { Token = runner.Token };
        await Assert.ThrowsAnyAsync<Exception>(() => again.Connect());
    }
}

public class RoutingTests
{
    static async Task<(TestApp app, Person alex, Person rosa, long ch, FakeRunner runner)> World(bool connect = true)
    {
        var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        var ch = await alex.Channel("checkout-app");
        var runner = await FakeRunner.Enrol(app, alex, "reviewer");
        (await alex.Http.PutAsync($"/api/channels/{ch}/members/{runner.AgentId}", null)).EnsureSuccessStatusCode();
        if (connect) await runner.Connect();
        return (app, alex, rosa, ch, runner);
    }

    [Fact]
    public async Task A_mention_starts_a_thread_on_the_post_and_delivers_it_wrapped_with_its_author()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var post = await alex.Post(ch, "@reviewer what does this repository do?");
        var root = post.GetProperty("id").GetInt64();
        Assert.True(post.GetProperty("hasThread").GetBoolean());
        var d = await runner.NextDelivery();
        Assert.Equal(root, d.ThreadId);
        Assert.Equal($"<cowork-message from=\"@alex\" name=\"Alex\" owner=\"true\" channel=\"#checkout-app\" thread=\"{root}\">@reviewer what does this repository do?</cowork-message>", d.Text);
        Assert.Equal("Be brief.", d.Instructions);
        Assert.Equal(new[] { "opencode", "acp" }, d.HarnessCommand);
    }

    [Fact]
    public async Task A_reply_goes_to_the_agent_already_in_the_thread_and_says_who_wrote_it()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer hello")).GetProperty("id").GetInt64();
        await runner.NextDelivery(); await runner.End(root);
        await rosa.Post(ch, "and what tests does it have?", root);        // no @mention: it's their thread
        var d = await runner.NextDelivery();
        Assert.Contains("from=\"@rosa\"", d.Text); Assert.Contains("owner=\"false\"", d.Text);
        Assert.Equal(root, d.ThreadId);
    }

    [Fact]
    public async Task One_turn_at_a_time_per_thread_later_messages_queue()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer first")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        await rosa.Post(ch, "second", root);
        await Task.Delay(300);                                              // negative check: nothing may arrive while the turn runs
        Assert.False(runner.Delivered.Reader.TryRead(out _));
        await runner.End(root);
        var d = await runner.NextDelivery();
        Assert.Contains(">second<", d.Text);
    }

    [Fact]
    public async Task A_mention_while_the_runner_is_offline_waits_and_goes_out_when_it_connects()
    {
        var (app, alex, rosa, ch, runner) = await World(connect: false);
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer are you there?")).GetProperty("id").GetInt64();
        var t = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{root}");
        var agent = t.GetProperty("agents")[0];
        Assert.False(agent.GetProperty("online").GetBoolean());
        Assert.Equal(1, agent.GetProperty("queued").GetInt32());
        await runner.Connect();
        var d = await runner.NextDelivery();
        Assert.Contains("are you there?", d.Text);
    }

    [Fact]
    public async Task Streamed_events_become_thread_posts_and_tool_rows_and_browsers_hear_them()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var hub = rosa.Hub(); var deltas = new List<string>(); var created = new List<string>();
        hub.On<JsonElement>("post-created", p => { lock (created) created.Add(p.GetProperty("kind").GetString()!); });
        hub.On<JsonElement>("post-delta", p => { lock (deltas) deltas.Add(p.GetProperty("text").GetString()!); });
        await hub.StartAsync();
        var root = (await alex.Post(ch, "@reviewer hi")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        await runner.Say(root, "Hel"); await runner.Say(root, "lo");
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolStart, null, "t1", "read", "read", "running"));
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolUpdate, null, "t1", "README.md", null, null));     // the detail arrives later
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolEnd, null, "t1", null, null, "completed"));
        await runner.Say(root, "Done."); await runner.End(root);
        var t = await rosa.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{root}");
        var replies = t.GetProperty("replies").EnumerateArray().ToList();
        Assert.Equal(new[] { "text", "tool", "text" }, replies.Select(x => x.GetProperty("kind").GetString()).ToArray());
        Assert.Equal("Hello", replies[0].GetProperty("body").GetString());
        Assert.Equal("completed", replies[1].GetProperty("meta").GetProperty("status").GetString());
        Assert.Equal("README.md", replies[1].GetProperty("body").GetString());          // an update fills in the row; it doesn't end the tool
        Assert.All(replies, x => Assert.False(x.GetProperty("streaming").GetBoolean()));
        Assert.Equal("lo", Assert.Single(deltas, d => d == "lo"));
        Assert.Contains("tool", created);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Only_the_owner_can_answer_a_permission_ask_and_the_answer_reaches_the_runner()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer do it")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        await runner.Conn!.InvokeAsync("RequestPermission", new PermissionRequestMsg(root, "req1", "Run ls", new[] { new PermissionOption("allow", "Allow once", "allow_once") }));
        var t = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{root}");
        var ask = t.GetProperty("replies").EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "ask");
        var id = ask.GetProperty("id").GetInt64();
        Assert.Equal(alex.Id, ask.GetProperty("meta").GetProperty("ownerId").GetInt64());
        var opt = ask.GetProperty("meta").GetProperty("options")[0];       // the web app reads id / name / kind
        Assert.Equal(("allow", "Allow once", "allow_once"), (opt.GetProperty("id").GetString(), opt.GetProperty("name").GetString(), opt.GetProperty("kind").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.PostAsJsonAsync($"/api/posts/{id}/answer", new { optionId = "allow" })).StatusCode);
        Assert.False(runner.Answers.Reader.TryRead(out _));
        (await alex.Http.PostAsJsonAsync($"/api/posts/{id}/answer", new { optionId = "allow" })).EnsureSuccessStatusCode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ans = await runner.Answers.Reader.ReadAsync(cts.Token);
        Assert.Equal(("req1", "allow"), (ans.RequestId, ans.OptionId));
        Assert.Equal(HttpStatusCode.BadRequest, (await alex.Http.PostAsJsonAsync($"/api/posts/{id}/answer", new { optionId = "allow" })).StatusCode);   // once
    }

    [Fact]
    public async Task An_agents_own_post_never_triggers_itself_and_agent_to_agent_turns_stop_after_three()
    {
        var (app, alex, rosa, ch, a1) = await World();
        using var _a = app; await using var _b = a1;
        await using var a2 = await FakeRunner.Enrol(app, alex, "planner");
        (await alex.Http.PutAsync($"/api/channels/{ch}/members/{a2.AgentId}", null)).EnsureSuccessStatusCode();
        await a2.Connect();
        var root = (await alex.Post(ch, "@reviewer start")).GetProperty("id").GetInt64();
        // reviewer mentions itself and planner; they keep mentioning each other
        await a1.NextDelivery();
        await a1.Say(root, "talking to myself @reviewer and to @planner"); await a1.End(root);
        var turns = 0;
        for (var i = 0; i < 6; i++)
        {
            var (runner, other) = i % 2 == 0 ? (a2, "reviewer") : (a1, "planner");
            using var cts = new CancellationTokenSource(i < 3 ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(700));
            try { await runner.Delivered.Reader.ReadAsync(cts.Token); } catch (OperationCanceledException) { break; }
            turns++;
            await runner.Say(root, $"ping @{other}"); await runner.End(root);
        }
        Assert.Equal(3, turns);                  // three agent-to-agent turns, then they stop until a person speaks
        await alex.Post(ch, "ok carry on", root);
        Assert.NotNull(await a1.NextDelivery());
    }
}
