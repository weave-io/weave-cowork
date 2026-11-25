-- Brief 9: artifacts. What an agent shows with `cowork_show` (`ArtifactPublished` on the thread's stream): an artifact belongs to one thread and
-- is named by the path it came from there (the same path again is a new version). The bytes are in a content-addressed folder under the data
-- directory, never in events or here; each version lists its files by SHA-256. A card post (kind `artifact`) carries what the thread shows.
CREATE TABLE artifacts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  thread_id INTEGER NOT NULL, channel_id INTEGER NOT NULL, project_id INTEGER,
  agent_id INTEGER NOT NULL, path TEXT NOT NULL, title TEXT NOT NULL, kind TEXT NOT NULL,
  version INTEGER NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
CREATE UNIQUE INDEX artifacts_thread_path ON artifacts(thread_id, path);
CREATE INDEX artifacts_channel ON artifacts(channel_id);
CREATE TABLE artifact_versions (
  artifact_id INTEGER NOT NULL, version INTEGER NOT NULL, title TEXT NOT NULL, kind TEXT NOT NULL, entry TEXT NOT NULL,
  size INTEGER NOT NULL, files INTEGER NOT NULL, published_by INTEGER NOT NULL, behalf INTEGER, post_id INTEGER, created_at INTEGER NOT NULL,
  redacted INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (artifact_id, version));
CREATE TABLE artifact_files (
  artifact_id INTEGER NOT NULL, version INTEGER NOT NULL, path TEXT NOT NULL, sha256 TEXT NOT NULL, size INTEGER NOT NULL, type TEXT NOT NULL,
  PRIMARY KEY (artifact_id, version, path));
CREATE INDEX artifact_files_sha ON artifact_files(sha256);
