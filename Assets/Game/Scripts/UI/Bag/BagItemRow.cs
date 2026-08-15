using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// One item line in the bag: an icon mark, the name, and the count held.
    ///
    /// The count is right-aligned and monospaced by tracking so a column of quantities reads
    /// as a column rather than a ragged edge — with no item art available the count is the
    /// only distinguishing mark down the list, so it has to be scannable.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BagItemRow : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private Image _panel;
        [SerializeField] private Image _icon;
        [SerializeField] private Image _selectionRing;
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _count;

        private float _lastClickTime = -1f;

        /// <summary>Item id this row shows.</summary>
        public string ItemId { get; private set; }

        /// <summary>Raised on a single click.</summary>
        public Action<string> Selected;

        /// <summary>Raised on a double click, which uses the item directly.</summary>
        public Action<string> Activated;

        /// <summary>Binds an item and its count.</summary>
        public void Bind(string itemId, int count)
        {
            ItemId = itemId;
            if (_name != null) _name.SetText(BagScreenView.DisplayName(itemId));
            if (_count != null)
            {
                _count.SetText("×" + count);
                // Low stock is worth flagging: running out of balls mid-encounter is the
                // single most annoying surprise in this genre.
                _count.color = count <= 1 ? UiPalette.Caution : UiPalette.TextSecondary;
            }
            if (_icon != null)
            {
                _icon.sprite = UiSprites.Dot(24);
                _icon.color = TintFor(itemId);
            }
        }

        /// <summary>Shows or hides the selection ring.</summary>
        public void SetSelected(bool selected)
        {
            if (_selectionRing == null) return;
            var target = selected ? UiPalette.ScannerCyan : Color.clear;
            UiTween.Color(_selectionRing.color, target, 0.15f,
                c => { if (_selectionRing != null) _selectionRing.color = c; });
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            var now = Time.unscaledTime;
            if (_lastClickTime > 0f && now - _lastClickTime < 0.35f)
            {
                _lastClickTime = -1f;
                Activated?.Invoke(ItemId);
                return;
            }
            _lastClickTime = now;
            Selected?.Invoke(ItemId);
        }

        /// <summary>A stable colour per item id, so the same item always looks the same.</summary>
        private static Color TintFor(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return UiPalette.TextMuted;
            var id = itemId.ToLowerInvariant();
            if (id.Contains("ball")) return UiPalette.Negative;
            if (id.Contains("potion") || id.Contains("heal")) return UiPalette.Positive;
            if (id.Contains("revive")) return UiPalette.ScannerAmber;
            if (id.Contains("antidote")) return UiPalette.Status(Core.StatusCondition.Poison);
            return UiPalette.Info;
        }

        /// <summary>Builds a row.</summary>
        public static BagItemRow Build(Transform parent)
        {
            var root = UiBuilder.Rect("BagItem", parent);
            UiBuilder.Size(root, preferredHeight: 46f, minHeight: 46f, flexibleWidth: 1f);
            var row = root.gameObject.AddComponent<BagItemRow>();

            row._panel = UiBuilder.Image("Bg", root, UiSprites.Panel(10), UiPalette.SurfaceRaised.WithAlpha(0.8f),
                Image.Type.Sliced, true);
            UiBuilder.Stretch(row._panel.rectTransform);

            row._selectionRing = UiBuilder.Image("Ring", root, UiSprites.Frame(10, 2), Color.clear);
            UiBuilder.Stretch(row._selectionRing.rectTransform);

            var inner = UiBuilder.Rect("Inner", root);
            UiBuilder.Horizontal(inner, 12f, new RectOffset(14, 16, 0, 0), TextAnchor.MiddleLeft);

            row._icon = UiBuilder.Image("Icon", inner, UiSprites.Dot(24), UiPalette.Info, Image.Type.Simple);
            row._icon.preserveAspect = true;
            UiBuilder.Size(row._icon.rectTransform, preferredWidth: 14f, preferredHeight: 14f,
                minWidth: 14f, minHeight: 14f);

            row._name = UiBuilder.Text("Name", inner, "—", UiTextRole.Body, UiPalette.TextPrimary);
            row._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(row._name.rectTransform, flexibleWidth: 1f);

            row._count = UiBuilder.Text("Count", inner, "×0", UiTextRole.Numeric, UiPalette.TextSecondary,
                TextAlignmentOptions.Right);
            row._count.fontSize = 16f;
            row._count.characterSpacing = 1f;
            UiBuilder.Size(row._count.rectTransform, preferredWidth: 62f, minWidth: 62f);

            UiButtonMotion.Attach(root, 10);
            return row;
        }
    }
}
