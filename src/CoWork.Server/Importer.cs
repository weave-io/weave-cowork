using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>
/// The upgrade from a server that kept its state in tables. On the first start where people exist but the log has no `ServerSetUp` event, the tables
/// are written down as synthetic events (`data.imported = true`, `schema` 1) in a structural order: the server, members, channels, invites, then
/// posts in the order they were made with each thread and its agents, then the queued and past deliveries. In ONE transaction the projections are
/// emptied and rebuilt from those events and compared with what the tables held before; any difference undoes the whole thing and the server
/// refuses to start. Events a round-1 server already wrote are moved aside to `legacy_events` (the tables they built are what is imported).
/// </summary>
public static class LegacyImporter
{
    public sealed record Result(bool Imported, int Events, int MovedAside);

    public static Result Run(Db db, IEventStore store, Action<string>? log = null)
    {
        using var c = db.Open();
        var people = c.ExecuteScalar<int>("SELECT COUNT(*) FROM people");
        var setUp = c.ExecuteScalar<int>("SELECT COUNT(*) FROM events WHERE type='ServerSetUp'") > 0;
        if (people == 0 || setUp) { c.Execute("DROP TABLE IF EXISTS legacy_deliveries"); return new(false, 0, 0); }

        var movedAside = c.ExecuteScalar<int>("SELECT COUNT(*) FROM events");
        var events = Build(c);
        string before = "";
        var stored = store.Import(
            pc =>
            {
                before = Fingerprint(pc);
                if (movedAside > 0)
                {
                    pc.Execute("CREATE TABLE IF NOT EXISTS legacy_events AS SELECT * FROM events WHERE 0");
                    pc.Execute("INSERT INTO legacy_events SELECT * FROM events");
                    pc.Execute("DELETE FROM events");
                }
                Projections.Clear(pc);
            },
            events,
            pc =>
            {
                var after = Fingerprint(pc);
                if (after != before) throw new InvalidOperationException("The upgrade could not reproduce your data from events, so nothing was changed. " + FirstDifference(before, after));
                pc.Execute("DROP TABLE IF EXISTS legacy_deliveries");
            });
        log?.Invoke($"Upgraded: wrote {stored.Count} events from the tables" + (movedAside > 0 ? $" and moved {movedAside} older events aside to legacy_events." : "."));
        return new(true, stored.Count, movedAside);
    }

    // ---- what the tables say, as events ----

    private sealed class Mem { public long Id { get; set; } public string Kind { get; set; } = ""; public string Handle { get; set; } = ""; public string DisplayName { get; set; } = ""; public long CreatedAt { get; set; } public bool Removed { get; set; } public string? Role { get; set; }
        public long? OwnerId { get; set; } public string? About { get; set; } public string? Instructions { get; set; } public string? HarnessCommand { get; set; } public string? Workspace { get; set; } public string? RunnerHost { get; set; }
        public long? LastSeen { get; set; } public long? CodeExpires { get; set; } public bool RecordToolOutput { get; set; } public bool HasToken { get; set; } public bool HasCode { get; set; } }
    private sealed class Ch { public long Id { get; set; } public string Name { get; set; } = ""; public string Topic { get; set; } = ""; public bool IsPrivate { get; set; } public string Kind { get; set; } = "text"; public long CreatorId { get; set; } public long CreatedAt { get; set; } }
    private sealed class Inv { public long Id { get; set; } public long CreatedBy { get; set; } public long CreatedAt { get; set; } public long ExpiresAt { get; set; } public long MaxUses { get; set; } public long Uses { get; set; } public bool Revoked { get; set; } }
    private sealed class Th { public long RootId { get; set; } public long ChannelId { get; set; } public long StartedBy { get; set; } public long AgentChain { get; set; } }
    private sealed class Del { public long Id { get; set; } public long ThreadId { get; set; } public long AgentId { get; set; } public long PostId { get; set; } public string Text { get; set; } = ""; public string Status { get; set; } = "queued";
        public long CreatedAt { get; set; } public string? Turn { get; set; } public long? Behalf { get; set; } }

