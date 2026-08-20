using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>
    /// The Legends Z-A skin, as the battle screens need it.
    ///
    /// <b>Why these live here and not in <c>UiPalette</c>.</b> They belong in the palette and
    /// they are on their way there — another worker is retargeting <c>UiPalette</c> and
    /// <c>UiSprites</c> to this same reference right now, and two people editing the shared
    /// foundation in the same hour is how a merge eats a colour ramp. So the battle surfaces
    /// name their tokens locally, with the same values, and this file becomes a set of
    /// forwarding aliases the moment the palette's navy/lime/cyan land.
    ///
    /// <b>The language, in one paragraph.</b> Deep navy and indigo, never black. Translucent
    /// lighter-navy panels with a one-pixel brighter rim, layered on a darkened scene. The
    /// signature move is that <i>selection inverts</i>: the highlighted thing becomes a solid
    /// near-white pill carrying dark navy text, rather than an outline or a tint. Lime green
    /// is the cursor and the confirmation; cyan is progress — experience, rings; gold is
    /// money. Types keep their own colours and are never restyled.
    /// </summary>
    public static class BattleSkin
    {
        // ---- ground ----------------------------------------------------------------

        /// <summary>Top of the background gradient. Indigo, deliberately not black.</summary>
        public static readonly Color SceneTop = UiPalette.Hex("#0F2036");

        /// <summary>Bottom of the background gradient, a shade warmer and lighter.</summary>
        public static readonly Color SceneLow = UiPalette.Hex("#1B3A5C");

        // ---- panels ----------------------------------------------------------------

        /// <summary>Translucent panel fill. Sits on the scene, does not replace it.</summary>
        public static readonly Color Panel = UiPalette.Hex("#24486E").WithAlpha(0.72f);

        /// <summary>The one-pixel rim that separates a panel from what is behind it.</summary>
        public static readonly Color PanelRim = UiPalette.Hex("#5D8FC4").WithAlpha(0.45f);

        /// <summary>
        /// The name-plate body: deeper and more opaque than a menu panel.
        ///
        /// The reference's panels float over a <i>blurred, darkened</i> photo of the scene.
        /// The battle plates float over a lit diorama at full contrast, so the reference's own
        /// 72% #24486E leaves white text at barely 3:1 against a sunlit tile. This is the same
        /// hue family carried down far enough to stay legible there.
        /// </summary>
        public static readonly Color PlateBody = UiPalette.Hex("#16304F").WithAlpha(0.90f);

        /// <summary>Alternating row tint, for lists that need a rhythm rather than dividers.</summary>
        public static readonly Color RowTint = UiPalette.Hex("#2C5480").WithAlpha(0.34f);

        // ---- inversion -------------------------------------------------------------

        /// <summary>The near-white of an inverted surface: a selected pill, a card's header.</summary>
        public static readonly Color Light = UiPalette.Hex("#F2F6FB");

        /// <summary>Text and glyphs drawn on <see cref="Light"/>.</summary>
        public static readonly Color Ink = UiPalette.Hex("#142B45");

        /// <summary>Secondary text on <see cref="Light"/>.</summary>
        public static readonly Color InkSoft = UiPalette.Hex("#3E6087");

        // ---- accents ---------------------------------------------------------------

        /// <summary>Cursor, confirmation, done-checks. The one saturated green in the UI.</summary>
        public static readonly Color Lime = UiPalette.Hex("#9EE34A");

        /// <summary>Progress: the experience bar, level rings. Never used for health.</summary>
        public static readonly Color Cyan = UiPalette.Hex("#4FD8E8");

        /// <summary>Currency.</summary>
        public static readonly Color Gold = UiPalette.Hex("#FFC24B");

        /// <summary>
        /// Health stays on <c>UiPalette.Health</c>'s green→amber→red ramp.
        ///
        /// Stated here so the next person does not "fix" the inconsistency: the reference's
        /// health bar is green and its experience bar is cyan, sitting directly under it, and
        /// that pairing only works because the two never share a hue. Health is the one bar in
        /// the game whose colour carries information, and the ramp is that information.
        /// </summary>
        public static Color Health(float fraction) => UiPalette.Health(fraction);
    }
}
