using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoWork.Server.Tests;

/// <summary>Brief 5: projects hold channels and own their workspaces; an agent in a project has a grant and a boundary.</summary>
public class ProjectTests
{
    static async Task<JsonElement> Json(HttpResponseMessage r) { r.EnsureSuccessStatusCode(); return await r.Content.ReadFromJsonAsync<JsonElement>(); }

    /// <summary>Alex (admin), Rosa and Sam in "Checkout app" (a repository), Jo outside it; reviewer, Alex's agent, connected on "testbox".</summary>
    static async Task<(TestApp app, Person alex, Person rosa, Person sam, Person jo, long project, long ch, FakeRunner reviewer)> World(object? workspace = null)
    {
        var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa"); var sam = await app.Join(alex, "sam", "Sam"); var jo = await app.Join(alex, "jo", "Jo");
        var reviewer = await FakeRunner.Enrol(app, alex, "reviewer");
        await reviewer.Connect();
        var project = (await Json(await alex.Http.PostAsJsonAsync("/api/projects", new
        {
            name = "Checkout app", about = "Cart, payment and order confirmation", memberIds = new[] { rosa.Id, sam.Id },
            workspace = workspace ?? new { kind = "repository", url = "/srv/git/checkout-app.git", branch = "main" },
            channels = new[] { new { name = "checkout-app", kind = "text" }, new { name = "checkout-standup", kind = "voice" } },
        }))).GetProperty("id").GetInt64();
        var ch = app.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='checkout-app'");
        return (app, alex, rosa, sam, jo, project, ch, reviewer);
    }

    [Fact]
    public async Task A_project_is_its_events_and_only_its_members_see_it_and_its_channels()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _b = reviewer;
        using var c = app.Sql();
        var events = c.Query<(string Stream, string Type)>("SELECT stream_id, type FROM events WHERE stream_id=@s OR stream_id LIKE 'channel-%' ORDER BY position", new { s = $"project-{project}" }).ToList();
        Assert.Equal(new[] { "ProjectCreated", "ProjectMemberAdded", "ProjectMemberAdded", "ProjectMemberAdded", "WorkspaceAdded" }, events.Where(e => e.Stream == $"project-{project}").Select(e => e.Type));
        Assert.Equal(project, c.ExecuteScalar<long>("SELECT json_extract(data,'$.projectId') FROM events WHERE type='ChannelCreated' AND json_extract(data,'$.name')='checkout-app'"));
        Assert.Equal("lead", c.ExecuteScalar<string>("SELECT role FROM project_members WHERE project_id=@project AND member_id=@id", new { project, id = alex.Id }));

        // Rosa sees the project and its two channels, with #general outside it; Jo sees only #general
        var rosaBoot = await rosa.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Equal("Checkout app", rosaBoot.GetProperty("projects")[0].GetProperty("name").GetString());
        Assert.False(rosaBoot.GetProperty("projects")[0].GetProperty("canEdit").GetBoolean());
        var rosaChannels = rosaBoot.GetProperty("channels").EnumerateArray().Select(x => (x.GetProperty("name").GetString(), x.GetProperty("projectId").ValueKind == JsonValueKind.Null ? (long?)null : x.GetProperty("projectId").GetInt64())).ToList();
        Assert.Contains(("checkout-app", (long?)project), rosaChannels); Assert.Contains(("checkout-standup", (long?)project), rosaChannels); Assert.Contains(("general", (long?)null), rosaChannels);
        var joBoot = await jo.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Empty(joBoot.GetProperty("projects").EnumerateArray());
        Assert.Equal(new[] { "general" }, joBoot.GetProperty("channels").EnumerateArray().Select(x => x.GetProperty("name").GetString()));

