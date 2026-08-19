using PokeLab.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.OnScreen;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// A thumbstick, an action button and a look surface, for playing on a phone.
    ///
    /// The web build is the version people will actually open, and on a phone it has no
    /// controls at all: the game reads a keyboard and a gamepad, and a browser on a
    /// touchscreen offers neither. You can tap through a conversation and never move.
    ///
    /// The controls are Input System on-screen controls, not a parallel input path: the
    /// stick injects into <c>&lt;Gamepad&gt;/leftStick</c> and the button into
    /// <c>&lt;Gamepad&gt;/buttonNorth</c>, which are exactly the paths the action asset and
    /// <c>OverworldInputReader</c> already bind. Camera look is the one exception — a drag
    /// on the right half of the screen routes pixel deltas through
    /// <see cref="TouchLookRoute"/> into the reader's own look path, because the Look
    /// action's <c>&lt;Pointer&gt;/delta</c> binding cannot tell a camera drag from the
    /// stick's thumb. Everything downstream — movement, the dialogue advance, the interact
    /// prompt — works unchanged, and the input reader's freeze gate applies to a thumb the
    /// same way it applies to a keyboard.
    ///
    /// Self-bootstrapped like <c>AvPresenterHost</c>, because no scene may be edited and
    /// every scene needs it. Built rather than authored, like every other screen here.
    ///
    /// Shown only where a touchscreen exists, and hidden for the length of a battle: that
    /// screen is tap-driven end to end, so the pad has nothing to steer and would only
    /// cover the command buttons.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TouchControls : MonoBehaviour
    {
        // Under the dialogue box at 400 and the menus at 460: any full-screen UI that
        // offers its own tap targets covers these, which is right — dialogue already
        // advances on a tap anywhere via its ClickCatcher. Battle goes further and hides
        // the pad outright; see Refresh.
        private const int SortingOrder = 300;

        // Canvas units on the 1080-high reference resolution.
        private const float ZoneSize = 430f;      // ~40% of screen height: the grabbable footprint
        private const float RingSize = 300f;
        private const float KnobSize = 130f;
        private const float StickRange = 100f;    // drag distance for full deflection
        private const float ButtonSize = 170f;

        private const string StickPath = "<Gamepad>/leftStick";
        private const string ActionPath = "<Gamepad>/buttonNorth";

        private static TouchControls s_instance;

        private Canvas _canvas;
        private RectTransform _knob;
        private bool _built;
        private IGameFlow _flow;
        private bool _battleHidden;

        /// <summary>
        /// Self-bootstraps after the first scene loads. AfterSceneLoad so a scene-authored
        /// EventSystem has had its Awake before <see cref="UiBuilder.EnsureEventSystem"/>
        /// looks for one — creating a second EventSystem is the failure this avoids.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (s_instance != null) return;
            var go = new GameObject("PL_TouchControls");
            DontDestroyOnLoad(go);
            go.AddComponent<TouchControls>();
        }

        /// <summary>
        /// Whether this device wants touch controls at all.
        ///
        /// Not "is it WebGL": a laptop opening the page has a keyboard, and drawing a
        /// thumbstick over the game for someone holding a mouse is clutter. A touchscreen
        /// device — or Unity reporting a mobile browser — is the actual question.
        /// </summary>
        private static bool Wanted => Touchscreen.current != null || Application.isMobilePlatform;

        private void Awake()
        {
            if (s_instance != null && s_instance != this) { Destroy(gameObject); return; }
            s_instance = this;

            // WebGL can register the touchscreen after startup, so visibility follows the
            // device list rather than being decided once at boot.
            InputSystem.onDeviceChange += OnDeviceChange;
            Refresh();
        }

        private void OnDestroy()
        {
            if (s_instance != this) return;
            InputSystem.onDeviceChange -= OnDeviceChange;
            s_instance = null;
        }

        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (!(device is Touchscreen)) return;
            if (change == InputDeviceChange.Added || change == InputDeviceChange.Removed
                || change == InputDeviceChange.Enabled || change == InputDeviceChange.Disabled)
                Refresh();
        }

        /// <summary>
        /// Battle hiding polls the flow service, same idiom as BattlePropCurtain: the
        /// service registers after this host boots, and a poll self-heals across scene
        /// loads where an event subscription would need re-acquisition anyway. Runs only
        /// once the canvas exists, so a desktop never pays for it.
        /// </summary>
        private void Update()
        {
            if (!_built) return;
            if (_flow == null && !ServiceHub.TryGet(out _flow)) return;

            var inBattle = _flow.Mode == GameMode.Battle || _flow.Mode == GameMode.BattleOutro;
            if (inBattle == _battleHidden) return;
            _battleHidden = inBattle;
            Refresh();
        }

        private void Refresh()
        {
            // Built on first demand, never on desktop: a machine that never reports a
            // touchscreen never allocates a canvas, so the desktop build is untouched.
            if (Wanted && !_built) Build();
            if (!_built) return;

            // Hidden through Battle and BattleOutro: that UI is tap-driven and owns the
            // whole screen, so the pad would only cover its buttons. The touch-device rule
            // stays the outer condition — a desktop never shows the pad in any mode.
            var visible = Wanted && !_battleHidden;
            if (_canvas.gameObject.activeSelf == visible) return;
            _canvas.gameObject.SetActive(visible);

            // Deactivating the canvas disables the on-screen controls, whose OnDisable
            // resets a non-default control and removes the virtual gamepad with the last
            // control — a stick held when the battle starts cannot latch its input. Only
            // the knob's rect needs help: a hide mid-drag skips OnPointerUp, and the knob
            // would come back visually deflected.
            if (!visible && _knob != null) _knob.anchoredPosition = Vector2.zero;
        }

        // --- construction ----------------------------------------------------------------

        private void Build()
        {
            _built = true;

            var canvasGo = new GameObject("TouchCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(_canvas, SortingOrder);
            UiBuilder.EnsureEventSystem();

            // The look pad: an invisible drag surface over the right half of the screen.
            // Without it the only yaw on a phone is AutoFollow, which reads as a camera
            // moving on its own. First child on purpose — the A button and the stick are
            // later in the hierarchy, so they render above and win the raycast wherever
            // they overlap it: a touch on the button presses the button, never looks.
            var pad = UiBuilder.Image("LookPad", canvasGo.transform, null,
                new Color(0f, 0f, 0f, 0f), Image.Type.Simple, raycast: true);
            pad.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            pad.rectTransform.anchorMax = Vector2.one;
            pad.rectTransform.offsetMin = Vector2.zero;
            pad.rectTransform.offsetMax = Vector2.zero;
            pad.gameObject.AddComponent<LookPad>();

            // Notches and rounded phone corners: the container tracks Screen.safeArea, so
            // neither control is ever authored flush to a physical edge it cannot use.
            var safe = UiBuilder.Rect("SafeArea", canvasGo.transform);
            canvasGo.AddComponent<SafeAreaFitter>().SetTarget(safe);

            _knob = BuildStick(safe);
            BuildActionButton(safe);
        }

        /// <summary>
        /// The stick: a fixed ring bottom-left with a knob the whole zone can grab.
        ///
        /// The OnScreenStick lives on the knob and the knob's image is the only raycast
        /// target, with its raycast padding expanded to the full zone — a thumb landing
        /// anywhere in the corner starts steering without having to hit a 130-unit circle.
        /// RelativePositionWithStaticOrigin because it is the only behaviour whose maths is
        /// a pure delta: the exact-position modes resolve against canvas-local space and
        /// misplace a stick whose parent is not centred on the canvas.
        /// </summary>
        private static RectTransform BuildStick(RectTransform safe)
        {
            var zone = UiBuilder.Rect("StickZone", safe, false);
            UiBuilder.Anchor(zone, Vector2.zero, Vector2.zero, Vector2.zero,
                new Vector2(28f, 40f), new Vector2(ZoneSize, ZoneSize));

            var ring = UiBuilder.Image("Ring", zone, UiSprites.RingSprite(256, 0.07f),
                new Color(1f, 1f, 1f, 0.20f), Image.Type.Simple);
            UiBuilder.Anchor(ring.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(RingSize, RingSize));

            var knob = UiBuilder.Image("Knob", zone, UiSprites.Dot(256),
                new Color(1f, 1f, 1f, 0.35f), Image.Type.Simple, raycast: true);
            UiBuilder.Anchor(knob.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(KnobSize, KnobSize));

            var grabMargin = (ZoneSize - KnobSize) * 0.5f;
            knob.raycastPadding = new Vector4(-grabMargin, -grabMargin, -grabMargin, -grabMargin);

            var feedback = knob.gameObject.AddComponent<PressFeedback>();
            feedback.Target = knob;
            feedback.IdleAlpha = 0.35f;
            feedback.HeldAlpha = 0.55f;

            var stick = knob.gameObject.AddComponent<OnScreenStick>();
            stick.movementRange = StickRange;
            stick.controlPath = StickPath;
            return knob.rectTransform;
        }

        /// <summary>
        /// The action button, bottom-right, where a right thumb rests. buttonNorth is this
        /// game's talk-and-advance: the input reader turns it into InteractPressed while the
        /// world is live and AdvancePressed inside a conversation, so one button both opens
        /// a conversation and moves it on — same as Space on a keyboard.
        /// </summary>
        private static void BuildActionButton(RectTransform safe)
        {
            var face = UiBuilder.Image("ActionButton", safe, UiSprites.Dot(256),
                UiPalette.ScannerCyan.WithAlpha(0.26f), Image.Type.Simple, raycast: true);
            UiBuilder.Anchor(face.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 0f), new Vector2(-48f, 100f), new Vector2(ButtonSize, ButtonSize));
            face.raycastPadding = new Vector4(-20f, -20f, -20f, -20f);

            var rim = UiBuilder.Image("Rim", face.rectTransform, UiSprites.RingSprite(256, 0.07f),
                UiPalette.ScannerCyan.WithAlpha(0.85f), Image.Type.Simple);
            UiBuilder.Stretch(rim.rectTransform);

            var label = UiBuilder.Text("Label", face.rectTransform, "A", UiTextRole.Title,
                UiPalette.TextPrimary, TMPro.TextAlignmentOptions.Center);
            UiBuilder.Stretch(label.rectTransform);
            label.fontSize = 64f;
            label.raycastTarget = false;

            var feedback = face.gameObject.AddComponent<PressFeedback>();
            feedback.Target = face;
            feedback.IdleAlpha = 0.26f;
            feedback.HeldAlpha = 0.45f;

            var button = face.gameObject.AddComponent<OnScreenButton>();
            button.controlPath = ActionPath;
        }

        // --- helpers -----------------------------------------------------------------------

        /// <summary>
        /// Keeps a stretched rect glued to <see cref="Screen.safeArea"/>. Reapplied from the
        /// canvas rect's own change callback — rotation and resolution changes resize the
        /// canvas, so no per-frame polling is needed.
        /// </summary>
        private sealed class SafeAreaFitter : MonoBehaviour
        {
            private RectTransform _target;
            private Rect _applied;

            // OnEnable has already run by the time AddComponent returns, so the target must
            // apply on assignment or the first layout would wait for a resolution change.
            public void SetTarget(RectTransform target)
            {
                _target = target;
                Apply();
            }

            private void OnEnable() => Apply();
            private void OnRectTransformDimensionsChange() => Apply();

            private void Apply()
            {
                if (_target == null) return;
                var safe = Screen.safeArea;
                if (safe == _applied) return;
                _applied = safe;

                var width = Mathf.Max(1f, Screen.width);
                var height = Mathf.Max(1f, Screen.height);
                _target.anchorMin = new Vector2(safe.xMin / width, safe.yMin / height);
                _target.anchorMax = new Vector2(safe.xMax / width, safe.yMax / height);
                _target.offsetMin = Vector2.zero;
                _target.offsetMax = Vector2.zero;
            }
        }

        /// <summary>
        /// Camera look from a drag on the right half of the screen, fed to the input reader
        /// through <see cref="TouchLookRoute"/> in raw screen pixels — the reader applies
        /// its mouse degrees-per-pixel, so touch and mouse look at identical sensitivity.
        ///
        /// uGUI's per-pointer capture is the claiming rule: a touch is this pad's only if
        /// it began on the pad — began on the stick and it stays the stick's for its whole
        /// life, began on a button and it is a press — which is what lets one thumb steer
        /// while the other looks. Touch pointers only: mouse and pen already reach the
        /// reader through the Look action, and counting them here too would double every
        /// right-half drag on a touch laptop.
        /// </summary>
        private sealed class LookPad : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
        {
            private const int None = int.MinValue;

            // One finger owns the camera: a second touch landing on the pad while the
            // first still drags would double the rate, so it is ignored until the owner
            // lifts.
            private int _pointer = None;

            public void OnPointerDown(PointerEventData eventData)
            {
                if (_pointer == None && IsTouch(eventData)) _pointer = eventData.pointerId;
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (eventData.pointerId == _pointer) TouchLookRoute.Add(eventData.delta);
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                if (eventData.pointerId == _pointer) _pointer = None;
            }

            // The pad hides with the canvas (battle, touchscreen removal); the claim must
            // not survive into the next show or a recycled pointer id could steal it.
            private void OnDisable() => _pointer = None;

            private static bool IsTouch(PointerEventData eventData) =>
                eventData is ExtendedPointerEventData extended
                && extended.pointerType == UIPointerType.Touch;
        }

        /// <summary>
        /// Held state a thumb can see from under itself: the control brightens while
        /// pressed. The on-screen components expose no press event, but pointer handlers on
        /// the same GameObject receive the same dispatch, so this rides alongside them.
        /// </summary>
        private sealed class PressFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
        {
            public Graphic Target;
            public float IdleAlpha;
            public float HeldAlpha;

            public void OnPointerDown(PointerEventData eventData) => Set(HeldAlpha);
            public void OnPointerUp(PointerEventData eventData) => Set(IdleAlpha);

            // A control disabled mid-press (scene change, visibility flip) must not come
            // back lit.
            private void OnDisable() => Set(IdleAlpha);

            private void Set(float alpha)
            {
                if (Target == null) return;
                var color = Target.color;
                color.a = alpha;
                Target.color = color;
            }
        }
    }
}
