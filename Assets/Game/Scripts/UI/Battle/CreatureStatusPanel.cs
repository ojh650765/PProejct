using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The name-plate for one combatant, built to the Sword/Shield shape: a sheared dark slab
    /// carrying name, level, a full-width health bar and the exact HP figures, with the
    /// experience bar added on the player's side.
    ///
    /// The slab leans rather than sitting square because the two plates are the only UI in the
    /// shot that touches the corners of the frame, and a square slab in the corner of a
    /// perspective HD-2D scene reads as a debug overlay pasted on top. The lean is small — 14px
    /// over the plate height — which is enough to tie it to the diorama and not enough to make
    /// the text look crooked.
    ///
    /// Two kinds of update reach it and they are not the same thing.
    ///
    /// <see cref="Bind"/> is <b>state</b>: whatever is true now, diffed against the last
    /// snapshot so an unrelated status change never restarts a health tween. Send-out, switch,
    /// the start of a turn.
    ///
    /// <see cref="PlayDamage"/> and its neighbours are <b>events</b>, and they are driven from
    /// the numbers the event itself carries rather than from the creature. That distinction is
    /// load-bearing: the engine resolves a whole turn before a single frame of it is performed,
    /// so a creature struck twice in one turn already holds its post-turn HP by the time the
    /// first hit is staged. Reading the instance would drain the bar straight to the end on the
    /// first blow and then sit still through the second. The event's own
    /// <c>RemainingHp</c> is the only honest source for a hit that is being replayed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CreatureStatusPanel : MonoBehaviour
    {
        /// <summary>Plate width. Public so the HUD anchors the plate at the size its slab sprite was drawn for.</summary>
        public const float PlateWidth = 496f;

        /// <summary>Opponent plate height: name row, health bar, HP figures.</summary>
        public const float OpponentHeight = 132f;

        /// <summary>Player plate height. The extra band is the experience line.</summary>
        public const float PlayerHeight = 148f;

        /// <summary>Horizontal shear of the slab, in pixels across its full height.</summary>
        private const int Shear = 14;

        /// <summary>Below this share of max HP the player's own bar starts to throb.</summary>
        private const float DangerFraction = 0.28f;

        [Header("Identity")]
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _gender;
        [SerializeField] private TextMeshProUGUI _level;
        [SerializeField] private StatusBadge _status;

        [Header("Health")]
        [SerializeField] private AnimatedBar _healthBar;
        [SerializeField] private AnimatedNumber _healthNumber;

        [Header("Experience")]
        [SerializeField] private AnimatedBar _experienceBar;

        [Header("Shell")]
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _rect;
        [SerializeField] private Image _impactFlash;
        [SerializeField] private RectTransform _floaters;

        private BattleSide _side = BattleSide.Player;
        private string _boundInstanceId;
        private int _lastHp = -1;
        private int _lastMaxHp = 1;
        private StatusCondition _lastStatus = StatusCondition.None;
        private TweenHandle _visibilityFade;
        private TweenHandle _flashTween;
        private TweenHandle _shakeTween;
        private TweenHandle _pulseTween;
        private bool _pulsing;
        private float _pulseUrgency;
        private ExperienceRoll _expRoll;

        /// <summary>Instance currently displayed, or null.</summary>
        public string BoundInstanceId => _boundInstanceId;

        /// <summary>Which side this plate speaks for.</summary>
        public BattleSide Side => _side;

        private void OnDisable()
        {
            StopLowPulse();
            _expRoll?.Cancel();
        }

        /// <summary>
        /// Binds a creature. Call this on every state change; it is cheap and only animates
        /// what actually moved.
        /// </summary>
        public void Bind(CreatureInstance creature, bool immediate = false)
        {
            if (creature == null)
            {
                SetVisible(false);
                _boundInstanceId = null;
                StopLowPulse();
                return;
            }

            var isNewCreature = creature.InstanceId != _boundInstanceId;
            _boundInstanceId = creature.InstanceId;
            SetVisible(true);

            if (_name != null) _name.SetText(UiServices.NameOf(creature));
            if (_level != null) _level.SetText("Lv. " + creature.Level);

            BindHealth(creature, immediate || isNewCreature);
            BindStatus(creature, immediate || isNewCreature);
            BindExperience(creature, immediate || isNewCreature);

            if (isNewCreature && !immediate) PlayEnter();
        }

        /// <summary>
        /// Shows or clears the gender mark beside the name. Pass null to hide it.
        ///
        /// Separate from <see cref="Bind"/> because <see cref="CreatureInstance"/> carries no
        /// gender yet. The plate reserves the space and draws nothing rather than deriving a
        /// symbol from the instance id: a made-up mark here would contradict whatever the
        /// party and summary screens later show for the same creature, and a wrong ♂ is worse
        /// than no ♂.
        /// </summary>
        public void SetGenderMark(string mark, Color? color = null)
        {
            if (_gender == null) return;
            var visible = !string.IsNullOrEmpty(mark);
            _gender.gameObject.SetActive(visible);
            if (!visible) return;
            _gender.SetText(mark);
            _gender.color = color ?? UiPalette.TextSecondary;
        }

        // ------------------------------------------------------------------ the hit

        /// <summary>
        /// The plate's half of a landing blow, played on the frame of contact.
        ///
        /// Ordering inside it is deliberate, because it is the whole complaint this method
        /// exists to answer. The flash and the shake fire on frame zero; the bar does
        /// <i>not</i> move yet. It holds for a beat scaled to the size of the hit — the HUD's
        /// share of a hitstop — and only then starts draining, over a duration that is itself
        /// scaled by the size of the hit. The trailing chip fill lags further behind again, so
        /// what was lost stays legible after the bar has already settled.
        ///
        /// Every intensity here is a function of <c>Amount / MaxHp</c>, crit and effectiveness.
        /// A four-point chip and a hit that takes half the bar must not read the same, and
        /// before this they did: one <c>SetValue</c> call, same tween, same 0.55 seconds.
        /// </summary>
        public void PlayDamage(DamageDealtEvent damage)
        {
            if (damage == null) return;

            var max = damage.MaxHp > 0 ? damage.MaxHp : Mathf.Max(1, _lastMaxHp);
            var remaining = Mathf.Clamp(damage.RemainingHp, 0, max);
            var share = Mathf.Clamp01(damage.Amount / (float)max);
            var weight = Mathf.Sqrt(share);

            // A poison tick is damage too, and dressing it in the language of a super-effective
            // critical is how every real hit ends up feeling smaller than it is.
            var indirect = !string.IsNullOrEmpty(damage.IndirectSourceId);

            var loudness = 1f;
            if (damage.Critical) loudness = 1.6f;
            else if (damage.Effectiveness == Effectiveness.SuperEffective) loudness = 1.3f;
            else if (damage.Effectiveness == Effectiveness.NotVeryEffective) loudness = 0.75f;
            if (indirect) loudness *= 0.45f;

            var fraction = remaining / (float)max;
            var colour = UiPalette.Health(fraction);

            // Frame zero: the bar blows out, the plate is knocked.
            _healthBar?.Flash(colour, 0.42f, Mathf.Clamp01(0.55f + weight * 0.45f));
            FlashPlate(damage.Critical ? UiPalette.Critical
                : damage.Effectiveness == Effectiveness.SuperEffective ? UiPalette.ScannerAmber : Color.white,
                Mathf.Clamp(0.10f + weight * 0.30f, 0.08f, 0.42f) * loudness);
            Shake(Mathf.Clamp(Mathf.Lerp(4f, 26f, weight) * loudness, 2f, 34f),
                  Mathf.Lerp(0.22f, 0.42f, weight));

            BattleFloater.Damage(_floaters, damage.Amount, share, damage.Critical, damage.Effectiveness,
                FloatToRight, FloatUpward);

            // The hold, then the drain. Both scale: a chip snaps away, a near-KO grinds down.
            var hitstop = indirect ? 0.02f : Mathf.Lerp(0.05f, 0.16f, weight) * (damage.Critical ? 1.4f : 1f);
            var drain = Mathf.Lerp(0.30f, 1.0f, weight);

            ApplyHealth(remaining, max, drain, hitstop, colour);
        }

        /// <summary>Health coming back. The same machinery, turned the other way and softer.</summary>
        public void PlayHeal(int amount, int remainingHp, int maxHp)
        {
            var max = maxHp > 0 ? maxHp : Mathf.Max(1, _lastMaxHp);
            var remaining = Mathf.Clamp(remainingHp, 0, max);
            var share = Mathf.Clamp01(amount / (float)max);
            var colour = UiPalette.Health(remaining / (float)max);

            _healthBar?.Flash(colour, 0.5f, 0.5f);
            BattleFloater.Heal(_floaters, amount, share, FloatToRight, FloatUpward);
            ApplyHealth(remaining, max, Mathf.Lerp(0.4f, 0.9f, share), 0.04f, colour);
        }

        /// <summary>
        /// Drives the health readout from explicit figures. The one path every animated health
        /// change goes through, so the bar, the chip, the numerals, the colour ramp and the
        /// low-health throb can never disagree about what the creature is on.
        /// </summary>
        private void ApplyHealth(int current, int max, float drainSeconds, float delay, Color colour)
        {
            max = Mathf.Max(1, max);
            current = Mathf.Clamp(current, 0, max);
            var fraction = current / (float)max;

            _lastHp = current;
            _lastMaxHp = max;

            _healthBar?.SetValue(fraction, drainSeconds, null, delay);

            if (_healthNumber != null)
            {
                var capturedMax = max;
                var number = _healthNumber;
                number.WithFormat(v => $"{Mathf.RoundToInt(v)}<size=78%><color=#FFFFFF70>/{capturedMax}</color></size>");
                // Delayed rather than started now, so the figure and the bar move as one thing.
                // A delay survives reduced motion (see UiTween.Run) — it is pacing, not
                // animation — so the count still lands after the hold instead of before it.
                UiTween.Delay(Mathf.Max(0.0001f, delay), () =>
                {
                    if (this == null || number == null) return;
                    number.SetValue(current, drainSeconds);
                    number.SetColor(colour, 0.3f);
                });
            }

            UiTween.Delay(Mathf.Max(0.0001f, delay + drainSeconds * 0.6f), () =>
            {
                if (this == null) return;
                SetLowPulse(fraction);
            });
        }

        // ----------------------------------------------------------- the experience

        /// <summary>
        /// Rolls the experience bar from one running total to another, level-ups and all.
        ///
        /// The chain itself lives in <see cref="ExperienceRoll"/> because the battle-mode
        /// result screen fills the same kind of bar the same way; what belongs here is the
        /// plate's own reaction to a rollover — the level caption, the wash, the knock — which
        /// is handed in as the flourish.
        /// </summary>
        public void PlayExperience(int fromTotal, int fromLevel, int toTotal, int toLevel,
            float budget = 1.5f, Action onLevelUp = null, Action onComplete = null)
        {
            if (_experienceBar == null) { onComplete?.Invoke(); return; }

            // A roll still running belongs to a creature that has since been switched out or
            // to a battle that has since ended; letting it chain on would fight this one for
            // the same bar.
            _expRoll?.Cancel();
            _expRoll = ExperienceRoll.Play(_experienceBar, fromTotal, fromLevel, toTotal, toLevel, budget,
                level =>
                {
                    if (this == null) return;
                    SetLevel(level);
                    if (_rect != null) UiTween.Punch(_rect, 0.06f, 0.4f);
                    FlashPlate(UiPalette.ScannerAmber, 0.35f);
                    Shake(7f, 0.3f);
                    onLevelUp?.Invoke();
                },
                onComplete);
        }

        /// <summary>
        /// Explicit experience fraction, for when the progression system owns the curve.
        /// Preferred over letting <see cref="Bind"/> guess.
        /// </summary>
        public void SetExperienceFraction(float fraction, bool immediate = false)
        {
            if (_experienceBar == null) return;
            if (immediate) _experienceBar.SetImmediate(fraction);
            else _experienceBar.SetValue(fraction, 0.8f);
        }

        /// <summary>Writes the level caption without rebinding anything else.</summary>
        public void SetLevel(int level)
        {
            if (_level != null) _level.SetText("Lv. " + Mathf.Max(1, level));
        }

        /// <summary>The layer floaters are parented into, so callers can add their own.</summary>
        public RectTransform FloaterLayer => _floaters;

        /// <summary>True when this plate's floaters should travel up rather than down.</summary>
        public bool FloatUpward => _side == BattleSide.Player;

        /// <summary>True when this plate's floaters hang off its right edge.</summary>
        public bool FloatToRight => _side == BattleSide.Opponent;

        // ------------------------------------------------------------------ binding

        private void BindHealth(CreatureInstance creature, bool immediate)
        {
            var max = Mathf.Max(1, creature.MaxHp);
            var fraction = creature.HpFraction;
            var colour = UiPalette.Health(fraction);

            if (immediate)
            {
                _lastHp = creature.CurrentHp;
                _lastMaxHp = max;
                _healthBar?.SetImmediate(fraction);
                _healthBar?.SetColorImmediate(colour);

                if (_healthNumber != null)
                {
                    _healthNumber.WithFormat(v => $"{Mathf.RoundToInt(v)}<size=78%><color=#FFFFFF70>/{max}</color></size>");
                    _healthNumber.SetImmediate(creature.CurrentHp);
                    _healthNumber.SetColor(colour, 0f);
                }

                // Never carried across a rebind. _lastHp then belongs to whoever was out
                // before, and comparing across the two makes sending out an already-hurt
                // reserve look like it was just struck.
                StopLowPulse();
                SetLowPulse(fraction);
                return;
            }

            // A state refresh that finds a number the event stream already animated is a
            // no-op, which is the common case now that every hit drives ApplyHealth directly.
            if (creature.CurrentHp == _lastHp && max == _lastMaxHp) return;

            ApplyHealth(creature.CurrentHp, max, 0.45f, 0f, colour);
            _healthBar?.SetColor(colour, 0.35f);
        }

        private void BindStatus(CreatureInstance creature, bool immediate)
        {
            if (_status == null) return;
            var changed = creature.Status != _lastStatus;
            _status.Bind(creature.Status, immediate);
            _lastStatus = creature.Status;
            if (changed && !immediate && creature.Status != StatusCondition.None)
            {
                Shake(9f, 0.34f);
                FlashPlate(UiPalette.Status(creature.Status), 0.22f);
            }
        }

        private void BindExperience(CreatureInstance creature, bool immediate)
        {
            if (_experienceBar == null) return;

            var fraction = ExperienceCurve.FractionWithin(creature.Experience, Mathf.Max(1, creature.Level));
            if (immediate)
            {
                // A different creature is on the bar now, so a roll still chaining belongs to
                // nobody and would keep writing the previous one's levels over this one's.
                _expRoll?.Cancel();
                _experienceBar.SetImmediate(fraction);
                _experienceBar.SetColorImmediate(BattleSkin.Cyan);
                return;
            }
            _experienceBar.SetValue(fraction, 0.8f);
        }

        // ------------------------------------------------------------- punctuation

        /// <summary>
        /// A short knock on the plate, in pixels rather than in scale.
        ///
        /// Scale was what <see cref="UiTween.Punch"/> gave and it is the wrong verb here: a
        /// plate that grows on impact reads as a button being pressed. A plate that is shoved
        /// off its anchor and springs back reads as something hitting the creature it belongs
        /// to. The motion decays exponentially and alternates sign, which is a shake rather
        /// than a wobble, and it always ends exactly on the anchor it started from.
        /// </summary>
        public void Shake(float pixels, float duration = 0.3f)
        {
            if (_rect == null || pixels <= 0.01f) return;

            UiTween.Kill(ref _shakeTween);
            var home = _rect.anchoredPosition;
            var lean = _side == BattleSide.Player ? -1f : 1f;

            _shakeTween = UiTween.Run(duration, t =>
            {
                if (_rect == null) return;
                var decay = Mathf.Pow(1f - t, 2f);
                var swing = Mathf.Sin(t * Mathf.PI * 7f) * pixels * decay;
                _rect.anchoredPosition = home + new Vector2(swing * lean, swing * 0.35f);
            }, Ease.Linear, 0f, true, () =>
            {
                if (_rect != null) _rect.anchoredPosition = home;
            });
        }

        /// <summary>A tinted wash across the slab, in the shape of the slab.</summary>
        public void FlashPlate(Color tint, float peakAlpha, float duration = 0.3f)
        {
            if (_impactFlash == null || peakAlpha <= 0.001f) return;

            UiTween.Kill(ref _flashTween);
            _impactFlash.enabled = true;
            _flashTween = UiTween.Run(duration, t =>
            {
                if (_impactFlash == null) return;
                // Instant attack, square decay: a flash, not a cross-dissolve.
                var alpha = t < 0.12f ? t / 0.12f : Mathf.Pow(1f - (t - 0.12f) / 0.88f, 2f);
                _impactFlash.color = tint.WithAlpha(alpha * peakAlpha);
            }, Ease.Linear, 0f, true, () =>
            {
                if (_impactFlash == null) return;
                _impactFlash.color = tint.WithAlpha(0f);
                _impactFlash.enabled = false;
            });
        }

        // --------------------------------------------------------- low-health throb

        /// <summary>
        /// Starts, retunes or stops the danger throb.
        ///
        /// Only ever on the player's own plate. A red pulse on the opponent's bar says "nearly
        /// there" — encouragement — where the same signal on your own says "you are about to
        /// lose this creature", and only one of those is worth interrupting the player for.
        ///
        /// Colour alone, no motion on the plate: <see cref="UiPalette.Health"/> already ramps
        /// green to amber to red with the fraction, so this brightens what is already the right
        /// hue rather than introducing a second, competing signal. The period tightens as the
        /// creature gets closer to fainting, which is the part that actually escalates.
        /// </summary>
        private void SetLowPulse(float fraction)
        {
            var wanted = _side == BattleSide.Player && fraction > 0f && fraction <= DangerFraction;

            if (!wanted || !UiTween.MotionEnabled)
            {
                StopLowPulse();
                return;
            }

            _pulseUrgency = Mathf.Clamp01(Mathf.InverseLerp(DangerFraction, 0.04f, fraction));
            if (_pulsing) return;
            _pulsing = true;
            PulseStep();
        }

        private void PulseStep()
        {
            // Re-checked every cycle, not just at the start. With motion off a zero-delay tween
            // completes inside Run, so a loop that only tested MotionEnabled on entry would
            // recurse without ever yielding a frame.
            if (this == null || !_pulsing || _healthBar == null || !UiTween.MotionEnabled)
            {
                _pulsing = false;
                return;
            }

            var settled = UiPalette.Health(_healthBar.Fraction);
            var hot = Color.Lerp(settled, Color.white, 0.5f);
            var period = Mathf.Lerp(0.9f, 0.44f, _pulseUrgency);
            var depth = Mathf.Lerp(0.45f, 0.9f, _pulseUrgency);

            UiTween.Kill(ref _pulseTween);
            _pulseTween = UiTween.Run(period, t =>
            {
                if (this == null || _healthBar == null) return;
                _healthBar.SetFillColorImmediate(Color.Lerp(settled, hot, Mathf.Sin(t * Mathf.PI) * depth));
            }, Ease.Linear, 0f, true, PulseStep);
        }

        private void StopLowPulse()
        {
            _pulsing = false;
            UiTween.Kill(ref _pulseTween);
            if (_healthBar != null) _healthBar.SetFillColorImmediate(UiPalette.Health(_healthBar.Fraction));
        }

        // ----------------------------------------------------------------- lifecycle

        /// <summary>Slides the plate in or out. Used on send-out and withdraw.</summary>
        public void SetVisible(bool visible, bool immediate = false)
        {
            if (!visible) StopLowPulse();
            if (_group == null) return;
            if (immediate)
            {
                UiTween.Kill(ref _visibilityFade);
                _group.alpha = visible ? 1f : 0f;
                gameObject.SetActive(visible);
                return;
            }
            UiTween.FadeActive(ref _visibilityFade, _group, visible, 0.25f,
                visible ? Ease.OutCubic : Ease.InCubic);
        }

        private void PlayEnter()
        {
            if (_rect == null) return;
            // A shake still running would restore the plate to the position it was captured at
            // — somewhere along this slide — and leave it there for the rest of the battle.
            UiTween.Kill(ref _shakeTween);
            var target = _rect.anchoredPosition;
            // Enters from the outside edge the plate is pinned to, so it reads as sliding in
            // from off-screen rather than drifting across the creature it describes.
            var from = target + new Vector2(_side == BattleSide.Player ? -90f : 90f, 0f);
            _rect.anchoredPosition = from;
            UiTween.AnchoredMove(_rect, target, 0.45f, Ease.OutCubic);
            UiTween.FadeActive(ref _visibilityFade, _group, true, 0.3f);
        }

        // --------------------------------------------------------------------- build

        /// <summary>
        /// Builds a plate. Both sides share the construction; the player's carries the
        /// experience band underneath the health bar.
        /// </summary>
        public static CreatureStatusPanel Build(Transform parent, BattleSide side)
        {
            var isPlayer = side == BattleSide.Player;
            var height = isPlayer ? PlayerHeight : OpponentHeight;

            var root = UiBuilder.Rect("StatusPanel_" + side, parent, false);
            root.sizeDelta = new Vector2(PlateWidth, height);

            var panel = root.gameObject.AddComponent<CreatureStatusPanel>();
            panel._rect = root;
            panel._side = side;
            panel._group = UiBuilder.Group(root);
            BuildSlab(root, Mathf.RoundToInt(height));

            // Side padding is wider than the top/bottom because the slab's edges lean: at the
            // top of the plate the left edge has already travelled Shear pixels inward, and
            // text laid out to a square inset would touch it.
            var stack = UiBuilder.Rect("Stack", root);
            UiBuilder.Vertical(stack, 7f, new RectOffset(30, 30, 14, 14), TextAnchor.MiddleLeft);

            // --- name row: name, gender mark, status pill, level right-aligned
            var nameRow = UiBuilder.Rect("NameRow", stack);
            UiBuilder.Horizontal(nameRow, 10f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(nameRow, preferredHeight: 38f, minHeight: 38f, flexibleWidth: 1f);

            panel._name = UiBuilder.Text("Name", nameRow, "—", UiTextRole.Body, UiPalette.TextPrimary);
            panel._name.fontStyle = FontStyles.Bold;
            panel._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(panel._name.rectTransform, minWidth: 90f, flexibleWidth: 1f);

            panel._gender = UiBuilder.Text("Gender", nameRow, string.Empty, UiTextRole.Body,
                UiPalette.TextSecondary, TextAlignmentOptions.Center);
            UiBuilder.Size(panel._gender.rectTransform, preferredWidth: 22f, minWidth: 22f);
            panel._gender.gameObject.SetActive(false);

            // Carried on both plates, not just the player's. A status the opponent is under
            // changes what the player should do next as much as one of their own, and the badge
            // hides itself when there is nothing to say, so it costs no space when idle.
            panel._status = StatusBadge.Build(nameRow, 30f);

            panel._level = UiBuilder.Text("Level", nameRow, "Lv. 1", UiTextRole.Numeric,
                UiPalette.TextSecondary, TextAlignmentOptions.Right);
            // Wide enough for "Lv. 100" at the Numeric size without ellipsing. Level is the one
            // figure on the plate that silently grows a digit late in the game.
            UiBuilder.Size(panel._level.rectTransform, preferredWidth: 132f, minWidth: 132f);

            // --- the bar pair, straight off the reference: a green health bar with a thinner
            // cyan experience bar directly beneath it, both fully rounded. The two never share
            // a hue — health's colour is information (the green→amber→red ramp is the warning)
            // and experience's is not, so making the lower one cyan is what keeps a draining
            // health bar from ever being mistaken for a filling experience one.
            panel._healthBar = AnimatedBar.Build("HealthBar", stack, 12f, BattleSkin.Health(1f), true);

            if (isPlayer)
            {
                panel._experienceBar = AnimatedBar.Build("ExpBar", stack, 6f, BattleSkin.Cyan, false,
                    BattleSkin.SceneTop.WithAlpha(0.75f));
                panel._experienceBar.SetImmediate(0f);
            }

            // --- exact HP figures, left-aligned under the bars
            //
            // Shown for the opponent as well as the player. That reverses the asymmetry this
            // plate used to enforce — the genre hides the opponent's raw HP so "can I survive
            // this" stays a judgement call — because the reference layout being matched puts
            // the figures on both plates. Flip the opponent back to a percentage by giving that
            // side a plain label instead of an AnimatedNumber here; nothing else depends on it.
            var hpText = UiBuilder.Text("HpValue", stack, "0/0", UiTextRole.Numeric,
                UiPalette.TextPrimary, TextAlignmentOptions.Left);
            UiBuilder.Size(hpText.rectTransform, preferredHeight: 30f, minHeight: 30f, flexibleWidth: 1f);
            panel._healthNumber = AnimatedNumber.Attach(hpText, v => Mathf.RoundToInt(v).ToString(), 0.6f);

            // --- impact wash, in the shape of the slab and above its text. Built after the
            // stack so it draws over the readout it is washing, and disabled until something
            // hits: an always-on transparent full-plate image is a draw call per plate per
            // frame for nothing.
            panel._impactFlash = UiBuilder.Image("ImpactFlash", root, UiSprites.Slant(Mathf.RoundToInt(height), Shear),
                Color.white.WithAlpha(0f));
            UiBuilder.Stretch(panel._impactFlash.rectTransform);
            panel._impactFlash.enabled = false;

            // --- floater layer, last so numbers draw over everything, and unclipped so they
            // can travel outside the plate they belong to.
            panel._floaters = UiBuilder.Rect("Floaters", root);

            // Hidden until something is bound to it.
            //
            // The plate is built with placeholder content — an em dash for the name, "Lv. 1",
            // a full green bar — and the HUD flies in as a whole, so an unbound plate is a
            // complete, confident, entirely fictional readout of a creature that is not on the
            // field yet. That used to be invisible by luck: send-out events reached the HUD on
            // the arrival clock, so both plates were bound before the HUD was ever shown. Now
            // that every event waits for its beat, the player's plate is genuinely empty until
            // its creature is thrown, and an empty plate has to look empty.
            panel.SetVisible(false, true);

            return panel;
        }

        /// <summary>The sheared slab, its rim and the shadow beneath it.</summary>
        private static void BuildSlab(RectTransform root, int height)
        {
            // Drawn first so it lands behind the body. Rounded rather than sheared: at this
            // spread and opacity the lean is invisible, and a sheared shadow would need a
            // second generated texture per plate height for no visible gain.
            var shadow = UiBuilder.Image("Shadow", root, UiSprites.Shadow(16, 22),
                UiPalette.Shadow.WithAlpha(0.55f));
            shadow.rectTransform.offsetMin = new Vector2(-14f, -18f);
            shadow.rectTransform.offsetMax = new Vector2(14f, 10f);

            // Navy, never black — the reference's whole ground is indigo, and the old
            // near-black Backdrop was the loudest thing separating these plates from it. Kept
            // deeper and more opaque than a menu panel because these sit over a lit diorama
            // rather than over a blurred photo of one; see BattleSkin.PlateBody.
            var body = UiBuilder.Image("Body", root, UiSprites.Slant(height, Shear),
                BattleSkin.PlateBody);
            UiBuilder.Stretch(body.rectTransform);

            var rim = UiBuilder.Image("Rim", root, UiSprites.SlantFrame(height, Shear, 2),
                BattleSkin.PanelRim);
            UiBuilder.Stretch(rim.rectTransform);
        }
    }
}
