using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// One feature's contribution, drawn as a diverging bar around a centre axis.
    ///
    /// A diverging bar is the right form here because <see cref="PredictionEvidence.ProbabilityPoints"/>
    /// is signed and centred on zero: length encodes magnitude, side encodes direction, and
    /// the eye ranks the whole list in one pass without reading a single number. Bars are
    /// scaled against the largest contribution in the current readout, not against a fixed
    /// maximum, so the shape of the reasoning stays legible whether the top factor is worth
    /// 2 points or 30.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EvidenceRow : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI _label;
        [SerializeField] private TextMeshProUGUI _value;
        [SerializeField] private TextMeshProUGUI _points;
        [SerializeField] private RectTransform _barTrack;
        [SerializeField] private RectTransform _barFill;
        [SerializeField] private Image _barImage;
        [SerializeField] private CanvasGroup _group;

        private TweenHandle _barTween;
        private float _fraction;
        private bool _positive = true;

        /// <summary>
        /// Binds one evidence item. <paramref name="scale"/> is the largest absolute
        /// contribution in the readout and normalises every bar in the list.
        /// </summary>
        public void Bind(PredictionEvidence evidence, float scale, int index, bool animate)
        {
            var points = evidence.ProbabilityPoints;
            _positive = points >= 0f;
            var color = _positive ? UiPalette.ScannerCyan : UiPalette.Negative;

            if (_label != null)
            {
                // Prefer the authored player-facing label; fall back to a humanised feature
                // name so a newly added model feature never renders as an empty row.
                _label.SetText(string.IsNullOrWhiteSpace(evidence.Label)
                    ? Humanise(evidence.Feature)
                    : evidence.Label);
            }

            if (_value != null)
            {
                _value.SetText(FormatValue(evidence.Value));
            }

            if (_points != null)
            {
                _points.SetText(UiType.SignedPoints(points, false));
                _points.color = color;
            }

            if (_barImage != null) _barImage.color = color;

            var target = scale > 0.001f ? Mathf.Clamp01(Mathf.Abs(points) / scale) : 0f;

            UiTween.Kill(ref _barTween);
            if (!animate)
            {
                _fraction = target;
                ApplyBar();
            }
            else
            {
                var from = _fraction;
                // Stagger down the list: the ranked order becomes visible as it draws.
                _barTween = UiTween.Run(0.45f, t =>
                {
                    _fraction = Mathf.LerpUnclamped(from, target, t);
                    ApplyBar();
                }, Ease.OutCubic, index * 0.045f);
            }

            if (animate && _group != null)
            {
                _group.alpha = 0f;
                UiTween.Fade(_group, 1f, 0.28f, Ease.OutCubic, index * 0.045f);
            }
            else if (_group != null)
            {
                _group.alpha = 1f;
            }
        }

        private void ApplyBar()
        {
            if (_barFill == null) return;
            var half = Mathf.Clamp01(_fraction) * 0.5f;
            if (_positive)
            {
                _barFill.anchorMin = new Vector2(0.5f, 0f);
                _barFill.anchorMax = new Vector2(0.5f + half, 1f);
            }
            else
            {
                _barFill.anchorMin = new Vector2(0.5f - half, 0f);
                _barFill.anchorMax = new Vector2(0.5f, 1f);
            }
            _barFill.offsetMin = Vector2.zero;
            _barFill.offsetMax = Vector2.zero;
        }

        /// <summary>Turns "Speed_diff" into "Speed diff" for the fallback label path.</summary>
        private static string Humanise(string feature)
        {
            if (string.IsNullOrEmpty(feature)) return "Unnamed factor";
            return feature.Replace('_', ' ');
        }

        /// <summary>
        /// Feature values arrive as raw model inputs — stat differences, ratios, multipliers.
        /// Whole numbers print whole; everything else gets two significant decimals.
        /// </summary>
        private static string FormatValue(float value)
        {
            if (Mathf.Approximately(value, Mathf.Round(value))) return Mathf.RoundToInt(value).ToString();
            return value.ToString("0.##");
        }

        /// <summary>Builds a row sized for the scanner's evidence list.</summary>
        public static EvidenceRow Build(Transform parent)
        {
            var root = UiBuilder.Rect("EvidenceRow", parent);
            UiBuilder.Size(root, preferredHeight: 30f, minHeight: 30f, flexibleWidth: 1f);
            UiBuilder.Horizontal(root, 10f, new RectOffset(0, 0, 0, 0), TextAnchor.MiddleLeft);

            var row = root.gameObject.AddComponent<EvidenceRow>();
            row._group = UiBuilder.Group(root);

            var labelStack = UiBuilder.Rect("LabelStack", root);
            UiBuilder.Vertical(labelStack, 0f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(labelStack, preferredWidth: 168f, minWidth: 120f, flexibleWidth: 0.9f);

            row._label = UiBuilder.Text("Label", labelStack, string.Empty, UiTextRole.Secondary, UiPalette.TextPrimary);
            row._label.overflowMode = TextOverflowModes.Ellipsis;
            row._label.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(row._label.rectTransform, preferredHeight: 17f);

            row._value = UiBuilder.Text("Value", labelStack, string.Empty, UiTextRole.Caption, UiPalette.TextMuted);
            UiBuilder.Size(row._value.rectTransform, preferredHeight: 12f);

            // --- diverging bar
            var track = UiBuilder.Rect("BarTrack", root);
            UiBuilder.Size(track, preferredWidth: 150f, minWidth: 90f, preferredHeight: 12f, flexibleWidth: 1.6f);
            row._barTrack = track;

            var trackFill = UiBuilder.Image("TrackFill", track, UiSprites.Pill(12), UiPalette.SurfaceSunken.WithAlpha(0.7f));
            UiBuilder.Stretch(trackFill.rectTransform);

            var fill = UiBuilder.Rect("Fill", track, false);
            fill.pivot = new Vector2(0.5f, 0.5f);
            row._barFill = fill;
            row._barImage = UiBuilder.Image("FillImage", fill, UiSprites.Pill(12), UiPalette.ScannerCyan);
            UiBuilder.Stretch(row._barImage.rectTransform);

            var axis = UiBuilder.Image("Axis", track, UiSprites.Solid(), Color.white.WithAlpha(0.22f), Image.Type.Simple);
            axis.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            axis.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            axis.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            axis.rectTransform.anchoredPosition = Vector2.zero;
            axis.rectTransform.sizeDelta = new Vector2(1f, 6f);

            row._points = UiBuilder.Text("Points", root, string.Empty, UiTextRole.Numeric, UiPalette.ScannerCyan);
            row._points.alignment = TextAlignmentOptions.Right;
            row._points.fontSize = 15f;
            UiBuilder.Size(row._points.rectTransform, preferredWidth: 52f, minWidth: 48f);

            return row;
        }
    }
}
