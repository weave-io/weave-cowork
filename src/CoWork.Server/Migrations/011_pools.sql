-- Brief 7, round 1: agents on demand (docs/architecture/provisioning.html). A team template is an agent whose placement is a pool instead of
-- a runner its owner connects; a pool starts a sandbox (a container) per thread; the runner inside connects with a token good for that one
-- thread, which is also its key to the model gateway.

-- a template: placement 'pool', the image its sandboxes start from and the model it asks the gateway for
ALTER TABLE agents ADD COLUMN placement TEXT NOT NULL DEFAULT 'runner';     -- runner | pool
ALTER TABLE agents ADD COLUMN image TEXT;
ALTER TABLE agents ADD COLUMN model TEXT;

-- pools (projection of the `pool-{id}` streams)
CREATE TABLE pools (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL UNIQUE,                   -- pool-01
  host TEXT,
  slots INTEGER NOT NULL DEFAULT 0,
  memory_mb INTEGER,
  created_by INTEGER,
  created_at INTEGER NOT NULL,
  code_expires INTEGER,
  enrolled_at INTEGER,
  last_seen INTEGER,
  removed INTEGER NOT NULL DEFAULT 0
);
-- not a projection: a pool's token and one-time code, as hashes
CREATE TABLE pool_secrets (pool_id INTEGER PRIMARY KEY, token_hash TEXT, code_hash TEXT);
CREATE INDEX pool_secrets_token ON pool_secrets(token_hash);
CREATE INDEX pool_secrets_code ON pool_secrets(code_hash);

-- one sandbox per thread and template (projection of the Sandbox* events in the thread's stream). It is created once and resumed on its
-- volume as often as the thread needs it. `post_id` is its current starting card in the thread.
CREATE TABLE sandboxes (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  thread_id INTEGER NOT NULL,
  agent_id INTEGER NOT NULL,
  project_id INTEGER,
  pool_id INTEGER,                             -- where it is (or will be); null while it waits for room
  state TEXT NOT NULL,                         -- waiting | starting | resuming | running | suspending | suspended | failed | deleted
  image TEXT,
  memory_mb INTEGER,
  branch TEXT,
  requested_by INTEGER,
  requested_at INTEGER NOT NULL,               -- when it last asked for room (the queue's order)
  started_at INTEGER,
  last_active INTEGER,
  post_id INTEGER,
  starts INTEGER NOT NULL DEFAULT 0,
  reason TEXT,
  UNIQUE (thread_id, agent_id)
);
CREATE INDEX sandboxes_pool ON sandboxes(pool_id, state);

-- not a projection: thread tokens, as hashes. Minted per create and per resume, bound to a sandbox (and so to its agent, project and thread),
-- revoked on suspend, removal or delete. Nothing in the event log holds one.
CREATE TABLE thread_tokens (
  token_hash TEXT PRIMARY KEY,
  sandbox_id INTEGER NOT NULL,
  agent_id INTEGER NOT NULL,
  project_id INTEGER,
  thread_id INTEGER NOT NULL,
  minted_at INTEGER NOT NULL,
  revoked_at INTEGER
);
CREATE INDEX thread_tokens_sandbox ON thread_tokens(sandbox_id);

-- what the model gateway forwarded, per turn (projection of ModelUsageRecorded)
CREATE TABLE model_usage (
  thread_id INTEGER NOT NULL,
  agent_id INTEGER NOT NULL,
  turn TEXT NOT NULL,
  project_id INTEGER,
  model TEXT,
  calls INTEGER NOT NULL DEFAULT 0,
  prompt_tokens INTEGER NOT NULL DEFAULT 0,
  completion_tokens INTEGER NOT NULL DEFAULT 0,
  cost REAL,
  PRIMARY KEY (thread_id, agent_id, turn)
);

-- not a projection: the server's own secrets (the model provider's key). Never in an event.
CREATE TABLE server_secrets (key TEXT PRIMARY KEY, value TEXT NOT NULL);
