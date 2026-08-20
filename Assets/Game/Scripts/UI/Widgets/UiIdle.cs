using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>What an idling element does with its time.</summary>
    public enum UiIdleMode
    {
        /// <summary>Rides up and down. The cursor, and anything that should look eager.</summary>
        Bob = 0,
        /// <summary>Breathes in and out of scale. For the thing the player is meant to press.</summary>
        Pulse = 1,
        /// <summary>Turns continuously. Background watermarks and burst art.</summary>
        Spin = 2,
        /// <summary>Rocks about its centre. A card that should look hand-placed rather than aligned.</summary>
        Sway = 3,
        /// <summary>Travels sideways and wraps. Clouds.</summary>
        Drift = 4,
    }

    /// <summary>
    /// Endless idle motion for one rect.
    ///
    /// <b>Why not a tween.</b> <see cref="UiTween"/> is a one-shot engine — every handle has a
    /// duration and dies at the end of it — and rebuilding a tween from its own completion
    /// callback forever is a leak waiting to be found on the screen nobody closes. A screen
    /// like the title, which sits open for minutes with clouds crossing it and a ball bobbing
    /// beside the cursor, wants a component that simply evaluates a function of time.
    ///
    /// <b>It honours reduced motion.</b> With <see cref="UiTween.MotionEnabled"/> off the
    /// element settles on its resting value and the component switches itself off, so the
    /// accessibility toggle turns off the loudest thing on the screen rather than being the
    /// one setting the decorative layer ignores.
    ///
    /// Time is unscaled throughout: these screens can be open while the game behind them is
    /// paused, and a cloud that stops when <c>Time.timeScale</c> does looks broken.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UiIdle : MonoBehaviour
    {
        private RectTransform _rect;
        private UiIdleMode _mode;
        private float _amount;
        private float _period;
        private float _phase;
        private float _wrap;

        private Vector2 _basePosition;
        private Vector3 _baseScale;
        private float _elapsed;
        private bool _settled;

        /// <summary>
        /// Attaches (or reconfigures) idle motion.
        /// </summary>
        /// <param name="target">The rect that moves.</param>
        /// <param name="mode">What kind of motion.</param>
        /// <param name="amount">
        /// Pixels for <see cref="UiIdleMode.Bob"/>, a 0-1 scale fraction for
        /// <see cref="UiIdleMode.Pulse"/>, degrees for <see cref="UiIdleMode.Spin"/> and
        /// <see cref="UiIdleMode.Sway"/>, pixels per second for <see cref="UiIdleMode.Drift"/>.
        /// </param>
        /// <param name="period">Seconds for one full cycle. For Spin, seconds per <paramref name="amount"/> degrees.</param>
        /// <param name="phase">0-1 offset into the cycle, so a row of bobbing things is not one bobbing thing.</param>
        /// <param name="wrap">Drift only: the width to wrap across.</param>
        public static UiIdle Attach(RectTransform target, UiIdleMode mode, float amount, float period,
                                    float phase = 0f, float wrap = 0f)
        {
            if (target == null) return null;

            var idle = target.GetComponent<UiIdle>();
            if (idle == null) idle = target.gameObject.AddComponent<UiIdle>();

            idle._rect = target;
            idle._mode = mode;
            idle._amount = amount;
            idle._period = Mathf.Max(0.05f, period);
            idle._phase = phase;
            idle._wrap = wrap;
            idle._basePosition = target.anchoredPosition;
            idle._baseScale = target.localScale;
            idle._elapsed = phase * idle._period;
            idle._settled = false;
            idle.enabled = true;
            return idle;
        }

        /// <summary>Re-reads the resting position, for a caller that moved the rect after attaching.</summary>
        public void Rebase()
        {
            if (_rect == null) return;
            _basePosition = _rect.anchoredPosition;
            _baseScale = _rect.localScale;
        }

        private void Update()
        {
            if (_rect == null) { enabled = false; return; }

            if (!UiTween.MotionEnabled)
            {
                if (_settled) return;
                Settle();
                return;
            }

            _settled = false;
            _elapsed += Time.unscaledDeltaTime;

            switch (_mode)
            {
                case UiIdleMode.Bob:
                    _rect.anchoredPosition = _basePosition
                        + new Vector2(0f, Mathf.Sin(_elapsed / _period * Mathf.PI * 2f) * _amount);
                    break;

                case UiIdleMode.Pulse:
                    _rect.localScale = _baseScale
                        * (1f + Mathf.Sin(_elapsed / _period * Mathf.PI * 2f) * _amount);
                    break;

                case UiIdleMode.Spin:
                    _rect.localRotation = Quaternion.Euler(0f, 0f, _elapsed / _period * _amount);
                    break;

                case UiIdleMode.Sway:
                    _rect.localRotation = Quaternion.Euler(0f, 0f,
                        Mathf.Sin(_elapsed / _period * Mathf.PI * 2f) * _amount);
                    break;

                case UiIdleMode.Drift:
                    if (_wrap <= 1f) { enabled = false; break; }
                    // Kept as an absolute function of the base so a long-lived screen cannot
                    // accumulate float error into a cloud that has drifted off the top.
                    var travelled = _basePosition.x + 400f + _elapsed * _amount;
                    _rect.anchoredPosition = new Vector2(Mathf.Repeat(travelled, _wrap) - 400f, _basePosition.y);
                    break;
            }
        }

        private void Settle()
        {
            _settled = true;
            switch (_mode)
            {
                case UiIdleMode.Bob:
                case UiIdleMode.Drift:
                    _rect.anchoredPosition = _basePosition;
                    break;
                case UiIdleMode.Pulse:
                    _rect.localScale = _baseScale;
                    break;
                case UiIdleMode.Spin:
                case UiIdleMode.Sway:
                    _rect.localRotation = Quaternion.identity;
                    break;
            }
        }
    }
}
