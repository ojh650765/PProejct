using System;
using UnityEngine;

namespace PokeLab.Online
{
    /// <summary>
    /// Finding an opponent, and carrying the turns once you have one.
    ///
    /// <b>Two sockets, not one.</b> The Worker's <c>MatchRoom</c> Durable Object serves two
    /// roles addressed by name. <c>/pvp/queue</c> reaches the single <c>lobby</c> object, which
    /// holds at most one waiting player, pairs the second arrival, tells both the match id and
    /// a shared seed, and then <b>closes both sockets</b> — deliberately, so that every
    /// concurrent battle in the world does not relay through one object. <c>/pvp/match/{id}</c>
    /// reaches that match's own object, where the two turn streams are relayed. So a normal
    /// match is: connect, queue, get paired, disconnect, reconnect somewhere else, play. The
    /// lobby socket closing is a SUCCESS, and treating it as a dropped connection is the first
    /// mistake this class exists to not make.
    ///
    /// <b>Nothing here simulates a battle.</b> The room is a relay: both clients run the same
    /// deterministic engine from <see cref="Seed"/> and exchange their chosen moves. That is
    /// lockstep, and lockstep trusts each client not to lie about its own choice — a modified
    /// client can cheat a PvP battle today. What it cannot do is invent a team, because
    /// <see cref="OpponentRoster"/> is read from the database by the Worker and never from the
    /// socket, or pay itself experience, which is settled against the match id over HTTP and
    /// recorded once. Treat this as a friendly-match protocol and say so to players.
    ///
    /// <b>It is a MonoBehaviour because it has to be pumped.</b> <see cref="PvpSocket"/>
    /// deliberately delivers nothing by callback; frames are drained on the Unity thread from
    /// Update, in order.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PvpSession : MonoBehaviour
    {
        /// <summary>Where the search has got to. The UI is a direct reading of this.</summary>
        public enum Phase
        {
            Idle,
            /// <summary>Opening the lobby socket.</summary>
            Connecting,
            /// <summary>In the queue, waiting for somebody else to arrive.</summary>
            Queued,
            /// <summary>Paired. Moving from the lobby to the match room.</summary>
            Matched,
            /// <summary>Both players are in the room. The battle may begin.</summary>
            Ready,
            /// <summary>The other player left. The match is over however it stood.</summary>
            OpponentLeft,
            /// <summary>Ended badly. <see cref="Error"/> says how.</summary>
            Failed,
        }

        public Phase State { get; private set; } = Phase.Idle;
        public string Error { get; private set; } = "";

        /// <summary>Set once paired. The id both players report their result against.</summary>
        public string MatchId { get; private set; } = "";

        /// <summary>The seed both clients' engines roll from. Minted by the room, never by a client.</summary>
        public int Seed { get; private set; }

        /// <summary>0 or 1. Decides who is treated as the "player" side locally.</summary>
        public int PlayerIndex { get; private set; }

        public string OpponentName { get; private set; } = "";
        public RosterEntry[] OpponentRoster { get; private set; } = Array.Empty<RosterEntry>();

        /// <summary>Seconds spent in the queue, for the UI's "찾는 중" timer.</summary>
        public float QueuedFor { get; private set; }

        /// <summary>Raised whenever <see cref="State"/> changes, so a screen can redraw once.</summary>
        public event Action Changed;

        /// <summary>Raised with each turn payload the opponent sent. Battle code subscribes.</summary>
        public event Action<string> TurnReceived;

        private PvpSocket _socket;
        private bool _inMatchRoom;

        public static PvpSession Ensure()
        {
            var existing = FindAnyObjectByType<PvpSession>();
            if (existing != null) return existing;

            // Hung off the session that owns the token rather than a scene object: matchmaking
            // outlives the title screen it was started from.
            var host = OnlineSession.Ensure();
            return host.gameObject.AddComponent<PvpSession>();
        }

        // --- Driving it ------------------------------------------------------------------

        /// <summary>Joins the queue. Safe to call when already searching — it is ignored.</summary>
        public void FindMatch()
        {
            if (State == Phase.Connecting || State == Phase.Queued || State == Phase.Matched
                || State == Phase.Ready)
            {
                return;
            }

            var session = OnlineSession.Instance;
            if (session == null || !session.IsSignedIn) { Fail("unauthorised"); return; }
            if (!session.HasTeam) { Fail("no_team"); return; }
            if (string.IsNullOrEmpty(OnlineConfig.SocketBase)) { Fail("no_server"); return; }

            Reset();
            _inMatchRoom = false;
            QueuedFor = 0f;
            Move(Phase.Connecting);

            // The token rides in the query string because a browser cannot set an Authorization
            // header on a WebSocket handshake — the constructor takes a URL and a subprotocol
            // list and nothing else. The Worker accepts it there only on the socket routes.
            _socket = PvpSocket.Connect(
                $"{OnlineConfig.SocketBase}/pvp/queue?token={Uri.EscapeDataString(session.Token)}");
        }

        /// <summary>Leaves the queue or the room. Always safe.</summary>
        public void Cancel()
        {
            Reset();
            Move(Phase.Idle);
        }

        /// <summary>Sends one turn to the opponent. Verbatim — the room does not parse it.</summary>
        public bool SendTurn(string payload)
        {
            if (State != Phase.Ready || _socket == null || !_socket.IsOpen) return false;
            return _socket.Send(payload);
        }

        private void Update()
        {
            if (_socket == null) return;

            if (State == Phase.Queued) QueuedFor += Time.unscaledDeltaTime;

            _socket.Pump();

            while (_socket.TryReceive(out var raw)) Handle(raw);

            if (_socket.State == PvpSocket.SocketState.Error)
            {
                Fail(string.IsNullOrEmpty(_socket.Error) ? "socket_error" : _socket.Error);
                return;
            }

            if (_socket.State == PvpSocket.SocketState.Closed)
            {
                // The lobby closes the socket the instant it has paired us, and that is the
                // success path — the move to the match room is already under way. Only a close
                // we did not expect is a failure.
                if (State == Phase.Matched) return;
                if (State == Phase.Ready || _inMatchRoom) { Move(Phase.OpponentLeft); return; }
                if (State != Phase.Idle && State != Phase.Failed) Fail("disconnected");
            }
        }

        private void OnDestroy() => Reset();

        // --- The protocol ----------------------------------------------------------------

        [Serializable]
        private sealed class Envelope
        {
            public string type;
            public string matchId;
            public int seed;
            public int player;
            public string opponentName;
            /// <summary>A JSON array, as a STRING — the Worker forwards it as an opaque blob.</summary>
            public string opponentRoster;
            public string error;
        }

        [Serializable]
        private sealed class RosterWrapper { public RosterEntry[] roster; }

        private void Handle(string raw)
        {
            Envelope message = null;
            try { message = JsonUtility.FromJson<Envelope>(raw); }
            catch { /* not ours */ }

            // Anything without a recognised type is a turn payload: the room relays those
            // verbatim and never looks inside them, so this is where the battle's own protocol
            // is handed on rather than parsed here.
            if (message == null || string.IsNullOrEmpty(message.type))
            {
                if (State == Phase.Ready) TurnReceived?.Invoke(raw);
                return;
            }

            switch (message.type)
            {
                case "queued":
                    Move(Phase.Queued);
                    break;

                case "matched":
                    MatchId = message.matchId ?? "";
                    Seed = message.seed;
                    PlayerIndex = message.player;
                    OpponentName = message.opponentName ?? "";
                    OpponentRoster = ParseRoster(message.opponentRoster);
                    Move(Phase.Matched);
                    EnterMatchRoom();
                    break;

                case "joined":
                    // Acknowledged; the room is not playable until both are in it.
                    if (!string.IsNullOrEmpty(message.matchId)) MatchId = message.matchId;
                    break;

                case "ready":
                    PlayerIndex = message.player;
                    if (!string.IsNullOrEmpty(message.matchId)) MatchId = message.matchId;
                    if (!string.IsNullOrEmpty(message.opponentName)) OpponentName = message.opponentName;
                    var roster = ParseRoster(message.opponentRoster);
                    if (roster.Length > 0) OpponentRoster = roster;
                    Move(Phase.Ready);
                    break;

                case "opponent_left":
                    Move(Phase.OpponentLeft);
                    break;

                case "error":
                    Fail(string.IsNullOrEmpty(message.error) ? "room_error" : message.error);
                    break;
            }
        }

        /// <summary>
        /// Drops the lobby socket and opens the match's own.
        ///
        /// The old socket is disposed first: the lobby is about to close it anyway, and leaving
        /// it live would mean two sockets pumping into one inbox with the match's first frames
        /// interleaved behind the lobby's last.
        /// </summary>
        private void EnterMatchRoom()
        {
            var session = OnlineSession.Instance;
            if (session == null || string.IsNullOrEmpty(MatchId)) { Fail("bad_match"); return; }

            _socket?.Dispose();
            _inMatchRoom = true;
            _socket = PvpSocket.Connect(
                $"{OnlineConfig.SocketBase}/pvp/match/{Uri.EscapeDataString(MatchId)}" +
                $"?token={Uri.EscapeDataString(session.Token)}");
        }

        private static RosterEntry[] ParseRoster(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<RosterEntry>();
            try
            {
                // JsonUtility cannot read a top-level array, so it is wrapped into a field it
                // can. The Worker sends the array exactly as it was serialised for the query
                // string, which is why this is a string here and not RosterEntry[].
                var wrapped = JsonUtility.FromJson<RosterWrapper>("{\"roster\":" + json + "}");
                return wrapped?.roster ?? Array.Empty<RosterEntry>();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Pvp] Opponent roster would not parse: {e.Message}");
                return Array.Empty<RosterEntry>();
            }
        }

        // --- State -----------------------------------------------------------------------

        private void Move(Phase next)
        {
            if (State == next) return;
            State = next;
            Changed?.Invoke();
        }

        private void Fail(string error)
        {
            Error = error ?? "";
            Reset();
            Move(Phase.Failed);
        }

        private void Reset()
        {
            _socket?.Dispose();
            _socket = null;
            _inMatchRoom = false;
        }

        /// <summary>The player-facing sentence for a matchmaking failure.</summary>
        public static string Explain(string error)
        {
            switch (error)
            {
                case "no_team":
                    return PokeLab.Core.Loc.Pick("Draw your team first.", "먼저 팀을 뽑아 주세요.");
                case "room_full":
                    return PokeLab.Core.Loc.Pick("That match is already full.", "이미 시작된 대전이에요.");
                case "disconnected":
                    return PokeLab.Core.Loc.Pick("Lost connection to the match.", "대전 연결이 끊겼어요.");
                case "bad_match":
                    return PokeLab.Core.Loc.Pick("The match could not be joined.", "대전에 참가할 수 없었어요.");
                default:
                    return OnlineClient.Explain(error);
            }
        }
    }
}
