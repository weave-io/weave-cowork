-- Conversations as events (docs/architecture/event-sourcing.html). The thread stream and top-level posts are recorded here;
-- `posts`, `threads`, `thread_agents` and `deliveries` are projections the app reads, updated in the same transaction.
CREATE TABLE events (
  position INTEGER PRIMARY KEY AUTOINCREMENT,   -- global order; what subscribers catch up from
  stream_id TEXT NOT NULL,                      -- 'thread-42', 'channel-3'
  version INTEGER NOT NULL,                     -- per stream; UNIQUE(stream_id, version) is the optimistic concurrency
  type TEXT NOT NULL,                           -- 'MessagePosted', 'ToolCallFinished', ...
  schema INTEGER NOT NULL DEFAULT 1,            -- for upcasting old events
  data TEXT NOT NULL,                           -- JSON
  actor_id INTEGER,                             -- who did it: a person, an agent, or null for the server
  on_behalf_of INTEGER,                         -- the person whose request an agent is acting on
  correlation TEXT,                             -- the turn this belongs to
  causation INTEGER,                            -- the position of the event that caused this one
  occurred_at INTEGER NOT NULL,
  UNIQUE (stream_id, version)
);
CREATE INDEX events_correlation ON events(correlation) WHERE correlation IS NOT NULL;
CREATE INDEX events_type ON events(type, position);

-- the position of the last event that changed a post: a browser keeps the newer of two copies
ALTER TABLE posts ADD COLUMN position INTEGER NOT NULL DEFAULT 0;

-- a delivery remembers its turn, who started it and the event that queued it
ALTER TABLE deliveries ADD COLUMN turn TEXT;
ALTER TABLE deliveries ADD COLUMN behalf INTEGER;
ALTER TABLE deliveries ADD COLUMN cause INTEGER;

-- "Record full tool output": off, so only the size and a SHA-256 of what a tool printed are kept
ALTER TABLE agents ADD COLUMN record_tool_output INTEGER NOT NULL DEFAULT 0;
