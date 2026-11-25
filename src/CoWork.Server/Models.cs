using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

public class MemberRow
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string Handle { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long CreatedAt { get; set; }
    public bool Removed { get; set; }
    public string? Role { get; set; }          // people only
    public long? OwnerId { get; set; }         // agents only
    public string? About { get; set; }
    public string? Instructions { get; set; }
    public string? HarnessCommand { get; set; }
    public string? Workspace { get; set; }
    public string? RunnerHost { get; set; }
    public bool HasToken { get; set; }
}

public class ChannelRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Topic { get; set; } = "";
    public bool IsPrivate { get; set; }
    public string Kind { get; set; } = "text";
    public long CreatorId { get; set; }
    public long CreatedAt { get; set; }
    public long? ProjectId { get; set; }
}

public class PostRow
{
    public long Id { get; set; }
    public long ChannelId { get; set; }
    public long? ThreadId { get; set; }
    public long AuthorId { get; set; }
    public string Body { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string? Meta { get; set; }
    public bool Streaming { get; set; }
    public long CreatedAt { get; set; }
    public long Position { get; set; }
    public int ReplyCount { get; set; }
    public bool HasThread { get; set; }
}

public static class Q
{
    public const string MemberSelect = @"
        SELECT m.id, m.kind, m.handle, m.display_name, m.created_at, m.removed,
               p.role AS role, a.owner_id, a.about, a.instructions, a.harness_command, a.workspace, a.runner_host,
               (rs.token_hash IS NOT NULL) AS has_token
        FROM members m LEFT JOIN people p ON p.member_id = m.id LEFT JOIN agents a ON a.member_id = m.id LEFT JOIN runner_secrets rs ON rs.agent_id = m.id";

    public static MemberRow? Member(SqliteConnection c, long id) =>
        c.QueryFirstOrDefault<MemberRow>(MemberSelect + " WHERE m.id=@id", new { id });

    public static ChannelRow? Channel(SqliteConnection c, long id) =>
        c.QueryFirstOrDefault<ChannelRow>("SELECT * FROM channels WHERE id=@id", new { id });

    public const string PostSelect = @"
        SELECT p.*, (SELECT COUNT(*) FROM posts r WHERE r.thread_id = p.id AND r.kind = 'text' AND r.deleted = 0) AS reply_count,
               (SELECT COUNT(*) FROM threads t WHERE t.root_id = p.id) AS has_thread
        FROM (SELECT * FROM posts WHERE deleted = 0) p";

    public static PostRow? Post(SqliteConnection c, long id) =>
        c.QueryFirstOrDefault<PostRow>(PostSelect + " WHERE p.id=@id", new { id });

    /// <summary>The stream a post's events are in: a top-level post in its channel's, a reply in its thread's.</summary>
    public static string StreamOf(PostRow p) => p.ThreadId is { } t ? $"thread-{t}" : $"channel-{p.ChannelId}";

    public static object PostDto(PostRow p, IReadOnlyList<string>? threadAgents = null) => new
    {
        id = p.Id, channelId = p.ChannelId, threadId = p.ThreadId, authorId = p.AuthorId, body = p.Body, kind = p.Kind,
        meta = string.IsNullOrEmpty(p.Meta) ? null : JsonNode.Parse(p.Meta), streaming = p.Streaming, createdAt = p.CreatedAt, position = p.Position,
        replyCount = p.ReplyCount, hasThread = p.HasThread || p.ReplyCount > 0,
        threadAgents,
    };

    public static List<string> ThreadAgentHandles(SqliteConnection c, long threadId) =>
        c.Query<string>(@"SELECT m.handle FROM thread_agents ta JOIN members m ON m.id = ta.agent_id WHERE ta.thread_id=@threadId ORDER BY m.handle",
            new { threadId }).ToList();

    public static object FullPostDto(SqliteConnection c, long id)
    {
        var p = Post(c, id)!;
        return PostDto(p, p.ThreadId is null ? ThreadAgentHandles(c, p.Id) : null);
    }

    public static object MemberDto(MemberRow m, bool online) => new
    {
        id = m.Id, kind = m.Kind, handle = m.Handle, displayName = m.DisplayName, removed = m.Removed,
        role = m.Role, ownerId = m.OwnerId, about = m.About, online,
        harnessCommand = m.HarnessCommand is null ? null : JsonSerializer.Deserialize<string[]>(m.HarnessCommand),
        workspace = m.Workspace, runnerHost = m.RunnerHost, connected = m.HasToken,
    };
}
