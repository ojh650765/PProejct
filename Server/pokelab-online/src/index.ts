import { handleCreate, handleLogin, authenticate } from "./accounts";
import { handleBattleResult } from "./battle";
import { Env, assertEnv, fail, json } from "./env";
import { handleRoll, handleRoster, odds, partyOf, poolSize, rosterFor } from "./gacha";
import {
  handleBreakthrough,
  handleCandy,
  handleEnhance,
  handleSetParty,
  handleTeach
} from "./growth";
import { handleSaveDelete, handleSaveGet, handleSaveInfo, handleSavePut } from "./saves";
import { MatchRoom } from "./MatchRoom";

export { MatchRoom };

/**
 * The whole surface the game talks to.
 *
 * Flat and explicit, in the shape of the reference Worker: one `if` per route, no framework,
 * every handler returning a JSON body with an `ok` and — when it is false — an `error` code the
 * client already has a sentence for. A 500 with an HTML body is the one thing a Unity client
 * cannot do anything sensible with, so the outer try/catch turns everything into that shape.
 */
export default {
  async fetch(request: Request, env: Env, _ctx: ExecutionContext): Promise<Response> {
    try {
      assertEnv(env);
      const url = new URL(request.url);
      const path = url.pathname;

      // The WebGL build is served from GitHub Pages and this Worker from workers.dev, so every
      // call is cross-origin and the browser sends a preflight first. Answered before anything
      // else, because a preflight that 404s takes the real request with it.
      if (request.method === "OPTIONS") return json({ ok: true });

      if (request.method === "GET" && path === "/health") {
        return json({ ok: true, pool: poolSize(), odds: odds() });
      }

      if (request.method === "GET" && path === "/odds") {
        return json({ ok: true, odds: odds() });
      }

      if (request.method === "POST" && path === "/account/create") {
        return await handleCreate(request, env);
      }

      if (request.method === "POST" && path === "/account/login") {
        return await handleLogin(request, env);
      }

      if (request.method === "GET" && path === "/roster") {
        return await handleRoster(request, env);
      }

      if (request.method === "POST" && path === "/gacha/roll") {
        return await handleRoll(request, env);
      }

      if (request.method === "POST" && path === "/battle/result") {
        return await handleBattleResult(request, env);
      }

      // 내 포켓몬. Everything that changes what a collected creature IS lives behind these five,
      // and every one of them answers with the whole account — collection, purse and bag — so
      // the screen redraws from one reply instead of three.
      if (request.method === "POST" && path === "/creature/enhance") {
        return await handleEnhance(request, env);
      }

      if (request.method === "POST" && path === "/creature/breakthrough") {
        return await handleBreakthrough(request, env);
      }

      if (request.method === "POST" && path === "/creature/candy") {
        return await handleCandy(request, env);
      }

      if (request.method === "POST" && path === "/creature/teach") {
        return await handleTeach(request, env);
      }

      if (request.method === "POST" && path === "/party/set") {
        return await handleSetParty(request, env);
      }

      // Story-mode cloud save. Written only when the player presses 리포트 — see saves.ts for
      // why that makes last-write-wins the right rule here.
      if (request.method === "POST" && path === "/save/put") {
        return await handleSavePut(request, env);
      }

      if (request.method === "GET" && path === "/save/get") {
        return await handleSaveGet(request, env);
      }

      // The description without the payload, so a menu can ask "is there a cloud save?"
      // without paying for the save file to find out.
      if (request.method === "GET" && path === "/save/info") {
        return await handleSaveInfo(request, env);
      }

      if (request.method === "POST" && path === "/save/delete") {
        return await handleSaveDelete(request, env);
      }

      if (path === "/pvp/queue" || path.startsWith("/pvp/match/")) {
        return await routeToRoom(request, env, url, path);
      }

      return fail("not_found", 404);
    } catch (error) {
      console.error("request failed", error instanceof Error ? error.message : "unknown");
      return fail("internal_error", 500);
    }
  }
};

/**
 * Hands a socket to the right Durable Object.
 *
 * <b>The token never reaches the room.</b> Authentication happens here, in the Worker, and what
 * is forwarded is the account id, the display name and the roster — the three things the room
 * actually needs. A Durable Object that had to verify tokens would need the database binding
 * and a reason to be trusted with it, for no gain.
 *
 * The roster is read from D1 rather than accepted from the client for the reason the whole
 * backend exists: an opponent's team is only meaningful if the opponent did not choose it.
 */
async function routeToRoom(request: Request, env: Env, url: URL, path: string): Promise<Response> {
  const account = await authenticate(request, env) ?? (await authenticateByQuery(request, env, url));
  if (!account) return fail("unauthorised", 401);

  // The PARTY, not the collection. A room handed fifty creatures would build a team out of
  // whichever six it happened to read first, and the two clients would not agree about which
  // six those were.
  const roster = partyOf(await rosterFor(env, account.id));
  if (roster.length === 0) return fail("no_team");

  const lobby = path === "/pvp/queue";
  const roomName = lobby ? "lobby" : `match:${path.slice("/pvp/match/".length)}`;
  if (!lobby && roomName === "match:") return fail("bad_match", 400);

  const id = env.MATCH.idFromName(roomName);
  const room = env.MATCH.get(id);

  const forwarded = new URL(request.url);
  forwarded.searchParams.set("role", lobby ? "lobby" : "match");
  forwarded.searchParams.set("accountId", account.id);
  forwarded.searchParams.set("name", account.name);
  forwarded.searchParams.set(
    "roster",
    JSON.stringify(
      roster.map((row) => ({
        speciesId: row.species_id,
        level: row.level,
        experience: row.experience,
        rarity: row.rarity,
        slot: row.slot,
        partySlot: row.party_slot ?? -1,
        // Stars and the taught moveset travel with the opponent because the client builds the
        // creature from them. Two clients that disagree about a stat bonus or a fourth move
        // disagree about who won, and that disagreement only shows up as a desync.
        stars: row.stars ?? 0,
        shards: 0,
        moves: row.moves ?? ""
      }))
    )
  );

  return room.fetch(new Request(forwarded.toString(), request));
}

/**
 * The same authentication, from a query parameter.
 *
 * Browsers cannot set an Authorization header on a WebSocket handshake — the WebSocket
 * constructor takes a URL and a subprotocol list and nothing else — so the WebGL build has no
 * way to present a bearer token except in the URL. Accepted only on the socket routes, and only
 * after the header form has been tried, so the desktop path keeps the header and the token
 * stays out of URLs everywhere it can.
 *
 * The cost is real and worth stating: a token in a query string can appear in logs. It is
 * scoped to this Worker's own request log, the token expires, and the alternative is no PvP in
 * the browser at all.
 */
async function authenticateByQuery(request: Request, env: Env, url: URL) {
  const token = url.searchParams.get("token");
  if (!token) return null;

  const proxied = new Request(request.url, {
    headers: { authorization: `Bearer ${token}` }
  });
  return authenticate(proxied, env);
}