        // every way in is checked: the feed, posting, the settings, the hub
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.GetAsync($"/api/channels/{ch}/posts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.PostAsJsonAsync($"/api/channels/{ch}/posts", new { body = "hello?" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.GetAsync($"/api/projects/{project}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.GetAsync($"/api/projects/{project}/agents/{reviewer.AgentId}/boundary")).StatusCode);
        await using (var hub = jo.Hub())
        {
            await hub.StartAsync();
            await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => hub.InvokeAsync("Subscribe", ch));
        }
        await using (var hub = rosa.Hub()) { await hub.StartAsync(); await hub.InvokeAsync("Subscribe", ch); }
        Assert.Equal(HttpStatusCode.OK, (await rosa.Http.GetAsync($"/api/channels/{ch}/posts")).StatusCode);

        // leads and admins edit it; a member doesn't
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.PatchAsJsonAsync($"/api/projects/{project}", new { about = "mine now" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.PutAsJsonAsync($"/api/projects/{project}/members/{jo.Id}", new { role = "member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.PostAsJsonAsync($"/api/projects/{project}/workspaces", new { kind = "folder", path = "/x", host = "h" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "members" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alex.Http.DeleteAsync($"/api/projects/{project}/members/{alex.Id}")).StatusCode);        // the last lead stays
        // Jo joins: now she sees it
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/members/{jo.Id}", new { role = "member" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await jo.Http.GetAsync($"/api/channels/{ch}/posts")).StatusCode);
        // the settings page: workspace, people with roles, rules
        var settings = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{project}");
        Assert.Equal("repository", settings.GetProperty("workspaces")[0].GetProperty("kind").GetString());
        Assert.Equal("lead", settings.GetProperty("people")[0].GetProperty("role").GetString());
        Assert.Contains(settings.GetProperty("rules").EnumerateArray(), r => r.GetString()!.Contains("A hook refuses pushes to `main`"));
    }

    [Fact]
    public async Task Two_threads_in_a_project_each_get_their_own_branch_and_the_runner_is_told_the_workspace()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _b = reviewer;
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
        // in the project, reviewer is in the project's channels without being added to each
        Assert.Contains(reviewer.AgentId, (await rosa.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap")).GetProperty("channels").EnumerateArray()
            .First(x => x.GetProperty("id").GetInt64() == ch).GetProperty("memberIds").EnumerateArray().Select(x => x.GetInt64()));
        var t1 = (await rosa.Post(ch, "@reviewer write notes about the tax step")).GetProperty("id").GetInt64();
        var t2 = (await sam.Post(ch, "@reviewer write notes about the cart")).GetProperty("id").GetInt64();
        var d1 = await reviewer.NextDelivery(); var d2 = await reviewer.NextDelivery();
        var byThread = new[] { d1, d2 }.ToDictionary(d => d.ThreadId);
        Assert.Equal(project, byThread[t1].ProjectId);
        Assert.Equal("repository", byThread[t1].ThreadWorkspace!.Kind);
        Assert.Equal("/srv/git/checkout-app.git", byThread[t1].ThreadWorkspace!.Url);
        Assert.Equal("main", byThread[t1].ThreadWorkspace!.BaseBranch);
        Assert.Equal($"cowork/write-notes-about-the-tax-{t1}", byThread[t1].ThreadWorkspace!.Branch);
        Assert.Equal($"cowork/write-notes-about-the-cart-{t2}", byThread[t2].ThreadWorkspace!.Branch);

        // the runner says where it made the worktree: the thread's session line shows the branch and that it isn't sandboxed
        await reviewer.Conn!.InvokeAsync("Stream", new StreamEvent(t1, StreamKinds.Workspace, "/data/repos/checkout-app", null, $"cowork/write-notes-about-the-tax-{t1}", null, "worktree", WorkspaceId: byThread[t1].ThreadWorkspace!.WorkspaceId));
        await reviewer.End(t1);
        var thread = await rosa.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{t1}");
        var ws = thread.GetProperty("agents")[0].GetProperty("workspace");
        Assert.Equal($"cowork/write-notes-about-the-tax-{t1}", ws.GetProperty("branch").GetString());
        Assert.Equal("worktree", ws.GetProperty("place").GetString());
        Assert.Equal("testbox", ws.GetProperty("host").GetString());
        Assert.False(ws.GetProperty("sandboxed").GetBoolean());
        var settings = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{project}");
        Assert.Equal("testbox", settings.GetProperty("workspaces")[0].GetProperty("places")[0].GetProperty("host").GetString());

        // outside the project: the agent's own folder, as before
        var general = app.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='general'");
        (await alex.Http.PutAsync($"/api/channels/{general}/members/{reviewer.AgentId}", null)).EnsureSuccessStatusCode();
        await alex.Post(general, "@reviewer hello");
        var d3 = await reviewer.NextDelivery();
        Assert.Null(d3.ProjectId); Assert.Null(d3.ThreadWorkspace); Assert.Equal("/tmp", d3.Workspace);
    }

    [Fact]
    public async Task A_folder_on_another_machine_is_out_of_reach_and_one_on_its_own_is_used()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World(new { kind = "folder", name = "specs", path = "/srv/specs", host = "build-02" });
        using var _a = app; await using var _b = reviewer;
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
        await rosa.Post(ch, "@reviewer read the specs");
        var d = await reviewer.NextDelivery();
        Assert.Equal("none", d.ThreadWorkspace!.Kind);                 // reviewer's runner is on testbox
        var boundary = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/agents/{reviewer.AgentId}/boundary");
        Assert.Contains("Can't reach `specs`: that folder is on build-02.", Line(boundary, "worksOn"));
        (await alex.Http.PostAsJsonAsync($"/api/projects/{project}/workspaces", new { kind = "folder", name = "local", path = "/srv/local", host = "testbox" })).EnsureSuccessStatusCode();
        await reviewer.End(d.ThreadId);
        await sam.Post(ch, "@reviewer and now?");
        var d2 = await reviewer.NextDelivery();
        Assert.Equal("folder", d2.ThreadWorkspace!.Kind); Assert.Equal("/srv/local", d2.ThreadWorkspace.Path);
    }

    static string Line(JsonElement boundary, string key) => boundary.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("key").GetString() == key).GetProperty("text").GetString()!;

    [Fact]
    public async Task The_boundary_says_in_sentences_what_the_agent_may_do_from_the_real_data()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _b = reviewer;
        // before it is added: the dialog's preview
        var preview = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/agents/{reviewer.AgentId}/boundary");
        Assert.False(preview.GetProperty("inProject").GetBoolean());
        Assert.Equal("Runs unsandboxed on testbox. It can read and change anything the runner's account can there. Threads show this to everyone.", preview.GetProperty("warning").GetString());
        Assert.Equal(new[] { "sees", "takesWorkFrom", "worksOn", "reaches", "asks", "spends", "remembers", "stops" }, preview.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("key").GetString()));
        Assert.Equal("Checkout app's channels (#checkout-app and 🔊 checkout-standup) and the threads it's in. Not other projects or channels.", Line(preview, "sees"));
        Assert.Equal("Members of Checkout app: Alex, Rosa and Sam.", Line(preview, "takesWorkFrom"));
        Assert.StartsWith("`app` (`/srv/git/checkout-app.git`, branch `main`), cloned on testbox, in a worktree per thread", Line(preview, "worksOn"));
        Assert.Equal("Alex, its owner, whenever the harness asks permission. Everyone else sees that it's waiting for Alex.", Line(preview, "asks"));
        Assert.Contains("A data folder of its own for Checkout app on testbox", Line(preview, "remembers"));
        // the grant it would get, previewed, and once set
        var ownerOnly = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/agents/{reviewer.AgentId}/boundary?takesWorkFrom=owner");
        Assert.StartsWith("Only Alex.", Line(ownerOnly, "takesWorkFrom"));
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
        var general = app.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='general'");
        (await alex.Http.PutAsync($"/api/channels/{general}/members/{reviewer.AgentId}", null)).EnsureSuccessStatusCode();
        var added = await rosa.Http.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/agents/{reviewer.AgentId}/boundary");
        Assert.True(added.GetProperty("inProject").GetBoolean());
        Assert.EndsWith("Outside this project, only #general, where it was added.", Line(added, "sees"));
    }

    [Fact]
    public async Task An_agent_that_takes_work_from_its_owner_only_answers_others_with_a_short_reply_and_the_audit_says_why()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _b = reviewer;
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "owner" })).EnsureSuccessStatusCode();
        var root = (await rosa.Post(ch, "@reviewer please fix the rounding")).GetProperty("id").GetInt64();
        var thread = await rosa.Http.GetFromJsonAsync<JsonElement>($"/api/threads/{root}");
        var reply = thread.GetProperty("replies")[0];
        Assert.Equal("notice", reply.GetProperty("kind").GetString());
        Assert.Equal("reviewer only takes work from Alex.", reply.GetProperty("body").GetString());
        Assert.Equal(reviewer.AgentId, reply.GetProperty("authorId").GetInt64());
        Assert.Empty(thread.GetProperty("agents").EnumerateArray());
        // Alex, its owner, is heard; Rosa's next mention in the same thread is refused again
        await alex.Post(ch, "@reviewer please fix the rounding", root);
        Assert.Equal(root, (await reviewer.NextDelivery()).ThreadId);
        await reviewer.End(root);
        await rosa.Post(ch, "thanks, and @reviewer the tests too", root);
        await rosa.Post(ch, "(no mention: not passed on)", root);
        using var c = app.Sql();
        Assert.Equal(2, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='WorkRefused'"));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='MessageDelivered'"));
        var refused = c.Query<(long? Actor, long? Behalf, string Grant)>("SELECT actor_id, on_behalf_of, json_extract(data,'$.grant') FROM events WHERE type='WorkRefused'").First();
        Assert.Equal((reviewer.AgentId, rosa.Id, "owner"), (refused.Actor!.Value, refused.Behalf!.Value, refused.Grant));
        // a members grant: Jo, not in the project, can't see the channel at all; switched back, Rosa is heard
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
        await rosa.Post(ch, "@reviewer now?");
        await reviewer.NextDelivery();

        // the audit is an async view: wait for it to reach the last event (the projection's checkpoint), then read it
        var head = c.ExecuteScalar<long>("SELECT MAX(position) FROM events");
        for (var i = 0; i < 400 && (c.ExecuteScalar<long?>("SELECT position FROM projection_checkpoints WHERE name='audit'") ?? 0) < head; i++) await Task.Delay(25);
        var audit = await alex.Http.GetFromJsonAsync<JsonElement>("/api/audit?limit=200");
        var sentences = audit.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("sentence").GetString()!).ToList();
        Assert.Contains("Rosa asked reviewer (Alex's agent) for work in #checkout-app. It didn't take it: it takes work from Alex only.", sentences);
        Assert.Contains("Alex created the project Checkout app.", sentences);
        Assert.Contains("Alex added Rosa to Checkout app as a member.", sentences);
        Assert.Contains("Alex added the repository `/srv/git/checkout-app.git` (branch main) to Checkout app as app.", sentences);
        Assert.Contains("Alex created the channel #checkout-app in Checkout app.", sentences);
        Assert.Contains("Alex added reviewer (Alex's agent) to Checkout app. It takes work from Alex only.", sentences);
        Assert.Contains("Alex set reviewer (Alex's agent) to take work from the members of Checkout app in Checkout app.", sentences);
    }

    [Fact]
    public async Task Only_a_lead_who_owns_the_agent_or_an_admin_adds_it_and_moving_a_channel_changes_who_sees_it()
    {
        var (app, alex, rosa, sam, jo, project, ch, reviewer) = await World();
        using var _a = app; await using var _b = reviewer;
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/members/{rosa.Id}", new { role = "lead" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "members" })).StatusCode);   // a lead, not its owner
        (await alex.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
        (await rosa.Http.PutAsJsonAsync($"/api/projects/{project}/agents/{reviewer.AgentId}", new { takesWorkFrom = "owner" })).EnsureSuccessStatusCode();             // a lead may narrow its grant
        // #lounge, outside any project, moves in: Jo stops seeing it; out again, she sees it
        var lounge = await alex.Channel("lounge");
        (await alex.Http.PutAsJsonAsync($"/api/channels/{lounge}/project", new { projectId = project })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.GetAsync($"/api/channels/{lounge}/posts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sam.Http.GetAsync($"/api/channels/{lounge}/posts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sam.Http.PutAsJsonAsync($"/api/channels/{lounge}/project", new { projectId = (long?)null })).StatusCode);
        (await rosa.Http.PutAsJsonAsync($"/api/channels/{lounge}/project", new { projectId = (long?)null })).EnsureSuccessStatusCode();     // a lead of the project it is in
        Assert.Equal(HttpStatusCode.OK, (await jo.Http.GetAsync($"/api/channels/{lounge}/posts")).StatusCode);
        // a channel created in a project needs a member
        Assert.Equal(HttpStatusCode.NotFound, (await jo.Http.PostAsJsonAsync("/api/channels", new { name = "jo-room", projectId = project })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sam.Http.PostAsJsonAsync("/api/channels", new { name = "sam-room", projectId = project })).StatusCode);
    }

    [Fact]
    public void A_thread_slug_is_its_first_words_and_its_id()
    {
        Assert.Equal("fix-rounding-in-refunds-12", Projects.Slug("@reviewer fix rounding in refunds", 12));
        Assert.Equal("what-does-src-tax-ts-7", Projects.Slug("@reviewer What does src/tax.ts do?", 7));
        Assert.Equal("thread-9", Projects.Slug("@reviewer ???", 9));
        Assert.True(Projects.Slug(new string('a', 30) + " " + new string('b', 30), 5).Length < 50);
    }
}
