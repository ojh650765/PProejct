using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>
    /// Generates and caches every sprite the Poké Lab UI draws.
    ///
    /// The brief forbids Unity's built-in UI sprites — they read as placeholder instantly —
    /// and this worker owns no editor session to author PNGs in. So the atlas is built at
    /// runtime from <see cref="UiShapes"/>: one nine-slice panel family, bars, rings,
    /// chevrons and glyphs, all sharing a single antialiasing rule and corner radius scale.
    ///
    /// Every generator is keyed and cached, so the twenty views that all want the same
    /// 24px rounded panel share one texture.
    /// </summary>
    public static class UiSprites
    {
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>(64);

        /// <summary>Pixels-per-unit used by every generated sprite. 100 keeps Image sizing intuitive.</summary>
        private const float Ppu = 100f;

        // ------------------------------------------------------------------ panels

        /// <summary>
        /// Solid rounded panel as a nine-slice. Because the border is generated at
        /// <paramref name="radius"/> and the slice inset sits just outside it, the sprite
        /// stretches to any size without the corners deforming.
        /// </summary>
        public static Sprite Panel(int radius = 18)
        {
            radius = Mathf.Clamp(radius, 2, 64);
            var key = "panel:" + radius;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var size = radius * 2 + 8;
            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var half = new Vector2(size * 0.5f - 1f, size * 0.5f - 1f);

            ForEachPixel(texture, p =>
            {
                var d = UiShapes.RoundedBox(p, centre, half, radius);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(d));
            });

            var inset = radius + 3;
            return Store(key, texture, new Vector4(inset, inset, inset, inset));
        }

        /// <summary>
        /// Rounded panel with a 1-2px inner rim, for cards that need to separate from a
        /// similarly valued background. The rim is drawn brighter than the fill so a single
        /// tinted Image produces both.
        /// </summary>
        public static Sprite PanelOutline(int radius = 18, int border = 2)
        {
            radius = Mathf.Clamp(radius, 2, 64);
            border = Mathf.Clamp(border, 1, 8);
            var key = $"panelOutline:{radius}:{border}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var size = radius * 2 + 8;
            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var half = new Vector2(size * 0.5f - 1f, size * 0.5f - 1f);

            ForEachPixel(texture, p =>
            {
                var outer = UiShapes.RoundedBox(p, centre, half, radius);
                var inner = UiShapes.RoundedBox(p, centre, half - Vector2.one * border, Mathf.Max(1f, radius - border));
                var rim = UiShapes.Coverage(UiShapes.Subtract(outer, inner));
                var fill = UiShapes.Coverage(inner);
                // Rim rides on top at full value; fill sits at 55% so one tint yields both tones.
                var alpha = Mathf.Max(rim, fill * 0.55f);
                return new Color(1f, 1f, 1f, alpha);
            });

            var inset = radius + 3;
            return Store(key, texture, new Vector4(inset, inset, inset, inset));
        }

        /// <summary>Hollow rounded frame — no fill, just the rim. For focus rings and selection.</summary>
        public static Sprite Frame(int radius = 18, int border = 2)
        {
            radius = Mathf.Clamp(radius, 2, 64);
            border = Mathf.Clamp(border, 1, 8);
            var key = $"frame:{radius}:{border}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var size = radius * 2 + 8;
            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var half = new Vector2(size * 0.5f - 1f, size * 0.5f - 1f);

            ForEachPixel(texture, p =>
            {
                var outer = UiShapes.RoundedBox(p, centre, half, radius);
                var inner = UiShapes.RoundedBox(p, centre, half - Vector2.one * border, Mathf.Max(1f, radius - border));
                return new Color(1f, 1f, 1f, UiShapes.Coverage(UiShapes.Subtract(outer, inner)));
            });

            var inset = radius + 3;
            return Store(key, texture, new Vector4(inset, inset, inset, inset));
        }

        /// <summary>
        /// A soft radial drop shadow as a nine-slice. Placed behind a panel at a small
        /// offset it gives the whole UI the raised-card depth the visual direction asks for
        /// without a single blur pass at runtime.
        /// </summary>
        public static Sprite Shadow(int radius = 18, int spread = 16)
        {
            radius = Mathf.Clamp(radius, 2, 64);
            spread = Mathf.Clamp(spread, 2, 48);
            var key = $"shadow:{radius}:{spread}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var size = (radius + spread) * 2 + 8;
            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var half = new Vector2(size * 0.5f - spread - 1f, size * 0.5f - spread - 1f);

            ForEachPixel(texture, p =>
            {
                var d = UiShapes.RoundedBox(p, centre, half, radius);
                // Quadratic falloff over the spread reads much closer to a gaussian than linear.
                var t = Mathf.Clamp01(1f - d / spread);
                return new Color(0f, 0f, 0f, t * t);
            });

            var inset = radius + spread + 3;
            return Store(key, texture, new Vector4(inset, inset, inset, inset));
        }

        /// <summary>Fully rounded capsule — bar fills, badges, pills.</summary>
        public static Sprite Pill(int height = 24)
        {
            height = Mathf.Clamp(height, 4, 96);
            var key = "pill:" + height;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var size = height + 8;
            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var half = new Vector2(size * 0.5f - 1f, size * 0.5f - 1f);

            ForEachPixel(texture, p =>
            {
                var d = UiShapes.RoundedBox(p, centre, half, half.y);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(d));
            });

            var inset = size * 0.5f - 1f;
            return Store(key, texture, new Vector4(inset, inset, inset, inset));
        }

        /// <summary>
        /// A filled circle, NOT nine-sliced.
        ///
        /// Every other sprite here is a nine-slice so it can stretch to any rect; a disc must
        /// not be one, because stretching a nine-sliced circle produces a rounded rectangle
        /// with a stretched middle rather than an ellipse. It is drawn once at a resolution
        /// large enough for the biggest use (the gacha ball) and scaled down, which is the
        /// right way round: a circle scaled up shows its own texels.
        ///
        /// Used with <c>Image.type = Filled</c> and <c>Radial180</c> it also gives clean half
        /// discs, which is how the ball's red cap is drawn without a second sprite.
        /// </summary>
        public static Sprite Disc(int size = 256)
        {
            size = Mathf.Clamp(size, 8, 512);
            var key = "disc:" + size;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var radius = size * 0.5f - 1f;

            ForEachPixel(texture, p =>
            {
                var d = UiShapes.Circle(p, centre, radius);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(d));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// An unfilled circle of the given thickness, for the glow rings the gacha reveal
        /// throws outward. Not nine-sliced, for the same reason <see cref="Disc"/> is not.
        /// </summary>
        public static Sprite DiscRing(int size = 256, int thickness = 10)
        {
            size = Mathf.Clamp(size, 8, 512);
            thickness = Mathf.Clamp(thickness, 1, size / 2);
            var key = "discring:" + size + ":" + thickness;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var radius = size * 0.5f - thickness * 0.5f - 1f;

            ForEachPixel(texture, p =>
            {
                var d = UiShapes.Ring(p, centre, radius, thickness);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(d));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>Flat 1x1 white. The only unstyled sprite, for dividers and solid washes.</summary>
        public static Sprite Solid()
        {
            const string key = "solid";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(4, 4);
            ForEachPixel(texture, _ => Color.white);
            return Store(key, texture, Vector4.zero);
        }

        // ------------------------------------------------------------- overlay art

        /// <summary>
        /// A vertical alpha ramp: solid along the bottom edge, gone at the top. Draw it with
        /// <see cref="UnityEngine.UI.Image.Type.Simple"/> so it stretches to any height.
        ///
        /// The conversation overlay needs a top edge nobody can point at. A hard-edged
        /// translucent bar over a lit scene reads as a rectangle laid on the picture; the
        /// same value arrived at over eighty pixels reads as the picture darkening, which is
        /// the whole reason the reference composition keeps the art as the subject.
        /// </summary>
        public static Sprite VerticalFade(int height = 64, float gamma = 1.7f)
        {
            height = Mathf.Clamp(height, 4, 256);
            var key = $"vfade:{height}:{gamma:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(4, height);
            var span = Mathf.Max(1f, height - 1f);
            ForEachPixel(texture, p =>
            {
                // Row 0 is the bottom in texture space, which is the solid end.
                var t = Mathf.Clamp01((p.y - 0.5f) / span);
                return new Color(1f, 1f, 1f, Mathf.Pow(1f - t, gamma));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// A hairline that starts hard at its left end and dissolves before its right one.
        ///
        /// A rule that runs edge to edge draws a box; a rule that fades out draws an
        /// underline that happens to be long. The reference does the latter, and it is what
        /// keeps the composition from re-acquiring the panel it just got rid of.
        /// </summary>
        public static Sprite FadeRule(int length = 256, float solidFraction = 0.72f)
        {
            length = Mathf.Clamp(length, 8, 1024);
            solidFraction = Mathf.Clamp01(solidFraction);
            var key = $"faderule:{length}:{solidFraction:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(length, 4);
            var span = Mathf.Max(1f, length - 1f);
            ForEachPixel(texture, p =>
            {
                var x = Mathf.Clamp01((p.x - 0.5f) / span);
                var a = x <= solidFraction ? 1f : 1f - Mathf.InverseLerp(solidFraction, 1f, x);
                return new Color(1f, 1f, 1f, a * a);
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// A horizontally sheared slab: horizontal top and bottom, leaning left and right
        /// edges. Because only the ends lean, the shape nine-slices cleanly across its width
        /// and one sprite serves a 90px button and a 700px choice row at the same lean.
        /// </summary>
        public static Sprite Slant(int height = 40, int slant = 8)
        {
            height = Mathf.Clamp(height, 6, 160);
            slant = Mathf.Clamp(slant, 0, height);
            var key = $"slant:{height}:{slant}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var width = slant * 2 + 12;
            var texture = NewTexture(width, height);
            ForEachPixel(texture, p => new Color(1f, 1f, 1f,
                UiShapes.Coverage(SlantDistance(p, width, height, slant, 0f))));

            var inset = slant + 5;
            return Store(key, texture, new Vector4(inset, 0f, inset, 0f));
        }

        /// <summary>Hollow counterpart to <see cref="Slant"/> — the leaning rim on its own.</summary>
        public static Sprite SlantFrame(int height = 40, int slant = 8, int border = 2)
        {
            height = Mathf.Clamp(height, 6, 160);
            slant = Mathf.Clamp(slant, 0, height);
            border = Mathf.Clamp(border, 1, 8);
            var key = $"slantFrame:{height}:{slant}:{border}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var width = slant * 2 + 12;
            var texture = NewTexture(width, height);
            ForEachPixel(texture, p =>
            {
                var outer = SlantDistance(p, width, height, slant, 0f);
                var inner = SlantDistance(p, width, height, slant, border);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(UiShapes.Subtract(outer, inner)));
            });

            var inset = slant + 5;
            return Store(key, texture, new Vector4(inset, 0f, inset, 0f));
        }

        /// <summary>
        /// Signed distance to the sheared slab. The shear is applied to the sample point
        /// rather than to the box, which keeps the top and bottom edges exactly horizontal —
        /// rotating the box instead would lean those too and the slab would read as a
        /// crooked rectangle rather than a deliberate one.
        /// </summary>
        private static float SlantDistance(Vector2 p, int width, int height, int slant, float inset)
        {
            var centre = new Vector2(width * 0.5f, height * 0.5f);
            var shear = slant / Mathf.Max(1f, height - 1f);
            var q = new Vector2(p.x - shear * (p.y - centre.y), p.y);
            var half = new Vector2(
                width * 0.5f - slant * 0.5f - 1f - inset,
                height * 0.5f - 1f - inset);
            return UiShapes.RoundedBox(q, centre, half, 1f);
        }

        // -------------------------------------------------------------- device art

        /// <summary>
        /// Horizontal scanline strip for the scanner screen. Tiled vertically by an Image in
        /// Tiled mode; the duty cycle is deliberately low so it darkens rather than stripes.
        /// </summary>
        public static Sprite Scanlines(int period = 4)
        {
            period = Mathf.Clamp(period, 2, 16);
            var key = "scanlines:" + period;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(4, period);
            for (var y = 0; y < period; y++)
            {
                // One dark row per period, softened by a half-strength neighbour.
                var alpha = y == 0 ? 0.30f : (y == 1 ? 0.12f : 0f);
                for (var x = 0; x < 4; x++) texture.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
            }
            texture.Apply(false, false);
            texture.wrapMode = TextureWrapMode.Repeat;
            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// Radial vignette that darkens the screen edges — the single cheapest cue that the
        /// readout is behind curved glass rather than painted on the HUD.
        /// </summary>
        public static Sprite Vignette(int size = 128, float strength = 0.75f)
        {
            var key = $"vignette:{size}:{strength:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var maxDistance = centre.magnitude;

            ForEachPixel(texture, p =>
            {
                var t = Mathf.Clamp01((p - centre).magnitude / maxDistance);
                return new Color(0f, 0f, 0f, Mathf.Pow(t, 2.4f) * strength);
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// Hex-mesh screen texture. Sits at very low alpha under the readout so the glass has
        /// a physical grain and the flat colour fields never look like a web page.
        /// </summary>
        public static Sprite HexGrid(int cell = 24)
        {
            cell = Mathf.Clamp(cell, 8, 64);
            var key = "hexgrid:" + cell;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            // A hex lattice tiles on a rectangle of width sqrt(3)*r and height 3*r.
            var r = cell * 0.5f;
            var width = Mathf.RoundToInt(Mathf.Sqrt(3f) * r);
            var height = Mathf.RoundToInt(3f * r);
            var texture = NewTexture(Mathf.Max(4, width), Mathf.Max(4, height));

            ForEachPixelTiling(texture, (p, w, h) =>
            {
                var best = float.MaxValue;
                // Evaluate the three lattice neighbours that can own this pixel.
                for (var ox = -1; ox <= 1; ox++)
                for (var oy = -1; oy <= 1; oy++)
                {
                    var centre = new Vector2(
                        w * 0.5f + ox * w,
                        h * 0.5f + oy * h * 0.5f + (Mathf.Abs(oy) % 2 == 1 ? w * 0.5f : 0f));
                    best = Mathf.Min(best, Mathf.Abs(UiShapes.Polygon(p, centre, r * 0.92f, 6, Mathf.PI / 6f)));
                }
                return new Color(1f, 1f, 1f, UiShapes.Coverage(best - 0.5f, 1.4f) * 0.5f);
            });

            texture.wrapMode = TextureWrapMode.Repeat;
            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// Corner bracket, drawn once and mirrored by scale at the four corners of the
        /// scanner's target reticle.
        /// </summary>
        public static Sprite Bracket(int size = 48, int thickness = 4, float armFraction = 0.45f)
        {
            var key = $"bracket:{size}:{thickness}:{armFraction:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var arm = size * Mathf.Clamp01(armFraction);
            var origin = new Vector2(thickness * 0.5f + 1f, size - thickness * 0.5f - 1f);

            ForEachPixel(texture, p =>
            {
                var horizontal = UiShapes.Segment(p, origin, origin + Vector2.right * arm, thickness);
                var vertical = UiShapes.Segment(p, origin, origin + Vector2.down * arm, thickness);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(UiShapes.Union(horizontal, vertical)));
            });

            return Store(key, texture, Vector4.zero);
        }

        // ---------------------------------------------------------------- glyphs

        /// <summary>Solid triangular chevron pointing up. Rotate the RectTransform for the other directions.</summary>
        public static Sprite Chevron(int size = 32, bool filled = true, int thickness = 5)
        {
            var key = $"chevron:{size}:{filled}:{thickness}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var a = new Vector2(size * 0.12f, size * 0.30f);
            var b = new Vector2(size * 0.50f, size * 0.80f);
            var c = new Vector2(size * 0.88f, size * 0.30f);

            ForEachPixel(texture, p =>
            {
                float d;
                if (filled)
                {
                    d = UiShapes.Triangle(p, a, b, c);
                }
                else
                {
                    d = UiShapes.Union(
                        UiShapes.Segment(p, a, b, thickness),
                        UiShapes.Segment(p, b, c, thickness));
                }
                return new Color(1f, 1f, 1f, UiShapes.Coverage(d));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>Circular dot, used for list bullets and the confidence indicator.</summary>
        public static Sprite Dot(int size = 16)
        {
            var key = "dot:" + size;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            ForEachPixel(texture, p => new Color(1f, 1f, 1f, UiShapes.Coverage(UiShapes.Circle(p, centre, size * 0.5f - 1f))));
            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// Circular progress track as a full ring. Paired with an Image in Filled/Radial360
        /// mode it becomes the scanner's confidence dial without a second texture.
        /// </summary>
        public static Sprite RingSprite(int size = 128, float thicknessFraction = 0.12f)
        {
            var key = $"ring:{size}:{thicknessFraction:0.000}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var thickness = size * thicknessFraction;
            var radius = size * 0.5f - thickness * 0.5f - 1f;

            ForEachPixel(texture, p => new Color(1f, 1f, 1f, UiShapes.Coverage(UiShapes.Ring(p, centre, radius, thickness))));
            return Store(key, texture, Vector4.zero);
        }

        /// <summary>Warning triangle with a cut-out bang. Used by the low-confidence badge and lethal threats.</summary>
        public static Sprite WarningGlyph(int size = 32)
        {
            var key = "warn:" + size;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var s = size;
            var a = new Vector2(s * 0.06f, s * 0.14f);
            var b = new Vector2(s * 0.50f, s * 0.92f);
            var c = new Vector2(s * 0.94f, s * 0.14f);

            ForEachPixel(texture, p =>
            {
                var body = UiShapes.Triangle(p, a, b, c);
                var stem = UiShapes.RoundedBox(p, new Vector2(s * 0.5f, s * 0.50f), new Vector2(s * 0.045f, s * 0.16f), s * 0.04f);
                var dot = UiShapes.Circle(p, new Vector2(s * 0.5f, s * 0.26f), s * 0.055f);
                var bang = UiShapes.Union(stem, dot);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(UiShapes.Subtract(body, bang)));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>Rounded X, for close buttons and the "clear" affordance on threat rows.</summary>
        public static Sprite CrossGlyph(int size = 32, float thickness = 4f)
        {
            var key = $"cross:{size}:{thickness:0.0}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var lo = size * 0.24f;
            var hi = size * 0.76f;

            ForEachPixel(texture, p =>
            {
                var d = UiShapes.Union(
                    UiShapes.Segment(p, new Vector2(lo, lo), new Vector2(hi, hi), thickness),
                    UiShapes.Segment(p, new Vector2(lo, hi), new Vector2(hi, lo), thickness));
                return new Color(1f, 1f, 1f, UiShapes.Coverage(d));
            });

            return Store(key, texture, Vector4.zero);
        }

        // ------------------------------------------------------------------ joy art

        /// <summary>
        /// A Poké Ball drawn as line art in alpha: outer rim, equator band, and the button's
        /// two rings. One tint gives the whole thing, so it works as a huge translucent
        /// watermark behind a title, as a 32px bullet, and as the cursor beside a menu row.
        ///
        /// Alpha-only rather than a coloured ball on purpose. The coloured ball is a composite
        /// of five images (<see cref="Disc"/> plus a radial fill) because it has to be taken
        /// apart and animated; this is the flat mark, and a mark that carries its own colours
        /// cannot be recoloured to sit on whatever the screen behind it happens to be.
        /// </summary>
        public static Sprite BallGlyph(int size = 256, int stroke = 12)
        {
            size = Mathf.Clamp(size, 16, 512);
            stroke = Mathf.Clamp(stroke, 1, size / 6);
            var key = $"ballGlyph:{size}:{stroke}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var outer = size * 0.5f - stroke * 0.5f - 1f;
            var buttonOuter = size * 0.17f;
            var buttonInner = size * 0.085f;

            ForEachPixel(texture, p =>
            {
                var rim = UiShapes.Ring(p, centre, outer, stroke);
                // The band is a full-width bar intersected with the ball's disc, which is what
                // keeps its ends flush with the rim instead of poking through it.
                var bar = UiShapes.RoundedBox(p, centre, new Vector2(size, stroke * 0.5f), 0f);
                var band = UiShapes.Intersect(bar, UiShapes.Circle(p, centre, outer + stroke * 0.5f));
                var ring = UiShapes.Ring(p, centre, buttonOuter, stroke);
                var pip = UiShapes.Circle(p, centre, buttonInner);
                var d = UiShapes.Union(UiShapes.Union(rim, band), UiShapes.Union(ring, pip));
                return new Color(1f, 1f, 1f, UiShapes.Coverage(d));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// A radial glow: opaque at the centre, gone at the edge, with no hard rim anywhere.
        ///
        /// <paramref name="gamma"/> is the whole character of it — 1 is a flat cone that reads
        /// as a disc with soft edges, 3 is a tight core with a long haze, which is what a
        /// burst of light actually looks like. Not nine-sliced; see <see cref="Disc"/>.
        /// </summary>
        public static Sprite Glow(int size = 256, float gamma = 2.4f)
        {
            size = Mathf.Clamp(size, 8, 512);
            var key = $"glow:{size}:{gamma:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var radius = size * 0.5f - 1f;

            ForEachPixel(texture, p =>
            {
                var t = Mathf.Clamp01(1f - (p - centre).magnitude / radius);
                return new Color(1f, 1f, 1f, Mathf.Pow(t, gamma));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// A spiked star. <paramref name="points"/> spikes reaching the edge, with the valleys
        /// at <paramref name="innerFraction"/> of the radius.
        ///
        /// Scaled up and spun slowly behind a gacha payoff it is the cartoon "ta-daa"; at eight
        /// points and a deep valley it is a sunburst. The distance is polar rather than
        /// Euclidean, so the antialiasing is approximate — acceptable because this shape is
        /// only ever drawn large and moving.
        /// </summary>
        public static Sprite Starburst(int size = 256, int points = 8, float innerFraction = 0.46f)
        {
            size = Mathf.Clamp(size, 16, 512);
            points = Mathf.Clamp(points, 3, 32);
            innerFraction = Mathf.Clamp(innerFraction, 0.05f, 0.95f);
            var key = $"starburst:{size}:{points}:{innerFraction:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var radius = size * 0.5f - 1f;
            var inner = radius * innerFraction;

            ForEachPixel(texture, p =>
            {
                var v = p - centre;
                var angle = Mathf.Atan2(v.y, v.x);
                // Triangular wave over one spike period: 0 at a tip, 1 in a valley.
                var phase = angle * points / (Mathf.PI * 2f);
                var saw = phase - Mathf.Floor(phase);
                var tri = Mathf.Abs(saw * 2f - 1f);
                var bound = Mathf.Lerp(radius, inner, tri);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(v.magnitude - bound));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// A four-point twinkle with concave sides — the sparkle that flies off a rare pull.
        ///
        /// Concave is the entire difference between a sparkle and a plus sign: the sides curve
        /// inward as a power of the angle, so the arms taper to needle points.
        /// </summary>
        public static Sprite Sparkle(int size = 64, float sharpness = 2.6f)
        {
            size = Mathf.Clamp(size, 8, 256);
            var key = $"sparkle:{size}:{sharpness:0.0}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var radius = size * 0.5f - 1f;

            ForEachPixel(texture, p =>
            {
                var v = p - centre;
                var angle = Mathf.Atan2(v.y, v.x);
                var phase = angle * 4f / (Mathf.PI * 2f);
                var saw = phase - Mathf.Floor(phase);
                var tri = Mathf.Abs(saw * 2f - 1f);
                var bound = radius * Mathf.Pow(1f - tri, sharpness) + radius * 0.04f;
                return new Color(1f, 1f, 1f, UiShapes.Coverage(v.magnitude - bound));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// Radial speed lines: wedges thrown outward from the centre, narrow at the middle and
        /// wide at the rim, fading in over the inner hole.
        ///
        /// Spun behind a reveal it is the one effect that turns a scaling image into an
        /// arrival. The hole matters: without it the spokes converge into a solid blob exactly
        /// where the subject has to be readable.
        /// </summary>
        public static Sprite SpeedLines(int size = 256, int spokes = 24, float holeFraction = 0.22f)
        {
            size = Mathf.Clamp(size, 32, 512);
            spokes = Mathf.Clamp(spokes, 4, 64);
            holeFraction = Mathf.Clamp(holeFraction, 0.02f, 0.9f);
            var key = $"speedlines:{size}:{spokes}:{holeFraction:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var radius = size * 0.5f - 1f;
            var hole = radius * holeFraction;

            ForEachPixel(texture, p =>
            {
                var v = p - centre;
                var r = v.magnitude;
                if (r >= radius) return new Color(1f, 1f, 1f, 0f);

                var angle = Mathf.Atan2(v.y, v.x);
                var phase = angle * spokes / (Mathf.PI * 2f);
                var saw = phase - Mathf.Floor(phase);
                var tri = Mathf.Abs(saw * 2f - 1f);

                // The wedge takes a fixed angular share; the arc length that share covers grows
                // with radius, which is what makes the spokes fan out.
                var inside = Mathf.Clamp01((0.45f - tri) * spokes * 0.5f);
                var fade = Mathf.Clamp01((r - hole) / Mathf.Max(1f, radius - hole));
                return new Color(1f, 1f, 1f, inside * fade * (1f - fade * fade * 0.35f));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// A cloud: four lobes smooth-unioned onto a flat base, so the top is bumpy and the
        /// bottom is a straight line. Drawn once and reused at several scales, which is what
        /// stops three clouds in the same sky from being three copies of one silhouette.
        /// </summary>
        public static Sprite CloudPuff(int width = 256, int seed = 0)
        {
            width = Mathf.Clamp(width, 32, 512);
            var key = $"cloud:{width}:{seed}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var height = Mathf.Max(16, width / 2);
            var texture = NewTexture(width, height);

            // Deterministic per-seed lobe layout: same seed, same cloud, every run.
            var rng = new System.Random(seed * 977 + 13);
            var count = 4 + rng.Next(0, 2);
            var lobes = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var t = count == 1 ? 0.5f : i / (float)(count - 1);
                var x = Mathf.Lerp(width * 0.18f, width * 0.82f, t);
                var bell = Mathf.Sin(t * Mathf.PI);
                var r = height * (0.30f + 0.26f * bell) * (0.85f + (float)rng.NextDouble() * 0.3f);
                var y = height * 0.42f + bell * height * 0.14f;
                lobes[i] = new Vector3(x, y, r);
            }
            var baseHalf = new Vector2(width * 0.34f, height * 0.16f);
            var baseCentre = new Vector2(width * 0.5f, height * 0.40f);

            ForEachPixel(texture, p =>
            {
                var d = UiShapes.RoundedBox(p, baseCentre, baseHalf, height * 0.14f);
                for (var i = 0; i < lobes.Length; i++)
                {
                    d = UiShapes.SmoothUnion(d, UiShapes.Circle(p, new Vector2(lobes[i].x, lobes[i].y), lobes[i].z),
                                             height * 0.16f);
                }
                // Flat bottom: everything below the base line is cut away.
                var floorPlane = height * 0.245f - p.y;
                d = UiShapes.Subtract(d, floorPlane);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(d));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// Seamless 45-degree candy stripes. The texture is square with side
        /// <paramref name="period"/>, which is the only size at which <c>x + y</c> wraps
        /// cleanly in both axes — any other aspect and the tiling shows a seam every repeat.
        /// Draw it with <see cref="UnityEngine.UI.Image.Type.Tiled"/>.
        /// </summary>
        public static Sprite DiagonalStripes(int period = 24, float duty = 0.5f)
        {
            period = Mathf.Clamp(period, 4, 128);
            duty = Mathf.Clamp(duty, 0.05f, 0.95f);
            var key = $"stripes:{period}:{duty:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(period, period);
            var band = period * duty;

            ForEachPixel(texture, p =>
            {
                var s = Mathf.Repeat(p.x + p.y, period);
                // Distance to the nearest band edge, so the stripe antialiases like every
                // other shape here instead of stair-stepping.
                var d = Mathf.Min(s, band - s);
                return new Color(1f, 1f, 1f, UiShapes.Coverage(-d));
            });

            texture.wrapMode = TextureWrapMode.Repeat;
            return Store(key, texture, Vector4.zero);
        }

        /// <summary>Seamless polka dots on a square lattice. Tiled, at low alpha, it gives a flat card a printed grain.</summary>
        public static Sprite Dots(int cell = 32, float radiusFraction = 0.18f)
        {
            cell = Mathf.Clamp(cell, 6, 128);
            radiusFraction = Mathf.Clamp(radiusFraction, 0.02f, 0.49f);
            var key = $"dots:{cell}:{radiusFraction:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(cell, cell);
            var centre = new Vector2(cell * 0.5f, cell * 0.5f);
            var r = cell * radiusFraction;

            ForEachPixel(texture, p => new Color(1f, 1f, 1f, UiShapes.Coverage(UiShapes.Circle(p, centre, r))));
            texture.wrapMode = TextureWrapMode.Repeat;
            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// A fat right-pointing arrowhead with rounded corners — the menu cursor.
        ///
        /// Rounded rather than sharp because a needle-sharp triangle at 40px reads as a
        /// UI chevron, and this one has to read as a thing that was drawn by hand.
        /// </summary>
        public static Sprite ArrowGlyph(int size = 48, float round = 5f)
        {
            size = Mathf.Clamp(size, 8, 256);
            var key = $"arrow:{size}:{round:0.0}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var inset = round + 1f;
            var a = new Vector2(inset, inset);
            var b = new Vector2(size - inset, size * 0.5f);
            var c = new Vector2(inset, size - inset);

            ForEachPixel(texture, p =>
                new Color(1f, 1f, 1f, UiShapes.Coverage(UiShapes.Triangle(p, a, b, c) - round)));

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// A ribbon: a horizontal bar with a V cut out of each end. Nine-sliced across its
        /// width only, so one sprite serves a 200px rarity banner and a 900px header at the
        /// same notch depth.
        /// </summary>
        public static Sprite Banner(int height = 56, int notch = 20)
        {
            height = Mathf.Clamp(height, 8, 200);
            notch = Mathf.Clamp(notch, 0, height);
            var key = $"banner:{height}:{notch}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var width = notch * 2 + 16;
            var texture = NewTexture(width, height);
            var half = new Vector2(width * 0.5f, height * 0.5f - 1f);
            var centre = new Vector2(width * 0.5f, height * 0.5f);

            ForEachPixel(texture, p =>
            {
                var body = UiShapes.RoundedBox(p, centre, half, 1f);
                if (notch > 0)
                {
                    var left = UiShapes.Triangle(p,
                        new Vector2(-1f, -1f), new Vector2(notch, height * 0.5f), new Vector2(-1f, height + 1f));
                    var right = UiShapes.Triangle(p,
                        new Vector2(width + 1f, -1f), new Vector2(width - notch, height * 0.5f), new Vector2(width + 1f, height + 1f));
                    body = UiShapes.Subtract(body, UiShapes.Union(left, right));
                }
                return new Color(1f, 1f, 1f, UiShapes.Coverage(body));
            });

            var inset = notch + 6;
            return Store(key, texture, new Vector4(inset, 0f, inset, 0f));
        }

        /// <summary>
        /// A rounded slab whose top half carries a bright gloss and whose bottom edge is
        /// slightly darkened — the moulded-plastic look, in one nine-sliceable sprite.
        ///
        /// Drawn as alpha so a single white Image over the slab's fill produces the highlight
        /// and the tint underneath still authors the colour. Nine-slices horizontally only:
        /// the gradient runs vertically, so the middle row must not be stretched, which is why
        /// the top and bottom insets are zero and the sprite is generated at the height it is
        /// used at.
        /// </summary>
        public static Sprite Gloss(int height = 64, int radius = 18, float coverFraction = 0.46f)
        {
            height = Mathf.Clamp(height, 8, 256);
            radius = Mathf.Clamp(radius, 2, height / 2);
            coverFraction = Mathf.Clamp(coverFraction, 0.1f, 0.9f);
            var key = $"gloss:{height}:{radius}:{coverFraction:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var width = radius * 2 + 8;
            var texture = NewTexture(width, height);
            var centre = new Vector2(width * 0.5f, height * 0.5f);
            var half = new Vector2(width * 0.5f - 1f, height * 0.5f - 1f);
            var top = height * (1f - coverFraction);

            ForEachPixel(texture, p =>
            {
                var shape = UiShapes.Coverage(UiShapes.RoundedBox(p, centre, half, radius));
                // Row 0 is the bottom in texture space; the gloss lives at the top.
                var t = Mathf.Clamp01((p.y - top) / Mathf.Max(1f, height - top));
                return new Color(1f, 1f, 1f, shape * t * t);
            });

            var inset = radius + 3;
            return Store(key, texture, new Vector4(inset, 0f, inset, 0f));
        }

        /// <summary>
        /// The list cursor: a solid arrowhead with a bar standing behind it, pointing right.
        ///
        /// The bar is what makes it read as a cursor rather than as a play button — a bare
        /// triangle beside a menu row is an expander, and this one has to say "you are here".
        /// </summary>
        public static Sprite ChevronCursor(int size = 64, float round = 3f)
        {
            size = Mathf.Clamp(size, 12, 256);
            var key = $"cursor:{size}:{round:0.0}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var centre = new Vector2(size * 0.5f, size * 0.5f);
            var barCentre = new Vector2(size * 0.16f, centre.y);
            var barHalf = new Vector2(size * 0.075f, size * 0.34f);

            var a = new Vector2(size * 0.36f, size * 0.13f);
            var b = new Vector2(size * 0.94f, centre.y);
            var c = new Vector2(size * 0.36f, size * 0.87f);

            ForEachPixel(texture, p =>
            {
                var bar = UiShapes.RoundedBox(p, barCentre, barHalf, size * 0.06f);
                var head = UiShapes.Triangle(p, a, b, c) - round;
                return new Color(1f, 1f, 1f, UiShapes.Coverage(UiShapes.Union(bar, head)));
            });

            return Store(key, texture, Vector4.zero);
        }

        /// <summary>
        /// A stack of rounded horizontal bars — the "menu" mark that sits beside a screen
        /// title. <paramref name="bars"/> of them, evenly spread, the top one short.
        /// </summary>
        public static Sprite BarsGlyph(int size = 64, int bars = 3, float thickness = 0.13f)
        {
            size = Mathf.Clamp(size, 12, 256);
            bars = Mathf.Clamp(bars, 2, 6);
            var key = $"bars:{size}:{bars}:{thickness:0.00}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var texture = NewTexture(size, size);
            var half = size * thickness * 0.5f;
            var span = size * 0.74f;
            var top = size * 0.87f;
            var step = bars > 1 ? span / (bars - 1) : 0f;

            ForEachPixel(texture, p =>
            {
                var best = float.MaxValue;
                for (var i = 0; i < bars; i++)
                {
                    // The first bar is shorter, which is the detail that stops the mark
                    // reading as a hamburger button.
                    var width = i == 0 ? size * 0.30f : size * 0.40f;
                    var centre = new Vector2(size * 0.12f + width, top - i * step);
                    best = Mathf.Min(best, UiShapes.RoundedBox(p, centre, new Vector2(width, half), half));
                }
                return new Color(1f, 1f, 1f, UiShapes.Coverage(best));
            });

            return Store(key, texture, Vector4.zero);
        }

        // --------------------------------------------------------------- plumbing

        private static Texture2D NewTexture(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                name = "PokeLabUiGenerated",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            return texture;
        }

        private static void ForEachPixel(Texture2D texture, System.Func<Vector2, Color> shade)
        {
            var w = texture.width;
            var h = texture.height;
            var pixels = new Color[w * h];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                // Sample at the pixel centre so symmetric shapes stay symmetric.
                pixels[y * w + x] = shade(new Vector2(x + 0.5f, y + 0.5f));
            }
            texture.SetPixels(pixels);
            texture.Apply(false, false);
        }

        private static void ForEachPixelTiling(Texture2D texture, System.Func<Vector2, float, float, Color> shade)
        {
            var w = texture.width;
            var h = texture.height;
            var pixels = new Color[w * h];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                pixels[y * w + x] = shade(new Vector2(x + 0.5f, y + 0.5f), w, h);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, false);
        }

        private static Sprite Store(string key, Texture2D texture, Vector4 border)
        {
            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                Ppu,
                0,
                SpriteMeshType.FullRect,
                border);
            sprite.name = key;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Cache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Drops every generated texture. Called on play mode exit — without it the cache
        /// survives domain reload with dead texture pointers behind live Sprite objects.
        /// </summary>
        public static void Reset()
        {
            foreach (var kvp in Cache)
            {
                if (kvp.Value == null) continue;
                var texture = kvp.Value.texture;
                Destroy(kvp.Value);
                if (texture != null) Destroy(texture);
            }
            Cache.Clear();
        }

        /// <summary>
        /// Destroys a generated object with the right call for the context. DestroyImmediate
        /// is required outside play mode (Destroy is deferred to a frame that never comes),
        /// and forbidden during it.
        /// </summary>
        internal static void Destroy(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }
    }
}
