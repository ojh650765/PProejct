/**
 * Hashing the recovery answer, and minting the tokens that stand in for it afterwards.
 *
 * <b>The threat this file is sized against.</b> The account is protected by the answer to a
 * fixed question — 태어난 곳, 어릴 적 별명 — and answers like that come from a space of a few
 * thousand plausible values, not the ~10^12 a chosen password spans. Two consequences follow,
 * and both are implemented here rather than assumed:
 *
 *  1. A fast hash is useless. SHA-256 over a few thousand candidates is instant, so the answer
 *     goes through PBKDF2 with a high iteration count and a per-account salt. That makes an
 *     offline attack on a stolen database expensive per guess instead of free.
 *  2. Hashing alone is still not enough, because the attacker does not need the database — they
 *     can guess against the live endpoint. That is why the rate limit in accounts.ts is not an
 *     optional nicety: it is the other half of this scheme, and removing it removes the
 *     security, not merely some of it.
 *
 * WebCrypto only. Workers have no Node crypto by default and do not need it: PBKDF2, SHA-256
 * and a CSPRNG are all in `crypto.subtle`.
 */

/**
 * 100,000 per round, twice — 200,000 effective.
 *
 * <b>Why not one call at 210,000.</b> That is OWASP's 2023 floor and it is what this used to
 * be, and Workers refuses it outright: <c>Pbkdf2 failed: iteration counts above 100000 are not
 * supported (requested 210000)</c>. Caught on the first live account creation, which returned
 * a 500 — the cap is a hard platform limit on <c>crypto.subtle.deriveBits</c>, not a warning.
 *
 * Dropping to a single 100,000-round call would have halved the work an attacker has to do per
 * guess. Chaining instead — derive 256 bits, then use those bits as the key material for a
 * second 100,000-round derivation over the same salt — costs the same total iterations as one
 * 200,000-round call would, stays under the per-call cap, and is a standard construction: the
 * output of round one is a high-entropy secret, so round two is strictly additional work with
 * no shortcut around it.
 *
 * The cost is charged once to a human pressing a button and 200,000 times per guess to somebody
 * working through a word list of birthplaces. That asymmetry is the entire defence, and it is
 * only half the story — see the rate limit in accounts.ts for the other half.
 */
const PBKDF2_ITERATIONS_PER_ROUND = 100_000;
const PBKDF2_ROUNDS = 2;
const KEY_LENGTH_BITS = 256;

/**
 * Folds the harmless differences out of an answer before it is hashed.
 *
 * "Seoul", "seoul" and " Seoul " are the same answer to a human and three different answers to
 * a hash, and a player who cannot sign in because they capitalised their birthplace differently
 * a month later has lost their account to a technicality. Trim, case-fold, and collapse runs of
 * whitespace — and no more than that: stripping punctuation or spaces entirely would shrink an
 * already small answer space.
 *
 * NFKC first, because Korean text arrives in more than one normalisation depending on the
 * platform's IME and two byte sequences that render identically must hash identically.
 */
export function normaliseAnswer(answer: string): string {
  return answer.normalize("NFKC").trim().toLowerCase().replace(/\s+/gu, " ");
}

/** The lookup key for a trainer name: same folding, so "Kes" and "kes" are one account. */
export function normaliseName(name: string): string {
  return name.normalize("NFKC").trim().toLowerCase();
}

export function randomSalt(): string {
  return toBase64Url(crypto.getRandomValues(new Uint8Array(16)));
}

/** 32 bytes of CSPRNG. Opaque to the client, and the only credential it stores. */
export function randomToken(): string {
  return toBase64Url(crypto.getRandomValues(new Uint8Array(32)));
}

export function randomId(): string {
  return toBase64Url(crypto.getRandomValues(new Uint8Array(12)));
}

export async function hashAnswer(answer: string, salt: string): Promise<string> {
  const saltBytes = fromBase64Url(salt);
  let digest = new TextEncoder().encode(normaliseAnswer(answer));

  for (let round = 0; round < PBKDF2_ROUNDS; round += 1) {
    const material = await crypto.subtle.importKey("raw", digest, "PBKDF2", false, ["deriveBits"]);
    const bits = await crypto.subtle.deriveBits(
      {
        name: "PBKDF2",
        hash: "SHA-256",
        salt: saltBytes,
        iterations: PBKDF2_ITERATIONS_PER_ROUND
      },
      material,
      KEY_LENGTH_BITS
    );
    digest = new Uint8Array(bits);
  }

  return toBase64Url(digest);
}

/**
 * Compares two hashes without leaking where they diverged.
 *
 * A plain `===` on strings returns as soon as it finds a difference, and the time that takes is
 * measurable across a network with enough samples. The hashes here are not secret enough for
 * that to be a realistic attack, but constant-time comparison of a credential costs four lines
 * and removes the question.
 */
export function timingSafeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let difference = 0;
  for (let index = 0; index < a.length; index += 1) {
    difference |= a.charCodeAt(index) ^ b.charCodeAt(index);
  }
  return difference === 0;
}

function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (let index = 0; index < bytes.length; index += 1) {
    binary += String.fromCharCode(bytes[index]);
  }
  return btoa(binary).replace(/\+/gu, "-").replace(/\//gu, "_").replace(/=+$/gu, "");
}

function fromBase64Url(value: string): Uint8Array {
  const base64 = value
    .replace(/-/gu, "+")
    .replace(/_/gu, "/")
    .padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}
