import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { build } from 'esbuild';

const temp = mkdtempSync(join(tmpdir(), 'pokelab-gacha-'));
await build({ entryPoints: ['src/index.ts'], bundle: true, platform: 'node', format: 'esm', outfile: join(temp, 'worker.mjs') });
const { default: worker } = await import(pathToFileURL(join(temp, 'worker.mjs')));
process.on('exit', () => rmSync(temp, { recursive: true, force: true }));

// Exercise the actual SQL, constraints and rollback with SQLite, behind the D1 API shape.
function fixture({ coins = 0, used = 0 } = {}) {
  const db = new DatabaseSync(':memory:');
  db.exec(readFileSync('schema.sql', 'utf8'));
  db.prepare("INSERT INTO accounts(id,name,name_key,question_id,answer_hash,answer_salt,created_at,last_seen_at,coins,rolls_used) VALUES ('a','Test','test','q','h','s',0,0,?,?)").run(coins, used);
  db.exec("INSERT INTO tokens VALUES ('test-token','a',0,9999999999999)");
  const prepare = sql => ({
    values: [], bind(...values) { this.values = values; return this; },
    exec() { const q = db.prepare(sql); return q.columns().length ? { results: q.all(...this.values), success: true } : { results: [], meta: q.run(...this.values), success: true }; },
    async first() { return db.prepare(sql).get(...this.values) ?? null; },
    async all() { return this.exec(); }, async run() { return this.exec(); }
  });
  const env = { DB: { prepare, async batch(steps) { db.exec('BEGIN'); try { const result = steps.map(x => x.exec()); db.exec('COMMIT'); return result; } catch(e) { db.exec('ROLLBACK'); throw e; } } }, MATCH: {} };
  async function call(path, body) {
    const response = await worker.fetch(new Request('http://localhost' + path, { method: body === undefined ? 'GET' : 'POST', headers: { authorization: 'Bearer test-token', 'content-type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify({ version: 1, ...body }) }), env, {});
    return { status: response.status, ...await response.json() };
  }
  return { db, call };
}

test('five free groups survive retries; choosing one persists exactly six without spending PP', async () => {
  const { call, db } = fixture();
  const initial = await call('/roster'); assert.equal(initial.starterEligible, true);
  const [first, retry] = await Promise.all([call('/gacha/starter', {}), call('/gacha/starter', {})]);
  assert.equal(first.starterGroups.length, 5);
  assert.deepEqual(first.starterGroups, retry.starterGroups);
  for (const group of first.starterGroups) assert.equal(new Set(group.pulls.map(x=>x.speciesId)).size, 6);
  assert.equal(first.roster.length, 0);
  const chosen = await call('/gacha/starter', { selected: 2 });
  assert.equal(chosen.ok, true); assert.equal(chosen.coins, 0);
  assert.deepEqual(chosen.roster.map(x=>x.speciesId), first.starterGroups[2].pulls.map(x=>x.speciesId));
  assert.equal(chosen.starterEligible, false);
  assert.equal((await call('/gacha/starter', { selected: 2 })).ok, true);
  assert.equal((await call('/gacha/starter', { selected: 1 })).status, 409);
  assert.equal(db.prepare('SELECT rolls_used FROM accounts').get().rolls_used, 5);
  assert.equal((await call('/roster')).starterSelected, 2);
});

test('concurrent choices award only one group', async () => {
  const { call } = fixture(); await call('/gacha/starter', {});
  const answers = await Promise.all([call('/gacha/starter', { selected: 0 }), call('/gacha/starter', { selected: 4 })]);
  assert.equal(answers.filter(x=>x.ok).length, 1);
  assert.equal((await call('/roster')).roster.length, 6);
});

test('returning account uses PP; duplicate request charges once; insufficient balance awards nothing', async () => {
  const { call } = fixture({ coins: 1000, used: 1 });
  assert.equal((await call('/gacha/starter', {})).status, 409);
  const body = { pulls: 2, requestId: 'repeat-request-0001' };
  const [a,b] = await Promise.all([call('/gacha/roll', body), call('/gacha/roll', body)]);
  assert.equal(a.ok,true); assert.equal(b.ok,true); assert.deepEqual(a.pulls,b.pulls);
  assert.equal(a.spent,1000); assert.equal((await call('/roster')).coins,0);
  const before = await call('/roster');
  assert.equal((await call('/gacha/roll', { pulls: 1, requestId: 'different-request-02' })).error,'not_enough_coins');
  assert.deepEqual((await call('/roster')).roster,before.roster);
});

test('competing purchases cannot overspend; malformed counts and first-time paid bypass are refused', async () => {
  const { call } = fixture({ coins: 500, used: 1 });
  const results = await Promise.all([call('/gacha/roll', { pulls: 1, requestId:'competing-request-01' }),call('/gacha/roll', { pulls: 1, requestId:'competing-request-02' })]);
  assert.equal(results.filter(x=>x.ok).length,1);
  assert.equal((await call('/roster')).coins,0);
  for (const pulls of [0, -1, 1.5, 11, '6', null])
    assert.equal((await call('/gacha/roll', { pulls, requestId:'invalid-request-0001' })).error,'bad_count');
  const fresh = fixture({ coins: 500 });
  assert.equal((await fresh.call('/gacha/roll', { pulls:1, requestId:'first-time-request-1' })).error,'choose_starter_first');
});

test('existing battle rewards feed the same PP balance spent by gacha', async () => {
  const { call, db } = fixture({ used: 1 });
  db.exec("INSERT INTO roster(account_id,slot,species_id,rarity,level,experience,drawn_at,party_slot) VALUES ('a',0,1,'common',5,125,0,0)");
  const win = await call('/battle/result', { mode:'ai', won:true });
  assert.equal(win.coinsGained,140); assert.equal(win.coins,140);
  const loss = await call('/battle/result', { mode:'ai', won:false });
  assert.equal(loss.coinsGained,45); assert.equal(loss.coins,185);
  const pvp = await call('/battle/result', { mode:'pvp', matchId:'test-match', won:true });
  assert.equal(pvp.coinsGained,252); assert.equal(pvp.coins,437);
  assert.equal((await call('/battle/result', { mode:'pvp', matchId:'test-match', won:true })).error,'already_recorded');
  assert.equal((await call('/roster')).coins,437);
});
