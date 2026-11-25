using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>
/// Threads as the channel list and "All threads" show them (docs/build/brief-8.md): a title, the agents in it, who started it, where its work
/// stands and when something last happened in it. Read from the thread projection at read time, like <see cref="ThreadStatus"/>, so it can't drift.
/// </summary>
public static class ThreadList
{
    /// <summary>How many threads a channel shows in the list before "All threads".</summary>
    public const int Shown = 5;
    /// <summary>A done thread leaves the list a day after it was resolved, unless it has replies the person hasn't read.</summary>
    public const long DoneShownMs = 24 * 60 * 60 * 1000;

    public sealed record Agent(long Id, string Handle, string DisplayName);
    public sealed record Summary(long RootId, long ChannelId, string Title, string TitleFrom, long StartedBy, IReadOnlyList<Agent> Agents,
        ThreadStatus.Status? Status, long LastAt, long CreatedAt, int Replies, long? ResolvedAt, int Unread);

    sealed class Row
    {
        public long RootId { get; set; } public long ChannelId { get; set; } public long StartedBy { get; set; }
        public string? Title { get; set; } public long? TitledAt { get; set; } public long? ResolvedPost { get; set; } public long? ResolvedAt { get; set; }
        public long? LastAt { get; set; } public string? Body { get; set; } public long CreatedAt { get; set; }
    }

    const string Select = @"SELECT t.root_id, t.channel_id, t.started_by, t.title, t.titled_at, t.resolved_post, t.resolved_at, t.last_at, p.body, p.created_at
        FROM threads t JOIN posts p ON p.id = t.root_id AND p.deleted = 0";

    static readonly Regex MentionRx = new(@"(?<![\w@])@[a-zA-Z0-9][a-zA-Z0-9_-]*", RegexOptions.Compiled);
    static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The first words of the opening post, without its @mentions: at most 8 words and about 60 characters, with "…" when cut.</summary>
    public static string AutoTitle(string? body)
    {
        var text = Space.Replace(MentionRx.Replace(body ?? "", " "), " ").Trim().TrimStart(',', ':', ';', '.', '-', '–', '—', ' ');
        if (text.Length == 0) return "Untitled thread";
        var words = text.Split(' ');
        var title = "";
        var cut = false;
        foreach (var (w, i) in words.Select((w, i) => (w, i)))
        {
            if (i == 8 || (title.Length > 0 && title.Length + 1 + w.Length > 60)) { cut = true; break; }
            title = title.Length == 0 ? w : title + " " + w;
        }
        if (title.Length > 60) { title = title[..59]; cut = true; }
        title = title.TrimEnd(',', ':', ';', '-', '–', '—');
        if (cut) title = title.TrimEnd('.', '?', '!') + "…";
        return char.ToUpperInvariant(title[0]) + title[1..];
    }

    /// <summary>The title: the name someone gave it; until anyone does, the title of the outcome an agent resolved it with; until then, the first
    /// words of its opening post. Returns the title and where it came from (renamed, outcome, words).</summary>
    static (string Title, string From) TitleOf(SqliteConnection c, Row r) =>
        r.Title is { Length: > 0 } title ? (title, r.TitledAt is null ? "outcome" : "renamed") : (AutoTitle(r.Body), "words");

    /// <summary>A thread's title, for where only the title is shown (the post's "N replies" chip, search), or null if there's no such thread.</summary>
    public static string? TitleFor(SqliteConnection c, long rootId) =>
        c.QueryFirstOrDefault<Row>(Select + " WHERE t.root_id=@rootId", new { rootId }) is { } r ? TitleOf(c, r).Title : null;

    static Summary Build(SqliteConnection c, Row r, int unread)
    {
        var (title, from) = TitleOf(c, r);
        var agents = c.Query<Agent>("SELECT m.id, m.handle, m.display_name FROM thread_agents ta JOIN members m ON m.id = ta.agent_id WHERE ta.thread_id=@t ORDER BY m.handle", new { t = r.RootId }).ToList();
        var replies = c.ExecuteScalar<int>("SELECT COUNT(*) FROM posts WHERE thread_id=@t AND kind='text' AND deleted=0", new { t = r.RootId });
        return new Summary(r.RootId, r.ChannelId, title, from, r.StartedBy, agents, ThreadStatus.Of(c, r.RootId), r.LastAt ?? r.CreatedAt, r.CreatedAt, replies, r.ResolvedAt, unread);
    }

