import { authenticate } from "./accounts";
import {
  CANDY_ID,
  COINS_LOSS,
  COINS_WIN,
  COIN_MODE_MULTIPLIER,
  DROP_CANDY_SHARE,
  DROP_CHANCE_LOSS,
  DROP_CHANCE_WIN,
  DROP_DISC_FROM_OWNED,
  discId,
  levelCap,
  pick,
  random
} from "./economy";
import { CONTRACT_VERSION, Env, fail, json, now } from "./env";
import {
  RosterRow,
  cappedLevel,
  experienceForLevel,
  itemsFor,
  itemsToWire,
  partyOf,
  purseFor,
  rosterFor
} from "./gacha";
import { LEARNSETS, TEACHABLE_MOVES } from "./learnsets";
import { randomId } from "./crypto";

/**
 * What a finished battle is worth.
 *
 * <b>The rule.</b> 포켓몬 레벨업은 대전/ai대전 할때마다 경험치 증가, and now beside it
 * 대전에서 승리하거나 패배할때 코인 지급 and 대전해서 … 기술디스크? 이런걸 가끔식 획득가능하게.
 * Three payouts from one report: experience, coins, and sometimes an item. All three pay on a
 * loss as well as a win, at a lower rate — a mode whose losses are worth nothing is a mode
 * people stop entering, and the player who most needs the coins is the one who keeps losing.
 *
 * <b>The client proposes nothing.</b> It says which mode, whether it won, and which slots took
 * part. Every number below is computed here. That is not paranoia about a single-player AI
 * battle; it is that the same collection is what a PvP opponent faces, so a level or a disc the
 * client could choose is a level or a disc that means nothing in a match.
 *
 * <b>Paid once.</b> The battle id is written before anything is granted, in the same batch, and
 * its primary key is what makes a replayed result a no-op rather than a second payout. For PvP
 * the id comes from the room, so both players' reports settle against the same match; for AI
 * the server mints one, because there is nothing on the client worth trusting to be unique.
 */

/** Base experience for taking part at all, before the mode and result multipliers. */
const BASE_EXPERIENCE = 220;

const MODE_MULTIPLIER: Record<string, number> = {
  ai: 1,
  pvp: 1.75
};

/** A loss still pays this fraction. Enough to matter, not enough to make losing efficient. */
const LOSS_FRACTION = 0.35;

/** A creature that fainted did take part, and is paid less rather than nothing. */
const FAINTED_FRACTION = 0.5;

interface Participant {
  slot?: number;
  fainted?: boolean;
}

interface ResultBody {
  version?: number;
  mode?: string;
  won?: boolean;
  matchId?: string;
  participants?: Participant[];
}

