namespace PokeLab.Core
{
    /// <summary>
    /// Switches a debug entry point sets before anything else runs.
    ///
    /// The debug jumps open the game in the middle of it — a wild battle, an NPC, the starter
    /// case — and on a fresh save the opening episode auto-plays underneath them. Two attempts
    /// to avoid that by waiting failed, because both the jump and the opening wait on the
    /// player profile being registered and neither can be first reliably: whichever loses the
    /// race delivers a prologue on top of a live battle.
    ///
    /// A flag set during the scene-load callback, before any Start runs, has no race in it.
    /// It lives in Core because the runner that reads it is PokeLab.Overworld and the jump
    /// that writes it is PokeLab.Boot, and Overworld cannot see Boot.
    /// </summary>
    public static class DebugFlow
    {
        /// <summary>
        /// When true the opening does not auto-play, whatever the save says.
        ///
        /// Only ever set by a debug jump. A normal session never touches it, so the opening
        /// remains governed by its completion flag alone.
        /// </summary>
        public static bool SuppressOpening { get; set; }
    }
}
