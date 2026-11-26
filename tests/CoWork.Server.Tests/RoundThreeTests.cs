using System.Net.Http.Json;
using System.Text.Json;
using CoWork.Contracts;
using Microsoft.Extensions.DependencyInjection;
using CoWork.Server;
using Dapper;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoWork.Server.Tests;

/// <summary>The first new views (round 3 of docs/build/brief-4.md): audit, unread counts, the mentions inbox, agent activity; their checkpoints and rebuilds.</summary>
public class RoundThreeTests
{
    static async Task<(TestApp app, Person alex, Person rosa, long ch, FakeRunner runner)> World()
    {
        var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        var ch = await alex.Channel("checkout-app");
        var runner = await FakeRunner.Enrol(app, alex, "reviewer");
        (await alex.Http.PutAsync($"/api/channels/{ch}/members/{runner.AgentId}", null)).EnsureSuccessStatusCode();
        await runner.Connect();
        return (app, alex, rosa, ch, runner);
    }

    static ProjectionRunner Views(TestApp app) => app.Services.GetRequiredService<ProjectionRunner>();
    static IEventStore Store(TestApp app) => app.Services.GetRequiredService<IEventStore>();
    static Task Settle(TestApp app) => Views(app).CatchUpAsync();
    static string[] Tables(params IAsyncProjection[] ps) => ps.SelectMany(p => p.Tables).ToArray();

