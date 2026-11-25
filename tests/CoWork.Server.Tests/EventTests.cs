using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoWork.Contracts;
using CoWork.Server;
using Dapper;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoWork.Server.Tests;

/// <summary>The event store, the thread stream and what is recorded of an agent's work (round 1 of docs/build/brief-4.md).</summary>
public class EventTests
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

    sealed class Ev
    {
        public long Position { get; set; } public string StreamId { get; set; } = ""; public int Version { get; set; } public string Type { get; set; } = "";
        public string Data { get; set; } = ""; public long? ActorId { get; set; } public long? OnBehalfOf { get; set; } public string? Correlation { get; set; } public long? Causation { get; set; }
    }
    static List<Ev> Events(TestApp app, string? where = null)
    {
        using var c = app.Sql();
        return c.Query<Ev>($"SELECT position, stream_id AS StreamId, version, type, data, actor_id AS ActorId, on_behalf_of AS OnBehalfOf, correlation, causation FROM events {where} ORDER BY position").ToList();
    }
    static JsonElement Data(Ev e) => JsonDocument.Parse(e.Data).RootElement;

    /// <summary>Waits for a row to appear (a signal, not a clock).</summary>
    static async Task<T> Until<T>(Func<T?> probe, int seconds = 15) where T : class
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (probe() is { } v) return v; await Task.Delay(50); }
        throw new TimeoutException("it never happened");
    }

    static IEventStore Store(TestApp app) => (IEventStore)app.Services.GetService(typeof(IEventStore))!;

    // ---- the store ----

    [Fact]
    public async Task The_store_numbers_positions_globally_and_versions_per_stream_and_replays_in_order()
    {
        using var app = new TestApp(); await app.Setup();
        var store = Store(app);
        var a = store.Append(new[] { new NewEvent("s-1", "One", new { n = 1 }), new NewEvent("s-2", "Other", new { n = 2 }), new NewEvent("s-1", "Two", new { n = 3 }, 7, 8, "turn", 99) });
        Assert.Equal(new[] { 1, 1, 2 }, a.Select(e => e.Version));
        Assert.Equal(a[0].Position + 1, a[1].Position);
        Assert.Equal((7L, 8L, "turn", 99L), (a[2].ActorId, a[2].OnBehalfOf, a[2].Correlation, a[2].Causation));
        Assert.Equal(new[] { "One", "Two" }, store.ReadStream("s-1").Select(e => e.Type));
        Assert.Equal(new[] { "Two" }, store.ReadStream("s-1", afterVersion: 1).Select(e => e.Type));
        Assert.Equal(new[] { "Other", "Two" }, store.ReadAll(a[0].Position).Select(e => e.Type));
        Assert.Equal(a[2].Position, store.Head());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_batch_can_name_an_earlier_event_in_it_as_its_cause()
    {
        using var app = new TestApp(); await app.Setup();
        var e = Store(app).Append(new[] { new NewEvent("s", "A", new { }), new NewEvent("s", "B", new { }, CausedByIndex: 0) });
        Assert.Equal(e[0].Position, e[1].Causation);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Two_writers_on_one_stream_cant_both_win_an_expected_version()
    {
        using var app = new TestApp(); await app.Setup();
        var store = Store(app);
        store.Append(new[] { new NewEvent("s", "A", new { }, ExpectedVersion: 0) });
        Assert.Throws<ConcurrencyException>(() => store.Append(new[] { new NewEvent("s", "B", new { }, ExpectedVersion: 0) }));
        store.Append(new[] { new NewEvent("s", "B", new { }, ExpectedVersion: 1) });
        Assert.Equal(2, store.ReadStream("s").Count);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failed_append_writes_nothing_and_subscribers_hear_each_commit_in_order()
    {
        using var app = new TestApp(); await app.Setup();
        var store = Store(app);
        var heard = new List<string>();
        using var sub = store.Subscribe(b => { lock (heard) heard.AddRange(b.Select(e => e.Type)); });
        store.Append(new[] { new NewEvent("s", "A", new { }), new NewEvent("s", "B", new { }) });
        // the second event is rejected (MessagePosted for a channel that doesn't exist): the first must roll back with it
        Assert.ThrowsAny<Exception>(() => store.Append(new[] { new NewEvent("s", "C", new { }), new NewEvent("thread-1", "MessagePosted", new { postId = 1, channelId = 999, threadId = (long?)null, body = "x" }, 1) }));
        Assert.Equal(new[] { "A", "B" }, store.ReadStream("s").Select(e => e.Type));
        Assert.Equal(new[] { "A", "B" }, heard);
        await Task.CompletedTask;
    }

    // ---- a thread as events ----

    [Fact]
    public async Task A_mention_is_recorded_as_the_thread_starting_the_agent_joining_and_the_message_delivered_with_who_and_why()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer what does this repository do?")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        var evs = Events(app, "WHERE type IN ('MessagePosted','ThreadStarted','AgentJoinedThread','MessageDelivered','TurnStarted')");
        Assert.Equal(new[] { "MessagePosted", "ThreadStarted", "AgentJoinedThread", "MessageDelivered", "TurnStarted" }, evs.Select(e => e.Type));
        Assert.Equal(new[] { $"channel-{ch}", $"thread-{root}", $"thread-{root}", $"thread-{root}", $"thread-{root}" }, evs.Select(e => e.StreamId));
        Assert.Equal(new[] { 1, 2, 3, 4 }, evs.Skip(1).Select(e => e.Version));
        Assert.Equal(alex.Id, evs[0].ActorId);                                        // the author is the caller
        Assert.Equal(evs[0].Position, evs[1].Causation);                              // the mention started the thread
        Assert.Equal(alex.Id, evs[3].OnBehalfOf);                                     // delivered on behalf of Alex
        var turn = evs[3].Correlation; Assert.False(string.IsNullOrEmpty(turn));
        Assert.Equal(turn, evs[4].Correlation);                                       // one id per turn
        Assert.Equal(evs[3].Position, evs[4].Causation);                              // the turn started because of the delivery
        Assert.Equal(runner.AgentId, evs[4].ActorId); Assert.Equal(alex.Id, evs[4].OnBehalfOf);
        Assert.Contains("<cowork-message from=\"@alex\"", Data(evs[3]).GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_answer_is_stored_in_segments_at_tool_boundaries_and_the_end_and_the_posts_follow()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer hi")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        await runner.Say(root, "Let me "); await runner.Say(root, "look.");
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolStart, null, "t1", "read", "read", "running"));
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolUpdate, null, "t1", "README.md", null, null));
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolEnd, null, "t1", null, null, "completed"));
        await runner.Say(root, "It's an app."); await runner.End(root);
        var types = Events(app, $"WHERE stream_id='thread-{root}'").Select(e => e.Type).SkipWhile(t => t != "TurnStarted").ToArray();
        Assert.Equal(new[] { "TurnStarted", "AgentReplied", "AgentReplied", "ToolCallStarted", "ToolCallDetailed", "ToolCallFinished", "AgentReplied", "AgentReplied", "TurnEnded" }, types);
        var replied = Events(app, "WHERE type='AgentReplied'").Select(Data).ToList();
        Assert.Equal("Let me look.", string.Concat(replied.Take(2).Select(d => d.GetProperty("text").GetString())));
        Assert.False(replied[0].GetProperty("complete").GetBoolean()); Assert.True(replied[1].GetProperty("complete").GetBoolean());   // closed at the tool call
        Assert.Equal("It's an app.", string.Concat(replied.Skip(2).Select(d => d.GetProperty("text").GetString())));
        Assert.True(replied[^1].GetProperty("complete").GetBoolean());                                                                  // closed at the end of the turn
        var t = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{root}");
        Assert.Equal(new[] { "Let me look.", "README.md", "It's an app." }, t.GetProperty("replies").EnumerateArray().Select(x => x.GetProperty("body").GetString()).ToArray());
        // every post and thread row the app reads came from an event: the position of the last one that changed it
        using var c = app.Sql();
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM posts WHERE position=0"));
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM open_turns"));                                    // the turn ended
        Assert.True(c.ExecuteScalar<long>("SELECT position FROM runner_positions") >= c.ExecuteScalar<long>("SELECT MAX(position) FROM events WHERE type='TurnStarted'"));   // nothing queued: the runner is caught up
    }

    [Fact]
    public async Task Text_that_keeps_coming_is_stored_every_few_seconds_not_only_at_the_end()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var old = Dispatcher.FlushIntervalMs; Dispatcher.FlushIntervalMs = 150;
        try
        {
            var root = (await alex.Post(ch, "@reviewer hi")).GetProperty("id").GetInt64();
            await runner.NextDelivery();
            await runner.Say(root, "one ");                    // stored at once: the post exists for anyone who catches up
            await runner.Say(root, "two ");                    // later: stored by the timer (the turn has not ended)
            var second = await Until(() => Events(app, "WHERE type='AgentReplied'").Skip(1).FirstOrDefault());
            Assert.Equal("two ", Data(second).GetProperty("text").GetString());
            using var c = app.Sql();
            Assert.Equal("one two ", c.ExecuteScalar<string>("SELECT body FROM posts WHERE kind='text' AND thread_id=@root", new { root }));
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT streaming FROM posts WHERE kind='text' AND thread_id=@root", new { root }));
            await runner.End(root);
            Assert.Equal(0, c.ExecuteScalar<int>("SELECT streaming FROM posts WHERE kind='text' AND thread_id=@root", new { root }));
        }
        finally { Dispatcher.FlushIntervalMs = old; }
    }

    [Fact]
    public async Task An_approval_is_caused_by_its_request_and_the_turn_belongs_to_the_person_who_started_it()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer hello")).GetProperty("id").GetInt64();
        await runner.NextDelivery(); await runner.End(root);
        await rosa.Post(ch, "@reviewer run git log -3", root);
        await runner.NextDelivery();
        await runner.Conn!.InvokeAsync("RequestPermission", new PermissionRequestMsg(root, "req1", "run git log -3", new[] { new PermissionOption("allow", "Allow once", "allow_once") }));
        var ask = Events(app, "WHERE type='PermissionRequested'").Single();
        var postId = Data(ask).GetProperty("postId").GetInt64();
        Assert.Equal(rosa.Id, ask.OnBehalfOf);                                                 // Rosa started this turn
        (await alex.Http.PostAsJsonAsync($"/api/posts/{postId}/answer", new { optionId = "allow" })).EnsureSuccessStatusCode();
        var granted = Events(app, "WHERE type='PermissionGranted'").Single();
        Assert.Equal((alex.Id, ask.Position, ask.Correlation, rosa.Id), (granted.ActorId, granted.Causation, granted.Correlation, granted.OnBehalfOf));
        Assert.Equal("answered", JsonDocument.Parse(app.Sql().ExecuteScalar<string>("SELECT meta FROM posts WHERE id=@postId", new { postId })!).RootElement.GetProperty("status").GetString());
        // a denial is its own event
        await runner.Conn!.InvokeAsync("RequestPermission", new PermissionRequestMsg(root, "req2", "rm -rf", new[] { new PermissionOption("allow", "Allow once", "allow_once") }));
        var second = Events(app, "WHERE type='PermissionRequested'").Last();
        (await alex.Http.PostAsJsonAsync($"/api/posts/{Data(second).GetProperty("postId").GetInt64()}/answer", new { optionId = (string?)null })).EnsureSuccessStatusCode();
        Assert.Single(Events(app, "WHERE type='PermissionDenied'"));
    }

    // ---- tool output ----

    [Fact]
    public async Task Tool_output_is_recorded_as_size_and_hash_unless_the_agent_records_it_in_full_up_to_64_kB()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer hi")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        async Task Tool(string id, string output)
        {
            var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
            await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolStart, null, id, "read", "read", "running"));
            await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolEnd, null, id, null, null, "completed", Encoding.UTF8.GetByteCount(output), sha, output));
        }
        await Tool("off", "secret contents");                                  // the switch is off: even if a runner sends the text, the server doesn't keep it
        var off = Data(Events(app, "WHERE type='ToolCallFinished'").Single());
        Assert.Equal(15, off.GetProperty("outputBytes").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("secret contents"))).ToLowerInvariant(), off.GetProperty("outputSha256").GetString());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("output").ValueKind);

        var agent = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/agents/{runner.AgentId}");
        Assert.False(agent.GetProperty("recordToolOutput").GetBoolean());
        (await alex.Http.PutAsJsonAsync($"/api/agents/{runner.AgentId}", new { displayName = "reviewer", about = "", instructions = "", harnessCommand = "opencode acp", workspace = "/tmp", recordToolOutput = true })).EnsureSuccessStatusCode();
        Assert.True((await alex.Http.GetFromJsonAsync<JsonElement>($"/api/agents/{runner.AgentId}")).GetProperty("recordToolOutput").GetBoolean());
        Assert.False(runner.Delivered.Reader.TryRead(out _));
        await Tool("on", "hello output");
        Assert.Equal("hello output", Data(Events(app, "WHERE type='ToolCallFinished'").Last()).GetProperty("output").GetString());

        await Tool("big", new string('x', 70_000));
        var big = Data(Events(app, "WHERE type='ToolCallFinished'").Last());
        Assert.Equal(64 * 1024, big.GetProperty("output").GetString()!.Length);
        Assert.True(big.GetProperty("outputTruncated").GetBoolean());
        Assert.Equal(70_000, big.GetProperty("outputBytes").GetInt64());      // the size is the real size
        // the posts the app reads never carry the output
        Assert.DoesNotContain("hello output", app.Sql().ExecuteScalar<string>("SELECT group_concat(meta) FROM posts WHERE kind='tool'"));
    }

    [Fact]
    public void Output_is_cut_on_a_character_boundary()
    {
        var (t, cut) = Dispatcher.Cap(new string('é', 40_000));          // 2 bytes each
        Assert.True(cut); Assert.Equal(32 * 1024, t.Length);
        Assert.Equal(("small", false), Dispatcher.Cap("small"));
    }

    // ---- recovery ----

    [Fact]
    public async Task A_turn_that_started_and_never_ended_is_interrupted_at_startup_and_the_thread_says_the_answer_was_cut_off()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var root = (await alex.Post(ch, "@reviewer hi")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        await runner.Say(root, "Half an ans");              // the server "crashes" here: a second server starts on the same database, the first one's work stops
        using (var c = app.Sql()) Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM posts WHERE streaming=1"));
        using var again = new TestApp(existingDb: app.DbPath);
        _ = again.Server;
        var inter = Events(again, "WHERE type='TurnInterrupted'");
        Assert.Single(inter);
        Assert.Equal($"thread-{root}", inter[0].StreamId);
        Assert.Equal(Events(again, "WHERE type='TurnStarted'").Single().Correlation, inter[0].Correlation);
        using var c2 = again.Sql();
        Assert.Equal(0, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM posts WHERE streaming=1"));
        Assert.Equal("Half an ans", c2.ExecuteScalar<string>("SELECT body FROM posts WHERE kind='text' AND thread_id IS NOT NULL"));
        Assert.Contains("cut off", c2.ExecuteScalar<string>("SELECT body FROM posts WHERE kind='notice'"));
        Assert.Equal(0, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM open_turns"));
        // and the next restart doesn't interrupt it again
        using var third = new TestApp(existingDb: app.DbPath); _ = third.Server;
        Assert.Single(Events(third, "WHERE type='TurnInterrupted'"));
    }

    // ---- live and catch-up ----

    [Fact]
    public async Task Live_messages_carry_the_position_of_the_event_and_the_post()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var hub = rosa.Hub(); var got = new List<(string Name, long At, long Position)>();
        foreach (var n in new[] { "post-created", "post-updated", "thread-updated" })
            hub.On<JsonElement>(n, p => { lock (got) got.Add((n, p.GetProperty("at").GetInt64(), n == "thread-updated" ? 0 : p.GetProperty("position").GetInt64())); });
        await hub.StartAsync();
        var root = (await alex.Post(ch, "@reviewer hi")).GetProperty("id").GetInt64();
        await Until(() => { lock (got) return got.Any(g => g.Name == "thread-updated") ? got : null; });
        lock (got) { Assert.Equal("post-created", got[0].Name); Assert.True(got[0].At > 0 && got[0].Position == got[0].At); }
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_browser_that_reconnects_gets_exactly_what_it_missed_for_the_channels_it_may_see()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var priv = await alex.Channel("leads", priv: true);
        var root = (await alex.Post(ch, "@reviewer hi")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        var seen = Store(app).Head();                                         // Rosa's browser has seen everything up to here
        // while it was away: an answer, a tool call, an approval, and a post in a private channel Rosa can't see
        await runner.Say(root, "Hello");
        await runner.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolStart, null, "t1", "read", "read", "running"));
        await runner.Conn!.InvokeAsync("RequestPermission", new PermissionRequestMsg(root, "r1", "run ls", new[] { new PermissionOption("allow", "Allow once", "allow_once") }));
        await alex.Post(priv, "secret plans");
        var hub = rosa.Hub(); await hub.StartAsync();
        var r = await hub.InvokeAsync<JsonElement>("CatchUp", seen);
        Assert.False(r.GetProperty("truncated").GetBoolean());
        var msgs = r.GetProperty("messages").EnumerateArray().ToList();
        var posts = msgs.Where(m => m.GetProperty("name").GetString() == "post-upsert").Select(m => m.GetProperty("payload")).ToList();
        Assert.Equal(new[] { "text", "tool", "ask" }, posts.Select(p => p.GetProperty("kind").GetString()).ToArray());
        Assert.Equal("Hello", posts[0].GetProperty("body").GetString());
        Assert.DoesNotContain("secret plans", r.GetRawText());
        Assert.Single(msgs, m => m.GetProperty("name").GetString() == "thread-updated");
        Assert.Equal(Store(app).Head(), r.GetProperty("head").GetInt64());
        // asking again from the head returns nothing new
        var again = await hub.InvokeAsync<JsonElement>("CatchUp", r.GetProperty("head").GetInt64());
        Assert.Empty(again.GetProperty("messages").EnumerateArray());
        // a browser ahead of the server (a reset database) is told to start afresh
        Assert.True((await hub.InvokeAsync<JsonElement>("CatchUp", 99999999L)).GetProperty("truncated").GetBoolean());
        await hub.DisposeAsync();
        // Alex can see the private channel and gets its post
        var alexHub = alex.Hub(); await alexHub.StartAsync();
        Assert.Contains("secret plans", (await alexHub.InvokeAsync<JsonElement>("CatchUp", seen)).GetRawText());
        await alexHub.DisposeAsync();
    }

    [Fact]
    public async Task Bootstrap_says_where_the_event_log_is_so_a_browser_knows_where_to_catch_up_from()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        await alex.Post(ch, "@reviewer hi");
        var b = await alex.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Equal(Store(app).Head(), b.GetProperty("position").GetInt64());
    }

    [Fact]
    public async Task Deleting_a_channel_is_a_recorded_fact()
    {
        var (app, alex, rosa, ch, runner) = await World();
        using var _a = app; await using var _b = runner;
        var other = await alex.Channel("old");
        (await alex.Http.DeleteAsync($"/api/channels/{other}")).EnsureSuccessStatusCode();
        var e = Events(app, "WHERE type='ChannelDeleted'").Single();
        Assert.Equal(($"channel-{other}", alex.Id), (e.StreamId, e.ActorId));
    }
}
