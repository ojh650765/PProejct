using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The single place the new Input System is touched.
    ///
    /// Actions are resolved by map/action name off a serialized <see cref="InputActionAsset"/>
    /// rather than through a generated wrapper class, because the generated wrapper is a
    /// checked-in file that regenerates whenever anyone edits the asset — a guaranteed merge
    /// conflict across eight parallel workers.
    ///
    /// Everything downstream reads cached values from here, so gameplay never subscribes to
    /// input directly and freezing the player is a single flag.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public sealed class OverworldInputReader : MonoBehaviour
    {
        [Header("Action asset")]
        [Tooltip("Assign Assets/InputSystem_Actions.inputactions.")]
        [SerializeField] private InputActionAsset _actions;
        [SerializeField] private string _mapName = "Player";
        [SerializeField] private string _moveAction = "Move";
        [SerializeField] private string _lookAction = "Look";
        [SerializeField] private string _sprintAction = "Sprint";
        [SerializeField] private string _interactAction = "Interact";
        [SerializeField] private string _menuAction = "Crouch";

        [Header("Look tuning")]
        [Tooltip("Degrees per unit of mouse delta.")]
        [SerializeField] private float _mouseLookSensitivity = 0.12f;
        [Tooltip("Degrees per second at full gamepad stick deflection.")]
        [SerializeField] private float _gamepadLookSensitivity = 220f;
        [SerializeField] private bool _invertLookY;

        [Header("Behaviour")]
        [Tooltip("Sprint held vs. sprint toggled. Toggle suits long routes; hold suits short bursts.")]
        [SerializeField] private bool _runIsToggle = true;

        private InputAction _move, _look, _sprint, _interact, _menu;
        private bool _runToggleState;
        private bool _enabled = true;

        /// <summary>Raw stick/WASD vector, already deadzoned by the action's processors.</summary>
        public Vector2 Move { get; private set; }

        /// <summary>Look delta in degrees for this frame, sensitivity and device already applied.</summary>
        public Vector2 Look { get; private set; }

        /// <summary>True while the player should be running, honouring hold vs toggle mode.</summary>
        public bool Run { get; private set; }

        /// <summary>True on the frame interact was pressed. Consumed by <see cref="PlayerInteractor"/>.</summary>
        public bool InteractPressed { get; private set; }

        public bool MenuPressed { get; private set; }

        /// <summary>
        /// True on the frame the interact button was pressed, <em>ignoring</em>
        /// <see cref="InputEnabled"/>. Dialogue advances on this: input is gated off so the player
        /// cannot walk during a conversation, but the confirm button must still work. Gameplay
        /// interaction uses <see cref="InteractPressed"/>, which respects the gate.
        /// </summary>
        public bool SubmitPressed { get; private set; }

        /// <summary>
        /// True on the frame the player asked to move a conversation on.
        ///
        /// Separate from <see cref="SubmitPressed"/> because that one reads the Interact
        /// action, which is bound to E — the key you press to start talking to somebody. Using
        /// the same key to advance the line meant the button that opens a conversation is the
        /// button that skips through it. Space is what these games use, and it is not bound to
        /// anything in the world.
        ///
        /// The gamepad face buttons come along, so a controller still works and so the play
        /// probe — which drives a synthetic pad through the real action asset — can still get
        /// through a conversation.
        /// </summary>
        public bool AdvancePressed { get; private set; }

        /// <summary>Raised on interact so interactables can react without polling.</summary>
        public event Action Interacted;

        /// <summary>
        /// Master gate. <see cref="GameFlowController"/> clears this for transitions, dialogue
        /// and battle so no other system has to know how to stop the player.
        /// </summary>
        public bool InputEnabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!_enabled)
                {
                    // Zero on disable, otherwise the locomotion keeps coasting on a stale vector.
                    Move = Vector2.zero;
                    Look = Vector2.zero;
                    Run = false;
                    _runToggleState = false;
                }
            }
        }

        private void Awake()
        {
            if (_actions == null)
            {
                Debug.LogError("[OverworldInputReader] No InputActionAsset assigned; the player will not move.", this);
                return;
            }

            var map = _actions.FindActionMap(_mapName, false);
            if (map == null)
            {
                Debug.LogError($"[OverworldInputReader] Action map '{_mapName}' not found.", this);
                return;
            }

            _move = map.FindAction(_moveAction, false);
            _look = map.FindAction(_lookAction, false);
            _sprint = map.FindAction(_sprintAction, false);
            _interact = map.FindAction(_interactAction, false);
            _menu = map.FindAction(_menuAction, false);

            map.Enable();
        }

        private void OnDisable()
        {
            // Leave the map enabled — other maps in the asset may be shared with the UI worker —
            // but drop our cached state so a re-enable starts clean.
            Move = Vector2.zero;
            Look = Vector2.zero;
            Run = false;
            InteractPressed = false;
            SubmitPressed = false;
            AdvancePressed = false;
            MenuPressed = false;
        }

        private void Update()
        {
            InteractPressed = false;
            MenuPressed = false;

            // Read before the gate: dialogue and menus need confirm even while movement is frozen.
            SubmitPressed = _interact != null && _interact.WasPressedThisFrame();

            // Read off the devices rather than through an action, because the asset's Interact
            // is the E binding this is deliberately not using and adding a second action to a
            // shared asset would change what every other map sees.
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            var pad = UnityEngine.InputSystem.Gamepad.current;
            AdvancePressed =
                (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
                || (pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.buttonNorth.wasPressedThisFrame));

            if (!_enabled) return;

            Move = _move != null ? _move.ReadValue<Vector2>() : Vector2.zero;
            Look = ReadLook();

            var sprintHeld = _sprint != null && _sprint.IsPressed();
            if (_runIsToggle)
            {
                if (_sprint != null && _sprint.WasPressedThisFrame()) _runToggleState = !_runToggleState;
                // Standing still cancels the toggle so you never sprint out of an idle by accident.
                if (Move.sqrMagnitude < 0.0001f) _runToggleState = false;
                Run = _runToggleState;
            }
            else
            {
                Run = sprintHeld;
            }

            // Space talks as well as advances, which is how the series works: one button both
            // opens a conversation and moves it on. E is kept as an alias rather than replaced,
            // because it is what the action asset binds and what anything reading the Interact
            // action directly still expects.
            //
            // Gated on _enabled, unlike AdvancePressed itself — dialogue needs the button while
            // movement is frozen, but walking up to somebody and talking to them is movement's
            // business and must stop when movement does.
            if (SubmitPressed || AdvancePressed)
            {
                InteractPressed = true;
                Interacted?.Invoke();
            }

            if (_menu != null && _menu.WasPressedThisFrame()) MenuPressed = true;
        }

        private Vector2 ReadLook()
        {
            if (_look == null) return Vector2.zero;
            var raw = _look.ReadValue<Vector2>();
            if (raw.sqrMagnitude < 0.000001f) return Vector2.zero;

            var control = _look.activeControl;

            // A touchscreen is a Pointer, so a thumb dragging the on-screen stick would land
            // here as look delta and yaw the camera while steering. This game has no
            // touch-look: on a touchscreen the camera belongs to the rig, never the finger.
            if (control != null && control.device is Touchscreen) return Vector2.zero;

            // Mouse delta is per-frame pixels; a stick is a normalised, frame-rate independent
            // rate. Treating them the same makes one of the two feel broken, so branch on device.
            var isPointerDelta = control != null && control.device is Pointer;

            var scaled = isPointerDelta
                ? raw * _mouseLookSensitivity
                : raw * (_gamepadLookSensitivity * Time.deltaTime);

            if (_invertLookY) scaled.y = -scaled.y;
            return scaled;
        }

        /// <summary>Clears the one-frame flags. Called by consumers that handle the press so it
        /// is not handled twice in the same frame by two listeners.</summary>
        public void ConsumeInteract() => InteractPressed = false;
    }
}
