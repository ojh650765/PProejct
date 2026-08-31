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
    ///
    /// <b>It is handed a span and narrows itself inside it.</b> See <see cref="Fit"/> — a
    /// fixed width centred on the screen is not safe here, and a capture proved it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleTurnClockView : MonoBehaviour
    {
        /// <summary>Widest the clock is ever drawn. It narrows below this; it never exceeds it.</summary>
        public const float PanelWidth = 420f;
        public const float PanelHeight = 54f;

        /// <summary>Narrower than this and the countdown is not worth drawing at all.</summary>
        private const float MinimumWidth = 180f;

        private const float BarHeight = 10f;

        /// <summary>Below this fraction the bar turns amber; below <see cref="UrgentAt"/>, red.</summary>
        private const float CautionAt = 0.4f;
        private const float UrgentAt = 0.2f;

        private RectTransform _body;
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

            // Everything visible hangs off a centred inner rect rather than off this one,
            // because this one is the SPAN the HUD gives us and that span changes with the
            // window. See Fit.
            _body = UiBuilder.Rect("Body", rect, false);
            UiBuilder.Anchor(_body, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(PanelWidth, 0f));

            // The caption sits above the bar rather than beside it: at 420px a label and a
            // count on the same row leave the bar too short to read as a rate.
            _label = UiBuilder.Text("Label", _body, "", UiTextRole.Overline,
                UiPalette.TextSecondary, TextAlignmentOptions.Left);
            UiBuilder.Anchor(_label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 26f));

            _count = UiBuilder.Text("Count", _body, "", UiTextRole.Numeric,
                UiPalette.TextPrimary, TextAlignmentOptions.Right);
            UiBuilder.Anchor(_count.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 26f));

            _barTrack = UiBuilder.Image("Track", _body, UiSprites.Pill((int)BarHeight),
                UiPalette.SurfaceSunken.WithAlpha(0.85f));
            UiBuilder.Anchor(_barTrack.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, BarHeight));

            // The fill is a FRACTION of the track, expressed as an anchor rather than a width.
            //
            // A width in pixels would have to be recomputed every time the track resized, and
            // the one place that would be forgotten is the resize -- which is exactly the class
            // of bug this whole element was just fixed for. As an anchor it is correct at any
            // track width without anybody having to remember. Resized rather than scaled, so
            // the pill's rounded caps stay round instead of being squashed into ellipses.
            _barFillImage = UiBuilder.Image("Fill", _barTrack.rectTransform,
                UiSprites.Pill((int)BarHeight), UiPalette.Info);
            _barFill = _barFillImage.rectTransform;
            _barFill.anchorMin = Vector2.zero;
            _barFill.anchorMax = Vector2.one;
            _barFill.pivot = new Vector2(0f, 0.5f);
            _barFill.offsetMin = Vector2.zero;
            _barFill.offsetMax = Vector2.zero;

            Fit();
            SetVisible(false);
        }

        /// <summary>
        /// Narrows the clock to whatever room it has been given.
        ///
        /// <b>This is a bug fix with a picture behind it.</b> The clock was a fixed 420 units
        /// centred on the screen, and the canvas scales by HEIGHT — so a 4:3 window has 1440
        /// units of width where the layout was authored for 1920, while the two status plates
        /// keep their 496 each. That leaves 360 units between them, and a 420-unit clock
        /// centred in a 360-unit gap runs 30 units under the opponent's plate at each end.
        /// It was invisible in every 16:9 screenshot and obvious the moment one was taken at
        /// 4:3.
        ///
        /// So the HUD hands this the span between the plates and the clock fits inside it,
        /// capped at <see cref="PanelWidth"/> so an ultrawide does not stretch a countdown
        /// into a fourteen-hundred-unit ribbon. Below <see cref="MinimumWidth"/> there is no
        /// honest way to draw a rate, so it stops drawing rather than shrinking into a smear.
        /// </summary>
        private void Fit()
        {
            if (_body == null) return;

            var available = ((RectTransform)transform).rect.width;
            // Zero before the first layout pass. Leave the authored width alone and wait for
            // the callback rather than collapsing to nothing on frame one.
            if (available <= 1f) return;

            _body.sizeDelta = new Vector2(Mathf.Min(PanelWidth, available), 0f);
            _body.gameObject.SetActive(available >= MinimumWidth);
        }

        /// <summary>Unity's own resize notification — a window drag, a rotation, a fullscreen toggle.</summary>
        private void OnRectTransformDimensionsChange() => Fit();

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
            Fit();
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
            Fit();
            _barFill.anchorMax = Vector2.one;

            // Repainted here rather than left to the first Update. Committing on the last
            // second of the clock leaves the bar red, and one frame of a full red bar under
            // "waiting for the opponent" reads as an alarm about the wrong thing.
            _barFillImage.color = UiPalette.Info.WithAlpha(0.35f);
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
            _barFill.anchorMax = new Vector2(remaining, 1f);

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
