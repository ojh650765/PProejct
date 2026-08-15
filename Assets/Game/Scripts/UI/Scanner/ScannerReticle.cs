using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// The aiming reticle drawn over the world while the handheld scanner is raised.
    ///
    /// Four corner brackets that idle wide and converge when a target is acquired, plus a
    /// short caption. The convergence is the whole point: it gives the player unambiguous
    /// feedback about whether the device has locked on, without a word of instruction, and
    /// it makes aiming feel like operating a machine rather than moving a cursor.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScannerReticle : MonoBehaviour
    {
        [SerializeField] private RectTransform _root;
        [Tooltip("Rotating container for the brackets. The caption stays outside it so it never tilts.")]
        [SerializeField] private RectTransform _spinner;
        [SerializeField] private RectTransform[] _brackets = new RectTransform[4];
        [SerializeField] private Image[] _bracketImages = new Image[4];
        [SerializeField] private TextMeshProUGUI _caption;
        [SerializeField] private CanvasGroup _group;

        [Header("Tuning")]
        [SerializeField] private float _idleSpread = 92f;
        [SerializeField] private float _lockedSpread = 54f;

        private TweenHandle _spreadTween;
        private float _spread;
        private bool _locked;
        private float _spin;

        /// <summary>True when the reticle is showing a lock.</summary>
        public bool IsLocked => _locked;

        private void Update()
        {
            if (_spinner == null) return;
            // A slow idle rotation when unlocked; dead still when locked. Motion means
            // "searching", stillness means "found", and nobody has to be told.
            _spin = _locked ? Mathf.Lerp(_spin, 0f, Time.unscaledDeltaTime * 8f)
                            : _spin + Time.unscaledDeltaTime * 14f;
            _spinner.localEulerAngles = new Vector3(0f, 0f, _spin);
        }

        /// <summary>Moves the reticle to a screen position.</summary>
        public void SetScreenPosition(Vector2 screenPosition, Camera uiCamera, RectTransform canvasRect)
        {
            if (_root == null || canvasRect == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, screenPosition, uiCamera, out var local))
            {
                _root.anchoredPosition = Vector2.Lerp(_root.anchoredPosition, local, 0.35f);
            }
        }

        /// <summary>Sets lock state and the caption line beneath the brackets.</summary>
        public void SetLocked(bool locked, string caption = null)
        {
            if (_caption != null)
            {
                _caption.SetText(caption ?? (locked ? "TARGET ACQUIRED" : "SCANNING"));
                _caption.color = locked ? UiPalette.ScannerCyan : UiPalette.TextMuted;
            }

            for (var i = 0; i < _bracketImages.Length; i++)
            {
                if (_bracketImages[i] != null)
                {
                    _bracketImages[i].color = locked ? UiPalette.ScannerCyan : UiPalette.ScannerCyan.WithAlpha(0.45f);
                }
            }

            if (_locked == locked) return;
            _locked = locked;

            var target = locked ? _lockedSpread : _idleSpread;
            var from = _spread;
            UiTween.Kill(ref _spreadTween);
            _spreadTween = UiTween.Run(0.28f, t =>
            {
                _spread = Mathf.LerpUnclamped(from, target, t);
                ApplySpread();
            }, locked ? Ease.OutBack : Ease.OutCubic);

            if (locked) UiTween.Punch(_root, 0.08f, 0.3f);
        }

        /// <summary>Fades the whole reticle in or out.</summary>
        public void SetVisible(bool visible)
        {
            if (_group == null) return;
            UiTween.Fade(_group, visible ? 1f : 0f, 0.2f);
            if (!visible) _locked = false;
        }

        private void ApplySpread()
        {
            if (_brackets == null) return;
            // Corner order: TL, TR, BR, BL.
            var offsets = new[]
            {
                new Vector2(-_spread, _spread),
                new Vector2(_spread, _spread),
                new Vector2(_spread, -_spread),
                new Vector2(-_spread, -_spread),
            };
            for (var i = 0; i < _brackets.Length && i < offsets.Length; i++)
            {
                if (_brackets[i] != null) _brackets[i].anchoredPosition = offsets[i];
            }
        }

        /// <summary>Builds the reticle. Parent must be a full-screen canvas rect.</summary>
        public static ScannerReticle Build(Transform parent)
        {
            var root = UiBuilder.Rect("ScannerReticle", parent, false);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(260f, 260f);

            var reticle = root.gameObject.AddComponent<ScannerReticle>();
            reticle._root = root;
            reticle._group = UiBuilder.Group(root, 0f, false, false);
            reticle._spread = reticle._idleSpread;

            var spinner = UiBuilder.Rect("Spinner", root);
            reticle._spinner = spinner;

            var bracketSprite = UiSprites.Bracket(56, 5, 0.42f);
            for (var i = 0; i < 4; i++)
            {
                var image = UiBuilder.Image("Bracket" + i, spinner, bracketSprite,
                    UiPalette.ScannerCyan.WithAlpha(0.45f), Image.Type.Simple);
                image.preserveAspect = true;
                var rect = image.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(34f, 34f);
                // One bracket sprite reused at four rotations rather than four textures.
                rect.localEulerAngles = new Vector3(0f, 0f, -90f * i);
                reticle._brackets[i] = rect;
                reticle._bracketImages[i] = image;
            }

            var caption = UiBuilder.Text("Caption", root, "SCANNING", UiTextRole.Overline, UiPalette.TextMuted,
                TMPro.TextAlignmentOptions.Center);
            caption.fontSize = 10f;
            UiBuilder.Anchor(caption.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(220f, 16f));
            reticle._caption = caption;

            reticle.ApplySpread();
            return reticle;
        }
    }
}
