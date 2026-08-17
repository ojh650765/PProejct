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
                 "Guards an unattended session, not the player.")]
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
            // null, `_bound` is a destroyed reference, and == calls them equal. That early
            // return is why the plates, the command panel and the log were still drawn over the
            // overworld after the battle: Unbind, and with it _hud.Hide(), was never reached.
            if (ReferenceEquals(presenter, _bound)) return;

            Unbind();
            if (presenter != null) Bind(presenter);
        }

        private void OnDestroy() => Unbind();

        private void Bind(BattlePresenter presenter)
        {
            _bound = presenter;

            EnsureHud();
            if (_hud == null) return;

            _bound.ActionRoutine = AskPlayer;
            _bound.EventObserved += OnBattleEvent;

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
            }

            // Outside the branch, because the guard above is Unity's == and a destroyed
            // presenter does not pass it: the field would keep the dead reference, never again
            // match the null Update() reads, and Unbind would re-run every frame for the rest
            // of the session.
            _bound = null;

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

            var active = engine.State?.ActiveOf(BattleSide.Player);
            if (active == null) yield break;

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

            var deadline = Time.unscaledTime + Mathf.Max(5f, _decisionTimeout);
            while (!_hasChoice && Time.unscaledTime < deadline) yield return null;

            // Locked the moment the choice is taken, so a second press during the performance
            // cannot queue a move the player never meant to make on the following turn.
            _hud.LockCommands();

            if (_hasChoice) commit(_choice);
            else Debug.LogWarning("[BattleHud] No command was given within the decision timeout; " +
                                  "the auto-play policy will answer this turn.", this);
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
            _engine?.State?.ActiveOf(BattleSide.Player);

        /// <summary>
        /// The player's party as the engine sees it — the instances whose HP the battle has
        /// been mutating. The profile's list is the fallback for the window before the first
        /// turn hands us an engine; mid-battle it can be a snapshot the fight has moved past.
        /// </summary>
        private IReadOnlyList<CreatureInstance> PlayerParty()
        {
            var party = _engine?.State?.PartyOf(BattleSide.Player);
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
