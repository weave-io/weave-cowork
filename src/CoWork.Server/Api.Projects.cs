using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

public record WorkspaceBody(string? Name, string? Kind, string? Url, string? Branch, string? Path, string? Host);
public record ProjectChannelBody(string? Name, string? Kind);
public record ProjectBody(string? Name, string? About, WorkspaceBody? Workspace, long[]? MemberIds, ProjectChannelBody[]? Channels);
public record ProjectPatchBody(string? Name, string? About);
public record ProjectMemberBody(string? Role);
public record ProjectAgentBody(string? TakesWorkFrom);
public record ChannelProjectBody(long? ProjectId);

public static partial class Api
{
    static void MapProjects(RouteGroupBuilder authed)
    {
        // a project, with its lead, people, a workspace and its channels, in one write
        authed.MapPost("/projects", async (ProjectBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            var name = (b.Name ?? "").Trim();
            if (name.Length is 0 or > 60) return Err(400, "Name the project (up to 60 characters).");
            var about = (b.About ?? "").Trim();
            if (about.Length > 300) return Err(400, "Keep what it's about to 300 characters.");
            var (ws, wsError) = CheckWorkspace(b.Workspace);
            if (wsError is not null) return Err(400, wsError);
            var channels = (b.Channels ?? Array.Empty<ProjectChannelBody>()).Select(x => (Name: NormaliseChannel(x.Name), Kind: x.Kind ?? "text")).ToList();
            if (channels.Count == 0) channels.Add((NormaliseChannel(name), "text"));
            foreach (var ch in channels)
            {
                if (!ChannelRx.IsMatch(ch.Name)) return Err(400, "Channel names are lowercase letters, numbers, - and _.");
                if (ch.Kind is not ("text" or "voice")) return Err(400, "A channel is text or voice.");
            }
            if (channels.Select(x => x.Name).Distinct().Count() != channels.Count) return Err(400, "Two of the channels have the same name.");
            var people = (b.MemberIds ?? Array.Empty<long>()).Where(id => id != me).Distinct().ToList();
            using (var c = db.Open())
            {
                if (!Access.IsActivePerson(c, me)) return Err(403, "Sign in first.");
                if (people.Any(id => !Access.IsActivePerson(c, id))) return Err(400, "Only people in the server can be added to a project.");
            }
            long id = 0; string? refused = null;
            store.Append(d =>
            {
                refused = null;
                if (d.Scalar<int>("SELECT COUNT(*) FROM projects WHERE name=@name", new { name }) > 0) { refused = "There's already a project with that name."; return Array.Empty<NewEvent>(); }
                foreach (var ch in channels)
                    if (d.Scalar<int>("SELECT COUNT(*) FROM channels WHERE name=@n", new { n = ch.Name }) > 0) { refused = $"There's already a channel named #{ch.Name}."; return Array.Empty<NewEvent>(); }
                id = d.NextId("projects");
                var s = $"project-{id}";
                var evs = new List<NewEvent> { new(s, "ProjectCreated", new { projectId = id, name, about }, me) };
                evs.Add(new(s, "ProjectMemberAdded", new { projectId = id, memberId = me, role = "lead" }, me, CausedByIndex: 0));
                foreach (var p in people) evs.Add(new(s, "ProjectMemberAdded", new { projectId = id, memberId = p, role = "member" }, me, CausedByIndex: 0));
                if (ws is not null) evs.Add(new(s, "WorkspaceAdded", WorkspaceData(id, d.NextId("workspaces"), ws), me, CausedByIndex: 0));
                foreach (var ch in channels)
                {
                    var cid = d.NextId("channels");
                    var at = evs.Count;
                    evs.Add(new($"channel-{cid}", "ChannelCreated", new { channelId = cid, name = ch.Name, topic = about, isPrivate = false, kind = ch.Kind, creatorId = me, projectId = id }, me, CausedByIndex: 0));
                    evs.Add(new($"channel-{cid}", "MemberAdded", new { channelId = cid, memberId = me }, me, CausedByIndex: at));
                }
                return evs;
            });
            if (refused is not null) return Err(409, refused);
            await rt.RefreshAll();
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { id });
        });

