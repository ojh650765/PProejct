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
  last_seen_at   INTEGER NOT NULL,
  -- How many gacha rolls this account has spent. A statistic now rather than a limit: the
  -- gacha is gated by coins, not by a lifetime cap. Kept because it is the only record of how
  -- much an account has drawn, and dropping a column loses that for everyone at once.
  rolls_used     INTEGER NOT NULL DEFAULT 0,

  -- The purse. Battles pay into it -- win or lose, 대전에서 승리하거나 패배할때 코인 지급 --
  -- and the gacha, 강화 and 돌파 spend it. Server-side for the same reason everything else
  -- here is: it buys things that go into a PvP match.
  coins          INTEGER NOT NULL DEFAULT 0,

  -- Pulls owed rather than paid for. A new account gets six, because the first team should not
  -- be a wall at the front door; they are spendable one at a time rather than in one go, which
  -- is the whole of 무조건 6개가 아니라 1개도 뽑을 수도 있고.
  free_pulls     INTEGER NOT NULL DEFAULT 6
);

-- A device's claim to be an account. Deleted on sign-out, expired by TOKEN_TTL_SECONDS.
CREATE TABLE IF NOT EXISTS tokens (
  token       TEXT PRIMARY KEY,
  account_id  TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  issued_at   INTEGER NOT NULL,
  expires_at  INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS tokens_account ON tokens(account_id);

-- The collection, and the six of it that fight.
--
-- slot is the identity of a collected creature for the whole of its life: the client reports
-- experience against a slot, growth is applied to a slot, and the UNIQUE below is what makes
-- "one row per species" a property of the database rather than of the roll that happened to
-- produce it. It used to be 0-5 and mean "position in the team"; it is now an ever-growing
-- collection index, and party_slot carries the meaning it gave up.
--
-- ONE ROW PER SPECIES IS KEPT DELIBERATELY, now that duplicates are drawable. A collection
-- holding two identical rows has two places to spend a candy and no answer for which one a
-- party slot points at. So a duplicate pull is not a second row: it is `shards` on the row
-- that already exists, which is what 돌파 is then paid for with.
CREATE TABLE IF NOT EXISTS roster (
  account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  slot         INTEGER NOT NULL,
  species_id   INTEGER NOT NULL,
  rarity       TEXT NOT NULL,
  level        INTEGER NOT NULL DEFAULT 5,
  experience   INTEGER NOT NULL DEFAULT 125,
  drawn_at     INTEGER NOT NULL,
  -- 0-5 for a party member, NULL for one on the bench.
  party_slot   INTEGER,
  -- 돌파: 0-5. Raises the level cap and the stat bonus together.
  stars        INTEGER NOT NULL DEFAULT 0,
  -- Duplicate pulls, kept as breakthrough material for this species.
  shards       INTEGER NOT NULL DEFAULT 0,
  -- The four moves, comma separated. Empty means "whatever the level-up learnset gives at this
  -- level" and is what every creature starts as; written the first time a disc is taught.
  moves        TEXT NOT NULL DEFAULT '',
  PRIMARY KEY (account_id, slot)
);
-- One species per account: the no-duplicates rule, enforced where it cannot be forgotten.
CREATE UNIQUE INDEX IF NOT EXISTS roster_unique_species ON roster(account_id, species_id);
-- The party is read on every battle entry and on every roster fetch.
CREATE INDEX IF NOT EXISTS roster_party ON roster(account_id, party_slot);

-- What the account is carrying.
--
-- One row per KIND rather than per item, because nothing distinguishes two copies of the same
-- disc. item_id is 'candy' or 'disc:<moveId>', and the move id is the string moves.json keys
-- on -- so a disc means the same thing on both runtimes with no mapping table between them.
CREATE TABLE IF NOT EXISTS items (
  account_id  TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  item_id     TEXT NOT NULL,
  count       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (account_id, item_id)
);

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