    private static Dictionary<string, object?> D(params (string, object?)[] kv)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) d[k] = v;
        d["imported"] = true;
        return d;
    }

    public static List<NewEvent> Build(SqliteConnection c)
    {
        var evs = new List<NewEvent>();
        int Add(NewEvent e) { evs.Add(e); return evs.Count - 1; }

        var members = c.Query<Mem>(@"SELECT m.id, m.kind, m.handle, m.display_name, m.created_at, m.removed, p.role, a.owner_id, a.about, a.instructions, a.harness_command, a.workspace, a.runner_host,
                a.last_seen, a.code_expires, COALESCE(a.record_tool_output,0) AS record_tool_output, COALESCE(rs.token_hash IS NOT NULL,0) AS has_token, COALESCE(rs.code_hash IS NOT NULL,0) AS has_code
            FROM members m LEFT JOIN people p ON p.member_id=m.id LEFT JOIN agents a ON a.member_id=m.id LEFT JOIN runner_secrets rs ON rs.agent_id=m.id ORDER BY m.id").ToList();
        var firstAt = members.Count > 0 ? members.Min(m => m.CreatedAt) : Db.Now();
        var admin = members.FirstOrDefault(m => m.Kind == "person");
        var serverName = c.ExecuteScalar<string>("SELECT value FROM settings WHERE key='server_name'") ?? "CoWork";
        Add(new NewEvent("server", "ServerSetUp", D(("serverName", serverName)), admin?.Id, OccurredAt: firstAt));

        foreach (var m in members)
        {
            if (m.Kind == "person")
            {
                var i = Add(new NewEvent("server", "PersonJoined", D(("memberId", m.Id), ("handle", m.Handle), ("displayName", m.DisplayName), ("role", m.Role ?? "member")), m.Id, OccurredAt: m.CreatedAt));
                if (m.Removed) Add(new NewEvent("server", "PersonRemoved", D(("memberId", m.Id)), null, CausedByIndex: i, OccurredAt: m.CreatedAt));
            }
            else
            {
                var s = $"agent-{m.Id}";
                var i = Add(new NewEvent(s, "AgentCreated", D(("agentId", m.Id), ("handle", m.Handle), ("displayName", m.DisplayName), ("ownerId", m.OwnerId), ("about", m.About ?? ""), ("instructions", m.Instructions ?? ""),
                    ("harnessCommand", JsonNode.Parse(m.HarnessCommand ?? "[]")), ("workspace", m.Workspace), ("recordToolOutput", m.RecordToolOutput)), m.OwnerId, OccurredAt: m.CreatedAt));
                if (m.HasCode && m.CodeExpires is { } exp) Add(new NewEvent(s, "EnrolmentCodeIssued", D(("agentId", m.Id), ("expiresAt", exp)), m.OwnerId, CausedByIndex: i, OccurredAt: m.CreatedAt));
                if (m.HasToken) Add(new NewEvent(s, "RunnerEnrolled", D(("agentId", m.Id), ("host", m.RunnerHost)), m.Id, CausedByIndex: i, OccurredAt: m.LastSeen ?? m.CreatedAt));
                else if (m.RunnerHost is not null) Add(new NewEvent(s, "RunnerConnected", D(("agentId", m.Id), ("host", m.RunnerHost)), m.Id, CausedByIndex: i, OccurredAt: m.LastSeen ?? m.CreatedAt));
                if (m.Removed) Add(new NewEvent(s, "AgentRemoved", D(("agentId", m.Id)), null, CausedByIndex: i, OccurredAt: m.CreatedAt));
            }
        }

        foreach (var ch in c.Query<Ch>("SELECT id, name, topic, is_private, kind, creator_id, created_at FROM channels ORDER BY id"))
        {
            var s = $"channel-{ch.Id}";
            var i = Add(new NewEvent(s, "ChannelCreated", D(("channelId", ch.Id), ("name", ch.Name), ("topic", ch.Topic), ("isPrivate", ch.IsPrivate), ("kind", ch.Kind), ("creatorId", ch.CreatorId)), ch.CreatorId, OccurredAt: ch.CreatedAt));
            foreach (var m in c.Query<long>("SELECT member_id FROM channel_members WHERE channel_id=@id ORDER BY member_id", new { id = ch.Id }))
                Add(new NewEvent(s, "MemberAdded", D(("channelId", ch.Id), ("memberId", m)), null, CausedByIndex: i, OccurredAt: ch.CreatedAt));
        }

        foreach (var v in c.Query<Inv>("SELECT id, created_by, created_at, expires_at, max_uses, uses, revoked FROM invites ORDER BY id"))
        {
            var i = Add(new NewEvent("server", "InviteCreated", D(("inviteId", v.Id), ("createdBy", v.CreatedBy), ("expiresAt", v.ExpiresAt), ("maxUses", v.MaxUses)), v.CreatedBy, OccurredAt: v.CreatedAt));
            for (var n = 0; n < v.Uses; n++) Add(new NewEvent("server", "InviteUsed", D(("inviteId", v.Id)), null, CausedByIndex: i, OccurredAt: v.CreatedAt));
            if (v.Revoked) Add(new NewEvent("server", "InviteRevoked", D(("inviteId", v.Id)), null, CausedByIndex: i, OccurredAt: v.CreatedAt));
        }

        // posts in the order they were made; a thread starts right after its root post
        var kinds = members.ToDictionary(m => m.Id, m => m.Kind);
        var owners = members.Where(m => m.Kind == "agent").ToDictionary(m => m.Id, m => m.OwnerId);
        var threads = c.Query<Th>("SELECT root_id, channel_id, started_by, agent_chain FROM threads").ToDictionary(t => t.RootId);
        var joined = c.Query<(long Thread, long Agent)>("SELECT thread_id, agent_id FROM thread_agents ORDER BY thread_id, agent_id").GroupBy(x => x.Thread).ToDictionary(g => g.Key, g => g.Select(x => x.Agent).ToList());
        var posts = c.Query<PostRow>("SELECT id, channel_id, thread_id, author_id, body, kind, meta, streaming, created_at FROM posts ORDER BY id").ToList();
        // the last person's reply in a thread carries the agent-to-agent count that followed it (a thread with no reply by a person: the thread itself does)
        var lastPersonReply = posts.Where(p => p.ThreadId is not null && p.Kind == "text" && kinds.GetValueOrDefault(p.AuthorId) == "person").GroupBy(p => p.ThreadId!.Value).ToDictionary(g => g.Key, g => g.Max(p => p.Id));
        foreach (var p in posts)
        {
            var thread = p.ThreadId;
            var s = thread is null ? $"channel-{p.ChannelId}" : $"thread-{thread}";
            var meta = string.IsNullOrEmpty(p.Meta) ? null : JsonNode.Parse(p.Meta)!.AsObject();
            switch (p.Kind)
            {
                case "text" when thread is not null && kinds.GetValueOrDefault(p.AuthorId) == "agent":
                    Add(new NewEvent(s, "AgentReplied", D(("postId", p.Id), ("channelId", p.ChannelId), ("text", p.Body), ("segment", 0), ("complete", !p.Streaming)), p.AuthorId, OccurredAt: p.CreatedAt));
                    break;
                case "text":
                {
                    var chain = thread is { } t && lastPersonReply.GetValueOrDefault(t) == p.Id ? threads.GetValueOrDefault(t)?.AgentChain ?? 0 : 0;
                    Add(new NewEvent(s, "MessagePosted", D(("postId", p.Id), ("channelId", p.ChannelId), ("threadId", thread), ("body", p.Body), ("mentions", Dispatcher.Mentions(p.Body).ToArray()), ("agentChain", chain)), p.AuthorId, OccurredAt: p.CreatedAt));
                    break;
                }
                case "tool":
                {
                    var status = (string?)meta?["status"] ?? "completed";
                    var i = Add(new NewEvent(s, "ToolCallStarted", D(("postId", p.Id), ("channelId", p.ChannelId), ("toolId", (string?)meta?["toolId"]), ("title", p.Body), ("toolKind", (string?)meta?["toolKind"]), ("status", "running")), p.AuthorId, OccurredAt: p.CreatedAt));
                    if (status != "running") Add(new NewEvent(s, "ToolCallFinished", D(("postId", p.Id), ("toolId", (string?)meta?["toolId"]), ("status", status), ("title", p.Body)), p.AuthorId, CausedByIndex: i, OccurredAt: p.CreatedAt));
                    break;
                }
                case "ask":
                {
                    var owner = (long?)meta?["ownerId"] ?? owners.GetValueOrDefault(p.AuthorId) ?? 0;
                    var i = Add(new NewEvent(s, "PermissionRequested", D(("postId", p.Id), ("channelId", p.ChannelId), ("requestId", (string?)meta?["requestId"]), ("agentId", p.AuthorId), ("ownerId", owner),
                        ("title", p.Body), ("options", meta?["options"]?.DeepClone() ?? new JsonArray())), p.AuthorId, OccurredAt: p.CreatedAt));
                    var st = (string?)meta?["status"];
                    if (st is "answered" or "denied")
                        Add(new NewEvent(s, st == "answered" ? "PermissionGranted" : "PermissionDenied", D(("postId", p.Id), ("requestId", (string?)meta?["requestId"]), ("agentId", p.AuthorId), ("optionId", st == "answered" ? (string?)meta?["answer"] : null)), owner, CausedByIndex: i, OccurredAt: p.CreatedAt));
                    break;
                }
                case "notice":
                    Add(new NewEvent(s, "TurnFailed", D(("agentId", p.AuthorId), ("channelId", p.ChannelId), ("postId", p.Id), ("reason", p.Body), ("ended", false)), p.AuthorId, OccurredAt: p.CreatedAt));
                    break;
            }
            if (thread is null && threads.TryGetValue(p.Id, out var th))
            {
                var i = Add(new NewEvent($"thread-{p.Id}", "ThreadStarted", D(("rootId", p.Id), ("channelId", th.ChannelId), ("startedBy", th.StartedBy), ("agentChain", lastPersonReply.ContainsKey(p.Id) ? 0 : th.AgentChain)), null, th.StartedBy, CausedByIndex: evs.Count - 1, OccurredAt: p.CreatedAt));
                foreach (var a in joined.GetValueOrDefault(p.Id) ?? new())
                    Add(new NewEvent($"thread-{p.Id}", "AgentJoinedThread", D(("agentId", a), ("ownerId", owners.GetValueOrDefault(a))), null, th.StartedBy, CausedByIndex: i, OccurredAt: p.CreatedAt));
            }
        }

        // what was handed to agents: queued stays queued, the rest has its turn
        foreach (var d in c.Query<Del>("SELECT id, thread_id, agent_id, post_id, text, status, created_at, turn, behalf FROM legacy_deliveries ORDER BY id"))
        {
            if (!threads.ContainsKey(d.ThreadId)) continue;
            var s = $"thread-{d.ThreadId}"; var turn = d.Turn ?? $"t-imp-{d.Id}";
            var i = Add(new NewEvent(s, "MessageDelivered", D(("agentId", d.AgentId), ("postId", d.PostId), ("chained", false), ("text", d.Text)), null, d.Behalf, turn, OccurredAt: d.CreatedAt));
            if (d.Status == "queued") continue;
            var started = Add(new NewEvent(s, "TurnStarted", D(("agentId", d.AgentId), ("deliveryId", d.Id)), d.AgentId, d.Behalf, turn, CausedByIndex: i, OccurredAt: d.CreatedAt));
            if (d.Status == "done") Add(new NewEvent(s, "TurnEnded", D(("agentId", d.AgentId)), d.AgentId, d.Behalf, turn, CausedByIndex: started, OccurredAt: d.CreatedAt));
            else if (d.Status == "failed") Add(new NewEvent(s, "TurnInterrupted", D(("agentId", d.AgentId), ("reason", "the turn did not finish before the upgrade")), d.AgentId, d.Behalf, turn, CausedByIndex: started, OccurredAt: d.CreatedAt));
        }
        return evs;
    }

    // ---- the check: the same tables, before and after, in a form that ignores what is new (positions) ----

    /// <summary>What the app can show, table by table, in a stable text form. Not compared: event positions, `last_seen`, an enrolment code's expiry (new facts the old tables didn't have).</summary>
    public static string Fingerprint(ProjectionContext x)
    {
        var sb = new System.Text.StringBuilder();
        void Table(string name, string sql)
        {
            foreach (var row in x.Query<dynamic>(sql))
                sb.Append(name).Append(": ").AppendLine(string.Join(" | ", ((IDictionary<string, object>)row).Values.Select(v => v?.ToString() ?? "∅")));
        }
        Table("settings", "SELECT key, value FROM settings ORDER BY key");
        Table("members", "SELECT id, kind, handle, display_name, created_at, removed FROM members ORDER BY id");
        Table("people", "SELECT member_id, role FROM people ORDER BY member_id");
        Table("agents", "SELECT member_id, owner_id, about, instructions, harness_command, workspace, runner_host, record_tool_output FROM agents ORDER BY member_id");
        Table("invites", "SELECT id, created_by, created_at, expires_at, max_uses, uses, revoked FROM invites ORDER BY id");
        Table("channels", "SELECT id, name, topic, is_private, kind, creator_id, created_at FROM channels ORDER BY id");
        Table("channel_members", "SELECT channel_id, member_id FROM channel_members ORDER BY channel_id, member_id");
        Table("threads", "SELECT root_id, channel_id, started_by, agent_chain FROM threads ORDER BY root_id");
        Table("thread_agents", "SELECT thread_id, agent_id FROM thread_agents ORDER BY thread_id, agent_id");
        foreach (var p in x.Query<PostRow>("SELECT id, channel_id, thread_id, author_id, body, kind, meta, streaming, created_at FROM posts ORDER BY id"))
            sb.Append("post: ").Append(string.Join(" | ", p.Id, p.ChannelId, p.ThreadId, p.AuthorId, p.Body, p.Kind, p.Streaming, p.CreatedAt, CanonicalMeta(p))).AppendLine();
        return sb.ToString();
    }

    /// <summary>A post's meta without what carries no information: nulls, and a tool's title (the post's body says it, and an old row may hold null there).</summary>
    private static string CanonicalMeta(PostRow p)
    {
        if (string.IsNullOrEmpty(p.Meta)) return "";
        var o = JsonNode.Parse(p.Meta)!.AsObject();
        if (p.Kind == "tool") o.Remove("title");
        var keys = o.Where(kv => kv.Value is not null).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value!.ToJsonString()}");
        return string.Join(",", keys);
    }

    private static string FirstDifference(string a, string b)
    {
        var la = a.Split('\n'); var lb = b.Split('\n');
        for (var i = 0; i < Math.Max(la.Length, lb.Length); i++)
            if ((i < la.Length ? la[i] : null) != (i < lb.Length ? lb[i] : null))
                return $"First difference: before «{(i < la.Length ? la[i] : "(missing)")}», after «{(i < lb.Length ? lb[i] : "(missing)")}».";
        return "";
    }
}
