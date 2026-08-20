import { randomId } from "./crypto";

/**
 * The PvP room: finding an opponent, and carrying the turns between the two of you.
 *
 * <b>Why a Durable Object.</b> A battle between two people is a single piece of state that both
 * of them must agree on, and a Durable Object is the only Cloudflare primitive that guarantees
 * exactly one instance of a named object exists anywhere in the world. Matchmaking with two
 * Workers and a KV key is a race — both read "nobody waiting", both wait, neither is matched —
 * and it is not a race that can be patched afterwards, because the symptom is an empty queue
 * that occasionally works.
 *
 * <b>One class, two roles, chosen by the name it is addressed with.</b>
 *
 * <list>
 *  - <c>lobby</c> — holds at most one waiting player. The second arrival is paired with them: a
 *    match id and a shared seed are minted, both are told, and both are released to reconnect
 *    to the match room. All queueing goes through this one object, which is a genuine
 *    bottleneck and the right trade at this scale: it is a handful of messages per match, and
 *    the alternative is a distributed queue for a game that does not have one yet.
 *  - <c>match:ID</c> — the battle itself. Accepts two sockets, refuses a third, relays every
 *    turn message from each to the other, and tells the survivor when the other leaves.
 * </list>
 *
 * <b>What this room does and does not decide.</b> It owns the match's identity, the seed both
 * clients simulate from, and who is player 0. It does NOT simulate the battle: the two clients
 * run the same deterministic engine from the same seed and exchange their chosen moves, which
 * is lockstep, and lockstep trusts both clients not to lie about their own choices. That is a
 * real limitation and it is written down rather than glossed: a modified client could cheat a
 * PvP battle today. What it cannot do is invent a team — the roster comes from the database,
 * not from the socket — or pay itself experience, which is settled against the match id on the
 * HTTP side. Porting the battle engine to the Worker is the fix when it matters; until then
 * this is a friendly-match protocol and should be described as one.
 */
export class MatchRoom {
  private readonly state: DurableObjectState;

  /** The player waiting for an opponent, in the lobby role. */
  private waiting: Waiting | null = null;

  /** The two sockets, in the match role. Index is the player number. */
  private players: Player[] = [];

  /** Minted when the pair is made, so both clients report their result against one id. */
  private matchId = "";

  constructor(state: DurableObjectState) {
    this.state = state;
  }

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);

    if (request.headers.get("upgrade") !== "websocket") {
      return new Response("expected websocket", { status: 426 });
    }

    const role = url.searchParams.get("role") ?? "lobby";
    const accountId = url.searchParams.get("accountId") ?? "";
    const name = url.searchParams.get("name") ?? "Trainer";
    const roster = url.searchParams.get("roster") ?? "[]";

    if (!accountId) return new Response("unauthorised", { status: 401 });

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    server.accept();

    if (role === "lobby") this.joinLobby(server, accountId, name, roster);
    else this.joinMatch(server, accountId, name, roster);

    return new Response(null, { status: 101, webSocket: client });
  }

  // --- Lobby ---------------------------------------------------------------------------

  private joinLobby(socket: WebSocket, accountId: string, name: string, roster: string): void {
    // A player already in the queue who opens a second socket replaces their first rather than
    // being matched against themselves — which is what happens without this check the moment
    // somebody reloads the page while queued.
    if (this.waiting && this.waiting.accountId === accountId) {
      try {
        this.waiting.socket.close(1000, "replaced by a newer connection");
      } catch {
        /* already gone */
      }
      this.waiting = null;
    }

    if (!this.waiting) {
      this.waiting = { socket, accountId, name, roster };
      send(socket, { type: "queued" });

      socket.addEventListener("close", () => {
        if (this.waiting?.socket === socket) this.waiting = null;
      });
      return;
    }

    const opponent = this.waiting;
    this.waiting = null;

    const matchId = randomId();
    // One seed, sent to both, so the two clients' engines roll identically. Minted here and not
    // by either client for the obvious reason.
    const seed = Math.floor(Math.random() * 2_147_483_647);

    send(opponent.socket, {
      type: "matched",
      matchId,
      seed,
      player: 0,
      opponentName: name,
      opponentRoster: roster
    });
    send(socket, {
      type: "matched",
      matchId,
      seed,
      player: 1,
      opponentName: opponent.name,
      opponentRoster: opponent.roster
    });

    // Both are released: the match itself happens in the `match:ID` room, which they now open.
    // Keeping them here would make the lobby the relay for every concurrent battle.
    close(opponent.socket, "matched");
    close(socket, "matched");
  }

  // --- The match ------------------------------------------------------------------------

  private joinMatch(socket: WebSocket, accountId: string, name: string, roster: string): void {
    if (this.players.length >= 2) {
      send(socket, { type: "error", error: "room_full" });
      close(socket, "room full");
      return;
    }

    if (!this.matchId) this.matchId = this.state.id.toString();

    const player: Player = { socket, accountId, name, roster };
    this.players.push(player);
    const index = this.players.length - 1;

    send(socket, { type: "joined", player: index, matchId: this.matchId });

    socket.addEventListener("message", (event) => {
      // Relayed verbatim, and only to the other player. The room does not parse turn payloads:
      // the engine that understands them is on the client, and a server that half-understands a
      // message format is a server that has to be redeployed every time the format moves.
      const other = this.players.find((candidate) => candidate.socket !== socket);
      if (!other) return;
      try {
        other.socket.send(typeof event.data === "string" ? event.data : "");
      } catch {
        /* the other side went away between the find and the send */
      }
    });

    socket.addEventListener("close", () => {
      this.players = this.players.filter((candidate) => candidate.socket !== socket);
      for (const remaining of this.players) {
        send(remaining.socket, { type: "opponent_left" });
      }
    });

    if (this.players.length === 2) {
      // Told to both at once, so neither starts a turn the other has not begun.
      this.players.forEach((entry, position) => {
        const other = this.players[1 - position];
        send(entry.socket, {
          type: "ready",
          player: position,
          matchId: this.matchId,
          opponentName: other.name,
          opponentRoster: other.roster
        });
      });
    }
  }
}

interface Waiting {
  socket: WebSocket;
  accountId: string;
  name: string;
  roster: string;
}

type Player = Waiting;

function send(socket: WebSocket, payload: unknown): void {
  try {
    socket.send(JSON.stringify(payload));
  } catch {
    /* the socket closed between the decision to send and the send */
  }
}

function close(socket: WebSocket, reason: string): void {
  try {
    socket.close(1000, reason);
  } catch {
    /* already closed */
  }
}
