using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// A read-only move row for detail screens: type glyph, name, category, power, PP.
    ///
    /// Separate from <see cref="MoveButtonView"/> on purpose. That one is a decision surface
    /// carrying a live damage forecast; this one is a reference line in a list. Sharing a
    /// component between the two would mean every detail screen dragging a forecast pipeline
    /// it has no use for.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MoveListRow : MonoBehaviour
    {
        [SerializeField] private TypeBadge _typeBadge;
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _category;
        [SerializeField] private TextMeshProUGUI _power;
        [SerializeField] private TextMeshProUGUI _pp;

        /// <summary>Binds a move slot, degrading to dashes when the move registry is absent.</summary>
        public void Bind(MoveSlot slot)
        {
            var move = UiServices.MoveOf(slot.MoveId);

            _typeBadge?.Bind(move?.Type ?? ElementType.None);
            if (_name != null) _name.SetText(UiServices.MoveName(slot.MoveId));

            if (_category != null)
            {
                _category.SetText(move != null ? move.Category.ToString().ToUpperInvariant() : "—");
                _category.color = UiPalette.Category(move?.Category ?? MoveCategory.Status);
            }
            if (_power != null)
            {
                _power.SetText(move != null && move.Power > 0 ? move.Power.ToString() : "—");
            }
            if (_pp != null)
            {
                _pp.SetText($"{slot.CurrentPp}/{slot.MaxPp}");
                _pp.color = slot.CurrentPp == 0 ? UiPalette.Negative : UiPalette.TextSecondary;
            }
        }

        /// <summary>Builds a row.</summary>
        public static MoveListRow Build(Transform parent)
        {
            var root = UiBuilder.Rect("MoveRow", parent);
            UiBuilder.Size(root, preferredHeight: 30f, minHeight: 30f, flexibleWidth: 1f);
            var row = root.gameObject.AddComponent<MoveListRow>();

            var bg = UiBuilder.Image("Bg", root, UiSprites.Panel(8), UiPalette.SurfaceSunken.WithAlpha(0.6f));
            UiBuilder.Stretch(bg.rectTransform);

            var inner = UiBuilder.Rect("Inner", root);
            UiBuilder.Horizontal(inner, 8f, new RectOffset(8, 10, 0, 0), TextAnchor.MiddleLeft);

            row._typeBadge = TypeBadge.BuildCompact(inner, ElementType.None, 18f);

            row._name = UiBuilder.Text("Name", inner, "—", UiTextRole.Secondary, UiPalette.TextPrimary);
            row._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(row._name.rectTransform, flexibleWidth: 1f, minWidth: 70f);

            row._category = UiBuilder.Text("Category", inner, "—", UiTextRole.Overline, UiPalette.TextMuted);
            row._category.fontSize = 9f;
            row._category.characterSpacing = 1.5f;
            UiBuilder.Size(row._category.rectTransform, preferredWidth: 62f, minWidth: 62f);

            row._power = UiBuilder.Text("Power", inner, "—", UiTextRole.Numeric, UiPalette.TextSecondary,
                TextAlignmentOptions.Right);
            row._power.fontSize = 13f;
            UiBuilder.Size(row._power.rectTransform, preferredWidth: 32f, minWidth: 32f);

            row._pp = UiBuilder.Text("Pp", inner, "—", UiTextRole.Caption, UiPalette.TextSecondary,
                TextAlignmentOptions.Right);
            row._pp.fontSize = 12f;
            row._pp.characterSpacing = 0f;
            UiBuilder.Size(row._pp.rectTransform, preferredWidth: 44f, minWidth: 44f);

            return row;
        }
    }
}
