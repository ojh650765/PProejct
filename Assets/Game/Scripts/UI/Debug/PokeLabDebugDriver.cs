using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PokeLab.UI.Debugging
{
    /// <summary>
    /// Drives the UI from <see cref="FakePokeLabOracle"/> so the scanner and HUD can be
    /// developed, reviewed and regression-checked before any other system exists.
    ///
    /// Everything it does is gated behind <see cref="_enabled"/>, which defaults to off, so
    /// the component is safe to leave on a prefab in a shipping scene. When it is on it will
    /// register fake services — but only ones nothing else has claimed, so dropping it into a
    /// fully integrated scene degrades to "drives nothing" rather than shadowing the real
    /// oracle.
    ///
    /// Controls while running: <c>N</c> steps one turn, <c>R</c> restarts the sequence,
    /// <c>Space</c> toggles auto-advance, <c>T</c> cycles the scanner mode.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PokeLabDebugDriver : MonoBehaviour
    {
        [Header("Enable")]
        [Tooltip("Master switch. Off in every shipping scene; the integrator flips it to verify UI.")]
        [SerializeField] private bool _enabled;
        [Tooltip("Register fake species, moves and profile services when nothing else has.")]
        [SerializeField] private bool _registerFakeServices = true;

        [Header("Simulation")]
        [SerializeField] private int _seed = 20260815;
        [SerializeField] private bool _autoAdvance = true;
        [Tooltip("Seconds between simulated turns. Long enough to watch each animation land.")]
        [SerializeField] private float _turnSeconds = 3.5f;
        [Tooltip("Turn on which the scripted paralysis swing fires. The moment the feature sells.")]
        [SerializeField] private int _scriptedSwingTurn = 4;

        [Header("Targets")]
        [Tooltip("Listeners driven each turn. Populated from the scene when left empty.")]
        [SerializeField] private MonoBehaviour[] _listenerBehaviours;
        [SerializeField] private PokeLabScannerView _scanner;
        [Tooltip("Optional. When present the whole battle HUD is shown and given a turn, not just the scanner.")]
        [SerializeField] private BattleHudView _battleHud;
        [SerializeField] private bool _openScannerOnStart = true;

        private readonly List<IPokeLabListener> _listeners = new List<IPokeLabListener>(4);
        private FakePokeLabOracle _oracle;
        private FakeSpeciesRegistry _species;
        private float _timer;

        /// <summary>The fake oracle, exposed so an editor-side harness can step it directly.</summary>
        public FakePokeLabOracle Oracle => _oracle;

        private void Awake()
        {
            if (!_enabled) return;

            _species = new FakeSpeciesRegistry();
            _oracle = new FakePokeLabOracle(_seed, _species) { ScriptedSwingTurn = _scriptedSwingTurn };

            if (_registerFakeServices)
            {
                // Never overwrite a real service. During integration the real registries land
                // one at a time, and a debug driver that clobbered them would produce bugs
                // that look like UI bugs.
                if (!ServiceHub.Has<ISpeciesRegistry>()) ServiceHub.Register<ISpeciesRegistry>(_species);
                if (!ServiceHub.Has<IMoveRegistry>()) ServiceHub.Register<IMoveRegistry>(new FakeMoveRegistry());
                if (!ServiceHub.Has<IPlayerProfile>()) ServiceHub.Register<IPlayerProfile>(new FakePlayerProfile(_species));
                if (!ServiceHub.Has<IPokeLabOracle>()) ServiceHub.Register<IPokeLabOracle>(_oracle);
                UiServices.Reset();
            }
        }

        private void Start()
        {
            if (!_enabled) return;

            CollectListeners();

            if (_openScannerOnStart)
            {
                if (_battleHud != null)
                {
                    // Showing the HUD is what actually reveals the scanner: the docked screen
                    // lives inside the HUD's CanvasGroup, which starts at zero alpha.
                    _battleHud.Show();
                    var lead = UiServices.Profile?.Party;
                    if (lead != null && lead.Count > 0) _battleHud.OnBattleEvent(new CreatureSentOutEvent
                    {
                        Side = BattleSide.Player,
                        Creature = lead[0],
                    });
                    if (lead != null && lead.Count > 1) _battleHud.OnBattleEvent(new CreatureSentOutEvent
                    {
                        Side = BattleSide.Opponent,
                        Creature = lead[1],
                    });
                    if (lead != null && lead.Count > 0) _battleHud.BeginPlayerTurn(lead[0]);
                }
                else if (_scanner != null)
                {
                    _scanner.SetMode(ScannerMode.Docked);
                }
            }

            // First readout after the boot sequence has had time to run, so the opening
            // numbers arrive on a lit screen rather than during the power-on fade.
            UiTween.Delay(1.1f, Step);
        }

        private void Update()
        {
            if (!_enabled) return;

            ReadDebugKeys();

            if (!_autoAdvance) return;
            _timer += Time.unscaledDeltaTime;
            if (_timer < _turnSeconds) return;
            _timer = 0f;
            Step();
        }

        /// <summary>Advances one simulated turn and pushes the readout to every listener.</summary>
        public void Step()
        {
            if (_oracle == null) return;
            var readout = _oracle.Advance();
            for (var i = 0; i < _listeners.Count; i++)
            {
                _listeners[i]?.OnReadoutUpdated(readout);
            }
        }

        /// <summary>Rewinds the sequence to turn zero.</summary>
        public void Restart()
        {
            _oracle?.Restart();
            _timer = 0f;
            Step();
        }

        /// <summary>
        /// Collects listeners from the serialized list, falling back to a scene sweep so the
        /// driver works when simply dropped next to a scanner with nothing wired.
        /// </summary>
        private void CollectListeners()
        {
            _listeners.Clear();

            if (_listenerBehaviours != null)
            {
                for (var i = 0; i < _listenerBehaviours.Length; i++)
                {
                    if (_listenerBehaviours[i] is IPokeLabListener listener) _listeners.Add(listener);
                }
            }

            if (_listeners.Count > 0) return;

#if UNITY_2023_1_OR_NEWER
            var behaviours = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var behaviours = Object.FindObjectsOfType<MonoBehaviour>(true);
#endif
            for (var i = 0; i < behaviours.Length; i++)
            {
                if (!(behaviours[i] is IPokeLabListener listener)) continue;

                // A docked scanner is already fed by its BattleHudView. Adding it here as
                // well would render every readout twice — two sweeps, two audio cues, and a
                // headline number that tweens from its own half-finished value.
                // includeInactive matters: the scanner deactivates its own root while hidden,
                // and the default overload only walks active parents.
                if (behaviours[i] is PokeLabScannerView &&
                    behaviours[i].GetComponentInParent<BattleHudView>(true) != null)
                {
                    continue;
                }

                _listeners.Add(listener);
            }

            for (var i = 0; i < behaviours.Length; i++)
            {
                if (_battleHud == null && behaviours[i] is BattleHudView hud) _battleHud = hud;
                if (_scanner == null && behaviours[i] is PokeLabScannerView view) _scanner = view;
            }
            if (_scanner == null && _battleHud != null) _scanner = _battleHud.Scanner;
        }

        private void ReadDebugKeys()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.nKey.wasPressedThisFrame) Step();
            if (keyboard.rKey.wasPressedThisFrame) Restart();
            if (keyboard.spaceKey.wasPressedThisFrame) _autoAdvance = !_autoAdvance;
            if (keyboard.tKey.wasPressedThisFrame && _scanner != null)
            {
                var next = _scanner.Mode switch
                {
                    ScannerMode.Hidden => ScannerMode.Docked,
                    ScannerMode.Docked => ScannerMode.Overworld,
                    _ => ScannerMode.Hidden,
                };
                _scanner.SetMode(next);
                if (next == ScannerMode.Overworld && _oracle != null && _species != null)
                {
                    _scanner.BindScout(_oracle.ScoutEncounter(null, _species.OpponentId), _species.OpponentId, true);
                }
            }
#endif
        }
    }
}
