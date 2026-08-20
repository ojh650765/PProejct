using TMPro;
using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>
    /// A hard dark rim around a label, built from copies of the label rather than from the
    /// font material.
    ///
    /// <b>Why not the material.</b> TextMesh Pro's <c>OUTLINE_ON</c> and <c>UNDERLAY_ON</c>
    /// passes both read the glyph's signed distance field to decide how far outside the letter
    /// they are. This project's Korean font asset is generated with a <i>smooth</i> atlas, not
    /// a distance-field one — the ramp from inside to outside a glyph is about one pixel wide,
    /// so every outline width maps to roughly nothing and the material route silently draws no
    /// rim at all. That was verified on screen, not assumed: the first pass of the bright title
    /// screen set <c>_OutlineWidth</c> to 0.24 and the wordmark came back with no outline on it.
    ///
    /// <b>So the rim is real geometry.</b> Eight copies of the label in the rim colour, ringed
    /// around the original at a pixel offset, with the face drawn last on top. It costs eight
    /// extra text meshes per label, which is why it is opt-in and reserved for display type —
    /// the wordmark, a ribbon caption, the creature's name at the end of a pull — rather than
    /// applied to body copy.
    ///
    /// <b>It tracks the string.</b> A label whose text is reassigned later (the gacha's roll
    /// button changes when the team exists) would otherwise keep a rim spelling the old words,
    /// which is a far worse failure than having no rim. The component watches the face and
    /// re-copies whenever it changes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UiInkText : MonoBehaviour
    {
        /// <summary>Unit ring the copies sit on: four sides at full offset, four corners pulled in.</summary>
        private static readonly Vector2[] Ring =
        {
            new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
            new Vector2(0.72f, 0.72f), new Vector2(-0.72f, 0.72f),
            new Vector2(0.72f, -0.72f), new Vector2(-0.72f, -0.72f),
        };

        private TextMeshProUGUI _face;
        private TextMeshProUGUI[] _copies;
        private string _last;

        /// <summary>
        /// Rims <paramref name="face"/> in <paramref name="ink"/>, <paramref name="pixels"/>
        /// wide, optionally with a cast shadow <paramref name="drop"/> pixels below it.
        /// </summary>
        public static UiInkText Apply(TextMeshProUGUI face, Color ink, float pixels, float drop = 0f)
        {
            if (face == null || pixels <= 0f) return null;

            var existing = face.GetComponent<UiInkText>();
            if (existing != null) return existing;

            var parent = face.transform.parent;
            if (parent == null) return null;

            var count = Ring.Length + (drop > 0f ? 1 : 0);
            var copies = new TextMeshProUGUI[count];

            for (var i = 0; i < count; i++)
            {
                var isDrop = i >= Ring.Length;
                var offset = isDrop ? new Vector2(0f, -drop) : Ring[i] * pixels;
                var tint = isDrop ? ink.WithAlpha(ink.a * 0.5f) : ink;
                copies[i] = Clone(face, parent, offset, tint, isDrop ? "Drop" : "Rim" + i);
            }

            // The face has to end up in front of every copy, and the copies were appended
            // after it. One reorder is cheaper and less fragile than inserting eight.
            face.rectTransform.SetAsLastSibling();

            var component = face.gameObject.AddComponent<UiInkText>();
            component._face = face;
            component._copies = copies;
            component._last = face.text;
            return component;
        }

        private static TextMeshProUGUI Clone(TextMeshProUGUI source, Transform parent,
                                             Vector2 offset, Color tint, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);

            var from = source.rectTransform;
            rect.anchorMin = from.anchorMin;
            rect.anchorMax = from.anchorMax;
            rect.pivot = from.pivot;
            rect.sizeDelta = from.sizeDelta;
            rect.anchoredPosition = from.anchoredPosition + offset;
            rect.localScale = from.localScale;
            rect.localRotation = from.localRotation;

            var copy = go.AddComponent<TextMeshProUGUI>();
            copy.font = source.font;
            copy.fontSize = source.fontSize;
            copy.fontStyle = source.fontStyle;
            copy.characterSpacing = source.characterSpacing;
            copy.lineSpacing = source.lineSpacing;
            copy.alignment = source.alignment;
            copy.textWrappingMode = source.textWrappingMode;
            copy.overflowMode = source.overflowMode;
            copy.raycastTarget = false;
            copy.color = tint;
            copy.text = source.text;
            return copy;
        }

        private void LateUpdate()
        {
            if (_face == null || _copies == null) { enabled = false; return; }
            if (_face.text == _last) return;

            _last = _face.text;
            for (var i = 0; i < _copies.Length; i++)
            {
                if (_copies[i] != null) _copies[i].text = _last;
            }
        }
    }
}
