using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// Communicates <see cref="Core.TacticalReadout.IsConfident"/>.
    ///
    /// The brief is explicit that the scanner must never silently show a confident wrong
    /// number, and a small grey caption is silent in practice. So low confidence is
    /// expressed three ways at once: the badge turns amber and names the reason, it pulses
    /// slowly so it is caught peripherally, and the caller desaturates the headline number.
    /// High confidence is deliberately quiet — a green tick on every readout becomes chrome
    /// within a minute and stops meaning anything.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConfidenceBadge : MonoBehaviour
    {
        [SerializeField] private Image _background;
        [SerializeField] private Image _icon;
        [SerializeField] private TextMeshProUGUI _label;
        [SerializeField] private CanvasGroup _group;

        private TweenHandle _pulse;
        private float _pulseTime;
        private bool _confident = true;

        /// <summary>Whether the last bind reported a confident readout.</summary>
        public bool IsConfident => _confident;

        /// <summary>
        /// Sets the confidence state. <paramref name="reason"/> overrides the default copy —
        /// pass something specific when the caller knows what is missing.
        /// </summary>
        public void Bind(bool confident, string reason = null)
        {
            _confident = confident;

            if (confident)
            {
                UiTween.Kill(ref _pulse);
                if (_group != null) _group.alpha = 0.55f;
                if (_background != null) _background.color = UiPalette.ScannerCyanDim.WithAlpha(0.28f);
                if (_icon != null)
                {
                    _icon.sprite = UiSprites.Dot(16);
                    _icon.color = UiPalette.ScannerCyan;
                }
                if (_label != null)
                {
                    _label.SetText(reason ?? "Full scan");
                    _label.color = UiPalette.ScannerCyan.WithAlpha(0.9f);
                }
                return;
            }

            if (_group != null) _group.alpha = 1f;
            if (_background != null) _background.color = UiPalette.Caution.WithAlpha(0.18f);
            if (_icon != null)
            {
                _icon.sprite = UiSprites.WarningGlyph(28);
                _icon.color = UiPalette.Caution;
            }
            if (_label != null)
            {
                _label.SetText(reason ?? "Partial scan — estimate may shift");
                _label.color = UiPalette.Caution;
            }

            // Slow, shallow pulse. Fast enough to notice, slow enough not to nag.
            // Driven from Update rather than a self-restarting tween: with reduced motion
            // enabled a tween completes on the same frame it starts, and a completion
            // callback that re-armed itself would recurse until the stack gave out.
            UiTween.Kill(ref _pulse);
            _pulseTime = 0f;
        }

        private void Update()
        {
            if (_confident || _group == null) return;
            if (!UiTween.MotionEnabled) { _group.alpha = 1f; return; }

            _pulseTime += Time.unscaledDeltaTime;
            var phase = Mathf.Sin(_pulseTime * Mathf.PI * 2f / 2.4f) * 0.5f + 0.5f;
            _group.alpha = Mathf.Lerp(0.62f, 1f, phase);
        }

        private void OnDisable() => UiTween.Kill(ref _pulse);

        /// <summary>Builds the badge as a pill sized for the scanner header.</summary>
        public static ConfidenceBadge Build(Transform parent, float height = 22f)
        {
            // Layout group on the root so the pill reports its own width to the header row.
            var root = UiBuilder.Rect("ConfidenceBadge", parent, false);
            UiBuilder.Horizontal(root, 6f, new RectOffset(9, 12, 0, 0), TextAnchor.MiddleLeft);
            UiBuilder.Size(root, preferredHeight: height, minHeight: height);

            var badge = root.gameObject.AddComponent<ConfidenceBadge>();
            badge._group = UiBuilder.Group(root, 0.55f, false, false);

            badge._background = UiBuilder.Backdrop("Bg", root, UiSprites.Pill(Mathf.RoundToInt(height)),
                UiPalette.ScannerCyanDim.WithAlpha(0.28f));

            badge._icon = UiBuilder.Image("Icon", root, UiSprites.Dot(16), UiPalette.ScannerCyan, Image.Type.Simple);
            badge._icon.preserveAspect = true;
            UiBuilder.Size(badge._icon.rectTransform, preferredWidth: height * 0.5f, preferredHeight: height * 0.5f,
                minWidth: height * 0.5f, minHeight: height * 0.5f);

            badge._label = UiBuilder.Text("Label", root, "Full scan", UiTextRole.Caption, UiPalette.ScannerCyan);
            badge._label.fontSize = 11f;
            badge._label.characterSpacing = 1f;
            badge._label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;

            return badge;
        }
    }
}
