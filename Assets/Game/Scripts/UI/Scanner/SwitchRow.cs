using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// One ranked switch recommendation: who to bring in, the projected win probability
    /// after the switch, the gain in percentage points, and why.
    ///
    /// The gain is the number that justifies the recommendation, so it is coloured and sized
    /// as the decision cue; the projected probability sits beside it as context. When a
    /// recommendation moves up or down the ranking between readouts the row shows a movement
    /// chevron for a couple of seconds — that is how the list communicates "re-ranked"
    /// without fighting the layout group for animated reordering.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SwitchRow : MonoBehaviour
    {
        [SerializeField] private Image _background;
        [SerializeField] private TextMeshProUGUI _rankLabel;
        [SerializeField] private Image _movementArrow;
        [SerializeField] private Image _portrait;
        [SerializeField] private Image _portraitFallback;
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _rationale;
        [SerializeField] private AnimatedNumber _projected;
        [SerializeField] private TextMeshProUGUI _gain;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _rect;

        private TweenHandle _movementTween;
        private bool _bound;

        /// <summary>Instance id of the recommended creature, used for rank diffing.</summary>
        public string InstanceId { get; private set; }

        /// <summary>Party index, so a click can be turned into a <see cref="BattleAction"/>.</summary>
        public int PartyIndex { get; private set; } = -1;

        /// <summary>Raised when the player activates the row. Null when the panel is read-only.</summary>
        public System.Action<int> Chosen;

        /// <summary>
        /// Binds a recommendation. <paramref name="previousRank"/> is the row's rank in the
        /// last readout, or -1 when it was not listed.
        /// </summary>
        public void Bind(SwitchRecommendation recommendation, int rank, int previousRank, bool animate)
        {
            InstanceId = recommendation.InstanceId;
            PartyIndex = recommendation.PartyIndex;

            var creature = UiServices.PartyMember(recommendation.InstanceId, recommendation.PartyIndex);
            var displayName = creature != null
                ? UiServices.NameOf(creature)
                : (recommendation.PartyIndex >= 0 ? "Party slot " + (recommendation.PartyIndex + 1) : "Unknown");

            if (_rankLabel != null) _rankLabel.SetText((rank + 1).ToString());
            if (_name != null) _name.SetText(displayName);
            if (_rationale != null)
            {
                var hasRationale = !string.IsNullOrWhiteSpace(recommendation.Rationale);
                _rationale.gameObject.SetActive(hasRationale);
                if (hasRationale) _rationale.SetText(recommendation.Rationale);
            }

            // Rank 1 is visually promoted: the top recommendation should be obvious before
            // the player compares any numbers.
            var isTop = rank == 0;
            if (_background != null)
            {
                _background.color = isTop
                    ? Color.Lerp(UiPalette.SurfaceRaised, UiPalette.ScannerCyan, 0.14f)
                    : UiPalette.SurfaceRaised.WithAlpha(0.7f);
            }
            if (_rankLabel != null) _rankLabel.color = isTop ? UiPalette.ScannerCyan : UiPalette.TextMuted;

            var gainColor = UiPalette.Delta(recommendation.GainPoints);
            if (_gain != null)
            {
                _gain.SetText(UiType.SignedPoints(recommendation.GainPoints, false));
                _gain.color = gainColor;
            }

            if (_projected != null)
            {
                if (_bound && animate) _projected.SetValue(recommendation.ProjectedWinProbability);
                else _projected.SetImmediate(recommendation.ProjectedWinProbability);
                _projected.SetColor(UiPalette.Probability(recommendation.ProjectedWinProbability), 0.3f);
            }

            BindPortrait(creature);
            BindMovement(rank, previousRank, animate);

            gameObject.SetActive(true);
            if (_group != null)
            {
                if (animate && !_bound)
                {
                    _group.alpha = 0f;
                    UiTween.Fade(_group, 1f, 0.26f, Ease.OutCubic, rank * 0.05f);
                    if (_rect != null)
                    {
                        var target = _rect.anchoredPosition;
                        _rect.anchoredPosition = target + new Vector2(0f, -12f);
                        UiTween.AnchoredMove(_rect, target, 0.32f, Ease.OutCubic, rank * 0.05f);
                    }
                }
                else
                {
                    _group.alpha = 1f;
                }
            }

            _bound = true;
        }

        private void BindPortrait(CreatureInstance creature)
        {
            var portrait = creature != null ? UiServices.PortraitOf(creature.SpeciesId) : null;
            if (_portrait != null)
            {
                _portrait.sprite = portrait;
                _portrait.enabled = portrait != null;
            }
            if (_portraitFallback != null)
            {
                // No art yet: show the creature's primary type glyph rather than an empty
                // square, so the row still carries useful information during integration.
                var showFallback = portrait == null;
                _portraitFallback.enabled = showFallback;
                if (showFallback && creature != null)
                {
                    UiServices.TypesOf(creature.SpeciesId, out var primary, out _);
                    _portraitFallback.sprite = UiTypeIcons.Get(primary, 48);
                    _portraitFallback.color = UiPalette.Type(primary).WithAlpha(0.75f);
                }
            }
        }

        private void BindMovement(int rank, int previousRank, bool animate)
        {
            if (_movementArrow == null) return;

            var moved = animate && previousRank >= 0 && previousRank != rank;
            if (!moved)
            {
                _movementArrow.color = Color.clear;
                return;
            }

            var rose = rank < previousRank;
            _movementArrow.rectTransform.localEulerAngles = new Vector3(0f, 0f, rose ? 0f : 180f);
            var color = rose ? UiPalette.Positive : UiPalette.Caution;

            UiTween.Kill(ref _movementTween);
            _movementTween = UiTween.Run(2.2f, t =>
            {
                if (_movementArrow == null) return;
                // Hold for the first two thirds, then fade — long enough to be noticed, short
                // enough that it does not become permanent chrome.
                var alpha = t < 0.65f ? 1f : 1f - (t - 0.65f) / 0.35f;
                _movementArrow.color = color.WithAlpha(alpha);
            }, Ease.Linear, 0f, true, () => { if (_movementArrow != null) _movementArrow.color = Color.clear; });

            UiTween.Punch(transform, 0.06f, 0.3f);
        }

        /// <summary>
        /// Emits the choice, against the party as it stands rather than as the readout that
        /// drew this row found it.
        ///
        /// A party index is a position, not an identity, and the panel rebinds its rows on
        /// every recalculation — so a click landing on a row built from an older readout could
        /// send out whoever has since moved into that slot. Re-resolving by instance id makes
        /// the row mean the creature it names, and a creature no longer in the party emits
        /// nothing at all rather than a switch to a stranger.
        /// </summary>
        public void Choose()
        {
            if (!_bound || !gameObject.activeInHierarchy) return;

            var party = UiServices.Profile?.Party;
            // No profile registered yet — during integration the bind is all there is to go on.
            if (party == null)
            {
                if (PartyIndex >= 0) Chosen?.Invoke(PartyIndex);
                return;
            }

            if (!string.IsNullOrEmpty(InstanceId))
            {
                for (var i = 0; i < party.Count; i++)
                {
                    if (party[i] != null && party[i].InstanceId == InstanceId)
                    {
                        Chosen?.Invoke(i);
                        return;
                    }
                }
                return;
            }

            if (PartyIndex >= 0 && PartyIndex < party.Count) Chosen?.Invoke(PartyIndex);
        }

        /// <summary>Hides the row for reuse.</summary>
        public void HideImmediate()
        {
            UiTween.Kill(ref _movementTween);
            gameObject.SetActive(false);
            _bound = false;
            InstanceId = null;
            PartyIndex = -1;
        }

        /// <summary>Builds a switch row. <paramref name="interactive"/> makes it clickable.</summary>
        public static SwitchRow Build(Transform parent, bool interactive)
        {
            // Layout group on the root with padding folded in; the background is excluded
            // from layout. See ThreatRow for why this shape rather than a padded child.
            var root = UiBuilder.Rect("SwitchRow", parent);
            UiBuilder.Horizontal(root, 10f, new RectOffset(10, 12, 7, 7), TextAnchor.MiddleLeft);
            UiBuilder.Size(root, minHeight: 52f, flexibleWidth: 1f);

            var row = root.gameObject.AddComponent<SwitchRow>();
            row._rect = root;
            row._group = UiBuilder.Group(root);

            row._background = UiBuilder.Backdrop("Bg", root, UiSprites.Panel(10),
                UiPalette.SurfaceRaised.WithAlpha(0.7f), interactive);

            // --- rank + movement chevron
            var rankStack = UiBuilder.Rect("Rank", root, false);
            UiBuilder.Size(rankStack, preferredWidth: 22f, minWidth: 22f, flexibleHeight: 1f);

            row._rankLabel = UiBuilder.Text("Number", rankStack, "1", UiTextRole.Numeric, UiPalette.ScannerCyan,
                TextAlignmentOptions.Center);
            row._rankLabel.fontSize = 17f;
            UiBuilder.Stretch(row._rankLabel.rectTransform);

            row._movementArrow = UiBuilder.Image("Movement", rankStack, UiSprites.Chevron(20, false, 4),
                Color.clear, Image.Type.Simple);
            row._movementArrow.preserveAspect = true;
            UiBuilder.Anchor(row._movementArrow.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, 2f), new Vector2(10f, 10f));

            // --- portrait
            var portraitHolder = UiBuilder.Rect("Portrait", root, false);
            UiBuilder.Size(portraitHolder, preferredWidth: 38f, preferredHeight: 38f, minWidth: 38f, minHeight: 38f);
            var portraitBg = UiBuilder.Image("Bg", portraitHolder, UiSprites.Panel(9), UiPalette.SurfaceSunken);
            UiBuilder.Stretch(portraitBg.rectTransform);

            row._portrait = UiBuilder.Image("Image", portraitHolder, null, Color.white, Image.Type.Simple);
            row._portrait.preserveAspect = true;
            UiBuilder.Stretch(row._portrait.rectTransform, 2f);
            row._portrait.enabled = false;

            row._portraitFallback = UiBuilder.Image("Fallback", portraitHolder, null, UiPalette.TextMuted, Image.Type.Simple);
            row._portraitFallback.preserveAspect = true;
            UiBuilder.Stretch(row._portraitFallback.rectTransform, 8f);

            // --- name + rationale
            var textStack = UiBuilder.Rect("Text", root);
            UiBuilder.Vertical(textStack, 1f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(textStack, flexibleWidth: 1f, minWidth: 100f);

            row._name = UiBuilder.Text("Name", textStack, string.Empty, UiTextRole.Body, UiPalette.TextPrimary);
            row._name.fontSize = 16f;
            row._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(row._name.rectTransform, preferredHeight: 19f, flexibleWidth: 1f);

            row._rationale = UiBuilder.Text("Rationale", textStack, string.Empty, UiTextRole.Caption, UiPalette.TextMuted);
            row._rationale.characterSpacing = 0f;
            row._rationale.fontSize = 12f;
            UiBuilder.Size(row._rationale.rectTransform, flexibleWidth: 1f);

            // --- projected probability + gain
            var numbers = UiBuilder.Rect("Numbers", root, false);
            UiBuilder.Vertical(numbers, 0f, null, TextAnchor.MiddleRight);
            UiBuilder.Size(numbers, preferredWidth: 62f, minWidth: 62f);

            var projectedLabel = UiBuilder.Text("Projected", numbers, "50%", UiTextRole.Numeric,
                UiPalette.ScannerCyan, TextAlignmentOptions.Right);
            projectedLabel.fontSize = 19f;
            UiBuilder.Size(projectedLabel.rectTransform, preferredHeight: 22f, flexibleWidth: 1f);
            row._projected = AnimatedNumber.Attach(projectedLabel, v => UiType.Percent(v), 0.5f);

            row._gain = UiBuilder.Text("Gain", numbers, "+0.0", UiTextRole.Caption, UiPalette.Positive,
                TextAlignmentOptions.Right);
            row._gain.fontSize = 12f;
            row._gain.characterSpacing = 0f;
            UiBuilder.Size(row._gain.rectTransform, preferredHeight: 14f, flexibleWidth: 1f);

            if (interactive)
            {
                UiButtonMotion.Attach(root, 10);
                UiBuilder.Button("Choose", root, row._background, row.Choose);
            }

            root.gameObject.SetActive(false);
            return row;
        }
    }
}
