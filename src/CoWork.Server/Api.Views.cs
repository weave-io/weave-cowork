using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Routing;

namespace CoWork.Server;

/// <summary>What the async projections answer: audit (admins), unread counts, the mentions inbox, an agent's activity, and the admin's rebuild.</summary>
public static partial class Api
{
    static void MapViews(RouteGroupBuilder authed)
    {
        // ---- audit: admins only ----
        authed.MapGet("/audit", (HttpContext ctx, Db db, long? person, long? agent, long? channel, long? before, int? limit) =>
        {
            using var c = db.Open();
            if (!Access.IsAdmin(c, Me(ctx))) return Err(403, "Only admins can see the audit.");
            var n = Math.Clamp(limit ?? 50, 1, 200);
            var rows = c.Query<AuditRow>(@"SELECT id, kind, occurred_at, sentence, channel_id, thread_id FROM audit_entries e
                WHERE (@person IS NULL OR e.id IN (SELECT entry_id FROM audit_members WHERE member_id=@person))
                  AND (@agent IS NULL OR e.id IN (SELECT entry_id FROM audit_members WHERE member_id=@agent))
                  AND (@channel IS NULL OR e.channel_id=@channel) AND (@before IS NULL OR e.id<@before)
                ORDER BY e.id DESC LIMIT @n", new { person, agent, channel, before, n = n + 1 }).ToList();
            var more = rows.Count > n;
            var names = c.Query<(string Kind, long Id, string Name)>("SELECT kind, id, name FROM audit_names ORDER BY name").ToList();
            return Results.Ok(new
            {
                entries = rows.Take(n).Select(r => new { id = r.Id, kind = r.Kind, at = r.OccurredAt, sentence = r.Sentence, channelId = r.ChannelId, threadId = r.ThreadId }),
                hasMore = more,
                people = names.Where(x => x.Kind == "person").Select(x => new { id = x.Id, name = x.Name }),
                agents = names.Where(x => x.Kind == "agent").Select(x => new { id = x.Id, name = x.Name }),
                channels = names.Where(x => x.Kind == "channel").Select(x => new { id = x.Id, name = x.Name }),
            });
        });

        // ---- the projections themselves (admin): where each one is, and a rebuild from zero ----
        authed.MapGet("/admin/projections", (HttpContext ctx, Db db, ProjectionRunner runner) =>
        {
            using (var c = db.Open()) if (!Access.IsAdmin(c, Me(ctx))) return Err(403, "Only admins can do that.");
            return Results.Ok(runner.Status().Select(s => new { name = s.Name, title = s.Title, position = s.Position, head = s.Head, rebuiltAt = s.RebuiltAt }));
        });
        authed.MapPost("/admin/projections/{name}/rebuild", async (string name, HttpContext ctx, Db db, ProjectionRunner runner) =>
        {
            using (var c = db.Open()) if (!Access.IsAdmin(c, Me(ctx))) return Err(403, "Only admins can do that.");
            if (!runner.Projections.Any(p => p.Name == name)) return Err(404, "No such projection.");
            var replayed = await runner.RebuildAsync(name);
            return Results.Ok(new { ok = true, replayed });
        });

        // ---- unread and mentions: each person's own ----
        authed.MapGet("/unread", async (HttpContext ctx, Db db, ProjectionRunner runner) =>
        {
            var me = Me(ctx);
            await runner.CatchUpAsync();          // read your own writes: a person who has just joined or read a channel sees it in the counts
            using var c = db.Open();
            return Results.Ok(UnreadFor(c, me));
        });
        authed.MapGet("/mentions", async (HttpContext ctx, Db db, ProjectionRunner runner) =>
        {
            var me = Me(ctx);
            await runner.CatchUpAsync();
            using var c = db.Open();
            var visible = Access.VisibleChannelIds(c, me).ToHashSet();
            var rows = c.Query<MentionRow>(@"SELECT m.position, m.post_id, m.channel_id, m.thread_id, m.author_id, m.excerpt, m.occurred_at,
                    (m.position > MAX(COALESCE((SELECT position FROM unread_markers WHERE person_id=@me AND channel_id=m.channel_id), 0),
                        COALESCE((SELECT position FROM unread_markers WHERE person_id=@me AND channel_id=0), 0),
                        COALESCE((SELECT value FROM unread_settings WHERE key='baseline'), 0))) AS unread
                FROM unread_mentions m WHERE m.person_id=@me ORDER BY m.position DESC LIMIT 200", new { me }).Where(m => visible.Contains(m.ChannelId)).Take(50).ToList();
            return Results.Ok(rows.Select(m => new
            {
                position = m.Position, postId = m.PostId, channelId = m.ChannelId, channelName = Q.Channel(c, m.ChannelId)?.Name, threadId = m.ThreadId,
                authorId = m.AuthorId, excerpt = m.Excerpt, at = m.OccurredAt, unread = m.Unread,
            }));
        });

        // a person has read a channel: up to its latest message. Read markers only move forward, and an event is written only when something is new.
        authed.MapPost("/channels/{id:long}/read", async (long id, HttpContext ctx, Db db, IEventStore store, ProjectionRunner runner) =>
        {
            var me = Me(ctx);
            using (var c = db.Open()) if (!Access.CanSee(c, id, me)) return Err(404, "No such channel.");
            long latest = 0;
            store.Append(d =>
            {
                latest = d.Scalar<long?>("SELECT MAX(position) FROM posts WHERE channel_id=@id AND kind='text' AND deleted=0", new { id }) ?? 0;
                var marker = d.Scalar<long?>("SELECT MAX(json_extract(data,'$.position')) FROM events WHERE stream_id=@s AND type='ChannelRead' AND json_extract(data,'$.channelId')=@id", new { s = $"person-{me}", id }) ?? 0;
                return latest > marker ? new[] { new NewEvent($"person-{me}", "ChannelRead", new { channelId = id, position = latest }, me) } : Array.Empty<NewEvent>();
            });
            await runner.CatchUpAsync();          // the counts a browser asks for next include this
            return Results.Ok(new { position = latest });
        });

        // ---- search: what the caller may see, filters typed into the box (docs/build/brief-6.md) ----
        authed.MapGet("/search", async (HttpContext ctx, Db db, ProjectionRunner runner, string? q, int? limit, int? offset) =>
        {
            var me = Me(ctx);
            await runner.CatchUpAsync();          // read your own writes: what you just wrote is found
            using var c = db.Open();
            if (!Access.IsActivePerson(c, me)) return Err(403, "Only people can search.");
            return Results.Ok(Search.Run(c, me, SearchQuery.Parse(q), Math.Clamp(limit ?? 20, 1, 50), Math.Max(0, offset ?? 0)));
        });

        // ---- an agent's activity (on its profile) ----
        authed.MapGet("/agents/{id:long}/activity", (long id, HttpContext ctx, Db db, RunnerRegistry runners) =>
        {
            var me = Me(ctx);
            using var c = db.Open();
            var agent = c.QueryFirstOrDefault<MemberRow>(Q.MemberSelect + " WHERE m.id=@id AND m.kind='agent'", new { id });
            if (agent is null) return Err(404, "No such agent.");
            var visible = Access.VisibleChannelIds(c, me).ToHashSet();
            object Turn(TurnRow t)
            {
                var seen = t.ChannelId is { } ch && visible.Contains(ch);        // a thread in a channel the caller can't see is only "a private thread"
                return new
                {
                    turn = t.Turn, threadId = seen ? t.ThreadId : (long?)null, channelId = seen ? t.ChannelId : null,
                    channelName = seen ? Q.Channel(c, t.ChannelId!.Value)?.Name : null,
                    thread = seen ? c.ExecuteScalar<string>("SELECT excerpt FROM activity_roots WHERE post_id=@t", new { t = t.ThreadId }) : null,
                    forId = t.BehalfId, startedAt = t.StartedAt, endedAt = t.EndedAt, outcome = t.Outcome, reason = t.Reason, toolCalls = t.ToolCalls, host = t.Host,
                };
            }
            var turns = c.Query<TurnRow>("SELECT * FROM activity_turns WHERE agent_id=@id ORDER BY started_position DESC LIMIT 15", new { id }).ToList();
            var busy = c.Query<TurnRow>("SELECT * FROM activity_turns WHERE agent_id=@id AND outcome='running' ORDER BY started_position DESC", new { id }).ToList();
            var failures = c.Query<TurnRow>("SELECT * FROM activity_turns WHERE agent_id=@id AND outcome IN ('failed','interrupted') ORDER BY started_position DESC LIMIT 10", new { id }).ToList();
            var totals = c.QueryFirst<(long Turns, long Failed, long Tools)>("SELECT COUNT(*), COALESCE(SUM(outcome IN ('failed','interrupted')),0), COALESCE(SUM(tool_calls),0) FROM activity_turns WHERE agent_id=@id", new { id });
            var conns = c.Query<(string? Host, long ConnectedAt, long? DisconnectedAt)>("SELECT host, connected_at, disconnected_at FROM activity_connections WHERE agent_id=@id ORDER BY id DESC LIMIT 5", new { id }).ToList();
            return Results.Ok(new
            {
                agent = new { id = agent.Id, handle = agent.Handle, displayName = agent.DisplayName, ownerId = agent.OwnerId, online = runners.IsOnline(agent.Id), host = agent.RunnerHost },
                busy = busy.Select(Turn), turns = turns.Select(Turn), failures = failures.Select(Turn),
                totals = new { turns = totals.Turns, failed = totals.Failed, toolCalls = totals.Tools },
                connections = conns.Select(k => new { host = k.Host, connectedAt = k.ConnectedAt, disconnectedAt = k.DisconnectedAt }),
            });
        });
    }

    /// <summary>Unread messages per channel the person may see, and how many mentions they haven't seen.</summary>
    public static object UnreadFor(Microsoft.Data.Sqlite.SqliteConnection c, long me)
    {
        var visible = Access.VisibleChannelIds(c, me);
        var counts = c.Query<(long ChannelId, long N)>($@"SELECT X.channel_id, COUNT(*) FROM unread_activity X
            WHERE X.author_id<>@me AND X.position > {UnreadProjection.Effective} GROUP BY X.channel_id", new { me })
            .Where(r => visible.Contains(r.ChannelId)).ToDictionary(r => r.ChannelId.ToString(), r => r.N);
        var mentions = c.Query<(long ChannelId, long N)>($@"SELECT X.channel_id, COUNT(*) FROM unread_mentions X
            WHERE X.person_id=@me AND X.position > {UnreadProjection.Effective} GROUP BY X.channel_id", new { me })
            .Where(r => visible.Contains(r.ChannelId)).Sum(r => r.N);
        return new { channels = counts, mentions };
    }

    sealed class AuditRow { public long Id { get; set; } public string Kind { get; set; } = ""; public long OccurredAt { get; set; } public string Sentence { get; set; } = ""; public long? ChannelId { get; set; } public long? ThreadId { get; set; } }
    sealed class MentionRow
    {
        public long Position { get; set; } public long PostId { get; set; } public long ChannelId { get; set; } public long? ThreadId { get; set; } public long AuthorId { get; set; }
        public string Excerpt { get; set; } = ""; public long OccurredAt { get; set; } public bool Unread { get; set; }
    }
    sealed class TurnRow
    {
        public string Turn { get; set; } = ""; public long AgentId { get; set; } public long ThreadId { get; set; } public long? ChannelId { get; set; } public long? BehalfId { get; set; }
        public long StartedPosition { get; set; } public long StartedAt { get; set; } public long? EndedAt { get; set; } public string Outcome { get; set; } = ""; public string? Reason { get; set; }
        public long ToolCalls { get; set; } public string? Host { get; set; }
    }
}
