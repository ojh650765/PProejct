using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace PokeLab.UI
{
    /// <summary>
    /// Makes Korean typeable in the web build.
    ///
    /// <b>The problem.</b> Unity's WebGL player builds characters out of <c>keydown</c> codes.
    /// An IME does not deliver characters that way: 한글 arrives as a <c>compositionstart</c>, a
    /// run of updates while the syllable is assembled from jamo, and a <c>compositionend</c>
    /// carrying the finished text — an event stream no key handler sees. So the login form
    /// accepted Latin letters and silently dropped everything typed in Korean, which is the
    /// user's report: 트레이너 이름이랑 답 칸이 한글 입력이 안됨. No <see cref="TMP_InputField"/>
    /// setting fixes it, because the text never reaches Unity at all.
    ///
    /// <b>The shape of the fix.</b> A real, transparent <c>&lt;input&gt;</c> is parked exactly
    /// over whichever field has focus (see <c>Assets/Plugins/WebGL/PokeLabIme.jslib</c>). The
    /// browser and the OS keyboard do what they are built to do; this copies the value back into
    /// the TMP field every frame, so TMP still draws the text, the caret and the placeholder and
    /// nothing else in the game knows the difference.
    ///
    /// <b>Why the capture flag is toggled rather than set once.</b> Unity claims every key on the
    /// document so that a game in a page still gets WASD. Left on, it eats the keys before the
    /// overlay sees them; turned off for the whole session, the game stops receiving keys any
    /// time the canvas is not the focused element, which is a much worse bug than the one being
    /// fixed. So it is off only while a text field is selected and back on the moment it is not.
    ///
    /// Outside WebGL this compiles to nothing: desktop and editor already route IME input into
    /// TMP through Unity's own <c>Input.compositionString</c>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WebGlImeBridge : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int PokeLabImeOpen(int x, int y, int w, int h, string text, int caret);

        [DllImport("__Internal")] private static extern void PokeLabImeClose();
        [DllImport("__Internal")] private static extern int PokeLabImeRead(byte[] buffer, int size);
        [DllImport("__Internal")] private static extern int PokeLabImeCaret();
        [DllImport("__Internal")] private static extern int PokeLabImeComposing();

        /// <summary>Bytes. A trainer name is 16 characters and an answer is a short phrase;
        /// this is four times the longest either could be in UTF-8 and is allocated once.</summary>
        private const int ReadBufferBytes = 512;

        private static WebGlImeBridge s_instance;

        private readonly byte[] _buffer = new byte[ReadBufferBytes];
        private TMP_InputField _field;
        private bool _captureSuspended;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (s_instance != null) return;
            var go = new GameObject("PL_ImeBridge");
            DontDestroyOnLoad(go);
            go.AddComponent<WebGlImeBridge>();
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this) { Destroy(gameObject); return; }
            s_instance = this;
        }

        private void OnDestroy()
        {
            if (s_instance != this) return;
            Release();
            s_instance = null;
        }

        private void LateUpdate()
        {
            var selected = Selected();

            if (selected != _field)
            {
                Release();
                _field = selected;
                if (_field == null) return;

                // Unity stops seeing keys from here until the field is dropped, which is what
                // lets the overlay have them.
                WebGLInput.captureAllKeyboardInput = false;
                _captureSuspended = true;
            }

            if (_field == null) return;

            // Placed every frame, not once on focus: the canvas is resizable, the page can
            // scroll, and the layout under a field can move while it is being typed into. The
            // jslib treats a repeat placement as a no-op.
            if (!TryScreenRect(_field, out var rect)) return;

            PokeLabImeOpen((int)rect.x, (int)rect.y, (int)rect.width, (int)rect.height,
                           _field.text ?? "", _field.caretPosition);

            PullFromBrowser();
        }

        /// <summary>
        /// Copies the browser's value into the TMP field, and its caret with it.
        ///
        /// Skipped mid-composition. The half-built syllable belongs to the IME until it says
        /// otherwise, and writing Unity's idea of the text over the top of it is what makes a
        /// field that eats every second keystroke.
        /// </summary>
        private void PullFromBrowser()
        {
            if (PokeLabImeComposing() != 0) return;

            var length = PokeLabImeRead(_buffer, _buffer.Length);
            if (length < 0) return;

            var value = System.Text.Encoding.UTF8.GetString(_buffer, 0, length);
            if (_field.characterLimit > 0 && value.Length > _field.characterLimit)
                value = value.Substring(0, _field.characterLimit);

            if (value == _field.text) return;

            // SetTextWithoutNotify would be wrong here: the whole point is that the screen and
            // anything validating the form learn that the player typed something.
            _field.text = value;

            var caret = Mathf.Clamp(PokeLabImeCaret(), 0, value.Length);
            _field.caretPosition = caret;
            _field.selectionAnchorPosition = caret;
            _field.selectionFocusPosition = caret;
        }

        private static TMP_InputField Selected()
        {
            var events = EventSystem.current;
            if (events == null) return null;

            var go = events.currentSelectedGameObject;
            if (go == null) return null;

            var field = go.GetComponent<TMP_InputField>();
            return field != null && field.isFocused && field.interactable && !field.readOnly
                ? field
                : null;
        }

        /// <summary>
        /// The field's rectangle in framebuffer pixels, measured from the TOP.
        ///
        /// The viewport rather than the field's own rect, because the viewport is the region the
        /// text is actually drawn in — an overlay the size of the whole control would put the
        /// IME's candidate window against the field's border rather than against its text.
        ///
        /// Y is flipped because Unity's screen origin is the bottom-left corner and the DOM's is
        /// the top-left one.
        /// </summary>
        private static bool TryScreenRect(TMP_InputField field, out Rect rect)
        {
            rect = default;

            var area = field.textViewport != null ? field.textViewport : field.transform as RectTransform;
            if (area == null) return false;

            var canvas = area.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;

            var corners = new Vector3[4];
            area.GetWorldCorners(corners);

            var bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            var topRight = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);

            var width = topRight.x - bottomLeft.x;
            var height = topRight.y - bottomLeft.y;
            if (width <= 1f || height <= 1f) return false;

            rect = new Rect(bottomLeft.x, Screen.height - topRight.y, width, height);
            return true;
        }

        private void Release()
        {
            if (_field == null && !_captureSuspended) return;

            _field = null;
            PokeLabImeClose();

            if (!_captureSuspended) return;
            WebGLInput.captureAllKeyboardInput = true;
            _captureSuspended = false;
        }
#endif
    }
}
