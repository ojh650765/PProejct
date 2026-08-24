import { authenticate } from "./accounts";
import {
  BREAKTHROUGH_COINS,
  BREAKTHROUGH_SHARDS,
  CANDY_ID,
  CANDY_LEVELS,
  MAX_STARS,
  PULL_COST,
  discId,
  enhanceCost,
  levelCap
} from "./economy";
import { CONTRACT_VERSION, Env, fail, json } from "./env";
import {
  RosterRow,
  cappedLevel,
  experienceForLevel,
  itemsFor,
  itemsToWire,
  purseFor,
  rosterFor,
  toWire
} from "./gacha";
import { canLearn, defaultMoves } from "./learnsets";

/**
 * 내 포켓몬: everything that makes a collected creature stronger.
 *
 * <b>What the user asked for, in their words.</b> 내 포켓몬 이라는 메뉴가 존재했으면 좋겠음.
 * 강화하고 돌파하고, 대전해서 … 기술디스크 … 이런걸 가끔식 획득가능하게 한다음에. 그걸 내 포켓몬
 * 이라는 메뉴에서 가르치는거지. 그리고 이상한 사탕 아이템도 멕이고. Four verbs, one screen, and
 * each verb is a route here:
 *
 *  - <b>강화</b> buys experience with coins. The most ordinary thing on the screen, and the
 *    reason coins from a lost battle are worth anything.
 *  - <b>돌파</b> spends the duplicates the gacha produces to raise the level ceiling. This is
 *    what makes pulling a common you already own feel like something rather than nothing.
 *  - <b>기술</b> teaches a disc — and refuses, loudly, where the species cannot learn it. The
 *    user asked for that refusal specifically: 막 모든 포켓몬이 막 모든 디스크를 배울 수 있다 X.
 *  - <b>사탕</b> is a level in an item, for the creature you want strong right now.
 *
 * <b>Why the server owns all four.</b> Everything here changes what a creature is, and a
 * creature is what a PvP opponent has to fight. A level, a star or a move the client could
 * grant itself is a number that means nothing across a match — the same argument that put the
 * roster and the moveset here, applied to the things that change them.
 *
 * <b>Every route answers with the whole account.</b> The collection, the purse and the bag come
 * back from each of these, because every one of them moves at least two of the three and a
 * screen that had to re-fetch would be drawing numbers that are already stale.
 */

const PARTY_SIZE = 6;
const MOVE_SLOTS = 4;

/** The shape every route here replies with, so one client path can read all of them. */
async function accountState(env: Env, accountId: string, extra: Record<string, unknown> = {}) {
  const [roster, purse, items] = await Promise.all([
    rosterFor(env, accountId),
    purseFor(env, accountId),
    itemsFor(env, accountId)
  ]);

  return json({
    ok: true,
    roster: toWire(roster),
    items: itemsToWire(items),
    coins: purse.coins,
    freePulls: purse.free_pulls,
    pullCost: PULL_COST,
    ...extra
  });
}

interface SlotBody {
  version?: number;
  slot?: number;
}

/** Reads and checks the envelope every route here shares. Returns the row, or a Response. */
async function resolve(
  request: Request,
  env: Env,
  accountId: string
): Promise<{ row: RosterRow; body: any } | Response> {
  let body: any = null;
  try {
    body = await request.json();
  } catch {
    return fail("bad_request");
  }
  if ((body?.version ?? 0) !== CONTRACT_VERSION) return fail("version_mismatch");

  const slot = Math.floor((body as SlotBody).slot ?? -1);
  if (!Number.isInteger(slot) || slot < 0) return fail("bad_slot");

  const row = await env.DB.prepare(
    `SELECT slot, species_id, rarity, level, experience, party_slot, stars, shards, moves
       FROM roster WHERE account_id = ? AND slot = ?`
  )
    .bind(accountId, slot)
    .first<RosterRow>();

  if (!row) return fail("no_such_creature");
  return { row, body };
}

// --- 강화 -----------------------------------------------------------------------------------

/**
 * Buys one level's worth of experience.
 *
 * Granted as EXPERIENCE rather than as a level, so that a creature enhanced to the ceiling and
 * one battled to the ceiling are in exactly the same state — and so a 돌파 afterwards pays out
 * the same either way. Two ways of gaining a level that leave different rows behind is how a
 * progression system starts disagreeing with itself.
 */
