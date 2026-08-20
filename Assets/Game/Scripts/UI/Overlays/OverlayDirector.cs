using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// Full-screen moments: the encounter flash, the battle intro banner, the end of a battle,
    /// and the corner wash that answers a level-up.
    ///
    /// What is <b>not</b> here is as deliberate as what is. The capture sequence and the
    /// level-up card both used to live on this canvas and both were removed, for the same
    /// reason each time: something else was already staging that moment better, and two
    /// systems narrating one event is worse than either of them alone. See the capture section
    /// and <see cref="PlayLevelUp"/>.
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

        [Header("Result")]
        [SerializeField] private CanvasGroup _resultGroup;
        [SerializeField] private RectTransform _resultRect;
        [SerializeField] private Image _resultGround;
        [SerializeField] private Image _resultRays;
        [SerializeField] private RectTransform _resultBand;
        [SerializeField] private Image _resultRibbon;
        [SerializeField] private Image _resultStripe;
        [SerializeField] private Image _resultBall;
        [SerializeField] private Image _resultGlow;
        [SerializeField] private TextMeshProUGUI _resultTitle;
        [SerializeField] private TextMeshProUGUI _resultSub;
        [SerializeField] private TextMeshProUGUI _resultReward;
        [SerializeField] private RectTransform _resultSparkles;
        [SerializeField] private Image _levelGlow;

        private TweenHandle _veilTween;
        private TweenHandle _flashTween;
        private TweenHandle _bannerTween;
        private TweenHandle _cardTween;

        // The exit halves. Each overlay is a hold followed by a move-and-fade, and only the
        // hold used to be held onto — so a card interrupted during its exit went on fading
        // itself to nothing and switching itself off, over the top of the card that replaced
        // it. A VICTORY shown while a level-up was still leaving was invisible for its whole
        // hold and then "finished".
        private TweenHandle _bannerExit;
        private TweenHandle _bannerExitMove;
        private TweenHandle _cardExit;
        private TweenHandle _cardExitScale;
        private TweenHandle _resultLead;
        private TweenHandle _resultHold;
        private TweenHandle _resultRayspin;
        private TweenHandle _resultExit;
        private TweenHandle _levelGlowTween;

        // The staged-but-not-yet-shown result. See PlayResult: the beat is armed with a lead
        // so it lands on the celebration rather than under it, and CueResult pulls it forward
        // the moment the choreography says the celebration has actually started.
        private Action _resultPending;

        // Whoever is still waiting to be told the last one finished. A Kill on the hold threw
        // the previous caller's callback away with it, and a presenter that never hears back
        // from a banner it is waiting on does not start the battle.
        private Action _bannerDone;
        private Action _cardDone;
        private Action _resultDone;

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
            var title = kind == BattleKind.Trainer
                ? Loc.Pick("TRAINER BATTLE", "트레이너 승부")
                : Loc.Pick("WILD ENCOUNTER", "야생 포켓몬");
            var who = kind == BattleKind.Trainer && !string.IsNullOrEmpty(trainerId)
                ? UiServices.Titleise(trainerId.Replace('_', ' '))
                : null;
            var subtitle = who != null
                ? Loc.Pick(who + " wants to battle", who + "이(가) 승부를 걸어왔다!")
                : Loc.Pick("A creature blocks the way", "길을 가로막고 있다!");
            ShowBanner(title, subtitle, kind == BattleKind.Trainer ? UiPalette.ScannerAmber : UiPalette.ScannerCyan,
                2.0f, onComplete);
        }

        /// <summary>Generic banner, for anything else that wants the same treatment.</summary>
        public void ShowBanner(string title, string subtitle, Color accent, float hold = 1.8f, Action onComplete = null)
        {
            if (_bannerGroup == null || _bannerRect == null) { onComplete?.Invoke(); return; }

            if (_bannerTitle != null) { _bannerTitle.SetText(title ?? string.Empty); _bannerTitle.color = accent; }
            if (_bannerSubtitle != null) _bannerSubtitle.SetText(subtitle ?? string.Empty);

            // Everything the previous banner still had running, and the caller it still owed a
            // callback to. The callback is handed over rather than dropped: the presenter that
            // asked for it is waiting on it whether or not its banner got to finish. It is
            // fired at the bottom of this method, after the new banner is fully built, so a
            // caller that answers by asking for another one replaces this one cleanly.
            var superseded = _bannerDone;
            _bannerDone = onComplete;
            KillBanner();

            _bannerGroup.gameObject.SetActive(true);

            var centre = Vector2.zero;
            _bannerRect.anchoredPosition = centre + new Vector2(-160f, 0f);
            _bannerGroup.alpha = 0f;

            UiTween.AnchoredMove(_bannerRect, centre, 0.5f, Ease.OutExpo);
            UiTween.Fade(_bannerGroup, 1f, 0.28f);

            _bannerTween = UiTween.Delay(hold, () =>
            {
                if (_bannerRect != null)
                {
                    _bannerExitMove = UiTween.AnchoredMove(_bannerRect, centre + new Vector2(160f, 0f),
                        0.4f, Ease.InCubic);
                }
                _bannerExit = UiTween.Fade(_bannerGroup, 0f, 0.3f, Ease.InCubic, 0.05f, () =>
                {
                    if (_bannerGroup != null) _bannerGroup.gameObject.SetActive(false);
                    var done = _bannerDone;
                    _bannerDone = null;
                    done?.Invoke();
                });
            });

            superseded?.Invoke();
        }

        /// <summary>Drops the banner's hold and its exit, leaving the callback for the caller.</summary>
        private void KillBanner()
        {
            UiTween.Kill(ref _bannerTween);
            UiTween.Kill(ref _bannerExit);
            UiTween.Kill(ref _bannerExitMove);
        }

        // ---------------------------------------------------------------- capture
        //
        // There is deliberately nothing here any more.
        //
        // A flat Poké Ball used to sit on this canvas and wobble +/-18 degrees once per shake,
        // under a "포획률 42%" readout, while <c>BattlePresenter.PlayCapture</c> threw a real
        // ball in the arena, absorbed the creature into it, dropped it, and shook it exactly
        // CaptureAttemptEvent.Shakes times with rising tension. The same event was dramatised
        // twice, at once, in two dimensions — and the 3D one is the one that owns the drama:
        // its own note says the player counts those shakes, and a second ball counting them in
        // the corner is a second answer to the same question.
        //
        // This is the reasoning already written down for the victory beat a few methods below
        // — the HUD no-ops the result flash because the card is already staging that moment,
        // "the two systems shouting over each other" — applied to the beat it was never
        // applied to.
        //
        // The catch rate went with it. The 3D cannot show a probability, which is the argument
        // for printing one, but the games it is modelled on never print it either: the whole
        // point of the shakes is not knowing, and a number above the ball answers the question
        // the sequence exists to ask. The outcome lines are not lost — the battle log already
        // prints "신난다! 포켓몬을 잡았다!" and "앗! 포켓몬이 볼에서 나와버렸다!" from the same
        // event, and a capture that succeeds ends the battle, so PlayResult's CAUGHT case
        // announces it once, properly, on the beat it belongs to.

        // ------------------------------------------------------------------ result

        /// <summary>
        /// The end of a battle, as one staged beat rather than a toast.
        ///
        /// <b>What was wrong with the old one.</b> It was <see cref="ShowCard"/>: a centred box
        /// that faded up, scaled from 0.86 with an overshoot, held, and faded away — the same
        /// box, the same curve and the same duration whether you had won, lost, caught
        /// something or run. Behind it the choreography was doing something specific and well
        /// staged — the winner turning to the lens, celebrating, throwing sparkles — and the
        /// card had no relationship to any of it. It read as a debug toast that happened to
        /// appear after a battle, and it was in English in a Korean-first game.
        ///
        /// <b>Three things fix that.</b>
        ///
        /// It is <i>grounded</i>: a dark wash rises from the bottom of the frame under the
        /// band, so the words sit on the scene instead of hovering in front of it.
        ///
        /// It is <i>caused</i>: the beat is armed with a lead rather than played on arrival,
        /// and the lead is chosen from the choreography's own shape — the winner spends about
        /// half a second turning to camera before it celebrates. <see cref="CueResult"/> lets
        /// the choreography land it exactly, on the frame the celebration actually starts, and
        /// the lead is the fallback for when that beat never comes.
        ///
        /// It is <i>different per outcome</i>. A win slams in sideways with rays behind it and
        /// sparkles coming up off the band. A loss does not slam at all: it sinks from above,
        /// slowly, on an in-out curve, with no rays, no sparkles and a heavier ground. A
        /// capture rises from below and clicks. A flee is small, quick and quiet. The motion
        /// carries the news before the word is read.
        ///
        /// Experience and level-ups are deliberately <b>not</b> here. They belong on the
        /// plate, where the bar they change is — three cards fighting for one slot is what
        /// <see cref="ShowCard"/>'s own supersede note is a scar from.
        /// </summary>
        public void PlayResult(BattleOutcome outcome, int moneyEarned, Action onComplete = null)
        {
            if (_resultGroup == null || _resultBand == null || _resultRect == null)
            {
                onComplete?.Invoke();
                return;
            }

            var superseded = _resultDone;
            _resultDone = onComplete;
            KillResult();

            string title;
            string sub;
            Color accent;
            float lead;

            switch (outcome)
            {
                case BattleOutcome.PlayerVictory:
                    title = Loc.Pick("VICTORY", "승리!");
                    sub = Loc.Pick("The field is yours.", "승부에서 이겼다!");
                    accent = UiPalette.Positive;
                    // The winner turns to the lens over ~0.5s before it celebrates. Landing on
                    // the celebration is the whole point; landing on the turn is landing early.
                    lead = 0.62f;
                    break;
                case BattleOutcome.Captured:
                    title = Loc.Pick("CAUGHT", "겟!");
                    sub = Loc.Pick("Added to your party.", "동료가 되었다!");
                    accent = UiPalette.ScannerCyan;
                    lead = 0.45f;
                    break;
                case BattleOutcome.PlayerDefeat:
                    title = Loc.Pick("DEFEAT", "패배…");
                    sub = Loc.Pick("You scrambled back to safety.", "눈앞이 캄캄해졌다…");
                    accent = UiPalette.Negative;
                    lead = 0.30f;
                    break;
                case BattleOutcome.Fled:
                    title = Loc.Pick("GOT AWAY", "도망쳤다!");
                    sub = Loc.Pick("You escaped safely.", "무사히 빠져나왔다.");
                    accent = UiPalette.TextSecondary;
                    lead = 0.14f;
                    break;
                default:
                    _resultDone = null;
                    superseded?.Invoke();
                    onComplete?.Invoke();
                    return;
            }

            _resultPending = () => StageResult(outcome, title, sub, accent, moneyEarned);
            _resultLead = UiTween.Delay(lead, RunPendingResult);

            superseded?.Invoke();
        }

        /// <summary>
        /// Lands an armed result now, from the choreography's own beat.
        ///
        /// Safe and free to call when nothing is armed, which is what lets the HUD wire it
        /// straight to the victory beat without knowing whether a result is coming.
        /// </summary>
        public void CueResult()
        {
            if (_resultPending == null) return;
            UiTween.Kill(ref _resultLead);
            RunPendingResult();
        }

        private void RunPendingResult()
        {
            var staged = _resultPending;
            _resultPending = null;
            staged?.Invoke();
        }

        private void StageResult(BattleOutcome outcome, string title, string sub, Color accent, int money)
        {
            if (_resultGroup == null || _resultBand == null) return;

            var won = outcome == BattleOutcome.PlayerVictory || outcome == BattleOutcome.Captured;
            var loud = outcome == BattleOutcome.PlayerVictory;

            _resultGroup.gameObject.SetActive(true);
            _resultGroup.alpha = 1f;

            // The inversion, which is the reference's signature and the answer to "it floats".
            //
            // A good outcome takes the light treatment — a solid near-white ribbon carrying
            // dark navy text, exactly as the reference draws the header of a detail card and
            // the selected row of a menu. A translucent panel with coloured text on it reads
            // as an overlay laid on the picture; a solid inverted bar reads as part of the
            // interface, which is the whole difference the user was pointing at.
            //
            // A bad outcome does not get it. Losing is the un-selected state: the navy panel,
            // light text, no inversion. The value relationship carries the news before the
            // word does.
            var inverted = won;
            var ink = inverted ? BattleSkin.Ink : UiPalette.TextPrimary;

            if (_resultTitle != null)
            {
                _resultTitle.SetText(title);
                _resultTitle.color = inverted ? BattleSkin.Ink : accent;
            }
            if (_resultSub != null)
            {
                _resultSub.SetText(sub);
                _resultSub.color = inverted ? BattleSkin.InkSoft : UiPalette.TextSecondary;
            }
            if (_resultRibbon != null)
                _resultRibbon.color = inverted ? BattleSkin.Light.WithAlpha(0.97f) : BattleSkin.PlateBody.WithAlpha(0.95f);
            if (_resultStripe != null)
                _resultStripe.color = outcome == BattleOutcome.Captured ? BattleSkin.Cyan : accent;
            if (_resultBall != null)
                _resultBall.color = ink.WithAlpha(inverted ? 0.14f : 0.10f);
            if (_resultGlow != null) _resultGlow.color = accent.WithAlpha(loud ? 0.34f : 0.12f);

            // The ground. Heavier on a loss, because a loss should weigh on the frame; a win
            // only needs enough to stop the words floating.
            if (_resultGround != null)
            {
                _resultGround.enabled = true;
                _resultGround.color = BattleSkin.SceneTop.WithAlpha(0f);
                UiTween.Color(BattleSkin.SceneTop.WithAlpha(0f),
                    BattleSkin.SceneTop.WithAlpha(won ? 0.46f : 0.72f), won ? 0.35f : 0.9f,
                    c => { if (_resultGround != null) _resultGround.color = c; },
                    won ? Ease.OutCubic : Ease.InOutQuad);
            }

            // Rays: a win only. They are the loudest thing this overlay owns and spending them
            // on anything else would leave a win with nothing to be louder with.
            if (_resultRays != null)
            {
                _resultRays.enabled = loud;
                if (loud)
                {
                    _resultRays.color = accent.WithAlpha(0f);
                    _resultRays.rectTransform.localScale = Vector3.one * 0.7f;
                    UiTween.Run(0.5f, t =>
                    {
                        if (_resultRays == null) return;
                        _resultRays.color = accent.WithAlpha(0.16f * t);
                        _resultRays.rectTransform.localScale = Vector3.one * Mathf.LerpUnclamped(0.7f, 1.15f, t);
                    }, Ease.OutCubic);
                    _resultRayspin = UiTween.Run(9f, t =>
                    {
                        if (_resultRays != null) _resultRays.rectTransform.localEulerAngles = new Vector3(0f, 0f, t * 40f);
                    }, Ease.Linear);
                }
            }

            // The money line counts rather than appears. Nothing is earned by losing, so the
            // line is simply absent there instead of reading "₽0".
            if (_resultReward != null)
            {
                var show = money > 0;
                _resultReward.gameObject.SetActive(show);
                if (show)
                {
                    var prefix = Loc.Pick("Earned  ₽", "상금  ₽");
                    var counter = AnimatedNumber.Attach(_resultReward,
                        v => prefix + Mathf.RoundToInt(v), 0.9f);
                    counter.SetImmediate(0f);
                    // Gold on navy, ink on white. Gold on a near-white ribbon is the one place
                    // the currency colour cannot be used, because it is barely a colour there.
                    counter.SetColor(inverted ? BattleSkin.Ink : BattleSkin.Gold, 0f);
                    UiTween.Delay(0.42f, () => { if (counter != null) counter.SetValue(money, 0.85f); });
                }
            }

            StageResultBand(outcome, accent, loud);

            var hold = loud ? 2.4f : outcome == BattleOutcome.Fled ? 1.5f : 2.2f;
            _resultHold = UiTween.Delay(hold + 0.5f, () => ExitResult(loud));
        }

        /// <summary>
        /// The band's entrance, which is where the outcomes actually diverge.
        ///
        /// Each is a different verb. A win is <i>thrown</i> in from the side and rebounds; a
        /// capture <i>rises</i> and settles; a defeat <i>sinks</i>, slowly, and keeps sinking a
        /// little through the hold; a flee simply <i>passes through</i>.
        /// </summary>
        private void StageResultBand(BattleOutcome outcome, Color accent, bool loud)
        {
            if (_resultBand == null) return;

            var home = Vector2.zero;
            var group = _resultGroup;

            switch (outcome)
            {
                case BattleOutcome.PlayerVictory:
                    _resultBand.anchoredPosition = home + new Vector2(-520f, 0f);
                    _resultBand.localScale = new Vector3(1.35f, 0.72f, 1f);
                    UiTween.AnchoredMove(_resultBand, home, 0.42f, Ease.OutExpo);
                    UiTween.Run(0.5f, t =>
                    {
                        if (_resultBand != null)
                            _resultBand.localScale = Vector3.LerpUnclamped(new Vector3(1.35f, 0.72f, 1f), Vector3.one, t);
                    }, Ease.OutBack);
                    UiTween.Delay(0.46f, () =>
                    {
                        if (_resultTitle != null) UiTween.Punch(_resultTitle.transform, 0.10f, 0.4f);
                        BurstResultSparkles(accent);
                    });
                    break;

                case BattleOutcome.Captured:
                    _resultBand.anchoredPosition = home + new Vector2(0f, -150f);
                    _resultBand.localScale = Vector3.one * 0.86f;
                    UiTween.AnchoredMove(_resultBand, home, 0.5f, Ease.OutBack);
                    UiTween.Scale(_resultBand, Vector3.one, 0.5f, Ease.OutBack);
                    UiTween.Delay(0.5f, () =>
                    {
                        if (_resultTitle != null) UiTween.Punch(_resultTitle.transform, 0.08f, 0.36f);
                        BurstResultSparkles(accent);
                    });
                    break;

                case BattleOutcome.PlayerDefeat:
                    // No overshoot anywhere. The whole statement is that nothing springs back.
                    _resultBand.anchoredPosition = home + new Vector2(0f, 120f);
                    _resultBand.localScale = Vector3.one;
                    if (group != null) group.alpha = 0f;
                    UiTween.AnchoredMove(_resultBand, home, 0.85f, Ease.InOutQuad);
                    if (group != null) UiTween.Fade(group, 1f, 0.7f, Ease.InOutQuad);
                    // Keeps settling through the hold, a few pixels — enough to feel like
                    // weight rather than a stopped animation.
                    UiTween.Delay(0.9f, () =>
                    {
                        if (_resultBand != null)
                            UiTween.AnchoredMove(_resultBand, home + new Vector2(0f, -14f), 1.6f, Ease.OutCubic);
                    });
                    break;

                default:
                    _resultBand.anchoredPosition = home + new Vector2(240f, 0f);
                    _resultBand.localScale = Vector3.one;
                    UiTween.AnchoredMove(_resultBand, home, 0.34f, Ease.OutCubic);
                    break;
            }
        }

        /// <summary>Sparkles rising off the band. A win and a capture only.</summary>
        private void BurstResultSparkles(Color accent)
        {
            if (_resultSparkles == null) return;

            var count = _resultSparkles.childCount;
            for (var i = 0; i < count; i++)
            {
                var child = _resultSparkles.GetChild(i) as RectTransform;
                if (child == null) continue;
                var image = child.GetComponent<Image>();
                if (image == null) continue;

                var home = new Vector2(child.anchoredPosition.x, 0f);
                child.anchoredPosition = home;
                child.localScale = Vector3.one * UnityEngine.Random.Range(0.5f, 1.15f);
                image.color = accent.WithAlpha(0f);

                var rise = UnityEngine.Random.Range(150f, 320f);
                var drift = UnityEngine.Random.Range(-60f, 60f);
                var life = UnityEngine.Random.Range(0.9f, 1.5f);
                var delay = i * 0.045f + UnityEngine.Random.Range(0f, 0.12f);

                UiTween.Run(life, t =>
                {
                    if (child == null || image == null) return;
                    child.anchoredPosition = home + new Vector2(drift * t, rise * t);
                    child.localEulerAngles = new Vector3(0f, 0f, t * 140f);
                    // Up fast, out slow.
                    image.color = accent.WithAlpha(t < 0.18f ? t / 0.18f : Mathf.Pow(1f - (t - 0.18f) / 0.82f, 1.6f));
                }, Ease.OutCubic, delay, true, () =>
                {
                    if (image != null) image.color = accent.WithAlpha(0f);
                });
            }
        }

        private void ExitResult(bool loud)
        {
            UiTween.Kill(ref _resultRayspin);

            if (_resultGround != null)
            {
                var from = _resultGround.color;
                UiTween.Color(from, from.WithAlpha(0f), 0.45f, c =>
                {
                    if (_resultGround != null) _resultGround.color = c;
                }, Ease.InCubic);
            }

            if (_resultBand != null)
            {
                UiTween.AnchoredMove(_resultBand, _resultBand.anchoredPosition + new Vector2(loud ? 420f : 0f, loud ? 0f : -60f),
                    0.42f, Ease.InCubic);
            }

            _resultExit = UiTween.Fade(_resultGroup, 0f, 0.42f, Ease.InCubic, 0f, () =>
            {
                if (_resultGroup != null) _resultGroup.gameObject.SetActive(false);
                if (_resultGround != null) _resultGround.enabled = false;
                var done = _resultDone;
                _resultDone = null;
                done?.Invoke();
            });
        }

        /// <summary>Drops every part of a result beat, leaving the callback for the caller.</summary>
        private void KillResult()
        {
            _resultPending = null;
            UiTween.Kill(ref _resultLead);
            UiTween.Kill(ref _resultHold);
            UiTween.Kill(ref _resultRayspin);
            UiTween.Kill(ref _resultExit);
        }

        /// <summary>
        /// The screen's share of a level-up, and deliberately only a share of it.
        ///
        /// The level-up itself happens on the plate: the experience bar rolls over, the level
        /// caption ticks, and "레벨 업!" is shouted where the bar the player was watching
        /// actually is. This is the corner of the frame agreeing with it — a warm wash and a
        /// pulse up from the bottom-left, where that plate lives. It takes no space, blocks
        /// nothing, and does not queue: two levels in a row give two pulses, not two cards
        /// fighting over one slot.
        ///
        /// This used to be a full centred card that stacked on top of the victory card and,
        /// per the note on <see cref="ShowCard"/>, sometimes ate it.
        /// </summary>
        public void PlayLevelUp(CreatureInstance creature, int newLevel, Action onComplete = null)
        {
            if (_levelGlow == null) { onComplete?.Invoke(); return; }

            UiTween.Kill(ref _levelGlowTween);
            _levelGlow.enabled = true;
            var tint = UiPalette.ScannerAmber;

            _levelGlowTween = UiTween.Run(0.85f, t =>
            {
                if (_levelGlow == null) return;
                var alpha = t < 0.14f ? t / 0.14f : Mathf.Pow(1f - (t - 0.14f) / 0.86f, 1.8f);
                _levelGlow.color = tint.WithAlpha(alpha * 0.4f);
                _levelGlow.rectTransform.localScale = Vector3.one * Mathf.LerpUnclamped(0.75f, 1.25f, t);
            }, Ease.OutCubic, 0f, true, () =>
            {
                if (_levelGlow != null) { _levelGlow.color = tint.WithAlpha(0f); _levelGlow.enabled = false; }
                onComplete?.Invoke();
            });
        }

        /// <summary>Generic centred card. Scales up with an overshoot, holds, fades.</summary>
        public void ShowCard(string title, string body, Color accent, float hold = 2.2f, Action onComplete = null)
        {
            if (_cardGroup == null || _cardRect == null) { onComplete?.Invoke(); return; }

            if (_cardTitle != null) { _cardTitle.SetText(title ?? string.Empty); _cardTitle.color = accent; }
            if (_cardBody != null) _cardBody.SetText(body ?? string.Empty);
            if (_cardAccent != null) _cardAccent.color = accent;

            // The exit is killed as well as the hold, or the level-up card still leaving would
            // fade the VICTORY card that replaced it to nothing and switch it off — the card
            // invisible for its entire hold and the battle's result never shown.
            var superseded = _cardDone;
            _cardDone = onComplete;
            KillCard();

            _cardGroup.gameObject.SetActive(true);

            _cardGroup.alpha = 0f;
            _cardRect.localScale = Vector3.one * 0.86f;
            UiTween.Fade(_cardGroup, 1f, 0.24f);
            UiTween.Scale(_cardRect, Vector3.one, 0.46f, Ease.OutBack);

            _cardTween = UiTween.Delay(hold, () =>
            {
                _cardExitScale = UiTween.Scale(_cardRect, Vector3.one * 0.96f, 0.3f, Ease.InCubic);
                _cardExit = UiTween.Fade(_cardGroup, 0f, 0.3f, Ease.InCubic, 0f, () =>
                {
                    if (_cardGroup != null) _cardGroup.gameObject.SetActive(false);
                    var done = _cardDone;
                    _cardDone = null;
                    done?.Invoke();
                });
            });

            superseded?.Invoke();
        }

        /// <summary>Drops the card's hold and its exit, leaving the callback for the caller.</summary>
        private void KillCard()
        {
            UiTween.Kill(ref _cardTween);
            UiTween.Kill(ref _cardExit);
            UiTween.Kill(ref _cardExitScale);
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
            BuildLevelGlow(root);
            // Last, so the end of a battle draws over everything else this director owns.
            BuildResult(root);
        }

        /// <summary>
        /// The result stage: a ground wash, rays, a ribbon band and the words on it.
        ///
        /// Built once and re-dressed per outcome rather than rebuilt, because the sprites here
        /// are generated textures — <see cref="UiSprites.SpeedLines"/> alone rasterises a
        /// 512² image — and a battle can end in front of a player who immediately starts
        /// another one.
        /// </summary>
        private void BuildResult(RectTransform root)
        {
            var holder = UiBuilder.Rect("Result", root);
            _resultRect = holder;
            _resultGroup = UiBuilder.Group(holder, 0f, false, false);

            // Solid at the bottom of the frame, gone by the middle of it. This is the piece
            // that stops the words floating: they now sit on something that belongs to the
            // shot rather than hovering in front of it.
            _resultGround = UiBuilder.Image("Ground", holder, UiSprites.VerticalFade(128, 1.5f),
                BattleSkin.SceneTop.WithAlpha(0f), Image.Type.Simple);
            UiBuilder.Anchor(_resultGround.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 620f));
            _resultGround.enabled = false;

            _resultRays = UiBuilder.Image("Rays", holder, UiSprites.SpeedLines(512, 30, 0.18f),
                UiPalette.Positive.WithAlpha(0f), Image.Type.Simple);
            UiBuilder.Anchor(_resultRays.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1180f, 1180f));
            _resultRays.enabled = false;

            // Sparkles live outside the band so the band's own entrance and exit do not drag
            // them around; they are the frame reacting, not the ribbon moving.
            _resultSparkles = UiBuilder.Rect("Sparkles", holder, false);
            UiBuilder.Anchor(_resultSparkles, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, -70f), new Vector2(1000f, 40f));
            for (var i = 0; i < 9; i++)
            {
                var spark = UiBuilder.Image("Sparkle" + i, _resultSparkles, UiSprites.Sparkle(64),
                    UiPalette.Positive.WithAlpha(0f), Image.Type.Simple);
                var size = 26f + (i % 3) * 14f;
                UiBuilder.Anchor(spark.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2((i - 4) * 108f, 0f), new Vector2(size, size));
            }

            _resultBand = UiBuilder.Rect("Band", holder, false);
            UiBuilder.Anchor(_resultBand, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0f, 250f));

            _resultGlow = UiBuilder.Image("Glow", _resultBand, UiSprites.Glow(256),
                UiPalette.Positive.WithAlpha(0.2f), Image.Type.Simple);
            UiBuilder.Anchor(_resultGlow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400f, 520f));

            // Notched at both ends, so it reads as a ribbon laid across the frame rather than
            // as a bar that happens to be the width of the screen. Its colour is set per
            // outcome — near-white and inverted for a win, navy for a loss — in StageResult.
            _resultRibbon = UiBuilder.Image("Ribbon", _resultBand, UiSprites.Banner(160, 48),
                BattleSkin.Light.WithAlpha(0.97f));
            UiBuilder.Anchor(_resultRibbon.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-120f, 160f));

            // The one saturated edge on an otherwise value-only bar. It is what tells a
            // victory from a capture at a glance once the ribbon itself is the same white.
            //
            // On the ribbon's bottom edge, not under the subtitle. Centred and 280px wide it
            // landed exactly on the subtitle's baseline and read as a strikethrough through the
            // line it was supposed to accent — visible in the first defeat capture. An edge on
            // the band belongs to the band; a rule under a sentence belongs to the sentence.
            _resultStripe = UiBuilder.Image("Stripe", _resultBand, UiSprites.Pill(5), UiPalette.Positive);
            UiBuilder.Anchor(_resultStripe.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, -78f), new Vector2(-300f, 5f));

            // A ball watermark at the ribbon's left, the way the reference marks the header of
            // a detail card. Deliberately near-invisible: it is a mark of ownership, not a
            // decoration competing with the word beside it.
            _resultBall = UiBuilder.Image("Ball", _resultBand, UiSprites.BallGlyph(160, 10),
                BattleSkin.Ink.WithAlpha(0.14f), Image.Type.Simple);
            UiBuilder.Anchor(_resultBall.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(110f, 0f), new Vector2(118f, 118f));

            var stack = UiBuilder.Rect("Stack", _resultBand);
            UiBuilder.Vertical(stack, 2f, new RectOffset(60, 60, 18, 18), TextAnchor.MiddleCenter);

            // 78pt inside a 112px row. TMP draws nothing at all when the line is taller than
            // the rect, and a result screen that silently renders no word is the single worst
            // way for this overlay to fail.
            _resultTitle = UiBuilder.Text("Title", stack, string.Empty, UiTextRole.Title,
                UiPalette.Positive, TextAlignmentOptions.Center);
            _resultTitle.fontSize = 78f;
            _resultTitle.characterSpacing = 4f;
            _resultTitle.textWrappingMode = TextWrappingModes.NoWrap;
            _resultTitle.overflowMode = TextOverflowModes.Overflow;
            UiBuilder.Size(_resultTitle.rectTransform, preferredHeight: 112f, minHeight: 112f, flexibleWidth: 1f);
            UiType.ApplyShadow(_resultTitle);

            _resultSub = UiBuilder.Text("Sub", stack, string.Empty, UiTextRole.Body,
                UiPalette.TextSecondary, TextAlignmentOptions.Center);
            UiBuilder.Size(_resultSub.rectTransform, preferredHeight: 42f, minHeight: 42f, flexibleWidth: 1f);

            _resultReward = UiBuilder.Text("Reward", stack, string.Empty, UiTextRole.Numeric,
                UiPalette.ScannerAmber, TextAlignmentOptions.Center);
            UiBuilder.Size(_resultReward.rectTransform, preferredHeight: 42f, minHeight: 42f, flexibleWidth: 1f);
            _resultReward.gameObject.SetActive(false);

            holder.gameObject.SetActive(false);
        }

        /// <summary>The corner wash that answers the plate's own level-up moment.</summary>
        private void BuildLevelGlow(RectTransform root)
        {
            _levelGlow = UiBuilder.Image("LevelGlow", root, UiSprites.Glow(256),
                UiPalette.ScannerAmber.WithAlpha(0f), Image.Type.Simple);
            // Pinned to the bottom-left, which is where the player's plate — and therefore the
            // experience bar this is agreeing with — actually is.
            UiBuilder.Anchor(_levelGlow.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0.5f, 0.5f), new Vector2(180f, 130f), new Vector2(900f, 640f));
            _levelGlow.enabled = false;
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

    }
}
