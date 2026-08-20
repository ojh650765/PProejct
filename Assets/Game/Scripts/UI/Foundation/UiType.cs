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

        /// <summary>
        /// The bold cut of the same family, handed to the display roles by <see cref="Apply"/>.
        ///
        /// Null is a supported state and means "no real bold available"; the roles that want
        /// weight then ask for <see cref="FontStyles.Bold"/> against <see cref="Font"/> and TMP
        /// synthesises it, which is what the game did before Pretendard arrived. Assigning this
        /// by hand overrides the lookup, the same as <see cref="Font"/>.
        /// </summary>
        public static TMP_FontAsset BoldFont;

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

        /// <summary>True for the roles that carry weight: the display and readout roles.</summary>
        /// <remarks>
        /// Five of the eight. That is deliberate — headings, buttons, section labels and
        /// numbers are the parts of a screen a player scans rather than reads, and weight is
        /// what separates them from the sentences around them.
        /// </remarks>
        private static bool WantsWeight(UiTextRole role) =>
            role == UiTextRole.Metric || role == UiTextRole.Title || role == UiTextRole.Heading
            || role == UiTextRole.Overline || role == UiTextRole.Numeric;

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

            // The display roles take the bold face as their own asset rather than asking TMP
            // for FontStyles.Bold against the regular one.
            //
            // Both routes reach real outlines — the regular asset's weight table has the bold
            // cut parked at 700 — but they differ in one way that matters here. Resolving
            // through the weight table makes those glyphs come from a second asset, so TMP
            // splits the label into a submesh drawn with that asset's own material; anything
            // set on this label's material, which is what ApplyShadow sets, then reaches the
            // regular glyphs and not the bold ones. Handing the role the bold asset outright
            // keeps a heading on one material, one submesh and one draw, with the underlay
            // where it was put. The weight table stays wired for the views that set
            // FontStyles.Bold by hand and for rich text.
            var wantsWeight = WantsWeight(role);
            var face = wantsWeight ? (EnsureBoldFont() ?? Font) : Font;
            if (face != null) text.font = face;

            // With a real bold face on the label there is nothing left to synthesise, so the
            // Bold flag comes off — leaving it on would ask TMP to dilate a face that is
            // already bold. It stays on only when the bold cut is missing, which is the old
            // behaviour and the honest fallback.
            var weight = wantsWeight && BoldFont != null ? FontStyles.Normal : FontStyles.Bold;

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
                    text.fontStyle = weight;
                    text.characterSpacing = -4f;
                    break;
                case UiTextRole.Title:
                    text.fontStyle = weight;
                    text.characterSpacing = -1.5f;
                    break;
                case UiTextRole.Heading:
                    text.fontStyle = weight;
                    text.characterSpacing = -0.5f;
                    break;
                case UiTextRole.Overline:
                    text.fontStyle = weight | FontStyles.UpperCase;
                    text.characterSpacing = 9f;
                    break;
                case UiTextRole.Caption:
                    text.fontStyle = FontStyles.Normal;
                    text.characterSpacing = 2f;
                    break;
                case UiTextRole.Numeric:
                    text.fontStyle = weight;
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
        /// The shipped Korean face, relative to a Resources folder.
        ///
        /// Pretendard (SIL OFL 1.1), committed at Assets/Game/Art/Fonts/ and turned into these
        /// assets by PokeLab.UI.Editor.KoreanFontAssetBuilder. Loaded through
        /// <see cref="Resources"/> because that is the only asset lookup a player has —
        /// AssetDatabase does not exist outside the editor, and nothing else in the project
        /// references the assets, so without the Resources folder they would simply not be in
        /// the build.
        ///
        /// Three weights are generated, not one. The game asks for bold on five of the eight
        /// roles below; with a single regular face TMP has nothing to switch to and fakes the
        /// weight by dilating the glyph's distance field, which closes the counters inside
        /// ㅁ ㅂ ㅇ ㅎ and turns a Hangul syllable into a smudge. The bold cut exists so that
        /// never happens. It replaced Nanum Gothic, which was a body face pressed into
        /// interface duty and had no bold committed at all.
        /// </summary>
        public const string KoreanFontResourcePath = "Fonts/Pretendard SDF";

        /// <summary>The 600 cut, reachable from rich text as <c>&lt;font-weight=600&gt;</c>.</summary>
        public const string KoreanSemiBoldFontResourcePath = "Fonts/Pretendard SemiBold SDF";

        /// <summary>The 700 cut. <see cref="BoldFont"/>, and slot 700 of the regular asset's weight table.</summary>
        public const string KoreanBoldFontResourcePath = "Fonts/Pretendard Bold SDF";

        /// <summary>
        /// Families tried, in order, when <see cref="EnsureFont"/> has to find one itself.
        /// Every entry must carry Hangul as well as Latin — the dialogue writes both, often
        /// in the same line.
        ///
        /// This is an editor-and-desktop convenience only; see <see cref="EnsureFont"/> for
        /// why a browser can never satisfy it.
        /// </summary>
        private static readonly string[] FallbackFamilies =
        {
            "Malgun Gothic",     // ships with every Korean-capable Windows install
            "Pretendard",
            "Noto Sans KR",
            "NanumGothic",
            "Nanum Gothic",
            "Source Han Sans K",
            "Arial Unicode MS",
        };

        private static bool _fontSearched;
        private static bool _boldFontSearched;

        /// <summary>
        /// Makes sure <see cref="Font"/> points at something that can actually draw the
        /// script the game is written in.
        ///
        /// TextMesh Pro's built-in default is Liberation Sans: Latin only. Every Korean
        /// character in a dialogue line renders as a missing-glyph box against it, and the
        /// failure is silent — the layout is correct, the text is simply not there.
        ///
        /// The committed asset is tried first and is what any build gets. This used to go
        /// straight to <see cref="TMP_FontAsset.CreateFontAsset(string,string,int)"/>, which
        /// asks the operating system for an installed family. That works on Windows and
        /// cannot work in a browser: WebGL has no OS font list, so every family returned
        /// null, <see cref="Font"/> stayed null, and the shipped game rendered all 107
        /// authored dialogue lines, the battle log and the menus as boxes. Borrowing an OS
        /// font is kept below it only as a desktop-editor convenience.
        ///
        /// Returns whatever <see cref="Font"/> ends up as, including null when nothing
        /// suitable is found; callers should treat null as "Latin only" and say so
        /// rather than assume it worked.
        /// </summary>
        public static TMP_FontAsset EnsureFont()
        {
            // An integrator-assigned Font wins outright: that assignment is the documented
            // contract of the public field and must not be second-guessed here.
            if (Font != null || _fontSearched) return Font;
            _fontSearched = true;

            try
            {
                var shipped = Resources.Load<TMP_FontAsset>(KoreanFontResourcePath);
                if (shipped != null)
                {
                    Font = shipped;
                    return Font;
                }

                Debug.LogWarning("[UiType] No font asset at Resources/" + KoreanFontResourcePath
                                 + ". In a player this is unrecoverable — the OS search below "
                                 + "finds nothing on WebGL — and all Korean will render as "
                                 + "boxes. Run PokeLab > UI > Rebuild Korean Font Asset.");

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
                                 + "font asset is assigned to UiType.Font. On WebGL this branch "
                                 + "is always reached, because a browser exposes no OS fonts — "
                                 + "the shipped asset at Resources/" + KoreanFontResourcePath
                                 + " is the only thing that works there.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[UiType] Runtime font resolution failed; falling back to the "
                                 + "TMP default, which has no Hangul coverage. " + e.Message);
            }

            return Font;
        }

        /// <summary>
        /// Resolves the bold cut, or leaves it null.
        ///
        /// There is deliberately no OS search behind this one. A missing bold is a downgrade —
        /// TMP synthesises the weight and the screen looks the way it did before Pretendard —
        /// whereas a missing regular is Korean rendered as boxes, which is why that one is
        /// worth borrowing an installed family for. Borrowing a system bold would also silently
        /// mix two families on one screen, which reads worse than a synthesised weight.
        ///
        /// The lookup runs once. A null result is cached as null: a Resources.Load that missed
        /// will miss every frame, and this is called from Apply on every label built.
        /// </summary>
        public static TMP_FontAsset EnsureBoldFont()
        {
            if (BoldFont != null || _boldFontSearched) return BoldFont;
            _boldFontSearched = true;

            try
            {
                BoldFont = Resources.Load<TMP_FontAsset>(KoreanBoldFontResourcePath);
                if (BoldFont == null)
                    Debug.LogWarning("[UiType] No bold font asset at Resources/"
                                     + KoreanBoldFontResourcePath + ". Headings and buttons will "
                                     + "fall back to TextMesh Pro's synthesised bold, which "
                                     + "dilates the glyph outline and closes the counters in "
                                     + "Hangul. Run PokeLab > UI > Rebuild Korean Font Asset.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[UiType] Bold font resolution failed; headings will use "
                                 + "synthesised bold. " + e.Message);
            }

            return BoldFont;
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
                // A label that was told FontStyles.Bold by hand — the battle floaters, the
                // dialogue speaker's subtitle — resolves its glyphs out of the bold asset
                // through the weight table, and TMP draws those in a submesh with the bold
                // asset's own material. The underlay set below lives on this label's material,
                // so on such a label it would light up nothing at all. Moving the label onto
                // the bold face outright collapses it back to one material, which is the one
                // being written here. Same glyphs either way; this is about where they draw.
                UseRealBoldFace(text);

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

        /// <summary>
        /// Swaps a bold-styled label onto the bold face and drops the Bold flag, so the whole
        /// label draws from one font asset with one material.
        ///
        /// Only touches labels that are actually styled bold and are still sitting on the
        /// regular face; a label already on the bold cut, or one that is not bold, is left
        /// exactly as it is. Italics keep their flag — TMP shears them in the vertex pass
        /// rather than swapping outlines, so the bold face carries an italic perfectly well.
        /// </summary>
        private static void UseRealBoldFace(TMP_Text text)
        {
            if (text == null) return;
            if ((text.fontStyle & FontStyles.Bold) != FontStyles.Bold) return;
            if (EnsureBoldFont() == null) return;

            if (text.font != BoldFont) text.font = BoldFont;

            // Cleared even when the face was already the bold one: a display role comes out of
            // Apply on the bold cut with no Bold flag, and several views then set the flag back
            // on regardless. The weight table catches that and hands the request straight back
            // to this same asset, but clearing it here means the request is never made.
            text.fontStyle &= ~FontStyles.Bold;
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
