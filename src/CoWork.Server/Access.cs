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

    /// <summary>Public channels: every active person. Private: only those added.</summary>
    public static bool CanSee(SqliteConnection c, long channelId, long memberId)
    {
        var ch = Q.Channel(c, channelId);
        if (ch is null || !IsActivePerson(c, memberId)) return false;
        return !ch.IsPrivate || IsChannelMember(c, channelId, memberId);
    }

    public static bool CanManage(SqliteConnection c, ChannelRow ch, long memberId) =>
        IsActivePerson(c, memberId) && (ch.CreatorId == memberId || IsAdmin(c, memberId));

    public static List<long> VisibleChannelIds(SqliteConnection c, long memberId)
    {
        if (!IsActivePerson(c, memberId)) return new();
        return c.Query<long>(@"SELECT id FROM channels WHERE is_private=0
            OR id IN (SELECT channel_id FROM channel_members WHERE member_id=@memberId)", new { memberId }).ToList();
    }
}
