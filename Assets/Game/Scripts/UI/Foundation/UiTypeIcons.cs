using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// One procedurally drawn glyph per <see cref="ElementType"/>.
    ///
    /// These are deliberately abstract marks rather than illustrations: at badge size
    /// (20-28px) an illustration turns to mush, and a set of eighteen geometric marks
    /// authored against one stroke weight and one optical margin reads as a designed
    /// icon family. Every glyph is composed from <see cref="UiShapes"/> in a normalised
    /// 0-1 square, so they stay crisp at any requested size.
    /// </summary>
    public static class UiTypeIcons
    {
        private static readonly Dictionary<int, Sprite> Cache = new Dictionary<int, Sprite>(32);

        /// <summary>Glyph for a type. Tint the Image with <see cref="UiPalette.Type"/> at the call site.</summary>
        public static Sprite Get(ElementType type, int size = 40)
        {
            size = Mathf.Clamp(size, 12, 256);
            var key = ((int)type + 2) * 1000 + size;
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "TypeIcon_" + type,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color[size * size];
            var s = (float)size;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var d = Distance(type, p, s);
                pixels[y * size + x] = new Color(1f, 1f, 1f, UiShapes.Coverage(d));
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);

            var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "TypeIcon_" + type;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Cache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Signed distance for one glyph. <paramref name="p"/> is in pixels, <paramref name="s"/>
        /// is the icon edge length — every constant below is a fraction of it.
        /// </summary>
        private static float Distance(ElementType type, Vector2 p, float s)
        {
            var c = new Vector2(s * 0.5f, s * 0.5f);

            switch (type)
            {
                case ElementType.Normal:
                    // Neutral mark: a ring with a centred dot. Reads as "no special property".
                    return UiShapes.Union(
                        UiShapes.Ring(p, c, s * 0.30f, s * 0.09f),
                        UiShapes.Circle(p, c, s * 0.10f));

                case ElementType.Fire:
                {
                    // Flame: a round base smoothly fused to a leaning tip.
                    var bulb = UiShapes.Circle(p, new Vector2(s * 0.5f, s * 0.34f), s * 0.24f);
                    var tip = UiShapes.Triangle(p,
                        new Vector2(s * 0.28f, s * 0.42f),
                        new Vector2(s * 0.58f, s * 0.92f),
                        new Vector2(s * 0.72f, s * 0.40f));
                    var lick = UiShapes.Circle(p, new Vector2(s * 0.38f, s * 0.52f), s * 0.11f);
                    return UiShapes.SmoothUnion(UiShapes.SmoothUnion(bulb, tip, s * 0.10f), lick, s * 0.08f);
                }

                case ElementType.Water:
                {
                    // Droplet: circle fused to a point above it.
                    var bulb = UiShapes.Circle(p, new Vector2(s * 0.5f, s * 0.34f), s * 0.25f);
                    var point = UiShapes.Triangle(p,
                        new Vector2(s * 0.30f, s * 0.40f),
                        new Vector2(s * 0.50f, s * 0.90f),
                        new Vector2(s * 0.70f, s * 0.40f));
                    return UiShapes.SmoothUnion(bulb, point, s * 0.06f);
                }

                case ElementType.Electric:
                {
                    // Bolt: two offset wedges forming a zigzag.
                    var upper = UiShapes.Triangle(p,
                        new Vector2(s * 0.58f, s * 0.92f),
                        new Vector2(s * 0.24f, s * 0.46f),
                        new Vector2(s * 0.56f, s * 0.46f));
                    var lower = UiShapes.Triangle(p,
                        new Vector2(s * 0.42f, s * 0.08f),
                        new Vector2(s * 0.76f, s * 0.54f),
                        new Vector2(s * 0.44f, s * 0.54f));
                    return UiShapes.Union(upper, lower);
                }

                case ElementType.Grass:
                {
                    // Leaf: the lens where two circles overlap, tilted, with a midrib cut out.
                    var q = UiShapes.Rotate(p, c, -Mathf.PI * 0.25f);
                    var lens = UiShapes.Intersect(
                        UiShapes.Circle(q, c + new Vector2(-s * 0.22f, 0f), s * 0.46f),
                        UiShapes.Circle(q, c + new Vector2(s * 0.22f, 0f), s * 0.46f));
                    var rib = UiShapes.Segment(q, c + new Vector2(0f, -s * 0.30f), c + new Vector2(0f, s * 0.30f), s * 0.055f);
                    return UiShapes.Subtract(lens, rib);
                }

                case ElementType.Ice:
                {
                    // Snowflake: three crossing spokes with barbs at two thirds out.
                    var d = float.MaxValue;
                    for (var i = 0; i < 3; i++)
                    {
                        var angle = Mathf.PI * i / 3f;
                        var dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                        d = UiShapes.Union(d, UiShapes.Segment(p, c - dir * s * 0.38f, c + dir * s * 0.38f, s * 0.075f));

                        var barbBase = c + dir * s * 0.24f;
                        var perp = new Vector2(-dir.y, dir.x);
                        d = UiShapes.Union(d, UiShapes.Segment(p, barbBase, barbBase + (dir + perp).normalized * s * 0.13f, s * 0.06f));
                        d = UiShapes.Union(d, UiShapes.Segment(p, barbBase, barbBase + (dir - perp).normalized * s * 0.13f, s * 0.06f));
                    }
                    return d;
                }

                case ElementType.Fighting:
                {
                    // Fist: a rounded block with three knuckles and a thumb notch.
                    var palm = UiShapes.RoundedBox(p, new Vector2(s * 0.50f, s * 0.40f),
                        new Vector2(s * 0.27f, s * 0.24f), s * 0.09f);
                    var knuckles = float.MaxValue;
                    for (var i = 0; i < 3; i++)
                    {
                        var x = s * (0.32f + i * 0.18f);
                        knuckles = UiShapes.Union(knuckles, UiShapes.Circle(p, new Vector2(x, s * 0.63f), s * 0.095f));
                    }
                    var thumb = UiShapes.Circle(p, new Vector2(s * 0.20f, s * 0.44f), s * 0.10f);
                    return UiShapes.SmoothUnion(UiShapes.SmoothUnion(palm, knuckles, s * 0.04f), thumb, s * 0.04f);
                }

                case ElementType.Poison:
                {
                    // Rising bubbles — one large, two trailing.
                    var big = UiShapes.Circle(p, new Vector2(s * 0.44f, s * 0.36f), s * 0.24f);
                    var mid = UiShapes.Circle(p, new Vector2(s * 0.68f, s * 0.62f), s * 0.13f);
                    var small = UiShapes.Circle(p, new Vector2(s * 0.54f, s * 0.78f), s * 0.075f);
                    return UiShapes.Union(UiShapes.Union(big, mid), small);
                }

                case ElementType.Ground:
                {
                    // Strata: three stacked bars of decreasing width, like a soil cross-section.
                    var a = UiShapes.RoundedBox(p, new Vector2(s * 0.50f, s * 0.68f), new Vector2(s * 0.34f, s * 0.075f), s * 0.075f);
                    var b = UiShapes.RoundedBox(p, new Vector2(s * 0.46f, s * 0.47f), new Vector2(s * 0.28f, s * 0.075f), s * 0.075f);
                    var d = UiShapes.RoundedBox(p, new Vector2(s * 0.52f, s * 0.26f), new Vector2(s * 0.20f, s * 0.075f), s * 0.075f);
                    return UiShapes.Union(UiShapes.Union(a, b), d);
                }

                case ElementType.Flying:
                {
                    // Wing: a crescent carved from a disc, plus a trailing feather.
                    var crescent = UiShapes.Subtract(
                        UiShapes.Circle(p, new Vector2(s * 0.42f, s * 0.46f), s * 0.38f),
                        UiShapes.Circle(p, new Vector2(s * 0.30f, s * 0.20f), s * 0.40f));
                    var feather = UiShapes.Segment(p, new Vector2(s * 0.60f, s * 0.30f), new Vector2(s * 0.86f, s * 0.62f), s * 0.10f);
                    return UiShapes.SmoothUnion(crescent, feather, s * 0.05f);
                }

                case ElementType.Psychic:
                {
                    // Concentric orbit: outer ring, inner ring, core.
                    return UiShapes.Union(UiShapes.Union(
                            UiShapes.Ring(p, c, s * 0.38f, s * 0.055f),
                            UiShapes.Ring(p, c, s * 0.24f, s * 0.055f)),
                        UiShapes.Circle(p, c, s * 0.09f));
                }

                case ElementType.Bug:
                {
                    // Beetle: a segmented oval with two antennae.
                    var stretched = new Vector2(p.x, c.y + (p.y - c.y) * 0.72f);
                    var body = UiShapes.Circle(stretched, new Vector2(s * 0.5f, s * 0.40f), s * 0.26f);
                    var split = UiShapes.Segment(p, new Vector2(s * 0.5f, s * 0.08f), new Vector2(s * 0.5f, s * 0.62f), s * 0.05f);
                    var head = UiShapes.Circle(p, new Vector2(s * 0.5f, s * 0.70f), s * 0.11f);
                    var left = UiShapes.Segment(p, new Vector2(s * 0.44f, s * 0.76f), new Vector2(s * 0.28f, s * 0.92f), s * 0.055f);
                    var right = UiShapes.Segment(p, new Vector2(s * 0.56f, s * 0.76f), new Vector2(s * 0.72f, s * 0.92f), s * 0.055f);
                    return UiShapes.Union(UiShapes.Union(UiShapes.Subtract(body, split), head), UiShapes.Union(left, right));
                }

                case ElementType.Rock:
                {
                    // Boulder: a rotated pentagon with a chip taken out of one shoulder.
                    var stone = UiShapes.Polygon(p, new Vector2(s * 0.5f, s * 0.46f), s * 0.38f, 5, Mathf.PI * 0.1f);
                    var chip = UiShapes.Circle(p, new Vector2(s * 0.80f, s * 0.76f), s * 0.16f);
                    return UiShapes.Subtract(stone, chip);
                }

                case ElementType.Ghost:
                {
                    // Sheet: domed top, flat sides, three scallops bitten out of the hem.
                    var dome = UiShapes.Circle(p, new Vector2(s * 0.5f, s * 0.58f), s * 0.30f);
                    var skirt = UiShapes.RoundedBox(p, new Vector2(s * 0.5f, s * 0.38f), new Vector2(s * 0.30f, s * 0.22f), s * 0.04f);
                    var body = UiShapes.Union(dome, skirt);
                    var hem = float.MaxValue;
                    for (var i = 0; i < 3; i++)
                    {
                        hem = UiShapes.Union(hem, UiShapes.Circle(p, new Vector2(s * (0.28f + i * 0.22f), s * 0.15f), s * 0.11f));
                    }
                    return UiShapes.Subtract(body, hem);
                }

                case ElementType.Dragon:
                {
                    // Claw: three tapering talons fanning from a common root.
                    var root = new Vector2(s * 0.5f, s * 0.12f);
                    var d = float.MaxValue;
                    for (var i = 0; i < 3; i++)
                    {
                        var angle = Mathf.PI * (0.5f + (i - 1) * 0.24f);
                        var tip = root + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * s * 0.74f;
                        var mid = Vector2.Lerp(root, tip, 0.55f) + new Vector2((i - 1) * s * 0.06f, 0f);
                        d = UiShapes.Union(d, UiShapes.Segment(p, root, mid, s * 0.105f));
                        d = UiShapes.Union(d, UiShapes.Segment(p, mid, tip, s * 0.055f));
                    }
                    return d;
                }

                case ElementType.Dark:
                {
                    // Crescent: a disc with an offset disc removed.
                    return UiShapes.Subtract(
                        UiShapes.Circle(p, c, s * 0.36f),
                        UiShapes.Circle(p, c + new Vector2(s * 0.17f, s * 0.09f), s * 0.32f));
                }

                case ElementType.Steel:
                {
                    // Hex nut: hexagon with a bore through it.
                    return UiShapes.Subtract(
                        UiShapes.Polygon(p, c, s * 0.40f, 6, Mathf.PI / 6f),
                        UiShapes.Circle(p, c, s * 0.16f));
                }

                case ElementType.Fairy:
                {
                    // Five-petal bloom around a core.
                    var d = UiShapes.Circle(p, c, s * 0.12f);
                    for (var i = 0; i < 5; i++)
                    {
                        var angle = Mathf.PI * 0.5f + i * Mathf.PI * 2f / 5f;
                        var petal = c + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * s * 0.23f;
                        d = UiShapes.SmoothUnion(d, UiShapes.Circle(p, petal, s * 0.155f), s * 0.05f);
                    }
                    return d;
                }

                default:
                    // Unknown / None: a hollow question-neutral diamond, so it is obviously "no data".
                    return UiShapes.Subtract(
                        UiShapes.Polygon(p, c, s * 0.34f, 4, Mathf.PI * 0.25f),
                        UiShapes.Polygon(p, c, s * 0.22f, 4, Mathf.PI * 0.25f));
            }
        }

        /// <summary>Drops every generated icon texture. Paired with <see cref="UiSprites.Reset"/>.</summary>
        public static void Reset()
        {
            foreach (var kvp in Cache)
            {
                if (kvp.Value == null) continue;
                var texture = kvp.Value.texture;
                UiSprites.Destroy(kvp.Value);
                if (texture != null) UiSprites.Destroy(texture);
            }
            Cache.Clear();
        }
    }
}
