using PokeLab.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// The shot clock, and the only place a PvP match tells the player where the turn is.
    ///
    /// <b>Why a battle needs one at all.</b> A single-player turn waits forever, because the
    /// only person it inconveniences is the one not answering. A match between two people is
    /// the opposite: every second one player spends deciding is a second the other spends
    /// looking at a still screen with no way to tell a hard decision from a closed laptop.
    /// The clock is what makes the exchange legible from both ends — mine is running, then
    /// theirs is, and neither can last forever.
    ///
    /// <b>Three states, and the third is the one that was missing.</b>
    /// <list type="bullet">
    /// <item><description><b>Yours</b> — a draining bar and a count, in seconds.</description></item>
    /// <item><description><b>Theirs</b> — the choice is sent and this is waiting for the
    /// answer. Indeterminate rather than counting: the number that matters is on the other
    /// player's screen, and showing our own patience window as a countdown would promise a
    /// deadline this side does not own.</description></item>
    /// <item><description><b>Off</b> — every battle that is not PvP. The clock is hidden
    /// rather than paused, because a story battle has no shot clock and a dimmed one sitting
    /// there would read as broken.</description></item>
    /// </list>
    ///
    /// <b>It runs on unscaled time.</b> A battle performance slows the game clock for a hit
    /// and stops it outright for a hit-stop; a shot clock that did the same would give the
    /// player a different amount of real thinking time depending on how hard they were hit.
    /// The presenter's own deadline is unscaled for the same reason, and the two have to
    /// agree or the bar would empty at a moment the turn did not actually end.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleTurnClockView : MonoBehaviour
    {
        /// <summary>Bar width. Wide enough to read a drain rate, narrow enough to sit above the field.</summary>
        public const float PanelWidth = 420f;
        public const float PanelHeight = 54f;

        private const float BarHeight = 10f;

        /// <summary>Below this fraction the bar turns amber; below <see cref="UrgentAt"/>, red.</summary>
        private const float CautionAt = 0.4f;
        private const float UrgentAt = 0.2f;

        private RectTransform _barFill;
        private Image _barFillImage;
        private Image _barTrack;
        private TextMeshProUGUI _label;
        private TextMeshProUGUI _count;
        private CanvasGroup _group;

        private enum Mode { Off, Mine, Theirs }

        private Mode _mode = Mode.Off;
        private float _limit;
        private float _endsAt;

        /// <summary>The last whole second painted, so the count is not re-laid-out every frame.</summary>
        private int _shown = -1;

        public static BattleTurnClockView Build(Transform parent)
        {
            var rect = UiBuilder.Rect("TurnClock", parent, false);
            var view = rect.gameObject.AddComponent<BattleTurnClockView>();
            view.BuildRuntime();
            return view;
        }

        private void BuildRuntime()
        {
            var rect = (RectTransform)transform;
            _group = UiBuilder.Group(this, 0f, false, false);

            // The caption sits above the bar rather than beside it: at 420px a label and a
            // count on the same row leave the bar too short to read as a rate.
            _label = UiBuilder.Text("Label", rect, "", UiTextRole.Overline,
                UiPalette.TextSecondary, TextAlignmentOptions.Left);
            UiBuilder.Anchor(_label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 26f));

            _count = UiBuilder.Text("Count", rect, "", UiTextRole.Numeric,
                UiPalette.TextPrimary, TextAlignmentOptions.Right);
            UiBuilder.Anchor(_count.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 26f));

            _barTrack = UiBuilder.Image("Track", rect, UiSprites.Pill((int)BarHeight),
                UiPalette.SurfaceSunken.WithAlpha(0.85f));
            UiBuilder.Anchor(_barTrack.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, BarHeight));

            // Anchored to the track's left edge and resized rather than scaled: a scaled fill
            // squashes the pill's rounded caps into ellipses as it empties.
            _barFillImage = UiBuilder.Image("Fill", _barTrack.rectTransform,
                UiSprites.Pill((int)BarHeight), UiPalette.Info);
            _barFill = _barFillImage.rectTransform;
            UiBuilder.Anchor(_barFill, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), Vector2.zero, new Vector2(PanelWidth, 0f));

            SetVisible(false);
        }

        /// <summary>Starts this side's countdown. <paramref name="seconds"/> is the whole budget.</summary>
        public void BeginMyTurn(float seconds)
        {
            _mode = Mode.Mine;
            _limit = Mathf.Max(1f, seconds);
            _endsAt = Time.unscaledTime + _limit;
            _shown = -1;

            _label.text = Loc.Pick("YOUR TURN", "내 차례");
            _label.color = UiPalette.TextSecondary;
            SetVisible(true);
            Paint(1f);
        }

        /// <summary>
        /// The choice is away and this is waiting for theirs.
        ///
        /// The bar is left full and dimmed rather than drained: it is not measuring anything
        /// this side controls, and an emptying bar here would be read as "you are running out
        /// of time", which is exactly backwards.
        /// </summary>
        public void BeginTheirTurn()
        {
            _mode = Mode.Theirs;
            _shown = -1;

            _label.text = Loc.Pick("WAITING FOR THE OPPONENT", "상대를 기다리는 중");
            _label.color = UiPalette.TextMuted;
            _count.text = "";
            SetVisible(true);
            _barFill.sizeDelta = new Vector2(PanelWidth, 0f);
        }

        /// <summary>Hides the clock. Called when the turn resolves and when the battle ends.</summary>
        public void Stop()
        {
            _mode = Mode.Off;
            SetVisible(false);
        }

        private void Update()
        {
            switch (_mode)
            {
                case Mode.Mine:
                    Paint(Mathf.Clamp01((_endsAt - Time.unscaledTime) / _limit));
                    break;

                case Mode.Theirs:
                    // A slow breath, so a wait that goes on is visibly still alive. Nothing
                    // about it is a measurement; it is the difference between "waiting" and
                    // "frozen", which is the whole complaint a silent exchange produces.
                    var pulse = 0.35f + 0.25f * Mathf.Sin(Time.unscaledTime * 2.2f);
                    _barFillImage.color = UiPalette.Info.WithAlpha(pulse);
                    break;
            }
        }

        private void Paint(float remaining)
        {
            _barFill.sizeDelta = new Vector2(PanelWidth * remaining, 0f);

            var colour = remaining <= UrgentAt ? UiPalette.Critical
                       : remaining <= CautionAt ? UiPalette.Caution
                       : UiPalette.Info;

            // The last fifth flashes. Deliberately only there: a bar that pulsed the whole way
            // down would be noise for the four fifths of the turn where there is no hurry.
            if (remaining <= UrgentAt)
            {
                var flash = 0.65f + 0.35f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 6f));
                colour = colour.WithAlpha(flash);
            }

            _barFillImage.color = colour;

            // Ceil, so the count reaches zero exactly when the turn does rather than a second
            // early — a clock that reads 0 while the menu is still live is a clock nobody
            // trusts the second time.
            var whole = Mathf.Max(0, Mathf.CeilToInt(remaining * _limit));
            if (whole == _shown) return;
            _shown = whole;
            _count.text = whole.ToString();
            _count.color = remaining <= UrgentAt ? UiPalette.Critical
                         : remaining <= CautionAt ? UiPalette.Caution
                         : UiPalette.TextPrimary;
        }

        private void SetVisible(bool visible)
        {
            if (_group == null) return;
            _group.alpha = visible ? 1f : 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }
    }
}
