CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

CREATE TABLE members (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  kind TEXT NOT NULL,                          -- person | agent
  handle TEXT NOT NULL UNIQUE COLLATE NOCASE,
  display_name TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  removed INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE people (
  member_id INTEGER PRIMARY KEY REFERENCES members(id),
  password_hash TEXT NOT NULL,
  role TEXT NOT NULL                           -- admin | member
);

CREATE TABLE agents (
  member_id INTEGER PRIMARY KEY REFERENCES members(id),
  owner_id INTEGER NOT NULL REFERENCES members(id),
  about TEXT NOT NULL DEFAULT '',
  instructions TEXT NOT NULL DEFAULT '',
  harness_command TEXT NOT NULL,               -- JSON array
  workspace TEXT NOT NULL,
  token_hash TEXT,                             -- SHA-256 of the runner token
  code_hash TEXT,                              -- SHA-256 of the one-time enrolment code
  code_expires INTEGER,
  runner_host TEXT,
  last_seen INTEGER
);

CREATE TABLE invites (
  code TEXT PRIMARY KEY,
  created_by INTEGER NOT NULL REFERENCES members(id),
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  max_uses INTEGER NOT NULL,
  uses INTEGER NOT NULL DEFAULT 0,
  revoked INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE channels (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  topic TEXT NOT NULL DEFAULT '',
  is_private INTEGER NOT NULL DEFAULT 0,
  creator_id INTEGER NOT NULL REFERENCES members(id),
  created_at INTEGER NOT NULL
);

CREATE TABLE channel_members (
  channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
  member_id INTEGER NOT NULL REFERENCES members(id),
  PRIMARY KEY (channel_id, member_id)
);

CREATE TABLE posts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
  thread_id INTEGER,                           -- the root post's id, null for a top-level post
  author_id INTEGER NOT NULL REFERENCES members(id),
  body TEXT NOT NULL,
  kind TEXT NOT NULL DEFAULT 'text',           -- text | tool | ask | notice
  meta TEXT,                                   -- JSON for tool / ask posts
  streaming INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL
);
CREATE INDEX posts_channel ON posts(channel_id, id);
CREATE INDEX posts_thread ON posts(thread_id, id);

CREATE TABLE threads (
  root_id INTEGER PRIMARY KEY REFERENCES posts(id) ON DELETE CASCADE,
  channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
  started_by INTEGER NOT NULL REFERENCES members(id),
  agent_chain INTEGER NOT NULL DEFAULT 0       -- agent-to-agent turns since a person last posted
);

CREATE TABLE thread_agents (
  thread_id INTEGER NOT NULL REFERENCES threads(root_id) ON DELETE CASCADE,
  agent_id INTEGER NOT NULL REFERENCES members(id),
  PRIMARY KEY (thread_id, agent_id)
);

CREATE TABLE deliveries (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  thread_id INTEGER NOT NULL REFERENCES threads(root_id) ON DELETE CASCADE,
  agent_id INTEGER NOT NULL REFERENCES members(id),
  post_id INTEGER NOT NULL,
  text TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'queued',       -- queued | sent | done | failed
  created_at INTEGER NOT NULL
);
CREATE INDEX deliveries_agent ON deliveries(agent_id, status);
