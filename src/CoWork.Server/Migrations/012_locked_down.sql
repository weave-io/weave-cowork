-- Brief 7, round 2: locked down. A project's network list (what its sandboxes may reach through the pool's proxy) and what someone allowed
-- for one thread; its setup command; budgets enforced by the gateway, in tokens and in dollars (a provider that reports no cost, such as
-- GitHub Copilot, is held by the token caps); and Pause all agents. All projections of events.

ALTER TABLE projects ADD COLUMN setup_command TEXT;
ALTER TABLE projects ADD COLUMN paused_by INTEGER;
ALTER TABLE projects ADD COLUMN paused_at INTEGER;

CREATE TABLE project_network (project_id INTEGER NOT NULL, host TEXT NOT NULL, PRIMARY KEY (project_id, host));

-- hosts someone allowed for the rest of a thread (an ask answered "Allow for this thread")
CREATE TABLE thread_egress (thread_id INTEGER NOT NULL, agent_id INTEGER NOT NULL, host TEXT NOT NULL, PRIMARY KEY (thread_id, agent_id, host));

-- a project's caps: a month and a thread at most, in tokens and in dollars (null: no cap)
CREATE TABLE budgets (
  project_id INTEGER PRIMARY KEY,
  month_tokens INTEGER, month_usd REAL,
  thread_tokens INTEGER, thread_usd REAL
);
-- what a lead added to one thread's cap
CREATE TABLE thread_budgets (thread_id INTEGER PRIMARY KEY, extra_tokens INTEGER NOT NULL DEFAULT 0, extra_usd REAL NOT NULL DEFAULT 0);

-- the gateway's calls per project, month (UTC, yyyy-MM) and agent
CREATE TABLE project_usage (
  project_id INTEGER NOT NULL, month TEXT NOT NULL, agent_id INTEGER NOT NULL,
  calls INTEGER NOT NULL DEFAULT 0, tokens INTEGER NOT NULL DEFAULT 0, cost REAL,
  PRIMARY KEY (project_id, month, agent_id)
);
