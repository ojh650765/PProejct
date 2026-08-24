/**
 * Every number the game's economy is made of, in one file.
 *
 * <b>Why they are together.</b> A growth game is a set of exchange rates -- what a battle is
 * worth, what a pull costs, how much a level costs, how far a 돌파 gets you -- and those rates
 * are only meaningful relative to each other. Scattered across four handlers they drift: coins
 * get raised in battle.ts to make a mode feel better and the gacha silently becomes free.
 * Here, changing one is done while looking at the rest.
 *
 * <b>Why they are on the server.</b> Same reason as the roster and the moveset. A collection
 * that the client could price is a collection that means nothing when it walks into a PvP
 * match.
 */

// --- Coins ---------------------------------------------------------------------------------

/**
 * What a finished battle pays, before the mode multiplier.
 *
 * The user's rule: 대전에서 승리하거나 패배할때 코인 지급 -- BOTH, and that is the load-bearing
 * half. A loss that pays nothing makes losing feel like the game was taken away from you, and
 * the player who most needs the coins is the one who keeps losing. It pays less, not nothing.
 */
export const COINS_WIN = 140;
export const COINS_LOSS = 45;

/** PvP is worth more, exactly as it is for experience. A real opponent is the harder thing. */
export const COIN_MODE_MULTIPLIER: Record<string, number> = {
  ai: 1,
  pvp: 1.8
};

// --- The gacha -----------------------------------------------------------------------------

/**
 * Coins for one pull.
 *
 * Set against COINS_WIN so that roughly four AI wins buy a pull. That is deliberately on the
 * generous side: this is a collection game for one player and a small circle, not a storefront,
 * and a rate that makes the collection feel reachable is worth more here than one that makes it
 * feel earned.
 */
export const PULL_COST = 500;

/** Most that may be drawn in one request. Ten is the standard multi; the client offers 1/5/10. */
export const MAX_PULLS_PER_ROLL = 10;

/** Level every drawn creature starts at. */
export const START_LEVEL = 5;

// --- Growth --------------------------------------------------------------------------------

/**
 * 강화: what one press of the button costs, and what it grants.
 *
 * Priced per level rather than flat, so that levelling a creature that is already strong costs
 * what it should and a newly drawn one catches up cheaply. The experience granted is a whole
 * level's worth at the creature's current level, so one press is one level until the curve
 * outgrows it -- which reads honestly on a button labelled 강화.
 */
export function enhanceCost(level: number): number {
  return Math.max(40, Math.round(18 * level + 0.9 * level * level));
}

/**
 * 돌파: shards for the next star, and the coins beside them.
 *
 * Duplicates are the gate. The coin cost exists so that a player sitting on shards still has a
 * reason to battle, not because the shards alone are too cheap.
 */
export const BREAKTHROUGH_SHARDS = [2, 4, 8, 14, 24];
export const BREAKTHROUGH_COINS = [400, 900, 1800, 3200, 5400];

/** Stars a creature can hold. 0 is a fresh pull; 5 is the ceiling. */
export const MAX_STARS = 5;

/**
 * The level ceiling at a given star count: 40, 52, 64, 76, 88, 100.
 *
 * A cap is what gives 돌파 a job -- without one there is nothing to break through and the
 * duplicates a gacha produces have no use at all. 40 at zero stars is above anything an
 * account reached under the old uncapped rules, so nobody's existing creature is standing over
 * its own ceiling the moment this ships.
 */
export function levelCap(stars: number): number {
  return Math.min(100, 40 + 12 * Math.max(0, Math.min(MAX_STARS, stars)));
}

/**
 * What a star is worth in the fight: +4% to every stat per star, at 5 stars +20%.
 *
 * Applied on the client by CreatureFactory, from the `stars` on the wire -- which is why stars
 * have to travel with a PvP opponent's roster. Two clients that disagree about a stat bonus
 * disagree about who won.
 */
export function starMultiplier(stars: number): number {
  return 1 + 0.04 * Math.max(0, Math.min(MAX_STARS, stars));
}

// --- Drops ---------------------------------------------------------------------------------

/**
 * The chance a battle hands over an item at all, and what kind.
 *
 * "가끔식 획득가능하게" -- occasionally, and the word is doing work. An item every battle is a
 * chore rather than a reward. Measured against the rest of the economy: a win pays COINS_WIN and
 * a pull costs PULL_COST, so a pull is a bit under four wins; at a third of wins dropping, and
 * two thirds of drops being discs, a disc arrives about every fifth win. Often enough to be
 * worth playing for, rare enough to be worth getting. A loss still drops sometimes, for the
 * same reason a loss still pays coins.
 */
export const DROP_CHANCE_WIN = 0.32;
export const DROP_CHANCE_LOSS = 0.12;

/** Of the drops that happen, how many are a rare candy rather than a move disc. */
export const DROP_CANDY_SHARE = 0.35;

/**
 * How often a dropped disc is drawn from a species the player actually owns, rather than from
 * the whole pool.
 *
 * A disc nobody on the team can learn is not a reward, it is a note saying "not for you". Most
 * drops are therefore usable today; the rest are for a creature not drawn yet, which is the
 * part that makes them worth collecting.
 */
export const DROP_DISC_FROM_OWNED = 0.72;

// --- Items ---------------------------------------------------------------------------------

export const CANDY_ID = "candy";
export const DISC_PREFIX = "disc:";

export function discId(moveId: string): string {
  return DISC_PREFIX + moveId;
}

/** The move a disc teaches, or null when the id is not a disc. */
export function discMove(itemId: string): string | null {
  return itemId.startsWith(DISC_PREFIX) ? itemId.slice(DISC_PREFIX.length) : null;
}

/** A rare candy is one level, ceiling permitting. 이상한 사탕. */
export const CANDY_LEVELS = 1;

// --- Randomness ----------------------------------------------------------------------------

/**
 * A uniform float from the platform CSPRNG.
 *
 * `Math.random()` would do for fairness and would not do for trust: the odds on a gacha are the
 * one number players compare against what they actually got, and a generator whose sequence
 * could be reasoned about from a few observed rolls is not worth defending later. Drops are
 * drawn from the same source because they are the same kind of promise.
 */
export function random(): number {
  const bytes = crypto.getRandomValues(new Uint32Array(1));
  return bytes[0] / 4_294_967_296;
}

export function pick<T>(from: readonly T[]): T | null {
  if (from.length === 0) return null;
  return from[Math.floor(random() * from.length)];
}
