import { Account, authenticate } from "./accounts";
import { CONTRACT_VERSION, Env, fail, json, now } from "./env";
import { BY_RARITY, POOL, PoolEntry, RARITY_ORDER, Rarity, TIER_WEIGHT, rarityRank } from "./pool";

/**
 * Drawing a team, and reading back the one you drew.
 *
 * <b>The specification, in the user's words.</b> 소유 포켓몬은 갓챠로 뽑는거지 … 6번
 * 가챠돌리는거고, (중복없는거). 좋은 포켓몬일수록 가챠 확률 조절. Three rules, and each one is
 * enforced somewhere it cannot be skipped:
 *
 *  - <b>Six.</b> Clamped here, and the roster's primary key is (account, slot) with slot 0-5.
 *  - <b>No duplicates.</b> Drawn without replacement in this file, AND backed by a UNIQUE index
 *    on (account_id, species_id) in the schema. Two enforcements because they fail differently:
 *    the draw guarantees a good team, the constraint guarantees a correct database even if a
 *    future change to the draw forgets.
 *  - <b>Better is rarer.</b> The tier is derived from the base stat total once, in pool.ts, and
 *    the weights are per tier rather than per species — so adding a creature to the pool does
 *    not quietly move everyone else's odds.
 *
 * <b>Everything is decided here, never on the client.</b> Not because a single-player gacha
 * needs protecting from its own player, but because the same roster is what a PvP opponent
 * fights: a team the client could choose is a team that means nothing across the network.
 */

const TEAM_SIZE = 6;

/** Level every drawn creature starts at, and the experience floor that matches it. */
const START_LEVEL = 5;

/**
 * The client's curve, restated.
 *
 * `PokeLab.Battle.StatMath.ExperienceForLevel` is `level ** 3` and the engine levels up on
 * `Experience >= ExperienceForLevel(Level + 1)`. That formula is duplicated here rather than
 * shared, because the two run on different runtimes — and it is duplicated with this note, so
 * that whoever changes one has been told the other exists. A server and a client that disagree
 * about what level a creature is will disagree about who won.
 */
export function experienceForLevel(level: number): number {
  if (level <= 1) return 0;
  return Math.min(level, 100) ** 3;
}

export function levelForExperience(experience: number): number {
  let level = 1;
  while (level < 100 && experience >= experienceForLevel(level + 1)) level += 1;
  return level;
}

export interface RosterRow {
  slot: number;
  species_id: number;
  rarity: string;
  level: number;
  experience: number;
}

export async function rosterFor(env: Env, accountId: string): Promise<RosterRow[]> {
  const result = await env.DB.prepare(
    `SELECT slot, species_id, rarity, level, experience FROM roster WHERE account_id = ? ORDER BY slot`
  )
    .bind(accountId)
    .all<RosterRow>();

  return result.results ?? [];
}

function toWire(rows: RosterRow[]) {
  return rows.map((row) => ({
    speciesId: row.species_id,
    level: row.level,
    experience: row.experience,
    rarity: row.rarity,
    slot: row.slot
  }));
}

export async function handleRoster(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  const rows = await rosterFor(env, account.id);
  return json({ ok: true, roster: toWire(rows) });
}

interface RollBody {
  version?: number;
  pulls?: number;
  reroll?: boolean;
}

