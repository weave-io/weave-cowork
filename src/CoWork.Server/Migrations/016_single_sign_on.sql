-- Brief 11: people sign in through the server's OpenID Connect provider, never with a password. A person is the provider's subject id under
-- its issuer (`iss` + `sub`): an email can change or be claimed by someone else, a subject can't. Like the secrets in 004, a sign-in is
-- written in the same transaction as the event that made the person and never goes into an event (the email is personal data), so a
-- rebuild leaves the table alone. Password hashes are dropped: there is no migration for password accounts (decided 26 Nov 2026), and a
-- server that had them says so on its sign-in screen.
CREATE TABLE logins (
  issuer TEXT NOT NULL,
  subject TEXT NOT NULL,
  member_id INTEGER NOT NULL,
  email TEXT,
  created_at INTEGER NOT NULL,
  PRIMARY KEY (issuer, subject)
);
CREATE INDEX logins_member ON logins(member_id);
DROP TABLE credentials;
