using Dapper;

namespace CoWork.Server;

/// <summary>
/// The audit: one readable sentence for each thing that happened. A tool call and a turn are made of several events (the ask, the answer, the
/// finish, the host it ran on), found through the turn they share (`correlation`) and the position that caused them (`causation`: an answer
/// names the ask it answers), and told as one sentence that is rewritten as the later events arrive:
///   "Rosa asked reviewer (Alex's agent) to run `git log -3`. Alex allowed it once. It ran on build-01."
/// Names come from the events themselves (`audit_names`), so a rebuild gives the same sentences however the names changed since.
/// Message text is never put in the audit; only who, where and what kind of thing.
/// </summary>
public sealed class AuditProjection : IAsyncProjection
{
    public string Name => "audit";
    public string Title => "Audit log";
    public string[] Tables => new[] { "audit_members", "audit_entries", "audit_turns", "audit_threads", "audit_names", "audit_invites" };

    public sealed class Entry
    {
        public long Id { get; set; } public string Kind { get; set; } = ""; public long OccurredAt { get; set; }
        public long? ActorId { get; set; } public long? BehalfId { get; set; } public long? AgentId { get; set; }
        public long? ChannelId { get; set; } public long? ThreadId { get; set; }
        public string? Correlation { get; set; } public string? ToolId { get; set; }
        public string? Verb { get; set; } public string? Detail { get; set; }
        public long? AskPosition { get; set; } public string? AskState { get; set; } public long? AnsweredBy { get; set; }
        public string? RunState { get; set; } public string? Reason { get; set; } public string Sentence { get; set; } = "";
    }

    // ---- names ----
    private static string? NameOf(ProjectionContext x, string kind, long? id) =>
        id is null ? null : x.Scalar<string>("SELECT name FROM audit_names WHERE kind=@kind AND id=@id", new { kind, id });
    private static string Person(ProjectionContext x, long? id) => NameOf(x, "person", id) ?? NameOf(x, "agent", id) ?? "Someone";
    private static string Channel(ProjectionContext x, long? id) => NameOf(x, "channel", id) is { } n ? "#" + n : "a channel";
    private static string Project(ProjectionContext x, long? id) => NameOf(x, "project", id) ?? "a project";
    private static string AgentName(ProjectionContext x, long? id) => id is null ? "an agent" : NameOf(x, "agent", id) ?? "an agent";
    /// <summary>"reviewer (Alex's agent)": who it answers to, said where an agent does something for someone.</summary>
    private static string Agent(ProjectionContext x, long? id)
    {
        if (id is null) return "an agent";
        var name = NameOf(x, "agent", id) ?? "an agent";
        var owner = x.Scalar<long?>("SELECT owner_id FROM audit_names WHERE kind='agent' AND id=@id", new { id });
        return owner is null ? name : $"{name} ({NameOf(x, "person", owner) ?? "someone"}'s agent)";
    }
    private static string Actor(ProjectionContext x, StoredEvent e) => e.ActorId is null ? "Someone" : Person(x, e.ActorId);

    // ---- writing entries ----
    private static void Add(ProjectionContext x, StoredEvent e, string kind, string sentence, long? channel = null, long? thread = null, params long?[] members)
    {
        x.Execute(@"INSERT OR REPLACE INTO audit_entries(id, kind, occurred_at, actor_id, behalf_id, channel_id, thread_id, correlation, sentence)
            VALUES (@id,@kind,@at,@actor,@behalf,@channel,@thread,@corr,@sentence)",
            new { id = e.Position, kind, at = e.OccurredAt, actor = e.ActorId, behalf = e.OnBehalfOf, channel, thread, corr = e.Correlation, sentence });
        Members(x, e.Position, new[] { e.ActorId, e.OnBehalfOf }.Concat(members));
    }

    private static void Members(ProjectionContext x, long entry, IEnumerable<long?> ids)
    {
        foreach (var id in ids.Where(i => i is not null).Distinct())
            x.Execute("INSERT OR IGNORE INTO audit_members VALUES (@entry,@id)", new { entry, id });
    }

