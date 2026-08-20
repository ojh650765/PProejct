import { authenticate } from "./accounts";
import { CONTRACT_VERSION, Env, fail, json, now } from "./env";

/**
 * The story-mode save, kept against the account.
 *
 * <b>Explicit only.</b> This is written when the player presses 리포트 in the trainer's menu
 * and at no other time — the user's decision (`저장save버튼을 눌러야만 저장시키면될듯`), and it
 * is what makes the conflict rule defensible. With no background sync there is no second device
 * quietly overwriting a session somebody is still playing; every row here was put there by a
 * person choosing to save, so **last write wins** is both the simplest rule and the least
 * surprising one. If autosave is ever added, this rule has to be revisited, not inherited.
 *
 * <b>The payload is opaque.</b> It is the exact JSON `SaveSystem.Save` writes to disk, stored as
 * text and never parsed here. The save shape is the game's and changes with it; a Worker that
 * understood it would need redeploying every time a field was added, and would be a second
 * place for the format to drift. The few columns that ARE extracted — version, trainer name,
 * play time, saved-at — exist only so a client can decide whether a download is worth making
 * without first making it.
 *
 * One row per account, replaced wholesale. There is no history: a save file is already the
 * player's single slot, and offering versions here would promise a rollback the game has no UI
 * for.
 */

/** Refuse anything absurd before it reaches the database. A real save is tens of kilobytes. */
const MAX_PAYLOAD_BYTES = 512 * 1024;

interface PutBody {
  version?: number;
  /** The save file, verbatim, as a JSON string. */
  payload?: string;
  /** Pulled from the save by the client so this Worker does not have to parse it. */
  saveVersion?: number;
  trainerName?: string;
  playTimeSeconds?: number;
  savedAtUtc?: string;
}

export async function handleSavePut(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  let body: PutBody | null = null;
  try {
    body = (await request.json()) as PutBody;
  } catch {
    return fail("bad_request");
  }
  if ((body.version ?? 0) !== CONTRACT_VERSION) return fail("version_mismatch");

  const payload = body.payload ?? "";
  if (!payload) return fail("empty_save");

  // Byte length, not string length: a Korean trainer name is three bytes per character and a
  // limit measured in UTF-16 units would let through nearly twice what it claims to.
  if (new TextEncoder().encode(payload).length > MAX_PAYLOAD_BYTES) return fail("save_too_large");

  // It has to at least BE JSON. The Worker does not care what is inside, but storing something
  // the game will fail to parse on the way back down turns a bad upload into a broken load
  // much later, on another device, with nothing to point at.
  try {
    JSON.parse(payload);
  } catch {
    return fail("bad_save");
  }

  const at = now();
  const savedAt = parseSavedAt(body.savedAtUtc, at);

  await env.DB.prepare(
    `INSERT INTO saves (account_id, payload, version, trainer_name, play_time, saved_at, uploaded_at)
     VALUES (?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT(account_id) DO UPDATE SET
       payload = excluded.payload,
       version = excluded.version,
       trainer_name = excluded.trainer_name,
       play_time = excluded.play_time,
       saved_at = excluded.saved_at,
       uploaded_at = excluded.uploaded_at`
  )
    .bind(
      account.id,
      payload,
      Math.max(0, Math.floor(body.saveVersion ?? 0)),
      (body.trainerName ?? "").slice(0, 32),
      Math.max(0, Number(body.playTimeSeconds) || 0),
      savedAt,
      at
    )
    .run();

  return json({ ok: true, savedAt, uploadedAt: at });
}

/**
 * The stored save, or `ok` with `hasSave: false`.
 *
 * A missing save is NOT an error — a player who has never pressed 리포트 on any device is in a
 * perfectly normal state, and returning 404 for it would make every client treat "new player"
 * as a failure to explain.
 */
export async function handleSaveGet(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  const row = await env.DB.prepare(
    `SELECT payload, version, trainer_name, play_time, saved_at, uploaded_at
     FROM saves WHERE account_id = ?`
  )
    .bind(account.id)
    .first<{
      payload: string;
      version: number;
      trainer_name: string;
      play_time: number;
      saved_at: number;
      uploaded_at: number;
    }>();

  if (!row) return json({ ok: true, hasSave: false });

  return json({
    ok: true,
    hasSave: true,
    payload: row.payload,
    saveVersion: row.version,
    trainerName: row.trainer_name,
    playTimeSeconds: row.play_time,
    savedAt: row.saved_at,
    uploadedAt: row.uploaded_at
  });
}

/**
 * Just the description, without the payload.
 *
 * This is what the title screen asks before deciding whether to offer a download: the payload
 * is tens of kilobytes and fetching it to read a timestamp would make opening a menu cost a
 * save file.
 */
export async function handleSaveInfo(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  const row = await env.DB.prepare(
    `SELECT version, trainer_name, play_time, saved_at, uploaded_at
     FROM saves WHERE account_id = ?`
  )
    .bind(account.id)
    .first<{
      version: number;
      trainer_name: string;
      play_time: number;
      saved_at: number;
      uploaded_at: number;
    }>();

  if (!row) return json({ ok: true, hasSave: false });

  return json({
    ok: true,
    hasSave: true,
    saveVersion: row.version,
    trainerName: row.trainer_name,
    playTimeSeconds: row.play_time,
    savedAt: row.saved_at,
    uploadedAt: row.uploaded_at
  });
}

export async function handleSaveDelete(request: Request, env: Env): Promise<Response> {
  const account = await authenticate(request, env);
  if (!account) return fail("unauthorised", 401);

  await env.DB.prepare(`DELETE FROM saves WHERE account_id = ?`).bind(account.id).run();
  return json({ ok: true });
}

/**
 * The save's own timestamp, in seconds, falling back to the upload time.
 *
 * The client sends `SavedAtUtc` straight out of the save file, which is a round-trip string
 * written by the game. A device with a wrong clock would otherwise poison the ordering, so an
 * unparseable or absurd value is replaced by the server's own time rather than trusted.
 */
function parseSavedAt(value: string | undefined, fallback: number): number {
  if (!value) return fallback;
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) return fallback;

  const seconds = Math.floor(parsed / 1000);
  // Before 2020, or more than a day in the future: the clock is wrong, not the save.
  if (seconds < 1_577_836_800 || seconds > fallback + 86_400) return fallback;
  return seconds;
}
