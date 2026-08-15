using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// The headline of the scanner: live win probability, the trend arrow, and the split bar.
    ///
    /// Two decisions carry this widget. First, the number counts and the bar tweens, so a
    /// paralysis landing for +18 points is a visible *movement* rather than a different
    /// screen. Second, a ghost marker is left at the previous value and fades over the next
    /// second — that is what lets the player see the size of the jump instead of having to
    /// remember the old number.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProbabilityGauge : MonoBehaviour
    {
        [Header("Readout")]
        [SerializeField] private AnimatedNumber _percent;
        [SerializeField] private TextMeshProUGUI _caption;

        [Header("Delta")]
        [SerializeField] private RectTransform _deltaGroup;
        [SerializeField] private Image _deltaArrow;
        [SerializeField] private TextMeshProUGUI _deltaLabel;
        [SerializeField] private CanvasGroup _deltaAlpha;

        [Header("Bar")]
        [SerializeField] private AnimatedBar _bar;
        [SerializeField] private RectTransform _barTrack;
        [SerializeField] private RectTransform _ghostMarker;
        [SerializeField] private Image _ghostImage;

        private float _probability = 0.5f;
        private TweenHandle _ghostTween;
        private TweenHandle _deltaTween;
        private bool _hasValue;

        /// <summary>Last probability handed to <see cref="Bind"/>, 0-1.</summary>
        public float Probability => _probability;

        /// <summary>
        /// Pushes a new probability and its delta in percentage points.
        /// Pass <paramref name="immediate"/> when opening the panel so it does not count up
        /// from zero every time the player toggles the scanner.
        /// </summary>
        public void Bind(float probability, float deltaPoints, bool confident, bool immediate = false)
        {
            probability = Mathf.Clamp01(probability);
            var previous = _probability;
            _probability = probability;

            var color = confident ? UiPalette.Probability(probability) : UiPalette.TextSecondary;

            if (_percent != null)
            {
                if (immediate || !_hasValue) _percent.SetImmediate(probability);
                else _percent.SetValue(probability);
                _percent.SetColor(color, immediate ? 0f : 0.4f);
            }

            if (_bar != null)
            {
                if (immediate || !_hasValue) _bar.SetImmediate(probability);
                else _bar.SetValue(probability, 0.7f);
                _bar.SetColorImmediate(color);
            }

            if (!immediate && _hasValue) ShowGhost(previous);
            BindDelta(deltaPoints, immediate || !_hasValue);

            _hasValue = true;
        }

        /// <summary>Sets the caption under the number, e.g. "vs Gyarados" or "estimated".</summary>
        public void SetCaption(string caption)
        {
            if (_caption != null) _caption.SetText(caption ?? string.Empty);
        }

        private void BindDelta(float deltaPoints, bool immediate)
        {
            if (_deltaGroup == null) return;

            var meaningful = Mathf.Abs(deltaPoints) >= 0.05f;
            var color = UiPalette.Delta(deltaPoints);

            if (_deltaLabel != null)
            {
                _deltaLabel.SetText(meaningful ? UiType.SignedPoints(deltaPoints) : "no change");
                _deltaLabel.color = meaningful ? color : UiPalette.TextMuted;
            }

            if (_deltaArrow != null)
            {
                _deltaArrow.enabled = meaningful;
                _deltaArrow.color = color;
                // One chevron sprite, flipped by rotation — down is the same mark, upside down.
                _deltaArrow.rectTransform.localEulerAngles = new Vector3(0f, 0f, deltaPoints >= 0f ? 0f : 180f);
            }

            if (immediate)
            {
                if (_deltaAlpha != null) _deltaAlpha.alpha = meaningful ? 1f : 0.5f;
                return;
            }

            UiTween.Kill(ref _deltaTween);
            if (!meaningful)
            {
                _deltaTween = UiTween.Fade(_deltaAlpha, 0.5f, 0.3f);
                return;
            }

            // The flash: three quick pulses on a big swing, one on a small one. The player
            // should be able to tell "something changed a lot" without reading the number.
            var pulses = Mathf.Abs(deltaPoints) >= 8f ? 3 : 1;
            _deltaTween = UiTween.Run(0.26f * pulses, t =>
            {
                if (_deltaAlpha == null) return;
                var phase = Mathf.Repeat(t * pulses, 1f);
                _deltaAlpha.alpha = Mathf.Lerp(0.35f, 1f, Mathf.Abs(Mathf.Sin(phase * Mathf.PI)));
            }, Ease.Linear, 0f, true, () => { if (_deltaAlpha != null) _deltaAlpha.alpha = 1f; });

            UiTween.Punch(_deltaGroup, Mathf.Abs(deltaPoints) >= 8f ? 0.28f : 0.14f);
        }

        private void ShowGhost(float previousProbability)
        {
            if (_ghostMarker == null || _barTrack == null || _ghostImage == null) return;

            // Park the marker at the old value in normalised track space, then fade it out.
            _ghostMarker.anchorMin = new Vector2(previousProbability, 0f);
            _ghostMarker.anchorMax = new Vector2(previousProbability, 1f);
            _ghostMarker.anchoredPosition = Vector2.zero;

            UiTween.Kill(ref _ghostTween);
            _ghostTween = UiTween.Run(1.1f, t =>
            {
                if (_ghostImage == null) return;
                _ghostImage.color = Color.white.WithAlpha((1f - t) * 0.75f);
            }, Ease.InCubic, 0.05f, true, () =>
            {
                if (_ghostImage != null) _ghostImage.color = Color.clear;
            });
        }

        /// <summary>Builds the full gauge. Height is driven by the metric type size.</summary>
        public static ProbabilityGauge Build(Transform parent)
        {
            var root = UiBuilder.Rect("ProbabilityGauge", parent);
            UiBuilder.Vertical(root, 6f, new RectOffset(0, 0, 0, 0));
            UiBuilder.Size(root, preferredHeight: 168f, minHeight: 140f, flexibleWidth: 1f);
            var gauge = root.gameObject.AddComponent<ProbabilityGauge>();

            UiBuilder.Text("Overline", root, "Win probability", UiTextRole.Overline, UiPalette.ScannerCyan.WithAlpha(0.75f));

            // --- number row: giant percentage, then the delta stack beside it.
            var numberRow = UiBuilder.Rect("NumberRow", root);
            UiBuilder.Horizontal(numberRow, 14f, null, TextAnchor.LowerLeft);
            UiBuilder.Size(numberRow, preferredHeight: 82f, minHeight: 70f, flexibleWidth: 1f);

            var percentLabel = UiBuilder.Text("Percent", numberRow, "50%", UiTextRole.Metric, UiPalette.ScannerCyan);
            percentLabel.alignment = TextAlignmentOptions.BottomLeft;
            UiBuilder.Size(percentLabel.rectTransform, preferredWidth: 190f, minWidth: 150f, preferredHeight: 82f);
            // Elastic on the headline number: a big swing overshoots and settles, which reads
            // as the device reacting rather than the value being edited.
            gauge._percent = AnimatedNumber.Attach(percentLabel, v => UiType.Percent(v), 0.75f, Ease.OutElastic);

            var deltaGroup = UiBuilder.Rect("Delta", numberRow, false);
            UiBuilder.Horizontal(deltaGroup, 5f, null, TextAnchor.LowerLeft);
            UiBuilder.Size(deltaGroup, preferredWidth: 150f, preferredHeight: 34f);
            gauge._deltaGroup = deltaGroup;
            gauge._deltaAlpha = UiBuilder.Group(deltaGroup, 0.5f, false, false);

            var arrow = UiBuilder.Image("Arrow", deltaGroup, UiSprites.Chevron(28), UiPalette.Positive, Image.Type.Simple);
            arrow.preserveAspect = true;
            UiBuilder.Size(arrow.rectTransform, preferredWidth: 20f, preferredHeight: 20f, minWidth: 20f, minHeight: 20f);
            gauge._deltaArrow = arrow;

            var deltaLabel = UiBuilder.Text("DeltaLabel", deltaGroup, "±0.0 pts", UiTextRole.Numeric, UiPalette.TextMuted);
            deltaLabel.alignment = TextAlignmentOptions.BottomLeft;
            UiBuilder.Size(deltaLabel.rectTransform, preferredWidth: 116f, preferredHeight: 24f);
            gauge._deltaLabel = deltaLabel;

            UiBuilder.Spacer(numberRow);

            // --- split bar with quartile ticks and the ghost marker.
            // The holder carries a layout group so the bar takes its own 14px preferred
            // height and stays vertically centred, rather than stretching to fill the row.
            var barHolder = UiBuilder.Rect("BarHolder", root);
            UiBuilder.Vertical(barHolder, 0f, null, TextAnchor.MiddleCenter);
            UiBuilder.Size(barHolder, preferredHeight: 18f, minHeight: 18f, flexibleWidth: 1f);

            var bar = AnimatedBar.Build("Bar", barHolder, 14f, UiPalette.ScannerCyan, false,
                UiPalette.Negative.WithAlpha(0.28f), 0.7f);
            gauge._bar = bar;
            gauge._barTrack = bar.GetComponent<RectTransform>();

            // Quartile ticks sit above the fill so they stay visible at any value.
            foreach (var mark in new[] { 0.25f, 0.5f, 0.75f })
            {
                var tick = UiBuilder.Image("Tick", gauge._barTrack, UiSprites.Solid(),
                    Color.black.WithAlpha(mark == 0.5f ? 0.45f : 0.22f), Image.Type.Simple);
                tick.rectTransform.anchorMin = new Vector2(mark, 0f);
                tick.rectTransform.anchorMax = new Vector2(mark, 1f);
                tick.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                tick.rectTransform.anchoredPosition = Vector2.zero;
                tick.rectTransform.sizeDelta = new Vector2(mark == 0.5f ? 2f : 1f, 0f);
            }

            var ghost = UiBuilder.Image("GhostMarker", gauge._barTrack, UiSprites.Solid(), Color.clear, Image.Type.Simple);
            ghost.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            ghost.rectTransform.anchoredPosition = Vector2.zero;
            ghost.rectTransform.sizeDelta = new Vector2(3f, 8f);
            gauge._ghostMarker = ghost.rectTransform;
            gauge._ghostImage = ghost;

            var caption = UiBuilder.Text("Caption", root, string.Empty, UiTextRole.Caption, UiPalette.TextMuted);
            UiBuilder.Size(caption.rectTransform, preferredHeight: 18f, flexibleWidth: 1f);
            gauge._caption = caption;

            return gauge;
        }
    }
}
