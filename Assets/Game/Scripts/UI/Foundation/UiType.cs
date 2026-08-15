using UnityEngine;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// The typographic scale.
    ///
    /// Eight sizes on a roughly 1.25 ratio, each with a fixed weight, tracking and casing.
    /// Views name a role — <see cref="UiTextRole.Metric"/>, <see cref="UiTextRole.Overline"/> —
    /// instead of a point size, which is what keeps thirty independently written screens
    /// looking like one product.
    /// </summary>
    public enum UiTextRole
    {
        /// <summary>The headline number on the scanner. Tabular, heavy, huge.</summary>
        Metric = 0,
        /// <summary>Screen title.</summary>
        Title = 1,
        /// <summary>Card / panel heading.</summary>
        Heading = 2,
        /// <summary>Creature names, move names — the primary reading size.</summary>
        Body = 3,
        /// <summary>Supporting sentence, rationale lines, battle log.</summary>
        Secondary = 4,
        /// <summary>Numeric readout beside a label, tabular figures.</summary>
        Numeric = 5,
        /// <summary>Small all-caps section label above a group.</summary>
        Overline = 6,
        /// <summary>Smallest legible text: PP counts, badge text, units.</summary>
        Caption = 7,
    }

    /// <summary>Applies the typographic scale to a TMP component.</summary>
    public static class UiType
    {
        /// <summary>
        /// Optional project font. The integrator assigns this once at boot; until then TMP
        /// falls back to its own default asset, so nothing renders blank during integration.
        /// </summary>
        public static TMP_FontAsset Font;

        /// <summary>Point size for a role at the reference 1080p canvas height.</summary>
        public static float Size(UiTextRole role) => role switch
        {
            UiTextRole.Metric => 72f,
            UiTextRole.Title => 34f,
            UiTextRole.Heading => 22f,
            UiTextRole.Body => 18f,
            UiTextRole.Secondary => 15f,
            UiTextRole.Numeric => 18f,
            UiTextRole.Overline => 12f,
            UiTextRole.Caption => 12f,
            _ => 18f,
        };

        /// <summary>
        /// Applies size, weight, tracking, casing and colour for a role.
        /// Tracking is opened up on the small all-caps roles because tight caps at 12pt
        /// on a glowing scanner screen are the first thing to become unreadable.
        /// </summary>
        public static TextMeshProUGUI Apply(TextMeshProUGUI text, UiTextRole role, Color? color = null)
        {
            if (text == null) return null;

            if (Font != null) text.font = Font;
            text.fontSize = Size(role);
            text.color = color ?? DefaultColor(role);
            text.textWrappingMode = role == UiTextRole.Metric || role == UiTextRole.Numeric
                ? TextWrappingModes.NoWrap
                : TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;

            switch (role)
            {
                case UiTextRole.Metric:
                    text.fontStyle = FontStyles.Bold;
                    text.characterSpacing = -4f;
                    break;
                case UiTextRole.Title:
                    text.fontStyle = FontStyles.Bold;
                    text.characterSpacing = -1.5f;
                    break;
                case UiTextRole.Heading:
                    text.fontStyle = FontStyles.Bold;
                    text.characterSpacing = -0.5f;
                    break;
                case UiTextRole.Overline:
                    text.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
                    text.characterSpacing = 9f;
                    break;
                case UiTextRole.Caption:
                    text.fontStyle = FontStyles.Normal;
                    text.characterSpacing = 2f;
                    break;
                case UiTextRole.Numeric:
                    text.fontStyle = FontStyles.Bold;
                    text.characterSpacing = 0f;
                    break;
                default:
                    text.fontStyle = FontStyles.Normal;
                    text.characterSpacing = 0f;
                    break;
            }

            // Comfortable measure for the reading roles; the display roles set their own.
            text.lineSpacing = role == UiTextRole.Secondary ? 8f : 0f;
            return text;
        }

        private static Color DefaultColor(UiTextRole role) => role switch
        {
            UiTextRole.Overline => UiPalette.TextMuted,
            UiTextRole.Caption => UiPalette.TextMuted,
            UiTextRole.Secondary => UiPalette.TextSecondary,
            _ => UiPalette.TextPrimary,
        };

        /// <summary>
        /// Formats a 0-1 probability as whole percent. Used everywhere a probability is
        /// shown so the game never mixes "62%" with "0.62" or "62.4%".
        /// </summary>
        public static string Percent(float probability01)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(probability01) * 100f).ToString() + "%";
        }

        /// <summary>Formats signed percentage points with an explicit sign, e.g. "+18.0 pts".</summary>
        public static string SignedPoints(float points, bool withUnit = true)
        {
            var sign = points > 0f ? "+" : (points < 0f ? "−" : "±");
            var body = Mathf.Abs(points).ToString("0.0");
            return withUnit ? $"{sign}{body} pts" : $"{sign}{body}";
        }

        /// <summary>Formats a type multiplier the way players expect: "×2", "×0.5", "×0" for immunity.</summary>
        public static string Multiplier(float multiplier)
        {
            if (multiplier <= 0f) return "×0";
            if (Mathf.Approximately(multiplier, Mathf.Round(multiplier))) return "×" + Mathf.RoundToInt(multiplier);
            return "×" + multiplier.ToString("0.##");
        }

        /// <summary>Human label for a type. Kept here so a localisation pass has one seam.</summary>
        public static string TypeName(Core.ElementType type)
        {
            return type == Core.ElementType.None ? "—" : type.ToString();
        }
    }
}
