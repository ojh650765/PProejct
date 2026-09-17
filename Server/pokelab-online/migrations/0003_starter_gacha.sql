-- Five saved candidate teams; only the chosen six enter the collection.
CREATE TABLE IF NOT EXISTS starter_gacha (
  account_id TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
  groups_json TEXT NOT NULL,
  selected INTEGER NOT NULL DEFAULT -1 CHECK(selected BETWEEN -1 AND 4)
);
-- A saved receipt prevents a network retry charging for the same roll twice.
CREATE TABLE IF NOT EXISTS gacha_receipts (
  account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  request_id TEXT NOT NULL,
  pulls_json TEXT NOT NULL,
  spent INTEGER NOT NULL,
  accepted INTEGER NOT NULL CHECK(accepted = 1),
  PRIMARY KEY(account_id, request_id)
);
