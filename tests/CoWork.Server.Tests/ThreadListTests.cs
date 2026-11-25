using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoWork.Server.Tests;

/// <summary>Brief 8: threads you can find. Threads under their channel in the list (titles, most recent activity first, at most five, done ones
/// for a day unless unread), All threads with its filters, renaming, unread replies, who sees which threads, and a mention that can't reach
/// its agent saying so.</summary>
public class ThreadListTests
{
    static async Task<JsonElement> Json(HttpResponseMessage r) { r.EnsureSuccessStatusCode(); return await r.Content.ReadFromJsonAsync<JsonElement>(); }

    static async Task Until(Func<Task<bool>> done, string what, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!await done()) { if (DateTime.UtcNow > end) throw new Xunit.Sdk.XunitException("timed out waiting for " + what); await Task.Delay(25); }
    }

    /// <summary>Alex (admin, lead), Rosa and Sam in Checkout app with #checkout-app; Jo outside it; reviewer, Alex's agent, connected, taking work
    /// from the project's members.</summary>
    static async Task<(TestApp app, Person alex, Person rosa, Person sam, Person jo, long project, long ch, FakeRunner reviewer)> World()
    {
        var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa"); var sam = await app.Join(alex, "sam", "Sam"); var jo = await app.Join(alex, "jo", "Jo");
        var reviewer = await FakeRunner.Enrol(app, alex, "reviewer");
        await reviewer.Connect();
        var project = (await Json(await alex.Http.PostAsJsonAsync("/api/projects", new
        {
            name = "Checkout app", about = "Cart, payment and order confirmation", memberIds = new[] { rosa.Id, sam.Id },
            workspace = new { kind = "repository", url = "/srv/git/checkout-app.git", branch = "main" },
            channels = new[] { new { name = "checkout-app", kind = "text" } },
        }))).GetProperty("id").GetInt64();
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
        var ch = app.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='checkout-app'");
        return (app, alex, rosa, sam, jo, project, ch, reviewer);
    }

    /// <summary>An agent's tool call over MCP, with its runner's token, for a thread it's in.</summary>
    static async Task Resolve(TestApp app, FakeRunner agent, long thread, string title)
    {
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agent.Token);
        http.DefaultRequestHeaders.Add("X-Cowork-Thread", thread.ToString());
        var res = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "cowork_resolve_thread", arguments = new { title, summary = title + "." } } });
        res.EnsureSuccessStatusCode();
        Assert.False((bool)JsonNode.Parse(await res.Content.ReadAsStringAsync())!["result"]!["isError"]!);
    }

    static async Task<JsonElement[]> Listed(Person who, long channel)
    {
        var list = await Json(await who.Http.GetAsync("/api/thread-list"));
        return list.EnumerateArray().Where(c => c.GetProperty("channelId").GetInt64() == channel).SelectMany(c => c.GetProperty("threads").EnumerateArray()).ToArray();
    }
    static async Task<int> Total(Person who, long channel) =>
        (await Json(await who.Http.GetAsync("/api/thread-list"))).EnumerateArray().Where(c => c.GetProperty("channelId").GetInt64() == channel).Select(c => c.GetProperty("total").GetInt32()).FirstOrDefault();
    static async Task<JsonElement> Thread(Person who, long channel, long root) => (await Json(await who.Http.GetAsync($"/api/channels/{channel}/threads"))).EnumerateArray().First(t => t.GetProperty("rootId").GetInt64() == root);
    static string Title(JsonElement t) => t.GetProperty("title").GetString()!;
    static int Unread(JsonElement t) => t.GetProperty("unread").GetInt32();
    static long Id(JsonElement post) => post.GetProperty("id").GetInt64();

    [Theory]
    [InlineData("@reviewer what does the tax step do with refunds in January and February?", "What does the tax step do with refunds…")]
    [InlineData("@reviewer, why is the total a cent out?", "Why is the total a cent out?")]
    [InlineData("Can @reviewer look at this", "Can look at this")]
    [InlineData("@reviewer", "Untitled thread")]
    [InlineData("supercalifragilisticexpialidocious-and-then-some-more-words-that-never-stop-anywhere-at-all", "Supercalifragilisticexpialidocious-and-then-some-more-words…")]
    public void A_thread_without_a_name_is_titled_by_the_first_words_of_its_post_without_mentions(string body, string title) =>
        Assert.Equal(title, ThreadList.AutoTitle(body));

    [Fact]
    public async Task A_title_is_the_outcome_when_resolved_else_the_first_words_and_anyone_in_the_thread_can_rename_it()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _r = reviewer;
        var root = Id(await rosa.Post(ch, "@reviewer why is the refund total a cent out on large orders?"));
        await reviewer.NextDelivery();
        Assert.Equal("Why is the refund total a cent out…", Title(await Thread(rosa, ch, root)));
        Assert.Equal("words", (await Thread(rosa, ch, root)).GetProperty("titleFrom").GetString());

        // the browser hears the new title with the thread
        var heard = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = rosa.Hub();
        hub.On<JsonElement>("thread-updated", p => { if (p.GetProperty("thread").GetProperty("titleFrom").GetString() == "renamed") heard.TrySetResult(p); });
        await hub.StartAsync();

        // who may rename: whoever started it, wrote in it or owns an agent in it (and admins); not someone who only sees the channel; nobody outside it
        var rename = (Person p, string t) => p.Http.PutAsJsonAsync($"/api/threads/{root}/title", new { title = t });
        Assert.Equal(HttpStatusCode.Forbidden, (await rename(sam, "Sam's name")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rename(jo, "Jo's name")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rename(rosa, new string('x', 121))).StatusCode);
        Assert.Equal("Refunds a cent out", Title(await Json(await rename(rosa, "  Refunds   a cent out "))));
        var live = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Refunds a cent out", live.GetProperty("thread").GetProperty("title").GetString());
        await sam.Post(ch, "I see it on orders over 100 too.", root);
        Assert.Equal(HttpStatusCode.OK, (await rename(sam, "Refunds a cent out on big orders")).StatusCode);       // he wrote in it now
        var reviewerOwner = await Json(await rename(alex, "Refund rounding"));                                          // he owns reviewer
        Assert.Equal(("Refund rounding", "renamed"), (Title(reviewerOwner), reviewerOwner.GetProperty("titleFrom").GetString()));
        Assert.Equal(new[] { "ThreadRenamed", "ThreadRenamed", "ThreadRenamed" }, app.Sql().Query<string>("SELECT type FROM events WHERE type='ThreadRenamed' AND stream_id=@s", new { s = $"thread-{root}" }));

        // resolved: the outcome's title; renamed after that: the new name; an empty name goes back to the outcome's
        await Resolve(app, reviewer, root, "Tax rounds per line, not per order");
        Assert.Equal(("Tax rounds per line, not per order", "outcome"), (Title(await Thread(rosa, ch, root)), (await Thread(rosa, ch, root)).GetProperty("titleFrom").GetString()));
        await rename(rosa, "Per-line rounding");
        Assert.Equal("Per-line rounding", Title(await Thread(rosa, ch, root)));
        await rename(rosa, "");
        Assert.Equal("Tax rounds per line, not per order", Title(await Thread(rosa, ch, root)));
        // reopened: not resolved any more, so the first words again
        (await rosa.Http.PostAsJsonAsync($"/api/threads/{root}/reopen", new { })).EnsureSuccessStatusCode();
        Assert.Equal("Why is the refund total a cent out…", Title(await Thread(rosa, ch, root)));
        // the thread's own answer carries it too, and whether the caller may rename
        var t = await Json(await sam.Http.GetAsync($"/api/threads/{root}"));
        Assert.True(t.GetProperty("canRename").GetBoolean());
        Assert.Equal(root, t.GetProperty("thread").GetProperty("rootId").GetInt64());
    }

    [Fact]
    public async Task The_list_shows_five_threads_per_channel_most_recent_activity_first_and_done_ones_for_a_day_unless_unread()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _r = reviewer;
        var roots = new List<long>();
        for (var i = 1; i <= 7; i++) { roots.Add(Id(await rosa.Post(ch, $"@reviewer question {i} about refunds"))); await reviewer.NextDelivery(); }
        // newest first, five of seven, with the channel's total
        Assert.Equal(roots.AsEnumerable().Reverse().Take(5), (await Listed(rosa, ch)).Select(t => t.GetProperty("rootId").GetInt64()));
        Assert.Equal(7, await Total(rosa, ch));
        // a reply moves a thread to the top; so does an agent's answer
        await sam.Post(ch, "Same question here.", roots[0]);          // reviewer is still on Rosa's question there, so this waits for it
        Assert.Equal(roots[0], (await Listed(rosa, ch))[0].GetProperty("rootId").GetInt64());
        await reviewer.Say(roots[1], "Refunds round per line.");
        await Until(async () => (await Listed(rosa, ch))[0].GetProperty("rootId").GetInt64() == roots[1], "the answered thread to move up");
        var first = (await Listed(rosa, ch))[0];
        Assert.Equal("reviewer", first.GetProperty("agents")[0].GetProperty("handle").GetString());
        Assert.Equal(rosa.Id, first.GetProperty("startedBy").GetInt64());
        Assert.Equal("working", first.GetProperty("status").GetProperty("state").GetString());

        // done: still listed, for a day
        await Resolve(app, reviewer, roots[1], "Refunds round per line");
        await reviewer.End(roots[1]);
        await Until(async () => (await Listed(rosa, ch))[0].GetProperty("status").GetProperty("state").GetString() == "done", "the thread to be done");
        Assert.Equal("Refunds round per line", Title((await Listed(rosa, ch))[0]));
        // Rosa reads it: done and read, it stays for a day
        (await rosa.Http.PostAsync($"/api/threads/{roots[1]}/read", null)).EnsureSuccessStatusCode();
        Assert.Equal((roots[1], 0), (await Listed(rosa, ch)).Select(t => (t.GetProperty("rootId").GetInt64(), Unread(t))).First());
        using (var c = app.Sql()) c.Execute("UPDATE threads SET resolved_at = resolved_at - @day WHERE root_id=@t", new { day = ThreadList.DoneShownMs + 1000, t = roots[1] });
        // a day later it has left Rosa's list; Alex hasn't read reviewer's answer, so it stays in his (and All threads still has it, under Done)
        Assert.DoesNotContain(roots[1], (await Listed(rosa, ch)).Select(t => t.GetProperty("rootId").GetInt64()));
        Assert.Equal(1, Unread((await Listed(alex, ch)).First(t => t.GetProperty("rootId").GetInt64() == roots[1])));
        var done = await Json(await rosa.Http.GetAsync($"/api/channels/{ch}/threads?filter=done"));
        Assert.Equal(new[] { roots[1] }, done.EnumerateArray().Select(t => t.GetProperty("rootId").GetInt64()));
        Assert.Equal(6, (await Json(await rosa.Http.GetAsync($"/api/channels/{ch}/threads?filter=open"))).GetArrayLength());
        Assert.Equal(7, (await Json(await rosa.Http.GetAsync($"/api/channels/{ch}/threads"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await rosa.Http.GetAsync($"/api/channels/{ch}/threads?filter=mine")).StatusCode);
    }

    [Fact]
    public async Task Unread_replies_count_per_person_until_they_open_the_thread_or_write_in_it_and_waiting_for_me_filters()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _r = reviewer;
        var root = Id(await rosa.Post(ch, "@reviewer can I push the refund fix?"));
        await reviewer.NextDelivery();
        await reviewer.Say(root, "Looking.");
        await Until(async () => Unread(await Thread(rosa, ch, root)) == 1, "Rosa's unread reply");
        Assert.Equal(1, Unread(await Thread(sam, ch, root)));
        (await rosa.Http.PostAsync($"/api/threads/{root}/read", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, Unread(await Thread(rosa, ch, root)));
        Assert.Equal(1, Unread(await Thread(sam, ch, root)));          // each person's own
        // reading again writes nothing new
        (await rosa.Http.PostAsync($"/api/threads/{root}/read", null)).EnsureSuccessStatusCode();
        Assert.Equal(1, app.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='ThreadRead' AND stream_id=@s", new { s = $"person-{rosa.Id}" }));
        // writing in it reads it; your own replies are never unread
        await sam.Post(ch, "Please check the totals too.", root);
        Assert.Equal(0, Unread(await Thread(sam, ch, root)));
        Assert.Equal(1, Unread(await Thread(rosa, ch, root)));

        // an ask for Alex (he owns reviewer): the thread waits for him, and only his "waiting for me" has it
        await reviewer.End(root);
        await reviewer.NextDelivery();          // Sam's reply
        await reviewer.Conn!.InvokeAsync("RequestPermission", new PermissionRequestMsg(root, "req-1", "push to cowork/refunds", new[] { new PermissionOption("allow", "Allow once", "allow_once"), new PermissionOption("deny", "Deny", "reject_once") }));
        await Until(async () => (await Thread(alex, ch, root)).GetProperty("status").GetProperty("state").GetString() == "waiting", "the ask");
        Assert.Equal(new[] { root }, (await Json(await alex.Http.GetAsync($"/api/channels/{ch}/threads?filter=waiting"))).EnumerateArray().Select(t => t.GetProperty("rootId").GetInt64()));
        Assert.Empty((await Json(await rosa.Http.GetAsync($"/api/channels/{ch}/threads?filter=waiting"))).EnumerateArray());
    }

    [Fact]
    public async Task A_private_channels_threads_are_only_for_its_members()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _r = reviewer;
        var leads = await alex.Channel("leads", priv: true);
        (await alex.Http.PutAsync($"/api/channels/{leads}/members/{reviewer.AgentId}", null)).EnsureSuccessStatusCode();
        var root = Id(await alex.Post(leads, "@reviewer what is the Q4 refund budget?"));
        await reviewer.NextDelivery();
        Assert.Single(await Listed(alex, leads));
        Assert.Empty(await Listed(rosa, leads));
        Assert.Equal(HttpStatusCode.NotFound, (await rosa.Http.GetAsync($"/api/channels/{leads}/threads")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rosa.Http.PutAsJsonAsync($"/api/threads/{root}/title", new { title = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rosa.Http.PostAsync($"/api/threads/{root}/read", null)).StatusCode);
        // added to #leads, Rosa sees its threads; what was said before she joined isn't unread for her
        await reviewer.Say(root, "40k.");
        await Until(async () => Unread((await Listed(alex, leads))[0]) == 1, "Alex's unread reply");
        (await alex.Http.PutAsync($"/api/channels/{leads}/members/{rosa.Id}", null)).EnsureSuccessStatusCode();
        var hers = await Listed(rosa, leads);
        Assert.Single(hers);
        Assert.Equal(0, Unread(hers[0]));
    }

    [Fact]
    public async Task A_mention_that_cant_reach_its_agent_says_so_in_a_new_thread()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _r = reviewer;
        await using var researcher = await FakeRunner.Enrol(app, alex, "researcher");          // Alex's, not in Checkout app
        await researcher.Connect();
        // not in the channel's project
        var post = await rosa.Post(ch, "@researcher can you look at the refunds?");
        Assert.True(post.GetProperty("hasThread").GetBoolean());
        var root = Id(post);
        var thread = await Json(await rosa.Http.GetAsync($"/api/threads/{root}"));
        var notice = thread.GetProperty("replies")[0];
        Assert.Equal(("notice", "researcher isn't in Checkout app. A lead can add it in the project's settings."), (notice.GetProperty("kind").GetString(), notice.GetProperty("body").GetString()));
        Assert.Equal(researcher.AgentId, notice.GetProperty("authorId").GetInt64());
        using (var c = app.Sql())
        {
            Assert.Equal(new[] { "ThreadStarted", "MentionUnreachable" }, c.Query<string>("SELECT type FROM events WHERE stream_id=@s ORDER BY position", new { s = $"thread-{root}" }));
            Assert.Equal("project", c.ExecuteScalar<string>("SELECT json_extract(data,'$.reason') FROM events WHERE type='MentionUnreachable'"));
            Assert.Equal(rosa.Id, c.ExecuteScalar<long>("SELECT on_behalf_of FROM events WHERE type='MentionUnreachable'"));
        }
        Assert.True(researcher.Delivered.Reader.Count == 0);
        // outside a project: not in the channel
        var general = app.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='general'");
        var inGeneral = Id(await rosa.Post(general, "@reviewer and @researcher, a question"));
        var replies = (await Json(await rosa.Http.GetAsync($"/api/threads/{inGeneral}"))).GetProperty("replies").EnumerateArray().Select(r => r.GetProperty("body").GetString()).ToList();
        Assert.Equal(new[] { "researcher isn't in #general. Whoever manages #general can add it in the channel's settings.", "reviewer isn't in #general. Whoever manages #general can add it in the channel's settings." },
            replies.OrderBy(x => x));
        // in the channel, but with no runner yet
        var scribe = (await Json(await alex.Http.PostAsJsonAsync("/api/agents", new { handle = "scribe", instructions = "Take notes.", harnessCommand = "opencode acp", workspace = "/tmp" }))).GetProperty("agentId").GetInt64();
        (await alex.Http.PutAsync($"/api/channels/{general}/members/{scribe}", null)).EnsureSuccessStatusCode();
        var noRunner = Id(await rosa.Post(general, "@scribe take notes please"));
        Assert.Equal("scribe has no runner yet, so it can't take work. Alex can connect one.",
            (await Json(await rosa.Http.GetAsync($"/api/threads/{noRunner}"))).GetProperty("replies")[0].GetProperty("body").GetString());
        // a mention in a thread says so in that thread; an agent that can take it still does
        await rosa.Post(ch, "@researcher please, and @reviewer too", root);
        await reviewer.NextDelivery();
        var bodies = (await Json(await rosa.Http.GetAsync($"/api/threads/{root}"))).GetProperty("replies").EnumerateArray().Where(r => r.GetProperty("kind").GetString() == "notice").Count();
        Assert.Equal(2, bodies);
        // the audit says what happened
        await Until(async () => (await Json(await alex.Http.GetAsync("/api/audit"))).GetProperty("entries").EnumerateArray()
            .Any(i => i.GetProperty("sentence").GetString() == "Rosa mentioned researcher (Alex's agent) in #checkout-app. It couldn't take it: it isn't in Checkout app."), "the audit line");
        // a name nobody has, or a person's, is just text
        var plain = await rosa.Post(ch, "thanks @sam and @nobody");
        Assert.False(plain.GetProperty("hasThread").GetBoolean());
    }

    [Fact]
    public async Task A_sandbox_for_a_project_with_no_repository_says_so_in_its_starting_rows()
    {
        await using var w = await SandboxTests.Setup();
        var p = await Json(await w.Alex.Http.PostAsJsonAsync("/api/projects", new { name = "Research", about = "Questions", memberIds = new[] { w.Rosa.Id }, channels = new[] { new { name = "research", kind = "text" } } }));
        var project = p.GetProperty("id").GetInt64();
        (await w.Alex.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{w.ResearcherId}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
        var ch = w.App.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='research'");
        var root = Id(await w.Rosa.Post(ch, "@researcher what is a good refund policy?"));
        var create = await w.Pool.Next<CreateSandbox>();
        string Step() => w.App.Sql().ExecuteScalar<string>("SELECT json_extract(meta,'$.steps[2].text') FROM posts WHERE thread_id=@root AND kind='sandbox'", new { root })!;
        Assert.Equal("No repository: add one in the project's settings to give researcher something to work on.", Step());
        await w.Pool.Report(create.Spec.SandboxId, SandboxStates.Running);
        await Until(async () => { await Task.Yield(); return Step().StartsWith("Made an empty workspace."); }, "the started card");
        Assert.Equal("Made an empty workspace. No repository: add one in the project's settings to give researcher something to work on.", Step());
    }
}
