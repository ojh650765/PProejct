using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The six-stat spread as a stack of labelled bars.
    ///
    /// Bars beat a hexagon here. A radar chart looks impressive and is genuinely bad at the
    /// job: it makes two similar creatures indistinguishable, its area misleads because it
    /// depends on axis order, and reading an exact value off it is impossible. Six aligned
    /// bars let the eye compare within a creature and across two screens, and the numbers sit
    /// right beside them.
    ///
    /// Bars are scaled against a fixed ceiling rather than the creature's own maximum, so a
    /// weak creature genuinely looks weak instead of looking balanced.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StatSpreadView : MonoBehaviour
    {
        [Tooltip("Value that fills a bar completely. 180 covers every base stat in the dataset with headroom.")]
        [SerializeField] private float _ceiling = 180f;
        [SerializeField] private AnimatedBar[] _bars = new AnimatedBar[StatKinds.BaseCount];
        [SerializeField] private AnimatedNumber[] _values = new AnimatedNumber[StatKinds.BaseCount];
        [SerializeField] private TextMeshProUGUI _totalLabel;

        /// <summary>Short label for a stat. Six characters maximum keeps the column narrow.</summary>
        public static string Abbreviation(StatKind stat) => stat switch
        {
            StatKind.Hp => "HP",
            StatKind.Attack => "ATK",
            StatKind.Defense => "DEF",
            StatKind.SpAttack => "SP.ATK",
            StatKind.SpDefense => "SP.DEF",
            StatKind.Speed => "SPD",
            StatKind.Accuracy => "ACC",
            _ => "EVA",
        };

        /// <summary>Binds six stat values, animating each bar with a small stagger.</summary>
        public void Bind(int[] stats, bool immediate = false)
        {
            if (stats == null) return;
            var total = 0;

            for (var i = 0; i < _bars.Length && i < StatKinds.BaseCount; i++)
            {
                var value = i < stats.Length ? stats[i] : 0;
                total += value;
                var fraction = Mathf.Clamp01(value / _ceiling);

                if (_bars[i] != null)
                {
                    if (immediate) _bars[i].SetImmediate(fraction);
                    else _bars[i].SetValue(fraction, 0.45f + i * 0.04f);
                    // Colour by strength so a standout stat is visible before any reading.
                    _bars[i].SetColorImmediate(StatColor(fraction));
                }

                if (_values[i] == null) continue;
                if (immediate) _values[i].SetImmediate(value);
                else _values[i].SetValue(value, 0.45f);
            }

            if (_totalLabel != null) _totalLabel.SetText("BST " + total);
        }

        /// <summary>Green for a standout, amber for average, muted for weak.</summary>
        private static Color StatColor(float fraction)
        {
            if (fraction >= 0.62f) return UiPalette.Positive;
            if (fraction >= 0.38f) return UiPalette.ScannerCyan;
            return UiPalette.TextMuted;
        }

        /// <summary>Builds the six-row spread.</summary>
        public static StatSpreadView Build(Transform parent, float ceiling = 180f)
        {
            var root = UiBuilder.Rect("StatSpread", parent);
            UiBuilder.Vertical(root, 5f);
            UiBuilder.Size(root, flexibleWidth: 1f);

            var view = root.gameObject.AddComponent<StatSpreadView>();
            view._ceiling = ceiling;

            for (var i = 0; i < StatKinds.BaseCount; i++)
            {
                var stat = (StatKind)i;
                var row = UiBuilder.Rect("Stat_" + stat, root);
                UiBuilder.Horizontal(row, 8f, null, TextAnchor.MiddleLeft);
                UiBuilder.Size(row, preferredHeight: 18f, minHeight: 18f, flexibleWidth: 1f);

                var label = UiBuilder.Text("Label", row, Abbreviation(stat), UiTextRole.Overline, UiPalette.TextMuted);
                label.fontSize = 10f;
                label.characterSpacing = 1.5f;
                UiBuilder.Size(label.rectTransform, preferredWidth: 54f, minWidth: 54f);

                view._bars[i] = AnimatedBar.Build("Bar", row, 8f, UiPalette.ScannerCyan);

                var value = UiBuilder.Text("Value", row, "0", UiTextRole.Numeric, UiPalette.TextPrimary,
                    TextAlignmentOptions.Right);
                value.fontSize = 14f;
                UiBuilder.Size(value.rectTransform, preferredWidth: 38f, minWidth: 38f);
                view._values[i] = AnimatedNumber.Attach(value, v => Mathf.RoundToInt(v).ToString(), 0.45f);
            }

            view._totalLabel = UiBuilder.Text("Total", root, "BST 0", UiTextRole.Caption, UiPalette.TextSecondary,
                TextAlignmentOptions.Right);
            UiBuilder.Size(view._totalLabel.rectTransform, preferredHeight: 14f, flexibleWidth: 1f);

            return view;
        }
    }
}