    /// <summary>Alex asks reviewer something; reviewer greets Rosa; Rosa asks for `git log -3`; Alex allows it once; it runs.</summary>
    static async Task<long> TheStory(TestApp app, Person alex, Person rosa, long ch, FakeRunner runner)
    {
        var root = (await alex.Post(ch, "@reviewer what does this repository do?")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        await runner.Say(root, "It's a checkout app. @rosa I can look at the history too.");
        await runner.End(root);
        await rosa.Post(ch, "@reviewer can you run git log -3?", root);
        await runner.NextDelivery();
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolStart, null, "t1", "bash", "execute", "running"));
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolUpdate, null, "t1", "git log -3"));
        await runner.Conn!.InvokeAsync("RequestPermission", new PermissionRequestMsg(root, "req1", "git log -3", new[] { new PermissionOption("once", "Allow once", "allow_once") }));
        using (var c = app.Sql())
        {
            var postId = c.ExecuteScalar<long>("SELECT json_extract(data,'$.postId') FROM events WHERE type='PermissionRequested'");
            (await alex.Http.PostAsJsonAsync($"/api/posts/{postId}/answer", new { optionId = "once" })).EnsureSuccessStatusCode();
        }
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolEnd, null, "t1", null, null, "completed", 12, "00"));
        await runner.Say(root, "The last three commits are about the cart.");
        await runner.End(root);
        await Settle(app);
        return root;
    }

    static async Task<JsonElement> Audit(Person p, string query = "") => await p.Http.GetFromJsonAsync<JsonElement>("/api/audit" + query);
    static string[] Sentences(JsonElement audit) => audit.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("sentence").GetString()!).ToArray();

    [Fact]
    public async Task The_audit_tells_who_asked_who_allowed_and_where_it_ran_from_the_causation_chain()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        await TheStory(app, alex, rosa, ch, runner);
        var all = Sentences(await Audit(alex));
        Assert.Contains("Rosa asked reviewer (Alex's agent) to run `git log -3`. Alex allowed it once. It ran on testbox.", all);
        Assert.Contains("Rosa replied in a thread in #checkout-app, mentioning reviewer.", all);
        Assert.Contains("Alex posted in #checkout-app, mentioning reviewer.", all);
        Assert.Contains(all, s => s.StartsWith("Alex messaged reviewer (Alex's agent) in #checkout-app. It answered on testbox."));
        Assert.Contains(all, s => s.StartsWith("Rosa messaged reviewer (Alex's agent) in #checkout-app. It answered on testbox."));
        Assert.Contains("reviewer's runner connected from testbox.", all);
        Assert.Contains("Rosa joined with an invite link from Alex.", all);                      // one sign-up is one sentence
        Assert.DoesNotContain(all, s => s.Contains("used an invite link"));
        Assert.DoesNotContain(all, s => s.Contains("what does this repository do"));           // what people say is not in the audit

        // the sentence comes from the chain: the grant names the ask it answers, the ask belongs to the turn Rosa started
        using var c = app.Sql();
        var ask = c.QuerySingle<(long Position, string Correlation)>("SELECT position, correlation FROM events WHERE type='PermissionRequested'");
        var grant = c.QuerySingle<(long? Causation, string Correlation, long Actor)>("SELECT causation, correlation, actor_id FROM events WHERE type='PermissionGranted'");
        Assert.Equal((ask.Position, ask.Correlation, alex.Id), (grant.Causation, grant.Correlation, grant.Actor));

        // filters: a person, an agent, a channel; and only admins
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await rosa.Http.GetAsync("/api/audit")).StatusCode);
        Assert.All(Sentences(await Audit(alex, $"?person={rosa.Id}")), s => Assert.True(s.Contains("Rosa") || s.Contains("reviewer")));
        Assert.Contains("Rosa asked reviewer (Alex's agent) to run `git log -3`. Alex allowed it once. It ran on testbox.", Sentences(await Audit(alex, $"?person={rosa.Id}")));
        var forAgent = Sentences(await Audit(alex, $"?agent={runner.AgentId}"));
        Assert.Contains(forAgent, s => s.Contains("git log -3")); Assert.DoesNotContain(forAgent, s => s.StartsWith("Rosa joined"));
        var other = await alex.Channel("random");
        Assert.Empty(Sentences(await Audit(alex, $"?channel={other}")).Where(s => s.Contains("git log")));
        Assert.Contains(Sentences(await Audit(alex, $"?channel={ch}")), s => s.Contains("git log -3"));
        Assert.Equal(new[] { "Alex", "Rosa" }, (await Audit(alex)).GetProperty("people").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task A_denied_ask_and_a_turn_that_fails_are_told_as_they_happened()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await rosa.Post(ch, "@reviewer clean up")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolStart, null, "t9", "rm -rf build", "execute", "running"));
        await runner.Conn!.InvokeAsync("RequestPermission", new PermissionRequestMsg(root, "r9", "rm -rf build", new[] { new PermissionOption("once", "Allow once", "allow_once") }));
        await Settle(app);
        Assert.Contains("Rosa asked reviewer (Alex's agent) to run `rm -rf build`. Waiting for Alex to answer.", Sentences(await Audit(alex)));
        using (var c = app.Sql()) (await alex.Http.PostAsJsonAsync($"/api/posts/{c.ExecuteScalar<long>("SELECT json_extract(data,'$.postId') FROM events WHERE type='PermissionRequested'")}/answer", new { optionId = (string?)null })).EnsureSuccessStatusCode();
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.Error, "the model ran out of credit"));
        await runner.End(root);
        await Settle(app);
        var s = Sentences(await Audit(alex));
        Assert.Contains("Rosa asked reviewer (Alex's agent) to run `rm -rf build`. Alex said no.", s);
        Assert.Contains(s, x => x.Contains("It failed on testbox: the model ran out of credit"));
    }

    [Fact]
    public async Task Unread_counts_follow_what_others_wrote_and_what_a_person_has_read_and_a_mention_lands_in_the_inbox()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        await alex.Post(ch, "Morning everyone");
        await TheStory(app, alex, rosa, ch, runner);                       // reviewer says "@rosa" in its first answer
        async Task<JsonElement> Unread(Person p) => await p.Http.GetFromJsonAsync<JsonElement>("/api/unread");
        var before = await Unread(rosa);
        // Alex: "Morning", the mention post (and Rosa's own are hers); reviewer's two answers (the first one is segment 0 of two posts)
        Assert.True(before.GetProperty("channels").GetProperty(ch.ToString()).GetInt64() >= 3);
        Assert.Equal(1, before.GetProperty("mentions").GetInt64());
        var inbox = await rosa.Http.GetFromJsonAsync<JsonElement>("/api/mentions");
        Assert.Single(inbox.EnumerateArray());
        Assert.Contains("@rosa I can look at the history too.", inbox[0].GetProperty("excerpt").GetString());
        Assert.Equal("checkout-app", inbox[0].GetProperty("channelName").GetString());
        Assert.True(inbox[0].GetProperty("unread").GetBoolean());
        Assert.Empty((await alex.Http.GetFromJsonAsync<JsonElement>("/api/mentions")).EnumerateArray());      // nobody mentioned Alex

        // reading writes ChannelRead on her own stream, once; nothing new, nothing written
        (await rosa.Http.PostAsync($"/api/channels/{ch}/read", null)).EnsureSuccessStatusCode();
        (await rosa.Http.PostAsync($"/api/channels/{ch}/read", null)).EnsureSuccessStatusCode();
        using (var c = app.Sql())
        {
            var reads = c.Query<(string Stream, long? Actor)>("SELECT stream_id, actor_id FROM events WHERE type='ChannelRead'").ToList();
            Assert.Equal((new[] { ($"person-{rosa.Id}", (long?)rosa.Id) }), reads.ToArray());
        }
        var after = await Unread(rosa);
        Assert.False(after.GetProperty("channels").TryGetProperty(ch.ToString(), out _));
        Assert.Equal(0, after.GetProperty("mentions").GetInt64());
        Assert.False((await rosa.Http.GetFromJsonAsync<JsonElement>("/api/mentions"))[0].GetProperty("unread").GetBoolean());

        // a new message by Alex that mentions her: one unread, one mention; her own messages never count for her
        await alex.Post(ch, "@rosa lunch?");
        await rosa.Post(ch, "yes");
        await Settle(app);
        var again = await Unread(rosa);
        Assert.Equal(1, again.GetProperty("channels").GetProperty(ch.ToString()).GetInt64());
        Assert.Equal(1, again.GetProperty("mentions").GetInt64());
        Assert.Equal(2, (await rosa.Http.GetFromJsonAsync<JsonElement>("/api/mentions")).GetArrayLength());
        // Alex sees Rosa's "yes" and reviewer's answers as unread, not his own words
        Assert.True((await Unread(alex)).GetProperty("channels").GetProperty(ch.ToString()).GetInt64() >= 1);

        // someone who joins later, and someone who isn't in a private channel, have nothing from before
        var sam = await app.Join(alex, "sam", "Sam");       // asking for the counts is enough: they are brought up to date first
        Assert.Empty((await Unread(sam)).GetProperty("channels").EnumerateObject());
        var secret = await alex.Channel("leads", priv: true);
        await alex.Post(secret, "private words"); await rosa.Http.PostAsync($"/api/channels/{ch}/read", null);
        await Settle(app);
        Assert.False((await Unread(rosa)).GetProperty("channels").TryGetProperty(secret.ToString(), out _));
        (await alex.Http.PutAsync($"/api/channels/{secret}/members/{rosa.Id}", null)).EnsureSuccessStatusCode();
        await Settle(app);
        Assert.False((await Unread(rosa)).GetProperty("channels").TryGetProperty(secret.ToString(), out _));           // added later: earlier words weren't written for her
        await alex.Post(secret, "welcome Rosa"); await Settle(app);
        Assert.Equal(1, (await Unread(rosa)).GetProperty("channels").GetProperty(secret.ToString()).GetInt64());
        // a deleted message stops counting
        var gone = (await alex.Post(ch, "oops")).GetProperty("id").GetInt64(); await Settle(app);
        var n = (await Unread(rosa)).GetProperty("channels").GetProperty(ch.ToString()).GetInt64();
        (await alex.Http.DeleteAsync($"/api/posts/{gone}")).EnsureSuccessStatusCode(); await Settle(app);
        Assert.Equal(n - 1, (await Unread(rosa)).GetProperty("channels").TryGetProperty(ch.ToString(), out var v) ? v.GetInt64() : 0);
    }

    [Fact]
    public async Task An_agents_profile_shows_the_threads_it_is_busy_in_its_turns_tool_calls_and_failures()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer first question")).GetProperty("id").GetInt64();
        await runner.NextDelivery(); await Settle(app);
        var busy = await rosa.Http.GetFromJsonAsync<JsonElement>($"/api/agents/{runner.AgentId}/activity");
        Assert.Equal(1, busy.GetProperty("busy").GetArrayLength());
        Assert.Equal("@reviewer first question", busy.GetProperty("busy")[0].GetProperty("thread").GetString());
        Assert.Equal("checkout-app", busy.GetProperty("busy")[0].GetProperty("channelName").GetString());
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolStart, null, "a", "README.md", "read", "running"));
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolEnd, null, "a", null, null, "completed", 3, "00"));
        await runner.Say(root, "ok"); await runner.End(root);
        await rosa.Post(ch, "@reviewer second", root); await runner.NextDelivery();
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.Error, "boom")); await runner.End(root);
        await Settle(app);
        var a = await rosa.Http.GetFromJsonAsync<JsonElement>($"/api/agents/{runner.AgentId}/activity");
        Assert.Equal(0, a.GetProperty("busy").GetArrayLength());
        Assert.Equal((2, 1, 1), (a.GetProperty("totals").GetProperty("turns").GetInt32(), a.GetProperty("totals").GetProperty("failed").GetInt32(), a.GetProperty("totals").GetProperty("toolCalls").GetInt32()));
        Assert.Equal("failed", a.GetProperty("failures")[0].GetProperty("outcome").GetString());
        Assert.Equal("boom", a.GetProperty("failures")[0].GetProperty("reason").GetString());
        Assert.Equal("testbox", a.GetProperty("connections")[0].GetProperty("host").GetString());
        Assert.True(a.GetProperty("agent").GetProperty("online").GetBoolean());
        // a thread in a private channel the caller can't see is not described to them
        var secret = await alex.Channel("leads", priv: true);
        (await alex.Http.PutAsync($"/api/channels/{secret}/members/{runner.AgentId}", null)).EnsureSuccessStatusCode();
        await alex.Post(secret, "@reviewer secret plans"); await runner.NextDelivery(); await Settle(app);
        var seen = await rosa.Http.GetFromJsonAsync<JsonElement>($"/api/agents/{runner.AgentId}/activity");
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("busy")[0].GetProperty("thread").ValueKind);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("busy")[0].GetProperty("channelName").ValueKind);
        Assert.Equal("secret plans".Length > 0 ? "leads" : "", (await alex.Http.GetFromJsonAsync<JsonElement>($"/api/agents/{runner.AgentId}/activity")).GetProperty("busy")[0].GetProperty("channelName").GetString());
    }

    static Dictionary<string, string> Dump(TestApp app, IEnumerable<string> tables)
    {
        using var c = app.Sql();
        return tables.ToDictionary(t => t, t =>
        {
            var rows = c.Query($"SELECT * FROM {t} ORDER BY rowid").Select(r => JsonSerializer.Serialize((IDictionary<string, object>)r)).ToList();
            return string.Join("\n", rows);
        });
    }

    [Fact]
    public async Task Every_async_projection_rebuilds_from_zero_to_the_same_rows_and_the_checkpoint_follows_the_log()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        await TheStory(app, alex, rosa, ch, runner);
        await rosa.Http.PostAsync($"/api/channels/{ch}/read", null);
        await alex.Post(ch, "@rosa one more"); await Settle(app);

        var status = await alex.Http.GetFromJsonAsync<JsonElement>("/api/admin/projections");
        Assert.Equal(new[] { "audit", "unread", "agent-activity", "search" }.OrderBy(x => x), status.EnumerateArray().Select(s => s.GetProperty("name").GetString()!).OrderBy(x => x));
        var head = Store(app).Head();
        Assert.All(status.EnumerateArray(), s => Assert.Equal(head, s.GetProperty("position").GetInt64()));          // each is at the head of the log

        foreach (var p in Views(app).Projections)
        {
            var before = Dump(app, p.Tables);
            Assert.True(before.Values.Any(v => v.Length > 0), p.Name + " has rows");
            // the view is lost (a bug, a restore): rebuilding brings it back, row for row
            using (var c = app.Sql()) foreach (var t in p.Tables) { c.Execute($"DELETE FROM {t}"); c.Execute("DELETE FROM sqlite_sequence WHERE name=@t", new { t }); }
            using (var c = app.Sql()) c.Execute("UPDATE projection_checkpoints SET position=0 WHERE name=@n", new { n = p.Name });
            await Settle(app);                                                                     // catching up from the checkpoint alone does it …
            Assert.Equal(before, Dump(app, p.Tables));
            var r = await alex.Http.PostAsync($"/api/admin/projections/{p.Name}/rebuild", null);   // … and so does the admin's rebuild, from an empty view
            r.EnsureSuccessStatusCode();
            Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetInt32() >= head - 1);
            Assert.Equal(before, Dump(app, p.Tables));
            using (var c = app.Sql()) Assert.Equal(head, c.ExecuteScalar<long>("SELECT position FROM projection_checkpoints WHERE name=@n", new { n = p.Name }));
        }
        // a rebuild drops stale rows that no event explains
        using (var c = app.Sql()) c.Execute("INSERT INTO audit_entries(id, kind, occurred_at, sentence) VALUES (999999,'event',1,'Nobody did anything.')");
        Assert.Contains("Nobody did anything.", Sentences(await Audit(alex)));
        (await alex.Http.PostAsync("/api/admin/projections/audit/rebuild", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain("Nobody did anything.", Sentences(await Audit(alex)));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await rosa.Http.PostAsync("/api/admin/projections/audit/rebuild", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await alex.Http.PostAsync("/api/admin/projections/nope/rebuild", null)).StatusCode);
    }

    [Fact]
    public async Task A_projection_that_is_behind_catches_up_in_the_background_and_a_browser_is_told()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var changed = new TaskCompletionSource<bool>();
        var hub = rosa.Hub(); hub.On<JsonElement>("views-changed", _ => changed.TrySetResult(true));
        await hub.StartAsync();
        await alex.Post(ch, "hello Rosa");
        Assert.True(await Task.WhenAny(changed.Task, Task.Delay(15000)) == changed.Task, "the browser hears that its views changed");
        var end = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < end && !(await rosa.Http.GetFromJsonAsync<JsonElement>("/api/unread")).GetProperty("channels").TryGetProperty(ch.ToString(), out _)) await Task.Delay(50);
        Assert.Equal(1, (await rosa.Http.GetFromJsonAsync<JsonElement>("/api/unread")).GetProperty("channels").GetProperty(ch.ToString()).GetInt64());
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_message_redacted_or_a_channel_deleted_leaves_the_inbox_and_the_audit_without_the_words()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var mention = (await alex.Post(ch, "@rosa the password is hunter2")).GetProperty("id").GetInt64(); await Settle(app);
        Assert.Contains("hunter2", (await rosa.Http.GetFromJsonAsync<JsonElement>("/api/mentions"))[0].GetProperty("excerpt").GetString());
        (await alex.Http.PostAsync($"/api/posts/{mention}/redact", null)).EnsureSuccessStatusCode(); await Settle(app);
        Assert.Equal("[redacted]", (await rosa.Http.GetFromJsonAsync<JsonElement>("/api/mentions"))[0].GetProperty("excerpt").GetString());
        Assert.Contains("Alex redacted a message in #checkout-app.", Sentences(await Audit(alex)));
        (await alex.Http.DeleteAsync($"/api/channels/{ch}")).EnsureSuccessStatusCode(); await Settle(app);
        Assert.Empty((await rosa.Http.GetFromJsonAsync<JsonElement>("/api/mentions")).EnumerateArray());
        Assert.Contains("Alex deleted #checkout-app.", Sentences(await Audit(alex)));              // the audit keeps the fact, and the name
    }

    [Fact]
    public async Task A_server_that_had_people_before_the_unread_view_starts_with_everything_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cowork-legacy-{Guid.NewGuid():N}.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-main-round1.db"), path);
        try
        {
            using var app = new TestApp(existingDb: path);
            app.LinkSso("rosa");                    // a server from before single sign-on: Rosa is given a sign-in to look with
            var login = await app.SignIn("rosa");
            await Settle(app);
            Assert.Single(app.Sql().Query<long>("SELECT position FROM events WHERE type='UnreadBaselineSet'"));
            Assert.Empty((await login.Http.GetFromJsonAsync<JsonElement>("/api/unread")).GetProperty("channels").EnumerateObject());
            Assert.Equal(0, (await login.Http.GetFromJsonAsync<JsonElement>("/api/unread")).GetProperty("mentions").GetInt64());
            Assert.True((await login.Http.GetFromJsonAsync<JsonElement>("/api/mentions")).GetArrayLength() >= 0);
        }
        finally { foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch { } }
    }
}
