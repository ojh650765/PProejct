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
        /// <remarks>
        /// Raised by half again from the scale this started with, which was an application's
        /// scale rather than a game's: body copy at 18pt is 1.7% of a 1080p screen, fine in a
        /// window you lean into and much too small on a screen you sit back from. The ratios
        /// between the roles are unchanged, so the hierarchy reads exactly as before — only
        /// the whole ramp moved.
        ///
        /// Korean is the reason the floor moved most. Hangul syllables are dense — three
        /// letters stacked into one glyph — so a size that is merely small in Latin becomes
        /// genuinely unreadable in Korean, and this game is Korean first.
        /// </remarks>
        public static float Size(UiTextRole role) => role switch
        {
            UiTextRole.Metric => 96f,
            UiTextRole.Title => 50f,
            UiTextRole.Heading => 34f,
            UiTextRole.Body => 28f,
            UiTextRole.Secondary => 23f,
            UiTextRole.Numeric => 28f,
            UiTextRole.Overline => 19f,
            UiTextRole.Caption => 19f,
            _ => 28f,
        };

        /// <summary>
        /// Applies size, weight, tracking, casing and colour for a role.
        /// Tracking is opened up on the small all-caps roles because tight caps at 12pt
        /// on a glowing scanner screen are the first thing to become unreadable.
        /// </summary>
        public static TextMeshProUGUI Apply(TextMeshProUGUI text, UiTextRole role, Color? color = null)
        {
            if (text == null) return null;

            // Every label in the game passes through here, which makes this the only place the
            // font can be guaranteed. It used to be resolved by DialogueView.BuildRuntime alone,
            // so a screen built before the first conversation — the pause menu, the bag — got
            // TMP's Latin-only default and drew its Korean as empty boxes. The search itself
            // runs at most once; see EnsureFont.
            EnsureFont();
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

        /// <summary>
        /// Families tried, in order, when <see cref="EnsureFont"/> has to find one itself.
        /// Every entry must carry Hangul as well as Latin — the dialogue writes both, often
        /// in the same line.
        /// </summary>
        private static readonly string[] FallbackFamilies =
        {
            "Malgun Gothic",     // ships with every Korean-capable Windows install
            "Noto Sans KR",
            "NanumGothic",
            "Nanum Gothic",
            "Source Han Sans K",
            "Arial Unicode MS",
        };

        private static bool _fontSearched;

        /// <summary>
        /// Makes sure <see cref="Font"/> points at something that can actually draw the
        /// script the game is written in.
        ///
        /// TextMesh Pro's built-in default is Liberation Sans: Latin only. Every Korean
        /// character in a dialogue line renders as a missing-glyph box against it, and the
        /// failure is silent — the layout is correct, the text is simply not there. Until the
        /// integrator assigns a real font asset, this borrows an installed OS font as a
        /// dynamic atlas, which costs no committed asset and no package dependency.
        ///
        /// Returns whatever <see cref="Font"/> ends up as, including null when nothing
        /// suitable is installed; callers should treat null as "Latin only" and say so
        /// rather than assume it worked.
        /// </summary>
        public static TMP_FontAsset EnsureFont()
        {
            if (Font != null || _fontSearched) return Font;
            _fontSearched = true;

            try
            {
                for (var i = 0; i < FallbackFamilies.Length; i++)
                {
                    // CreateFontAsset resolves the family through the font engine and returns
                    // null (with a log line) when the machine does not have it. That is the
                    // whole probe — asking UnityEngine.Font for the installed list would drag
                    // in the legacy text module for no extra certainty.
                    var asset = TMP_FontAsset.CreateFontAsset(FallbackFamilies[i], "Regular");
                    if (asset == null) continue;

                    asset.name = "UiRuntimeFont(" + FallbackFamilies[i] + ")";
                    asset.hideFlags = HideFlags.HideAndDontSave;
                    Font = asset;
                    return Font;
                }

                Debug.LogWarning("[UiType] No Hangul-capable system font found among ["
                                 + string.Join(", ", FallbackFamilies)
                                 + "]. Korean text will render as missing-glyph boxes until a "
                                 + "font asset is assigned to UiType.Font.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[UiType] Runtime font resolution failed; falling back to the "
                                 + "TMP default, which has no Hangul coverage. " + e.Message);
            }

            return Font;
        }

        /// <summary>
        /// Turns on TMP's material underlay so a label reads over a lit scene rather than
        /// only over a panel.
        ///
        /// This is the single thing that makes an overlay caption legible: the scrim buys
        /// average contrast, but a white glyph landing on a highlight in the art still
        /// disappears, and only a shadow tied to the glyph itself fixes that. Touching
        /// <c>fontMaterial</c> instantiates a material for this label, which is the cost of
        /// per-label underlay and is why this is opt-in rather than part of a role.
        /// </summary>
        public static void ApplyShadow(TMP_Text text, Color? color = null,
            float offsetX = 0.4f, float offsetY = -0.4f, float softness = 0.28f, float dilate = 0.12f)
        {
            if (text == null) return;
            try
            {
                var material = text.fontMaterial;
                if (material == null || !material.HasProperty(UnderlayColorId)) return;

                material.EnableKeyword("UNDERLAY_ON");
                material.SetColor(UnderlayColorId, color ?? UiPalette.TextShadow);
                material.SetFloat(UnderlayOffsetXId, offsetX);
                material.SetFloat(UnderlayOffsetYId, offsetY);
                material.SetFloat(UnderlayDilateId, dilate);
                material.SetFloat(UnderlaySoftnessId, softness);
            }
            catch (System.Exception e)
            {
                // A font asset on a shader without the underlay pass is a legitimate setup;
                // losing the shadow is a cosmetic downgrade, not a reason to fail a line.
                Debug.LogWarning("[UiType] Text shadow unavailable on this material: " + e.Message);
            }
        }

        private static readonly int UnderlayColorId = Shader.PropertyToID("_UnderlayColor");
        private static readonly int UnderlayOffsetXId = Shader.PropertyToID("_UnderlayOffsetX");
        private static readonly int UnderlayOffsetYId = Shader.PropertyToID("_UnderlayOffsetY");
        private static readonly int UnderlayDilateId = Shader.PropertyToID("_UnderlayDilate");
        private static readonly int UnderlaySoftnessId = Shader.PropertyToID("_UnderlaySoftness");

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