export async function handleRoll(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  let body: RollBody | null = null;
  try {
    body = (await request.json()) as RollBody;
  } catch {
    return fail("bad_request");
  }
  if ((body.version ?? 0) !== CONTRACT_VERSION) return fail("version_mismatch");

  const existing = await rosterFor(env, account.id);
  const reroll = body.reroll === true;

  if (existing.length >= TEAM_SIZE && !reroll) return fail("already_rolled");

  const wanted = Math.max(1, Math.min(TEAM_SIZE, Math.floor(body.pulls ?? TEAM_SIZE)));
  const pulls = draw(wanted);

  const at = now();
  const statements: D1PreparedStatement[] = [];

  // A reroll replaces the whole team rather than topping it up. Deleting first is what lets the
  // UNIQUE on species survive a redraw that happens to include a creature the old team had.
  if (reroll || existing.length > 0) {
    statements.push(env.DB.prepare(`DELETE FROM roster WHERE account_id = ?`).bind(account.id));
  }

  pulls.forEach((entry, slot) => {
    statements.push(
      env.DB
        .prepare(
          `INSERT INTO roster (account_id, slot, species_id, rarity, level, experience, drawn_at)
           VALUES (?, ?, ?, ?, ?, ?, ?)`
        )
        .bind(
          account.id,
          slot,
          entry.speciesId,
          entry.rarity,
          START_LEVEL,
          experienceForLevel(START_LEVEL),
          at
        )
    );
  });

  await env.DB.batch(statements);

  const roster = await rosterFor(env, account.id);

  return json({
    ok: true,
    pulls: pulls.map((entry) => ({
      speciesId: entry.speciesId,
      level: START_LEVEL,
      rarity: entry.rarity,
      rarityRank: rarityRank(entry.rarity)
    })),
    roster: toWire(roster)
  });
}

/**
 * Draws `count` distinct species, weighted by tier.
 *
 * Two-step rather than one weighted list over all 53: pick a TIER by weight, then a species
 * uniformly inside it. That is what makes "좋은 포켓몬일수록 확률 낮게" a statement about
 * rarity bands rather than about individual creatures, and it keeps the published odds stable
 * when the pool grows — adding three commons should not make every epic rarer.
 *
 * Without replacement, by removing the drawn species from the working copy of its tier. A tier
 * that empties mid-draw is dropped from the weighting rather than retried, which matters
 * because legendary holds only three species and a six-pull could in principle exhaust it.
 */
function draw(count: number): PoolEntry[] {
  const remaining: Record<Rarity, PoolEntry[]> = {
    common: [...BY_RARITY.common],
    uncommon: [...BY_RARITY.uncommon],
    rare: [...BY_RARITY.rare],
    epic: [...BY_RARITY.epic],
    legendary: [...BY_RARITY.legendary]
  };

  const picked: PoolEntry[] = [];

  for (let index = 0; index < count; index += 1) {
    const tiers = RARITY_ORDER.filter((rarity) => remaining[rarity].length > 0);
    if (tiers.length === 0) break;

    const total = tiers.reduce((sum, rarity) => sum + TIER_WEIGHT[rarity], 0);
    let roll = random() * total;

    let chosen: Rarity = tiers[tiers.length - 1];
    for (const rarity of tiers) {
      roll -= TIER_WEIGHT[rarity];
      if (roll <= 0) {
        chosen = rarity;
        break;
      }
    }

    const bucket = remaining[chosen];
    const at = Math.floor(random() * bucket.length);
    picked.push(bucket[at]);
    bucket.splice(at, 1);
  }

  return picked;
}

/**
 * A uniform float from the platform CSPRNG.
 *
 * `Math.random()` would do for fairness and would not do for trust: the odds on a gacha are the
 * one number players compare against what they actually got, and a generator whose sequence
 * could be reasoned about from a few observed rolls is not worth defending later.
 */
function random(): number {
  const bytes = crypto.getRandomValues(new Uint32Array(1));
  return bytes[0] / 4_294_967_296;
}

/** The published odds, so the client can show them without recomputing the weights. */
export function odds(): Array<{ rarity: Rarity; percent: number; species: number }> {
  const total = RARITY_ORDER.reduce((sum, rarity) => sum + TIER_WEIGHT[rarity], 0);
  return RARITY_ORDER.map((rarity) => ({
    rarity,
    percent: Math.round((TIER_WEIGHT[rarity] / total) * 1000) / 10,
    species: BY_RARITY[rarity].length
  }));
}

export function poolSize(): number {
  return POOL.length;
}
