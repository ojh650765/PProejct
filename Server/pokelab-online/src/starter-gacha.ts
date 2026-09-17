import { authenticate } from "./accounts";
import { CONTRACT_VERSION, Env, fail, json, now } from "./env";
import { draw, handleRoster } from "./gacha";
import { rarityRank } from "./pool";

interface StarterRow { groups_json: string; selected: number }

export async function starterState(env: Env, accountId: string) {
  const row = await env.DB.prepare("SELECT groups_json, selected FROM starter_gacha WHERE account_id = ?")
    .bind(accountId).first<StarterRow>();
  const eligible = row ? row.selected < 0 : !!await env.DB.prepare(
    `SELECT id FROM accounts WHERE id = ? AND rolls_used = 0
      AND NOT EXISTS (SELECT 1 FROM roster WHERE account_id = accounts.id)`
  ).bind(accountId).first();
  return { starterEligible: eligible, starterGroups: row ? JSON.parse(row.groups_json) : [], starterSelected: row?.selected ?? -1 };
}

export async function handleStarter(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);
  const body = await request.json().catch(() => null) as { version?: number; selected?: number } | null;
  if (!body || body.version !== CONTRACT_VERSION) return fail("version_mismatch");
  let state = await starterState(env, account.id);
  if (body.selected === undefined) {
    if (!state.starterEligible && state.starterGroups.length === 0) return fail("already_rolled", 409);
    if (state.starterGroups.length === 0) {
      const groups = Array.from({ length: 5 }, () => ({ pulls: draw(6).map((entry, slot) => ({
        speciesId: entry.speciesId, rarity: entry.rarity, rarityRank: rarityRank(entry.rarity), level: 5, slot
      })) }));
      // Competing requests all read the first saved draw. Closing the UI cannot reroll it.
      await env.DB.prepare(`INSERT OR IGNORE INTO starter_gacha(account_id, groups_json, selected)
        SELECT id, ?, -1 FROM accounts WHERE id = ? AND rolls_used = 0
          AND NOT EXISTS (SELECT 1 FROM roster WHERE account_id = accounts.id)`)
        .bind(JSON.stringify(groups), account.id).run();
    }
    return handleRoster(request, env);
  }
  const index = body.selected;
  if (!Number.isInteger(index) || index < 0 || index >= 5 || state.starterGroups.length !== 5)
    return fail("bad_selection");
  if (state.starterSelected >= 0)
    return state.starterSelected === index ? handleRoster(request, env) : fail("starter_already_selected", 409);
  const statements: D1PreparedStatement[] = [];
  const group = state.starterGroups[index];
  for (const [slot, entry] of group.pulls.entries()) {
    statements.push(env.DB.prepare(`INSERT INTO roster
      (account_id, slot, species_id, rarity, level, experience, drawn_at, party_slot, stars, shards, moves)
      SELECT account_id, ?, ?, ?, 5, 125, ?, ?, 0, 0, '' FROM starter_gacha
      WHERE account_id = ? AND selected = -1`)
      .bind(slot, entry.speciesId, entry.rarity, now(), slot, account.id));
  }
  statements.push(env.DB.prepare(`UPDATE accounts SET free_pulls = 0, rolls_used = rolls_used + 5
    WHERE id = ? AND EXISTS (SELECT 1 FROM starter_gacha WHERE account_id = ? AND selected = -1)`)
    .bind(account.id, account.id));
  statements.push(env.DB.prepare("UPDATE starter_gacha SET selected = ? WHERE account_id = ? AND selected = -1")
    .bind(index, account.id));
  await env.DB.batch(statements);
  state = await starterState(env, account.id);
  if (state.starterSelected !== index) return fail("starter_already_selected", 409);
  return handleRoster(request, env);
}
