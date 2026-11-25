-- Round 3 of brief 4: the first new views, all ASYNC projections (docs/architecture/event-sourcing.html). Each one keeps a checkpoint (the event
-- position it has applied up to) and can be rebuilt from zero. None of these tables is ever the source of truth: `events` is.

CREATE TABLE projection_checkpoints (
  name TEXT PRIMARY KEY,
  position INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  rebuilt_at INTEGER
);

-- ---- audit: one entry per thing that happened; a tool call and a turn are folded from several events into one sentence ----
CREATE TABLE audit_names (kind TEXT NOT NULL, id INTEGER NOT NULL, name TEXT NOT NULL, handle TEXT, owner_id INTEGER, PRIMARY KEY (kind, id));   -- kind: person, agent, channel
CREATE TABLE audit_threads (thread_id INTEGER PRIMARY KEY, channel_id INTEGER NOT NULL);
CREATE TABLE audit_turns (turn TEXT PRIMARY KEY, agent_id INTEGER, behalf_id INTEGER, thread_id INTEGER, host TEXT);
CREATE TABLE audit_entries (
  id INTEGER PRIMARY KEY,                      -- the position of the first event it is made of
  kind TEXT NOT NULL,                          -- event, tool, turn
  occurred_at INTEGER NOT NULL,
  actor_id INTEGER, behalf_id INTEGER, agent_id INTEGER, channel_id INTEGER, thread_id INTEGER,
  correlation TEXT, tool_id TEXT,
  verb TEXT, detail TEXT,                      -- tool: "run", "git log -3"
  ask_position INTEGER, ask_state TEXT, answered_by INTEGER,   -- tool: pending, once, always, denied
  run_state TEXT,                              -- tool: running, completed, failed, unfinished …; turn: running, finished, failed, interrupted
  reason TEXT,
  sentence TEXT NOT NULL
);
CREATE INDEX audit_entries_correlation ON audit_entries(correlation, kind);
CREATE INDEX audit_entries_ask ON audit_entries(ask_position) WHERE ask_position IS NOT NULL;
CREATE INDEX audit_entries_channel ON audit_entries(channel_id, id);
CREATE TABLE audit_members (entry_id INTEGER NOT NULL, member_id INTEGER NOT NULL, PRIMARY KEY (entry_id, member_id));
CREATE INDEX audit_members_member ON audit_members(member_id, entry_id);

-- ---- unread counts and the mentions inbox ----
CREATE TABLE unread_activity (position INTEGER PRIMARY KEY, channel_id INTEGER NOT NULL, post_id INTEGER NOT NULL UNIQUE, author_id INTEGER NOT NULL);
CREATE INDEX unread_activity_channel ON unread_activity(channel_id, position);
-- channel_id 0 is the person's own baseline (their PersonJoined): nothing before it is unread
CREATE TABLE unread_markers (person_id INTEGER NOT NULL, channel_id INTEGER NOT NULL, position INTEGER NOT NULL, PRIMARY KEY (person_id, channel_id));
CREATE TABLE unread_handles (handle TEXT PRIMARY KEY, member_id INTEGER NOT NULL);
CREATE TABLE unread_mentions (
  position INTEGER NOT NULL, person_id INTEGER NOT NULL, post_id INTEGER NOT NULL, channel_id INTEGER NOT NULL, thread_id INTEGER,
  author_id INTEGER NOT NULL, excerpt TEXT NOT NULL, occurred_at INTEGER NOT NULL,
  PRIMARY KEY (person_id, post_id)
);
CREATE INDEX unread_mentions_person ON unread_mentions(person_id, position);
CREATE TABLE unread_settings (key TEXT PRIMARY KEY, value INTEGER NOT NULL);       -- baseline: the position of UnreadBaselineSet

-- ---- agent activity ----
CREATE TABLE activity_threads (thread_id INTEGER PRIMARY KEY, channel_id INTEGER NOT NULL);
CREATE TABLE activity_roots (post_id INTEGER PRIMARY KEY, channel_id INTEGER NOT NULL, excerpt TEXT NOT NULL);
CREATE TABLE activity_turns (
  turn TEXT PRIMARY KEY, agent_id INTEGER NOT NULL, thread_id INTEGER NOT NULL, channel_id INTEGER, behalf_id INTEGER,
  started_position INTEGER NOT NULL, started_at INTEGER NOT NULL, ended_at INTEGER,
  outcome TEXT NOT NULL, reason TEXT, tool_calls INTEGER NOT NULL DEFAULT 0, host TEXT
);
CREATE INDEX activity_turns_agent ON activity_turns(agent_id, started_position);
CREATE TABLE activity_connections (id INTEGER PRIMARY KEY AUTOINCREMENT, agent_id INTEGER NOT NULL, host TEXT, connected_at INTEGER NOT NULL, disconnected_at INTEGER);
CREATE INDEX activity_connections_agent ON activity_connections(agent_id, id);
