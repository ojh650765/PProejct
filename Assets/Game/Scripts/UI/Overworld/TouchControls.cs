using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// A thumbstick and an action button, for playing on a phone.
    ///
    /// The web build is the version people will actually open, and on a phone it currently has
    /// no controls at all: the game reads a keyboard and a gamepad, and a browser on a
    /// touchscreen offers neither. The whole thing is unplayable there, silently — nothing on
    /// screen says why nothing moves.
    ///
    /// Built rather than authored, like every other screen in this project, because the scenes
    /// are regenerated constantly and a serialized widget would have to survive every rebuild
    /// in three of them.
    ///
    /// The stick is *relative*, not fixed: the pad appears wherever the thumb lands inside the
    /// left half of the screen and measures from there. A fixed stick has to be found by
    /// looking, which on a phone means looking away from the game; a relative one is under your
    /// thumb by definition.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TouchControls : MonoBehaviour
    {
        // Under the dialogue box at 400 and the menu at 460: a conversation covers the stick,
        // which is right, because during one there is nothing to walk to.
        private const int SortingOrder = 300;

        private const float StickRadius = 130f;
        private const float DeadZone = 0.16f;

        [Tooltip("Fraction of the screen width, from the left, that steers. The rest is the " +
                 "camera's, so a thumb dragging to look never also walks.")]
        [SerializeField] private float _steerZone = 0.5f;

        private RectTransform _stickRoot;
        private RectTransform _stickKnob;
        private CanvasGroup _stickGroup;
        private Canvas _canvas;

        private int _stickFinger = -1;
        private Vector2 _stickOrigin;

        /// <summary>Where the stick is pushed, in the same shape a gamepad's left stick reports.</summary>
        public Vector2 Move { get; private set; }

        /// <summary>True on the frame the action button was pressed.</summary>
        public bool ActionPressed { get; private set; }

        /// <summary>True on the frame the menu button was pressed.</summary>
        public bool MenuPressed { get; private set; }

        /// <summary>
        /// The live instance, or null where there is no touchscreen.
        ///
        /// A static because the input reader has to ask for it every frame and lives in another
        /// assembly; a null answer is the ordinary case on a desktop and costs nothing.
        /// </summary>
        public static TouchControls Instance { get; private set; }

        /// <summary>
        /// Whether this device wants touch controls at all.
        ///
        /// Not "is it WebGL": a laptop opening the page has a keyboard, and drawing a thumbstick
        /// over the game for someone holding a mouse is clutter. Unity reports a touchscreen
        /// where one exists, which is the actual question.
        /// </summary>
        public static bool Wanted =>
            UnityEngine.InputSystem.Touchscreen.current != null;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Build();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            ActionPressed = false;
            MenuPressed = false;
            ReadStick();
        }

        // --- the stick -------------------------------------------------------------------

        private void ReadStick()
        {
            var screen = UnityEngine.InputSystem.Touchscreen.current;
            if (screen == null) { Move = Vector2.zero; Hide(); return; }

            foreach (var touch in screen.touches)
            {
                var phase = touch.phase.ReadValue();
                var id = touch.touchId.ReadValue();
                var position = touch.position.ReadValue();

                if (_stickFinger < 0
                    && phase == UnityEngine.InputSystem.TouchPhase.Began
                    && position.x < Screen.width * _steerZone
                    && !OverUi(position))
                {
                    _stickFinger = id;
                    _stickOrigin = position;
                    Show(position);
                }
                else if (id == _stickFinger)
                {
                    if (phase == UnityEngine.InputSystem.TouchPhase.Ended
                        || phase == UnityEngine.InputSystem.TouchPhase.Canceled)
                    {
                        _stickFinger = -1;
                        Move = Vector2.zero;
                        Hide();
                    }
                    else
                    {
                        Drag(position);
                    }
                }
            }
        }

        /// <summary>
        /// True when the touch landed on a button rather than on the world.
        ///
        /// Without it, tapping the action button also plants the stick under it and the
        /// character walks off while you are talking to somebody.
        /// </summary>
        private static bool OverUi(Vector2 position)
        {
            if (EventSystem.current == null) return false;
            var data = new PointerEventData(EventSystem.current) { position = position };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, hits);
            return hits.Count > 0;
        }

        private void Drag(Vector2 position)
        {
            var delta = position - _stickOrigin;
            var pushed = Vector2.ClampMagnitude(delta / StickRadius, 1f);
            Move = pushed.magnitude < DeadZone ? Vector2.zero : pushed;

            if (_stickKnob != null)
                _stickKnob.anchoredPosition = pushed * StickRadius;
        }

        private void Show(Vector2 screenPosition)
        {
            if (_stickRoot == null) return;

            // Converted through the canvas rather than assigned in pixels: the scaler matches
            // by height, so a screen position and an anchored position are different units on
            // every device but the reference one.
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)_canvas.transform, screenPosition,
                _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera,
                out var local);

            _stickRoot.anchoredPosition = local;
            if (_stickKnob != null) _stickKnob.anchoredPosition = Vector2.zero;
            if (_stickGroup != null) _stickGroup.alpha = 1f;
        }

        private void Hide()
        {
            if (_stickGroup != null) _stickGroup.alpha = 0f;
        }

        // --- construction ----------------------------------------------------------------

        private void Build()
        {
            var canvasGo = new GameObject("TouchCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(_canvas, SortingOrder);
            UiBuilder.EnsureEventSystem();

            // The stick: a ring that appears under the thumb, with a knob inside it.
            _stickRoot = UiBuilder.Rect("Stick", canvasGo.transform, false);
            UiBuilder.Anchor(_stickRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(StickRadius * 2f, StickRadius * 2f));

            var ring = UiBuilder.Image("Ring", _stickRoot, UiSprites.Panel(128),
                new Color(1f, 1f, 1f, 0.16f), Image.Type.Simple);
            UiBuilder.Stretch(ring.rectTransform);
            ring.raycastTarget = false;

            _stickKnob = UiBuilder.Rect("Knob", _stickRoot, false);
            UiBuilder.Anchor(_stickKnob, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(96f, 96f));
            var knob = UiBuilder.Image("Fill", _stickKnob, UiSprites.Panel(64),
                new Color(1f, 1f, 1f, 0.42f), Image.Type.Simple);
            UiBuilder.Stretch(knob.rectTransform);
            knob.raycastTarget = false;

            _stickGroup = _stickRoot.gameObject.AddComponent<CanvasGroup>();
            _stickGroup.alpha = 0f;
            _stickGroup.blocksRaycasts = false;

            // The action button, bottom right, where a right thumb rests.
            BuildButton(canvasGo.transform, Loc.Pick("A", "A"), new Vector2(-150f, 170f), 148f,
                UiPalette.ScannerCyan, () => ActionPressed = true);

            // And the menu, smaller and further in, because it is pressed rarely and must not
            // be the thing a thumb finds by accident.
            BuildButton(canvasGo.transform, Loc.Pick("≡", "≡"), new Vector2(-116f, 372f), 96f,
                UiPalette.SurfaceRaised, () => MenuPressed = true);
        }

        private static void BuildButton(Transform parent, string label, Vector2 offset,
            float size, Color accent, System.Action onPress)
        {
            var root = UiBuilder.Rect("TouchButton_" + label, parent, false);
            UiBuilder.Anchor(root, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                offset, new Vector2(size, size));

            var face = UiBuilder.Image("Face", root, UiSprites.Panel(128),
                new Color(accent.r, accent.g, accent.b, 0.30f), Image.Type.Simple);
            UiBuilder.Stretch(face.rectTransform);

            var rim = UiBuilder.Image("Rim", root, UiSprites.PanelOutline(128, 8),
                new Color(accent.r, accent.g, accent.b, 0.85f), Image.Type.Simple);
            UiBuilder.Stretch(rim.rectTransform);
            rim.raycastTarget = false;

            var text = UiBuilder.Text("Label", root, label, UiTextRole.Title,
                UiPalette.TextPrimary, TMPro.TextAlignmentOptions.Center);
            UiBuilder.Stretch(text.rectTransform);
            text.raycastTarget = false;

            UiBuilder.Button("Press", root, face, () => onPress());
        }
    }
}
