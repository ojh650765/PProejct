import { authenticate } from "./accounts";
import { CONTRACT_VERSION, Env, fail, json, now } from "./env";
import { experienceForLevel, levelForExperience, rosterFor } from "./gacha";
import { randomId } from "./crypto";

/**
 * What a finished battle is worth.
 *
 * <b>The rule.</b> 포켓몬 레벨업은 대전/ai대전 할때마다 경험치 증가 — both modes pay, every
 * time. What differs is how much: a win over another trainer is worth more than a win over the
 * computer, and a loss still pays something, because a mode whose losses are worth nothing is a
 * mode people stop entering.
 *
 * <b>The client proposes nothing.</b> It says which mode, whether it won, and which slots took
 * part. Every number below is computed here. That is not paranoia about a single-player AI
 * battle; it is that the same roster is the one a PvP opponent faces, so a level the client
 * could choose is a level that means nothing in a match.
 *
 * <b>Paid once.</b> The battle id is written before the experience is granted, in the same
 * batch, and its primary key is what makes a replayed result a no-op rather than a second
 * payout. For PvP the id comes from the room, so both players' reports settle against the same
 * match; for AI the server mints one, because there is nothing on the client worth trusting to
 * be unique.
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

  // Nobody named means the whole team was in it. That is the honest reading for a client that
  // did not track participation, and it is bounded by the team size either way.
  const involved =
    participants.length > 0
      ? participants
      : roster.map((row) => ({ slot: row.slot, fainted: false }));

  const multiplier = (MODE_MULTIPLIER[mode] ?? 1) * (won ? 1 : LOSS_FRACTION);

  const statements: D1PreparedStatement[] = [];
  const gains: Array<{
    slot: number;
    speciesId: number;
    experienceGained: number;
    experience: number;
    level: number;
    levelsGained: number;
  }> = [];

  // Written first, and with INSERT OR IGNORE: if this id already exists the row count is zero,
  // and the whole batch below still runs — so the guard is checked BEFORE the batch instead.
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
    // catches up instead of being permanently behind the rest of the six.
    const catchUp = Math.max(0.6, 1.6 - row.level * 0.05);
    const share = part.fainted ? FAINTED_FRACTION : 1;
    const gained = Math.max(1, Math.round(BASE_EXPERIENCE * multiplier * share * catchUp));

    const experience = row.experience + gained;
    const level = levelForExperience(experience);
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
      levelsGained
    });
  }

  await env.DB.batch(statements);

  return json({ ok: true, gains });
}
