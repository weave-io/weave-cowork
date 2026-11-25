-- Brief 8: threads you can find. A thread has a place of its own in the channel list and a full view, so the thread projection keeps what
-- the list needs: a title someone gave it (`ThreadRenamed`; null means the automatic one: the outcome's title when resolved, else the first
-- words of its opening post) and when something last happened in it (a reply, a tool row, an ask, an outcome…), to order the list by.
ALTER TABLE threads ADD COLUMN title TEXT;
ALTER TABLE threads ADD COLUMN titled_at INTEGER;
ALTER TABLE threads ADD COLUMN last_at INTEGER;
ALTER TABLE threads ADD COLUMN last_position INTEGER;
CREATE INDEX threads_channel_activity ON threads(channel_id, last_at);
UPDATE threads SET
  last_at = MAX(COALESCE((SELECT MAX(created_at) FROM posts p WHERE p.thread_id = threads.root_id), 0), COALESCE((SELECT created_at FROM posts WHERE id = threads.root_id), 0)),
  last_position = MAX(COALESCE((SELECT MAX(position) FROM posts p WHERE p.thread_id = threads.root_id), 0), COALESCE((SELECT position FROM posts WHERE id = threads.root_id), 0));

-- Unread replies per thread (the `unread` projection, async): each reply people and agents write in a thread, what each person has read of a
-- thread (`ThreadRead`, on `person-{id}`), and when they were added to a private channel (its earlier threads were not written for them).
CREATE TABLE unread_replies (position INTEGER PRIMARY KEY, thread_id INTEGER NOT NULL, channel_id INTEGER NOT NULL, post_id INTEGER NOT NULL UNIQUE, author_id INTEGER NOT NULL);
CREATE INDEX unread_replies_thread ON unread_replies(thread_id, position);
CREATE TABLE unread_thread_markers (person_id INTEGER NOT NULL, thread_id INTEGER NOT NULL, position INTEGER NOT NULL, PRIMARY KEY (person_id, thread_id));
CREATE TABLE unread_joined (person_id INTEGER NOT NULL, channel_id INTEGER NOT NULL, position INTEGER NOT NULL, PRIMARY KEY (person_id, channel_id));

-- the unread view replays from the start, so the new tables hold every thread's replies, not only those after the upgrade
DELETE FROM unread_mentions; DELETE FROM unread_activity; DELETE FROM unread_markers; DELETE FROM unread_handles; DELETE FROM unread_settings;
DELETE FROM projection_checkpoints WHERE name = 'unread';
