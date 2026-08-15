using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// Full-screen moments: encounter flash, battle intro banner, capture sequence,
    /// victory and defeat, and the level-up card.
    ///
    /// The ownership boundary here matters. The cinematics worker owns every camera blend
    /// and every mode transition in this project, so this class deliberately runs no
    /// full-screen wipe of its own — it exposes <see cref="SetVeil"/> and
    /// <see cref="FadeVeil"/> for them to drive, and its own overlays are content that sits
    /// on top of a transition rather than a transition itself. Two systems each running a
    /// screen fade is the classic way a handover ends up double-darkened.
    ///
    /// Every method is safe to call at any time and returns immediately; completion is
    /// reported through the optional callback.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverlayDirector : MonoBehaviour
    {
        [Header("Layers")]
        [SerializeField] private Image _veil;
        [SerializeField] private Image _flash;
        [SerializeField] private CanvasGroup _bannerGroup;
        [SerializeField] private RectTransform _bannerRect;
        [SerializeField] private TextMeshProUGUI _bannerTitle;
        [SerializeField] private TextMeshProUGUI _bannerSubtitle;
        [SerializeField] private CanvasGroup _cardGroup;
        [SerializeField] private RectTransform _cardRect;
        [SerializeField] private TextMeshProUGUI _cardTitle;
        [SerializeField] private TextMeshProUGUI _cardBody;
        [SerializeField] private Image _cardAccent;
        [SerializeField] private CanvasGroup _captureGroup;
        [SerializeField] private RectTransform _captureBall;
        [SerializeField] private TextMeshProUGUI _captureLabel;

        private TweenHandle _veilTween;
        private TweenHandle _flashTween;
        private TweenHandle _bannerTween;
        private TweenHandle _cardTween;
        private TweenHandle _captureTween;

        // ------------------------------------------------------------------- veil

        /// <summary>
        /// Sets the full-screen veil alpha directly. This is the seam for the cinematics
        /// worker: drive it from a Timeline or a custom blend and the UI will not fight you.
        /// </summary>
        public void SetVeil(float alpha, Color? color = null)
        {
            if (_veil == null) return;
            UiTween.Kill(ref _veilTween);
            var c = color ?? _veil.color;
            c.a = Mathf.Clamp01(alpha);
            _veil.color = c;
            _veil.enabled = c.a > 0.001f;
        }

        /// <summary>Tweens the veil. Provided for callers that only need a plain fade.</summary>
        public void FadeVeil(float alpha, float duration, Color? color = null, Action onComplete = null)
        {
            if (_veil == null) { onComplete?.Invoke(); return; }
            UiTween.Kill(ref _veilTween);
            _veil.enabled = true;
            var from = _veil.color;
            var to = color ?? from;
            to.a = Mathf.Clamp01(alpha);
            _veilTween = UiTween.Color(from, to, duration, c =>
            {
                if (_veil == null) return;
                _veil.color = c;
            }, Ease.InOutQuad, 0f, true, () =>
            {
                if (_veil != null) _veil.enabled = _veil.color.a > 0.001f;
                onComplete?.Invoke();
            });
        }

        // ------------------------------------------------------------------ flash

        /// <summary>
        /// The encounter flash: a hard white bloom that decays fast. Called the instant an
        /// encounter fires, before the cinematics blend takes over — it covers the frame in
        /// which the world swaps and makes the cut feel authored rather than abrupt.
        /// </summary>
        public void PlayEncounterFlash(Color? color = null, float duration = 0.45f, Action onComplete = null)
        {
            if (_flash == null) { onComplete?.Invoke(); return; }

            var tint = color ?? Color.white;
            _flash.enabled = true;
            UiTween.Kill(ref _flashTween);
            _flashTween = UiTween.Run(duration, t =>
            {
                if (_flash == null) return;
                // Near-instant attack, exponential decay. A symmetric fade reads as a
                // cross-dissolve; this reads as a flash.
                var alpha = t < 0.08f ? t / 0.08f : Mathf.Pow(1f - (t - 0.08f) / 0.92f, 2.2f);
                _flash.color = tint.WithAlpha(alpha);
            }, Ease.Linear, 0f, true, () =>
            {
                if (_flash != null) { _flash.color = tint.WithAlpha(0f); _flash.enabled = false; }
                onComplete?.Invoke();
            });
        }

        // ----------------------------------------------------------------- banner

        /// <summary>The battle intro banner: a title bar that sweeps in, holds, and sweeps out.</summary>
        public void PlayBattleIntro(BattleKind kind, string trainerId, Action onComplete = null)
        {
            var title = kind == BattleKind.Trainer ? "TRAINER BATTLE" : "WILD ENCOUNTER";
            var subtitle = kind == BattleKind.Trainer && !string.IsNullOrEmpty(trainerId)
                ? UiServices.Titleise(trainerId.Replace('_', ' ')) + " wants to battle"
                : "A creature blocks the way";
            ShowBanner(title, subtitle, kind == BattleKind.Trainer ? UiPalette.ScannerAmber : UiPalette.ScannerCyan,
                2.0f, onComplete);
        }

        /// <summary>Generic banner, for anything else that wants the same treatment.</summary>
        public void ShowBanner(string title, string subtitle, Color accent, float hold = 1.8f, Action onComplete = null)
        {
            if (_bannerGroup == null || _bannerRect == null) { onComplete?.Invoke(); return; }

            if (_bannerTitle != null) { _bannerTitle.SetText(title ?? string.Empty); _bannerTitle.color = accent; }
            if (_bannerSubtitle != null) _bannerSubtitle.SetText(subtitle ?? string.Empty);

            _bannerGroup.gameObject.SetActive(true);
            UiTween.Kill(ref _bannerTween);

            var centre = Vector2.zero;
            _bannerRect.anchoredPosition = centre + new Vector2(-160f, 0f);
            _bannerGroup.alpha = 0f;

            UiTween.AnchoredMove(_bannerRect, centre, 0.5f, Ease.OutExpo);
            UiTween.Fade(_bannerGroup, 1f, 0.28f);

            _bannerTween = UiTween.Delay(hold, () =>
            {
                if (_bannerRect != null) UiTween.AnchoredMove(_bannerRect, centre + new Vector2(160f, 0f), 0.4f, Ease.InCubic);
                UiTween.Fade(_bannerGroup, 0f, 0.3f, Ease.InCubic, 0.05f, () =>
                {
                    if (_bannerGroup != null) _bannerGroup.gameObject.SetActive(false);
                    onComplete?.Invoke();
                });
            });
        }

        // ---------------------------------------------------------------- capture

        /// <summary>
        /// The capture sequence. Each shake is a distinct beat with a pause between, because
        /// the pause is where the tension lives — a smooth wobble has none.
        /// </summary>
        public void PlayCapture(int shakes, bool succeeded, float catchProbability, Action onComplete = null)
        {
            if (_captureGroup == null || _captureBall == null) { onComplete?.Invoke(); return; }

            _captureGroup.gameObject.SetActive(true);
            _captureGroup.alpha = 1f;
            UiTween.Kill(ref _captureTween);

            if (_captureLabel != null)
            {
                _captureLabel.SetText($"Catch chance {UiType.Percent(catchProbability)}");
                _captureLabel.color = UiPalette.TextSecondary;
            }

            _captureBall.localEulerAngles = Vector3.zero;
            _captureBall.localScale = Vector3.one;

            var count = Mathf.Max(1, shakes);
            var perShake = 0.62f;

            _captureTween = UiTween.Run(perShake * count, t =>
            {
                if (_captureBall == null) return;
                var phase = Mathf.Repeat(t * count, 1f);
                // Wobble only in the first 45% of each beat; the rest is the held pause.
                var wobble = phase < 0.45f ? Mathf.Sin(phase / 0.45f * Mathf.PI * 2f) * 18f : 0f;
                _captureBall.localEulerAngles = new Vector3(0f, 0f, wobble);
            }, Ease.Linear, 0f, true, () =>
            {
                if (_captureLabel != null)
                {
                    _captureLabel.SetText(succeeded ? "Caught!" : "It broke free!");
                    _captureLabel.color = succeeded ? UiPalette.Positive : UiPalette.Negative;
                }
                if (_captureBall != null)
                {
                    if (succeeded) UiTween.Scale(_captureBall, Vector3.one * 1.25f, 0.35f, Ease.OutBack);
                    else UiTween.Punch(_captureBall, 0.3f, 0.4f);
                }
                UiTween.Delay(0.9f, () =>
                {
                    UiTween.Fade(_captureGroup, 0f, 0.3f, Ease.InCubic, 0f, () =>
                    {
                        if (_captureGroup != null) _captureGroup.gameObject.SetActive(false);
                        onComplete?.Invoke();
                    });
                });
            });
        }

        // ------------------------------------------------------------------- cards

        /// <summary>Victory or defeat card.</summary>
        public void PlayResult(BattleOutcome outcome, int moneyEarned, Action onComplete = null)
        {
            switch (outcome)
            {
                case BattleOutcome.PlayerVictory:
                    ShowCard("VICTORY", moneyEarned > 0 ? $"Earned ₽{moneyEarned}." : "The field is yours.",
                        UiPalette.Positive, 2.6f, onComplete);
                    break;
                case BattleOutcome.PlayerDefeat:
                    ShowCard("DEFEAT", "You scrambled back to safety.", UiPalette.Negative, 2.6f, onComplete);
                    break;
                case BattleOutcome.Captured:
                    ShowCard("CAUGHT", "Added to your party.", UiPalette.ScannerCyan, 2.4f, onComplete);
                    break;
                case BattleOutcome.Fled:
                    ShowCard("GOT AWAY", "You escaped safely.", UiPalette.TextSecondary, 1.8f, onComplete);
                    break;
                default:
                    onComplete?.Invoke();
                    break;
            }
        }

        /// <summary>The level-up card, with the creature named.</summary>
        public void PlayLevelUp(CreatureInstance creature, int newLevel, Action onComplete = null)
        {
            var name = creature != null ? UiServices.NameOf(creature) : "Your creature";
            ShowCard("LEVEL " + newLevel, $"{name} grew stronger.", UiPalette.ScannerAmber, 2.0f, onComplete);
        }

        /// <summary>Generic centred card. Scales up with an overshoot, holds, fades.</summary>
        public void ShowCard(string title, string body, Color accent, float hold = 2.2f, Action onComplete = null)
        {
            if (_cardGroup == null || _cardRect == null) { onComplete?.Invoke(); return; }

            if (_cardTitle != null) { _cardTitle.SetText(title ?? string.Empty); _cardTitle.color = accent; }
            if (_cardBody != null) _cardBody.SetText(body ?? string.Empty);
            if (_cardAccent != null) _cardAccent.color = accent;

            _cardGroup.gameObject.SetActive(true);
            UiTween.Kill(ref _cardTween);

            _cardGroup.alpha = 0f;
            _cardRect.localScale = Vector3.one * 0.86f;
            UiTween.Fade(_cardGroup, 1f, 0.24f);
            UiTween.Scale(_cardRect, Vector3.one, 0.46f, Ease.OutBack);

            _cardTween = UiTween.Delay(hold, () =>
            {
                UiTween.Scale(_cardRect, Vector3.one * 0.96f, 0.3f, Ease.InCubic);
                UiTween.Fade(_cardGroup, 0f, 0.3f, Ease.InCubic, 0f, () =>
                {
                    if (_cardGroup != null) _cardGroup.gameObject.SetActive(false);
                    onComplete?.Invoke();
                });
            });
        }

        // -------------------------------------------------------------------- build

        /// <summary>Builds every overlay layer, full-bleed and above the HUD.</summary>
        public void BuildRuntime()
        {
            var root = transform as RectTransform;
            if (root == null) return;
            UiBuilder.Stretch(root);

            _veil = UiBuilder.Image("Veil", root, UiSprites.Solid(), Color.black.WithAlpha(0f), Image.Type.Simple);
            _veil.enabled = false;

            _flash = UiBuilder.Image("Flash", root, UiSprites.Solid(), Color.white.WithAlpha(0f), Image.Type.Simple);
            _flash.enabled = false;

            BuildBanner(root);
            BuildCard(root);
            BuildCapture(root);
        }

        private void BuildBanner(RectTransform root)
        {
            var holder = UiBuilder.Rect("Banner", root, false);
            UiBuilder.Anchor(holder, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(0f, 120f));
            _bannerRect = holder;
            _bannerGroup = UiBuilder.Group(holder, 0f, false, false);

            var bar = UiBuilder.Image("Bar", holder, UiSprites.Solid(), UiPalette.Backdrop.WithAlpha(0.88f),
                Image.Type.Simple);
            UiBuilder.Stretch(bar.rectTransform);

            var stack = UiBuilder.Rect("Stack", holder);
            UiBuilder.Vertical(stack, 2f, new RectOffset(0, 0, 18, 18), TextAnchor.MiddleCenter);

            _bannerTitle = UiBuilder.Text("Title", stack, string.Empty, UiTextRole.Title, UiPalette.ScannerCyan,
                TextAlignmentOptions.Center);
            _bannerTitle.characterSpacing = 8f;
            UiBuilder.Size(_bannerTitle.rectTransform, preferredHeight: 40f, flexibleWidth: 1f);

            _bannerSubtitle = UiBuilder.Text("Subtitle", stack, string.Empty, UiTextRole.Secondary,
                UiPalette.TextSecondary, TextAlignmentOptions.Center);
            UiBuilder.Size(_bannerSubtitle.rectTransform, preferredHeight: 22f, flexibleWidth: 1f);

            holder.gameObject.SetActive(false);
        }

        private void BuildCard(RectTransform root)
        {
            var holder = UiBuilder.Rect("Card", root, false);
            UiBuilder.Anchor(holder, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(520f, 190f));
            _cardRect = holder;
            _cardGroup = UiBuilder.Group(holder, 0f, false, false);

            UiBuilder.Panel("Shell", holder, UiPalette.Surface.WithAlpha(0.97f), 20, true, 32, 0.7f);

            _cardAccent = UiBuilder.Image("Accent", holder, UiSprites.Pill(6), UiPalette.Positive);
            UiBuilder.Anchor(_cardAccent.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(84f, 4f));

            var stack = UiBuilder.Rect("Stack", holder);
            UiBuilder.Vertical(stack, 8f, new RectOffset(30, 30, 36, 26), TextAnchor.MiddleCenter);

            _cardTitle = UiBuilder.Text("Title", stack, string.Empty, UiTextRole.Title, UiPalette.Positive,
                TextAlignmentOptions.Center);
            _cardTitle.characterSpacing = 6f;
            UiBuilder.Size(_cardTitle.rectTransform, preferredHeight: 46f, flexibleWidth: 1f);

            _cardBody = UiBuilder.Text("Body", stack, string.Empty, UiTextRole.Body, UiPalette.TextSecondary,
                TextAlignmentOptions.Center);
            UiBuilder.Size(_cardBody.rectTransform, flexibleWidth: 1f);

            holder.gameObject.SetActive(false);
        }

        private void BuildCapture(RectTransform root)
        {
            var holder = UiBuilder.Rect("Capture", root, false);
            UiBuilder.Anchor(holder, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0f, -60f), new Vector2(320f, 200f));
            _captureGroup = UiBuilder.Group(holder, 0f, false, false);

            var ball = UiBuilder.Rect("Ball", holder, false);
            UiBuilder.Anchor(ball, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.4f),
                new Vector2(0f, 24f), new Vector2(84f, 84f));
            _captureBall = ball;

            // The ball is drawn from the shape library rather than a sprite: a disc, a band,
            // and a button. It reads correctly at this size and needs no art dependency.
            var shell = UiBuilder.Image("Shell", ball, UiSprites.Dot(96), UiPalette.TextPrimary, Image.Type.Simple);
            UiBuilder.Stretch(shell.rectTransform);
            var band = UiBuilder.Image("Band", ball, UiSprites.Solid(), UiPalette.Backdrop, Image.Type.Simple);
            UiBuilder.Anchor(band.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0f, 8f));
            var button = UiBuilder.Image("Button", ball, UiSprites.Dot(32), UiPalette.ScannerCyan, Image.Type.Simple);
            UiBuilder.Anchor(button.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22f, 22f));

            _captureLabel = UiBuilder.Text("Label", holder, string.Empty, UiTextRole.Heading, UiPalette.TextSecondary,
                TextAlignmentOptions.Center);
            UiBuilder.Anchor(_captureLabel.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 16f), new Vector2(0f, 30f));

            holder.gameObject.SetActive(false);
        }
    }
}
