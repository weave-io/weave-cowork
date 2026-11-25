-- Brief 7, round 3: threads as work. A thread an agent resolved (cowork_resolve_thread) is done: its outcome is a post of kind `outcome`
-- (title, summary, time and usage), the thread remembers it, and search indexes it as a document of kind `outcome`. A person's new message
-- or Reopen makes it work again. A done thread's sandbox volume is deleted after the server's retention time (its branch with it, unless it
-- has changes).
ALTER TABLE threads ADD COLUMN resolved_post INTEGER;
ALTER TABLE threads ADD COLUMN resolved_at INTEGER;
ALTER TABLE threads ADD COLUMN resolved_by INTEGER;
