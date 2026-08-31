using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using PokeLab.Battle;
using PokeLab.Core;
using PokeLab.Online;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Trades one turn's action with the other player, and refuses to guess.
    ///
    /// <b>What travels.</b> Only the choice — which move, which switch, which item — and a
    /// count of how many times the local engine has drawn from its generator. Nothing about
    /// what the choice did. Both machines run the same deterministic engine from the seed the
    /// match room minted, so given the same pair of actions they compute the same turn, and
    /// sending the outcome as well would only create a second opinion to disagree with.
    ///
    /// <b>Each turn carries a fingerprint, and that is the desync alarm.</b> Two simulations
    /// that have drifted apart are two players watching different battles while both are told
    /// they are in the same one — a failure that is invisible without a check like this,
    /// because both screens look completely normal right up until they disagree about who won.
    ///
    /// The fingerprint is the generator's draw count AND the state it produced: how far each
    /// engine has read, plus both actives' health and the turn number. The draw count alone
    /// was the first attempt and it is not enough — it counts how many times the generator was
    /// read, not what it said, so two engines seeded differently draw exactly as often as each
    /// other and agree perfectly while computing entirely different damage. Which is not a
    /// hypothetical: the engine seed WAS local rather than the match's, on both machines, and
    /// a count-only check would have gone on nodding at it.
    ///
    /// <b>It never falls back to the AI.</b> Silence, a desync and a departure all end the
    /// battle with a reason. Playing on with a machine-chosen action is exactly the bug this
    /// whole path exists to remove, and it would be worse here than it was before: the player
    /// would be told they were fighting a person.
    /// </summary>
    public sealed class PvpTurnBroker : IDisposable
    {
        /// <summary>
        /// How long to wait for the other player before giving up on the match.
        ///
        /// Generous, because it is a person reading a battlefield, not a request timing out —
        /// and the cost of being too eager is ending a match somebody was still thinking
        /// about. Their client going away is noticed separately and immediately, so this only
        /// governs somebody who is present and quiet.
        /// </summary>
        public const float DefaultTimeout = 90f;

        /// <summary>
        /// How long a player has to choose, before the turn is spent on nothing.
        ///
        /// <b>It must stay comfortably below <see cref="DefaultTimeout"/>, and that is the
        /// whole reason the two constants sit together.</b> The clock is local — each machine
        /// times its own player and sends whatever they had — so the far machine's patience
        /// has to outlast the far player's clock plus the round trip. Raise this above the
        /// timeout and a player who thinks for the full budget gets the match killed out from
        /// under them by an opponent who was waiting exactly as designed.
        ///
        /// Thirty seconds because that is a real decision on a six-creature field — read two
        /// health bars, check a type matchup, pick — and not so long that the other player
        /// starts wondering whether the game has hung.
        /// </summary>
        public const float TurnSeconds = 30f;

        private const string Version = "t1";

        private readonly PvpSession _session;
        private readonly BattleStage _stage;
        private readonly float _timeout;
        private readonly Queue<string> _inbox = new Queue<string>();

        private int _turn;
        private bool _disposed;

        /// <summary>Why the exchange gave up, for the message the player is shown.</summary>
        public string Failure { get; private set; } = "";

        /// <summary>
        /// The broker for the match now running, or null when this is not a PvP battle.
        ///
        /// A static handoff because the two halves meet in the middle: the launcher knows
        /// there is a match and owns its lifetime, while the thing that has to use it is
        /// whatever ends up bound to the arena's presenter, which the launcher never sees.
        /// Cleared by <see cref="Dispose"/>, and the launcher disposes on every exit path —
        /// a broker left standing here would attach a finished match's socket to the next
        /// battle the player starts.
        /// </summary>
        public static PvpTurnBroker Current { get; private set; }

        public PvpTurnBroker(PvpSession session, BattleStage stage, float timeout = DefaultTimeout)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _stage = stage ?? throw new ArgumentNullException(nameof(stage));
            _timeout = timeout;

            // Subscribed for the whole battle rather than per turn: the other player may answer
            // before this side has finished asking, and a frame that arrives while nobody is
            // listening is a frame that never existed.
            _session.TurnReceived += Receive;
            Current = this;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.TurnReceived -= Receive;
            if (ReferenceEquals(Current, this)) Current = null;
        }

        private void Receive(string raw)
        {
            if (!string.IsNullOrEmpty(raw)) _inbox.Enqueue(raw);
        }

        /// <summary>
        /// Sends this turn's action and waits for the opponent's. Reports null on any failure,
        /// having set <see cref="Failure"/> to why.
        /// </summary>
        public IEnumerator Exchange(BattleAction mine, Action<BattleAction?> got)
        {
            _turn++;

            // Sampled BEFORE the turn resolves, so both machines are describing the same
            // moment: the state they were in when the previous turn finished.
            var fingerprint = Fingerprint();

            if (!_session.SendTurn(Encode(_turn, mine, fingerprint)))
            {
                Fail("send_failed", got);
                yield break;
            }

            var waited = 0f;
            while (waited < _timeout)
            {
                while (_inbox.Count > 0)
                {
                    if (!TryDecode(_inbox.Dequeue(), out var turn, out var action, out var theirs))
                        continue; // Not a turn frame, or one this version cannot read.

                    // A frame for a turn already settled. Possible after a reconnect, and
                    // harmless: drop it rather than answering this turn with an old choice.
                    if (turn < _turn) continue;

                    if (turn > _turn)
                    {
                        // They are ahead, which means a turn was lost on the way here. There
                        // is no way back to a shared state from this side.
                        Fail("out_of_step", got);
                        yield break;
                    }

                    if (theirs != fingerprint)
                    {
                        Debug.LogError($"[Pvp] Desync at turn {_turn}: this battle fingerprints " +
                                       $"as {fingerprint:X}, theirs as {theirs:X}. The two " +
                                       "simulations diverged on an earlier turn.");
                        Fail("desync", got);
                        yield break;
                    }

                    got(action);
                    yield break;
                }

                if (_session.State == PvpSession.Phase.OpponentLeft)
                {
                    Fail("disconnected", got);
                    yield break;
                }

                // Unscaled: a battle performance may be slowing time for a hit, and the other
                // player's patience is not on the game's clock.
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            Fail("timeout", got);
        }

        private void Fail(string reason, Action<BattleAction?> got)
        {
            Failure = reason;
            got(null);
        }

        /// <summary>
        /// A cheap value that both machines must agree on if they are still running the same
        /// battle: how far the generator has been read, and what that reading produced.
        ///
        /// Read from the engine's CANONICAL sides, never from the local player's -- the two
        /// machines stand at opposite ends of the field, so a fingerprint taken from "mine"
        /// and "theirs" would differ between them even when nothing at all is wrong.
        /// </summary>
        private long Fingerprint()
        {
            var engine = _stage.Engine;
            if (engine == null) return 0L;

            var state = engine.State;
            var mine = state?.ActiveOf(BattleSide.Player);
            var theirs = state?.ActiveOf(BattleSide.Opponent);

            unchecked
            {
                var hash = 17L;
                hash = hash * 31 + (engine.Random?.DrawCount ?? 0L);
                hash = hash * 31 + (state?.TurnNumber ?? 0);
                hash = hash * 31 + (mine?.CurrentHp ?? -1);
                hash = hash * 31 + (theirs?.CurrentHp ?? -1);
                hash = hash * 31 + (int)(state?.Weather ?? Weather.Clear);
                return hash;
            }
        }

        // ---- the wire format -----------------------------------------------------------
        //
        // Deliberately flat text. It crosses a WebSocket between two builds of the same game,
        // so the only thing it has to survive is a version bump, which the leading tag makes
        // legible rather than a silent misparse.

        private static string Encode(int turn, BattleAction action, long fingerprint)
        {
            return string.Join("|", new[]
            {
                Version,
                turn.ToString(CultureInfo.InvariantCulture),
                ((int)action.Type).ToString(CultureInfo.InvariantCulture),
                action.MoveIndex.ToString(CultureInfo.InvariantCulture),
                action.PartyIndex.ToString(CultureInfo.InvariantCulture),
                action.ItemId ?? "",
                fingerprint.ToString(CultureInfo.InvariantCulture),
            });
        }

        private static bool TryDecode(string raw, out int turn, out BattleAction action,
                                      out long fingerprint)
        {
            turn = 0;
            fingerprint = 0;
            action = default;

            var parts = raw.Split('|');
            if (parts.Length != 7 || parts[0] != Version) return false;

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out turn)) return false;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind)) return false;
            if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var move)) return false;
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var party)) return false;
            if (!long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture,
                               out fingerprint)) return false;

            var item = parts[5];

            // Stamped for the opponent here, and re-stamped by the stage against which end of
            // the field this machine is. A side arriving off a socket is never trusted.
            switch ((BattleAction.Kind)kind)
            {
                case BattleAction.Kind.Move:
                    action = BattleAction.UseMove(BattleSide.Opponent, move);
                    return true;
                case BattleAction.Kind.Switch:
                    action = BattleAction.SwitchTo(BattleSide.Opponent, party);
                    return true;
                case BattleAction.Kind.Item:
                    action = BattleAction.UseItem(BattleSide.Opponent, item, party);
                    return true;
                case BattleAction.Kind.Run:
                    action = BattleAction.Run(BattleSide.Opponent);
                    return true;
                case BattleAction.Kind.Pass:
                    // Their clock ran out. Carried explicitly rather than inferred from a
                    // frame that never came, because "they chose nothing" and "they are gone"
                    // are different situations with different endings.
                    action = BattleAction.Pass(BattleSide.Opponent);
                    return true;
                default:
                    // Capture is not reachable in PvP and a client claiming it is either old
                    // or lying; either way there is nothing sensible to do with it.
                    return false;
            }
        }

        /// <summary>What to tell the player when an exchange ends the match.</summary>
        public static string Explain(string failure)
        {
            switch (failure)
            {
                case "timeout":
                    return Loc.Pick("The opponent stopped responding.", "상대가 응답하지 않았어요.");
                case "disconnected":
                    return Loc.Pick("The opponent left the match.", "상대가 대전을 떠났어요.");
                case "desync":
                    return Loc.Pick("The two battles fell out of step, so the match was stopped.",
                                    "양쪽 대전이 어긋나서 대전을 중단했어요.");
                case "out_of_step":
                    return Loc.Pick("A turn was lost on the way through.", "턴이 유실되었어요.");
                default:
                    return Loc.Pick("The match could not continue.", "대전을 이어갈 수 없어요.");
            }
        }
    }
}
