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
