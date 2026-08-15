using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// One dex tile, in one of three states.
    ///
    /// Unknown: number only, everything else silhouetted — the gap is visible and legible as
    /// a gap. Seen: name and types revealed, portrait still darkened. Caught: full colour
    /// with a caught mark. Three distinct states rather than two is what makes a partially
    /// filled dex feel like progress instead of a binary checklist.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DexEntryTile : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private Image _panel;
        [SerializeField] private Image _portrait;
        [SerializeField] private Image _selectionRing;
        [SerializeField] private Image _caughtMark;
        [SerializeField] private TextMeshProUGUI _number;
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private RectTransform _typeRow;

        private readonly TypeBadge[] _typeBadges = new TypeBadge[2];

        /// <summary>Species this tile shows.</summary>
        public int SpeciesId { get; private set; } = -1;

        /// <summary>Raised on click with the species id.</summary>
        public Action<int> Selected;

        /// <summary>Binds a species with its discovery state.</summary>
        public void Bind(SpeciesData species, bool seen, bool caught)
        {
            if (species == null) return;
            SpeciesId = species.Id;

            if (_number != null) _number.SetText("#" + species.NationalDex.ToString("000"));
            if (_name != null)
            {
                _name.SetText(seen ? species.DisplayName : "— — —");
                _name.color = seen ? UiPalette.TextPrimary : UiPalette.TextMuted;
            }

            EnsureTypeBadges();
            _typeBadges[0]?.Bind(seen ? species.Type1 : ElementType.None);
            _typeBadges[1]?.Bind(seen ? species.Type2 : ElementType.None);

            if (_portrait != null)
            {
                var art = UiServices.PortraitOf(species.Id);
                _portrait.sprite = art != null ? art : UiTypeIcons.Get(species.Type1, 96);
                _portrait.preserveAspect = true;
                // Silhouette when unseen; dimmed when seen but not caught; full when caught.
                _portrait.color = caught
                    ? (art != null ? Color.white : UiPalette.Type(species.Type1))
                    : (seen ? new Color(0.6f, 0.66f, 0.74f, 0.85f) : new Color(0.12f, 0.14f, 0.18f, 0.95f));
            }

            if (_caughtMark != null) _caughtMark.enabled = caught;
            if (_panel != null)
            {
                _panel.color = caught
                    ? Color.Lerp(UiPalette.SurfaceRaised, UiPalette.ScannerCyan, 0.10f)
                    : UiPalette.SurfaceRaised.WithAlpha(seen ? 0.85f : 0.5f);
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

        public void OnPointerClick(PointerEventData eventData) => Selected?.Invoke(SpeciesId);

        private void EnsureTypeBadges()
        {
            if (_typeRow == null) return;
            for (var i = 0; i < 2; i++)
            {
                if (_typeBadges[i] == null) _typeBadges[i] = TypeBadge.BuildCompact(_typeRow, ElementType.None, 16f);
            }
        }

        /// <summary>Builds a tile sized for the dex grid.</summary>
        public static DexEntryTile Build(Transform parent)
        {
            var root = UiBuilder.Rect("DexTile", parent, false);
            var tile = root.gameObject.AddComponent<DexEntryTile>();

            tile._panel = UiBuilder.Image("Bg", root, UiSprites.Panel(12), UiPalette.SurfaceRaised,
                Image.Type.Sliced, true);
            UiBuilder.Stretch(tile._panel.rectTransform);

            tile._selectionRing = UiBuilder.Image("Ring", root, UiSprites.Frame(12, 2), Color.clear);
            UiBuilder.Stretch(tile._selectionRing.rectTransform);

            var stack = UiBuilder.Rect("Stack", root);
            UiBuilder.Stretch(stack, 8f);
            UiBuilder.Vertical(stack, 3f, null, TextAnchor.UpperCenter);

            tile._number = UiBuilder.Text("Number", stack, "#000", UiTextRole.Caption, UiPalette.TextMuted,
                TextAlignmentOptions.Center);
            tile._number.fontSize = 10f;
            UiBuilder.Size(tile._number.rectTransform, preferredHeight: 12f, flexibleWidth: 1f);

            var portraitHolder = UiBuilder.Rect("Portrait", stack);
            UiBuilder.Size(portraitHolder, preferredHeight: 58f, minHeight: 58f, flexibleWidth: 1f);
            tile._portrait = UiBuilder.Image("Image", portraitHolder, null, Color.white, Image.Type.Simple);
            tile._portrait.preserveAspect = true;
            UiBuilder.Stretch(tile._portrait.rectTransform, 4f);

            tile._name = UiBuilder.Text("Name", stack, "— — —", UiTextRole.Caption, UiPalette.TextPrimary,
                TextAlignmentOptions.Center);
            tile._name.fontSize = 12f;
            tile._name.characterSpacing = 0f;
            tile._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(tile._name.rectTransform, preferredHeight: 15f, flexibleWidth: 1f);

            var typeRow = UiBuilder.Rect("Types", stack);
            UiBuilder.Horizontal(typeRow, 3f, null, TextAnchor.MiddleCenter);
            UiBuilder.Size(typeRow, preferredHeight: 16f, minHeight: 16f, flexibleWidth: 1f);
            tile._typeRow = typeRow;

            tile._caughtMark = UiBuilder.Image("Caught", root, UiSprites.Dot(14), UiPalette.ScannerCyan,
                Image.Type.Simple);
            UiBuilder.Anchor(tile._caughtMark.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(1f, 1f), new Vector2(-7f, -7f), new Vector2(8f, 8f));
            tile._caughtMark.enabled = false;

            UiButtonMotion.Attach(root, 12);
            return tile;
        }
    }
}
