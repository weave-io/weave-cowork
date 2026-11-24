-- a channel is text or voice
ALTER TABLE channels ADD COLUMN kind TEXT NOT NULL DEFAULT 'text';