export async function handleEnhance(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  const found = await resolve(request, env, account.id);
  if (found instanceof Response) return found;
  const { row } = found;

  const cap = levelCap(row.stars ?? 0);
  if (row.level >= cap) return json({ ok: false, error: "at_level_cap", cap }, 409);

  const purse = await purseFor(env, account.id);
  const cost = enhanceCost(row.level);
  if (purse.coins < cost) {
    return json({ ok: false, error: "not_enough_coins", coins: purse.coins, cost }, 409);
  }

  // One whole level at the current level, so the button does what it says for as long as the
  // curve allows. Past that it is still progress, just no longer one press per level.
  const granted = Math.max(1, experienceForLevel(row.level + 1) - experienceForLevel(row.level));
  const experience = row.experience + granted;
  const level = cappedLevel(experience, row.stars ?? 0);
  const settled = Math.max(experience, experienceForLevel(level));

  await env.DB.batch([
    env.DB
      .prepare(`UPDATE roster SET level = ?, experience = ? WHERE account_id = ? AND slot = ?`)
      .bind(level, settled, account.id, row.slot),
    env.DB.prepare(`UPDATE accounts SET coins = coins - ? WHERE id = ?`).bind(cost, account.id)
  ]);

  return accountState(env, account.id, {
    slot: row.slot,
    spent: cost,
    levelsGained: Math.max(0, level - row.level)
  });
}

// --- 돌파 -----------------------------------------------------------------------------------

/**
 * Raises the level ceiling by one star, paid for in duplicates.
 *
 * The shards come from the gacha and nowhere else, which is the point: a collection game needs
 * the thing you already own to be worth pulling again, and this is the only place that is true.
 */
export async function handleBreakthrough(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  const found = await resolve(request, env, account.id);
  if (found instanceof Response) return found;
  const { row } = found;

  const stars = row.stars ?? 0;
  if (stars >= MAX_STARS) return json({ ok: false, error: "at_max_stars" }, 409);

  const shardsNeeded = BREAKTHROUGH_SHARDS[stars];
  const coinsNeeded = BREAKTHROUGH_COINS[stars];
  if ((row.shards ?? 0) < shardsNeeded) {
    return json(
      { ok: false, error: "not_enough_shards", have: row.shards ?? 0, need: shardsNeeded },
      409
    );
  }

  const purse = await purseFor(env, account.id);
  if (purse.coins < coinsNeeded) {
    return json({ ok: false, error: "not_enough_coins", coins: purse.coins, cost: coinsNeeded }, 409);
  }

  // The ceiling moves, so the experience already banked above the old one becomes levels in the
  // same statement. That is why a capped creature is still worth battling.
  const level = cappedLevel(row.experience, stars + 1);

  await env.DB.batch([
    env.DB
      .prepare(
        `UPDATE roster SET stars = ?, shards = shards - ?, level = ?
          WHERE account_id = ? AND slot = ?`
      )
      .bind(stars + 1, shardsNeeded, level, account.id, row.slot),
    env.DB.prepare(`UPDATE accounts SET coins = coins - ? WHERE id = ?`).bind(coinsNeeded, account.id)
  ]);

  return accountState(env, account.id, {
    slot: row.slot,
    stars: stars + 1,
    levelsGained: Math.max(0, level - row.level)
  });
}

// --- 이상한 사탕 ----------------------------------------------------------------------------

/** A level in an item. Refuses at the ceiling rather than eating the candy for nothing. */
export async function handleCandy(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  const found = await resolve(request, env, account.id);
  if (found instanceof Response) return found;
  const { row } = found;

  const cap = levelCap(row.stars ?? 0);
  if (row.level >= cap) return json({ ok: false, error: "at_level_cap", cap }, 409);

  const held = await env.DB.prepare(`SELECT count FROM items WHERE account_id = ? AND item_id = ?`)
    .bind(account.id, CANDY_ID)
    .first<{ count: number }>();
  if (!held || held.count <= 0) return json({ ok: false, error: "no_candy" }, 409);

  const target = Math.min(cap, row.level + CANDY_LEVELS);
  const experience = Math.max(row.experience, experienceForLevel(target));

  await env.DB.batch([
    env.DB
      .prepare(`UPDATE roster SET level = ?, experience = ? WHERE account_id = ? AND slot = ?`)
      .bind(target, experience, account.id, row.slot),
    env.DB
      .prepare(`UPDATE items SET count = count - 1 WHERE account_id = ? AND item_id = ? AND count > 0`)
      .bind(account.id, CANDY_ID)
  ]);

  return accountState(env, account.id, {
    slot: row.slot,
    levelsGained: Math.max(0, target - row.level)
  });
}

// --- 기술 디스크 ----------------------------------------------------------------------------

interface TeachBody extends SlotBody {
  moveSlot?: number;
  moveId?: string;
}

