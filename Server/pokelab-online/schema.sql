-- Poké Lab online schema (Cloudflare D1 / SQLite).
--
-- Applied with:  npm run db:apply        (remote)
--                npm run db:apply:local  (the wrangler dev sandbox)
--
-- Every statement is IF NOT EXISTS so the file is safe to re-run; it is the only description
-- of the shape, and a schema you are afraid to re-apply is one that drifts.

-- One trainer.
--
-- There is no password column and there is not meant to be. The account is proved by answering
-- the question named in question_id, and what is stored is a PBKDF2 hash of the NORMALISED
-- answer plus a per-account salt -- never the answer, and never a bare SHA of it, because a
-- recovery answer comes from a small enough space that an unsalted fast hash is a lookup table.
--
-- name_key is the lower-cased trainer name and carries the uniqueness constraint, so "Kes" and
-- "kes" cannot both exist; name is what is shown, with the capitalisation the player chose.
CREATE TABLE IF NOT EXISTS accounts (
  id             TEXT PRIMARY KEY,
  name           TEXT NOT NULL,
  name_key       TEXT NOT NULL UNIQUE,
  question_id    TEXT NOT NULL,
  answer_hash    TEXT NOT NULL,
  answer_salt    TEXT NOT NULL,
  created_at     INTEGER NOT NULL,
  last_seen_at   INTEGER NOT NULL
);

-- A device's claim to be an account. Deleted on sign-out, expired by TOKEN_TTL_SECONDS.
CREATE TABLE IF NOT EXISTS tokens (
  token       TEXT PRIMARY KEY,
  account_id  TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  issued_at   INTEGER NOT NULL,
  expires_at  INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS tokens_account ON tokens(account_id);

-- The six.
--
-- slot is 0-5 and is the identity of a team member for the whole of its life: the client
-- reports experience against a slot, the PvP room orders the party by slot, and the UNIQUE
-- below is what makes "no duplicates" a property of the database rather than of the roll that
-- happened to produce it.
CREATE TABLE IF NOT EXISTS roster (
  account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  slot         INTEGER NOT NULL,
  species_id   INTEGER NOT NULL,
  rarity       TEXT NOT NULL,
  level        INTEGER NOT NULL DEFAULT 5,
  experience   INTEGER NOT NULL DEFAULT 125,
  drawn_at     INTEGER NOT NULL,
  PRIMARY KEY (account_id, slot)
);
-- One species per account: the no-duplicates rule, enforced where it cannot be forgotten.
CREATE UNIQUE INDEX IF NOT EXISTS roster_unique_species ON roster(account_id, species_id);

-- Sign-in attempts, for the rate limit that the recovery-question scheme depends on.
-- Rows are counted within a window and swept opportunistically; there is no cron for it,
-- because a table this small does not need one.
CREATE TABLE IF NOT EXISTS signin_attempts (
  name_key    TEXT NOT NULL,
  at          INTEGER NOT NULL,
  ok          INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS signin_attempts_window ON signin_attempts(name_key, at);

-- A finished battle.
--
-- Recorded before the experience is granted and keyed by an id the client cannot choose, so a
-- client that replays the same result twice is paid once. For PvP the id is the match's own,
-- which the room issues; for AI battles the server mints one.
CREATE TABLE IF NOT EXISTS battles (
  id          TEXT PRIMARY KEY,
  account_id  TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  mode        TEXT NOT NULL,
  won         INTEGER NOT NULL,
  at          INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS battles_account ON battles(account_id, at);

-- One story-mode save per account.
--
-- The payload is the save file VERBATIM — the same JSON `SaveSystem.Save` writes to disk,
-- stored as opaque text. The Worker deliberately does not parse it: the save shape belongs to
-- the game and changes with it, and a server that understood the shape would need redeploying
-- every time a field was added. What IS pulled out and indexed is only what a client needs to
-- decide whether a download is worth doing without downloading first.
--
-- Uploaded ONLY when the player presses 리포트 — the user's call, and it is what makes
-- last-write-wins honest here. There is no background sync racing a second device, so the
-- newest save is always one somebody deliberately made.
CREATE TABLE IF NOT EXISTS saves (
  account_id    TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
  payload       TEXT NOT NULL,
  version       INTEGER NOT NULL,
  trainer_name  TEXT NOT NULL DEFAULT '',
  play_time     REAL NOT NULL DEFAULT 0,
  saved_at      INTEGER NOT NULL,
  uploaded_at   INTEGER NOT NULL
);
