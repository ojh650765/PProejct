using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// A rounded progress bar whose value always tweens, with an optional trailing "chip"
    /// fill that lags behind on decrease so a big hit reads as a hit.
    ///
    /// Implementation note: the fill graphic always spans the full track and is revealed by
    /// a <see cref="RectMask2D"/> whose right padding is driven by the fraction. Anchoring
    /// the fill's width directly would squash the nine-slice caps to mush at low values, and
    /// a stencil <see cref="Mask"/> would cost an extra draw call on every bar in the party
    /// screen. Padding-based clipping has neither problem and keeps the left cap perfectly
    /// round at every value.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AnimatedBar : MonoBehaviour
    {
        [Header("Parts")]
        [SerializeField] private RectTransform _track;
        [SerializeField] private RectMask2D _clip;
        [SerializeField] private Image _fill;
        [SerializeField] private RectMask2D _chipClip;
        [SerializeField] private Image _chipFill;

        [Header("Behaviour")]
        [Tooltip("Seconds for the main fill to reach a new value.")]
        [SerializeField] private float _duration = 0.55f;
        [Tooltip("Extra seconds the trailing chip takes to catch up after a decrease.")]
        [SerializeField] private float _chipLag = 0.45f;
        [SerializeField] private Ease _ease = Ease.OutCubic;

        private float _fraction;
        private float _chipFraction;
        private TweenHandle _fillTween;
        private TweenHandle _chipTween;

        /// <summary>Current displayed fraction, 0-1. Reflects the tween, not the target.</summary>
        public float Fraction => _fraction;

        /// <summary>Colour of the main fill. Set through <see cref="SetColor"/> to tween it.</summary>
        public Color FillColor => _fill != null ? _fill.color : Color.white;

        private void OnRectTransformDimensionsChange() => ApplyPadding();

        /// <summary>Jumps to a value with no motion. Only for first bind and screen open.</summary>
        public void SetImmediate(float fraction)
        {
            UiTween.Kill(ref _fillTween);
            UiTween.Kill(ref _chipTween);
            _fraction = Mathf.Clamp01(fraction);
            _chipFraction = _fraction;
            ApplyPadding();
        }

        /// <summary>
        /// Animates to a new value. Decreases drag the chip fill behind them; increases pull
        /// it along immediately so a heal does not leave a stale ghost.
        /// </summary>
        public void SetValue(float fraction, float? duration = null)
        {
            fraction = Mathf.Clamp01(fraction);
            var decreasing = fraction < _fraction;
            var from = _fraction;
            var seconds = duration ?? _duration;

            UiTween.Kill(ref _fillTween);
            _fillTween = UiTween.Run(seconds, t =>
            {
                _fraction = Mathf.LerpUnclamped(from, fraction, t);
                ApplyPadding();
            }, _ease);

            if (_chipFill == null) return;

            var chipFrom = _chipFraction;
            UiTween.Kill(ref _chipTween);
            if (decreasing)
            {
                // Hold, then drain — the pause is what makes the lost chunk legible.
                _chipTween = UiTween.Run(seconds, t =>
                {
                    _chipFraction = Mathf.LerpUnclamped(chipFrom, fraction, t);
                    ApplyPadding();
                }, Ease.InOutQuad, seconds * 0.5f + _chipLag);
            }
            else
            {
                _chipTween = UiTween.Run(seconds, t =>
                {
                    _chipFraction = Mathf.LerpUnclamped(chipFrom, fraction, t);
                    ApplyPadding();
                }, _ease);
            }
        }

        /// <summary>Tweens the fill colour. Used by the health bar's green→amber→red ramp.</summary>
        public void SetColor(Color color, float duration = 0.35f)
        {
            if (_fill == null) return;
            var from = _fill.color;
            UiTween.Color(from, color, duration, c => { if (_fill != null) _fill.color = c; });
        }

        /// <summary>Sets the fill colour with no motion.</summary>
        public void SetColorImmediate(Color color)
        {
            if (_fill != null) _fill.color = color;
            if (_chipFill != null) _chipFill.color = Color.Lerp(color, Color.white, 0.55f).WithAlpha(0.8f);
        }

        private void ApplyPadding()
        {
            if (_track == null) return;
            var width = _track.rect.width;
            if (width <= 0f) return;

            if (_clip != null)
            {
                _clip.padding = new Vector4(0f, 0f, width * (1f - _fraction), 0f);
            }
            if (_chipClip != null)
            {
                _chipClip.padding = new Vector4(0f, 0f, width * (1f - _chipFraction), 0f);
            }
        }

        /// <summary>
        /// Builds a complete bar under <paramref name="parent"/>. The chip layer is optional
        /// because only health bars want it — an experience bar that ghosts backwards is
        /// just confusing.
        /// </summary>
        public static AnimatedBar Build(string name, Transform parent, float height, Color fillColor,
            bool withChip = false, Color? trackColor = null, float duration = 0.55f)
        {
            var root = UiBuilder.Rect(name, parent);
            UiBuilder.Size(root, preferredHeight: height, minHeight: height, flexibleWidth: 1f);

            var radius = Mathf.Max(2, Mathf.RoundToInt(height * 0.5f));
            var track = UiBuilder.Image("Track", root, UiSprites.Pill(radius * 2), trackColor ?? UiPalette.SurfaceSunken);

            var bar = root.gameObject.AddComponent<AnimatedBar>();
            bar._track = track.rectTransform;
            bar._duration = duration;

            if (withChip)
            {
                var chipHolder = UiBuilder.Rect("ChipClip", track.rectTransform);
                bar._chipClip = chipHolder.gameObject.AddComponent<RectMask2D>();
                var chip = UiBuilder.Image("ChipFill", chipHolder, UiSprites.Pill(radius * 2),
                    Color.Lerp(fillColor, Color.white, 0.55f).WithAlpha(0.8f));
                bar._chipFill = chip;
            }

            var clipHolder = UiBuilder.Rect("Clip", track.rectTransform);
            bar._clip = clipHolder.gameObject.AddComponent<RectMask2D>();
            var fill = UiBuilder.Image("Fill", clipHolder, UiSprites.Pill(radius * 2), fillColor);
            bar._fill = fill;

            bar.SetImmediate(1f);
            return bar;
        }
    }
}
