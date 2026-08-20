using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// One glass pane: the soft shadow under it, the fill, the hairline rim, and the sheen
    /// across its top. Held as a struct so a view can restyle a row it built ten frames ago
    /// without walking the hierarchy for it by name.
    /// </summary>
    public readonly struct UiPane
    {
        /// <summary>The pane's own rect. Content goes here, on top of the sheen.</summary>
        public readonly RectTransform Root;
        /// <summary>The soft shadow. Null when the pane was built flat against its parent.</summary>
        public readonly Image Shadow;
        /// <summary>The fill. This is the raycast target and the colour that means something.</summary>
        public readonly Image Fill;
        /// <summary>The hairline along the edge. Separating two translucent blues is its whole job.</summary>
        public readonly Image Rim;
        /// <summary>The highlight across the top of the fill. Null when the pane was built flat.</summary>
        public readonly Image Sheen;

        public UiPane(RectTransform root, Image shadow, Image fill, Image rim, Image sheen)
        {
            Root = root;
            Shadow = shadow;
            Fill = fill;
            Rim = rim;
            Sheen = sheen;
        }

        public bool IsValid => Root != null && Fill != null;
    }

    /// <summary>
    /// The front end's visual kit, in one place.
    ///
    /// <b>What it is for.</b> The title, gacha, account and confirmation screens are supposed
    /// to look like the shop window of a modern creature game: a lit navy backdrop, panes of
    /// blue glass layered over it, white bold type, and a selected row that inverts to
    /// near-white with a lime cursor stuck to its left edge. They used to look like the battle
    /// scanner — near-black, slate, a hairline of cyan — because the scanner's kit was the only
    /// kit there was. Rather than restyle four screens by hand and let them drift apart on the
    /// fifth edit, everything that makes a surface look like this lives here:
    /// <see cref="Pane"/>, <see cref="Backdrop"/>, <see cref="Cursor"/>, <see cref="Ball"/>.
    ///
    /// <b>Why a pane is four images.</b> A translucent rounded rect with a hairline rim, a soft
    /// shadow and a top sheen cannot be one sprite: the rim and the fill are different colours
    /// and different alphas, and the shadow has to sit outside the rect. Four stacked
    /// nine-slices cost four draws and buy a surface that reads as a sheet of glass over a lit
    /// scene instead of a flat rectangle — which is the entire difference between this and a
    /// settings dialog.
    ///
    /// <b>Nothing here changes <see cref="UiSprites"/> or <see cref="UiPalette"/>.</b> Both are
    /// shared with every gameplay screen; this composes what they already expose plus the
    /// additive Ace* names, and owns the composition rules itself.
    /// </summary>
    public static class UiJuice
    {
        /// <summary>Corner radius every pane in this register shares. One radius is what makes them a set.</summary>
        public const int Radius = 18;

        // ------------------------------------------------------------------- panes

        /// <summary>
        /// Builds a glass pane filling <paramref name="parent"/>.
        ///
        /// <paramref name="fill"/> carries its own alpha and is expected to be under 1 — the
        /// depth in this register comes from stacking the same blue at different opacities over
        /// one lit ground, not from inventing a new grey per layer.
        /// </summary>
        public static UiPane Pane(string name, Transform parent, Color fill,
            int radius = Radius, bool shadow = true, bool rim = true, bool sheen = true,
            Color? rimColour = null, int sheenHeight = 96)
        {
            var root = UiBuilder.Rect(name, parent);

            Image shadowImage = null;
            if (shadow)
            {
                shadowImage = UiBuilder.Image("Shadow", root, UiSprites.Shadow(radius, 24), UiPalette.AceShadow);
                var r = shadowImage.rectTransform;
                UiBuilder.Stretch(r);
                r.offsetMin = new Vector2(-14f, -20f);
                r.offsetMax = new Vector2(14f, 8f);
            }

            var fillImage = UiBuilder.Image("Fill", root, UiSprites.Panel(radius), fill);
            UiBuilder.Stretch(fillImage.rectTransform);

            Image sheenImage = null;
            if (sheen)
            {
                var h = Mathf.Clamp(sheenHeight, 16, 256);
                sheenImage = UiBuilder.Image("Sheen", root, UiSprites.Gloss(h, radius), UiPalette.AceSheen);
                UiBuilder.Stretch(sheenImage.rectTransform);
            }

            Image rimImage = null;
            if (rim)
            {
                rimImage = UiBuilder.Image("Rim", root, UiSprites.Frame(radius, 2), rimColour ?? UiPalette.AceRim);
                UiBuilder.Stretch(rimImage.rectTransform);
            }

            return new UiPane(root, shadowImage, fillImage, rimImage, sheenImage);
        }

        /// <summary>Tweens a pane's fill (and optionally its rim) to a new colour.</summary>
        public static void Recolour(in UiPane pane, Color fill, Color? rim = null, float seconds = 0.16f)
        {
            if (!pane.IsValid) return;
            var face = pane.Fill;
            UiTween.Color(face.color, fill, seconds, c => { if (face != null) face.color = c; });
            if (rim.HasValue && pane.Rim != null)
            {
                var edge = pane.Rim;
                UiTween.Color(edge.color, rim.Value, seconds, c => { if (edge != null) edge.color = c; });
            }
        }

        // --------------------------------------------------------------- backdrop

        /// <summary>
        /// The standing background for every front-end screen: a deep navy ground, two very
        /// large soft lights over it, a faint hex grain, and a darkening along the bottom edge.
        ///
        /// It is built out of lights rather than out of a gradient texture because that is what
        /// makes translucent panels worth having — a pane over a flat colour is just a lighter
        /// flat colour, and a pane over an uneven field reads as glass. The lights drift, so a
        /// screen that sits open for minutes is never completely still.
        /// </summary>
        public static RectTransform Backdrop(Transform parent, Color? key = null, Color? fillLight = null)
        {
            var root = UiBuilder.Rect("Backdrop", parent);
            UiBuilder.IgnoreLayout(root);

            var ground = UiBuilder.Image("Ground", root, null, UiPalette.AceNight, Image.Type.Simple);
            UiBuilder.Stretch(ground.rectTransform);

            Light(root, key ?? UiPalette.AceHalo, 0.55f, new Vector2(0.30f, 1.02f), 2600f, 34f, 0f);
            Light(root, fillLight ?? UiPalette.AceCyan, 0.16f, new Vector2(1.02f, 0.18f), 1900f, 27f, 0.5f);

            var grain = UiBuilder.Image("Grain", root, UiSprites.HexGrid(40), new Color(1f, 1f, 1f, 0.035f),
                Image.Type.Tiled);
            UiBuilder.Stretch(grain.rectTransform);

            var floorFade = UiBuilder.Image("Floor", root, UiSprites.VerticalFade(128, 1.5f),
                new Color(0.008f, 0.024f, 0.063f, 0.75f), Image.Type.Simple);
            UiBuilder.Anchor(floorFade.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 420f));

            return root;
        }

        /// <summary>One big soft light. Drifts on a long period so the field is never quite static.</summary>
        private static void Light(Transform parent, Color colour, float strength, Vector2 at,
                                  float size, float period, float phase)
        {
            var glow = UiBuilder.Image("Light", parent, UiSprites.Glow(256, 1.5f),
                colour.WithAlpha(strength), Image.Type.Simple);
            UiBuilder.Anchor(glow.rectTransform, at, at, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(size, size));
            UiIdle.Attach(glow.rectTransform, UiIdleMode.Bob, 60f, period, phase);
        }

        // ----------------------------------------------------------------- cursor

        /// <summary>
        /// The "you are here" mark: a lime <b>double</b> chevron with a soft halo behind it.
        ///
        /// It sits outside the row it belongs to, in the list's left margin, so the rows stay a
        /// clean stack and none of them has to reserve space for a cursor that is only ever on
        /// one of them.
        ///
        /// <b>Two heads, not one.</b> It used to be <see cref="UiSprites.ChevronCursor"/> — a
        /// single arrowhead with a bar standing behind it — which is a perfectly good cursor and
        /// is not the one the reference uses. The Z-A menu's selected row grows a pair of
        /// chevrons out of its left edge, and a pair reads as motion toward the row in a way a
        /// single head does not: two marks at the same angle imply the direction they are
        /// stacked along. The trailing head is drawn at half alpha so the pair has a front and a
        /// back rather than looking like one mark that has been duplicated by mistake.
        /// </summary>
        public static RectTransform Cursor(Transform parent, float size = 46f, Color? colour = null)
        {
            var cursor = UiBuilder.Rect("Cursor", parent, false);
            UiBuilder.Anchor(cursor, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));

            var tint = colour ?? UiPalette.AceLime;

            var halo = UiBuilder.Image("Halo", cursor, UiSprites.Glow(128, 1.8f), tint.WithAlpha(0.45f),
                Image.Type.Simple);
            UiBuilder.Stretch(halo.rectTransform, -size * 0.45f);

            // UiSprites.Chevron draws its triangle pointing up and spanning roughly 0.76 of the
            // box across and 0.5 of it down, so a -90 degree turn makes it point right and the
            // heads have to be spaced by a little more than half a box or they merge into one
            // arrow. These two numbers were picked against that geometry, not by eye.
            ChevronHead(cursor, tint.WithAlpha(0.5f), size * 0.80f, -size * 0.30f);
            ChevronHead(cursor, tint, size * 0.80f, size * 0.08f);

            return cursor;
        }

        /// <summary>One arrowhead of the cursor's pair, turned to point right.</summary>
        private static void ChevronHead(Transform parent, Color tint, float box, float x)
        {
            var head = UiBuilder.Image("Head", parent, UiSprites.Chevron(64), tint, Image.Type.Simple);
            UiBuilder.Anchor(head.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(x, 0f), new Vector2(box, box));
            head.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -90f);
        }

        // -------------------------------------------------------------------- ball

        /// <summary>
        /// A Poké Ball, from generated discs alone.
        ///
        /// The red cap is the same disc sprite as the white body with
        /// <c>Image.type = Filled</c> and a 180 degree radial fill, so the ball needs no art
        /// asset at all — which matters because everything else in this UI is generated too,
        /// and one imported sprite here would be the only thing on the screen a reimport could
        /// break.
        ///
        /// Returned as its own rect with a <see cref="CanvasGroup"/> already on it, because
        /// every caller either fades the whole ball or spins it, and both want one handle.
        /// </summary>
        public static RectTransform Ball(Transform parent, float size, bool shine = true)
        {
            var ball = UiBuilder.Rect("Ball", parent, false);
            UiBuilder.Anchor(ball, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));

            var rim = Mathf.Max(2f, size * 0.055f);
            var shell = UiPalette.Hex("#0A1428");

            var ink = UiBuilder.Image("Ink", ball, UiSprites.Disc(256), shell, Image.Type.Simple);
            UiBuilder.Stretch(ink.rectTransform);

            var white = UiBuilder.Image("White", ball, UiSprites.Disc(256), UiPalette.AceSelect, Image.Type.Simple);
            UiBuilder.Stretch(white.rectTransform, rim);

            var red = UiBuilder.Image("Red", ball, UiSprites.Disc(256), UiPalette.Hex("#EE3B36"), Image.Type.Simple);
            UiBuilder.Stretch(red.rectTransform, rim);
            // A straight vertical fill at half, NOT Radial180. Radial180 sweeps a full half
            // turn as fillAmount goes 0 to 1, so at 1 it covers the whole disc and the ball
            // comes out a plain red circle with no white half and no band — which is exactly
            // what the first capture of this reveal showed. Vertical from the top cuts the
            // disc across its equator, which is the shape a Poke Ball actually is.
            red.type = Image.Type.Filled;
            red.fillMethod = Image.FillMethod.Vertical;
            red.fillOrigin = (int)Image.OriginVertical.Top;
            red.fillAmount = 0.5f;

            // Pulled in by two percent at each end. The band spans the ball's full width and the
            // disc is inscribed in the same square, so at the equator the two edges coincide —
            // and a hard-edged rectangle meeting an antialiased circle leaves a small square tab
            // sticking out of each side. Invisible on the 54px pip; obvious on the 430px one the
            // login screen stands behind its form.
            var band = UiBuilder.Image("Band", ball, UiSprites.Solid(), shell, Image.Type.Simple);
            UiBuilder.Anchor(band.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-size * 0.04f, size * 0.085f));

            var button = UiBuilder.Image("Button", ball, UiSprites.Disc(128), shell, Image.Type.Simple);
            UiBuilder.Anchor(button.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size * 0.30f, size * 0.30f));

            var lens = UiBuilder.Image("Lens", ball, UiSprites.Disc(128), UiPalette.AceSelect, Image.Type.Simple);
            UiBuilder.Anchor(lens.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size * 0.17f, size * 0.17f));

            if (shine)
            {
                // Upper-left specular. An ellipse rather than a circle, because a round
                // highlight on a sphere reads as a second, smaller ball.
                var gleam = UiBuilder.Image("Shine", ball, UiSprites.Disc(128),
                    new Color(1f, 1f, 1f, 0.5f), Image.Type.Simple);
                UiBuilder.Anchor(gleam.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(-size * 0.20f, size * 0.22f),
                    new Vector2(size * 0.26f, size * 0.16f));
                gleam.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 28f);
            }

            UiBuilder.Group(ball, 1f, false, false);
            return ball;
        }

        // ------------------------------------------------------------------ meters

        /// <summary>
        /// A slim horizontal meter: a recessed track with a coloured fill pinned to its left
        /// edge. Returns the fill, so a caller that animates one keeps a handle to it.
        ///
        /// The creature cards in the reference carry two of these stacked — a green health bar
        /// with a thinner cyan experience bar directly beneath it — and that pair is the single
        /// detail that makes a small tile read as a party member rather than as an icon. Both
        /// come from here so the two never drift apart in radius or in inset.
        /// </summary>
        public static Image Meter(string name, Transform parent, Vector2 anchoredPosition,
                                  float width, float height, float fraction, Color colour)
        {
            var host = UiBuilder.Rect(name, parent, false);
            UiBuilder.Anchor(host, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                anchoredPosition, new Vector2(width, height));

            // Below about eight pixels a capsule is all corner: the nine-slice's borders are
            // wider than the rect they are drawn into and the two ends overlap into a lozenge.
            // A square-ended bar is what the reference draws at that thickness anyway.
            var rounded = height >= 8f;
            var sprite = rounded ? UiSprites.Pill(Mathf.RoundToInt(height)) : UiSprites.Solid();
            var type = rounded ? Image.Type.Sliced : Image.Type.Simple;

            UiBuilder.Image("Track", host, sprite, UiPalette.AceNight.WithAlpha(0.72f), type);

            var fill = UiBuilder.Image("Fill", host, sprite, colour, type);
            UiBuilder.Anchor(fill.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), Vector2.zero,
                new Vector2(Mathf.Max(0f, width * Mathf.Clamp01(fraction)), 0f));
            return fill;
        }

        // --------------------------------------------------------------- hint bar

        /// <summary>One control hint: the key's glyph, and what pressing it does.</summary>
        public readonly struct Hint
        {
            public readonly string Glyph;
            public readonly string Label;

            public Hint(string glyph, string label)
            {
                Glyph = glyph;
                Label = label;
            }
        }

        /// <summary>
        /// The muted strip of button glyphs and labels that sits along the bottom of every
        /// screen in this register.
        ///
        /// <b>Why the glyph is a key-cap and not just more text.</b> A hint line written as one
        /// sentence — "Enter select   Esc back" — makes the player parse which half of each
        /// pair is the key. Boxing the key turns that into a shape they recognise without
        /// reading, which is the whole reason the reference draws it that way, and it is why
        /// this is worth thirty lines rather than a single label.
        ///
        /// Laid out as one flat horizontal group with fixed-width gaps between the pairs rather
        /// than as a group of groups: a <see cref="UnityEngine.UI.ContentSizeFitter"/> inside a
        /// layout group fights the group for the same rect, which <see cref="UiBuilder.Fit"/>
        /// warns about, and every nested pair would need one.
        /// </summary>
        public static RectTransform HintBar(Transform parent, System.Collections.Generic.IReadOnlyList<Hint> hints,
                                            float height = 34f)
        {
            var bar = UiBuilder.Rect("Hints", parent, false);
            UiBuilder.Horizontal(bar, 9f, null, TextAnchor.MiddleRight);

            if (hints == null) return bar;

            for (var i = 0; i < hints.Count; i++)
            {
                var hint = hints[i];

                if (!string.IsNullOrEmpty(hint.Glyph))
                {
                    var cap = UiBuilder.Rect("Cap", bar, false);
                    // Caption is 19pt and its glyphs are narrow; this is a measured floor plus a
                    // per-character allowance, which keeps "Esc" and "↑↓" the same height and
                    // lets "Enter" grow rather than clip.
                    var capWidth = Mathf.Max(38f, 15f * hint.Glyph.Length + 22f);
                    UiBuilder.Size(cap, capWidth, height, capWidth, height);

                    UiBuilder.Image("Fill", cap, UiSprites.Panel(8), UiPalette.AceNight.WithAlpha(0.55f));
                    UiBuilder.Image("Rim", cap, UiSprites.Frame(8, 2), UiPalette.AceRim.WithAlpha(0.42f));

                    var glyph = UiBuilder.Text("Glyph", cap, hint.Glyph, UiTextRole.Caption,
                        UiPalette.AceText.WithAlpha(0.92f), TMPro.TextAlignmentOptions.Center);
                    UiBuilder.Stretch(glyph.rectTransform);
                    glyph.characterSpacing = 0f;
                }

                var label = UiBuilder.Text("Label", bar, hint.Label ?? "", UiTextRole.Caption,
                    UiPalette.AceTextDim, TMPro.TextAlignmentOptions.Left);
                UiBuilder.Size(label.rectTransform, preferredHeight: height, minHeight: height);

                // A wider gap after each pair than the 9 inside it, so the eye groups the key
                // with its own label instead of with the next key along.
                if (i < hints.Count - 1) UiBuilder.Size(UiBuilder.Rect("Gap", bar, false), 20f, height, 20f, height);
            }

            return bar;
        }

        // -------------------------------------------------------------------- text

        /// <summary>
        /// Gives a label a hard dark rim, for the few places white type has to sit on top of
        /// something bright rather than on glass.
        ///
        /// It is real geometry — eight offset copies behind the face — and not TMP's material
        /// outline. The project's Korean font asset is generated with a smooth atlas rather
        /// than a distance-field one, so <c>_OutlineWidth</c> maps to about a pixel of ramp and
        /// draws nothing; that was checked on screen before this was written. See
        /// <see cref="UiInkText"/> for the mechanics and the cost.
        /// </summary>
        public static void Ink(TextMeshProUGUI text, Color? outline = null, float pixels = 4f, float drop = 0f)
        {
            UiInkText.Apply(text, outline ?? UiPalette.AceNight, pixels, drop);
        }

        // ------------------------------------------------------------------ motion

        /// <summary>
        /// The staggered entrance: each element slides in from <paramref name="from"/> and
        /// fades up, <paramref name="delay"/> seconds after the screen was built.
        ///
        /// <see cref="Ease.OutBack"/> on the move is what makes it read as thrown rather than
        /// slid — the overshoot is small and the settle is the whole effect.
        /// </summary>
        public static void PopIn(RectTransform rect, float delay, Vector2 from, float duration = 0.42f)
        {
            if (rect == null) return;

            var target = rect.anchoredPosition;
            rect.anchoredPosition = target + from;
            UiTween.AnchoredMove(rect, target, duration, Ease.OutBack, delay);

            var group = UiBuilder.Group(rect, 0f, true, true);
            UiTween.Fade(group, 1f, duration * 0.7f, Ease.OutCubic, delay);
        }

        /// <summary>Same entrance, expressed as a scale pop for things that have nowhere to slide from.</summary>
        public static void PopScale(Transform target, float delay, float from = 0.82f, float duration = 0.4f)
        {
            if (target == null) return;
            target.localScale = Vector3.one * from;
            UiTween.Scale(target, Vector3.one, duration, Ease.OutBack, delay);
        }

        /// <summary>
        /// Squash-and-stretch on a press, then <paramref name="then"/>.
        ///
        /// The action is fired on the way out rather than on the way in, so the player sees
        /// the button take the press before the screen changes underneath them — a menu that
        /// loads the next scene on the frame the key went down never shows its own feedback.
        /// With motion off there is nothing to wait for and the action fires immediately,
        /// because a delay with no animation behind it is just latency.
        /// </summary>
        public static void Squash(Transform target, Action then = null, float amount = 0.12f)
        {
            if (target == null || !UiTween.MotionEnabled)
            {
                then?.Invoke();
                return;
            }

            UiTween.Scale(target, new Vector3(1f + amount, 1f - amount, 1f), 0.07f, Ease.OutCubic, 0f,
                () => UiTween.Scale(target, Vector3.one, 0.26f, Ease.OutElastic, 0f, () => then?.Invoke()));
        }

        /// <summary>
        /// A shockwave: a ring thrown outward from a point, fading as it goes. The gacha's
        /// payoff is several of these at staggered delays, which is the whole vocabulary of
        /// "something just happened here".
        /// </summary>
        public static void Shockwave(Transform parent, Color colour, float startSize, float endScale,
                                     float seconds = 0.7f, float delay = 0f, int thickness = 8)
        {
            var ring = UiBuilder.Image("Shockwave", parent, UiSprites.DiscRing(256, thickness), colour, Image.Type.Simple);
            UiBuilder.Anchor(ring.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(startSize, startSize));
            ring.rectTransform.localScale = Vector3.one * 0.6f;

            var alpha = colour.a;
            UiTween.Run(seconds, t =>
            {
                if (ring == null) return;
                ring.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.6f, endScale, t);
                ring.color = colour.WithAlpha((1f - t) * alpha);
            }, Ease.OutCubic, delay);
        }
    }
}
