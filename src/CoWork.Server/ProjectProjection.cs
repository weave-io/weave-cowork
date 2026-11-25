namespace CoWork.Server;

/// <summary>
/// The inline projection of the `project-{id}` streams: `projects`, `project_members` (lead or member), `workspaces` and `project_agents`
/// (with the grant "takes work from": its owner, or the project's members). A channel's project is on the channel's own stream
/// (`ChannelCreated.projectId`, `ChannelMovedToProject`), kept by <see cref="DirectoryProjection"/>.
/// </summary>
public sealed class ProjectProjection : IInlineProjection
{
    public void Apply(ProjectionContext x, StoredEvent e)
    {
        switch (e.Type)
        {
            case "ProjectCreated":
                x.Execute("INSERT INTO projects(id, name, about, created_by, created_at) VALUES (@id,@name,@about,@by,@at)",
                    new { id = e.Long("projectId"), name = e.Str("name"), about = e.Str("about") ?? "", by = e.ActorId ?? 0, at = e.OccurredAt });
                break;
            case "ProjectUpdated":
                if (e.Str("name") is { } name) x.Execute("UPDATE projects SET name=@name WHERE id=@id", new { name, id = e.Long("projectId") });
                if (e.Str("about") is { } about) x.Execute("UPDATE projects SET about=@about WHERE id=@id", new { about, id = e.Long("projectId") });
                break;
            case "ProjectMemberAdded":         // also how a role changes: the person is added again with the new one
                x.Execute("INSERT OR REPLACE INTO project_members(project_id, member_id, role) VALUES (@p,@m,@role)",
                    new { p = e.Long("projectId"), m = e.Long("memberId"), role = e.Str("role") ?? "member" });
                break;
            case "ProjectMemberRemoved":
                x.Execute("DELETE FROM project_members WHERE project_id=@p AND member_id=@m", new { p = e.Long("projectId"), m = e.Long("memberId") });
                break;
            case "WorkspaceAdded":
                x.Execute(@"INSERT INTO workspaces(id, project_id, name, kind, url, branch, path, host, added_by, created_at)
                            VALUES (@id,@p,@name,@kind,@url,@branch,@path,@host,@by,@at)",
                    new { id = e.Long("workspaceId"), p = e.Long("projectId"), name = e.Str("name") ?? "app", kind = e.Str("kind") ?? "none", url = e.Str("url"), branch = e.Str("branch"),
                          path = e.Str("path"), host = e.Str("host"), by = e.ActorId, at = e.OccurredAt });
                break;
            case "WorkspaceRemoved":
                x.Execute("DELETE FROM workspaces WHERE id=@id AND project_id=@p", new { id = e.Long("workspaceId"), p = e.Long("projectId") });
                break;
            case "AgentAddedToProject":
            case "AgentGrantsChanged":
                x.Execute(@"INSERT INTO project_agents(project_id, agent_id, takes_work_from, added_by) VALUES (@p,@a,@from,@by)
                            ON CONFLICT(project_id, agent_id) DO UPDATE SET takes_work_from=@from",
                    new { p = e.Long("projectId"), a = e.Long("agentId"), from = e.Str("takesWorkFrom") ?? "members", by = e.ActorId });
                break;
            case "AgentRemovedFromProject":
                x.Execute("DELETE FROM project_agents WHERE project_id=@p AND agent_id=@a", new { p = e.Long("projectId"), a = e.Long("agentId") });
                break;
        }
    }
}
