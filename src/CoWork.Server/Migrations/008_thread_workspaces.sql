-- Brief 5: a thread's work happens somewhere. `threads.slug` names its branch (`cowork/<slug>`), chosen once when the thread starts.
-- What the agent's runner reported when it prepared the thread's workspace (`ThreadWorkspaceReady`): which project workspace, what kind of
-- place (a worktree, a folder, a scratch folder, the agent's own folder), the branch, where on which host, and whether it is sandboxed.
ALTER TABLE threads ADD COLUMN slug TEXT;
ALTER TABLE thread_agents ADD COLUMN workspace_id INTEGER;
ALTER TABLE thread_agents ADD COLUMN place TEXT;
ALTER TABLE thread_agents ADD COLUMN branch TEXT;
ALTER TABLE thread_agents ADD COLUMN location TEXT;
ALTER TABLE thread_agents ADD COLUMN host TEXT;
ALTER TABLE thread_agents ADD COLUMN sandboxed INTEGER NOT NULL DEFAULT 0;
