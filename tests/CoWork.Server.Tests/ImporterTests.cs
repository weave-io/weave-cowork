using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoWork.Server;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server.Tests;

/// <summary>
/// The upgrade from tables to events. The fixture is a database made by the `main` build of round 1 (commit 291ef6b; tests/fixtures/make-legacy-db.mjs):
/// two people and one removed, three agents and a removed one, public, private and voice channels, a thread with a tool call and an approval,
/// an agent that mentions another, queued messages, and the answers the old server gave over its API.
/// </summary>
public class ImporterTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    static string Copy(Action<SqliteConnection>? tweak = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cowork-legacy-{Guid.NewGuid():N}.db");
        File.Copy(Fixture("legacy-main-round1.db"), path);
        if (tweak is not null) { using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); tweak(c); }
        return path;
    }

    static void Clean(string path) { foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch { } }

    static async Task<Person> SignIn(TestApp app, string user, string password)
    {
        var p = new Person(app, user);
        var r = await p.Http.PostAsJsonAsync("/api/auth/login", new { username = user, password });
        r.EnsureSuccessStatusCode(); p.CaptureCookie(r);
        return p;
    }

    static JsonNode Strip(JsonNode? n)
    {
        switch (n)
        {
            // positions differ by design; what brief 5 added to the answers (projects, a channel's project, a thread agent's workspace, all
            // empty for a database from before projects), brief 7 added (an agent's placement, image and model, a thread's sandbox, who paused it and its usage)
            // and brief 8 added (a thread's summary for the channel list, its title on its post, and whether the caller may rename it) wasn't in the old server's answers
            case JsonObject o: foreach (var k in o.Where(kv => kv.Key is "position" or "at" or "projects" or "projectId" or "placement" or "image" or "model" or "sandbox" or "pausedBy" or "usage" or "threadStatus" or "thread" or "threadTitle" or "canRename" || kv.Key == "workspace" && kv.Value is null).Select(kv => kv.Key).ToList()) o.Remove(k); foreach (var kv in o.ToList()) Strip(kv.Value); break;
            case JsonArray a: foreach (var x in a) Strip(x); break;
        }
        return n!;
    }

    /// <summary>What the app answers to one person, in the shape the fixture's script wrote it.</summary>
    static async Task<JsonObject> Answers(Person p)
    {
        var boot = await p.Http.GetFromJsonAsync<JsonObject>("/api/bootstrap");
        var feeds = new JsonObject(); var threads = new JsonObject();
        foreach (var ch in boot!["channels"]!.AsArray().Where(c => (string?)c!["kind"] == "text"))
        {
            var id = (long)ch!["id"]!;
            var feed = (await p.Http.GetFromJsonAsync<JsonArray>($"/api/channels/{id}/posts"))!;
            foreach (var post in feed.Where(x => (bool)x!["hasThread"]!)) threads[((long)post!["id"]!).ToString()] = await p.Http.GetFromJsonAsync<JsonObject>($"/api/threads/{(long)post["id"]!}");
            feeds[id.ToString()] = feed;
        }
        return (JsonObject)Strip(new JsonObject { ["bootstrap"] = boot, ["feeds"] = feeds, ["threads"] = threads });
    }

    static JsonNode Expected(string person) => Strip(JsonNode.Parse(File.ReadAllText(Fixture("legacy-main-round1.expected.json")))![person])!;

    static async Task AssertSameAnswersAsBefore(TestApp app)
    {
        var alex = await SignIn(app, "alex", "correct horse"); var rosa = await SignIn(app, "rosa", "rosa-password");
        Assert.True(JsonNode.DeepEquals(Expected("alex"), await Answers(alex)), "Alex sees what he saw before the upgrade");
        Assert.True(JsonNode.DeepEquals(Expected("rosa"), await Answers(rosa)), "Rosa sees what she saw before the upgrade");
    }

    [Fact]
    public async Task A_database_from_the_round_1_build_is_upgraded_and_the_app_answers_exactly_as_before()
    {
        var path = Copy();
        try
        {
            using (var app = new TestApp(existingDb: path))
            {
                _ = app.Server;
                await AssertSameAnswersAsBefore(app);
                using var c = app.Sql();
                // the tables are now written down as events, in order, each marked as imported, and the older events are kept aside
                var types = c.Query<string>("SELECT type FROM events ORDER BY position").ToList();
                Assert.Equal("ServerSetUp", types[0]);
                Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE json_extract(data,'$.imported') IS NOT 1 AND type<>'UnreadBaselineSet'"));
                Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='UnreadBaselineSet'"));       // round 3: what was written before the unread view counts as read, once (not imported: it is new)
                Assert.Equal(1, c.ExecuteScalar<int>("SELECT MAX(schema) FROM events WHERE type IN ('ServerSetUp','MessagePosted')"));
                Assert.Equal(38, c.ExecuteScalar<int>("SELECT COUNT(*) FROM legacy_events"));
                Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='legacy_deliveries'"));
                foreach (var t in new[] { "PersonJoined", "PersonRemoved", "AgentCreated", "AgentRemoved", "RunnerEnrolled", "EnrolmentCodeIssued", "ChannelCreated", "MemberAdded", "InviteCreated", "InviteUsed", "InviteRevoked",
                    "MessagePosted", "ThreadStarted", "AgentJoinedThread", "AgentReplied", "ToolCallStarted", "ToolCallFinished", "PermissionRequested", "PermissionGranted", "MessageDelivered", "TurnStarted", "TurnEnded" })
                    Assert.Contains(t, types);
                // what was queued is still queued: three messages to sleeper, none lost, the position of its runner before the first
                var sleeper = c.ExecuteScalar<long>("SELECT id FROM members WHERE handle='sleeper'");
                var queued = c.Query<Deliveries.Row>(Deliveries.Queued, new { agentId = sleeper }).ToList();
                Assert.Equal(3, queued.Count);
                Assert.Contains(queued, q => q.Text.Contains("are you there?"));
                // the same ids carry on: a new post doesn't reuse one
                var alex = await SignIn(app, "alex", "correct horse");
                var ch = c.ExecuteScalar<long>("SELECT id FROM channels WHERE name='checkout-app'");
                var next = (await (await alex.Http.PostAsJsonAsync($"/api/channels/{ch}/posts", new { body = "after the upgrade" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
                Assert.True(next > c.ExecuteScalar<long>("SELECT MAX(id) FROM posts WHERE id<>@next", new { next }));
                // a rebuild from these events gives the same tables (and it is what the upgrade checked against the old tables)
                var before = RoundTwoTests.Dump(path);
                ((IEventStore)app.Services.GetService(typeof(IEventStore))!).RebuildProjections();
                Assert.Equal(before, RoundTwoTests.Dump(path));
            }
            // starting again doesn't import again
            int events;
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False")) { c.Open(); events = c.ExecuteScalar<int>("SELECT COUNT(*) FROM events"); }
            using (var again = new TestApp(existingDb: path))
            {
                _ = again.Server;
                using var c = again.Sql();
                Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='ServerSetUp'"));
                Assert.True(c.ExecuteScalar<int>("SELECT COUNT(*) FROM events") <= events + 1);                    // at most what startup itself records
                Assert.Equal(38, c.ExecuteScalar<int>("SELECT COUNT(*) FROM legacy_events"));
            }
        }
        finally { Clean(path); }
    }

    [Fact]
    public async Task A_database_from_before_any_events_is_upgraded_the_same_way()
    {
        var path = Copy(c => { c.Execute("DELETE FROM events"); c.Execute("UPDATE posts SET position=0"); });
        try
        {
            using var app = new TestApp(existingDb: path);
            _ = app.Server;
            await AssertSameAnswersAsBefore(app);
            using var c2 = app.Sql();
            Assert.Equal(0, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='legacy_events'"));      // nothing to move aside
            Assert.True(c2.ExecuteScalar<int>("SELECT COUNT(*) FROM events") > 50);
        }
        finally { Clean(path); }
    }

    [Fact]
    public async Task A_turn_that_was_in_flight_at_the_upgrade_is_interrupted_and_the_thread_says_so_and_a_failed_one_stays_failed()
    {
        var path = Copy(c => c.Execute("UPDATE deliveries SET status='sent' WHERE id=3; UPDATE deliveries SET status='failed' WHERE id=1"));
        try
        {
            using var app = new TestApp(existingDb: path);
            _ = app.Server;
            using var c = app.Sql();
            var interrupted = c.Query<(string Reason, long Agent)>("SELECT json_extract(data,'$.reason'), json_extract(data,'$.agentId') FROM events WHERE type='TurnInterrupted' ORDER BY position").ToList();
            Assert.Equal(2, interrupted.Count);                                                       // the failed one (imported) and the one in flight (startup recovery)
            Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM open_turns"));
            Assert.Contains("restarted", c.ExecuteScalar<string>("SELECT body FROM posts WHERE kind='notice' ORDER BY id DESC LIMIT 1"));
        }
        finally { Clean(path); }
    }

    [Fact]
    public async Task An_upgrade_that_cannot_reproduce_the_data_changes_nothing_and_refuses_to_start()
    {
        var path = Copy(c => c.Execute("INSERT INTO posts(channel_id, thread_id, author_id, body, kind, created_at) VALUES (2, NULL, 1, 'from the future', 'hologram', 1)"));
        try
        {
            var e = Assert.ThrowsAny<Exception>(() => { using var app = new TestApp(existingDb: path); _ = app.Server; });
            Assert.Contains("could not reproduce your data", e.ToString());
            using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open();
            Assert.Equal(38, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events"));                    // the round-1 events are still where they were
            Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='ServerSetUp'"));
            Assert.Equal(7, c.ExecuteScalar<int>("SELECT COUNT(*) FROM members"));
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM posts WHERE kind='hologram'"));
            Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='legacy_deliveries'"));
        }
        finally { Clean(path); }
    }
}
