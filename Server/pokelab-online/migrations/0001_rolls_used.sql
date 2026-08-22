-- One-shot: add rolls_used to a database created before schema.sql carried it.
--
-- schema.sql is written to be re-appliable and every statement in it is IF NOT EXISTS. SQLite
-- has no ADD COLUMN IF NOT EXISTS, so putting this there would have broken that promise the
-- second time anyone ran it. It lives here instead, and running it twice is expected to fail
-- with "duplicate column name" -- that failure means the column is already present.
--
--   npx wrangler d1 execute pokelab --remote --file migrations/0001_rolls_used.sql
ALTER TABLE accounts ADD COLUMN rolls_used INTEGER NOT NULL DEFAULT 0;
