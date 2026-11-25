-- Round 2 of brief 4: every table the app reads is a projection of the events (docs/architecture/event-sourcing.html).
-- What can't be an event, because the log must be safe to export, moves into small tables of its own: password hashes, runner
-- token / enrolment-code hashes and invite codes. Nothing in them is rebuilt from events; the commands write them in the
-- same transaction as the event.

CREATE TABLE credentials (member_id INTEGER PRIMARY KEY, password_hash TEXT NOT NULL);
INSERT INTO credentials SELECT member_id, password_hash FROM people;
ALTER TABLE people DROP COLUMN password_hash;

CREATE TABLE runner_secrets (agent_id INTEGER PRIMARY KEY, token_hash TEXT, code_hash TEXT);
INSERT INTO runner_secrets SELECT member_id, token_hash, code_hash FROM agents WHERE token_hash IS NOT NULL OR code_hash IS NOT NULL;
CREATE INDEX runner_secrets_token ON runner_secrets(token_hash);
CREATE INDEX runner_secrets_code ON runner_secrets(code_hash);
ALTER TABLE agents DROP COLUMN token_hash;
ALTER TABLE agents DROP COLUMN code_hash;

-- an invite gets an id (the events name it); its code stays out of the log
CREATE TABLE invites_new (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  created_by INTEGER NOT NULL REFERENCES members(id),
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  max_uses INTEGER NOT NULL,
  uses INTEGER NOT NULL DEFAULT 0,
  revoked INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE invite_codes (invite_id INTEGER PRIMARY KEY, code TEXT NOT NULL UNIQUE);
INSERT INTO invites_new(created_by, created_at, expires_at, max_uses, uses, revoked)
  SELECT created_by, created_at, expires_at, max_uses, uses, revoked FROM invites ORDER BY rowid;
INSERT INTO invite_codes(invite_id, code) SELECT row_number() OVER (ORDER BY rowid), code FROM invites ORDER BY rowid;
DROP TABLE invites;
ALTER TABLE invites_new RENAME TO invites;

-- a deleted message stays in `posts` (later events about it must not bring it back) but nothing shows it
ALTER TABLE posts ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0;

-- the turns in flight, per thread and agent (from TurnStarted until a turn ends); replaces the `sent` status of a delivery
CREATE TABLE open_turns (
  thread_id INTEGER NOT NULL,
  agent_id INTEGER NOT NULL,
  turn TEXT,
  behalf INTEGER,
  cause INTEGER,                                -- the position of the TurnStarted event
  PRIMARY KEY (thread_id, agent_id)
);

-- a runner's position: every MessageDelivered to its agent at or before it has started a turn (or can no longer be heard)
CREATE TABLE runner_positions (agent_id INTEGER PRIMARY KEY, position INTEGER NOT NULL);

-- `deliveries` goes: what is queued is a MessageDelivered event after the runner's position that no turn has started yet.
-- The old rows are kept (renamed) only so the importer can read them once; it drops the table.
ALTER TABLE deliveries RENAME TO legacy_deliveries;

CREATE INDEX events_delivered_agent ON events(json_extract(data, '$.agentId'), position) WHERE type = 'MessageDelivered';
