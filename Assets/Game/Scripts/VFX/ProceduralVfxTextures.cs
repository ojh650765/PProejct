using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Every texture the VFX and shader library uses, generated in code.
    ///
    /// The brief forbids default Unity particle sprites, and this is the most
    /// robust way to honour it: nothing to import, no texture importer settings to
    /// get wrong, no binary assets to merge, and the results are deterministic on
    /// every machine. Textures are built once on first request and cached for the
    /// lifetime of the domain.
    ///
    /// Everything is authored as a signed-distance style field and then shaped, so
    /// shapes stay crisp at any resolution and read as drawn rather than as noise.
    /// Alpha carries the shape; RGB carries a subtle internal colour variation so
    /// an additive particle is not a flat disc of one colour.
    /// </summary>
    public static class ProceduralVfxTextures
    {
        public enum Kind
        {
            /// <summary>Radial glow. The workhorse: sparks, motes, impact flashes.</summary>
            SoftDot,
            /// <summary>Tight core with a wide halo. Reads as a light source.</summary>
            Glow,
            /// <summary>Vertically elongated streak for stretched-billboard sparks.</summary>
            Spark,
            /// <summary>Four-point star with thin diffraction spikes.</summary>
            Sparkle,
            /// <summary>Six-point flare for level-up and critical hits.</summary>
            Flare,
            /// <summary>Soft annulus. Shockwaves, aura ground rings, ripples.</summary>
            Ring,
            /// <summary>Hard double-edged ring for a sharp shockwave front.</summary>
            ShockRing,
            /// <summary>Billowing cloud with an eroded edge. Smoke, dust, mist.</summary>
            Smoke,
            /// <summary>Denser, rounder puff for steam and poison clouds.</summary>
            Puff,
            /// <summary>Tileable fbm, single channel in R. Flow and dissolve noise.</summary>
            NoiseTiling,
            /// <summary>Tileable water surface normal map.</summary>
            WaterNormal,
            /// <summary>Simple leaf silhouette for drifting foliage particles.</summary>
            Leaf,
            /// <summary>Flower petal, for pollen and grass-type effects.</summary>
            Petal,
            /// <summary>Teardrop, for rain and splash particles.</summary>
            Droplet,
            /// <summary>Crescent splash sheet for water impacts.</summary>
            Splash,
            /// <summary>Jagged bolt segment for Electric.</summary>
            Bolt,
            /// <summary>Flame tongue with a hot core.</summary>
            Flame,
            /// <summary>Angular shard for Rock, Ice and Steel impacts.</summary>
            Shard,
            /// <summary>Soft butterfly/bird silhouette for ambient wildlife.</summary>
            Wing,
            /// <summary>Radial streak burst, for cast telegraphs.</summary>
            Burst,
        }

        private const int DefaultSize = 128;

        private static readonly Dictionary<Kind, Texture2D> Cache = new Dictionary<Kind, Texture2D>();

        public static Texture2D Get(Kind kind)
        {
            if (Cache.TryGetValue(kind, out var cached) && cached != null) return cached;

            Texture2D tex = Build(kind);
            Cache[kind] = tex;
            return tex;
        }

        /// <summary>
        /// Drops every cached texture. Call from a play-mode-exit hook alongside
        /// ServiceHub.Reset so generated textures do not survive a domain reload as
        /// leaked native memory.
        /// </summary>
        public static void Clear()
        {
            foreach (var kv in Cache)
            {
                if (kv.Value == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(kv.Value);
                else UnityEngine.Object.DestroyImmediate(kv.Value);
            }
            Cache.Clear();
        }

        // ---------------------------------------------------------------------
        // Builders
        // ---------------------------------------------------------------------
        private static Texture2D Build(Kind kind)
        {
            int size = kind == Kind.NoiseTiling || kind == Kind.WaterNormal ? 256 : DefaultSize;
            var pixels = new Color32[size * size];

            switch (kind)
            {
                case Kind.SoftDot:    Fill(pixels, size, SoftDot); break;
                case Kind.Glow:       Fill(pixels, size, Glow); break;
                case Kind.Spark:      Fill(pixels, size, Spark); break;
                case Kind.Sparkle:    Fill(pixels, size, p => StarShape(p, 4, 0.055f, 0.42f)); break;
                case Kind.Flare:      Fill(pixels, size, p => StarShape(p, 6, 0.085f, 0.48f)); break;
                case Kind.Ring:       Fill(pixels, size, p => RingShape(p, 0.36f, 0.13f, 1.0f)); break;
                case Kind.ShockRing:  Fill(pixels, size, p => RingShape(p, 0.42f, 0.05f, 3.0f)); break;
                case Kind.Smoke:      Fill(pixels, size, p => Cloud(p, 3.1f, 0.42f, 0.65f)); break;
                case Kind.Puff:       Fill(pixels, size, p => Cloud(p, 1.9f, 0.30f, 0.85f)); break;
                case Kind.NoiseTiling: BuildTilingNoise(pixels, size); break;
                case Kind.WaterNormal: BuildWaterNormal(pixels, size); break;
                case Kind.Leaf:       Fill(pixels, size, Leaf); break;
                case Kind.Petal:      Fill(pixels, size, Petal); break;
                case Kind.Droplet:    Fill(pixels, size, Droplet); break;
                case Kind.Splash:     Fill(pixels, size, Splash); break;
                case Kind.Bolt:       Fill(pixels, size, Bolt); break;
                case Kind.Flame:      Fill(pixels, size, Flame); break;
                case Kind.Shard:      Fill(pixels, size, Shard); break;
                case Kind.Wing:       Fill(pixels, size, Wing); break;
                case Kind.Burst:      Fill(pixels, size, Burst); break;
                default:              Fill(pixels, size, SoftDot); break;
            }

            bool linear = kind == Kind.NoiseTiling || kind == Kind.WaterNormal;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, linear)
            {
                name = "PL_Vfx_" + kind,
                wrapMode = (kind == Kind.NoiseTiling || kind == Kind.WaterNormal)
                    ? TextureWrapMode.Repeat
                    : TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 1,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, false);
            return tex;
        }

        private static void Fill(Color32[] pixels, int size, Func<Vector2, Color> shape)
        {
            float inv = 1f / (size - 1);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Centred coordinates in -1..1, so every shape function can think
                    // in terms of distance from the middle.
                    var p = new Vector2(x * inv * 2f - 1f, y * inv * 2f - 1f);
                    Color c = shape(p);
                    pixels[y * size + x] = ToColor32(c);
                }
            }
        }

        private static Color32 ToColor32(Color c) => new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255));

        // ---------------------------------------------------------------------
        // Shape functions. All take p in -1..1 and return premultiplied-ish RGBA
        // where alpha is the shape and RGB adds internal variation.
        // ---------------------------------------------------------------------

        private static Color SoftDot(Vector2 p)
        {
            float d = p.magnitude;
            // Squared falloff reads much closer to a real out-of-focus light than a
            // linear one, and it keeps the outer edge from banding.
            float a = Mathf.Clamp01(1f - d);
            a = a * a * (3f - 2f * a);
            a *= a;
            float core = Mathf.Clamp01(1f - d * 2.4f);
            return new Color(1f, 1f - core * 0.02f, 1f - core * 0.06f, a);
        }

        private static Color Glow(Vector2 p)
        {
            float d = p.magnitude;
            float core = Mathf.Pow(Mathf.Clamp01(1f - d * 5.5f), 1.5f);
            float halo = Mathf.Pow(Mathf.Clamp01(1f - d), 3.5f);
            float a = Mathf.Clamp01(core + halo * 0.55f);
            return new Color(1f, 1f - halo * 0.05f, 1f - halo * 0.12f, a);
        }

        private static Color Spark(Vector2 p)
        {
            // A streak: much tighter across than along, with a bright head.
            float across = Mathf.Abs(p.x) * 5.5f;
            float along = Mathf.Abs(p.y);
            float d = Mathf.Sqrt(across * across + along * along);
            float a = Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f);
            float head = Mathf.Pow(Mathf.Clamp01(1f - (p - new Vector2(0f, -0.45f)).magnitude * 3.2f), 2f);
            a = Mathf.Clamp01(a + head * 0.8f);
            return new Color(1f, 1f - a * 0.03f, 1f - a * 0.08f, a);
        }

        private static Color StarShape(Vector2 p, int points, float spikeWidth, float radius)
        {
            float d = p.magnitude;
            float angle = Mathf.Atan2(p.y, p.x);

            // Core glow.
            float core = Mathf.Pow(Mathf.Clamp01(1f - d * 3.4f), 2.2f);

            // Diffraction spikes: a periodic function of angle, narrowed by distance
            // so the spikes taper instead of forming a fan.
            float spikes = Mathf.Abs(Mathf.Cos(angle * points * 0.5f));
            spikes = Mathf.Pow(spikes, 1f / Mathf.Max(spikeWidth, 1e-3f));
            float reach = Mathf.Pow(Mathf.Clamp01(1f - d / Mathf.Max(radius * 2.2f, 1e-3f)), 1.6f);
            float spike = spikes * reach;

            float a = Mathf.Clamp01(core + spike * 0.85f);
            return new Color(1f, 1f - core * 0.02f, 1f - core * 0.05f, a);
        }

        private static Color RingShape(Vector2 p, float radius, float thickness, float hardness)
        {
            float d = Mathf.Abs(p.magnitude - radius * 2f);
            float a = Mathf.Clamp01(1f - d / Mathf.Max(thickness * 2f, 1e-3f));
            a = Mathf.Pow(a, hardness);
            // Fade the outer edge of the texture so the ring never clips the quad.
            a *= Mathf.Clamp01((1f - p.magnitude) * 4f);
            return new Color(1f, 1f, 1f, a);
        }

        private static Color Cloud(Vector2 p, float frequency, float erosion, float roundness)
        {
            float d = p.magnitude;
            // Start from a soft disc, then erode it with fbm so the silhouette is
            // billowy rather than circular.
            float disc = Mathf.Clamp01(1f - d / Mathf.Max(roundness, 1e-3f));
            disc = disc * disc * (3f - 2f * disc);

            float n = Fbm(p * frequency + new Vector2(11.3f, 5.7f), 4);
            float a = Mathf.Clamp01(disc * (0.55f + n * 0.9f) - erosion * (1f - disc));
            a = Mathf.Clamp01(a * 1.35f);

            // Internal shading: the noise doubles as a density cue in RGB, so a lit
            // smoke shader has something to work with even before it adds a normal.
            float density = 0.72f + n * 0.28f;
            return new Color(density, density, density, a);
        }

        private static Color Leaf(Vector2 p)
        {
            // Two mirrored parabolas meeting at tip and stem.
            float y = p.y;
            float halfWidth = 0.55f * Mathf.Sqrt(Mathf.Clamp01(1f - y * y)) * (1f - Mathf.Abs(y) * 0.25f);
            float inside = Mathf.Clamp01((halfWidth - Mathf.Abs(p.x)) * 8f);
            // A darker midrib gives the silhouette some read at small sizes.
            float rib = Mathf.Clamp01(1f - Mathf.Abs(p.x) * 26f) * inside;
            float shade = 1f - rib * 0.35f;
            return new Color(shade, 1f, shade * 0.9f, inside);
        }

        private static Color Petal(Vector2 p)
        {
            float angle = Mathf.Atan2(p.y, p.x);
            float d = p.magnitude;
            float lobe = 0.55f + 0.35f * Mathf.Cos(angle * 1f);
            float inside = Mathf.Clamp01((lobe - d) * 6f);
            float centre = Mathf.Pow(Mathf.Clamp01(1f - d * 1.8f), 2f);
            return new Color(1f, 1f - centre * 0.15f, 1f - centre * 0.05f, inside);
        }

        private static Color Droplet(Vector2 p)
        {
            // Teardrop: circle at the bottom, tapering to a point at the top.
            float y = Mathf.Clamp(p.y, -1f, 1f);
            float taper = Mathf.Pow(Mathf.Clamp01((1f - y) * 0.5f), 0.65f);
            float halfWidth = 0.42f * taper;
            float bulge = Mathf.Clamp01(1f - (p - new Vector2(0f, -0.35f)).magnitude * 2.2f);
            float body = Mathf.Clamp01((halfWidth - Mathf.Abs(p.x)) * 9f) * Mathf.Clamp01((y + 1f) * 3f);
            float a = Mathf.Clamp01(Mathf.Max(body, bulge * 0.9f));
            float highlight = Mathf.Pow(Mathf.Clamp01(1f - (p - new Vector2(-0.12f, -0.28f)).magnitude * 5f), 2f);
            return new Color(1f, 1f, 1f, a * (0.75f + highlight * 0.25f));
        }

        private static Color Splash(Vector2 p)
        {
            // A crescent sheet: thin at the ends, thick in the middle, open upwards.
            float d = p.magnitude;
            float arc = Mathf.Abs(d - 0.62f);
            float band = Mathf.Clamp01(1f - arc / 0.30f);
            float upper = Mathf.Clamp01((p.y + 0.25f) * 1.8f);
            float n = Fbm(p * 5.5f + new Vector2(3.1f, 7.9f), 3);
            float a = Mathf.Clamp01(band * upper * (0.45f + n * 1.1f));
            a *= Mathf.Clamp01((1f - d) * 3f);
            return new Color(1f, 1f, 1f, a);
        }

        private static Color Bolt(Vector2 p)
        {
            // Centreline displaced by a couple of decorrelated sines, then a narrow
            // core with a wide glow around it.
            float wobble = Mathf.Sin(p.y * 7.3f) * 0.20f + Mathf.Sin(p.y * 17.1f + 1.7f) * 0.09f;
            float d = Mathf.Abs(p.x - wobble);
            float core = Mathf.Clamp01(1f - d * 26f);
            float glow = Mathf.Pow(Mathf.Clamp01(1f - d * 4.5f), 2.5f);
            float endFade = Mathf.Clamp01((1f - Mathf.Abs(p.y)) * 2.2f);
            float a = Mathf.Clamp01((core + glow * 0.5f) * endFade);
            return new Color(1f, 1f - glow * 0.06f, 1f, a);
        }

        private static Color Flame(Vector2 p)
        {
            // Tongue shape: widest low, tapering to a point, eroded by noise.
            float y01 = Mathf.Clamp01(p.y * 0.5f + 0.5f);
            float width = Mathf.Lerp(0.62f, 0.04f, Mathf.Pow(y01, 1.35f));
            float n = Fbm(new Vector2(p.x * 3.4f, p.y * 2.0f - 4f), 3);
            float edge = width * (0.72f + n * 0.55f);
            float inside = Mathf.Clamp01((edge - Mathf.Abs(p.x)) * 5.5f);
            inside *= Mathf.Clamp01((p.y + 1f) * 2.4f);

            float core = Mathf.Clamp01((edge * 0.45f - Mathf.Abs(p.x)) * 6f) * (1f - y01 * 0.7f);
            // Hot core towards white, outer towards the tint the material supplies.
            return new Color(1f, 0.55f + core * 0.45f, 0.18f + core * 0.6f, inside);
        }

        private static Color Shard(Vector2 p)
        {
            // Irregular convex polygon: an angular silhouette reads as broken rock
            // or ice in a way a circle never will.
            float angle = Mathf.Atan2(p.y, p.x);
            float radius = 0.62f
                         + 0.16f * Mathf.Cos(angle * 3f + 0.7f)
                         + 0.09f * Mathf.Cos(angle * 5f - 1.9f);
            float inside = Mathf.Clamp01((radius - p.magnitude) * 12f);
            // A facet highlight along one side gives the flat shape some volume.
            float facet = Mathf.Clamp01(Vector2.Dot(p.normalized, new Vector2(-0.6f, 0.8f))) * inside;
            float shade = 0.55f + facet * 0.45f;
            return new Color(shade, shade, shade, inside);
        }

        private static Color Wing(Vector2 p)
        {
            // Two teardrop wings mirrored about x, with a thin body.
            Vector2 q = new Vector2(Mathf.Abs(p.x), p.y);
            Vector2 c = q - new Vector2(0.40f, 0.10f);
            float wing = Mathf.Clamp01(1f - new Vector2(c.x * 1.5f, c.y * 2.4f).magnitude * 2.1f);
            float lower = Mathf.Clamp01(1f - (q - new Vector2(0.30f, -0.34f)).magnitude * 3.6f);
            float body = Mathf.Clamp01(1f - new Vector2(p.x * 9f, p.y * 1.6f).magnitude * 1.9f);
            float a = Mathf.Clamp01(Mathf.Max(Mathf.Max(wing, lower * 0.85f), body));
            float edge = Mathf.Clamp01(1f - a) * a;
            return new Color(1f, 1f - edge * 0.2f, 1f - edge * 0.35f, a);
        }

        private static Color Burst(Vector2 p)
        {
            // Many thin radial streaks of varying length: a cast telegraph.
            float d = p.magnitude;
            float angle = Mathf.Atan2(p.y, p.x);
            float rays = Mathf.Abs(Mathf.Sin(angle * 11f));
            rays = Mathf.Pow(rays, 14f);
            // Vary the reach per ray so it does not look like a gear.
            float lengthVariation = 0.55f + 0.45f * Hash(new Vector2(Mathf.Floor(angle * 11f / Mathf.PI), 3.7f));
            float reach = Mathf.Clamp01(1f - d / lengthVariation);
            float core = Mathf.Pow(Mathf.Clamp01(1f - d * 4.5f), 2f);
            float a = Mathf.Clamp01(rays * reach * 0.9f + core);
            return new Color(1f, 1f, 1f, a);
        }

        // ---------------------------------------------------------------------
        // Tiling noise. A wrapped lattice so the result repeats seamlessly, which
        // matters for the water normal and for every scrolling flow map.
        // ---------------------------------------------------------------------
        private static void BuildTilingNoise(Color32[] pixels, int size)
        {
            const int period = 8;
            float inv = 1f / size;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x * inv * period;
                    float v = y * inv * period;
                    float n = TilingFbm(u, v, period, 4);
                    // Two different frequencies in different channels, so one texture
                    // serves as flow noise (R) and as a coarser dissolve mask (G).
                    float coarse = TilingFbm(u * 0.5f, v * 0.5f, period, 3);
                    float fine = TilingFbm(u * 2f, v * 2f, period * 2, 3);
                    byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(n * 255f), 0, 255);
                    byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(coarse * 255f), 0, 255);
                    byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(fine * 255f), 0, 255);
                    pixels[y * size + x] = new Color32(r, g, b, r);
                }
            }
        }

        private static void BuildWaterNormal(Color32[] pixels, int size)
        {
            const int period = 6;
            float inv = 1f / size;
            var height = new float[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x * inv * period;
                    float v = y * inv * period;
                    // Ridged noise reads as wind chop far better than plain fbm: the
                    // creases become wave crests instead of soft lumps.
                    float n = TilingFbm(u, v, period, 4);
                    float ridged = 1f - Mathf.Abs(n * 2f - 1f);
                    height[y * size + x] = Mathf.Pow(ridged, 1.7f);
                }
            }

            // Sobel the height field into a tangent-space normal. The strength is
            // deliberately low: the water shader stacks two of these and adds its own
            // vertex wave normal on top.
            const float strength = 2.4f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float hl = height[y * size + Wrap(x - 1, size)];
                    float hr = height[y * size + Wrap(x + 1, size)];
                    float hd = height[Wrap(y - 1, size) * size + x];
                    float hu = height[Wrap(y + 1, size) * size + x];

                    var n = new Vector3((hl - hr) * strength, (hd - hu) * strength, 1f).normalized;
                    // Stored as XYZ in RGB with alpha left at 1. That satisfies both
                    // branches of UnpackNormalScale: the plain-RGB path reads xyz
                    // directly, and the RGorAG path multiplies alpha by red first,
                    // so it ends up reading (r, g) as well. Uncompressed runtime
                    // textures have no importer to mark them as normal maps, so
                    // getting this right by construction is the only option.
                    pixels[y * size + x] = new Color32(
                        (byte)Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f),
                        255);
                }
            }
        }

        private static int Wrap(int v, int size) => ((v % size) + size) % size;

        // ---------------------------------------------------------------------
        // Noise primitives
        // ---------------------------------------------------------------------
        private static float Hash(Vector2 p)
        {
            float h = Vector2.Dot(p, new Vector2(127.1f, 311.7f));
            return Frac(Mathf.Sin(h) * 43758.5453f);
        }

        private static float Frac(float v) => v - Mathf.Floor(v);

        private static float ValueNoise(Vector2 p)
        {
            Vector2 i = new Vector2(Mathf.Floor(p.x), Mathf.Floor(p.y));
            Vector2 f = p - i;
            f = new Vector2(f.x * f.x * (3f - 2f * f.x), f.y * f.y * (3f - 2f * f.y));

            float a = Hash(i);
            float b = Hash(i + new Vector2(1f, 0f));
            float c = Hash(i + new Vector2(0f, 1f));
            float d = Hash(i + new Vector2(1f, 1f));
            return Mathf.Lerp(Mathf.Lerp(a, b, f.x), Mathf.Lerp(c, d, f.x), f.y);
        }

        private static float Fbm(Vector2 p, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += ValueNoise(p) * amp;
                norm += amp;
                p *= 2.03f;
                amp *= 0.5f;
            }
            return sum / Mathf.Max(norm, 1e-5f);
        }

        /// <summary>
        /// Value noise on a lattice that wraps at <paramref name="period"/>, so the
        /// texture tiles exactly. Without this the scrolling flow maps would show a
        /// seam every repeat, which is very visible on water.
        /// </summary>
        private static float TilingValueNoise(float x, float y, int period)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            int x1 = Wrap(x0 + 1, period), y1 = Wrap(y0 + 1, period);
            x0 = Wrap(x0, period); y0 = Wrap(y0, period);

            float a = Hash(new Vector2(x0, y0));
            float b = Hash(new Vector2(x1, y0));
            float c = Hash(new Vector2(x0, y1));
            float d = Hash(new Vector2(x1, y1));
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        private static float TilingFbm(float x, float y, int period, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            int p = period;
            for (int i = 0; i < octaves; i++)
            {
                sum += TilingValueNoise(x, y, p) * amp;
                norm += amp;
                x *= 2f; y *= 2f; p *= 2;
                amp *= 0.5f;
            }
            return sum / Mathf.Max(norm, 1e-5f);
        }
    }
}
