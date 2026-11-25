using System.Text.Json;

namespace CoWork.Server;

/// <summary>
/// The inline projection of the server, channel, agent and person streams: `members`, `people`, `agents`, `invites`, `channels`,
/// `channel_members` and the server's name. Secrets (password, token and code hashes, invite codes) are not here: no event holds them.
/// </summary>
public sealed class DirectoryProjection : IInlineProjection
{
    public void Apply(ProjectionContext x, StoredEvent e)
    {
        switch (e.Type)
        {
            // ---- server ----
            case "ServerSetUp":
                x.Execute("INSERT OR REPLACE INTO settings VALUES ('server_name', @n)", new { n = e.Str("serverName") });
                break;
            case "PersonJoined":
                x.Execute("INSERT INTO members(id, kind, handle, display_name, created_at) VALUES (@id,'person',@h,@n,@at)",
                    new { id = e.Long("memberId"), h = e.Str("handle"), n = e.Str("displayName"), at = e.OccurredAt });
                x.Execute("INSERT INTO people(member_id, role) VALUES (@id,@role)", new { id = e.Long("memberId"), role = e.Str("role") });
                break;
            case "RoleChanged":
                x.Execute("UPDATE people SET role=@role WHERE member_id=@id", new { role = e.Str("role"), id = e.Long("memberId") });
                break;
            case "PersonRemoved":
                x.Execute("UPDATE members SET removed=1 WHERE id=@id", new { id = e.Long("memberId") });
                x.Execute("DELETE FROM channel_members WHERE member_id=@id", new { id = e.Long("memberId") });
                x.Execute("DELETE FROM project_members WHERE member_id=@id", new { id = e.Long("memberId") });
                break;
            case "InviteCreated":
                x.Execute("INSERT INTO invites(id, created_by, created_at, expires_at, max_uses) VALUES (@id,@by,@at,@exp,@max)",
                    new { id = e.Long("inviteId"), by = e.Long("createdBy"), at = e.OccurredAt, exp = e.Long("expiresAt"), max = e.Long("maxUses") });
                break;
            case "InviteUsed":
                x.Execute("UPDATE invites SET uses=uses+1 WHERE id=@id", new { id = e.Long("inviteId") });
                break;
            case "InviteRevoked":
                x.Execute("UPDATE invites SET revoked=1 WHERE id=@id", new { id = e.Long("inviteId") });
                break;

            // ---- channels ----
            case "ChannelCreated":
                x.Execute("INSERT INTO channels(id, name, topic, is_private, kind, creator_id, created_at, project_id) VALUES (@id,@name,@topic,@p,@kind,@by,@at,@project)",
                    new { id = e.Long("channelId"), name = e.Str("name"), topic = e.Str("topic") ?? "", p = e.Bool("isPrivate"), kind = e.Str("kind") ?? "text", by = e.Long("creatorId"), at = e.OccurredAt, project = e.LongOrNull("projectId") });
                break;
            case "ChannelMovedToProject":
                x.Execute("UPDATE channels SET project_id=@project WHERE id=@id", new { project = e.LongOrNull("projectId"), id = e.Long("channelId") });
                break;
            case "ChannelRenamed":
                x.Execute("UPDATE channels SET name=@name WHERE id=@id", new { name = e.Str("name"), id = e.Long("channelId") });
                break;
            case "TopicChanged":
                x.Execute("UPDATE channels SET topic=@t WHERE id=@id", new { t = e.Str("topic") ?? "", id = e.Long("channelId") });
                break;
            case "ChannelMadePrivate": case "ChannelMadePublic":
                x.Execute("UPDATE channels SET is_private=@p WHERE id=@id", new { p = e.Type == "ChannelMadePrivate", id = e.Long("channelId") });
                break;
            case "MemberAdded":
                x.Execute("INSERT OR IGNORE INTO channel_members VALUES (@ch,@m)", new { ch = e.Long("channelId"), m = e.Long("memberId") });
                break;
            case "MemberRemoved":
                x.Execute("DELETE FROM channel_members WHERE channel_id=@ch AND member_id=@m", new { ch = e.Long("channelId"), m = e.Long("memberId") });
                break;
            case "ChannelDeleted":
            {
                var id = e.Long("channelId");
                x.Execute("DELETE FROM open_turns WHERE thread_id IN (SELECT root_id FROM threads WHERE channel_id=@id)", new { id });
                x.Execute("DELETE FROM thread_agents WHERE thread_id IN (SELECT root_id FROM threads WHERE channel_id=@id)", new { id });
                x.Execute("DELETE FROM threads WHERE channel_id=@id", new { id });
                x.Execute("DELETE FROM posts WHERE channel_id=@id", new { id });
                x.Execute("DELETE FROM channel_members WHERE channel_id=@id", new { id });
                x.Execute("DELETE FROM channels WHERE id=@id", new { id });
                break;
            }

            // ---- agents and their runners ----
            case "AgentCreated":
                x.Execute("INSERT INTO members(id, kind, handle, display_name, created_at) VALUES (@id,'agent',@h,@n,@at)",
                    new { id = e.Long("agentId"), h = e.Str("handle"), n = e.Str("displayName"), at = e.OccurredAt });
                x.Execute(@"INSERT INTO agents(member_id, owner_id, about, instructions, harness_command, workspace, record_tool_output, placement, image, model)
                            VALUES (@id,@owner,@about,@ins,@cmd,@ws,@rec,@placement,@image,@model)",
                    new { id = e.Long("agentId"), owner = e.Long("ownerId"), about = e.Str("about") ?? "", ins = e.Str("instructions") ?? "", cmd = e.Data.GetProperty("harnessCommand").GetRawText(),
                          ws = e.Str("workspace") ?? "", rec = e.Bool("recordToolOutput") ? 1 : 0, placement = e.Str("placement") ?? "runner", image = e.Str("image"), model = e.Str("model") });
                break;
            case "AgentEdited":
            {
                var id = e.Long("agentId");
                x.Execute("UPDATE agents SET about=@about, instructions=@ins, harness_command=@cmd, workspace=@ws WHERE member_id=@id",
                    new { about = e.Str("about") ?? "", ins = e.Str("instructions") ?? "", cmd = e.Data.GetProperty("harnessCommand").GetRawText(), ws = e.Str("workspace") ?? "", id });
                if (e.Data.TryGetProperty("recordToolOutput", out var rec) && rec.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    x.Execute("UPDATE agents SET record_tool_output=@rec WHERE member_id=@id", new { rec = rec.GetBoolean() ? 1 : 0, id });
                if (!string.IsNullOrWhiteSpace(e.Str("displayName"))) x.Execute("UPDATE members SET display_name=@n WHERE id=@id", new { n = e.Str("displayName"), id });
                if (e.Str("image") is { } image) x.Execute("UPDATE agents SET image=@image WHERE member_id=@id", new { image, id });       // a template's image and model
                if (e.Str("model") is { } model) x.Execute("UPDATE agents SET model=@model WHERE member_id=@id", new { model, id });
                break;
            }
            case "EnrolmentCodeIssued":
                x.Execute("UPDATE agents SET code_expires=@exp WHERE member_id=@id", new { exp = e.Long("expiresAt"), id = e.Long("agentId") });
                break;
            case "RunnerEnrolled":
                x.Execute("UPDATE agents SET runner_host=@host, code_expires=NULL WHERE member_id=@id", new { host = e.Str("host"), id = e.Long("agentId") });
                break;
            case "RunnerConnected":
                x.Execute("UPDATE agents SET runner_host=COALESCE(@host, runner_host), last_seen=@at WHERE member_id=@id", new { host = e.Str("host"), at = e.OccurredAt, id = e.Long("agentId") });
                break;
            case "TokenRevoked":
                x.Execute("UPDATE agents SET runner_host=NULL WHERE member_id=@id", new { id = e.Long("agentId") });
                break;
            case "AgentRemoved":
            {
                var id = e.Long("agentId");
                x.Execute("UPDATE members SET removed=1 WHERE id=@id", new { id });
                x.Execute("UPDATE agents SET runner_host=NULL, code_expires=NULL WHERE member_id=@id", new { id });
                x.Execute("DELETE FROM channel_members WHERE member_id=@id", new { id });
                x.Execute("DELETE FROM project_agents WHERE agent_id=@id", new { id });
                x.Execute("DELETE FROM runner_positions WHERE agent_id=@id", new { id });
                break;
            }
        }
    }
}