export async function handleBattleResult(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  let body: ResultBody | null = null;
  try {
    body = (await request.json()) as ResultBody;
  } catch {
    return fail("bad_request");
  }
  if ((body.version ?? 0) !== CONTRACT_VERSION) return fail("version_mismatch");

  const mode = body.mode === "pvp" ? "pvp" : "ai";
  const won = body.won === true;

  // A PvP result has to name its match. Without that there is nothing tying the two players'
  // reports together, and "pvp" becomes a free 1.75x multiplier any client can ask for.
  const matchId = (body.matchId ?? "").trim();
  if (mode === "pvp" && !matchId) return fail("no_match");

  const battleId = mode === "pvp" ? `${matchId}:${account.id}` : `ai:${randomId()}`;

  const roster = await rosterFor(env, account.id);
  if (roster.length === 0) return fail("no_team");

  const participants = (body.participants ?? []).filter(
    (entry): entry is Required<Participant> =>
      typeof entry?.slot === "number" && Number.isInteger(entry.slot)
  );

  // Nobody named means the PARTY was in it — not the whole collection, which is the change a
  // collection forces. A client that did not track participation must not pay experience into
  // fifty benched creatures for a fight six of them had.
  const party = partyOf(roster);
  const involved =
    participants.length > 0
      ? participants
      : party.map((row) => ({ slot: row.slot, fainted: false }));

  const multiplier = (MODE_MULTIPLIER[mode] ?? 1) * (won ? 1 : LOSS_FRACTION);

  const statements: D1PreparedStatement[] = [];
  const gains: Array<{
    slot: number;
    speciesId: number;
    experienceGained: number;
    experience: number;
    level: number;
    levelsGained: number;
    capped: boolean;
  }> = [];

  // Checked BEFORE the batch: `INSERT OR IGNORE` would report zero changed rows while the rest
  // of the batch still ran, so the guard has to be its own read.
  const already = await env.DB.prepare(`SELECT id FROM battles WHERE id = ?`)
    .bind(battleId)
    .first<{ id: string }>();
  if (already) return fail("already_recorded");

  statements.push(
    env.DB
      .prepare(`INSERT INTO battles (id, account_id, mode, won, at) VALUES (?, ?, ?, ?, ?)`)
      .bind(battleId, account.id, mode, won ? 1 : 0, now())
  );

  for (const row of roster) {
    const part = involved.find((entry) => entry.slot === row.slot);
    if (!part) continue;

    // Lower-level creatures gain faster, so a team member drawn late or benched for a while
    // catches up instead of being permanently behind the rest.
    const catchUp = Math.max(0.6, 1.6 - row.level * 0.05);
    const share = part.fainted ? FAINTED_FRACTION : 1;
    const gained = Math.max(1, Math.round(BASE_EXPERIENCE * multiplier * share * catchUp));

    const experience = row.experience + gained;

    // The ceiling is 돌파's job, and experience above it is KEPT rather than discarded: the
    // moment the player breaks through, the levels they had already earned arrive with it.
    // Throwing it away would make battling a capped creature pointless, which is the opposite
    // of what a cap is for.
    const level = cappedLevel(experience, row.stars ?? 0);
    const levelsGained = Math.max(0, level - row.level);

    // The floor moves with the level for the same reason CreatureFactory.GrantLevels moves it:
    // the bar draws `Experience - ExperienceForLevel(Level)` and goes negative without it.
    const settled = Math.max(experience, experienceForLevel(level));

    statements.push(
      env.DB
        .prepare(`UPDATE roster SET level = ?, experience = ? WHERE account_id = ? AND slot = ?`)
        .bind(level, settled, account.id, row.slot)
    );

    gains.push({
      slot: row.slot,
      speciesId: row.species_id,
      experienceGained: gained,
      experience: settled,
      level,
      levelsGained,
      // True when this creature is sitting on its ceiling and the experience it just earned is
      // banked rather than spent. The client says so on the summary, because otherwise a run of
      // battles that visibly change nothing reads as the report being lost.
      capped: level >= levelCap(row.stars ?? 0)
    });
  }

  // --- Coins ------------------------------------------------------------------------------

  const coinsGained = Math.max(
    1,
    Math.round((won ? COINS_WIN : COINS_LOSS) * (COIN_MODE_MULTIPLIER[mode] ?? 1))
  );
  statements.push(
    env.DB.prepare(`UPDATE accounts SET coins = coins + ? WHERE id = ?`).bind(coinsGained, account.id)
  );

  // --- Drops ------------------------------------------------------------------------------

  const drops = rollDrops(roster, won);
  for (const itemId of drops) {
    statements.push(
      env.DB
        .prepare(
          `INSERT INTO items (account_id, item_id, count) VALUES (?, ?, 1)
             ON CONFLICT(account_id, item_id) DO UPDATE SET count = count + 1`
        )
        .bind(account.id, itemId)
    );
  }

  await env.DB.batch(statements);

  const [after, items] = await Promise.all([purseFor(env, account.id), itemsFor(env, account.id)]);

  return json({
    ok: true,
    gains,
    coinsGained,
    coins: after.coins,
    drops,
    items: itemsToWire(items)
  });
}

/**
 * What, if anything, the battle handed over.
 *
 * At most one item, and most of the time none: "가끔식 획득가능하게" is doing real work in that
 * sentence, and an item every battle is a chore rather than a reward.
 *
 * A dropped disc is usually drawn from the learnset of something the player actually owns,
 * because a disc nobody can learn is not a reward — it is a note saying "not for you". The rest
 * come from the whole pool, and those are the ones that make a disc worth keeping for a
 * creature not drawn yet.
 */
function rollDrops(roster: RosterRow[], won: boolean): string[] {
  const chance = won ? DROP_CHANCE_WIN : DROP_CHANCE_LOSS;
  if (random() >= chance) return [];

  if (random() < DROP_CANDY_SHARE) return [CANDY_ID];

  if (random() < DROP_DISC_FROM_OWNED) {
    const owner = pick(roster);
    const learnable = owner ? LEARNSETS[owner.species_id] : undefined;
    if (learnable && learnable.length > 0) {
      const entry = pick(learnable);
      if (entry) return [discId(entry[0])];
    }
  }

  const any = pick(TEACHABLE_MOVES);
  return any ? [discId(any)] : [CANDY_ID];
}