    /// <summary>One thread, as everyone sees it (no unread count: that is each person's own).</summary>
    public static Summary? Of(SqliteConnection c, long rootId) =>
        c.QueryFirstOrDefault<Row>(Select + " WHERE t.root_id=@rootId", new { rootId }) is { } r ? Build(c, r, 0) : null;

    /// <summary>Replies in threads by someone else that this person hasn't read: after what they read of the thread (opening it, or writing in it),
    /// when they were added to its channel, when they joined, and when the unread view began. Per thread.</summary>
    public static Dictionary<long, int> Unread(SqliteConnection c, long me, long? threadId = null) =>
        c.Query<(long Thread, int N)>(@"SELECT r.thread_id, COUNT(*) FROM unread_replies r
            WHERE r.author_id<>@me" + (threadId is null ? "" : " AND r.thread_id=@threadId") + @" AND r.position > MAX(
              COALESCE((SELECT position FROM unread_thread_markers WHERE person_id=@me AND thread_id=r.thread_id), 0),
              COALESCE((SELECT position FROM unread_joined WHERE person_id=@me AND channel_id=r.channel_id), 0),
              COALESCE((SELECT position FROM unread_markers WHERE person_id=@me AND channel_id=0), 0),
              COALESCE((SELECT value FROM unread_settings WHERE key='baseline'), 0))
            GROUP BY r.thread_id", new { me, threadId }).ToDictionary(x => x.Thread, x => x.N);

    /// <summary>Whether a thread belongs in the channel list: open, or done less than a day ago, or with replies the person hasn't read.</summary>
    public static bool Listed(Summary s, long now) =>
        s.Status?.State != "done" || now - (s.ResolvedAt ?? s.LastAt) < DoneShownMs || s.Unread > 0;

    public sealed record ChannelThreads(long ChannelId, int Total, IReadOnlyList<Summary> Threads);

    /// <summary>The channel list's threads for a person: for each channel they see, the most recently active threads that belong in the list (at
    /// most <see cref="Shown"/>), and how many threads the channel has in all.</summary>
    public static List<ChannelThreads> Sidebar(SqliteConnection c, long me, long now)
    {
        var visible = Access.VisibleChannelIds(c, me).ToHashSet();
        var unread = Unread(c, me);
        var rows = c.Query<Row>(Select + " ORDER BY COALESCE(t.last_position, p.position) DESC, t.root_id DESC").Where(r => visible.Contains(r.ChannelId)).ToList();
        var result = new List<ChannelThreads>();
        foreach (var g in rows.GroupBy(r => r.ChannelId))
        {
            var shown = new List<Summary>();
            foreach (var r in g)
            {
                if (shown.Count == Shown) break;
                var n = unread.GetValueOrDefault(r.RootId);
                // a done thread from more than a day ago with nothing unread: skip it without working out the rest
                if (r.ResolvedPost is not null && n == 0 && now - (r.ResolvedAt ?? 0) >= DoneShownMs && ThreadStatus.Of(c, r.RootId)?.State == "done") continue;
                var s = Build(c, r, n);
                if (Listed(s, now)) shown.Add(s);
            }
            result.Add(new ChannelThreads(g.Key, g.Count(), shown));
        }
        return result;
    }

    /// <summary>Every thread in a channel, most recent activity first, for "All threads": open (not done), waiting (for this person), done, or all.</summary>
    public static List<Summary> InChannel(SqliteConnection c, long channelId, long me, string? filter)
    {
        var unread = Unread(c, me);
        var all = c.Query<Row>(Select + " WHERE t.channel_id=@channelId ORDER BY COALESCE(t.last_position, p.position) DESC, t.root_id DESC", new { channelId })
            .Select(r => Build(c, r, unread.GetValueOrDefault(r.RootId)));
        return (filter switch
        {
            "open" => all.Where(s => s.Status?.State != "done"),
            "waiting" => all.Where(s => s.Status is { State: "waiting" } st && st.WaitingForId == me),
            "done" => all.Where(s => s.Status?.State == "done"),
            _ => all,
        }).ToList();
    }

    /// <summary>Who may rename a thread: anyone who may post in it, that is, a person who sees its channel.</summary>
    public static bool CanRename(SqliteConnection c, long rootId, long me)
    {
        var channel = c.ExecuteScalar<long?>("SELECT channel_id FROM threads WHERE root_id=@rootId", new { rootId });
        return channel is not null && Access.IsActivePerson(c, me) && Access.CanSee(c, channel.Value, me);
    }
}
