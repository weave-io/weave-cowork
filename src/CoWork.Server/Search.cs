using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>What a person typed into the search box: the words, and the filters taken out of it.</summary>
public sealed record SearchQuery(IReadOnlyList<string> Terms, IReadOnlyList<string> In, IReadOnlyList<string> From, bool ThreadsOnly)
{
    public bool IsEmpty => Terms.Count == 0 && In.Count == 0 && From.Count == 0 && !ThreadsOnly;

    /// <summary>`refund rounding in:#checkout-app from:@reviewer is:thread "half even"`. Unknown `x:y` words are searched for as words.</summary>
    public static SearchQuery Parse(string? q)
    {
        var terms = new List<string>(); var ins = new List<string>(); var froms = new List<string>(); var threads = false;
        foreach (var raw in Tokens(q ?? ""))
        {
            var t = raw;
            if (t.StartsWith("in:", StringComparison.OrdinalIgnoreCase) && t.Length > 3) { ins.Add(t[3..].TrimStart('#')); continue; }
            if (t.StartsWith("from:", StringComparison.OrdinalIgnoreCase) && t.Length > 5) { froms.Add(t[5..].TrimStart('@')); continue; }
            if (t.Equals("is:thread", StringComparison.OrdinalIgnoreCase)) { threads = true; continue; }
            if (t.Any(char.IsLetterOrDigit)) terms.Add(t);
        }
        return new SearchQuery(terms, ins.Where(s => s.Length > 0).ToList(), froms.Where(s => s.Length > 0).ToList(), threads);
    }