    private static Entry? Find(ProjectionContext x, string sql, object args) => x.Query<Entry>(sql, args).FirstOrDefault();

    private static void Save(ProjectionContext x, Entry t, Func<Entry, string> compose)
    {
        t.Sentence = compose(t);
        x.Execute(@"UPDATE audit_entries SET verb=@Verb, detail=@Detail, ask_position=@AskPosition, ask_state=@AskState, answered_by=@AnsweredBy,
            run_state=@RunState, reason=@Reason, sentence=@Sentence, agent_id=@AgentId WHERE id=@Id", t);
    }

    private static string Code(string? s) => (s ?? "").Replace('`', '\'').Trim();
    private static string On(string? host) => string.IsNullOrWhiteSpace(host) ? "" : " on " + host;

    private static string Verb(string? toolKind) => toolKind switch
    {
        "execute" => "run", "read" => "read", "edit" => "edit", "delete" => "delete", "move" => "move",
        "search" => "search for", "fetch" => "fetch", _ => "use",
    };

    // ---- the sentences ----
    private static string ComposeTool(ProjectionContext x, Entry t)
    {
        var agent = Agent(x, t.AgentId);
        var what = $"{t.Verb ?? "use"} `{Code(t.Detail)}`";
        var s = t.BehalfId is null ? $"{agent} wanted to {what}." : $"{Person(x, t.BehalfId)} asked {agent} to {what}.";
        var owner = x.Scalar<long?>("SELECT owner_id FROM audit_names WHERE kind='agent' AND id=@id", new { id = t.AgentId });
        switch (t.AskState)
        {
            case "pending": s += t.RunState == "unfinished" ? " Nobody answered." : $" Waiting for {Person(x, owner)} to answer."; break;
            case "once": s += $" {Person(x, t.AnsweredBy)} allowed it once."; break;
            case "always": s += $" {Person(x, t.AnsweredBy)} allowed it every time."; break;
            case "denied": s += $" {Person(x, t.AnsweredBy)} said no."; return s;
        }
        var host = x.Scalar<string>("SELECT host FROM audit_turns WHERE turn=@c", new { c = t.Correlation });
        return s + t.RunState switch
        {
            "completed" => $" It ran{On(host)}.",
            "failed" => $" It failed{On(host)}.",
            "running" => t.AskState == "pending" ? "" : $" It is running{On(host)}.",
            "unfinished" => " It didn't finish.",
            null => "",
            var other => $" It ended as {other}{On(host)}.",
        };
    }

    private static string ComposeTurn(ProjectionContext x, Entry t)
    {
        var agent = Agent(x, t.AgentId);
        var host = x.Scalar<string>("SELECT host FROM audit_turns WHERE turn=@c", new { c = t.Correlation });
        var where = t.ChannelId is null ? "" : $" in {Channel(x, t.ChannelId)}";
        var s = t.BehalfId is null ? $"{agent} took a turn{where}." : $"{Person(x, t.BehalfId)} messaged {agent}{where}.";
        return s + t.RunState switch
        {
            "finished" => $" It answered{On(host)}.",
            "failed" => $" It failed{On(host)}: {Code(t.Reason)}",
            "interrupted" => $" It was cut off{On(host)}: {Code(t.Reason)}",
            _ => $" It is working{On(host)}.",
        };
    }

    public void Apply(ProjectionContext x, StoredEvent e)
    {
        var thread = ThreadProjection.ThreadOf(e);
        switch (e.Type)
        {
            // names: kept from the events, in order
            case "ServerSetUp": Add(x, e, "event", $"{e.Str("serverName")} was set up."); break;       // the first person joins in the next event, so there is no name to give yet
            case "PersonJoined":
            {
                var id = e.Long("memberId");
                x.Execute("INSERT OR REPLACE INTO audit_names(kind, id, name, handle) VALUES ('person',@id,@name,@handle)", new { id, name = e.Str("displayName") ?? e.Str("handle"), handle = e.Str("handle") });
                var inviter = e.LongOrNull("inviteId") is { } inv ? x.Scalar<long?>("SELECT created_by FROM audit_invites WHERE invite_id=@inv", new { inv }) : null;
                var how = e.LongOrNull("inviteId") is not null ? " with an invite link" + (inviter is null ? "" : $" from {Person(x, inviter)}") : e.Str("role") == "admin" ? " as an admin" : "";
                Add(x, e, "event", $"{Person(x, id)} joined{how}.", null, null, id, inviter);
                break;
            }
            case "RoleChanged": Add(x, e, "event", $"{Actor(x, e)} made {Person(x, e.Long("memberId"))} {(e.Str("role") == "admin" ? "an admin" : "a member")}.", null, null, e.Long("memberId")); break;
            case "PersonRemoved": Add(x, e, "event", $"{Actor(x, e)} removed {Person(x, e.Long("memberId"))}.", null, null, e.Long("memberId")); break;
            case "InviteCreated":
                x.Execute("INSERT OR REPLACE INTO audit_invites VALUES (@i,@by)", new { i = e.Long("inviteId"), by = e.LongOrNull("createdBy") ?? e.ActorId ?? 0 });
                Add(x, e, "event", $"{Actor(x, e)} created an invite link.");
                break;
            case "InviteUsed": break;                // told once, by the sign-up it belongs to ("Rosa joined with an invite link from Alex.")
            case "InviteRevoked": Add(x, e, "event", $"{Actor(x, e)} revoked an invite link."); break;

            case "ChannelCreated":
            {
                var id = e.Long("channelId");
                x.Execute("INSERT OR REPLACE INTO audit_names(kind, id, name) VALUES ('channel',@id,@name)", new { id, name = e.Str("name") });
                var what = e.Str("kind") == "voice" ? "voice channel" : "channel";
                var inProject = e.LongOrNull("projectId") is { } pid ? $" in {Project(x, pid)}" : "";
                Add(x, e, "event", $"{Actor(x, e)} created the {(e.Bool("isPrivate") ? "private " : "")}{what} {Channel(x, id)}{inProject}.", id);
                break;
            }
            case "ChannelRenamed":
            {
                var id = e.Long("channelId"); var was = Channel(x, id);
                x.Execute("UPDATE audit_names SET name=@name WHERE kind='channel' AND id=@id", new { id, name = e.Str("name") });
                Add(x, e, "event", $"{Actor(x, e)} renamed {was} to {Channel(x, id)}.", id);
                break;
            }
            case "TopicChanged": Add(x, e, "event", $"{Actor(x, e)} changed the topic of {Channel(x, e.Long("channelId"))}.", e.Long("channelId")); break;
            case "ChannelMadePrivate": Add(x, e, "event", $"{Actor(x, e)} made {Channel(x, e.Long("channelId"))} private.", e.Long("channelId")); break;
            case "ChannelMadePublic": Add(x, e, "event", $"{Actor(x, e)} made {Channel(x, e.Long("channelId"))} public.", e.Long("channelId")); break;
            case "MemberAdded":
                if (e.ActorId != e.LongOrNull("memberId")) Add(x, e, "event", $"{Actor(x, e)} added {Person(x, e.Long("memberId"))} to {Channel(x, e.Long("channelId"))}.", e.Long("channelId"), null, e.Long("memberId"));
                break;
            case "MemberRemoved": Add(x, e, "event", $"{Actor(x, e)} removed {Person(x, e.Long("memberId"))} from {Channel(x, e.Long("channelId"))}.", e.Long("channelId"), null, e.Long("memberId")); break;
            case "ChannelDeleted":
            {
                var id = e.Long("channelId");
                if (e.Str("name") is { } n) x.Execute("INSERT OR REPLACE INTO audit_names(kind, id, name) VALUES ('channel',@id,@name)", new { id, name = n });
                Add(x, e, "event", $"{Actor(x, e)} deleted {Channel(x, id)}.", id);
                break;
            }

            case "ChannelMovedToProject":
            {
                var id = e.Long("channelId");
                var s2 = e.LongOrNull("projectId") is { } to ? $"{Actor(x, e)} moved {Channel(x, id)} into {Project(x, to)}." : $"{Actor(x, e)} moved {Channel(x, id)} out of {Project(x, e.LongOrNull("from"))}.";
                Add(x, e, "event", s2, id);
                break;
            }

            // ---- projects ----
            case "ProjectCreated":
            {
                var id = e.Long("projectId");
                x.Execute("INSERT OR REPLACE INTO audit_names(kind, id, name) VALUES ('project',@id,@name)", new { id, name = e.Str("name") });
                Add(x, e, "event", $"{Actor(x, e)} created the project {Project(x, id)}.");
                break;
            }
            case "ProjectUpdated":
            {
                var id = e.Long("projectId"); var was = Project(x, id);
                if (e.Str("name") is { } n) { x.Execute("UPDATE audit_names SET name=@n WHERE kind='project' AND id=@id", new { id, n }); Add(x, e, "event", $"{Actor(x, e)} renamed the project {was} to {Project(x, id)}."); }
                else Add(x, e, "event", $"{Actor(x, e)} changed what {was} is about.");
                break;
            }
            case "ProjectMemberAdded":
            {
                var m = e.Long("memberId"); var role = e.Str("role") == "lead" ? "a lead" : "a member";
                Add(x, e, "event", m == e.ActorId ? $"{Person(x, m)} is {role} of {Project(x, e.Long("projectId"))}." : $"{Actor(x, e)} added {Person(x, m)} to {Project(x, e.Long("projectId"))} as {role}.", null, null, m);
                break;
            }
            case "ProjectMemberRemoved":
            {
                var m = e.Long("memberId");
                Add(x, e, "event", m == e.ActorId ? $"{Person(x, m)} left {Project(x, e.Long("projectId"))}." : $"{Actor(x, e)} removed {Person(x, m)} from {Project(x, e.Long("projectId"))}.", null, null, m);
                break;
            }
            case "WorkspaceAdded":
            {
                var what = e.Str("kind") == "folder" ? $"the folder `{Code(e.Str("path"))}` on {e.Str("host")}" : $"the repository `{Code(e.Str("url"))}` (branch {e.Str("branch")})";
                Add(x, e, "event", $"{Actor(x, e)} added {what} to {Project(x, e.Long("projectId"))} as {e.Str("name")}.");
                break;
            }
            case "WorkspaceRemoved": Add(x, e, "event", $"{Actor(x, e)} removed the workspace {e.Str("name") ?? "it had"} from {Project(x, e.Long("projectId"))}."); break;
            case "AgentAddedToProject": case "AgentGrantsChanged":
            {
                var ag = e.Long("agentId"); var p = Project(x, e.Long("projectId"));
                var owner = x.Scalar<long?>("SELECT owner_id FROM audit_names WHERE kind='agent' AND id=@a", new { a = ag });
                var from = e.Str("takesWorkFrom") == "owner" ? $"{Person(x, owner)} only" : $"the members of {p}";
                Add(x, e, "event", e.Type == "AgentAddedToProject" ? $"{Actor(x, e)} added {Agent(x, ag)} to {p}. It takes work from {from}." : $"{Actor(x, e)} set {Agent(x, ag)} to take work from {from} in {p}.",
                    null, null, ag, owner);
                break;
            }
            case "AgentRemovedFromProject": Add(x, e, "event", $"{Actor(x, e)} took {Agent(x, e.Long("agentId"))} out of {Project(x, e.Long("projectId"))}.", null, null, e.Long("agentId")); break;
            case "WorkRefused":
            {
                var ag = e.Long("agentId"); var ch = e.Long("channelId");
                var owner = x.Scalar<long?>("SELECT owner_id FROM audit_names WHERE kind='agent' AND id=@a", new { a = ag });
                var why = e.Str("grant") == "owner" ? $"it takes work from {Person(x, owner)} only" : $"it takes work from the members of {Project(x, e.LongOrNull("projectId"))} only";
                var who = e.OnBehalfOf is null ? "Someone" : Person(x, e.OnBehalfOf);
                Add(x, e, "event", $"{who} asked {Agent(x, ag)} for work in {Channel(x, ch)}. It didn't take it: {why}.", ch, thread, ag, owner);
                break;
            }
            case "ThreadRenamed":
            {
                var ch = e.LongOrNull("channelId");
                Add(x, e, "event", $"{Actor(x, e)} renamed a thread{(ch is null ? "" : " in " + Channel(x, ch))} to “{e.Str("title")}”.", ch, thread);
                break;
            }
            case "MentionUnreachable":
            {
                var ag = e.Long("agentId"); var ch = e.Long("channelId");
                var owner = x.Scalar<long?>("SELECT owner_id FROM audit_names WHERE kind='agent' AND id=@a", new { a = ag });
                var why = e.Str("reason") switch
                {
                    "project" => $"it isn't in {Project(x, e.LongOrNull("projectId"))}",
                    "channel" => $"it isn't in {Channel(x, ch)}",
                    _ => "it has no runner yet",
                };
                var who = e.OnBehalfOf is null ? "Someone" : Person(x, e.OnBehalfOf);
                Add(x, e, "event", $"{who} mentioned {Agent(x, ag)} in {Channel(x, ch)}. It couldn't take it: {why}.", ch, thread, ag, owner);
                break;
            }
            case "ThreadWorkspaceReady":
            {
                var ag = e.Long("agentId");
                var channel = x.Scalar<long?>("SELECT channel_id FROM audit_threads WHERE thread_id=@thread", new { thread });
                var where = e.Str("place") switch
                {
                    "worktree" => $"in its own worktree on `{Code(e.Str("branch"))}`",
                    "folder" => $"in the folder `{Code(e.Str("location"))}`",
                    "scratch" => "in an empty folder of its own",
                    _ => "in its own workspace folder",
                };
                Add(x, e, "event", $"{Agent(x, ag)} works on a thread in {Channel(x, channel)} {where}{On(e.Str("host"))}, {(e.Bool("sandboxed") ? "sandboxed" : "unsandboxed")}.", channel, thread, ag);
                break;
            }

            case "ThreadStarted":
                x.Execute("INSERT OR REPLACE INTO audit_threads VALUES (@t,@ch)", new { t = e.Long("rootId"), ch = e.Long("channelId") });
                break;
            case "MessagePosted":
            {
                var ch = e.Long("channelId"); var inThread = e.LongOrNull("threadId") is not null;
                var handles = e.Data.TryGetProperty("mentions", out var m) ? m.EnumerateArray().Select(hh => hh.GetString() ?? "").ToList() : new List<string>();
                var named = handles.Select(h => x.Scalar<string>("SELECT name FROM audit_names WHERE handle=@h", new { h })).Where(n => n is not null).ToList();
                var s = $"{Actor(x, e)} {(inThread ? "replied in a thread in" : "posted in")} {Channel(x, ch)}" + (named.Count > 0 ? $", mentioning {string.Join(" and ", named)}" : "") + ".";
                Add(x, e, "event", s, ch, e.LongOrNull("threadId") ?? (inThread ? null : e.Long("postId")));
                break;
            }
            case "MessageDeleted": Add(x, e, "event", $"{Actor(x, e)} deleted a message in {Channel(x, e.Long("channelId"))}.", e.Long("channelId")); break;
            case "MessageRedacted": Add(x, e, "event", $"{Actor(x, e)} redacted a message in {Channel(x, e.Long("channelId"))}.", e.Long("channelId")); break;

            case "AgentCreated":
            {
                var id = e.Long("agentId");
                x.Execute("INSERT OR REPLACE INTO audit_names(kind, id, name, handle, owner_id) VALUES ('agent',@id,@name,@handle,@owner)",
                    new { id, name = e.Str("displayName") ?? e.Str("handle"), handle = e.Str("handle"), owner = e.LongOrNull("ownerId") });
                Add(x, e, "event", $"{Actor(x, e)} created the agent {AgentName(x, id)}.", null, null, id, e.LongOrNull("ownerId"));
                break;
            }
            case "AgentEdited":
            {
                var id = e.Long("agentId");
                if (e.Str("displayName") is { } dn) x.Execute("UPDATE audit_names SET name=@dn WHERE kind='agent' AND id=@id", new { id, dn });
                Add(x, e, "event", $"{Actor(x, e)} edited {AgentName(x, id)}.", null, null, id);
                break;
            }
            case "EnrolmentCodeIssued": Add(x, e, "event", $"{Actor(x, e)} issued a runner code for {AgentName(x, e.Long("agentId"))}.", null, null, e.Long("agentId")); break;
            case "RunnerEnrolled": Add(x, e, "event", $"{AgentName(x, e.Long("agentId"))}'s runner enrolled{(e.Str("host") is { } eh ? " from " + eh : "")}.", null, null, e.Long("agentId")); break;
            case "RunnerConnected": Add(x, e, "event", $"{AgentName(x, e.Long("agentId"))}'s runner connected{(e.Str("host") is { } rh ? " from " + rh : "")}.", null, null, e.Long("agentId")); break;
            case "RunnerDisconnected": Add(x, e, "event", $"{AgentName(x, e.Long("agentId"))}'s runner disconnected.", null, null, e.Long("agentId")); break;
            case "TokenRevoked": Add(x, e, "event", $"{Actor(x, e)} disconnected the runner of {AgentName(x, e.Long("agentId"))}.", null, null, e.Long("agentId")); break;
            case "AgentRemoved": Add(x, e, "event", $"{Actor(x, e)} removed the agent {AgentName(x, e.Long("agentId"))}.", null, null, e.Long("agentId")); break;

            // ---- a turn, and the tool calls and asks inside it ----
            case "TurnStarted" when e.Correlation is not null:
            {
                var channel = x.Scalar<long?>("SELECT channel_id FROM audit_threads WHERE thread_id=@thread", new { thread });
                x.Execute("INSERT OR REPLACE INTO audit_turns VALUES (@c,@agent,@behalf,@thread,@host)", new { c = e.Correlation, agent = e.ActorId, behalf = e.OnBehalfOf, thread, host = e.Str("host") });
                var t = new Entry { Id = e.Position, Kind = "turn", OccurredAt = e.OccurredAt, ActorId = e.ActorId, BehalfId = e.OnBehalfOf, AgentId = e.ActorId, ChannelId = channel, ThreadId = thread, Correlation = e.Correlation, RunState = "running" };
                x.Execute("DELETE FROM audit_entries WHERE kind='turn' AND correlation=@c", new { c = e.Correlation });       // a turn that starts again after being put back in the queue
                x.Execute(@"INSERT INTO audit_entries(id, kind, occurred_at, actor_id, behalf_id, agent_id, channel_id, thread_id, correlation, run_state, sentence)
                    VALUES (@Id,'turn',@OccurredAt,@ActorId,@BehalfId,@AgentId,@ChannelId,@ThreadId,@Correlation,'running','')", t);
                Members(x, t.Id, new[] { t.ActorId, t.BehalfId, x.Scalar<long?>("SELECT owner_id FROM audit_names WHERE kind='agent' AND id=@id", new { id = t.AgentId }) });
                Save(x, t, tt => ComposeTurn(x, tt));
                break;
            }
            case "ToolCallStarted" when e.Correlation is not null:
            {
                var channel = x.Scalar<long?>("SELECT channel_id FROM audit_threads WHERE thread_id=@thread", new { thread });
                var t = new Entry { Id = e.Position, Kind = "tool", OccurredAt = e.OccurredAt, ActorId = e.ActorId, BehalfId = e.OnBehalfOf, AgentId = e.ActorId, ChannelId = channel, ThreadId = thread,
                    Correlation = e.Correlation, ToolId = e.Str("toolId"), Verb = Verb(e.Str("toolKind")), Detail = e.Str("title"), RunState = e.Str("status") ?? "running" };
                NewTool(x, t);
                break;
            }
            case "ToolCallDetailed" when e.Correlation is not null:
                if (Find(x, "SELECT * FROM audit_entries WHERE kind='tool' AND correlation=@c AND tool_id=@tool", new { c = e.Correlation, tool = e.Str("toolId") }) is { } d && !string.IsNullOrWhiteSpace(e.Str("title")))
                { d.Detail = e.Str("title"); Save(x, d, t => ComposeTool(x, t)); }
                break;
            case "ToolCallFinished" when e.Correlation is not null:
                if (Find(x, "SELECT * FROM audit_entries WHERE kind='tool' AND correlation=@c AND tool_id=@tool", new { c = e.Correlation, tool = e.Str("toolId") }) is { } f)
                {
                    if (!string.IsNullOrWhiteSpace(e.Str("title"))) f.Detail = e.Str("title");
                    f.RunState = e.Str("status") ?? "completed"; Save(x, f, t => ComposeTool(x, t));
                }
                break;
            case "PermissionRequested" when e.Correlation is not null:
            {
                // the ask belongs to the tool call the agent has just started and nobody has asked about yet
                var t = Find(x, "SELECT * FROM audit_entries WHERE kind='tool' AND correlation=@c AND ask_position IS NULL ORDER BY id DESC LIMIT 1", new { c = e.Correlation });
                if (t is null)
                {
                    var channel = x.Scalar<long?>("SELECT channel_id FROM audit_threads WHERE thread_id=@thread", new { thread });
                    t = new Entry { Id = e.Position, Kind = "tool", OccurredAt = e.OccurredAt, ActorId = e.ActorId, BehalfId = e.OnBehalfOf, AgentId = e.ActorId, ChannelId = channel, ThreadId = thread,
                        Correlation = e.Correlation, Verb = "use", Detail = e.Str("title"), RunState = "running" };
                    NewTool(x, t, save: false);
                }
                t.AskPosition = e.Position; t.AskState = "pending";
                Save(x, t, tt => ComposeTool(x, tt));
                break;
            }
            case "PermissionGranted" or "PermissionDenied" when e.Causation is { } ask:
                if (Find(x, "SELECT * FROM audit_entries WHERE ask_position=@ask", new { ask }) is { } a)
                {
                    a.AskState = e.Type == "PermissionDenied" ? "denied" : (e.Str("optionId") ?? "").Contains("always", StringComparison.OrdinalIgnoreCase) ? "always" : "once";
                    a.AnsweredBy = e.ActorId; Save(x, a, t => ComposeTool(x, t));
                    Members(x, a.Id, new[] { e.ActorId });
                }
                break;
            case "TurnEnded" or "TurnFailed" or "TurnInterrupted" when e.Correlation is not null:
            {
                var ended = e.Type != "TurnFailed" || e.Bool("ended");
                if (Find(x, "SELECT * FROM audit_entries WHERE kind='turn' AND correlation=@c", new { c = e.Correlation }) is { } turn)
                {
                    turn.RunState = e.Type switch { "TurnEnded" => turn.RunState == "running" ? "finished" : turn.RunState, "TurnFailed" => "failed", _ => "interrupted" };
                    if (e.Type != "TurnEnded") turn.Reason = e.Str("reason");
                    Save(x, turn, t => ComposeTurn(x, t));
                }
                if (ended || e.Type == "TurnEnded")
                    foreach (var open in x.Query<Entry>("SELECT * FROM audit_entries WHERE kind='tool' AND correlation=@c AND run_state='running'", new { c = e.Correlation }).ToList())
                    { open.RunState = "unfinished"; Save(x, open, t => ComposeTool(x, t)); }
                break;
            }
        }
    }

    private static void NewTool(ProjectionContext x, Entry t, bool save = true)
    {
        x.Execute(@"INSERT OR REPLACE INTO audit_entries(id, kind, occurred_at, actor_id, behalf_id, agent_id, channel_id, thread_id, correlation, tool_id, verb, detail, run_state, sentence)
            VALUES (@Id,'tool',@OccurredAt,@ActorId,@BehalfId,@AgentId,@ChannelId,@ThreadId,@Correlation,@ToolId,@Verb,@Detail,@RunState,'')", t);
        Members(x, t.Id, new[] { t.ActorId, t.BehalfId, x.Scalar<long?>("SELECT owner_id FROM audit_names WHERE kind='agent' AND id=@id", new { id = t.AgentId }) });
        if (save) Save(x, t, tt => ComposeTool(x, tt));
    }
}
