-- Brief 6: search. An ASYNC projection (SearchProjection): built from the events, checkpointed, rebuildable from zero. Not a source of truth.
-- One document per thing people search for. `kind` is `message` today (a post, or one agent answer however many segments it was written in);
-- `outcome` (a resolved thread's summary) and `library` (a saved document) will join it, with a title, under the same index and the same rules.

CREATE TABLE search_docs (
  id INTEGER PRIMARY KEY,                      -- the FTS row's rowid
  kind TEXT NOT NULL,                          -- message | outcome | library
  ref_id INTEGER NOT NULL,                     -- message: the post's id
  channel_id INTEGER NOT NULL,                 -- who may see it follows from the channel
  thread_id INTEGER,                           -- the thread's root post id; a root post gets its own id when its thread starts
  author_id INTEGER,
  position INTEGER NOT NULL,                   -- the event that made it
  occurred_at INTEGER NOT NULL,
  gone INTEGER NOT NULL DEFAULT 0,             -- deleted or redacted: out of the index for good (a later segment can't bring it back)
  UNIQUE (kind, ref_id)
);
CREATE INDEX search_docs_channel ON search_docs(channel_id);
CREATE INDEX search_docs_thread ON search_docs(thread_id) WHERE thread_id IS NOT NULL;

-- the words: rowid = search_docs.id. `title` is empty for messages (outcomes and library documents will have one; it ranks higher)
CREATE VIRTUAL TABLE search_fts USING fts5(title, body, tokenize = 'porter unicode61');
