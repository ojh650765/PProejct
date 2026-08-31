using System;
using System.Collections;
using System.Collections.Generic;
using PokeLab.Battle;
using PokeLab.Cinematics;
using PokeLab.Core;
using PokeLab.UI;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Gives the player the battle back.
    ///
    /// Every part of the command UI already existed — <see cref="BattleHudView"/>, the plates,
    /// the move buttons, the log, the FIGHT/BAG/POKéMON/RUN panel — and nothing built any of
    /// it. The only reference to the HUD in the whole project was a debug driver. So a battle
    /// ran with the auto-play policy choosing the player's moves, at full speed, with no
    /// health bars: the player watched their own fight happen to them.
    ///
    /// This is the missing wire. It builds the HUD, feeds it the event stream through
    /// <see cref="BattlePresenter.EventObserved"/>, and answers
    /// <see cref="BattlePresenter.ActionRoutine"/> with whatever the player presses.
    ///
    /// It lives in PokeLab.Boot for the same reason DialoguePresenter does: the HUD is
    /// PokeLab.UI, the presenter is PokeLab.Cinematics, and those two assemblies cannot see
    /// each other. Boot is the one place that can see both.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleHudPresenter : MonoBehaviour
    {
        // Under the dialogue box at 400, because a battle can still speak, and over the world.
        private const int SortingOrder = 380;

        [Tooltip("Longest the game will wait for the player before the policy answers for them. " +
                 "Guards an unattended session, not the player. PvP ignores this and uses " +
                 "PvpTurnBroker.TurnSeconds, which is a shot clock and does mean the player.")]
        [SerializeField] private float _decisionTimeout = 300f;

        private BattleHudView _hud;
        private BattlePresenter _bound;
        private Canvas _canvas;

        private BattleEngine _engine;
        private bool _hasChoice;
        private BattleAction _choice;

        private void Update()
        {
            // Polled rather than driven by an event, because the arena is loaded and unloaded
            // as a scene: the presenter this binds to does not exist for most of the session
            // and is a different object every battle.
            var arena = BattleArena.Current;
            var presenter = arena != null ? arena.Presenter : null;

            // ReferenceEquals, not ==. UnityEngine.Object's operator== reports a destroyed
            // object as equal to null, and the battle scene unloads by destroying the presenter
            // in the same frame the arena clears BattleArena.Current — so `presenter` is a real
            // null, `_bound` is a destroyed reference, and == calls them equal. Treating them
            // as equal is why the plates, the command panel and the log were still drawn over
            // the overworld after the battle: Unbind, and with it _hud.Hide(), was skipped.
            if (!ReferenceEquals(presenter, _bound))
            {
                Unbind();
                if (presenter != null) Bind(presenter);
            }

            // Every frame, not once at Bind. This is the line the whole PvP path turned on.
            //
            // The launcher builds the broker AFTER the arena scene has loaded, because the
            // broker needs the stage the arena registers -- but this component is polling
            // BattleArena.Current the whole time, and BattleArena sets it in OnEnable, during
            // that load. So Bind ran first, read a broker that did not exist yet, left
            // TurnExchange null, and the turn loop fell through to ChooseAction: the AI played
            // a match that had a real opponent's name and a real opponent's team on it, which
            // is exactly what was reported. Nothing else about the lockstep path was wrong. It
            // was simply never reached.
            //
            // Keeping the two in step every frame fixes it without either side having to know
            // the other's construction order -- which is the part that would rot again.
            SyncExchange();
        }

        /// <summary>
        /// Points the turn loop at the live match's exchange, or at nothing.
        ///
        /// Compared by reference rather than by delegate so this does not allocate a closure
        /// sixty times a second for the whole battle.
        /// </summary>
        private void SyncExchange()
        {
            var broker = PvpTurnBroker.Current;
            if (ReferenceEquals(broker, _broker)) return;
            _broker = broker;

            if (_bound == null) return;
            _bound.TurnExchange = broker != null ? Exchange : null;
        }

        /// <summary>The live match's exchange, or null in every other battle.</summary>
        private PvpTurnBroker _broker;

        /// <summary>
        /// The broker's exchange, with the clock switched over for the length of it.
        ///
        /// The wrapper exists so the broker never learns what a HUD is: it trades actions over
        /// a socket, and whether anything on screen says so is not its business. What it buys
        /// is the half of "확실하게 주고 받는" that the countdown alone does not — after the
        /// choice is sent there was previously no signal at all, so a thinking opponent and a
        /// hung game looked identical for as long as it took.
        /// </summary>
        private IEnumerator Exchange(BattleAction mine, Action<BattleAction?> got)
        {
            var broker = _broker;
            if (broker == null) { got(null); yield break; }

            _hud?.BeginOpponentWait();

            BattleAction? theirs = null;
            yield return broker.Exchange(mine, a => theirs = a);
            _hud?.StopTurnClock();

            // The reason, in the log, before the battle unwinds.
            //
            // PvpTurnBroker.Explain has always known how to say "they left", "they stopped
            // answering", "the two battles fell out of step" — and nothing called it, so an
            // aborted match simply ended and the player was left to guess which of those it
            // had been. This is the last frame on which there is anywhere to put it.
            if (theirs == null && _hud != null)
                _hud.Log?.Append(PvpTurnBroker.Explain(broker.Failure));

            got(theirs);
        }

        private void OnDestroy() => Unbind();

        private void Bind(BattlePresenter presenter)
        {
            _bound = presenter;

            EnsureHud();
            if (_hud == null) return;

            _bound.ActionRoutine = AskPlayer;
            _bound.EventObserved += OnBattleEvent;

            // TurnExchange is deliberately NOT set here. It is SyncExchange's, every frame,
            // because the broker is built after this binding happens -- see the note there.
            _broker = null;

            _hud.MoveChosen = index => Commit(BattleAction.UseMove(BattleSide.Player, index));
            _hud.SwitchRequested = index => Commit(BattleAction.SwitchTo(BattleSide.Player, index));
            _hud.RunRequested = () => Commit(BattleAction.Run(BattleSide.Player));

            // The default ball, by name. The HUD cannot commit this itself: ItemCatalog lives
            // in the battle assembly, which PokeLab.UI must not reference, so the UI raises
            // the intent and this binding — sitting in Boot, which sees both — names the item.
            _hud.CaptureRequested = () =>
                Commit(BattleAction.Capture(BattleSide.Player, ItemCatalog.PokeBallId));

            // The bag screen does not exist yet. Rather than a button that silently does
            // nothing, it says so in the log — a dead control is worse than a plain one.
            _hud.BagRequested = () =>
            {
                _hud.Log?.Append(Loc.Pick("The bag is not packed yet.", "가방은 아직 준비되지 않았네."));
                _hud.BeginPlayerTurn(ActivePlayer());
            };

            // The party command opens the in-battle picker over the engine's own party list —
            // the authoritative one, because mid-battle HP lives on the engine's instances.
            // The old honest line survives only for the case it was written for: a party with
            // genuinely nobody else to send.
            _hud.PartyRequested = () =>
            {
                var party = PlayerParty();
                var activeIndex = IndexOfActive(party, ActivePlayer());
                if (AnyBenchedHealthy(party, activeIndex))
                {
                    _hud.OpenPartyPicker(party, activeIndex, false);
                    return;
                }
                _hud.Log?.Append(Loc.Pick("There is nobody else to send out.", "내보낼 다른 포켓몬이 없어."));
                _hud.BeginPlayerTurn(ActivePlayer());
            };

            _hud.Show();
        }

        private void Unbind()
        {
            if (_bound != null)
            {
                _bound.EventObserved -= OnBattleEvent;
                // Cleared rather than left pointing here: the presenter outlives this binding
                // in the battle scene's own teardown, and a routine on a dead HUD would hang
                // the turn loop until its timeout.
                if (_bound.ActionRoutine == AskPlayer) _bound.ActionRoutine = null;
                _bound.TurnExchange = null;
            }

            // Outside the branch, because the guard above is Unity's == and a destroyed
            // presenter does not pass it: the field would keep the dead reference, never again
            // match the null Update() reads, and Unbind would re-run every frame for the rest
            // of the session.
            _bound = null;

            _broker = null;
            if (_hud != null) _hud.Hide();
            _hasChoice = false;
        }

        private void OnBattleEvent(BattleEvent evt)
        {
            if (_hud != null) _hud.OnBattleEvent(evt);
        }

        /// <summary>
        /// Asks the player for this turn and does not return until they answer.
        ///
        /// The timeout is a guard against an unattended editor session sitting on a modal
        /// forever, not a shot clock — five minutes is far longer than any real decision, and
        /// when it fires the caller falls back to the policy rather than aborting the battle.
        /// </summary>
        private IEnumerator AskPlayer(BattleEngine engine, Action<BattleAction> commit)
        {
            if (_hud == null || engine == null) yield break;
            _engine = engine;

            // With a player at the controls, a faint must become a choice rather than the
            // engine quietly fielding whoever is next in the list. Set every turn rather
            // than once per bind because the flag is per-engine and the engine is a new
            // object every battle; an assignment to the same bool is free.
            engine.DeferPlayerReplacement = true;

            var active = engine.State?.ActiveOf(PokeLab.UI.UiServices.MySide);
            if (active == null) yield break;

            // The other player's creature is down and this one's is not: there is nothing to
            // choose this turn, because the engine reads only the fainted side's switch.
            //
            // Something still has to be committed. The turn loop and the far machine advance
            // together, so skipping the commit would leave the two of them a turn apart --
            // and being a turn apart is the one failure a lockstep match cannot recover from.
            // A switch to the creature already out is the no-op: the engine rejects it as a
            // switch and never looks at it for a side that owes nothing.
            if (engine.IsReplacementTurn &&
                !engine.AwaitingReplacement(PokeLab.UI.UiServices.MySide))
            {
                _hud.Log?.Append(Loc.Pick("The opponent is sending out their next creature…",
                                          "상대가 다음 포켓몬을 내보내고 있어요…"));
                var standing = IndexOfActive(PlayerParty(), active);
                commit(BattleAction.SwitchTo(BattleSide.Player, Mathf.Max(0, standing)));
                yield break;
            }

            _hasChoice = false;
            _hud.Show();

            // The replacement turn: the active creature is down and somebody healthy is
            // benched, so the only legal action is a switch and the move menu would be a
            // menu of lies. The picker opens in forced mode — no cancel, because there is
            // no turn to go back to — and the chosen switch resolves as a free send-out.
            var party = PlayerParty();
            var activeIndex = IndexOfActive(party, active);
            if (active.IsFainted && AnyBenchedHealthy(party, activeIndex))
            {
                _hud.Log?.Append(Loc.Pick("Choose your next Pokémon!", "다음 포켓몬을 내보내자!"));
                _hud.OpenPartyPicker(party, activeIndex, true);
            }
            else
            {
                _hud.BeginPlayerTurn(active);
            }

            // Two different budgets, because they are two different things.
            //
            // Alone, the deadline guards an unattended editor session and nothing else; five
            // minutes is far longer than any real decision and when it lapses the policy
            // answering is harmless. In a match it is a shot clock: the other player is
            // sitting there, so the budget is short, it is SHOWN, and running it out is a
            // move -- the turn is spent on nothing rather than handed to the AI.
            var timed = _broker != null;
            var budget = timed ? PvpTurnBroker.TurnSeconds : Mathf.Max(5f, _decisionTimeout);
            if (timed) _hud.BeginTurnClock(budget);

            var deadline = Time.unscaledTime + Mathf.Max(5f, budget);
            while (!_hasChoice && Time.unscaledTime < deadline) yield return null;

            // Locked the moment the choice is taken, so a second press during the performance
            // cannot queue a move the player never meant to make on the following turn.
            _hud.LockCommands();

            if (_hasChoice) { commit(_choice); yield break; }

            if (!timed)
            {
                Debug.LogWarning("[BattleHud] No command was given within the decision timeout; " +
                                 "the auto-play policy will answer this turn.", this);
                yield break;
            }

            commit(Forfeit());
        }

        /// <summary>
        /// What is submitted when the shot clock runs out.
        ///
        /// <b>Something must be.</b> Both machines step together and neither can resolve the
        /// turn until it holds both actions, so a side that sends nothing does not lose a
        /// turn — it strands the match. This is why the answer is an action and not a skip.
        ///
        /// <b>Normally that action is a Pass</b>, which is the rule as asked for: nobody
        /// chose, so the opening goes by. It is a real engine action rather than a disguised
        /// one, so the log says what happened on both screens and the trace does too.
        ///
        /// <b>A forced replacement is the exception, and it has to be.</b> When the active
        /// creature is down, passing is not available: the field would stay empty, and every
        /// following turn would be the same non-choice, forever. Somebody has to come in, so
        /// the first healthy member does. It is a worse outcome than choosing — the player
        /// gets whoever is next rather than whoever answers the threat — which is the honest
        /// cost of not answering, and it is still a battle rather than a hang.
        /// </summary>
        private BattleAction Forfeit()
        {
            var party = PlayerParty();
            var active = ActivePlayer();
            var standing = IndexOfActive(party, active);

            if (active != null && active.IsFainted && AnyBenchedHealthy(party, standing))
            {
                var next = FirstHealthy(party, standing);
                _hud.Log?.Append(Loc.Pick("Out of time — sending out the next Pokémon.",
                                          "시간이 다 됐다! 다음 포켓몬이 나간다."));
                return BattleAction.SwitchTo(BattleSide.Player, Mathf.Max(0, next));
            }

            _hud.Log?.Append(Loc.Pick("Out of time — the turn was lost.",
                                      "시간이 다 됐다! 이번 턴을 놓쳤다."));
            return BattleAction.Pass(BattleSide.Player);
        }

        /// <summary>The first party member off the field who can still fight, or -1.</summary>
        private static int FirstHealthy(IReadOnlyList<CreatureInstance> party, int activeIndex)
        {
            if (party == null) return -1;
            for (var i = 0; i < party.Count; i++)
            {
                if (i == activeIndex) continue;
                var member = party[i];
                if (member != null && !member.IsFainted) return i;
            }
            return -1;
        }

        private void Commit(BattleAction action)
        {
            if (_hasChoice) return;      // first press wins; the rest are noise from the same frame
            _choice = action;
            _hasChoice = true;
        }

        /// <summary>
        /// The creature the player is currently commanding.
        ///
        /// Read off the engine the turn loop handed us rather than reached for through the
        /// arena: there are two types named BattleStage, one per assembly, and only the battle
        /// one owns an engine. Holding the reference the caller already gave us avoids having
        /// to pick between them at all.
        /// </summary>
        private CreatureInstance ActivePlayer() =>
            _engine?.State?.ActiveOf(PokeLab.UI.UiServices.MySide);

        /// <summary>
        /// The player's party as the engine sees it — the instances whose HP the battle has
        /// been mutating. The profile's list is the fallback for the window before the first
        /// turn hands us an engine; mid-battle it can be a snapshot the fight has moved past.
        /// </summary>
        private IReadOnlyList<CreatureInstance> PlayerParty()
        {
            var party = _engine?.State?.PartyOf(PokeLab.UI.UiServices.MySide);
            if (party != null && party.Count > 0) return party;
            return ServiceHub.TryGet<IPlayerProfile>(out var profile) ? profile.Party : null;
        }

        /// <summary>
        /// Where the active creature sits in the party list. By instance id before reference,
        /// because the engine may clone instances at battle start and the picker needs the
        /// index the switch action will be validated against.
        /// </summary>
        private static int IndexOfActive(IReadOnlyList<CreatureInstance> party, CreatureInstance active)
        {
            if (party == null || active == null) return -1;
            for (var i = 0; i < party.Count; i++)
            {
                var member = party[i];
                if (member == null) continue;
                if (ReferenceEquals(member, active)) return i;
                if (!string.IsNullOrEmpty(active.InstanceId) && member.InstanceId == active.InstanceId) return i;
            }
            return -1;
        }

        /// <summary>Whether anyone off the field could still fight.</summary>
        private static bool AnyBenchedHealthy(IReadOnlyList<CreatureInstance> party, int activeIndex)
        {
            if (party == null) return false;
            for (var i = 0; i < party.Count; i++)
            {
                if (i == activeIndex) continue;
                var member = party[i];
                if (member != null && !member.IsFainted) return true;
            }
            return false;
        }

        /// <summary>
        /// Builds the HUD once, on its own canvas.
        ///
        /// Its own canvas rather than a shared one because the battle scene is loaded and
        /// unloaded underneath it: a HUD parented into that scene would be destroyed with it,
        /// taking the binding with it halfway through the return transition.
        /// </summary>
        private void EnsureHud()
        {
            if (_hud != null) return;

            var canvasGo = new GameObject("BattleHudCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(_canvas, SortingOrder);

            var hudGo = new GameObject("BattleHud", typeof(RectTransform));
            hudGo.transform.SetParent(canvasGo.transform, false);

            _hud = hudGo.AddComponent<BattleHudView>();
            _hud.Hide();
        }
    }
}
