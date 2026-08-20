import {
  CONTRACT_VERSION,
  Env,
  fail,
  json,
  now,
  signinAttemptLimit,
  signinAttemptWindowSeconds,
  tokenTtlSeconds
} from "./env";
import {
  hashAnswer,
  normaliseAnswer,
  normaliseName,
  randomId,
  randomSalt,
  randomToken,
  timingSafeEqual
} from "./crypto";
import { rosterFor } from "./gacha";

/**
 * The fixed recovery questions.
 *
 * Duplicated from the client's `SecurityQuestions` deliberately rather than served from here.
 * The id is what is stored against the account, so a question that changed its id would lock
 * every account created under the old one out of its own recovery — and a list fetched at
 * runtime is a list that can be edited without anyone noticing that consequence. Two copies
 * that must be kept in step by hand is the cheaper failure: it is caught the first time
 * anybody tries to sign in.
 */
const QUESTION_IDS = new Set([
  "birthplace",
  "memory",
  "nickname",
  "pet",
  "food",
  "school"
]);

interface AuthBody {
  version?: number;
  trainerName?: string;
  questionId?: string;
  answer?: string;
}

export interface Account {
  id: string;
  name: string;
  question_id: string;
  answer_hash: string;
  answer_salt: string;
}

/**
 * Creates an account.
 *
 * The name is claimed by the UNIQUE on `name_key` rather than by a SELECT-then-INSERT, because
 * two players typing the same name at the same moment is exactly the race a check-then-act
 * loses. The constraint violation is caught and reported as `name_taken`, which is what it is.
 */
export async function handleCreate(request: Request, env: Env): Promise<Response> {
  const body = await readBody(request);
  if (!body) return fail("bad_request");
  if ((body.version ?? 0) !== CONTRACT_VERSION) return fail("version_mismatch");

  const name = (body.trainerName ?? "").trim();
  const nameKey = normaliseName(name);
  const questionId = body.questionId ?? "";
  const answer = body.answer ?? "";

  if (name.length < 2 || name.length > 16) return fail("bad_name");
  if (!QUESTION_IDS.has(questionId)) return fail("bad_question");
  // One character is enough, and the two-character floor was a bug rather than a policy.
  // The questions are answered in Korean, where a single syllable is a whole word -- 집, 산,
  // 개 are all complete answers to "where were you born" or "what was your first pet". A
  // player who typed one and was told 질문을 고르고 답을 입력해 주세요 had done both.
  if (normaliseAnswer(answer).length < 1) return fail("bad_answer");

  const salt = randomSalt();
  const hash = await hashAnswer(answer, salt);
  const id = randomId();
  const at = now();

  try {
    await env.DB.prepare(
      `INSERT INTO accounts (id, name, name_key, question_id, answer_hash, answer_salt, created_at, last_seen_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`
    )
      .bind(id, name, nameKey, questionId, hash, salt, at, at)
      .run();
  } catch (error) {
    const message = error instanceof Error ? error.message : "";
    if (message.includes("UNIQUE")) return fail("name_taken");
    throw error;
  }

  const token = await issueToken(env, id);
  return json({
    ok: true,
    accountId: id,
    token,
    trainerName: name,
    needsGacha: true
  });
}

/**
 * Signs an existing account in.
 *
 * <b>The rate limit is load-bearing, not hygiene.</b> Everything that makes a recovery answer
 * acceptable as a credential assumes an attacker cannot try thousands of them. The window is
 * counted per trainer name — not per IP, which is trivially rotated — so the account itself is
 * what cools down.
 *
 * <b>Every failure below returns the same shape.</b> A wrong name and a wrong answer both come
 * back as their own code because this is a game and telling a player "no account with that
 * name" is genuinely more useful than a vague refusal; what is NOT distinguished is how close
 * the answer was, and the comparison is constant-time so the timing does not say either.
 */