/**
 * Teaches a disc into one of the four move slots.
 *
 * <b>The refusal is the feature.</b> 막 모든 포켓몬이 막 모든 디스크를 배울 수 있다 X — so the
 * learnset is consulted before anything is spent, and a species that cannot learn the move is
 * told so with the disc still in the bag. Checked here rather than only on the client for the
 * usual reason: a taught move walks into a PvP match.
 *
 * The four slots are materialised on the first teach. Until then `moves` is empty and both
 * runtimes derive the same four from the level-up learnset; writing them down at this point is
 * what lets one of them be replaced without the other three moving.
 */
export async function handleTeach(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  const found = await resolve(request, env, account.id);
  if (found instanceof Response) return found;
  const { row, body } = found;

  const teach = body as TeachBody;
  const moveId = (teach.moveId ?? "").trim();
  if (!moveId) return fail("bad_move");

  const moveSlot = Math.floor(teach.moveSlot ?? -1);
  if (!Number.isInteger(moveSlot) || moveSlot < 0 || moveSlot >= MOVE_SLOTS) {
    return fail("bad_move_slot");
  }

  // The rule, before anything is spent.
  if (!canLearn(row.species_id, moveId)) {
    return json({ ok: false, error: "cannot_learn", speciesId: row.species_id, moveId }, 409);
  }

  const item = discId(moveId);
  const held = await env.DB.prepare(`SELECT count FROM items WHERE account_id = ? AND item_id = ?`)
    .bind(account.id, item)
    .first<{ count: number }>();
  if (!held || held.count <= 0) return json({ ok: false, error: "no_disc", moveId }, 409);

  const current =
    row.moves && row.moves.length > 0
      ? row.moves.split(",").filter((id) => id.length > 0)
      : defaultMoves(row.species_id, row.level);

  const slots: string[] = [];
  for (let i = 0; i < MOVE_SLOTS; i += 1) slots.push(current[i] ?? "");

  // Already there, in a different slot: SWAPPED, not duplicated.
  //
  // Blanking the old slot instead would be the obvious thing and it is wrong: it costs the
  // player a move slot for nothing, because what they asked for was a reorder. Four copies of
  // the same move would be worse still -- a moveset the battle engine would happily run and
  // nobody meant to make.
  const already = slots.indexOf(moveId);
  if (already === moveSlot) return json({ ok: false, error: "already_known", moveId }, 409);
  if (already >= 0) slots[already] = slots[moveSlot];

  slots[moveSlot] = moveId;

  await env.DB.batch([
    env.DB
      .prepare(`UPDATE roster SET moves = ? WHERE account_id = ? AND slot = ?`)
      .bind(slots.join(","), account.id, row.slot),
    env.DB
      .prepare(`UPDATE items SET count = count - 1 WHERE account_id = ? AND item_id = ? AND count > 0`)
      .bind(account.id, item)
  ]);

  return accountState(env, account.id, { slot: row.slot, moveId, moveSlot });
}

// --- The party ------------------------------------------------------------------------------

interface PartyBody {
  version?: number;
  /** Collection slots, in party order. Up to six; anything not named goes to the bench. */
  slots?: number[];
}

/**
 * Sets which six of the collection fight.
 *
 * Written as one batch that clears every assignment first, because a partial update is how two
 * creatures end up claiming party slot 3 and the battle builds a team of five.
 */
export async function handleSetParty(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  let body: PartyBody | null = null;
  try {
    body = (await request.json()) as PartyBody;
  } catch {
    return fail("bad_request");
  }
  if ((body.version ?? 0) !== CONTRACT_VERSION) return fail("version_mismatch");

  const owned = await rosterFor(env, account.id);
  const ownedSlots = new Set(owned.map((row) => row.slot));

  const wanted: number[] = [];
  for (const raw of body.slots ?? []) {
    const slot = Math.floor(raw);
    if (!Number.isInteger(slot) || !ownedSlots.has(slot)) continue;
    if (wanted.includes(slot)) continue;
    wanted.push(slot);
    if (wanted.length >= PARTY_SIZE) break;
  }

  // A party of nobody would leave every battle entrance refusing to open, so it is rejected
  // here rather than discovered at the arena door.
  if (wanted.length === 0) return fail("empty_party");

  const statements: D1PreparedStatement[] = [
    env.DB
      .prepare(`UPDATE roster SET party_slot = NULL WHERE account_id = ?`)
      .bind(account.id)
  ];

  wanted.forEach((slot, index) => {
    statements.push(
      env.DB
        .prepare(`UPDATE roster SET party_slot = ? WHERE account_id = ? AND slot = ?`)
        .bind(index, account.id, slot)
    );
  });

  await env.DB.batch(statements);

  return accountState(env, account.id);
}