        // the settings page: workspace, people with roles, agents with where they run and their grants, the rules in force
        authed.MapGet("/projects/{id:long}", (long id, HttpContext ctx, Db db, RunnerRegistry runners) =>
        {
            var me = Me(ctx);
            using var c = db.Open();
            if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
            return Results.Ok(Settings(c, id, me, runners));
        });

        authed.MapPatch("/projects/{id:long}", async (long id, ProjectPatchBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            var name = b.Name?.Trim(); var about = b.About?.Trim();
            if (name is not null && name.Length is 0 or > 60) return Err(400, "Name the project (up to 60 characters).");
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me)) return Err(403, "Only the project's leads or an admin can change it.");
            }
            string? refused = null;
            store.Append(d =>
            {
                refused = null;
                var now = d.Query<ProjectRow>("SELECT * FROM projects WHERE id=@id", new { id }).First();
                if (name is not null && name != now.Name && d.Scalar<int>("SELECT COUNT(*) FROM projects WHERE name=@name AND id<>@id", new { name, id }) > 0) { refused = "There's already a project with that name."; return Array.Empty<NewEvent>(); }
                var changedName = name is not null && name != now.Name; var changedAbout = about is not null && about != now.About;
                if (!changedName && !changedAbout) return Array.Empty<NewEvent>();
                return new[] { new NewEvent($"project-{id}", "ProjectUpdated", new { projectId = id, name = changedName ? name : null, about = changedAbout ? about : null, from = now.Name }, me) };
            });
            if (refused is not null) return Err(409, refused);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // a person joins the project (or gets a new role); leads and admins decide
        authed.MapPut("/projects/{id:long}/members/{memberId:long}", async (long id, long memberId, ProjectMemberBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            var role = b.Role ?? "member";
            if (role is not ("lead" or "member")) return Err(400, "A project role is lead or member.");
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me)) return Err(403, "Only the project's leads or an admin can add people.");
                if (!Access.IsActivePerson(c, memberId)) return Err(404, "No such person.");
                if (Access.ProjectRole(c, id, memberId) == role) return Results.Ok(new { ok = true });
            }
            string? refused = null;
            store.Append(d =>
            {
                refused = null;
                if (role == "member" && d.Scalar<string>("SELECT role FROM project_members WHERE project_id=@id AND member_id=@memberId", new { id, memberId }) == "lead"
                    && d.Scalar<int>("SELECT COUNT(*) FROM project_members WHERE project_id=@id AND role='lead'", new { id }) <= 1) { refused = "A project needs at least one lead."; return Array.Empty<NewEvent>(); }
                return new[] { new NewEvent($"project-{id}", "ProjectMemberAdded", new { projectId = id, memberId, role }, me) };
            });
            if (refused is not null) return Err(400, refused);
            await rt.Refresh(memberId);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/projects/{id:long}/members/{memberId:long}", async (long id, long memberId, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me) && memberId != me) return Err(403, "Only the project's leads or an admin can remove people.");
                if (Access.ProjectRole(c, id, memberId) is null) return Results.Ok(new { ok = true });
            }
            string? refused = null;
            store.Append(d =>
            {
                refused = null;
                if (d.Scalar<string>("SELECT role FROM project_members WHERE project_id=@id AND member_id=@memberId", new { id, memberId }) == "lead"
                    && d.Scalar<int>("SELECT COUNT(*) FROM project_members WHERE project_id=@id AND role='lead'", new { id }) <= 1) { refused = "A project needs at least one lead."; return Array.Empty<NewEvent>(); }
                return new[] { new NewEvent($"project-{id}", "ProjectMemberRemoved", new { projectId = id, memberId }, me) };
            });
            if (refused is not null) return Err(400, refused);
            await rt.Refresh(memberId);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapPost("/projects/{id:long}/workspaces", async (long id, WorkspaceBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me)) return Err(403, "Only the project's leads or an admin can add a workspace.");
            }
            var (ws, error) = CheckWorkspace(b);
            if (error is not null) return Err(400, error);
            if (ws is null) return Err(400, "A workspace is a repository or a folder.");
            long wid = 0;
            store.Append(d => { wid = d.NextId("workspaces"); return new[] { new NewEvent($"project-{id}", "WorkspaceAdded", WorkspaceData(id, wid, ws), me) }; });
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { id = wid });
        });

        authed.MapDelete("/projects/{id:long}/workspaces/{workspaceId:long}", async (long id, long workspaceId, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            string name;
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                if (!Access.CanEditProject(c, id, me)) return Err(403, "Only the project's leads or an admin can remove a workspace.");
                var w = c.QueryFirstOrDefault<WorkspaceRow>("SELECT * FROM workspaces WHERE id=@workspaceId AND project_id=@id", new { workspaceId, id });
                if (w is null) return Err(404, "No such workspace.");
                name = w.Name;
            }
            store.Append(new[] { new NewEvent($"project-{id}", "WorkspaceRemoved", new { projectId = id, workspaceId, name }, me) });
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // an agent's boundary in a project, in sentences: for the add-agent dialog (with the grant it would get) and for its profile
        authed.MapGet("/projects/{id:long}/agents/{agentId:long}/boundary", (long id, long agentId, string? takesWorkFrom, HttpContext ctx, Db db, RunnerRegistry runners) =>
        {
            var me = Me(ctx);
            using var c = db.Open();
            if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
            var agent = Q.Member(c, agentId);
            if (agent is null || agent.Kind != "agent" || agent.Removed) return Err(404, "No such agent.");
            var grant = c.QueryFirstOrDefault<string?>("SELECT takes_work_from FROM project_agents WHERE project_id=@id AND agent_id=@agentId", new { id, agentId });
            var from = takesWorkFrom is "owner" or "members" ? takesWorkFrom : grant ?? "members";
            return Results.Ok(Boundary.For(c, Projects.Get(c, id)!, agent, from, runners.IsOnline(agentId)));
        });

        // add an agent to a project, or change its grant. Adding: a lead or admin who owns the agent (or an admin). The grant: also its owner.
        authed.MapPut("/projects/{id:long}/agents/{agentId:long}", async (long id, long agentId, ProjectAgentBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            var from = b.TakesWorkFrom ?? "members";
            if (from is not ("owner" or "members")) return Err(400, "An agent takes work from its owner or the project's members.");
            string? existing;
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                var agent = Q.Member(c, agentId);
                if (agent is null || agent.Kind != "agent" || agent.Removed) return Err(404, "No such agent.");
                existing = c.QueryFirstOrDefault<string?>("SELECT takes_work_from FROM project_agents WHERE project_id=@id AND agent_id=@agentId", new { id, agentId });
                var owns = agent.OwnerId == me || Access.IsAdmin(c, me);
                if (existing is null && !(Access.CanEditProject(c, id, me) && owns)) return Err(403, "A project's lead adds their own agents; an admin can add any.");
                if (existing is not null && !(Access.CanEditProject(c, id, me) || agent.OwnerId == me)) return Err(403, "Only the agent's owner, the project's leads or an admin can change this.");
                if (existing == from) return Results.Ok(new { ok = true });
            }
            store.Append(new[] { new NewEvent($"project-{id}", existing is null ? "AgentAddedToProject" : "AgentGrantsChanged", new { projectId = id, agentId, takesWorkFrom = from }, me) });
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        authed.MapDelete("/projects/{id:long}/agents/{agentId:long}", async (long id, long agentId, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            using (var c = db.Open())
            {
                if (!Access.CanSeeProject(c, id, me)) return Err(404, "No such project.");
                var owner = c.QueryFirstOrDefault<long?>("SELECT owner_id FROM agents WHERE member_id=@agentId", new { agentId });
                if (!Access.CanEditProject(c, id, me) && owner != me) return Err(403, "Only the agent's owner, the project's leads or an admin can take it out.");
                if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM project_agents WHERE project_id=@id AND agent_id=@agentId", new { id, agentId }) == 0) return Results.Ok(new { ok = true });
            }
            store.Append(new[] { new NewEvent($"project-{id}", "AgentRemovedFromProject", new { projectId = id, agentId }, me) });
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // move a channel into a project, or out of it (projectId null): whoever manages the channel, and leads the project it goes into
        authed.MapPut("/channels/{id:long}/project", async (long id, ChannelProjectBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var me = Me(ctx);
            long? from;
            using (var c = db.Open())
            {
                var ch = Q.Channel(c, id);
                if (ch is null || !Access.CanSee(c, id, me)) return Err(404, "No such channel.");
                if (!Access.CanManage(c, ch, me)) return Err(403, "Only an admin, the channel's creator or the project's lead can move it.");
                if (b.ProjectId is { } p && !Access.CanEditProject(c, p, me)) return Err(403, "Only the project's leads or an admin can move a channel into it.");
                if (ch.ProjectId == b.ProjectId) return Results.Ok(new { ok = true });
                from = ch.ProjectId;
            }
            store.Append(new[] { new NewEvent($"channel-{id}", "ChannelMovedToProject", new { channelId = id, projectId = b.ProjectId, from }, me) });
            await rt.RefreshAll();
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });
    }

    static object WorkspaceData(long projectId, long workspaceId, WorkspaceBody w) => new
    {
        projectId, workspaceId, name = w.Name, kind = w.Kind, url = w.Url, branch = w.Branch, path = w.Path, host = w.Host,
    };

    /// <summary>A workspace as given, cleaned up, or why it can't be one. Kind "none" (or no workspace) is no workspace: null and no error.</summary>
    static (WorkspaceBody? Ws, string? Error) CheckWorkspace(WorkspaceBody? w)
    {
        if (w is null || (w.Kind ?? "none") == "none") return (null, null);
        var name = string.IsNullOrWhiteSpace(w.Name) ? "app" : w.Name.Trim();
        if (name.Length > 40) return (null, "Keep the workspace's name short (40 characters at most).");
        switch (w.Kind)
        {
            case "repository":
                var url = (w.Url ?? "").Trim();
                if (url.Length == 0) return (null, "Add the repository's address (what git clones).");
                if (url.StartsWith('-')) return (null, "That isn't a repository address.");
                var branch = string.IsNullOrWhiteSpace(w.Branch) ? "main" : w.Branch.Trim();
                if (branch.StartsWith('-') || branch.Contains(' ') || branch.Contains("..")) return (null, "That isn't a branch name.");
                return (new WorkspaceBody(name, "repository", url, branch, null, null), null);
            case "folder":
                var path = (w.Path ?? "").Trim(); var host = (w.Host ?? "").Trim();
                if (path.Length == 0) return (null, "Add the folder's path.");
                if (host.Length == 0) return (null, "Say which machine the folder is on (the runner's host name).");
                return (new WorkspaceBody(name, "folder", null, string.IsNullOrWhiteSpace(w.Branch) ? null : w.Branch.Trim(), path, host), null);
            default:
                return (null, "A workspace is a repository, a folder or none.");
        }
    }

    /// <summary>What the project's settings page shows.</summary>
    static object Settings(SqliteConnection c, long id, long me, RunnerRegistry runners)
    {
        var p = Projects.Get(c, id)!;
        var workspaces = Projects.Workspaces(c, id);
        var agents = c.Query<ProjectAgentRow>("SELECT agent_id, takes_work_from FROM project_agents WHERE project_id=@id ORDER BY agent_id", new { id })
            .Select(pa => (Grant: pa, Agent: Q.Member(c, pa.AgentId)!)).Where(x => !x.Agent.Removed).ToList();
        string NameOf(long? m) => m is null ? "someone" : Q.Member(c, m.Value)?.DisplayName ?? "someone";
        return new
        {
            id = p.Id, name = p.Name, about = p.About,
            role = Access.ProjectRole(c, id, me), canEdit = Access.CanEditProject(c, id, me),
            workspaces = workspaces.Select(w => new
            {
                id = w.Id, name = w.Name, kind = w.Kind, url = w.Url, branch = w.Branch, path = w.Path, host = w.Host, addedBy = NameOf(w.AddedBy),
                // where it is cloned or used: what the agents' runners reported when they prepared threads in it
                places = c.Query<(string? Host, string? Location, string? Handle)>(@"SELECT DISTINCT ta.host, ta.location, m.handle FROM thread_agents ta JOIN members m ON m.id=ta.agent_id
                    WHERE ta.workspace_id=@w ORDER BY ta.host, m.handle", new { w = w.Id }).Select(x => new { host = x.Host, location = x.Location, agent = x.Handle }).ToList(),
                cantReach = agents.Where(a => !w.ReachableFrom(a.Agent.RunnerHost)).Select(a => a.Agent.Handle).ToList(),
            }).ToList(),
            people = c.Query<(long Id, string Role)>(@"SELECT pm.member_id, pm.role FROM project_members pm JOIN members m ON m.id=pm.member_id WHERE pm.project_id=@id AND m.removed=0
                ORDER BY pm.role='lead' DESC, m.display_name", new { id }).Select(x => new { id = x.Id, role = x.Role }).ToList(),
            agents = agents.Select(a => new
            {
                id = a.Agent.Id, handle = a.Agent.Handle, displayName = a.Agent.DisplayName, ownerId = a.Agent.OwnerId, ownerName = NameOf(a.Agent.OwnerId),
                runnerHost = a.Agent.RunnerHost, online = runners.IsOnline(a.Agent.Id), takesWorkFrom = a.Grant.TakesWorkFrom, sandboxed = false,
                canChange = Access.CanEditProject(c, id, me) || a.Agent.OwnerId == me,
            }).ToList(),
            channels = c.Query<ChannelRow>("SELECT * FROM channels WHERE project_id=@id ORDER BY kind='voice', name", new { id }).Where(ch => Access.CanSee(c, ch.Id, me))
                .Select(ch => new { id = ch.Id, name = ch.Name, kind = ch.Kind, isPrivate = ch.IsPrivate }).ToList(),
            rules = Rules(workspaces),
        };
    }

    /// <summary>The rules in force in every project today, said plainly. They're the server's and runner's own behaviour, not settings.</summary>
    static List<string> Rules(List<WorkspaceRow> ws)
    {
        var rules = new List<string>();
        if (ws.Any(w => w.Kind == "repository"))
            rules.Add($"Each thread works in its own worktree on a `cowork/<thread>` branch. A hook refuses pushes to {Projects.List(ws.Where(w => w.Kind == "repository").Select(w => $"`{w.Branch}`").Distinct().ToList())}.");
        if (ws.Any(w => w.Kind == "folder"))
            rules.Add("A folder can only be used by agents whose runner is on its machine. A git folder gets a worktree per thread too; any other folder is shared.");
        if (ws.Count == 0) rules.Add("No workspace: each thread gets an empty folder on its agent's machine.");
        rules.Add("An agent's permission requests go to its owner only; everyone else sees that it's waiting.");
        rules.Add("An agent only takes work from who its grant says; anyone else gets a short reply in the thread.");
        rules.Add("Agent-to-agent turns stop after 3 without a person.");
        rules.Add("What an agent's harness keeps for this project stays in a data folder for this project.");
        return rules;
    }
}
