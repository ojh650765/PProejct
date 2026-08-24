import { authenticate } from "./accounts";
import {
  MAX_PULLS_PER_ROLL,
  PULL_COST,
  START_LEVEL,
  levelCap,
  pick,
  random
} from "./economy";
import { CONTRACT_VERSION, Env, fail, json, now } from "./env";
import { BY_RARITY, POOL, PoolEntry, RARITY_ORDER, Rarity, TIER_WEIGHT, rarityRank } from "./pool";

/**
 * Drawing creatures, and reading back everything the account owns.
 *
 * <b>The specification, and how it changed.</b> It began as 소유 포켓몬은 갓챠로 뽑는거지 … 6번
 * 가챠돌리는거고, (중복없는거) — a team of exactly six, drawn once. The user has since asked for
 * a collection instead: 무조건 6개가 아니라, 1개도 뽑을 수도 있고 ~n개를 뽑기 가능한거지 …
 * 수집하고. Those are different games, and the difference lands here.
 *
 * What survived unchanged:
 *
 *  - <b>Better is rarer.</b> The tier is derived from the base stat total once, in pool.ts, and
 *    the weights are per tier rather than per species — so adding a creature to the pool does
 *    not quietly move everyone else's odds.
 *  - <b>One row per species.</b> Still a UNIQUE index, and now load-bearing for a second
 *    reason: a collection with two identical rows has two places to spend a candy and no answer
 *    for which one a party slot points at.
 *
 * What is new:
 *
 *  - <b>A pull is a pull.</b> One to ten at a time, priced in coins, with the first six owed to
 *    a new account so the front door is still free.
 *  - <b>A duplicate is material.</b> Drawing something already owned adds a shard to the row
 *    that exists rather than failing or being skipped. That is what 돌파 is paid for with, and
 *    it is what makes drawing a common you already have worth something.
 *  - <b>The party is chosen, not drawn.</b> Six of the collection fight; the rest sit on the
 *    bench. A pull that finds room in the party takes it, so a new account is battle-ready
 *    without visiting a team screen it has not been shown yet.
 *
 * <b>Everything is decided here, never on the client.</b> Not because a single-player gacha
 * needs protecting from its own player, but because the same creatures are what a PvP opponent
 * fights: a collection the client could choose is a collection that means nothing across the
 * network.
 */

const PARTY_SIZE = 6;

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

/**
 * The level a creature actually reaches, given what it has earned and what it has broken
 * through to.
 *
 * Experience above the ceiling is KEPT rather than discarded, so a 돌파 pays out immediately in
 * the levels the creature had already earned and could not take. Throwing it away would make
 * battling a capped creature pointless, which is the opposite of what a cap is for.
 */
export function cappedLevel(experience: number, stars: number): number {
  return Math.min(levelForExperience(experience), levelCap(stars));
}

export interface RosterRow {
  slot: number;
  species_id: number;
  rarity: string;
  level: number;
  experience: number;
  party_slot: number | null;
  stars: number;
  shards: number;
  moves: string;
}

const ROSTER_COLUMNS =
  `slot, species_id, rarity, level, experience, party_slot, stars, shards, moves`;

export async function rosterFor(env: Env, accountId: string): Promise<RosterRow[]> {
  const result = await env.DB.prepare(
    `SELECT ${ROSTER_COLUMNS} FROM roster WHERE account_id = ? ORDER BY slot`
  )
    .bind(accountId)
    .all<RosterRow>();

  return result.results ?? [];
}

/**
 * The six that fight, in party order.
 *
 * Falls back to the first six of the collection when nothing has been assigned — which is what
 * an account created before party_slot existed looks like if the migration has not run, and
 * what an account whose party was emptied looks like. A battle that refused to start because a
 * team screen had not been visited would be a wall in front of the only mode that pays.
 */
export function partyOf(rows: RosterRow[]): RosterRow[] {
  const assigned = rows
    .filter((row) => row.party_slot !== null && row.party_slot !== undefined)
    .sort((a, b) => (a.party_slot as number) - (b.party_slot as number));

  return assigned.length > 0 ? assigned.slice(0, PARTY_SIZE) : rows.slice(0, PARTY_SIZE);
}