    /// <summary>Words split on spaces; "a quoted phrase" stays together.</summary>
    private static IEnumerable<string> Tokens(string q)
    {
        var sb = new StringBuilder(); var quoted = false;
        foreach (var ch in q)
        {
            if (ch == '"') { quoted = !quoted; if (!quoted && sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } continue; }
            if (char.IsWhiteSpace(ch) && !quoted) { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>The FTS5 match: every word must be there (AND), each one quoted so nothing typed is FTS syntax; the last word is a prefix,
    /// so results arrive while the word is still being typed. The tokenizer stems both sides, so "rounds" matches "rounding".</summary>
    public string? Match()
    {
        if (Terms.Count == 0) return null;
        return string.Join(' ', Terms.Select((t, i) => "\"" + t.Replace("\"", "\"\"") + "\"" + (i == Terms.Count - 1 ? "*" : "")));
    }
}

/// <summary>
/// The search query over <see cref="SearchProjection"/>'s index. Visibility is part of the SQL (public channels, or private ones the caller is in),
/// so the counts and the pages are of what the caller may see. Threads are grouped from their matching messages.
/// </summary>
public static class Search
{
    public const char HitStart = '\u0002', HitEnd = '\u0003';         // snippet() marks matches with these; the browser turns them into <mark>

    sealed class Hit
    {
        public long DocId { get; set; } public string Kind { get; set; } = ""; public long PostId { get; set; } public long ChannelId { get; set; } public long? ThreadId { get; set; }
        public long? AuthorId { get; set; } public long OccurredAt { get; set; } public string Snippet { get; set; } = ""; public double Rank { get; set; }
    }

    /// <summary>What a person sees: <see cref="Access.SeesChannel"/>. An agent's boundary: the channels it takes part in (cowork_search).</summary>
    public const string AgentSees = "ch.id IN (SELECT channel_id FROM channel_audience WHERE member_id=@m)";

    public static object Run(SqliteConnection c, long me, SearchQuery q, int limit, int offset, string? visibility = null)
    {
        if (q.IsEmpty) return new { empty = true, outcomes = new { total = 0, items = Array.Empty<object>() }, threads = new { total = 0, items = Array.Empty<object>() }, messages = new { total = 0, items = Array.Empty<object>() } };
        var match = q.Match();
        // the documents that match AND that the caller may see, with a rank (bm25: lower is better; no words: newest first) and a snippet
        var hits = match is null
            ? @"SELECT d.id AS doc_id, d.kind, d.ref_id AS post_id, d.channel_id, d.thread_id, d.author_id, d.occurred_at,
                   substr(f.body, 1, 240) || CASE WHEN length(f.body) > 240 THEN '…' ELSE '' END AS snippet, -d.position AS rank
               FROM search_docs d JOIN search_fts f ON f.rowid = d.id"
            : $@"SELECT d.id AS doc_id, d.kind, d.ref_id AS post_id, d.channel_id, d.thread_id, d.author_id, d.occurred_at,
                   snippet(search_fts, -1, char(2), char(3), '…', 32) AS snippet, bm25(search_fts, 4.0, 1.0) AS rank
               FROM search_fts JOIN search_docs d ON d.id = search_fts.rowid";
        var where = (match is null ? " WHERE " : " WHERE search_fts MATCH @match AND ") + @"d.gone = 0
              AND d.channel_id IN (SELECT ch.id FROM channels ch WHERE " + (visibility ?? Access.SeesChannel) + ")"     // who sees a channel: one rule (Access), projects included
            + (q.In.Count > 0 ? " AND d.channel_id IN (SELECT id FROM channels WHERE name IN @ins)" : "")
            + (q.From.Count > 0 ? " AND d.author_id IN (SELECT id FROM members WHERE handle IN @froms)" : "");
        var sql = hits + where + " AND d.kind='message'";
        // outcomes: a resolved thread's title and summary, first; the title is weighted 4x
        var outcomeSql = (match is null
            ? @"SELECT d.ref_id AS post_id, d.channel_id, d.thread_id, d.author_id, d.occurred_at, f.title AS title, substr(f.body, 1, 240) AS snippet, -d.position AS rank
               FROM search_docs d JOIN search_fts f ON f.rowid = d.id"
            : @"SELECT d.ref_id AS post_id, d.channel_id, d.thread_id, d.author_id, d.occurred_at, highlight(search_fts, 0, char(2), char(3)) AS title,
                   snippet(search_fts, 1, char(2), char(3), '…', 32) AS snippet, bm25(search_fts, 4.0, 1.0) AS rank
               FROM search_fts JOIN search_docs d ON d.id = search_fts.rowid") + where + " AND d.kind='outcome'";
        var args = new DynamicParameters(new { me, m = me, match, ins = q.In, froms = q.From, limit, offset });

        // threads: grouped from their matching messages (the opening post or any reply), best match first; a thread whose opening post is gone isn't listed
        var threadSql = $@"WITH h AS MATERIALIZED ({sql}), g AS (SELECT thread_id, MIN(rank) AS rank, COUNT(*) AS matches, post_id AS best_post, snippet AS best_snippet, author_id AS best_author
                FROM h WHERE thread_id IS NOT NULL GROUP BY thread_id)
            SELECT g.thread_id AS root_id, g.rank, g.matches, g.best_post, g.best_snippet, g.best_author, p.channel_id, p.author_id, p.body, p.created_at,
                (SELECT COUNT(*) FROM posts r WHERE r.thread_id = p.id AND r.kind = 'text' AND r.deleted = 0) AS reply_count
            FROM g JOIN posts p ON p.id = g.thread_id AND p.deleted = 0";
        var outcomeTotal = q.ThreadsOnly ? 0 : c.ExecuteScalar<int>($"SELECT COUNT(*) FROM ({outcomeSql})", args);
        var outcomes = q.ThreadsOnly ? new List<OutcomeHit>() : c.Query<OutcomeHit>(outcomeSql + " ORDER BY rank, post_id DESC LIMIT @limit", args).ToList();
        var threadTotal = c.ExecuteScalar<int>($"SELECT COUNT(*) FROM ({threadSql})", args);
        var threads = c.Query<ThreadHit>(threadSql + " ORDER BY g.rank, g.thread_id DESC LIMIT @limit", args).ToList();

        // messages: posts and thread replies (an opening post is shown as its thread)
        var msgWhere = " WHERE thread_id IS NULL OR thread_id <> post_id";
        var messageTotal = q.ThreadsOnly ? 0 : c.ExecuteScalar<int>($"WITH h AS MATERIALIZED ({sql}) SELECT COUNT(*) FROM h" + msgWhere, args);
        var messages = q.ThreadsOnly ? new List<Hit>() : c.Query<Hit>($"WITH h AS MATERIALIZED ({sql}) SELECT * FROM h" + msgWhere + " ORDER BY rank, post_id DESC LIMIT @limit OFFSET @offset", args).ToList();

        var channelNames = new Dictionary<long, string?>();
        string? Channel(long id) => channelNames.TryGetValue(id, out var n) ? n : channelNames[id] = Q.Channel(c, id)?.Name;
        var rootBodies = new Dictionary<long, string?>();
        string? Root(long id) => rootBodies.TryGetValue(id, out var b) ? b : rootBodies[id] = c.ExecuteScalar<string?>("SELECT body FROM posts WHERE id=@id AND deleted=0", new { id });

        return new
        {
            empty = false,
            query = new { terms = q.Terms, @in = q.In, from = q.From, isThread = q.ThreadsOnly },
            outcomes = new
            {
                total = outcomeTotal,
                items = outcomes.Select(o => new
                {
                    postId = o.PostId, threadId = o.ThreadId, channelId = o.ChannelId, channelName = Channel(o.ChannelId), authorId = o.AuthorId, at = o.OccurredAt,
                    title = o.Title, snippet = o.Snippet,
                    askedBy = c.ExecuteScalar<long?>("SELECT started_by FROM threads WHERE root_id=@t", new { t = o.ThreadId }),
                    done = c.ExecuteScalar<long?>("SELECT resolved_post FROM threads WHERE root_id=@t", new { t = o.ThreadId }) == o.PostId,
                }),
            },
            threads = new
            {
                total = threadTotal,
                items = threads.Select(t => new
                {
                    rootId = t.RootId, channelId = t.ChannelId, channelName = Channel(t.ChannelId), authorId = t.AuthorId, at = t.CreatedAt,
                    title = t.BestPost == t.RootId ? t.BestSnippet : Excerpt(t.Body), replyCount = t.ReplyCount, matches = t.Matches,
                    agents = Q.ThreadAgentHandles(c, t.RootId),
                    match = t.BestPost == t.RootId ? null : new { postId = t.BestPost, authorId = t.BestAuthor, snippet = t.BestSnippet },
                }),
            },
            messages = new
            {
                total = messageTotal,
                items = messages.Select(m => new
                {
                    postId = m.PostId, channelId = m.ChannelId, channelName = Channel(m.ChannelId), threadId = m.ThreadId, authorId = m.AuthorId, at = m.OccurredAt,
                    snippet = m.Snippet, threadTitle = m.ThreadId is { } t ? Excerpt(Root(t) ?? "", 60) : null,
                }),
            },
        };
    }

    private static string Excerpt(string text, int max = 140)
    {
        var s = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }

    sealed class OutcomeHit
    {
        public long PostId { get; set; } public long ChannelId { get; set; } public long ThreadId { get; set; } public long? AuthorId { get; set; } public long OccurredAt { get; set; }
        public string Title { get; set; } = ""; public string Snippet { get; set; } = ""; public double Rank { get; set; }
    }

    sealed class ThreadHit
    {
        public long RootId { get; set; } public double Rank { get; set; } public int Matches { get; set; } public long BestPost { get; set; } public string BestSnippet { get; set; } = "";
        public long? BestAuthor { get; set; } public long ChannelId { get; set; } public long AuthorId { get; set; } public string Body { get; set; } = ""; public long CreatedAt { get; set; }
        public int ReplyCount { get; set; }
    }
}
