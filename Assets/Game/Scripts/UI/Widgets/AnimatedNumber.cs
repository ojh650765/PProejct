using System;
using UnityEngine;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// A TMP label whose numeric content counts to its new value instead of snapping.
    ///
    /// The brief's rule is absolute: never snap a value. Rather than every view owning its
    /// own tween-and-format code, a label that needs to show a number gets one of these and
    /// a formatter delegate.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AnimatedNumber : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI _label;
        [SerializeField] private float _duration = 0.55f;
        [SerializeField] private Ease _ease = Ease.OutCubic;

        private float _value;
        private TweenHandle _tween;
        private Func<float, string> _format = v => Mathf.RoundToInt(v).ToString();

        /// <summary>Currently displayed value, mid-tween.</summary>
        public float Value => _value;

        public TextMeshProUGUI Label => _label;

        /// <summary>Sets the formatter, e.g. <c>v =&gt; UiType.Percent(v)</c>.</summary>
        public AnimatedNumber WithFormat(Func<float, string> format)
        {
            if (format != null) _format = format;
            return this;
        }

        /// <summary>Sets duration and easing. Elastic suits the scanner gauge, cubic suits everything else.</summary>
        public AnimatedNumber WithMotion(float duration, Ease ease)
        {
            _duration = duration;
            _ease = ease;
            return this;
        }

        /// <summary>Writes a value with no motion. First bind only.</summary>
        public void SetImmediate(float value)
        {
            UiTween.Kill(ref _tween);
            _value = value;
            Write();
        }

        /// <summary>Counts to a new value.</summary>
        public void SetValue(float value, float? duration = null, Action onComplete = null)
        {
            if (Mathf.Approximately(value, _value))
            {
                onComplete?.Invoke();
                return;
            }

            var from = _value;
            UiTween.Kill(ref _tween);
            _tween = UiTween.Run(duration ?? _duration, t =>
            {
                _value = Mathf.LerpUnclamped(from, value, t);
                Write();
            }, _ease, 0f, true, onComplete);
        }

        /// <summary>Tweens the label colour alongside a value change.</summary>
        public void SetColor(Color color, float duration = 0.3f)
        {
            if (_label == null) return;
            var from = _label.color;
            UiTween.Color(from, color, duration, c => { if (_label != null) _label.color = c; });
        }

        private void Write()
        {
            if (_label == null) return;
            _label.SetText(_format(_value));
        }

        /// <summary>Attaches to an existing label.</summary>
        public static AnimatedNumber Attach(TextMeshProUGUI label, Func<float, string> format = null,
            float duration = 0.55f, Ease ease = Ease.OutCubic)
        {
            if (label == null) return null;
            var number = label.gameObject.GetComponent<AnimatedNumber>();
            if (number == null) number = label.gameObject.AddComponent<AnimatedNumber>();
            number._label = label;
            number._duration = duration;
            number._ease = ease;
            if (format != null) number._format = format;
            return number;
        }
    }
}
