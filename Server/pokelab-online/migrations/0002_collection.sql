-- One-shot: turn the six-slot roster into a collection, and give an account a purse.
--
-- schema.sql stays re-appliable (every statement in it is IF NOT EXISTS) and SQLite has no
-- ADD COLUMN IF NOT EXISTS, so the column additions live here. Running this twice is expected
-- to fail with "duplicate column name" -- that failure means the column is already present.
--
--   npx wrangler d1 execute pokelab --remote --file migrations/0002_collection.sql
--
-- WHAT CHANGES, AND WHY IT IS SAFE FOR AN ACCOUNT THAT ALREADY EXISTS
--
-- `roster.slot` used to mean "position in the team of six" and now means "position in the
-- collection", which is the same number for every row that exists today: an account created
-- under the old rules holds exactly six rows at slots 0-5. The UPDATE at the bottom copies
-- those into party_slot, so a returning player finds the same six standing in the same order
-- and nothing about their team moved.

-- The purse. Battles pay into it and the gacha spends it.
ALTER TABLE accounts ADD COLUMN coins INTEGER NOT NULL DEFAULT 0;

-- Pulls owed rather than paid for.
--
-- A new account still gets its team of six for nothing -- that is the first thing anybody does
-- and charging for it would be a wall at the front door. What changed is that the six are no
-- longer taken in one go: they are six free pulls, spendable one at a time, which is the whole
-- of "무조건 6개가 아니라 1개도 뽑을 수 있고". Existing accounts get 0 because they have
-- already drawn.
ALTER TABLE accounts ADD COLUMN free_pulls INTEGER NOT NULL DEFAULT 0;

-- Which six go into battle. NULL means "collected, on the bench".
--
-- Nullable rather than a separate table because it is at most one small integer per row and
-- the question asked of it -- "who is in the party" -- is asked in the same query that reads
-- the roster.
ALTER TABLE roster ADD COLUMN party_slot INTEGER;

-- 돌파. 0-5, and the level cap and the stat bonus both move with it.
ALTER TABLE roster ADD COLUMN stars INTEGER NOT NULL DEFAULT 0;

-- Duplicate pulls of a species already owned, kept as breakthrough material.
--
-- The UNIQUE index on (account_id, species_id) is what makes this necessary and is deliberately
-- being kept: a collection with two identical rows has two places to spend a candy and no
-- answer for which one a party slot points at. So a duplicate pull is not a row, it is a
-- number on the row that already exists.
ALTER TABLE roster ADD COLUMN shards INTEGER NOT NULL DEFAULT 0;

-- The creature's four moves, comma separated, or empty for "whatever the level-up learnset
-- gives at this level". Written only once a disc has been taught.
ALTER TABLE roster ADD COLUMN moves TEXT NOT NULL DEFAULT '';

-- What the account is carrying: rare candies and move discs.
--
-- One row per kind rather than one row per item, because nothing distinguishes two copies of
-- the same disc. item_id is 'candy' or 'disc:<moveId>'; the move id is the same string
-- moves.json keys on, so a disc means the same thing on both runtimes without a mapping table.
CREATE TABLE IF NOT EXISTS items (
  account_id  TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  item_id     TEXT NOT NULL,
  count       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (account_id, item_id)
);

-- The six an existing account already had become that account's party, in the order they were
-- drawn in. Runs last so it cannot leave party_slot half-written if a column above failed.
UPDATE roster SET party_slot = slot WHERE slot < 6 AND party_slot IS NULL;
