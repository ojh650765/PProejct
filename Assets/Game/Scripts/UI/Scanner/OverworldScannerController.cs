using UnityEngine;
using UnityEngine.InputSystem;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// Raises the handheld scanner in the overworld, aims it, and requests a pre-battle
    /// scout from the oracle when it locks onto something.
    ///
    /// The environment artist is modelling the physical prop; this owns the screen content
    /// and the aiming behaviour. Two things it deliberately does not do: it never moves the
    /// camera (the cinematics worker owns every camera blend in this project), and it never
    /// starts an encounter — a scan is information, and committing to a fight stays the
    /// overworld's decision.
    ///
    /// Scouts are cached per target so sweeping the reticle across a field does not hammer
    /// the oracle with a prediction per frame.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldScannerController : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private PokeLabScannerView _scanner;
        [SerializeField] private ScannerReticle _reticle;
        [Tooltip("Camera the aim ray is cast from. Falls back to Camera.main.")]
        [SerializeField] private Camera _aimCamera;
        [Tooltip("Canvas rect the reticle is positioned inside.")]
        [SerializeField] private RectTransform _canvasRect;
        [Tooltip("Leave null for a Screen Space - Overlay canvas; assign the UI camera otherwise.")]
        [SerializeField] private Camera _canvasCamera;
        [Tooltip("Optional prop root — the modelled handheld. Shown while raised.")]
        [SerializeField] private GameObject _propRoot;

        [Header("Input")]
        [Tooltip("Action that raises the scanner. Hold or toggle per _holdToAim.")]
        [SerializeField] private InputActionReference _raiseAction;
        [Tooltip("True to hold the button, false to toggle on press.")]
        [SerializeField] private bool _holdToAim = true;
        [Tooltip("Fallback key when no action is assigned, so the feature is testable immediately.")]
        [SerializeField] private Key _fallbackKey = Key.Q;

        [Header("Aiming")]
        [SerializeField] private LayerMask _scanMask = ~0;
        [SerializeField] private float _scanRange = 30f;
        [Tooltip("Radius of the aim probe. A little forgiveness stops the lock flickering while walking.")]
        [SerializeField] private float _aimRadius = 0.6f;
        [Tooltip("Seconds a lock is held after the ray stops hitting, to smooth over occlusion.")]
        [SerializeField] private float _lockGrace = 0.35f;

        private IPokeLabScanTarget _target;
        private IPokeLabScanTarget _scoutedTarget;
        // Cached alongside the target, because an encounter zone re-rolls its species while
        // the reticle is still locked to it and the reference alone would not notice.
        private int _scoutedSpeciesId = -1;
        private float _lostAt = -99f;
        private bool _raised;
        private bool _togglePending;

        /// <summary>True while the player is holding the scanner up.</summary>
        public bool IsRaised => _raised;

        /// <summary>The target currently locked, or null.</summary>
        public IPokeLabScanTarget Target => _target;

        private void OnEnable()
        {
            if (_raiseAction != null && _raiseAction.action != null) _raiseAction.action.Enable();
        }

        private void OnDisable()
        {
            if (_raiseAction != null && _raiseAction.action != null) _raiseAction.action.Disable();
            Lower();
        }

        private void Update()
        {
            UpdateRaiseState();
            if (!_raised) return;
            UpdateAim();
        }

        private void UpdateRaiseState()
        {
            var pressed = ReadRaiseInput(out var wasPressedThisFrame);

            if (_holdToAim)
            {
                if (pressed && !_raised) Raise();
                else if (!pressed && _raised) Lower();
                return;
            }

            if (wasPressedThisFrame)
            {
                if (_raised) Lower();
                else Raise();
            }
        }

        private bool ReadRaiseInput(out bool wasPressedThisFrame)
        {
            wasPressedThisFrame = false;

            var action = _raiseAction != null ? _raiseAction.action : null;
            if (action != null)
            {
                wasPressedThisFrame = action.WasPressedThisFrame();
                return action.IsPressed();
            }

            // No bound action yet: keyboard fallback keeps the feature exercisable during
            // integration instead of silently doing nothing.
            var keyboard = Keyboard.current;
            if (keyboard == null) return false;
            var key = keyboard[_fallbackKey];
            if (key == null) return false;
            wasPressedThisFrame = key.wasPressedThisFrame;
            return key.isPressed;
        }

        /// <summary>Raises the device: prop appears, screen boots, reticle fades in.</summary>
        public void Raise()
        {
            if (_raised) return;
            _raised = true;
            if (_propRoot != null) _propRoot.SetActive(true);
            _reticle?.SetVisible(true);
            _scanner?.SetMode(ScannerMode.Overworld);
            _scoutedTarget = null;
            _scoutedSpeciesId = -1;
        }

        /// <summary>Lowers the device and powers the screen down.</summary>
        public void Lower()
        {
            if (!_raised) return;
            _raised = false;
            _target = null;
            _scoutedTarget = null;
            _scoutedSpeciesId = -1;
            _reticle?.SetLocked(false);
            _reticle?.SetVisible(false);
            _scanner?.SetMode(ScannerMode.Hidden);
            if (_propRoot != null) _propRoot.SetActive(false);
        }

        private void UpdateAim()
        {
            var camera = _aimCamera != null ? _aimCamera : Camera.main;
            if (camera == null) return;

            // A locked creature can be destroyed while the grace period is still holding the
            // lock. Dropped here rather than left to the timer, because everything downstream
            // reads the target's transform: the reticle threw on the first destroyed frame and
            // took the rest of this method with it, so the clear below never ran either and the
            // exception repeated for as long as the scanner stayed up.
            if (_target != null && !ScanTargets.IsAlive(_target))
            {
                _target = null;
                _lostAt = -99f;
            }

            var origin = camera.transform.position;
            var direction = camera.transform.forward;
            IPokeLabScanTarget found = null;

            // Sphere cast rather than a ray: a hairline ray makes small world objects
            // effectively unscannable while the player is moving.
            if (Physics.SphereCast(origin, _aimRadius, direction, out var hit, _scanRange, _scanMask,
                    QueryTriggerInteraction.Collide))
            {
                found = hit.collider.GetComponentInParent<ScanTargetTag>();
            }

            if (found != null)
            {
                _target = found;
                _lostAt = -99f;
            }
            else if (_target != null)
            {
                // Grace period stops the lock strobing when the player walks behind a bush.
                if (_lostAt < 0f) _lostAt = Time.unscaledTime;
                if (Time.unscaledTime - _lostAt > _lockGrace)
                {
                    _target = null;
                    _scoutedTarget = null;
                }
            }

            UpdateReticle(camera);

            // Re-scouted on a change of species as well as a change of object: an encounter
            // zone rolls a different spawn per approach, and a cache keyed on the reference
            // alone kept showing the reading for whatever it used to be.
            if (_target != null &&
                (!ReferenceEquals(_target, _scoutedTarget) || _target.SpeciesId != _scoutedSpeciesId))
            {
                _scoutedTarget = _target;
                _scoutedSpeciesId = _target.SpeciesId;
                RequestScout(_target);
            }
            else if (_target == null && _scoutedTarget != null)
            {
                _scoutedTarget = null;
                _scoutedSpeciesId = -1;
                _scanner?.BindScout(null, 0, false);
            }
        }

        private void UpdateReticle(Camera camera)
        {
            if (_reticle == null) return;

            // Re-checked rather than trusting the field: this is the one place that dereferences
            // the target, so it is the one place a destroyed tag would throw from.
            var target = ScanTargets.IsAlive(_target) ? _target : null;
            var anchor = target?.LockAnchor;
            var screenPoint = anchor != null
                ? (Vector2)camera.WorldToScreenPoint(anchor.position)
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            _reticle.SetScreenPosition(screenPoint, _canvasCamera, _canvasRect);
            _reticle.SetLocked(target != null, target != null ? CaptionFor(target) : "SCANNING");
        }

        private static string CaptionFor(IPokeLabScanTarget target)
        {
            if (!string.IsNullOrWhiteSpace(target.DisplayName)) return target.DisplayName.ToUpperInvariant();
            return target.Kind switch
            {
                ScanTargetKind.Trainer => "TRAINER · LEAD SCAN",
                ScanTargetKind.WildCreature => "WILD · LOCKED",
                _ => "ENCOUNTER ZONE",
            };
        }

        /// <summary>
        /// Asks the oracle for a pre-battle prior. Degrades to an unresolved scan when the
        /// intelligence layer has not landed — which is the normal state during integration,
        /// not an error.
        /// </summary>
        private void RequestScout(IPokeLabScanTarget target)
        {
            var oracle = UiServices.Oracle;
            if (oracle == null || !oracle.IsReady || target.SpeciesId <= 0)
            {
                _scanner?.BindScout(null, target.SpeciesId, false);
                return;
            }

            var lead = LeadCreature();
            var prediction = oracle.ScoutEncounter(lead, target.SpeciesId);

            // The confidence flag on an overworld scout means "have we met this species
            // before", which is what gates real intel in the battle readout too.
            _scanner?.BindScout(prediction, target.SpeciesId, UiServices.HasSeen(target.SpeciesId));
        }

        private static CreatureInstance LeadCreature()
        {
            var party = UiServices.Profile?.Party;
            if (party == null) return null;
            for (var i = 0; i < party.Count; i++)
            {
                if (party[i] != null && !party[i].IsFainted) return party[i];
            }
            return party.Count > 0 ? party[0] : null;
        }
    }
}
