using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Bakes a GradeDefinition's colour block into a URP-compatible user LUT.
    ///
    /// A LUT rather than a stack of ColorAdjustments sliders because the brief asks
    /// for a real art-directed grade. A LUT lets a look do things sliders cannot:
    /// push shadows towards a hue while pulling highlights towards its complement,
    /// desaturate only the darks, roll the top end off without crushing the mids.
    ///
    /// Format is URP's 2D strip: width = size * size, height = size, with blue
    /// varying along the strip. 32 is the size URP's own LUTs use.
    ///
    /// The volume profiles also carry equivalent ColorAdjustments and SplitToning
    /// values at a lower weight. That is deliberate belt and braces: if the LUT is
    /// applied in a slightly different colour space than assumed, the look degrades
    /// towards the slider version rather than towards something wrong.
    /// </summary>
    public static class ColorGradingLutBaker
    {
        public const int LutSize = 32;

        public static Texture2D Bake(in GradeDefinition grade)
        {
            int size = LutSize;
            var tex = new Texture2D(size * size, size, TextureFormat.RGBA32, false, true)
            {
                name = "PL_Lut_" + grade.Key,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color32[size * size * size];
            float inv = 1f / (size - 1);

            for (int b = 0; b < size; b++)
            {
                for (int g = 0; g < size; g++)
                {
                    for (int r = 0; r < size; r++)
                    {
                        var c = new Color(r * inv, g * inv, b * inv, 1f);
                        c = Apply(c, grade);

                        // Strip layout: slice b occupies columns [b*size, b*size+size).
                        int x = b * size + r;
                        int y = g;
                        pixels[y * (size * size) + x] = new Color32(
                            (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f), 0, 255),
                            255);
                    }
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>
        /// The grade itself. Ordering follows a colourist's workflow: neutralise,
        /// shape the tonal range, then push colour.
        /// </summary>
        private static Color Apply(Color c, in GradeDefinition grade)
        {
            // --- Lift / gamma / gain -----------------------------------------
            // Lift raises the floor without touching white, gain scales the top,
            // gamma bends the middle. This is the classic three-way and it is what
            // gives shadows a hue instead of making them merely grey.
            c.r = Lgg(c.r, grade.Lift.r, grade.Gamma.r, grade.Gain.r);
            c.g = Lgg(c.g, grade.Lift.g, grade.Gamma.g, grade.Gain.g);
            c.b = Lgg(c.b, grade.Lift.b, grade.Gamma.b, grade.Gain.b);

            // --- Contrast about mid grey -------------------------------------
            const float pivot = 0.4135884f;   // ACEScc mid grey, the usual pivot
            float contrast = 1f + grade.Contrast;
            c.r = (c.r - pivot) * contrast + pivot;
            c.g = (c.g - pivot) * contrast + pivot;
            c.b = (c.b - pivot) * contrast + pivot;

            c.r = Mathf.Max(c.r, 0f);
            c.g = Mathf.Max(c.g, 0f);
            c.b = Mathf.Max(c.b, 0f);

            // --- Split toning --------------------------------------------------
            // Shadows towards one hue, highlights towards another, split at a
            // balance point. This single step does most of the work of making a
            // grade look authored rather than adjusted.
            float luma = Luminance(c);
            float balance = Mathf.Clamp01(grade.SplitBalance * 0.5f + 0.5f);
            float shadowWeight = 1f - Smoothstep(0f, balance + 1e-4f, luma);
            float highlightWeight = Smoothstep(balance, 1f, luma);

            c = SoftLight(c, grade.SplitShadows, shadowWeight * 0.5f);
            c = SoftLight(c, grade.SplitHighlights, highlightWeight * 0.5f);

            // --- Hue shift -----------------------------------------------------
            if (Mathf.Abs(grade.HueShift) > 1e-3f)
            {
                Color.RGBToHSV(ClampPositive(c), out float h, out float s, out float v);
                h = Mathf.Repeat(h + grade.HueShift / 360f, 1f);
                c = Color.HSVToRGB(h, s, v, true);
            }

            // --- Saturation ----------------------------------------------------
            float l = Luminance(c);
            float sat = 1f + grade.Saturation;
            c.r = l + (c.r - l) * sat;
            c.g = l + (c.g - l) * sat;
            c.b = l + (c.b - l) * sat;

            return ClampPositive(c);
        }

        private static float Lgg(float x, float lift, float gamma, float gain)
        {
            // Lift maps 0 -> lift while leaving 1 fixed, then gain scales, then a
            // guarded pow applies the midtone bend.
            float v = x * (1f - lift) + lift;
            v = Mathf.Max(v, 0f) * gain;
            float g = Mathf.Max(gamma, 1e-3f);
            return Mathf.Pow(Mathf.Max(v, 0f), 1f / g);
        }

        private static float Luminance(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;

        /// <summary>Photoshop-style soft light, the standard split-toning blend.</summary>
        private static Color SoftLight(Color baseC, Color blend, float weight)
        {
            if (weight <= 1e-4f) return baseC;
            return new Color(
                Mathf.Lerp(baseC.r, SoftLightChannel(Mathf.Clamp01(baseC.r), blend.r), weight),
                Mathf.Lerp(baseC.g, SoftLightChannel(Mathf.Clamp01(baseC.g), blend.g), weight),
                Mathf.Lerp(baseC.b, SoftLightChannel(Mathf.Clamp01(baseC.b), blend.b), weight),
                baseC.a);
        }

        private static float SoftLightChannel(float b, float s)
        {
            return s <= 0.5f
                ? b - (1f - 2f * s) * b * (1f - b)
                : b + (2f * s - 1f) * (D(b) - b);
        }

        private static float D(float b) => b <= 0.25f
            ? ((16f * b - 12f) * b + 4f) * b
            : Mathf.Sqrt(Mathf.Max(b, 0f));

        private static float Smoothstep(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / Mathf.Max(b - a, 1e-5f));
            return t * t * (3f - 2f * t);
        }

        private static Color ClampPositive(Color c) =>
            new Color(Mathf.Max(c.r, 0f), Mathf.Max(c.g, 0f), Mathf.Max(c.b, 0f), c.a);
    }
}
