using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// One move in the selection grid, with its live <see cref="DamageForecast"/>.
    ///
    /// The forecast is the reason the Poké Lab exists inside the battle loop rather than
    /// beside it: the player picks a move while looking at what it will actually do. So the
    /// card carries four facts in a fixed hierarchy — name, then effectiveness, then expected
    /// damage, then turns to KO — and the same three numbers always occupy the same three
    /// positions on every card, which is what lets the player compare four moves at a glance
    /// instead of reading four sentences.
    ///
    /// A move with no forecast (the oracle has not landed, or the engine cannot simulate it)
    /// renders the card without the forecast strip rather than showing zeroes, because a
    /// confident "0% damage" would be a lie.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MoveButtonView : MonoBehaviour
    {
        [Header("Identity")]
        [SerializeField] private Image _panel;
        [SerializeField] private Image _typeStripe;
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _pp;
        [SerializeField] private TypeBadge _typeBadge;
        [SerializeField] private TextMeshProUGUI _category;
        [SerializeField] private Image _categoryChip;

        [Header("Forecast")]
        [SerializeField] private RectTransform _forecastRow;
        [SerializeField] private TextMeshProUGUI _effectiveness;
        [SerializeField] private AnimatedNumber _expected;
        [SerializeField] private TextMeshProUGUI _turnsToKo;
        [SerializeField] private TextMeshProUGUI _accuracy;
        [SerializeField] private AnimatedBar _expectedBar;

        [Header("State")]
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private Button _button;

        private int _index = -1;
        private bool _usable = true;
        private bool _hasForecast;

        /// <summary>Move slot index this card represents.</summary>
        public int Index => _index;

        /// <summary>True when the move can legally be selected.</summary>
        public bool IsUsable => _usable;

        /// <summary>Raised with the slot index when the player picks this move.</summary>
        public Action<int> Chosen;

        /// <summary>Binds a move slot. Pass a null forecast when none is available.</summary>
        public void Bind(int index, MoveSlot slot, DamageForecast? forecast, bool immediate = false)
        {
            _index = index;
            var move = UiServices.MoveOf(slot.MoveId);
            var type = move?.Type ?? ElementType.None;
            var category = move?.Category ?? MoveCategory.Physical;

            gameObject.SetActive(!string.IsNullOrEmpty(slot.MoveId));
            if (string.IsNullOrEmpty(slot.MoveId)) return;

            if (_name != null) _name.SetText(UiServices.MoveName(slot.MoveId));
            if (_pp != null)
            {
                _pp.SetText($"{slot.CurrentPp}<size=75%><color=#FFFFFF66>/{slot.MaxPp}</color></size>");
                // PP turns amber below a third and red at zero: the player should feel a move
                // running out before it actually does.
                var ratio = slot.MaxPp > 0 ? slot.CurrentPp / (float)slot.MaxPp : 0f;
                _pp.color = slot.CurrentPp == 0 ? UiPalette.Negative
                    : (ratio <= 0.34f ? UiPalette.Caution : UiPalette.TextSecondary);
            }

            _typeBadge?.Bind(type);
            if (_typeStripe != null) _typeStripe.color = UiPalette.Type(type);
            if (_panel != null) _panel.color = Color.Lerp(UiPalette.SurfaceRaised, UiPalette.Type(type), 0.10f);

            if (_category != null) _category.SetText(CategoryName(category));
            if (_categoryChip != null) _categoryChip.color = UiPalette.Category(category).WithAlpha(0.22f);
            if (_category != null) _category.color = UiPalette.Category(category);

            BindForecast(forecast, immediate);
            SetUsable(slot.Usable);
        }

        private void BindForecast(DamageForecast? forecast, bool immediate)
        {
            _hasForecast = forecast.HasValue;
            if (_forecastRow != null) _forecastRow.gameObject.SetActive(_hasForecast);
            if (!_hasForecast) return;

            var value = forecast.Value;

            if (_effectiveness != null)
            {
                _effectiveness.SetText(EffectivenessLabel(value.Effectiveness, value.TypeMultiplier));
                _effectiveness.color = UiPalette.Effectiveness(value.Effectiveness);
            }

            if (_expected != null)
            {
                _expected.WithFormat(v => UiType.Percent(v));
                if (immediate) _expected.SetImmediate(value.ExpectedFraction);
                else _expected.SetValue(value.ExpectedFraction);
                _expected.SetColor(value.ExpectedFraction >= 0.5f ? UiPalette.Positive : UiPalette.TextPrimary, 0.3f);
            }

            if (_expectedBar != null)
            {
                if (immediate) _expectedBar.SetImmediate(value.ExpectedFraction);
                else _expectedBar.SetValue(value.ExpectedFraction);
                _expectedBar.SetColorImmediate(UiPalette.Effectiveness(value.Effectiveness));
            }

            if (_turnsToKo != null)
            {
                var canKo = value.TurnsToKo > 0 && value.TurnsToKo < int.MaxValue;
                _turnsToKo.SetText(canKo
                    ? (value.TurnsToKo == 1 ? "KO this turn" : $"KO in {value.TurnsToKo}")
                    : "no KO");
                _turnsToKo.color = value.TurnsToKo == 1 ? UiPalette.Positive
                    : (canKo ? UiPalette.TextSecondary : UiPalette.TextMuted);
            }

            if (_accuracy != null)
            {
                // Only shown when it is not a certainty — "100%" on every card is noise.
                var uncertain = value.HitChance < 0.999f;
                _accuracy.gameObject.SetActive(uncertain);
                if (uncertain)
                {
                    _accuracy.SetText(UiType.Percent(value.HitChance) + " acc");
                    _accuracy.color = value.HitChance < 0.8f ? UiPalette.Caution : UiPalette.TextMuted;
                }
            }
        }

        /// <summary>Greys the card out when the move cannot be used (no PP, disabled, locked).</summary>
        public void SetUsable(bool usable)
        {
            _usable = usable;
            if (_group != null)
            {
                _group.alpha = usable ? 1f : 0.42f;
                _group.interactable = usable;
                _group.blocksRaycasts = usable;
            }
            if (_button != null) _button.interactable = usable;
        }

        /// <summary>Plays the confirm flourish before the caller closes the panel.</summary>
        public void PlayConfirm()
        {
            UiTween.Punch(transform, 0.06f, 0.22f);
        }

        private static string CategoryName(MoveCategory category) => category switch
        {
            MoveCategory.Physical => "PHYSICAL",
            MoveCategory.Special => "SPECIAL",
            _ => "STATUS",
        };

        private static string EffectivenessLabel(Effectiveness effectiveness, float multiplier) => effectiveness switch
        {
            Effectiveness.SuperEffective => UiType.Multiplier(multiplier) + " SUPER EFFECTIVE",
            Effectiveness.NotVeryEffective => UiType.Multiplier(multiplier) + " RESISTED",
            Effectiveness.Immune => "NO EFFECT",
            _ => "NEUTRAL",
        };

        /// <summary>Builds a move card sized for a two-column grid.</summary>
        public static MoveButtonView Build(Transform parent)
        {
            var root = UiBuilder.Rect("MoveButton", parent, false);
            var view = root.gameObject.AddComponent<MoveButtonView>();
            view._group = UiBuilder.Group(root);

            view._panel = UiBuilder.Image("Panel", root, UiSprites.Panel(12), UiPalette.SurfaceRaised, Image.Type.Sliced, true);
            UiBuilder.Stretch(view._panel.rectTransform);

            // Left type stripe: colour identity before a single word is read.
            view._typeStripe = UiBuilder.Image("Stripe", root, UiSprites.Pill(8), UiPalette.TextMuted);
            UiBuilder.Anchor(view._typeStripe.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(3f, -18f));

            var stack = UiBuilder.Rect("Stack", root);
            UiBuilder.Vertical(stack, 4f, new RectOffset(18, 12, 8, 8), TextAnchor.MiddleLeft);

            // --- title row
            var titleRow = UiBuilder.Rect("TitleRow", stack);
            UiBuilder.Horizontal(titleRow, 6f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(titleRow, preferredHeight: 22f, minHeight: 22f, flexibleWidth: 1f);

            view._name = UiBuilder.Text("Name", titleRow, "—", UiTextRole.Body, UiPalette.TextPrimary);
            view._name.fontStyle = FontStyles.Bold;
            view._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(view._name.rectTransform, flexibleWidth: 1f, minWidth: 60f);

            view._pp = UiBuilder.Text("Pp", titleRow, "0/0", UiTextRole.Numeric, UiPalette.TextSecondary,
                TextAlignmentOptions.Right);
            view._pp.fontSize = 14f;
            UiBuilder.Size(view._pp.rectTransform, preferredWidth: 54f, minWidth: 54f);

            // --- chips row
            var chipRow = UiBuilder.Rect("ChipRow", stack);
            UiBuilder.Horizontal(chipRow, 6f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(chipRow, preferredHeight: 22f, minHeight: 22f, flexibleWidth: 1f);

            view._typeBadge = TypeBadge.Build(chipRow, ElementType.None, 20f);

            var categoryHolder = UiBuilder.Rect("Category", chipRow, false);
            UiBuilder.Size(categoryHolder, preferredWidth: 74f, preferredHeight: 18f, minWidth: 74f, minHeight: 18f);
            view._categoryChip = UiBuilder.Image("Bg", categoryHolder, UiSprites.Pill(18),
                UiPalette.Category(MoveCategory.Physical).WithAlpha(0.22f));
            UiBuilder.Stretch(view._categoryChip.rectTransform);
            view._category = UiBuilder.Text("Label", categoryHolder, "PHYSICAL", UiTextRole.Overline,
                UiPalette.Category(MoveCategory.Physical), TextAlignmentOptions.Center);
            view._category.fontSize = 9f;
            view._category.characterSpacing = 2f;
            UiBuilder.Stretch(view._category.rectTransform);

            UiBuilder.Spacer(chipRow);

            view._accuracy = UiBuilder.Text("Accuracy", chipRow, string.Empty, UiTextRole.Caption, UiPalette.TextMuted,
                TextAlignmentOptions.Right);
            view._accuracy.fontSize = 11f;
            view._accuracy.characterSpacing = 0f;
            UiBuilder.Size(view._accuracy.rectTransform, preferredWidth: 62f, minWidth: 62f);

            // --- forecast strip
            // 22 (title) + 22 (chips) + 30 (forecast) + 2×4 spacing + 2×8 padding = 98,
            // which is why the grid cell that hosts this card is 100 tall. Changing any of
            // those numbers means changing the cell size in BattleCommandPanel too.
            var forecast = UiBuilder.Rect("Forecast", stack);
            UiBuilder.Vertical(forecast, 3f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(forecast, preferredHeight: 30f, minHeight: 30f, flexibleWidth: 1f);
            view._forecastRow = forecast;

            var forecastTop = UiBuilder.Rect("Top", forecast);
            UiBuilder.Horizontal(forecastTop, 8f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(forecastTop, preferredHeight: 16f, minHeight: 16f, flexibleWidth: 1f);

            view._effectiveness = UiBuilder.Text("Effectiveness", forecastTop, "NEUTRAL", UiTextRole.Overline,
                UiPalette.TextSecondary);
            view._effectiveness.fontSize = 9f;
            view._effectiveness.characterSpacing = 2.5f;
            view._effectiveness.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(view._effectiveness.rectTransform, flexibleWidth: 1f, minWidth: 90f);

            var expectedLabel = UiBuilder.Text("Expected", forecastTop, "0%", UiTextRole.Numeric,
                UiPalette.TextPrimary, TextAlignmentOptions.Right);
            expectedLabel.fontSize = 15f;
            UiBuilder.Size(expectedLabel.rectTransform, preferredWidth: 46f, minWidth: 46f);
            view._expected = AnimatedNumber.Attach(expectedLabel, v => UiType.Percent(v), 0.45f);

            view._turnsToKo = UiBuilder.Text("Ko", forecastTop, "no KO", UiTextRole.Caption, UiPalette.TextMuted,
                TextAlignmentOptions.Right);
            view._turnsToKo.fontSize = 11f;
            view._turnsToKo.characterSpacing = 0f;
            UiBuilder.Size(view._turnsToKo.rectTransform, preferredWidth: 72f, minWidth: 72f);

            view._expectedBar = AnimatedBar.Build("ExpectedBar", forecast, 4f, UiPalette.TextSecondary);
            view._expectedBar.SetImmediate(0f);

            UiButtonMotion.Attach(root, 12);
            view._button = UiBuilder.Button("Choose", root, view._panel, () =>
            {
                if (!view._usable) return;
                view.PlayConfirm();
                view.Chosen?.Invoke(view._index);
            });

            return view;
        }
    }
}
