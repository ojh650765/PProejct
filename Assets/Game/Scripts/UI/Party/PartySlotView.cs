using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// One party member in the list, draggable to reorder.
    ///
    /// Reordering is done by lifting the row out of the layout group onto a drag layer and
    /// letting the remaining rows close the gap. Trying to reorder inside a
    /// <see cref="VerticalLayoutGroup"/> without lifting produces the classic bug where the
    /// dragged row fights the layout for its own position and stutters; taking it out of the
    /// flow entirely and using a placeholder is the only version that feels solid.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PartySlotView : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        [Header("Parts")]
        [SerializeField] private Image _panel;
        [SerializeField] private Image _portrait;
        [SerializeField] private Image _portraitFallback;
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _level;
        [SerializeField] private AnimatedBar _healthBar;
        [SerializeField] private TextMeshProUGUI _healthText;
        [SerializeField] private StatusBadge _status;
        [SerializeField] private RectTransform _typeRow;
        [SerializeField] private Image _dragHandle;
        [SerializeField] private Image _selectionRing;
        [SerializeField] private CanvasGroup _group;

        private readonly TypeBadge[] _typeBadges = new TypeBadge[2];
        private RectTransform _rect;
        private RectTransform _dragLayer;
        private RectTransform _placeholder;
        private Transform _listParent;
        private int _index = -1;
        private bool _dragging;
        private bool _fainted;

        /// <summary>Party index this row currently represents.</summary>
        public int Index => _index;

        /// <summary>The creature displayed, or null for an empty slot.</summary>
        public CreatureInstance Creature { get; private set; }

        /// <summary>Raised when the row is clicked, with its index.</summary>
        public Action<int> Selected;

        /// <summary>Raised when a drag finishes, with the from and to indices.</summary>
        public Action<int, int> Reordered;

        private void Awake() => _rect = transform as RectTransform;

        /// <summary>Binds a party member. Pass null to render an empty slot.</summary>
        public void Bind(int index, CreatureInstance creature, bool immediate = false)
        {
            _index = index;
            Creature = creature;
            _fainted = creature != null && creature.IsFainted;

            if (creature == null)
            {
                if (_name != null) _name.SetText("—");
                if (_level != null) _level.SetText(string.Empty);
                if (_healthText != null) _healthText.SetText(string.Empty);
                _status?.Bind(StatusCondition.None, true);
                if (_group != null) _group.alpha = 0.35f;
                if (_portrait != null) _portrait.enabled = false;
                if (_portraitFallback != null) _portraitFallback.enabled = false;
                return;
            }

            if (_group != null) _group.alpha = _fainted ? 0.55f : 1f;
            if (_name != null) _name.SetText(UiServices.NameOf(creature));
            if (_level != null) _level.SetText("Lv " + creature.Level);

            var fraction = creature.HpFraction;
            if (_healthBar != null)
            {
                if (immediate) { _healthBar.SetImmediate(fraction); _healthBar.SetColorImmediate(UiPalette.Health(fraction)); }
                else { _healthBar.SetValue(fraction); _healthBar.SetColor(UiPalette.Health(fraction)); }
            }
            if (_healthText != null)
            {
                _healthText.SetText($"{creature.CurrentHp} / {creature.MaxHp}");
                _healthText.color = UiPalette.Health(fraction);
            }

            _status?.Bind(creature.IsFainted ? StatusCondition.Fainted : creature.Status, immediate);

            UiServices.TypesOf(creature.SpeciesId, out var primary, out var secondary);
            EnsureTypeBadges();
            _typeBadges[0]?.Bind(primary);
            _typeBadges[1]?.Bind(secondary);

            var portrait = UiServices.PortraitOf(creature.SpeciesId);
            if (_portrait != null)
            {
                _portrait.sprite = portrait;
                _portrait.enabled = portrait != null;
            }
            if (_portraitFallback != null)
            {
                _portraitFallback.enabled = portrait == null;
                _portraitFallback.sprite = UiTypeIcons.Get(primary, 64);
                _portraitFallback.color = UiPalette.Type(primary).WithAlpha(0.7f);
            }

            // The lead slot is visually promoted — party order is a real decision and the
            // first slot is the one that actually matters.
            if (_panel != null)
            {
                _panel.color = index == 0
                    ? Color.Lerp(UiPalette.SurfaceRaised, UiPalette.ScannerCyan, 0.10f)
                    : UiPalette.SurfaceRaised;
            }
        }

        /// <summary>Shows or hides the selection ring.</summary>
        public void SetSelected(bool selected)
        {
            if (_selectionRing == null) return;
            var target = selected ? UiPalette.ScannerCyan : Color.clear;
            UiTween.Color(_selectionRing.color, target, 0.18f,
                c => { if (_selectionRing != null) _selectionRing.color = c; });
        }

        /// <summary>Supplies the layer drags are lifted onto. Set by the owning screen.</summary>
        public void SetDragLayer(RectTransform dragLayer) => _dragLayer = dragLayer;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_dragging) return;
            Selected?.Invoke(_index);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Creature == null || _dragLayer == null || _rect == null) return;
            _dragging = true;
            _listParent = _rect.parent;

            // A zero-graphic placeholder holds the row's slot so the list does not collapse.
            _placeholder = UiBuilder.Rect("DragPlaceholder", _listParent, false);
            _placeholder.SetSiblingIndex(_rect.GetSiblingIndex());
            UiBuilder.Size(_placeholder, preferredHeight: _rect.rect.height, minHeight: _rect.rect.height,
                flexibleWidth: 1f);

            _rect.SetParent(_dragLayer, true);
            if (_group != null) _group.blocksRaycasts = false;
            UiTween.Scale(_rect, Vector3.one * 1.03f, 0.14f, Ease.OutCubic);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragging || _rect == null) return;
            _rect.position = eventData.position;
            UpdatePlaceholderPosition();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_dragging || _rect == null) return;
            _dragging = false;

            var target = _placeholder != null ? _placeholder.GetSiblingIndex() : _index;
            _rect.SetParent(_listParent, true);
            _rect.SetSiblingIndex(target);
            if (_group != null) _group.blocksRaycasts = true;
            UiTween.Scale(_rect, Vector3.one, 0.18f, Ease.OutCubic);

            if (_placeholder != null)
            {
                Destroy(_placeholder.gameObject);
                _placeholder = null;
            }

            if (target != _index) Reordered?.Invoke(_index, target);
        }

        /// <summary>
        /// Walks the siblings and moves the placeholder to wherever the dragged row's centre
        /// currently sits. Comparing world-space y is stable regardless of canvas scale.
        /// </summary>
        private void UpdatePlaceholderPosition()
        {
            if (_placeholder == null || _listParent == null) return;

            var newIndex = _placeholder.GetSiblingIndex();
            for (var i = 0; i < _listParent.childCount; i++)
            {
                var sibling = _listParent.GetChild(i) as RectTransform;
                if (sibling == null || sibling == _placeholder) continue;
                if (_rect.position.y > sibling.position.y)
                {
                    newIndex = i;
                    break;
                }
                newIndex = i;
            }

            if (newIndex != _placeholder.GetSiblingIndex()) _placeholder.SetSiblingIndex(newIndex);
        }

        private void EnsureTypeBadges()
        {
            if (_typeRow == null) return;
            for (var i = 0; i < 2; i++)
            {
                if (_typeBadges[i] == null) _typeBadges[i] = TypeBadge.BuildCompact(_typeRow, ElementType.None, 18f);
            }
        }

        /// <summary>Builds a party row.</summary>
        public static PartySlotView Build(Transform parent)
        {
            var root = UiBuilder.Rect("PartySlot", parent);
            UiBuilder.Size(root, preferredHeight: 76f, minHeight: 76f, flexibleWidth: 1f);

            var view = root.gameObject.AddComponent<PartySlotView>();
            view._group = UiBuilder.Group(root);

            view._panel = UiBuilder.Image("Panel", root, UiSprites.Panel(12), UiPalette.SurfaceRaised,
                Image.Type.Sliced, true);
            UiBuilder.Stretch(view._panel.rectTransform);

            view._selectionRing = UiBuilder.Image("Ring", root, UiSprites.Frame(12, 2), Color.clear);
            UiBuilder.Stretch(view._selectionRing.rectTransform);

            var content = UiBuilder.Rect("Content", root);
            UiBuilder.Horizontal(content, 10f, new RectOffset(10, 14, 8, 8), TextAnchor.MiddleLeft);

            // Drag handle: a grip mark, so the affordance is discoverable without a tooltip.
            view._dragHandle = UiBuilder.Image("Handle", content, UiSprites.Solid(),
                UiPalette.TextMuted.WithAlpha(0.3f), Image.Type.Simple);
            UiBuilder.Size(view._dragHandle.rectTransform, preferredWidth: 3f, minWidth: 3f, minHeight: 30f);

            var portraitHolder = UiBuilder.Rect("Portrait", content, false);
            UiBuilder.Size(portraitHolder, preferredWidth: 56f, preferredHeight: 56f, minWidth: 56f, minHeight: 56f);
            var portraitBg = UiBuilder.Image("Bg", portraitHolder, UiSprites.Panel(11), UiPalette.SurfaceSunken);
            UiBuilder.Stretch(portraitBg.rectTransform);
            view._portrait = UiBuilder.Image("Image", portraitHolder, null, Color.white, Image.Type.Simple);
            view._portrait.preserveAspect = true;
            UiBuilder.Stretch(view._portrait.rectTransform, 3f);
            view._portraitFallback = UiBuilder.Image("Fallback", portraitHolder, null, UiPalette.TextMuted,
                Image.Type.Simple);
            view._portraitFallback.preserveAspect = true;
            UiBuilder.Stretch(view._portraitFallback.rectTransform, 12f);

            var stack = UiBuilder.Rect("Stack", content);
            UiBuilder.Vertical(stack, 4f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(stack, flexibleWidth: 1f);

            var nameRow = UiBuilder.Rect("NameRow", stack);
            UiBuilder.Horizontal(nameRow, 6f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(nameRow, preferredHeight: 22f, minHeight: 22f, flexibleWidth: 1f);

            view._name = UiBuilder.Text("Name", nameRow, "—", UiTextRole.Body, UiPalette.TextPrimary);
            view._name.fontStyle = FontStyles.Bold;
            view._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(view._name.rectTransform, flexibleWidth: 1f, minWidth: 70f);

            view._status = StatusBadge.Build(nameRow, 17f);

            var typeRow = UiBuilder.Rect("Types", nameRow, false);
            UiBuilder.Horizontal(typeRow, 3f, null, TextAnchor.MiddleRight);
            UiBuilder.Size(typeRow, preferredWidth: 40f, minWidth: 40f);
            view._typeRow = typeRow;

            view._level = UiBuilder.Text("Level", nameRow, string.Empty, UiTextRole.Numeric,
                UiPalette.TextSecondary, TextAlignmentOptions.Right);
            view._level.fontSize = 14f;
            UiBuilder.Size(view._level.rectTransform, preferredWidth: 46f, minWidth: 46f);

            var healthRow = UiBuilder.Rect("HealthRow", stack);
            UiBuilder.Horizontal(healthRow, 8f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(healthRow, preferredHeight: 14f, minHeight: 14f, flexibleWidth: 1f);

            view._healthBar = AnimatedBar.Build("HealthBar", healthRow, 8f, UiPalette.Health(1f), true);

            view._healthText = UiBuilder.Text("HealthText", healthRow, string.Empty, UiTextRole.Caption,
                UiPalette.TextSecondary, TextAlignmentOptions.Right);
            view._healthText.fontSize = 12f;
            view._healthText.characterSpacing = 0f;
            UiBuilder.Size(view._healthText.rectTransform, preferredWidth: 76f, minWidth: 76f);

            UiButtonMotion.Attach(root, 12);
            return view;
        }
    }
}
