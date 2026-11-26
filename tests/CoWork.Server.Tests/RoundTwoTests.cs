using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CoWork.Contracts;
using CoWork.Server;
using Dapper;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;

namespace CoWork.Server.Tests;

/// <summary>Everything else as events (round 2 of docs/build/brief-4.md): the server, channel, agent and person streams, rebuilding, redaction, upcasting.</summary>
public class RoundTwoTests
{
    static IEventStore Store(TestApp app) => (IEventStore)app.Services.GetService(typeof(IEventStore))!;
    static async Task<JsonElement> Get(Person p, string url) => await p.Http.GetFromJsonAsync<JsonElement>(url);

    /// <summary>Every table the projections keep, as text, rows in key order.</summary>
    public static string Dump(string dbPath)
    {
        using var c = new SqliteConnection($"Data Source={dbPath}"); c.Open();
        var sb = new StringBuilder();
        foreach (var t in Projections.Tables.OrderBy(t => t, StringComparer.Ordinal))
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT * FROM {t} ORDER BY 1, 2";
            using var r = cmd.ExecuteReader();
            while (r.Read()) sb.Append(t).Append(": ").AppendLine(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "∅" : r.GetValue(i).ToString())));
        }
        return sb.ToString();
    }

    /// <summary>What the app answers: bootstrap, every visible channel's feed and every thread, for each person.</summary>
    public static async Task<string> Answers(TestApp app, params Person[] people)
    {
        var sb = new StringBuilder();
        foreach (var p in people)
        {
            var boot = await Get(p, "/api/bootstrap");
            sb.AppendLine(p.Handle + " bootstrap " + boot.GetRawText());
            foreach (var ch in boot.GetProperty("channels").EnumerateArray().Where(c => c.GetProperty("kind").GetString() == "text"))
            {
                var feed = await Get(p, $"/api/channels/{ch.GetProperty("id").GetInt64()}/posts");
                sb.AppendLine(p.Handle + " feed " + feed.GetRawText());
                foreach (var post in feed.EnumerateArray().Where(x => x.GetProperty("hasThread").GetBoolean()))
                    sb.AppendLine(p.Handle + " thread " + (await Get(p, $"/api/threads/{post.GetProperty("id").GetInt64()}")).GetRawText());
            }
        }
        return sb.ToString();
    }

    /// <summary>A server that has had a bit of everything happen to it.</summary>
    static async Task<(TestApp app, Person alex, Person rosa, FakeRunner reviewer, FakeRunner sleeper)> Busy()
    {
        var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        var spare = await app.Invite(alex); (await alex.Http.DeleteAsync($"/api/invites/{spare}")).EnsureSuccessStatusCode();
        (await alex.Http.PutAsJsonAsync($"/api/people/{rosa.Id}/role", new { role = "admin" })).EnsureSuccessStatusCode();
        (await alex.Http.PutAsJsonAsync($"/api/people/{rosa.Id}/role", new { role = "member" })).EnsureSuccessStatusCode();

        var checkout = await alex.Channel("checkout-app");
        var leads = await alex.Channel("leads", priv: true);
        await alex.VoiceChannel("standup");
        (await alex.Http.PutAsync($"/api/channels/{leads}/members/{rosa.Id}", null)).EnsureSuccessStatusCode();
        (await alex.Http.DeleteAsync($"/api/channels/{leads}/members/{rosa.Id}")).EnsureSuccessStatusCode();
        (await alex.Http.PatchAsJsonAsync($"/api/channels/{checkout}", new { name = "checkout", topic = "The checkout app" })).EnsureSuccessStatusCode();
        (await alex.Http.PatchAsJsonAsync($"/api/channels/{leads}", new { isPrivate = false })).EnsureSuccessStatusCode();
        var scratch = await alex.Channel("scratch"); await alex.Post(scratch, "this goes away");
        (await alex.Http.DeleteAsync($"/api/channels/{scratch}")).EnsureSuccessStatusCode();

        var reviewer = await FakeRunner.Enrol(app, alex, "reviewer");
        (await alex.Http.PutAsync($"/api/channels/{checkout}/members/{reviewer.AgentId}", null)).EnsureSuccessStatusCode();
        (await alex.Http.PutAsJsonAsync($"/api/agents/{reviewer.AgentId}", new { displayName = "Reviewer", about = "Reads code", instructions = "Be brief.", harnessCommand = "opencode acp", workspace = "/work/lumen", recordToolOutput = true })).EnsureSuccessStatusCode();
        await reviewer.Connect();
        var sleeper = await FakeRunner.Enrol(app, alex, "sleeper");                 // enrolled, never connects: what is said to it waits
        (await alex.Http.PutAsync($"/api/channels/{checkout}/members/{sleeper.AgentId}", null)).EnsureSuccessStatusCode();
        (await alex.Http.PostAsync($"/api/agents/{sleeper.AgentId}/code", null)).EnsureSuccessStatusCode();

        var root = (await alex.Post(checkout, "@reviewer what does this repository do?")).GetProperty("id").GetInt64();
        await reviewer.NextDelivery();
        await reviewer.Say(root, "Let me look.");
        await reviewer.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolStart, null, "t1", "read", "read", "running"));
        await reviewer.Conn!.InvokeAsync("Stream", new StreamEvent(root, StreamKinds.ToolEnd, null, "t1", "read README.md", null, "completed", 4, "abcd", "text"));
        await reviewer.Say(root, "It's an app."); await reviewer.End(root);
        var second = (await rosa.Post(checkout, "@reviewer and the tests?", root)).GetProperty("id").GetInt64();
        await reviewer.NextDelivery();
        await reviewer.Conn!.InvokeAsync("RequestPermission", new PermissionRequestMsg(root, "r1", "run git log -3", new[] { new PermissionOption("allow", "Allow once", "allow_once") }));
        var ask = app.Sql().ExecuteScalar<long>("SELECT id FROM posts WHERE kind='ask'");
        (await alex.Http.PostAsJsonAsync($"/api/posts/{ask}/answer", new { optionId = "allow" })).EnsureSuccessStatusCode();
        await reviewer.Say(root, "Three commits."); await reviewer.End(root);
        await alex.Post(checkout, "@sleeper are you there?");                       // queued: no runner yet
        var oops = (await alex.Post(checkout, "an api key sk-123 by mistake")).GetProperty("id").GetInt64();
        (await alex.Http.PostAsync($"/api/posts/{oops}/redact", null)).EnsureSuccessStatusCode();
        var typo = (await rosa.Post(checkout, "teh checkout")).GetProperty("id").GetInt64();
        (await rosa.Http.DeleteAsync($"/api/posts/{typo}")).EnsureSuccessStatusCode();

        var bob = await app.Join(alex, "bob", "Bob");
        await using (var bobot = await FakeRunner.Enrol(app, bob, "bobot")) { }
        (await alex.Http.DeleteAsync($"/api/people/{bob.Id}")).EnsureSuccessStatusCode();
        var gone = await FakeRunner.Enrol(app, alex, "gone");
        (await alex.Http.DeleteAsync($"/api/agents/{gone.AgentId}")).EnsureSuccessStatusCode();
        // brief 5: a project with people, a workspace, a channel of its own, an agent with its grant, and a channel moved in and out
        var proj = (await (await alex.Http.PostAsJsonAsync("/api/projects", new { name = "Checkout app", about = "Cart and payment", memberIds = new[] { rosa.Id },
            workspace = new { kind = "repository", url = "/srv/git/checkout-app.git", branch = "main" }, channels = new[] { new { name = "checkout-proj", kind = "text" }, new { name = "checkout-standup", kind = "voice" } } }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        (await alex.Http.PostAsJsonAsync($"/api/projects/{proj}/workspaces", new { name = "specs", kind = "folder", path = "/srv/specs", host = "build-02" })).EnsureSuccessStatusCode();
        (await alex.Http.PatchAsJsonAsync($"/api/projects/{proj}", new { about = "Cart, payment and order confirmation" })).EnsureSuccessStatusCode();
        (await alex.Http.PutAsJsonAsync($"/api/projects/{proj}/agents/{reviewer.AgentId}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
        (await alex.Http.PutAsJsonAsync($"/api/projects/{proj}/agents/{reviewer.AgentId}", new { takesWorkFrom = "owner" })).EnsureSuccessStatusCode();
        (await alex.Http.PutAsJsonAsync($"/api/projects/{proj}/members/{rosa.Id}", new { role = "lead" })).EnsureSuccessStatusCode();
        (await alex.Http.PutAsJsonAsync($"/api/channels/{leads}/project", new { projectId = proj })).EnsureSuccessStatusCode();
        (await alex.Http.PutAsJsonAsync($"/api/channels/{leads}/project", new { projectId = (long?)null })).EnsureSuccessStatusCode();
        var projCh = app.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='checkout-proj'");
        await rosa.Post(projCh, "@reviewer can you look at the cart?");          // owner only: a short reply, no turn
        // brief 8: a thread renamed by someone who wrote in it, a mention that can't reach its agent (a short reply in a new thread), a thread read
        (await rosa.Http.PutAsJsonAsync($"/api/threads/{root}/title", new { title = "What the repository does" })).EnsureSuccessStatusCode();
        await rosa.Post(leads, "@reviewer are you in here?");
        (await rosa.Http.PostAsync($"/api/threads/{root}/read", null)).EnsureSuccessStatusCode();

        // brief 7: a pool with one slot, a template in the project, a sandbox that answers through the gateway and is suspended, a second
        // thread that waited for the slot and failed to start
        await using (var up = await FakeUpstream.Start())
        await using (var pool = await FakePool.Enrol(app, alex, slots: 1))
        {
            (await alex.Http.PutAsJsonAsync("/api/settings/models", new { kind = "openai-compatible", baseUrl = up.BaseUrl, apiKey = "sk-busy-secret-77" })).EnsureSuccessStatusCode();
            (await alex.Http.PutAsJsonAsync("/api/settings/sandboxes", new { idleSeconds = 3600 })).EnsureSuccessStatusCode();
            var researcher = (await (await alex.Http.PostAsJsonAsync("/api/agents", new { handle = "researcher", harnessCommand = "opencode acp", placement = "pool", model = "claude-haiku-4.5" }))
                .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentId").GetInt64();
            (await alex.Http.PutAsJsonAsync($"/api/projects/{proj}/agents/{researcher}", new { takesWorkFrom = "members" })).EnsureSuccessStatusCode();
            await rosa.Post(projCh, "@researcher compare three tax APIs");
            await alex.Post(projCh, "@researcher and how they refund");              // one slot: this one waits
            var create = await pool.Next<CreateSandbox>();
            await pool.Report(create.Spec.SandboxId, SandboxStates.Running);
            await using (var box = await FakeSandbox.Connect(app, create.Spec))
            {
                await box.NextDelivery();
                var gw = app.CreateClient(); gw.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", create.Spec.ThreadToken);
                (await gw.PostAsJsonAsync("/gateway/v1/chat/completions", new { model = "claude-haiku-4.5", stream = true, messages = new[] { new { role = "user", content = "hi" } } })).EnsureSuccessStatusCode();
                for (var i = 0; i < 300 && app.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='ModelUsageRecorded'") == 0; i++) await Task.Delay(25);
                await box.Say("Tallyrate fits best."); await box.End();
                for (var i = 0; i < 300 && app.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='TurnEnded' AND actor_id=@researcher", new { researcher }) == 0; i++) await Task.Delay(25);
            }
            await app.Services.GetRequiredService<Sandboxes>().Suspend(Sandboxes.Parse(create.Spec.SandboxId)!.Value, "idle", 3600);
            await pool.Report((await pool.Next<SuspendSandbox>()).SandboxId, SandboxStates.Suspended);
            var waited = await pool.Next<CreateSandbox>();                           // the one that waited gets the slot
            await pool.Report(waited.Spec.SandboxId, SandboxStates.Failed, "no such image");
            for (var i = 0; i < 300 && app.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='SandboxFailed'") == 0; i++) await Task.Delay(25);
        }
        for (var i = 0; i < 300 && app.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='PoolDisconnected'") == 0; i++) await Task.Delay(25);

        await reviewer.Conn!.StopAsync();
        for (var i = 0; i < 300 && app.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='RunnerDisconnected'") == 0; i++) await Task.Delay(50);       // the server noticed: its event is the signal
        return (app, alex, rosa, reviewer, sleeper);
    }

    [Fact]
    public async Task Rebuilding_every_projection_from_the_events_gives_the_same_tables_and_the_same_answers()
    {
        var (app, alex, rosa, reviewer, sleeper) = await Busy();
        using var _a = app; await using var _b = reviewer; await using var _c = sleeper;
        var head = Store(app).Head();
        var tablesBefore = Dump(app.DbPath); var answersBefore = await Answers(app, alex, rosa);
        Assert.Contains("channels: ", tablesBefore); Assert.Contains("posts: ", tablesBefore); Assert.Contains("[redacted]", tablesBefore);
        Assert.Contains("projects: ", tablesBefore); Assert.Contains("project_agents: ", tablesBefore); Assert.Contains("workspaces: ", tablesBefore); Assert.Contains("only takes work from Alex", tablesBefore);
        Assert.Contains("What the repository does", tablesBefore); Assert.Contains("reviewer isn't in #leads.", tablesBefore);
        Assert.Contains("pools: ", tablesBefore); Assert.Contains("sandboxes: ", tablesBefore); Assert.Contains("model_usage: ", tablesBefore); Assert.Contains("| sandbox |", tablesBefore);

        // 1. in place: empty every projection table, replay
        Store(app).RebuildProjections();
        Assert.Equal(head, Store(app).Head());                                         // a rebuild writes no events
        Assert.Equal(tablesBefore, Dump(app.DbPath));
        Assert.Equal(answersBefore, await Answers(app, alex, rosa));

        // 2. from an empty database: only the events (a new file, migrated, nothing else) replay into the same tables
        var path = Path.Combine(Path.GetTempPath(), $"cowork-replay-{Guid.NewGuid():N}.db");
        try
        {
            var fresh = new Db(path); fresh.Migrate();
            using (var c = fresh.Open())
            {
                c.Execute("ATTACH DATABASE @p AS old", new { p = app.DbPath });
                c.Execute("INSERT INTO events SELECT * FROM old.events");
                c.Execute("DETACH DATABASE old");
            }
            new SqliteEventStore(fresh, new IInlineProjection[] { new DirectoryProjection(), new ProjectProjection(), new ThreadProjection(), new SandboxProjection(), new ArtifactProjection() }).RebuildProjections();
            Assert.Equal(tablesBefore, Dump(path));
        }
        finally { foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch { } }
    }

    [Fact]
    public async Task The_streams_hold_the_facts_and_none_of_the_secrets()
    {
        var (app, alex, rosa, reviewer, sleeper) = await Busy();
        using var _a = app; await using var _b = reviewer; await using var _c = sleeper;
        using var c = app.Sql();
        string[] Types(string like) => c.Query<string>("SELECT DISTINCT type FROM events WHERE stream_id LIKE @like ORDER BY type", new { like }).ToArray();
        Assert.Equal(new[] { "InviteCreated", "InviteRevoked", "InviteUsed", "ModelProviderSet", "PersonJoined", "PersonRemoved", "RoleChanged", "SandboxIdleSet", "ServerIdAssigned", "ServerSetUp" }, Types("server"));
        Assert.Equal(new[] { "PoolConnected", "PoolCreated", "PoolDisconnected", "PoolEnrolled" }, Types("pool-%"));
        foreach (var t in new[] { "SandboxRequested", "SandboxPlaced", "SandboxStarted", "SandboxConnected", "SandboxSuspending", "SandboxSuspended", "SandboxFailed", "ModelUsageRecorded" })
            Assert.Contains(t, Types("thread-%"));
        Assert.Contains("ChannelRenamed", Types("channel-%")); Assert.Contains("TopicChanged", Types("channel-%")); Assert.Contains("ChannelMadePublic", Types("channel-%"));
        Assert.Contains("MemberAdded", Types("channel-%")); Assert.Contains("MemberRemoved", Types("channel-%")); Assert.Contains("ChannelDeleted", Types("channel-%"));
        Assert.Contains("MessageRedacted", Types("channel-%")); Assert.Contains("MessageDeleted", Types("channel-%"));
        Assert.Equal(new[] { "AgentCreated", "AgentEdited", "AgentRemoved", "EnrolmentCodeIssued", "RunnerConnected", "RunnerDisconnected", "RunnerEnrolled" }, Types("agent-%"));
        Assert.Equal(new[] { "AgentAddedToProject", "AgentGrantsChanged", "ProjectCreated", "ProjectMemberAdded", "ProjectUpdated", "WorkspaceAdded" }, Types("project-%"));
        Assert.Contains("ChannelMovedToProject", Types("channel-%")); Assert.Contains("WorkRefused", Types("thread-%"));
        // the log is safe to export: no token, enrolment code or invite code is in it, nor who anyone is at the sign-in provider
        var secrets = c.Query<string>(@"SELECT subject FROM logins UNION ALL SELECT email FROM logins UNION ALL SELECT token_hash FROM runner_secrets WHERE token_hash IS NOT NULL UNION ALL SELECT code_hash FROM runner_secrets WHERE code_hash IS NOT NULL
            UNION ALL SELECT code FROM invite_codes UNION ALL SELECT token_hash FROM thread_tokens UNION ALL SELECT token_hash FROM pool_secrets WHERE token_hash IS NOT NULL UNION ALL SELECT value FROM server_secrets").ToList();
        Assert.True(secrets.Count >= 9);
        Assert.Contains("sk-busy-secret-77", secrets);
        var log = string.Concat(c.Query<string>("SELECT data FROM events"));
        foreach (var s in secrets) Assert.DoesNotContain(s, log);
        Assert.DoesNotContain(reviewer.Token, log);
        // who did what: the person is the actor, and the first events are the setup
        var first = c.Query<(string Type, long? ActorId)>("SELECT type, actor_id FROM events ORDER BY position LIMIT 3").ToList();
        Assert.Equal(new[] { "ServerSetUp", "PersonJoined", "ChannelCreated" }, first.Select(f => f.Type));
        Assert.All(first, f => Assert.Equal(alex.Id, f.ActorId));
    }

    [Fact]
    public async Task A_runner_catches_up_from_its_position_and_a_requeued_message_pulls_it_back()
    {
        var (app, alex, rosa, reviewer, sleeper) = await Busy();
        using var _a = app; await using var _b = reviewer; await using var _c = sleeper;
        using var c = app.Sql();
        var sleeperId = sleeper.AgentId;
        // the message to the offline agent is queued: a MessageDelivered no turn has started
        var queued = c.ExecuteScalar<long>("SELECT position FROM events WHERE type='MessageDelivered' AND json_extract(data,'$.agentId')=@sleeperId", new { sleeperId });
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='TurnStarted' AND json_extract(data,'$.agentId')=@sleeperId", new { sleeperId }));
        var rows = c.Query<Deliveries.Row>(Deliveries.Queued, new { agentId = sleeperId }).ToList();
        Assert.Equal(new[] { queued }, rows.Select(r => r.Position));
        Assert.Contains("are you there?", rows[0].Text);
        // the reviewer has nothing waiting, and its position is at the last thing that happened to it
        Assert.Empty(c.Query<Deliveries.Row>(Deliveries.Queued, new { agentId = reviewer.AgentId }));
        Assert.True(c.ExecuteScalar<long>("SELECT position FROM runner_positions WHERE agent_id=@id", new { id = reviewer.AgentId })
            >= c.ExecuteScalar<long>("SELECT MAX(position) FROM events WHERE type='TurnStarted' AND actor_id=@id", new { id = reviewer.AgentId }));

        // the runner connects: it is sent exactly that message, and its position moves past it
        await sleeper.Connect();
        var d = await sleeper.NextDelivery();
        Assert.Contains("are you there?", d.Text);
        Assert.Equal(queued, d.DeliveryId);                                            // a delivery's id is the position of its MessageDelivered event
        Assert.Empty(c.Query<Deliveries.Row>(Deliveries.Queued, new { agentId = sleeperId }));
        Assert.True(c.ExecuteScalar<long>("SELECT position FROM runner_positions WHERE agent_id=@sleeperId", new { sleeperId }) >= queued);

        // the same message put back (the runner couldn't be reached): queued again, the position is back before it
        var started = c.QuerySingle<(long Position, long ActorId, string Stream, string Turn)>("SELECT position, actor_id, stream_id, correlation FROM events WHERE type='TurnStarted' AND actor_id=@sleeperId", new { sleeperId });
        Store(app).Append(new[] { new NewEvent(started.Stream, "TurnInterrupted", new { agentId = sleeperId, requeued = true, reason = "the runner couldn't be reached" }, sleeperId, null, started.Turn, started.Position) });
        Assert.Equal(new[] { queued }, c.Query<Deliveries.Row>(Deliveries.Queued, new { agentId = sleeperId }).Select(r => r.Position));
        Assert.True(c.ExecuteScalar<long>("SELECT position FROM runner_positions WHERE agent_id=@sleeperId", new { sleeperId }) < queued);
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM open_turns WHERE agent_id=@sleeperId", new { sleeperId }));
    }

    [Fact]
    public async Task Deleting_hides_a_message_and_redacting_replaces_its_words_in_the_events_too_and_only_the_right_people_may()
    {
        var (app, alex, rosa, reviewer, sleeper) = await Busy();
        using var _a = app; await using var _b = reviewer; await using var _c = sleeper;
        var ch = app.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='checkout'");
        using var c = app.Sql();
        // the redacted one: the projection, the MessagePosted event and the events that carried it say "[redacted]"; the fact stays
        var oops = c.ExecuteScalar<long>("SELECT id FROM posts WHERE body='[redacted]'");
        var posted = c.QuerySingle<(string Data, long Position)>("SELECT data, position FROM events WHERE type='MessagePosted' AND json_extract(data,'$.postId')=@oops", new { oops });
        Assert.DoesNotContain("sk-123", posted.Data); Assert.Contains("\"redacted\":true", posted.Data);
        Assert.DoesNotContain("sk-123", string.Concat(c.Query<string>("SELECT data FROM events")));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='MessageRedacted'"));
        // the deleted one: gone from every answer, but the event of what happened stays, and so does the row, hidden
        var typo = c.ExecuteScalar<long>("SELECT json_extract(data,'$.postId') FROM events WHERE type='MessageDeleted'");
        var feed = await Get(alex, $"/api/channels/{ch}/posts");
        Assert.DoesNotContain(feed.EnumerateArray(), p => p.GetProperty("id").GetInt64() == typo);
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM posts WHERE id=@typo AND deleted=1", new { typo }));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='MessagePosted' AND json_extract(data,'$.postId')=@typo", new { typo }));
        // who may: the author, an admin; not another member, not someone who can't see the channel, not a tool row
        var mine = (await alex.Post(ch, "mine")).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.DeleteAsync($"/api/posts/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.PostAsync($"/api/posts/{mine}/redact", null)).StatusCode);
        var hers = (await rosa.Post(ch, "hers")).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await alex.Http.PostAsync($"/api/posts/{hers}/redact", null)).StatusCode);               // alex is an admin
        var tool = c.ExecuteScalar<long>("SELECT id FROM posts WHERE kind='tool'");
        Assert.Equal(HttpStatusCode.BadRequest, (await alex.Http.DeleteAsync($"/api/posts/{tool}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alex.Http.DeleteAsync("/api/posts/99999")).StatusCode);
        // a reply is in its thread's stream, and deleting it changes the thread's count
        var root = c.ExecuteScalar<long>("SELECT root_id FROM threads LIMIT 1");
        var reply = (await rosa.Post(ch, "a reply", root)).GetProperty("id").GetInt64();
        (await rosa.Http.DeleteAsync($"/api/posts/{reply}")).EnsureSuccessStatusCode();
        Assert.Equal($"thread-{root}", c.ExecuteScalar<string>("SELECT stream_id FROM events WHERE type='MessageDeleted' ORDER BY position DESC LIMIT 1"));
    }

    [Fact]
    public async Task A_deletion_reaches_browsers_live_and_on_catch_up()
    {
        var (app, alex, rosa, reviewer, sleeper) = await Busy();
        using var _a = app; await using var _b = reviewer; await using var _c = sleeper;
        var ch = app.Sql().ExecuteScalar<long>("SELECT id FROM channels WHERE name='checkout'");
        var hub = alex.Hub(); var deleted = new TaskCompletionSource<JsonElement>();
        hub.On<JsonElement>("post-deleted", p => deleted.TrySetResult(p));
        await hub.StartAsync(); await hub.InvokeAsync("Subscribe", ch);
        var seen = Store(app).Head();
        var id = (await rosa.Post(ch, "oops")).GetProperty("id").GetInt64();
        (await rosa.Http.DeleteAsync($"/api/posts/{id}")).EnsureSuccessStatusCode();
        var live = await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((id, ch), (live.GetProperty("id").GetInt64(), live.GetProperty("channelId").GetInt64()));
        Assert.True(live.GetProperty("at").GetInt64() > seen);
        var r = await hub.InvokeAsync<JsonElement>("CatchUp", seen);
        Assert.Contains(r.GetProperty("messages").EnumerateArray(), m => m.GetProperty("name").GetString() == "post-deleted" && m.GetProperty("payload").GetProperty("id").GetInt64() == id);
        Assert.DoesNotContain(r.GetProperty("messages").EnumerateArray(), m => m.GetProperty("name").GetString() == "post-upsert" && m.GetProperty("payload").GetProperty("id").GetInt64() == id);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task An_old_event_is_converted_to_the_current_shape_when_it_is_read()
    {
        using var app = new TestApp();
        await app.Setup();
        // round 1 wrote ChannelDeleted with only the id, at schema 1
        using (var c = app.Sql())
            c.Execute("INSERT INTO events(stream_id, version, type, schema, data, occurred_at) VALUES ('channel-77', 1, 'ChannelDeleted', 1, '{\"channelId\":77}', 1)");
        var old = Store(app).ReadStream("channel-77").Single();
        Assert.Equal(2, old.Schema);
        Assert.Equal(77, old.Long("channelId"));
        Assert.Equal(JsonValueKind.Null, old.Data.GetProperty("name").ValueKind);                   // the step: older events have no name
        // what is stored stays as it was written; new events are written in the current shape
        using (var c = app.Sql()) Assert.Equal(1, c.ExecuteScalar<int>("SELECT schema FROM events WHERE stream_id='channel-77'"));
        var alex = await app.Setup2();
        var ch = await alex.Channel("old"); (await alex.Http.DeleteAsync($"/api/channels/{ch}")).EnsureSuccessStatusCode();
        var fresh = Store(app).ReadStream($"channel-{ch}").Last();
        Assert.Equal(("ChannelDeleted", 2, "old"), (fresh.Type, fresh.Schema, fresh.Str("name")));
        using (var c = app.Sql()) Assert.Equal(2, c.ExecuteScalar<int>("SELECT schema FROM events WHERE stream_id=@s AND type='ChannelDeleted'", new { s = $"channel-{ch}" }));
        // and an event type nobody changed passes through untouched
        Assert.Equal((1, "x"), (Upcasting.ToCurrent("MessagePosted", 1, JsonDocument.Parse("{\"body\":\"x\"}").RootElement).Schema, Upcasting.ToCurrent("MessagePosted", 1, JsonDocument.Parse("{\"body\":\"x\"}").RootElement).Data.GetProperty("body").GetString()));
    }

    [Fact]
    public async Task Two_people_racing_for_the_same_name_or_the_last_invite_place_cannot_both_win()
    {
        using var app = new TestApp();
        var alex = await app.Setup();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => alex.Http.PostAsJsonAsync("/api/channels", new { name = "same" })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK)); Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var code = await (await alex.Http.PostAsJsonAsync("/api/invites", new { maxUses = 1 })).Content.ReadFromJsonAsync<JsonElement>();
        var people = new List<Person>();
        foreach (var i in Enumerable.Range(0, 4)) { var p = new Person(app, $"user{i}"); await app.Sso(p, FakeIssuer.For($"user{i}"), "invite", code.GetProperty("code").GetString()); people.Add(p); }
        var joins = await Task.WhenAll(people.Select((p, i) => p.Http.PostAsJsonAsync($"/api/invites/{code.GetProperty("code").GetString()}/accept", new { handle = $"user{i}", displayName = $"User {i}" })));
        Assert.Equal(1, joins.Count(r => r.StatusCode == HttpStatusCode.OK)); Assert.Equal(3, joins.Count(r => r.StatusCode == HttpStatusCode.Gone));
    }
}

static class TestAppExtras
{
    /// <summary>Signs in again as the first person (the one setup made).</summary>
    public static async Task<Person> Setup2(this TestApp app)
    {
        return await app.SignIn("alex");
    }
}