export async function handleLogin(request: Request, env: Env): Promise<Response> {
  const body = await readBody(request);
  if (!body) return fail("bad_request");
  if ((body.version ?? 0) !== CONTRACT_VERSION) return fail("version_mismatch");

  const nameKey = normaliseName(body.trainerName ?? "");
  const questionId = body.questionId ?? "";
  const answer = body.answer ?? "";

  if (!nameKey) return fail("bad_name");

  if (await isRateLimited(env, nameKey)) return fail("rate_limited", 429);

  const account = await env.DB.prepare(
    `SELECT id, name, question_id, answer_hash, answer_salt FROM accounts WHERE name_key = ?`
  )
    .bind(nameKey)
    .first<Account>();

  if (!account) {
    await recordAttempt(env, nameKey, false);
    return fail("no_account", 404);
  }

  // The question has to match as well as the answer. Without this, a player could pick the
  // question with the answer they happened to guess rather than the one they registered.
  if (account.question_id !== questionId) {
    await recordAttempt(env, nameKey, false);
    return fail("wrong_answer", 403);
  }

  const candidate = await hashAnswer(answer, account.answer_salt);
  if (!timingSafeEqual(candidate, account.answer_hash)) {
    await recordAttempt(env, nameKey, false);
    return fail("wrong_answer", 403);
  }

  await recordAttempt(env, nameKey, true);
  await env.DB.prepare(`UPDATE accounts SET last_seen_at = ? WHERE id = ?`)
    .bind(now(), account.id)
    .run();

  const token = await issueToken(env, account.id);
  const roster = await rosterFor(env, account.id);

  return json({
    ok: true,
    accountId: account.id,
    token,
    trainerName: account.name,
    needsGacha: roster.length === 0
  });
}

/**
 * The account behind a bearer token, or null.
 *
 * Expiry is checked in the query rather than after it, so an expired row can never be treated
 * as a live one by a caller that forgot to look.
 */
export async function authenticate(request: Request, env: Env): Promise<Account | null> {
  const header = request.headers.get("authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice("Bearer ".length).trim() : "";
  if (!token) return null;

  const row = await env.DB.prepare(
    `SELECT a.id, a.name, a.question_id, a.answer_hash, a.answer_salt
     FROM tokens t JOIN accounts a ON a.id = t.account_id
     WHERE t.token = ? AND t.expires_at > ?`
  )
    .bind(token, now())
    .first<Account>();

  return row ?? null;
}

async function issueToken(env: Env, accountId: string): Promise<string> {
  const token = randomToken();
  const issued = now();
  const expires = issued + tokenTtlSeconds(env);

  await env.DB.batch([
    // Expired rows for this account are swept on the way past. There is no cron for it because
    // this is the only place tokens accumulate and it is visited every time they do.
    env.DB.prepare(`DELETE FROM tokens WHERE account_id = ? AND expires_at <= ?`).bind(accountId, issued),
    env.DB.prepare(`INSERT INTO tokens (token, account_id, issued_at, expires_at) VALUES (?, ?, ?, ?)`)
      .bind(token, accountId, issued, expires)
  ]);

  return token;
}

async function isRateLimited(env: Env, nameKey: string): Promise<boolean> {
  const since = now() - signinAttemptWindowSeconds(env);
  const row = await env.DB.prepare(
    `SELECT COUNT(*) AS failures FROM signin_attempts WHERE name_key = ? AND at > ? AND ok = 0`
  )
    .bind(nameKey, since)
    .first<{ failures: number }>();

  return (row?.failures ?? 0) >= signinAttemptLimit(env);
}

async function recordAttempt(env: Env, nameKey: string, ok: boolean): Promise<void> {
  const at = now();
  const sweepBefore = at - signinAttemptWindowSeconds(env) * 4;

  await env.DB.batch([
    env.DB.prepare(`INSERT INTO signin_attempts (name_key, at, ok) VALUES (?, ?, ?)`)
      .bind(nameKey, at, ok ? 1 : 0),
    // A success clears the account's failures, so a player who fat-fingered their answer twice
    // and then got it right is not still one attempt from a lockout.
    ok
      ? env.DB.prepare(`DELETE FROM signin_attempts WHERE name_key = ? AND ok = 0`).bind(nameKey)
      : env.DB.prepare(`DELETE FROM signin_attempts WHERE at < ?`).bind(sweepBefore)
  ]);
}

async function readBody(request: Request): Promise<AuthBody | null> {
  try {
    return (await request.json()) as AuthBody;
  } catch {
    return null;
  }
}