export function toWire(rows: RosterRow[]) {
  return rows.map((row) => ({
    speciesId: row.species_id,
    level: row.level,
    experience: row.experience,
    rarity: row.rarity,
    slot: row.slot,
    partySlot: row.party_slot === null || row.party_slot === undefined ? -1 : row.party_slot,
    stars: row.stars ?? 0,
    shards: row.shards ?? 0,
    // JsonUtility on the client cannot deserialise a null string into a field it will then
    // compare — it produces "" either way, but only if the key is present. Sent as "" rather
    // than omitted so the two runtimes agree about what "no override" looks like.
    moves: row.moves ?? ""
  }));
}

export interface Purse {
  coins: number;
  free_pulls: number;
  rolls_used: number;
}

export async function purseFor(env: Env, accountId: string): Promise<Purse> {
  const row = await env.DB.prepare(
    `SELECT coins, free_pulls, rolls_used FROM accounts WHERE id = ?`
  )
    .bind(accountId)
    .first<Purse>();

  return {
    coins: row?.coins ?? 0,
    free_pulls: row?.free_pulls ?? 0,
    rolls_used: row?.rolls_used ?? 0
  };
}

export interface ItemRow {
  item_id: string;
  count: number;
}

export async function itemsFor(env: Env, accountId: string): Promise<ItemRow[]> {
  const result = await env.DB.prepare(
    `SELECT item_id, count FROM items WHERE account_id = ? AND count > 0 ORDER BY item_id`
  )
    .bind(accountId)
    .all<ItemRow>();

  return result.results ?? [];
}

export function itemsToWire(rows: ItemRow[]) {
  return rows.map((row) => ({ itemId: row.item_id, count: row.count }));
}

/**
 * Everything a screen needs to draw the account without asking twice.
 *
 * The collection, the purse and the bag come back together because every screen that wants one
 * wants all three: 내 포켓몬 prices 강화 against the coins, 돌파 against the shards on the row,
 * and 기술 against the discs in the bag. Three round trips to draw one list is three chances to
 * show a player a number that has already changed.
 */
export async function handleRoster(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  const [rows, purse, items] = await Promise.all([
    rosterFor(env, account.id),
    purseFor(env, account.id),
    itemsFor(env, account.id)
  ]);

  return json({
    ok: true,
    roster: toWire(rows),
    items: itemsToWire(items),
    coins: purse.coins,
    freePulls: purse.free_pulls,
    pullCost: PULL_COST,
    maxPulls: MAX_PULLS_PER_ROLL
  });
}

interface RollBody {
  version?: number;
  pulls?: number;
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

  const wanted = Math.max(1, Math.min(MAX_PULLS_PER_ROLL, Math.floor(body.pulls ?? 1)));

  const [existing, purse] = await Promise.all([
    rosterFor(env, account.id),
    purseFor(env, account.id)
  ]);

  // Free pulls are spent first and never partially: asking for ten with three free and no coins
  // is refused rather than quietly served as three. A gacha that hands back fewer than it was
  // asked for is a gacha the player has to audit.
  const freeUsed = Math.min(purse.free_pulls, wanted);
  const paidFor = wanted - freeUsed;
  const cost = paidFor * PULL_COST;
  if (cost > purse.coins) {
    return json(
      {
        ok: false,
        error: "not_enough_coins",
        coins: purse.coins,
        freePulls: purse.free_pulls,
        pullCost: PULL_COST
      },
      409
    );
  }

  // Which species already have a row, and where the collection's next index is. Read before the
  // draw because a duplicate is decided against what is owned NOW, not against what this batch
  // is about to insert -- the draw itself already refuses to repeat within one multi.
  const owned = new Map<number, RosterRow>();
  let nextSlot = 0;
  for (const row of existing) {
    owned.set(row.species_id, row);
    if (row.slot >= nextSlot) nextSlot = row.slot + 1;
  }

  const takenPartySlots = new Set<number>();
  for (const row of existing) {
    if (row.party_slot !== null && row.party_slot !== undefined) takenPartySlots.add(row.party_slot);
  }
  const freePartySlots: number[] = [];
  for (let i = 0; i < PARTY_SIZE; i += 1) if (!takenPartySlots.has(i)) freePartySlots.push(i);

  const drawn = draw(wanted);
  const at = now();
  const statements: D1PreparedStatement[] = [];

  const pulls: Array<{
    speciesId: number;
    level: number;
    rarity: string;
    rarityRank: number;
    duplicate: boolean;
    slot: number;
  }> = [];

