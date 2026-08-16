using System;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The single source of colour for every Poké Lab surface.
    ///
    /// It is a static class rather than a ScriptableObject on purpose: the UI worker owns
    /// no <c>.asset</c> files (the integrator wires those), so a palette that lives in code
    /// is the only form that survives the ownership split without a missing-reference hole
    /// in every prefab. <see cref="UiThemeOverride"/> exists for the case where art wants to
    /// retune it later from a serialized object.
    /// </summary>
    public static class UiPalette
    {
        // ---------------------------------------------------------------- surfaces

        /// <summary>Deep neutral behind full-screen menus. Never pure black — pure black kills the soft shadows.</summary>
        public static readonly Color Backdrop = Hex("#0B0E14");
        /// <summary>Raised card / panel body.</summary>
        public static readonly Color Surface = Hex("#161C27");
        /// <summary>Panel one step closer to the viewer (rows inside a card).</summary>
        public static readonly Color SurfaceRaised = Hex("#1E2635");
        /// <summary>Recessed well — bar tracks, input fields.</summary>
        public static readonly Color SurfaceSunken = Hex("#0E131C");
        /// <summary>Hairline between rows. Low contrast on purpose.</summary>
        public static readonly Color Divider = new Color(1f, 1f, 1f, 0.08f);
        /// <summary>Drop shadow tint used under every rounded panel.</summary>
        public static readonly Color Shadow = new Color(0f, 0f, 0f, 0.45f);

        // ------------------------------------------------------------------- text

        public static readonly Color TextPrimary = Hex("#F2F5FA");
        public static readonly Color TextSecondary = new Color(0.945f, 0.961f, 0.980f, 0.72f);
        public static readonly Color TextMuted = new Color(0.945f, 0.961f, 0.980f, 0.44f);
        public static readonly Color TextOnAccent = Hex("#08111A");

        // ----------------------------------------------------------------- accent

        /// <summary>The Poké Lab signature cyan. Everything the device itself emits is this hue.</summary>
        public static readonly Color ScannerCyan = Hex("#4FE8D2");
        public static readonly Color ScannerCyanDim = Hex("#1E6E68");
        /// <summary>Secondary device accent used for the evidence panel and headers.</summary>
        public static readonly Color ScannerAmber = Hex("#FFC24B");
        /// <summary>Housing / bezel colour of the handheld prop's screen surround.</summary>
        public static readonly Color ScannerHousing = Hex("#1A2028");
        /// <summary>Screen glass, before the glow layer.</summary>
        public static readonly Color ScannerGlass = Hex("#071417");

        // ------------------------------------------------- conversation overlay

        /// <summary>
        /// The wash the conversation overlay lays over the world.
        ///
        /// Cool and very dark, but never opaque. A conversation in this game happens in the
        /// middle of a scene the player is looking at, and an opaque box would cover the NPC
        /// they are talking to. The scrim exists to buy contrast for white text, nothing more.
        /// </summary>
        public static readonly Color Scrim = Hex("#080D16");

        /// <summary>Scrim strength behind the speaker's name row.</summary>
        public const float ScrimNameAlpha = 0.50f;

        /// <summary>Scrim strength behind the body copy. Heavier — it carries far more text.</summary>
        public const float ScrimBodyAlpha = 0.62f;

        /// <summary>
        /// The hairline that separates the name row from the body copy. Much brighter than
        /// <see cref="Divider"/>: this one has to survive on top of a lit 3D scene rather
        /// than on a flat panel.
        /// </summary>
        public static readonly Color RuleBright = new Color(1f, 1f, 1f, 0.66f);

        /// <summary>
        /// Fill of the small overlay controls (AUTO / MENU). Near-white on purpose: they are
        /// the one deliberately high-contrast element, which is what stops the player having
        /// to hunt for them against a bright scene.
        /// </summary>
        public static readonly Color ControlSlab = new Color(0.929f, 0.949f, 0.969f, 0.90f);

        /// <summary>Shadow cast under overlay text so it holds up over light pixel art.</summary>
        public static readonly Color TextShadow = new Color(0f, 0.02f, 0.06f, 0.85f);

        // --------------------------------------------------------------- semantic

        public static readonly Color Positive = Hex("#4ADE80");
        public static readonly Color Caution = Hex("#FBBF24");
        public static readonly Color Negative = Hex("#F87171");
        public static readonly Color Critical = Hex("#EF4444");
        public static readonly Color Info = Hex("#60A5FA");

        // ------------------------------------------------------------ health ramp

        private static readonly Color HealthHigh = Hex("#3FD37A");
        private static readonly Color HealthMid = Hex("#F2C744");
        private static readonly Color HealthLow = Hex("#EE5A5A");

        /// <summary>
        /// Health bar colour for a 0-1 fraction. Two-segment ramp with the break at 50%/20%
        /// so the amber warning band is wide enough to read during a fast tween — a single
        /// linear green→red lerp spends almost no time looking amber and reads as a fade.
        /// </summary>
        public static Color Health(float fraction)
        {
            fraction = Mathf.Clamp01(fraction);
            if (fraction > 0.5f) return Color.Lerp(HealthMid, HealthHigh, Mathf.InverseLerp(0.5f, 1f, fraction));
            return Color.Lerp(HealthLow, HealthMid, Mathf.InverseLerp(0.2f, 0.5f, fraction));
        }

        /// <summary>Colour for the win-probability gauge: red at hopeless, cyan at dominant.</summary>
        public static Color Probability(float probability)
        {
            probability = Mathf.Clamp01(probability);
            if (probability >= 0.5f) return Color.Lerp(Hex("#C9D6D4"), ScannerCyan, Mathf.InverseLerp(0.5f, 0.92f, probability));
            return Color.Lerp(Negative, Hex("#C9D6D4"), Mathf.InverseLerp(0.08f, 0.5f, probability));
        }

        /// <summary>Colour for a signed delta in percentage points.</summary>
        public static Color Delta(float points)
        {
            if (points > 0.15f) return Positive;
            if (points < -0.15f) return Negative;
            return TextMuted;
        }

        public static Color Threat(ThreatLevel level) => level switch
        {
            ThreatLevel.Lethal => Critical,
            ThreatLevel.Dangerous => Hex("#FB923C"),
            ThreatLevel.Manageable => Caution,
            _ => Hex("#7DD3C0"),
        };

        public static Color Effectiveness(Effectiveness effectiveness) => effectiveness switch
        {
            Core.Effectiveness.SuperEffective => Positive,
            Core.Effectiveness.NotVeryEffective => Caution,
            Core.Effectiveness.Immune => TextMuted,
            _ => TextSecondary,
        };

        public static Color Status(StatusCondition status) => status switch
        {
            StatusCondition.Burn => Hex("#F97316"),
            StatusCondition.Freeze => Hex("#67E8F9"),
            StatusCondition.Paralysis => Hex("#FACC15"),
            StatusCondition.Poison => Hex("#C084FC"),
            StatusCondition.BadPoison => Hex("#A855F7"),
            StatusCondition.Sleep => Hex("#94A3B8"),
            StatusCondition.Fainted => Hex("#64748B"),
            _ => TextMuted,
        };

        public static Color Category(MoveCategory category) => category switch
        {
            MoveCategory.Physical => Hex("#E8734A"),
            MoveCategory.Special => Hex("#5B8DEF"),
            _ => Hex("#9AA5B1"),
        };

        // ------------------------------------------------------------------ types

        /// <summary>
        /// Type identity colours. Chosen from the genre's shared visual language but pulled
        /// toward a consistent saturation/luminance band so eighteen badges sitting in one
        /// list read as one designed set instead of eighteen unrelated swatches.
        /// </summary>
        private static readonly Color[] Types = new Color[ElementTypes.Count];
        private static readonly Color[] TypesDeep = new Color[ElementTypes.Count];

        static UiPalette()
        {
            Set(ElementType.Normal, "#A7A9AE");
            Set(ElementType.Fire, "#F0733A");
            Set(ElementType.Water, "#4A8FE7");
            Set(ElementType.Electric, "#F5C542");
            Set(ElementType.Grass, "#5BC46A");
            Set(ElementType.Ice, "#79D8DE");
            Set(ElementType.Fighting, "#D9455A");
            Set(ElementType.Poison, "#A662C9");
            Set(ElementType.Ground, "#D6A75C");
            Set(ElementType.Flying, "#8CA8E8");
            Set(ElementType.Psychic, "#F26A8D");
            Set(ElementType.Bug, "#9DC13C");
            Set(ElementType.Rock, "#B8A05E");
            Set(ElementType.Ghost, "#6E5A9E");
            Set(ElementType.Dragon, "#6A5AE0");
            Set(ElementType.Dark, "#6B5A50");
            Set(ElementType.Steel, "#8FA3B8");
            Set(ElementType.Fairy, "#EE9BC4");
        }

        private static void Set(ElementType type, string hex)
        {
            var c = Hex(hex);
            Types[(int)type] = c;
            // Deep variant for badge backgrounds behind white text: same hue, half the value.
            Color.RGBToHSV(c, out var h, out var s, out var v);
            TypesDeep[(int)type] = Color.HSVToRGB(h, Mathf.Clamp01(s * 1.05f), v * 0.46f);
        }

        /// <summary>Identity colour for an elemental type. <see cref="ElementType.None"/> returns a neutral.</summary>
        public static Color Type(ElementType type)
        {
            var index = (int)type;
            if (index < 0 || index >= Types.Length) return TextMuted;
            return Types[index];
        }

        /// <summary>Darkened type colour, for badge fills that carry light text on top.</summary>
        public static Color TypeDeep(ElementType type)
        {
            var index = (int)type;
            if (index < 0 || index >= TypesDeep.Length) return SurfaceRaised;
            return TypesDeep[index];
        }

        // ----------------------------------------------------------------- helpers

        /// <summary>Same colour at a different alpha, without mutating the source.</summary>
        public static Color WithAlpha(this Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        /// <summary>Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. Falls back to magenta so a typo is visible, not silent.</summary>
        public static Color Hex(string hex)
        {
            if (ColorUtility.TryParseHtmlString(hex, out var color)) return color;
            return Color.magenta;
        }
    }

    /// <summary>
    /// Optional serialized override so art direction can retune the accent without a
    /// recompile. Nothing depends on this existing; when absent <see cref="UiPalette"/>
    /// values are used verbatim.
    /// </summary>
    [Serializable]
    public struct UiThemeOverride
    {
        public bool Enabled;
        public Color ScannerAccent;
        public Color Surface;
    }
}
