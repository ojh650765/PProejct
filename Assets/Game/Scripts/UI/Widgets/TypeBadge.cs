using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The elemental type chip: procedural glyph plus name, on a deepened type-coloured pill.
    ///
    /// Two variants exist because both are needed constantly and mixing them looks sloppy:
    /// <see cref="Build"/> makes the labelled chip for detail views, <see cref="BuildCompact"/>
    /// makes the glyph-only square for dense rows where a word would not fit.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TypeBadge : MonoBehaviour
    {
        [SerializeField] private Image _background;
        [SerializeField] private Image _glyph;
        [SerializeField] private TextMeshProUGUI _label;

        private ElementType _type = ElementType.None;

        /// <summary>The type currently shown.</summary>
        public ElementType Type => _type;

        /// <summary>Re-tints and re-glyphs for a type. Cheap enough to call on every rebind.</summary>
        public void Bind(ElementType type)
        {
            _type = type;
            var accent = UiPalette.Type(type);

            if (_background != null) _background.color = UiPalette.TypeDeep(type);
            if (_glyph != null)
            {
                _glyph.sprite = UiTypeIcons.Get(type, 40);
                _glyph.color = accent;
                _glyph.enabled = type != ElementType.None;
            }
            if (_label != null)
            {
                _label.SetText(UiType.TypeName(type).ToUpperInvariant());
                _label.color = accent;
            }
            gameObject.SetActive(type != ElementType.None);
        }

        /// <summary>Fades the whole badge, for the "not scouted yet" state.</summary>
        public void SetDimmed(bool dimmed)
        {
            var group = UiBuilder.Group(this);
            group.alpha = dimmed ? 0.35f : 1f;
        }

        /// <summary>Labelled chip: glyph + type name on a pill.</summary>
        public static TypeBadge Build(Transform parent, ElementType type, float height = 26f)
        {
            // The layout group lives on the root, not on a padded child, so the root itself
            // reports a preferred width to whatever list it sits in and the pill hugs its
            // label. A ContentSizeFitter here would fight that parent group instead.
            var root = UiBuilder.Rect("TypeBadge", parent, false);
            UiBuilder.Horizontal(root, height * 0.18f,
                new RectOffset(Mathf.RoundToInt(height * 0.30f), Mathf.RoundToInt(height * 0.44f), 0, 0),
                TextAnchor.MiddleLeft);
            UiBuilder.Size(root, preferredHeight: height, minHeight: height);

            var background = UiBuilder.Backdrop("Bg", root, UiSprites.Pill(Mathf.RoundToInt(height)), Color.white);

            var glyph = UiBuilder.Image("Glyph", root, null, Color.white, Image.Type.Simple);
            glyph.preserveAspect = true;
            UiBuilder.Size(glyph.rectTransform, preferredWidth: height * 0.60f, preferredHeight: height * 0.60f,
                minWidth: height * 0.60f, minHeight: height * 0.60f);

            var label = UiBuilder.Text("Label", root, string.Empty, UiTextRole.Caption, null, TextAlignmentOptions.Left);
            label.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            label.fontSize = height * 0.42f;

            var badge = root.gameObject.AddComponent<TypeBadge>();
            badge._background = background;
            badge._glyph = glyph;
            badge._label = label;
            badge.Bind(type);
            return badge;
        }

        /// <summary>Glyph-only square, for dense lists and health-bar headers.</summary>
        public static TypeBadge BuildCompact(Transform parent, ElementType type, float size = 24f)
        {
            var root = UiBuilder.Rect("TypeBadgeCompact", parent, false);
            UiBuilder.Size(root, preferredWidth: size, preferredHeight: size, minWidth: size, minHeight: size);

            var background = UiBuilder.Image("Bg", root, UiSprites.Panel(Mathf.Max(4, Mathf.RoundToInt(size * 0.30f))), Color.white);
            UiBuilder.Stretch(background.rectTransform);

            var glyph = UiBuilder.Image("Glyph", root, null, Color.white, Image.Type.Simple);
            glyph.preserveAspect = true;
            UiBuilder.Stretch(glyph.rectTransform, size * 0.18f);

            var badge = root.gameObject.AddComponent<TypeBadge>();
            badge._background = background;
            badge._glyph = glyph;
            badge.Bind(type);
            return badge;
        }
    }
}
