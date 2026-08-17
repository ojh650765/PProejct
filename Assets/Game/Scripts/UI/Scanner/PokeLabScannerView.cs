using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>How the scanner is presented. The screen content is the same; the shell is not.</summary>
    public enum ScannerMode
    {
        /// <summary>Powered down and not rendering.</summary>
        Hidden = 0,
        /// <summary>Handheld, raised by the player in the overworld. Shows a pre-battle scout.</summary>
        Overworld = 1,
        /// <summary>Docked into the battle HUD. Live, reacting to every readout.</summary>
        Docked = 2,
    }

    /// <summary>
    /// The Poké Lab battle scanner — the game's signature feature.
    ///
    /// It is a diegetic trainer's field device, not a debug panel, and every decision here
    /// follows from that. It boots rather than appearing. It sweeps its glass when it
    /// recalculates. Numbers count to their new value and leave a ghost marker behind so the
    /// size of a change is visible. Threats arrive and clear individually rather than the
    /// list swapping contents. When it is not sure, it says so loudly instead of showing a
    /// confident wrong number.
    ///
    /// Data flows one way: the intelligence layer pushes a <see cref="TacticalReadout"/>
    /// through <see cref="IPokeLabListener.OnReadoutUpdated"/>, and this view renders it.
    /// The view never asks the oracle for anything mid-battle and never touches battle state.
    /// The <see cref="IBattleEventListener"/> implementation is presentation only — it drives
    /// screen effects and the target header, never numbers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PokeLabScannerView : MonoBehaviour, IPokeLabListener, IBattleEventListener
    {
        [Header("Shell")]
        [SerializeField] private ScannerScreen _screen;
        [SerializeField] private CanvasGroup _rootGroup;
        [SerializeField] private RectTransform _rootRect;

        [Header("Header")]
        [SerializeField] private TextMeshProUGUI _deviceLabel;
        [SerializeField] private ConfidenceBadge _confidence;
        [SerializeField] private TextMeshProUGUI _targetName;
        [SerializeField] private TextMeshProUGUI _targetMeta;
        [SerializeField] private RectTransform _typeBadgeRow;
        [SerializeField] private Image _targetPortrait;

        [Header("Readout")]
        [SerializeField] private ProbabilityGauge _gauge;
        [SerializeField] private TextMeshProUGUI _typeSummary;
        [SerializeField] private RectTransform _matchupChipRow;
        [SerializeField] private RectTransform _recommendationBox;
        [SerializeField] private TextMeshProUGUI _recommendationText;
        [SerializeField] private EvidencePanel _evidence;
        [SerializeField] private ThreatPanel _threats;
        [SerializeField] private SwitchPanel _switches;
        [SerializeField] private RectTransform _warningRow;
        [SerializeField] private TextMeshProUGUI _warningText;
        [SerializeField] private TextMeshProUGUI _headToHead;

        [Header("Footer")]
        [SerializeField] private Image _liveDot;
        [SerializeField] private TextMeshProUGUI _footerText;

        [Header("Behaviour")]
        [Tooltip("Threat level at or above which the glass washes red on a new threat.")]
        [SerializeField] private ThreatLevel _alertThreshold = ThreatLevel.Dangerous;
        [Tooltip("Point swing that counts as a moment worth flashing the whole screen for.")]
        [SerializeField] private float _bigSwingPoints = 8f;
        [SerializeField] private bool _buildOnAwake = true;

        [Header("Audio")]
        [SerializeField] private ScannerAudio _audio;

        private readonly List<TypeBadge> _typeBadges = new List<TypeBadge>(2);
        private readonly List<GameObject> _matchupChips = new List<GameObject>(4);
        private readonly StringBuilder _builder = new StringBuilder(160);

        private ScannerMode _mode = ScannerMode.Hidden;
        private TacticalReadout _lastReadout;
        private bool _booted;
        private bool _built;
        private float _liveDotTime;

        // Everything SetMode starts, held so the next call can kill it. The device is raised
        // and lowered by a held key, so a mode change landing inside the previous change's
        // animation is ordinary play rather than an edge case.
        private TweenHandle _visibilityFade;
        private TweenHandle _slide;
        private TweenHandle _deferredRender;
        private Vector2 _restingPosition;

        /// <summary>Current presentation mode.</summary>
        public ScannerMode Mode => _mode;

        /// <summary>Raised when the player picks a switch recommendation directly off the scanner.</summary>
        public Action<int> SwitchRequested;

        /// <summary>The most recent readout rendered, or null before the first one.</summary>
        public TacticalReadout LastReadout => _lastReadout;

        private void Awake()
        {
            if (_buildOnAwake && _screen == null)
            {
                BuildRuntime();
            }
            else if (_switches != null)
            {
                // Prefab-wired path only. BuildRuntime does its own subscribing, and doing it
                // in both places would fire every switch request twice.
                _switches.SwitchRequested += index => SwitchRequested?.Invoke(index);
            }

            if (_rootRect == null) _rootRect = transform as RectTransform;
            if (_rootGroup == null) _rootGroup = UiBuilder.Group(this, 0f, false, false);
            SetModeImmediate(ScannerMode.Hidden);
        }

        private void Update()
        {
            if (_liveDot == null || _mode != ScannerMode.Docked) return;
            // The live indicator breathes so a docked scanner never looks like a static image.
            _liveDotTime += Time.unscaledDeltaTime;
            var pulse = Mathf.Sin(_liveDotTime * Mathf.PI * 2f / 1.6f) * 0.5f + 0.5f;
            _liveDot.color = UiPalette.ScannerCyan.WithAlpha(Mathf.Lerp(0.3f, 1f, pulse));
        }

        // -------------------------------------------------------------------- modes

        /// <summary>
        /// Raises or docks the scanner with the full power-on sequence. Calling this with the
        /// mode it is already in is a no-op, so input handlers can be naive about it.
        /// </summary>
        public void SetMode(ScannerMode mode)
        {
            if (_mode == mode) return;
            var previous = _mode;
            _mode = mode;

            UiTween.Kill(ref _deferredRender);

            if (mode == ScannerMode.Hidden)
            {
                _audio?.PlayPowerDown();
                if (_screen != null) _screen.PlayShutdown();
                UiTween.Kill(ref _visibilityFade);
                _visibilityFade = UiTween.Fade(_rootGroup, 0f, 0.22f, Ease.InCubic, 0f, () =>
                {
                    // Re-read rather than captured. This fade used to be untracked, so raising
                    // the device again inside its 0.22s left the screen switched off while
                    // _mode said Overworld and IsRaised said true — and BindScout, seeing a
                    // mode that was not Hidden, had nothing to recover from.
                    if (_mode != ScannerMode.Hidden) return;
                    if (_rootGroup != null) { _rootGroup.interactable = false; _rootGroup.blocksRaycasts = false; }
                    if (_rootRect != null) _rootRect.gameObject.SetActive(false);
                });
                _booted = false;
                _threats?.ClearAll();
                _switches?.ClearAll();
                return;
            }

            UiTween.Kill(ref _visibilityFade);
            if (_rootRect != null) _rootRect.gameObject.SetActive(true);
            ApplyModeLayout(mode);

            if (_rootGroup != null)
            {
                _rootGroup.interactable = mode == ScannerMode.Docked;
                _rootGroup.blocksRaycasts = mode == ScannerMode.Docked;
            }

            // Slide in from the side it lives on, so the device has a physical origin.
            if (_rootRect != null)
            {
                // The resting position is read only when no slide is running. Killing one
                // mid-flight leaves the rect part-way home, and adopting that as the new
                // target would walk the panel a little further off its corner on every raise
                // the player made during the previous raise.
                if (_slide == null || !_slide.IsAlive) _restingPosition = _rootRect.anchoredPosition;
                UiTween.Kill(ref _slide);

                _rootRect.anchoredPosition = _restingPosition
                    + new Vector2(0f, mode == ScannerMode.Overworld ? -70f : 0f)
                    + new Vector2(mode == ScannerMode.Docked ? 60f : 0f, 0f);
                _slide = UiTween.AnchoredMove(_rootRect, _restingPosition, 0.42f, Ease.OutCubic);
            }

            _visibilityFade = UiTween.Fade(_rootGroup, 1f, 0.24f);

            if (previous == ScannerMode.Hidden)
            {
                _audio?.PlayBoot();
                if (_screen != null) _screen.PlayBoot(() => _booted = true);
                else _booted = true;
                // Tracked, and it re-checks on arrival: lowering the device inside this delay
                // used to leave it to paint a readout under a deactivated root, which also
                // re-activated the recommendation box behind a screen that was off.
                if (_lastReadout != null)
                {
                    _deferredRender = UiTween.Delay(0.75f, () =>
                    {
                        if (_mode == ScannerMode.Hidden || _lastReadout == null) return;
                        RenderReadout(_lastReadout, false);
                    });
                }
            }
            else
            {
                _booted = true;
            }

            if (_footerText != null)
            {
                _footerText.SetText(mode == ScannerMode.Docked ? "LIVE · BATTLE LINK" : "FIELD SCAN");
            }
            if (_liveDot != null) _liveDot.enabled = mode == ScannerMode.Docked;
        }

        /// <summary>Sets the mode with no animation. Used at Awake and on scene load.</summary>
        public void SetModeImmediate(ScannerMode mode)
        {
            // Immediate means immediate: nothing the animated path started may still be in
            // flight to undo it a fraction of a second later.
            UiTween.Kill(ref _visibilityFade);
            UiTween.Kill(ref _slide);
            UiTween.Kill(ref _deferredRender);

            _mode = mode;
            var visible = mode != ScannerMode.Hidden;
            if (_rootRect != null) _rootRect.gameObject.SetActive(visible);
            if (_rootGroup != null)
            {
                _rootGroup.alpha = visible ? 1f : 0f;
                _rootGroup.interactable = mode == ScannerMode.Docked;
                _rootGroup.blocksRaycasts = mode == ScannerMode.Docked;
            }
            if (visible)
            {
                ApplyModeLayout(mode);
                // The screen has its own power state, and nothing but the boot sequence lights
                // it. Skipping this after a shutdown left a correctly positioned, correctly
                // bound scanner drawing on dark glass.
                _screen?.ShowImmediate();
            }
            _booted = visible;
            if (_liveDot != null) _liveDot.enabled = mode == ScannerMode.Docked;
        }

        /// <summary>
        /// Battle-mode sections are hidden in overworld scout mode because a pre-battle scan
        /// has no live field to threaten anything or a party to switch into.
        /// </summary>
        private void ApplyModeLayout(ScannerMode mode)
        {
            var docked = mode == ScannerMode.Docked;
            if (_threats != null) _threats.gameObject.SetActive(docked);
            if (_switches != null) _switches.gameObject.SetActive(docked);
            if (_recommendationBox != null) _recommendationBox.gameObject.SetActive(docked);
            if (_headToHead != null) _headToHead.gameObject.SetActive(!docked);
            if (_deviceLabel != null) _deviceLabel.SetText(docked ? "POKÉ LAB · TACTICAL" : "POKÉ LAB · FIELD SCAN");
        }

        // ------------------------------------------------------------------ binding

        /// <summary>
        /// The live path. Called by the intelligence layer whenever conditions change.
        /// Safe to call before the boot sequence finishes — the readout is cached and
        /// rendered when the screen comes up.
        /// </summary>
        public void OnReadoutUpdated(TacticalReadout readout)
        {
            if (readout == null) return;
            var isFirst = _lastReadout == null;
            _lastReadout = readout;

            if (!_booted || _mode == ScannerMode.Hidden) return;
            RenderReadout(readout, !isFirst);
        }

        /// <summary>
        /// The overworld path: a species-level prior from
        /// <see cref="IPokeLabOracle.ScoutEncounter"/> with no live battle behind it.
        /// </summary>
        public void BindScout(MatchupPrediction prediction, int opponentSpeciesId, bool scouted)
        {
            if (_mode == ScannerMode.Hidden) SetMode(ScannerMode.Overworld);

            BindTarget(opponentSpeciesId, null);

            if (prediction == null)
            {
                if (_gauge != null) { _gauge.Bind(0.5f, 0f, false); _gauge.SetCaption("no signal"); }
                _confidence?.Bind(false, "Target not resolved");
                _evidence?.ShowEmpty("Move closer to complete the scan.");
                // Cleared with the rest of it. Left alone, the previous target's type sentence
                // sat under an unresolved scan and read as a statement about this one.
                BindTypeSummary(null, null);
                BindWarnings(null);
                BindHeadToHead(null);
                BindMatchupChips(null);
                return;
            }

            var probability = prediction.FirstWinProbability;
            _gauge?.Bind(probability, 0f, scouted);
            _gauge?.SetCaption(scouted ? "species matchup prior" : "unscouted species · prior only");
            _confidence?.Bind(scouted, scouted ? "Species on record" : "First encounter — no field data");
            _evidence?.Bind(prediction);
            BindTypeSummary(prediction, null);
            BindMatchupChips(prediction);
            BindWarnings(prediction.Warnings);
            BindHeadToHead(prediction);

            _screen?.PlaySweep();
            _audio?.PlayRefresh();
        }

        /// <summary>Renders a readout. <paramref name="animate"/> is false for the first paint.</summary>
        private void RenderReadout(TacticalReadout readout, bool animate)
        {
            var matchup = readout.Matchup;
            var opponentSpeciesId = matchup?.SecondSpeciesId ?? 0;
            BindTarget(opponentSpeciesId, readout);

            _gauge?.Bind(readout.LiveWinProbability, readout.DeltaPoints, readout.IsConfident, !animate);
            _gauge?.SetCaption(readout.IsConfident ? "live estimate" : "estimate from partial data");

            _confidence?.Bind(readout.IsConfident,
                readout.IsConfident ? "Full scan" : "Partial scan — opponent not fully observed");

            _evidence?.Bind(matchup, animate);
            _threats?.Bind(readout.Threats, animate);
            _switches?.Bind(readout.Switches, animate);

            BindTypeSummary(matchup, readout.TypeSummary);
            BindMatchupChips(matchup);
            BindWarnings(matchup?.Warnings);
            BindRecommendation(readout.RecommendedLine, animate);

            if (!animate) return;

            // --- reaction layer: the screen responds to what changed, not merely that it did.
            _screen?.PlaySweep();

            var bigSwing = Mathf.Abs(readout.DeltaPoints) >= _bigSwingPoints;
            var newSevereThreat = _threats != null && _threats.HadNewThreat &&
                                  (int)_threats.HighestLevel >= (int)_alertThreshold;

            if (newSevereThreat)
            {
                _screen?.PlayAlert(UiPalette.Threat(_threats.HighestLevel), 0.26f, 0.8f);
                _screen?.PlayFlicker(0.35f);
                _audio?.PlayThreat(_threats.HighestLevel);
            }
            else if (bigSwing)
            {
                _screen?.PlayAlert(UiPalette.Delta(readout.DeltaPoints), 0.18f, 0.6f);
                _audio?.PlaySwing(readout.DeltaPoints);
            }
            else
            {
                _audio?.PlayRefresh();
            }

            if (_switches != null && _switches.TopChanged) _audio?.PlayRerank();
        }

        private void BindTarget(int speciesId, TacticalReadout readout)
        {
            if (_targetName != null)
            {
                _targetName.SetText(speciesId > 0 ? UiServices.SpeciesName(speciesId) : "No target");
            }

            UiServices.TypesOf(speciesId, out var primary, out var secondary);
            EnsureTypeBadges(primary, secondary);

            if (_targetPortrait != null)
            {
                var portrait = UiServices.PortraitOf(speciesId);
                _targetPortrait.sprite = portrait != null ? portrait : UiTypeIcons.Get(primary, 64);
                _targetPortrait.color = portrait != null ? Color.white : UiPalette.Type(primary).WithAlpha(0.7f);
                _targetPortrait.enabled = speciesId > 0;
            }

            if (_targetMeta == null) return;

            _builder.Clear();
            if (readout != null)
            {
                var opponent = UiServices.Engine?.State?.ActiveOf(BattleSide.Opponent);
                if (opponent != null)
                {
                    _builder.Append("Lv ").Append(opponent.Level);
                    _builder.Append("   HP ").Append(Mathf.RoundToInt(opponent.HpFraction * 100f)).Append('%');
                    if (opponent.Status != StatusCondition.None)
                    {
                        _builder.Append("   ").Append(StatusBadge.FullName(opponent.Status));
                    }
                }
                else
                {
                    _builder.Append("live target");
                }
            }
            else
            {
                _builder.Append("wild encounter");
            }
            _targetMeta.SetText(_builder.ToString());
        }

        private void EnsureTypeBadges(ElementType primary, ElementType secondary)
        {
            if (_typeBadgeRow == null) return;
            while (_typeBadges.Count < 2) _typeBadges.Add(TypeBadge.Build(_typeBadgeRow, ElementType.None, 24f));
            _typeBadges[0].Bind(primary);
            _typeBadges[1].Bind(secondary);
        }

        private void BindTypeSummary(MatchupPrediction matchup, string authoredSummary)
        {
            if (_typeSummary == null) return;

            if (!string.IsNullOrWhiteSpace(authoredSummary))
            {
                _typeSummary.SetText(authoredSummary);
                return;
            }

            if (matchup == null)
            {
                _typeSummary.SetText("Type advantage unresolved.");
                return;
            }

            // Fall back to composing a sentence from the multipliers, so the header still
            // reads as English when the oracle has not authored a summary line.
            var yours = matchup.AbilityTypeEffectFirst;
            var theirs = matchup.AbilityTypeEffectSecond;
            string sentence;
            if (yours > theirs * 1.5f) sentence = $"Your best coverage hits for {UiType.Multiplier(yours)} — you have the type edge.";
            else if (theirs > yours * 1.5f) sentence = $"They hit you for {UiType.Multiplier(theirs)} — you are on the wrong side of the chart.";
            else if (yours <= 0f) sentence = "Nothing you carry can touch this type.";
            else sentence = "Types are close to even; the stat spread decides this one.";
            _typeSummary.SetText(sentence);
        }

        /// <summary>
        /// Draws the two offensive multiplier chips. The ability-adjusted value is what is
        /// shown, because an immunity ability is precisely the case where the raw type chart
        /// would mislead the player into a wasted turn.
        /// </summary>
        private void BindMatchupChips(MatchupPrediction matchup)
        {
            if (_matchupChipRow == null) return;

            for (var i = 0; i < _matchupChips.Count; i++)
            {
                if (_matchupChips[i] != null) _matchupChips[i].SetActive(false);
            }
            if (matchup == null) return;

            DrawChip(0, "YOU DEAL", matchup.AbilityTypeEffectFirst, matchup.TypeEffectFirst);
            DrawChip(1, "YOU TAKE", matchup.AbilityTypeEffectSecond, matchup.TypeEffectSecond);
        }

        private void DrawChip(int index, string caption, float effective, float raw)
        {
            while (_matchupChips.Count <= index) _matchupChips.Add(BuildChip(_matchupChipRow));
            var chip = _matchupChips[index];
            if (chip == null) return;
            chip.SetActive(true);

            var labels = chip.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (labels.Length >= 2)
            {
                labels[0].SetText(caption);
                labels[1].SetText(UiType.Multiplier(effective));
                // "YOU DEAL" is good when high; "YOU TAKE" is good when low. The caption
                // decides which direction is friendly.
                var favourable = index == 0 ? effective > 1f : effective < 1f;
                var unfavourable = index == 0 ? effective < 1f : effective > 1f;
                labels[1].color = favourable ? UiPalette.Positive : (unfavourable ? UiPalette.Negative : UiPalette.TextSecondary);

                // Flag when an ability changed the answer — that is a caveat worth a mark.
                labels[0].color = Mathf.Approximately(effective, raw) ? UiPalette.TextMuted : UiPalette.ScannerAmber;
            }
        }

        private static GameObject BuildChip(Transform parent)
        {
            var root = UiBuilder.Rect("MatchupChip", parent, false);
            UiBuilder.Size(root, preferredWidth: 92f, preferredHeight: 40f, minWidth: 80f, minHeight: 40f);
            var bg = UiBuilder.Image("Bg", root, UiSprites.Panel(9), UiPalette.SurfaceSunken.WithAlpha(0.75f));
            UiBuilder.Stretch(bg.rectTransform);

            var stack = UiBuilder.Rect("Stack", root);
            UiBuilder.Vertical(stack, 0f, new RectOffset(9, 9, 4, 4), TextAnchor.MiddleLeft);

            var caption = UiBuilder.Text("Caption", stack, string.Empty, UiTextRole.Overline, UiPalette.TextMuted);
            caption.fontSize = 9f;
            caption.characterSpacing = 4f;
            UiBuilder.Size(caption.rectTransform, preferredHeight: 12f, flexibleWidth: 1f);

            var value = UiBuilder.Text("Value", stack, "×1", UiTextRole.Numeric, UiPalette.TextSecondary);
            value.fontSize = 18f;
            UiBuilder.Size(value.rectTransform, preferredHeight: 20f, flexibleWidth: 1f);

            return root.gameObject;
        }

        private void BindRecommendation(string line, bool animate)
        {
            if (_recommendationBox == null || _recommendationText == null) return;

            var hasLine = !string.IsNullOrWhiteSpace(line);
            _recommendationBox.gameObject.SetActive(hasLine && _mode == ScannerMode.Docked);
            if (!hasLine) return;

            var changed = _recommendationText.text != line;
            _recommendationText.SetText(line);
            if (animate && changed) UiTween.Punch(_recommendationBox, 0.03f, 0.32f);
        }

        private void BindWarnings(string[] warnings)
        {
            if (_warningRow == null || _warningText == null) return;

            if (warnings == null || warnings.Length == 0)
            {
                _warningRow.gameObject.SetActive(false);
                return;
            }

            _builder.Clear();
            for (var i = 0; i < warnings.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(warnings[i])) continue;
                if (_builder.Length > 0) _builder.Append('\n');
                _builder.Append("• ").Append(warnings[i]);
            }

            var any = _builder.Length > 0;
            _warningRow.gameObject.SetActive(any);
            if (any) _warningText.SetText(_builder.ToString());
        }

        private void BindHeadToHead(MatchupPrediction matchup)
        {
            if (_headToHead == null) return;

            if (matchup == null || matchup.DirectBattles <= 0)
            {
                _headToHead.SetText("No recorded encounters between these species.");
                _headToHead.color = UiPalette.TextMuted;
                return;
            }

            var favour = matchup.DirectFirstWins >= matchup.DirectSecondWins ? "in your favour" : "against you";
            _headToHead.SetText(
                $"Head-to-head record: <b>{matchup.DirectFirstWins}–{matchup.DirectSecondWins}</b> {favour} " +
                $"across {matchup.DirectBattles} recorded battles.");
            _headToHead.color = UiPalette.TextSecondary;
        }

        // ----------------------------------------------------- battle event reactions

        /// <summary>
        /// Presentation-only reactions. The scanner's numbers come exclusively from
        /// <see cref="OnReadoutUpdated"/>; this handler exists so the device visibly responds
        /// on the same beat as the battle rather than a frame after the readout catches up.
        /// </summary>
        public void OnBattleEvent(BattleEvent evt)
        {
            if (_mode != ScannerMode.Docked || !_booted) return;

            switch (evt)
            {
                case CreatureSentOutEvent sentOut when sentOut.Side == BattleSide.Opponent:
                    // A new opponent means a full re-acquisition, so run the whole boot tell.
                    _screen?.PlaySweep(0.5f);
                    _screen?.PlayFlicker(0.4f);
                    _threats?.ClearAll(true);
                    _audio?.PlayRetarget();
                    if (sentOut.Creature != null) BindTarget(sentOut.Creature.SpeciesId, _lastReadout);
                    break;

                case StatusChangedEvent status when status.Current != StatusCondition.None:
                    // Status is the classic big-swing cause; light the glass in its colour.
                    _screen?.PlayAlert(UiPalette.Status(status.Current), 0.2f, 0.55f);
                    break;

                case CreatureFaintedEvent fainted when fainted.Side == BattleSide.Opponent:
                    _threats?.ClearAll(true);
                    break;

                case BattleEndedEvent _:
                    _threats?.ClearAll(true);
                    _switches?.ClearAll();
                    break;

                case WeatherChangedEvent _:
                    _screen?.PlaySweep(0.55f);
                    break;
            }
        }

        // -------------------------------------------------------------------- build

        /// <summary>
        /// Assembles the whole device at runtime.
        ///
        /// The integrator can instead author a prefab and assign the serialized fields; this
        /// path exists so the scanner is verifiable today, against the fake oracle, without
        /// an editor session. Layout is entirely layout-group driven, so the same hierarchy
        /// resolves at 16:9 and 21:9 with no overlap.
        /// </summary>
        public void BuildRuntime()
        {
            // Idempotent: AddComponent runs Awake synchronously, so a caller that adds the
            // component and then calls BuildRuntime would otherwise build the whole device
            // twice, one hierarchy invisibly stacked on the other.
            if (_built) return;
            _built = true;

            var rootRect = transform as RectTransform;
            if (rootRect == null)
            {
                Debug.LogError("PokeLabScannerView must live on a RectTransform under a Canvas.");
                return;
            }

            _rootRect = rootRect;
            _rootGroup = UiBuilder.Group(this, 0f, false, false);

            _screen = ScannerScreen.Build(rootRect, true, 20);
            var content = _screen.ContentRoot;
            UiBuilder.Vertical(content, 12f, new RectOffset(0, 0, 0, 0));

            BuildHeader(content);
            BuildTargetBlock(content);

            _gauge = ProbabilityGauge.Build(content);

            _typeSummary = UiBuilder.Text("TypeSummary", content, string.Empty, UiTextRole.Secondary,
                UiPalette.TextSecondary);
            UiBuilder.Size(_typeSummary.rectTransform, flexibleWidth: 1f);

            _matchupChipRow = UiBuilder.Rect("MatchupChips", content);
            UiBuilder.Horizontal(_matchupChipRow, 8f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(_matchupChipRow, preferredHeight: 40f, minHeight: 40f, flexibleWidth: 1f);

            BuildRecommendationBox(content);

            // Everything below the fold scrolls: on a 16:9 docked panel the evidence, threat
            // and switch lists together exceed the glass, and clipping them would hide the
            // switch recommendations exactly when a player most needs them.
            var scroll = UiBuilder.ScrollList("Sections", content, out var sections, 14f,
                new RectOffset(0, 6, 2, 8));
            UiBuilder.Size(scroll.GetComponent<RectTransform>(), flexibleHeight: 1f, minHeight: 180f, flexibleWidth: 1f);

            _threats = ThreatPanel.Build(sections, 4);
            _switches = SwitchPanel.Build(sections, 3, true);
            _evidence = EvidencePanel.Build(sections, 6);
            BuildHeadToHead(sections);
            BuildWarningRow(sections);

            BuildFooter(content);

            if (_switches != null) _switches.SwitchRequested += index => SwitchRequested?.Invoke(index);
            if (_audio == null) _audio = GetComponent<ScannerAudio>();
            if (_audio == null) _audio = gameObject.AddComponent<ScannerAudio>();
        }

        private void BuildHeader(Transform parent)
        {
            var header = UiBuilder.Rect("Header", parent);
            UiBuilder.Horizontal(header, 8f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(header, preferredHeight: 22f, minHeight: 22f, flexibleWidth: 1f);

            _deviceLabel = UiBuilder.Text("Device", header, "POKÉ LAB · TACTICAL", UiTextRole.Overline,
                UiPalette.ScannerCyan.WithAlpha(0.65f));
            _deviceLabel.textWrappingMode = TextWrappingModes.NoWrap;

            UiBuilder.Spacer(header);
            _confidence = ConfidenceBadge.Build(header, 22f);
        }

        private void BuildTargetBlock(Transform parent)
        {
            var block = UiBuilder.Rect("Target", parent);
            UiBuilder.Horizontal(block, 12f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(block, preferredHeight: 62f, minHeight: 62f, flexibleWidth: 1f);

            var portraitHolder = UiBuilder.Rect("PortraitHolder", block, false);
            UiBuilder.Size(portraitHolder, preferredWidth: 58f, preferredHeight: 58f, minWidth: 58f, minHeight: 58f);
            var portraitBg = UiBuilder.Image("Bg", portraitHolder, UiSprites.Panel(12),
                UiPalette.ScannerCyan.WithAlpha(0.06f));
            UiBuilder.Stretch(portraitBg.rectTransform);
            var portraitRim = UiBuilder.Image("Rim", portraitHolder, UiSprites.Frame(12, 1),
                UiPalette.ScannerCyan.WithAlpha(0.28f));
            UiBuilder.Stretch(portraitRim.rectTransform);
            _targetPortrait = UiBuilder.Image("Portrait", portraitHolder, null, Color.white, Image.Type.Simple);
            _targetPortrait.preserveAspect = true;
            UiBuilder.Stretch(_targetPortrait.rectTransform, 8f);

            var stack = UiBuilder.Rect("Stack", block);
            UiBuilder.Vertical(stack, 3f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(stack, flexibleWidth: 1f);

            _targetName = UiBuilder.Text("Name", stack, "No target", UiTextRole.Heading, UiPalette.TextPrimary);
            _targetName.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(_targetName.rectTransform, preferredHeight: 26f, flexibleWidth: 1f);

            var row = UiBuilder.Rect("Row", stack);
            UiBuilder.Horizontal(row, 6f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(row, preferredHeight: 24f, minHeight: 24f, flexibleWidth: 1f);
            _typeBadgeRow = row;

            _targetMeta = UiBuilder.Text("Meta", stack, string.Empty, UiTextRole.Caption, UiPalette.TextMuted);
            _targetMeta.characterSpacing = 0.5f;
            UiBuilder.Size(_targetMeta.rectTransform, preferredHeight: 14f, flexibleWidth: 1f);
        }

        private void BuildRecommendationBox(Transform parent)
        {
            // Layout group on the box itself; the fill and the accent bar are excluded from
            // layout so the box's height tracks the recommendation text, which is a sentence
            // of unpredictable length and must be allowed to wrap to two lines.
            var box = UiBuilder.Rect("Recommendation", parent);
            UiBuilder.Vertical(box, 2f, new RectOffset(20, 12, 8, 8), TextAnchor.MiddleLeft);
            UiBuilder.Size(box, minHeight: 44f, flexibleWidth: 1f);
            _recommendationBox = box;

            UiBuilder.Backdrop("Bg", box, UiSprites.Panel(10), UiPalette.ScannerAmber.WithAlpha(0.10f));

            var accent = UiBuilder.Image("Accent", box, UiSprites.Pill(6), UiPalette.ScannerAmber);
            UiBuilder.Anchor(accent.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), new Vector2(9f, 0f), new Vector2(3f, -16f));
            UiBuilder.IgnoreLayout(accent.rectTransform);

            var caption = UiBuilder.Text("Caption", box, "Recommended line", UiTextRole.Overline,
                UiPalette.ScannerAmber.WithAlpha(0.8f));
            caption.fontSize = 9f;
            UiBuilder.Size(caption.rectTransform, preferredHeight: 12f, flexibleWidth: 1f);

            _recommendationText = UiBuilder.Text("Text", box, string.Empty, UiTextRole.Body, UiPalette.TextPrimary);
            _recommendationText.fontSize = 16f;
            UiBuilder.Size(_recommendationText.rectTransform, flexibleWidth: 1f);

            box.gameObject.SetActive(false);
        }

        private void BuildHeadToHead(Transform parent)
        {
            _headToHead = UiBuilder.Text("HeadToHead", parent, string.Empty, UiTextRole.Secondary, UiPalette.TextMuted);
            _headToHead.fontSize = 13f;
            UiBuilder.Size(_headToHead.rectTransform, flexibleWidth: 1f);
        }

        private void BuildWarningRow(Transform parent)
        {
            var row = UiBuilder.Rect("Warnings", parent);
            UiBuilder.Horizontal(row, 8f, new RectOffset(10, 10, 7, 7), TextAnchor.UpperLeft);
            UiBuilder.Size(row, minHeight: 32f, flexibleWidth: 1f);
            _warningRow = row;

            UiBuilder.Backdrop("Bg", row, UiSprites.Panel(9), UiPalette.Caution.WithAlpha(0.10f));

            var icon = UiBuilder.Image("Icon", row, UiSprites.WarningGlyph(24), UiPalette.Caution, Image.Type.Simple);
            icon.preserveAspect = true;
            UiBuilder.Size(icon.rectTransform, preferredWidth: 14f, preferredHeight: 14f,
                minWidth: 14f, minHeight: 14f);

            _warningText = UiBuilder.Text("Text", row, string.Empty, UiTextRole.Caption, UiPalette.Caution);
            _warningText.characterSpacing = 0f;
            _warningText.fontSize = 12f;
            UiBuilder.Size(_warningText.rectTransform, flexibleWidth: 1f);

            row.gameObject.SetActive(false);
        }

        private void BuildFooter(Transform parent)
        {
            var footer = UiBuilder.Rect("Footer", parent);
            UiBuilder.Horizontal(footer, 7f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(footer, preferredHeight: 16f, minHeight: 16f, flexibleWidth: 1f);

            _liveDot = UiBuilder.Image("LiveDot", footer, UiSprites.Dot(12), UiPalette.ScannerCyan, Image.Type.Simple);
            UiBuilder.Size(_liveDot.rectTransform, preferredWidth: 7f, preferredHeight: 7f, minWidth: 7f, minHeight: 7f);

            _footerText = UiBuilder.Text("Status", footer, "LIVE · BATTLE LINK", UiTextRole.Overline,
                UiPalette.TextMuted.WithAlpha(0.7f));
            _footerText.fontSize = 9f;
            UiBuilder.Size(_footerText.rectTransform, flexibleWidth: 1f);
        }
    }
}
