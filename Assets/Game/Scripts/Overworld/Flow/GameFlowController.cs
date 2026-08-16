using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The concrete <see cref="IGameFlow"/>: it owns mode transitions and the exploration-to-battle
    /// handshake, and it owns none of the visuals.
    ///
    /// The whole point of this type is that the transition is <em>delegated</em>. It freezes the
    /// player, asks the cinematics layer to cover the screen, stages the battle behind that cover,
    /// waits for the result, asks for the outro, restores the player exactly where they were, and
    /// only then asks for the reveal. There is never a frame where the camera jumps.
    ///
    /// Every external dependency is optional, because during partial integration none of them
    /// exist yet:
    ///
    /// - No transition director: falls back to a timed hold. Not pretty, but not a hard cut either
    ///   — and it never blocks the loop.
    /// - No battle host: falls back to a simulated resolution behind a loud warning, so the
    ///   overworld remains playable while the battle worker is still building.
    ///
    /// Both fallbacks are watched by a timeout. A dependency that accepts a callback and never
    /// invokes it would otherwise leave the player frozen forever, which is the worst failure this
    /// system can produce.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-400)]
    public sealed class GameFlowController : MonoBehaviour, IGameFlow
    {
        [Header("Player")]
        [SerializeField] private PlayerLocomotion _player;
        [SerializeField] private OverworldInputReader _input;
        [SerializeField] private PlayerInteractor _interactor;
        [SerializeField] private OverworldCameraRig _cameraRig;

        [Header("Cinematics")]
        [Tooltip("The cinematics worker's transition component. Resolved through ServiceHub first; "
                 + "this is the inspector fallback while ITransitionDirector is not in Core.")]
        [SerializeField] private UnityEngine.Object _transitionDirectorObject;
        [Tooltip("Seconds held when no transition director is available. Keeps the fallback from "
                 + "being a hard cut, without pretending to be an authored transition.")]
        [SerializeField] private float _fallbackTransitionSeconds = 0.4f;

        [Header("Battle staging")]
        [Tooltip("The battle worker's staging component. Must expose "
                 + "BeginEncounter(EncounterRequest, Action<EncounterResult>).")]
        [SerializeField] private UnityEngine.Object _battleStageObject;

        [Header("Safety")]
        [Tooltip("Seconds before an unanswered transition callback is assumed lost and forced "
                 + "through. Never let the player be frozen indefinitely.")]
        [SerializeField] private float _transitionTimeout = 12f;
        [Tooltip("Seconds before an unanswered battle is abandoned and treated as a flee.")]
        [SerializeField] private float _battleTimeout = 600f;

        [Header("Degraded mode")]
        [Tooltip("When no battle stage is registered, resolve encounters after a delay instead of "
                 + "hanging. This only ever runs when the real battle layer is absent — the real "
                 + "path is always preferred and taken automatically once it registers.")]
        [SerializeField] private bool _simulateBattleWhenUnavailable = true;
        [SerializeField] private float _simulatedBattleSeconds = 1.5f;

        [Header("Registration")]
        [Tooltip("Register this as IGameFlow at Awake. Turn off if the boot scene registers its own.")]
        [SerializeField] private bool _registerWithServiceHub = true;

        [Header("Output")]
        [SerializeField] private StringEvent _modeChanged = new StringEvent();

        private readonly List<GameMode> _modeStack = new List<GameMode>();
        // Initialised empty rather than left null: an encounter requested before Start would
        // otherwise dereference a null bridge, and "the very first encounter crashes" is exactly
        // the kind of ordering bug that survives to a build.
        private ServiceBridge _transitions = new ServiceBridge(null);
        private ServiceBridge _battleStage = new ServiceBridge(null);
        private GameMode _mode = GameMode.Boot;

        private Vector3 _returnPosition;
        private Quaternion _returnRotation;
        private bool _hasReturnPoint;
        private Coroutine _encounterRoutine;
        private EncounterResult _pendingResult;
        private bool _resultReceived;

        public GameMode Mode => _mode;

        public event Action<GameMode, GameMode> ModeChanged;

        /// <summary>True from the moment an encounter is requested until the player has control back.</summary>
        public bool IsInEncounterSequence => _encounterRoutine != null;

        private void Awake()
        {
            if (_registerWithServiceHub && !ServiceHub.Has<IGameFlow>())
                ServiceHub.Register<IGameFlow>(this);
        }

        private void Start()
        {
            // Resolved in Start, not Awake: the cinematics and battle layers register during their
            // own Awake, and resolution order between them is not guaranteed.
            _transitions = ServiceBridge.Resolve<ITransitionDirector>(_transitionDirectorObject);
            // Resolved the same way as the line above, which it was not: this took the
            // inspector field alone and never asked the hub, so BattleStageHost could
            // register a stage in Awake and this still reported not finding one. Every
            // encounter then resolved in degraded mode with nothing on screen, and the
            // warning pointed at a missing component that was sitting on the same object.
            _battleStage = ServiceBridge.Resolve<IBattleStage>(_battleStageObject);

            if (!_transitions.IsValid)
                Debug.LogWarning("[GameFlow] No transition director; falling back to a timed hold. "
                                 + "Assign the cinematics component to remove the placeholder transition.", this);
            if (!_battleStage.IsValid)
                Debug.LogWarning("[GameFlow] No battle stage; encounters will resolve in degraded mode.", this);

            SetMode(GameMode.Exploring);
        }

        private void OnDestroy()
        {
            // Deliberately does not call ServiceHub.Reset: that clears every worker's registration,
            // and tearing down one component must not deregister the whole game. The integrator's
            // boot scene owns the play-mode-exit reset.
            if (_encounterRoutine != null) StopCoroutine(_encounterRoutine);
            _encounterRoutine = null;
        }

        // ---- Mode stack ------------------------------------------------------------------

        public void PushMode(GameMode mode)
        {
            _modeStack.Add(_mode);
            SetMode(mode);
        }

        public void PopMode()
        {
            if (_modeStack.Count == 0)
            {
                SetMode(GameMode.Exploring);
                return;
            }
            var restored = _modeStack[_modeStack.Count - 1];
            _modeStack.RemoveAt(_modeStack.Count - 1);
            SetMode(restored);
        }

        private void SetMode(GameMode mode)
        {
            if (_mode == mode) return;
            var previous = _mode;
            _mode = mode;

            ModeChanged?.Invoke(previous, mode);
            GameEvents.RaiseModeChanged(previous, mode);
            _modeChanged.Invoke(mode.ToString());
        }

        // ---- Encounter orchestration ------------------------------------------------------

        /// <summary>
        /// Runs the full exploration-to-battle-and-back sequence. The callback fires once the
        /// player is standing back in the overworld with control restored, so callers that apply
        /// progression can assume the world is settled.
        /// </summary>
        public void RequestEncounter(EncounterRequest request, Action<EncounterResult> onResolved)
        {
            if (request == null)
            {
                onResolved?.Invoke(null);
                return;
            }

            if (_encounterRoutine != null)
            {
                // A second encounter mid-transition would leave two sequences racing for the
                // player's freeze state. Refuse cleanly rather than interleaving.
                Debug.LogWarning("[GameFlow] Encounter requested while one is already running; ignored.", this);
                onResolved?.Invoke(null);
                return;
            }

            _encounterRoutine = StartCoroutine(RunEncounter(request, onResolved));
        }

        private IEnumerator RunEncounter(EncounterRequest request, Action<EncounterResult> onResolved)
        {
            // 1. Take control away and remember exactly where the player was, before anything
            //    else can move them. This snapshot is what makes the return frame-accurate.
            CaptureReturnPoint();
            FreezePlayer(true);
            SetMode(GameMode.EncounterIntro);

            // 2. Ask the cinematics layer to cover the screen. We do not touch the camera.
            yield return PlayTransition(director => director.TryInvoke(
                nameof(ITransitionDirector.PlayEncounterIntro), request, (Action)OnStageReady));

            // 3. Stage the battle behind the cover.
            SetMode(GameMode.Battle);
            _resultReceived = false;
            _pendingResult = null;

            var staged = _battleStage.IsValid && _battleStage.TryInvoke(
                "BeginEncounter", request, (Action<EncounterResult>)OnBattleResolved);

            if (!staged) yield return RunDegradedBattle(request);

            // 4. Wait for the battle. Bounded, so a battle layer that never answers cannot strand
            //    the player in a frozen overworld.
            var elapsed = 0f;
            while (!_resultReceived && elapsed < _battleTimeout)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!_resultReceived)
            {
                Debug.LogError("[GameFlow] Battle never reported a result; treating it as a flee.", this);
                _pendingResult = new EncounterResult { Outcome = BattleOutcome.Fled };
            }

            var result = _pendingResult ?? new EncounterResult { Outcome = BattleOutcome.Fled };

            // 5. Outro, restore, reveal — in that order, so the reposition happens under cover.
            SetMode(GameMode.BattleOutro);
            yield return PlayTransition(director => director.TryInvoke(
                nameof(ITransitionDirector.PlayBattleOutro), result, (Action)OnStageReady));

            RestoreReturnPoint();
            SetMode(GameMode.Exploring);

            yield return PlayTransition(director => director.TryInvoke(
                nameof(ITransitionDirector.PlayReveal), (Action)OnStageReady));

            FreezePlayer(false);
            _encounterRoutine = null;

            // Invoked last so the progression handler sees a settled world and a live player.
            onResolved?.Invoke(result);
        }

        /// <summary>
        /// Runs one transition step. The callback flag is shared, which is safe because the
        /// sequence is strictly serial — only one transition is ever in flight.
        /// </summary>
        private IEnumerator PlayTransition(Func<ServiceBridge, bool> invoke)
        {
            _stageReady = false;

            var dispatched = _transitions.IsValid && invoke(_transitions);
            if (!dispatched)
            {
                // No director. Hold briefly rather than cutting instantly: an abrupt swap is the
                // one thing the brief is emphatic about avoiding, and a short hold at least reads
                // as intentional while the real transition is being built.
                if (_fallbackTransitionSeconds > 0f)
                    yield return new WaitForSecondsRealtime(_fallbackTransitionSeconds);
                yield break;
            }

            var elapsed = 0f;
            while (!_stageReady && elapsed < _transitionTimeout)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!_stageReady)
                Debug.LogError("[GameFlow] Transition callback never fired; forcing the sequence on. "
                               + "The cinematics layer must always invoke its completion callback.", this);
        }

        private bool _stageReady;
        private void OnStageReady() => _stageReady = true;

        private void OnBattleResolved(EncounterResult result)
        {
            _pendingResult = result;
            _resultReceived = true;
        }

        /// <summary>
        /// The no-battle-layer path. It exists so the overworld is playable and testable on its
        /// own; it is never taken when a real battle stage is registered.
        /// </summary>
        private IEnumerator RunDegradedBattle(EncounterRequest request)
        {
            if (!_simulateBattleWhenUnavailable)
            {
                _pendingResult = new EncounterResult { Outcome = BattleOutcome.Fled };
                _resultReceived = true;
                yield break;
            }

            Debug.LogWarning($"[GameFlow] DEGRADED MODE: simulating the battle against species "
                             + $"{request.WildSpeciesId}. Register a battle stage for the real path.", this);

            yield return new WaitForSecondsRealtime(_simulatedBattleSeconds);

            var result = new EncounterResult { Outcome = BattleOutcome.PlayerVictory, MoneyDelta = 0 };
            if (request.Kind == BattleKind.Wild && request.WildSpeciesId > 0)
                result.SpeciesSeen.Add(request.WildSpeciesId);

            _pendingResult = result;
            _resultReceived = true;
        }

        // ---- Return to overworld ----------------------------------------------------------

        /// <summary>
        /// Plays the outro and hands control back. Normally driven internally by
        /// <see cref="RequestEncounter"/>; this entry point exists for a battle layer that ends a
        /// battle on its own terms, e.g. a whiteout.
        /// </summary>
        public void ReturnToOverworld()
        {
            if (_encounterRoutine != null) return; // the running sequence already owns the return
            StartCoroutine(RunStandaloneReturn());
        }

        private IEnumerator RunStandaloneReturn()
        {
            SetMode(GameMode.BattleOutro);
            yield return PlayTransition(director => director.TryInvoke(
                nameof(ITransitionDirector.PlayBattleOutro), (EncounterResult)null, (Action)OnStageReady));

            RestoreReturnPoint();
            SetMode(GameMode.Exploring);

            yield return PlayTransition(director => director.TryInvoke(
                nameof(ITransitionDirector.PlayReveal), (Action)OnStageReady));

            FreezePlayer(false);
        }

        private void CaptureReturnPoint()
        {
            if (_player == null) return;
            _returnPosition = _player.transform.position;
            _returnRotation = _player.transform.rotation;
            _hasReturnPoint = true;
        }

        /// <summary>
        /// Puts the player back exactly where they were. Warping is the only correct way to do
        /// this — writing the transform directly leaves the CharacterController's internal cache
        /// holding the old position and it snaps back on the next move.
        /// </summary>
        private void RestoreReturnPoint()
        {
            if (!_hasReturnPoint || _player == null) return;
            _player.Warp(_returnPosition, _returnRotation);
        }

        private void FreezePlayer(bool frozen)
        {
            if (_player != null) _player.SetMotionFrozen(frozen);
            if (_input != null) _input.InputEnabled = !frozen;
            if (_cameraRig != null) _cameraRig.ControlEnabled = !frozen;
            if (frozen && _interactor != null) _interactor.ClearPrompt();
        }

        /// <summary>
        /// Late-binds the transition director, for a cinematics layer that comes up after this one.
        /// Passing null clears it and reverts to the fallback hold.
        /// </summary>
        public void SetTransitionDirector(UnityEngine.Object director)
        {
            _transitionDirectorObject = director;
            _transitions = ServiceBridge.Resolve<ITransitionDirector>(director);
        }

        /// <summary>Late-binds the battle staging host. Same reasoning as the transition director.</summary>
        public void SetBattleStage(UnityEngine.Object stage)
        {
            _battleStageObject = stage;
            _battleStage = new ServiceBridge(stage);
        }
    }
}
