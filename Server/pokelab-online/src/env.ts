/**
 * What the Worker is given, and the one place that checks it is all there.
 *
 * Modelled on the reference project's `assertEnv`, and for the same reason: a binding that is
 * missing shows up as `undefined.prepare is not a function` from whichever request happened to
 * touch it first, which is a stack trace about D1 rather than about the deploy that forgot the
 * database. Checked once at the top of `fetch`, so a misconfigured Worker says so in one line.
 */
export interface Env {
  DB: D1Database;
  MATCH: DurableObjectNamespace;

  TOKEN_TTL_SECONDS: string;
  SIGNIN_ATTEMPT_LIMIT: string;
  SIGNIN_ATTEMPT_WINDOW_SECONDS: string;
}

export function assertEnv(env: Env): void {
  if (!env.DB) {
    throw new Error(
      "The D1 binding 'DB' is missing. Run `wrangler d1 create pokelab`, put the id in " +
        "wrangler.toml, then `npm run db:apply`."
    );
  }
  if (!env.MATCH) {
    throw new Error("The Durable Object binding 'MATCH' is missing from wrangler.toml.");
  }
}

export function tokenTtlSeconds(env: Env): number {
  return positive(env.TOKEN_TTL_SECONDS, 60 * 60 * 24 * 14);
}

export function signinAttemptLimit(env: Env): number {
  return positive(env.SIGNIN_ATTEMPT_LIMIT, 8);
}

export function signinAttemptWindowSeconds(env: Env): number {
  return positive(env.SIGNIN_ATTEMPT_WINDOW_SECONDS, 900);
}

/**
 * A var is a string or it is absent, and `Number("")` is 0 -- which as a token lifetime means
 * every token is already expired. Falling back on anything non-positive rather than only on
 * NaN is the difference between a typo costing a warning and costing every sign-in.
 */
function positive(value: string | undefined, fallback: number): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

/** The contract version both sides carry. A mismatch is refused rather than half-understood. */
export const CONTRACT_VERSION = 1;

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      // The game is served from GitHub Pages and the Worker from workers.dev, so every call
      // the browser build makes is cross-origin. Without this the WebGL build fails at the
      // preflight with an error that never reaches the game's own error handling.
      "access-control-allow-origin": "*",
      "access-control-allow-headers": "content-type, authorization",
      "access-control-allow-methods": "GET, POST, OPTIONS"
    }
  });
}

export function fail(error: string, status = 400): Response {
  return json({ ok: false, error }, status);
}

export function now(): number {
  return Math.floor(Date.now() / 1000);
}
