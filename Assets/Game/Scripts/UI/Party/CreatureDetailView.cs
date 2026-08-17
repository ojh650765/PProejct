using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The full detail card for one creature: portrait, identity, types, ability, held item,
    /// stat spread and move list.
    ///
    /// Shared between the party screen and the dex, which is why it binds from a
    /// <see cref="CreatureInstance"/> or from a bare species id — the dex has no instance to
    /// show, only a species, and duplicating this layout for it would guarantee the two
    /// drift apart.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CreatureDetailView : MonoBehaviour
    {
        [Header("Identity")]
        [SerializeField] private Image _portrait;
        [SerializeField] private Image _portraitFallback;
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _speciesLine;
        [SerializeField] private RectTransform _typeRow;

        [Header("Detail")]
        [SerializeField] private TextMeshProUGUI _abilityValue;
        [SerializeField] private TextMeshProUGUI _itemValue;
        [SerializeField] private StatSpreadView _stats;
        [SerializeField] private RectTransform _moveList;
        [SerializeField] private TextMeshProUGUI _moveHeader;
        [SerializeField] private CanvasGroup _group;

        private readonly TypeBadge[] _typeBadges = new TypeBadge[2];
        private readonly List<MoveListRow> _moveRows = new List<MoveListRow>(4);
        private TweenHandle _visibilityFade;

        /// <summary>Binds a live creature — the party screen's path.</summary>
        public void Bind(CreatureInstance creature, bool immediate = false)
        {
            if (creature == null)
            {
                SetVisible(false);
                return;
            }
            SetVisible(true);

            BindIdentity(creature.SpeciesId, UiServices.NameOf(creature), $"Lv {creature.Level}");
            if (_stats != null) _stats.Bind(creature.Stats, immediate);

            if (_abilityValue != null)
            {
                _abilityValue.SetText(string.IsNullOrEmpty(creature.AbilityId)
                    ? "—"
                    : UiServices.Titleise(creature.AbilityId.Replace('_', ' ')));
            }
            if (_itemValue != null)
            {
                _itemValue.SetText(string.IsNullOrEmpty(creature.HeldItemId)
                    ? "None"
                    : UiServices.Titleise(creature.HeldItemId.Replace('_', ' ')));
                _itemValue.color = string.IsNullOrEmpty(creature.HeldItemId)
                    ? UiPalette.TextMuted
                    : UiPalette.TextPrimary;
            }

            BindMoves(creature.Moves);
        }

        /// <summary>Binds a species with no instance behind it — the dex's path.</summary>
        public void BindSpecies(int speciesId, bool caught, bool immediate = false)
        {
            SetVisible(true);

            var species = UiServices.Species;
            SpeciesData data = null;
            species?.TryGet(speciesId, out data);

            BindIdentity(speciesId, UiServices.SpeciesName(speciesId),
                data != null ? $"#{data.NationalDex:000}   {data.Height / 10f:0.0} m   {data.Weight / 10f:0.0} kg" : "—");

            if (_stats != null) _stats.Bind(UiServices.BaseStatsOf(speciesId), immediate);

            if (_abilityValue != null)
            {
                var abilities = data?.Abilities;
                _abilityValue.SetText(abilities != null && abilities.Length > 0
                    ? UiServices.Titleise(string.Join(", ", abilities).Replace('_', ' '))
                    : "—");
            }
            if (_itemValue != null)
            {
                // The dex has no held item, so the slot is repurposed for capture state
                // rather than left as a dangling empty field.
                _itemValue.SetText(caught ? "Caught" : "Seen only");
                _itemValue.color = caught ? UiPalette.Positive : UiPalette.TextMuted;
            }

            BindMoves(null);
            if (_moveHeader != null) _moveHeader.SetText(caught ? "Known moves" : "Moves unrecorded");
        }

        private void BindIdentity(int speciesId, string displayName, string subtitle)
        {
            if (_name != null) _name.SetText(displayName);
            if (_speciesLine != null) _speciesLine.SetText(subtitle);

            UiServices.TypesOf(speciesId, out var primary, out var secondary);
            EnsureTypeBadges();
            _typeBadges[0]?.Bind(primary);
            _typeBadges[1]?.Bind(secondary);

            var portrait = UiServices.PortraitOf(speciesId);
            if (_portrait != null)
            {
                _portrait.sprite = portrait;
                _portrait.enabled = portrait != null;
            }
            if (_portraitFallback != null)
            {
                _portraitFallback.enabled = portrait == null;
                _portraitFallback.sprite = UiTypeIcons.Get(primary, 128);
                _portraitFallback.color = UiPalette.Type(primary).WithAlpha(0.55f);
            }
        }

        private void BindMoves(List<MoveSlot> moves)
        {
            if (_moveList == null) return;
            if (_moveHeader != null) _moveHeader.SetText("Moves");

            var count = moves?.Count ?? 0;
            EnsureMoveRows(4);

            for (var i = 0; i < _moveRows.Count; i++)
            {
                var active = i < count;
                _moveRows[i].gameObject.SetActive(active);
                if (!active) continue;
                _moveRows[i].Bind(moves[i]);
            }
        }

        private void EnsureMoveRows(int count)
        {
            while (_moveRows.Count < count) _moveRows.Add(BuildMoveRow(_moveList));
        }

        /// <summary>Fades the card in or out.</summary>
        public void SetVisible(bool visible)
        {
            if (_group == null) return;
            // Tracked: the card is retargeted every time the selection moves, and an arrow key
            // held down through the 0.2s fade would otherwise hide a card that has already
            // rebound to the next creature.
            UiTween.FadeActive(ref _visibilityFade, _group, visible, 0.2f);
        }

        private void EnsureTypeBadges()
        {
            if (_typeRow == null) return;
            for (var i = 0; i < 2; i++)
            {
                if (_typeBadges[i] == null) _typeBadges[i] = TypeBadge.Build(_typeRow, ElementType.None, 24f);
            }
        }

        private static MoveListRow BuildMoveRow(Transform parent) => MoveListRow.Build(parent);

        /// <summary>Builds the detail card.</summary>
        public static CreatureDetailView Build(Transform parent)
        {
            var root = UiBuilder.Rect("CreatureDetail", parent);
            var view = root.gameObject.AddComponent<CreatureDetailView>();
            view._group = UiBuilder.Group(root);

            UiBuilder.Panel("Shell", root, UiPalette.Surface, 16, true, 22, 0.45f);

            var scroll = UiBuilder.ScrollList("Scroll", root, out var content, 14f, new RectOffset(18, 18, 18, 18));
            UiBuilder.Stretch(scroll.GetComponent<RectTransform>());

            // --- header
            var header = UiBuilder.Rect("Header", content);
            UiBuilder.Horizontal(header, 14f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(header, preferredHeight: 110f, minHeight: 110f, flexibleWidth: 1f);

            var portraitHolder = UiBuilder.Rect("Portrait", header, false);
            UiBuilder.Size(portraitHolder, preferredWidth: 104f, preferredHeight: 104f,
                minWidth: 104f, minHeight: 104f);
            var portraitBg = UiBuilder.Image("Bg", portraitHolder, UiSprites.Panel(14), UiPalette.SurfaceSunken);
            UiBuilder.Stretch(portraitBg.rectTransform);
            view._portrait = UiBuilder.Image("Image", portraitHolder, null, Color.white, Image.Type.Simple);
            view._portrait.preserveAspect = true;
            UiBuilder.Stretch(view._portrait.rectTransform, 6f);
            view._portraitFallback = UiBuilder.Image("Fallback", portraitHolder, null, UiPalette.TextMuted,
                Image.Type.Simple);
            view._portraitFallback.preserveAspect = true;
            UiBuilder.Stretch(view._portraitFallback.rectTransform, 20f);

            var identity = UiBuilder.Rect("Identity", header);
            UiBuilder.Vertical(identity, 6f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(identity, flexibleWidth: 1f);

            view._name = UiBuilder.Text("Name", identity, "—", UiTextRole.Title, UiPalette.TextPrimary);
            view._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(view._name.rectTransform, preferredHeight: 38f, flexibleWidth: 1f);

            view._speciesLine = UiBuilder.Text("Species", identity, string.Empty, UiTextRole.Caption,
                UiPalette.TextMuted);
            view._speciesLine.characterSpacing = 0.5f;
            UiBuilder.Size(view._speciesLine.rectTransform, preferredHeight: 15f, flexibleWidth: 1f);

            var typeRow = UiBuilder.Rect("Types", identity);
            UiBuilder.Horizontal(typeRow, 6f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(typeRow, preferredHeight: 26f, minHeight: 26f, flexibleWidth: 1f);
            view._typeRow = typeRow;

            UiBuilder.Divider(content);

            // --- ability and item
            view._abilityValue = BuildField(content, "Ability", out _);
            view._itemValue = BuildField(content, "Held item", out _);

            UiBuilder.Divider(content);

            UiBuilder.Text("StatsHeader", content, "Base stats", UiTextRole.Overline, UiPalette.TextMuted);
            view._stats = StatSpreadView.Build(content);

            UiBuilder.Divider(content);

            view._moveHeader = UiBuilder.Text("MovesHeader", content, "Moves", UiTextRole.Overline, UiPalette.TextMuted);

            view._moveList = UiBuilder.Rect("Moves", content);
            UiBuilder.Vertical(view._moveList, 5f);
            UiBuilder.Size(view._moveList, flexibleWidth: 1f);

            return view;
        }

        private static TextMeshProUGUI BuildField(Transform parent, string label, out TextMeshProUGUI labelText)
        {
            var row = UiBuilder.Rect("Field_" + label, parent);
            UiBuilder.Horizontal(row, 8f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(row, preferredHeight: 22f, minHeight: 22f, flexibleWidth: 1f);

            labelText = UiBuilder.Text("Label", row, label, UiTextRole.Overline, UiPalette.TextMuted);
            labelText.fontSize = 10f;
            UiBuilder.Size(labelText.rectTransform, preferredWidth: 96f, minWidth: 96f);

            var value = UiBuilder.Text("Value", row, "—", UiTextRole.Secondary, UiPalette.TextPrimary);
            value.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(value.rectTransform, flexibleWidth: 1f);
            return value;
        }
    }
}
