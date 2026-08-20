# Poké Lab Online

The Cloudflare Worker behind accounts, the gacha, experience, and PvP matchmaking.

Modelled on the layout of the `ttk` Worker (flat router in `src/index.ts`, one module per
concern, `assertEnv` at the top of `fetch`, WebCrypto for secrets) — but on **D1** rather than
Supabase. See the note in `wrangler.toml` for why.

## What it owns

| Route | Method | Purpose |
| --- | --- | --- |
| `/health` | GET | Liveness, pool size and the published odds. |
| `/odds` | GET | The gacha odds on their own. |
| `/account/create` | POST | Trainer name + recovery question + answer → token. |
| `/account/login` | POST | The same three → token. |
| `/roster` | GET | The six the account owns. |
| `/gacha/roll` | POST | Draw a team. Six pulls, no duplicates, weighted by tier. |
| `/battle/result` | POST | Grant experience for a finished AI or PvP battle. |
| `/pvp/queue` | WS | Matchmaking. Returns a match id and a shared seed. |
| `/pvp/match/{id}` | WS | The match room. Relays turns between the two players. |

## Deploying

You need a Cloudflare account and to be logged in (`npx wrangler login`).

```sh
cd Server/pokelab-online
npm install

# 1. Create the database. This prints a database_id.
npx wrangler d1 create pokelab

# 2. Paste that id into wrangler.toml, replacing REPLACE_WITH_D1_DATABASE_ID.

# 3. Create the tables.
npm run db:apply

# 4. Ship it.
npm run deploy
```

`wrangler deploy` prints the Worker's URL, something like
`https://pokelab-online.<your-subdomain>.workers.dev`. **Paste that into the game**: title
screen → 계정 → 서버 주소. It is kept in `PlayerPrefs`, so it is entered once per device.

Check it before doing anything else:

```sh
curl https://pokelab-online.<your-subdomain>.workers.dev/health
```

That returns the pool size (53) and the odds table, and it needs no account.

### Local

```sh
npm run db:apply:local
npm run dev          # http://localhost:8787
```

Point the game at `http://localhost:8787` the same way. Note the editor and a desktop player
can reach `localhost`; a WebGL build served from GitHub Pages cannot.

## The gacha

`src/pool.ts` is **generated**, from `Assets/StreamingAssets/pokelab/species.json` intersected
with `Assets/Game/Art/Sprites/Creatures/sprite_manifest.json`. The pool is the 53 species that
have artwork, not the 721 in the dex — a gacha that can hand out a creature the client cannot
draw hands out a blank rectangle.

Tiers come from the base stat total, once, at generation time:

| Rarity | BST | Species | Odds |
| --- | --- | --- | --- |
| common | < 320 | 26 | 55 % |
| uncommon | 320–399 | 14 | 27 % |
| rare | 400–479 | 7 | 12 % |
| epic | 480–499 | 3 | 4.5 % |
| legendary | ≥ 500 | 3 | 1.5 % |

A tier is picked by weight, then a species uniformly within it — so adding three commons to the
pool does not quietly make every epic rarer. Draws are without replacement, and the
`roster_unique_species` index in `schema.sql` enforces "no duplicates" a second time, where it
cannot be forgotten by a future change to the draw.

## Accounts, and what this scheme is worth

There is no password. An account is a trainer name plus the answer to one of six fixed recovery
questions — the user's design, and the reasoning is sound for a game: a password is more
ceremony than this is worth, and most players would reuse one anyway.

It is **weaker than a password**, and the code is built around admitting that:

- The answer is normalised (NFKC, trimmed, case-folded, whitespace collapsed) and hashed with
  **PBKDF2-HMAC-SHA256, 2 x 100 000 iterations (200 000 effective), per-account salt**. A stolen
  database does not yield answers cheaply. It is two chained rounds rather than one long one
  because Workers hard-caps `deriveBits` at 100 000 iterations per call — asking for more fails
  the request outright (`Pbkdf2 failed: iteration counts above 100000 are not supported`).
- Attempts are **rate limited per trainer name** — 8 failures in 15 minutes and the name cools
  down. This is not hygiene; it is the other half of the security argument. An answer drawn
  from a few thousand plausible values is safe only while nobody can try a few thousand of
  them. Removing the limit removes the security, not merely some of it.
- The comparison is constant-time, and the question must match as well as the answer.
- The player is told, on the account screen, not to reuse an answer they use anywhere else.

Nothing outside this game should ever be put behind it.

## PvP, and what it does not do

The match room owns the match's identity, the seed both clients simulate from, and who is
player 0. It does **not** simulate the battle: both clients run the same deterministic engine
from the same seed and exchange their chosen moves.

That is lockstep, and lockstep trusts each client not to lie about its own choice. A modified
client could cheat a PvP battle today. What it cannot do is invent a team — the roster is read
from D1, never from the socket — or pay itself experience, which is settled against the match
id on the HTTP side and recorded once. Treat this as a friendly-match protocol until the battle
engine is ported to the Worker.

## Experience

Both modes pay, every time, which is the requirement (`대전/ai대전 할때마다 경험치 증가`).

```
gained = 220 × mode × result × participation × catchUp
         mode:          ai 1.0, pvp 1.75
         result:        win 1.0, loss 0.35
         participation: fought 1.0, fainted 0.5
         catchUp:       max(0.6, 1.6 − level × 0.05)
```

The level curve is `level³`, duplicated from `PokeLab.Battle.StatMath.ExperienceForLevel` on the
client.
It is duplicated rather than shared because the two run on different runtimes — **if you change
one, change the other**, or the server and the client will disagree about what level a creature
is, and therefore about who won.
