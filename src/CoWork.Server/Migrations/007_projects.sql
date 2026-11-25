-- Brief 5: projects (docs/architecture/projects-agents-sandboxes.html). A project holds channels and owns what isn't conversation: its people
-- (lead or member), its workspaces (what agents work on) and which agents work here, with their grants. Every table here is an inline projection
-- of the `project-{id}` streams; `channels.project_id` follows `ChannelCreated.projectId` and `ChannelMovedToProject`.

CREATE TABLE projects (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  about TEXT NOT NULL DEFAULT '',
  created_by INTEGER NOT NULL,
  created_at INTEGER NOT NULL
);

CREATE TABLE project_members (
  project_id INTEGER NOT NULL,
  member_id INTEGER NOT NULL,                  -- a person
  role TEXT NOT NULL,                          -- lead | member
  PRIMARY KEY (project_id, member_id)
);

CREATE TABLE project_agents (
  project_id INTEGER NOT NULL,
  agent_id INTEGER NOT NULL,
  takes_work_from TEXT NOT NULL DEFAULT 'members',   -- owner | members
  added_by INTEGER,
  PRIMARY KEY (project_id, agent_id)
);

CREATE TABLE workspaces (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  project_id INTEGER NOT NULL,
  name TEXT NOT NULL,                          -- "app"
  kind TEXT NOT NULL,                          -- repository | folder | none
  url TEXT,                                    -- repository: what to clone
  branch TEXT,                                 -- repository: the project's branch (threads branch off it; nobody pushes to it)
  path TEXT,                                   -- folder: where it is
  host TEXT,                                   -- folder: the runner host it is on
  added_by INTEGER,
  created_at INTEGER NOT NULL
);
CREATE INDEX workspaces_project ON workspaces(project_id, id);

ALTER TABLE channels ADD COLUMN project_id INTEGER;

-- Who is in a channel. Outside a project, and for a private channel, it is who was added. A public channel inside a project defaults its
-- members to the project's: its people and its agents. (A view, not a table: nothing to rebuild.)
CREATE VIEW channel_audience AS
  SELECT channel_id, member_id FROM channel_members
  UNION
  SELECT c.id, pm.member_id FROM channels c JOIN project_members pm ON pm.project_id = c.project_id WHERE c.is_private = 0
  UNION
  SELECT c.id, pa.agent_id FROM channels c JOIN project_agents pa ON pa.project_id = c.project_id WHERE c.is_private = 0;
