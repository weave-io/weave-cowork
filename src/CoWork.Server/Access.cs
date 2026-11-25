using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>The one place that says who may see and change what. Every API and hub call goes through it.</summary>
public static class Access
{
    public static bool IsAdmin(SqliteConnection c, long memberId) =>
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM people p JOIN members m ON m.id=p.member_id WHERE p.member_id=@memberId AND p.role='admin' AND m.removed=0", new { memberId }) > 0;

    public static bool IsActivePerson(SqliteConnection c, long memberId) =>
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM people p JOIN members m ON m.id=p.member_id WHERE p.member_id=@memberId AND m.removed=0", new { memberId }) > 0;

    public static bool IsChannelMember(SqliteConnection c, long channelId, long memberId) =>
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM channel_members WHERE channel_id=@channelId AND member_id=@memberId", new { channelId, memberId }) > 0;

    /// <summary>
    /// Who sees a channel, as one SQL condition on `ch` (a channel row) and `@m` (the person), so a decider can check it inside its write too.
    /// A person added to a channel sees it. A public channel outside any project: every active person. A public channel inside a project: the
    /// project's members, and admins. A private channel: only the people added.
    /// </summary>
    public const string SeesChannel = @"EXISTS (SELECT 1 FROM people pe JOIN members mm ON mm.id=pe.member_id WHERE pe.member_id=@m AND mm.removed=0)
        AND (ch.id IN (SELECT channel_id FROM channel_members WHERE member_id=@m)
             OR (ch.is_private=0 AND (ch.project_id IS NULL
                 OR ch.project_id IN (SELECT project_id FROM project_members WHERE member_id=@m)
                 OR EXISTS (SELECT 1 FROM people WHERE member_id=@m AND role='admin'))))";

    public static bool CanSee(SqliteConnection c, long channelId, long memberId) =>
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM channels ch WHERE ch.id=@ch AND " + SeesChannel, new { ch = channelId, m = memberId }) > 0;

    /// <summary>The channel's creator, an admin, and for a channel in a project, the project's leads.</summary>
    public static bool CanManage(SqliteConnection c, ChannelRow ch, long memberId) =>
        IsActivePerson(c, memberId) && (ch.CreatorId == memberId || IsAdmin(c, memberId) || (ch.ProjectId is { } p && IsLead(c, p, memberId)));

    public static List<long> VisibleChannelIds(SqliteConnection c, long memberId) =>
        c.Query<long>("SELECT ch.id FROM channels ch WHERE " + SeesChannel, new { m = memberId }).ToList();

    // ---- projects ----

    public static string? ProjectRole(SqliteConnection c, long projectId, long memberId) =>
        c.QueryFirstOrDefault<string?>("SELECT role FROM project_members WHERE project_id=@projectId AND member_id=@memberId", new { projectId, memberId });

    public static bool IsLead(SqliteConnection c, long projectId, long memberId) => ProjectRole(c, projectId, memberId) == "lead";

    /// <summary>A project's members see it (its settings, its public channels); so do admins, who look after the whole server.</summary>
    public static bool CanSeeProject(SqliteConnection c, long projectId, long memberId) =>
        IsActivePerson(c, memberId) && c.ExecuteScalar<int>("SELECT COUNT(*) FROM projects WHERE id=@projectId", new { projectId }) > 0
        && (ProjectRole(c, projectId, memberId) is not null || IsAdmin(c, memberId));

    /// <summary>Leads and admins change a project: its name, people, workspaces and agents.</summary>
    public static bool CanEditProject(SqliteConnection c, long projectId, long memberId) =>
        CanSeeProject(c, projectId, memberId) && (IsLead(c, projectId, memberId) || IsAdmin(c, memberId));
}