  // Duplicates within ONE batch collapse onto the same row, so two copies of a species not yet
  // owned are one insert and one shard rather than two inserts that violate the UNIQUE.
  const addedThisRoll = new Map<number, number>();
  const shardsThisRoll = new Map<number, number>();

  for (const entry of drawn) {
    const existingRow = owned.get(entry.speciesId);
    const addedSlot = addedThisRoll.get(entry.speciesId);

    if (existingRow || addedSlot !== undefined) {
      const slot = existingRow ? existingRow.slot : (addedSlot as number);
      shardsThisRoll.set(entry.speciesId, (shardsThisRoll.get(entry.speciesId) ?? 0) + 1);
      pulls.push({
        speciesId: entry.speciesId,
        level: existingRow ? existingRow.level : START_LEVEL,
        rarity: entry.rarity,
        rarityRank: rarityRank(entry.rarity),
        duplicate: true,
        slot
      });
      continue;
    }

    const slot = nextSlot;
    nextSlot += 1;
    addedThisRoll.set(entry.speciesId, slot);

    // A new creature takes a free party slot if there is one. A brand new account therefore
    // walks out of its first six pulls with a party of six and never meets an empty team screen
    // it did not ask for; once the party is full, later pulls land on the bench.
    const partySlot = freePartySlots.length > 0 ? (freePartySlots.shift() as number) : null;

    statements.push(
      env.DB
        .prepare(
          `INSERT INTO roster
             (account_id, slot, species_id, rarity, level, experience, drawn_at, party_slot,
              stars, shards, moves)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0, 0, '')`
        )
        .bind(
          account.id,
          slot,
          entry.speciesId,
          entry.rarity,
          START_LEVEL,
          experienceForLevel(START_LEVEL),
          at,
          partySlot
        )
    );

    pulls.push({
      speciesId: entry.speciesId,
      level: START_LEVEL,
      rarity: entry.rarity,
      rarityRank: rarityRank(entry.rarity),
      duplicate: false,
      slot
    });
  }

  for (const [speciesId, count] of shardsThisRoll) {
    statements.push(
      env.DB
        .prepare(`UPDATE roster SET shards = shards + ? WHERE account_id = ? AND species_id = ?`)
        .bind(count, account.id, speciesId)
    );
  }

  // In the same batch as the draw. A roll that inserted creatures but failed to charge for
  // itself would hand back a free pull, which is the bug in the other direction; the guard
  // above already refused an unaffordable one, and this is what makes the two agree.
  statements.push(
    env.DB
      .prepare(
        `UPDATE accounts
            SET free_pulls = free_pulls - ?, coins = coins - ?, rolls_used = rolls_used + 1
          WHERE id = ?`
      )
      .bind(freeUsed, cost, account.id)
  );

  await env.DB.batch(statements);

  const [roster, after, items] = await Promise.all([
    rosterFor(env, account.id),
    purseFor(env, account.id),
    itemsFor(env, account.id)
  ]);

  return json({
    ok: true,
    pulls,
    roster: toWire(roster),
    items: itemsToWire(items),
    coins: after.coins,
    freePulls: after.free_pulls,
    pullCost: PULL_COST,
    maxPulls: MAX_PULLS_PER_ROLL,
    spent: cost
  });
}

/**
 * Draws `count` species, weighted by tier.
 *
 * Two-step rather than one weighted list over all 53: pick a TIER by weight, then a species
 * uniformly inside it. That is what makes "좋은 포켓몬일수록 확률 낮게" a statement about
 * rarity bands rather than about individual creatures, and it keeps the published odds stable
 * when the pool grows — adding three commons should not make every epic rarer.
 *
 * <b>No repeats within one multi, repeats against the collection allowed.</b> The second half
 * is the collection rule: pulling something you own is a shard, not a wasted pull. The first
 * half is a courtesy that matters most at the moment it applies — a new account's opening six,
 * which without it could be six of the same common and an unplayable team. A tier that empties
 * mid-draw is dropped from the weighting rather than retried, because legendary holds only
 * three species and a ten-pull would otherwise spin on it.
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

/** A species from the whole pool, for a drop that is not tied to what the player owns. */
export function anyPoolSpecies(): PoolEntry | null {
  return pick(POOL);
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
