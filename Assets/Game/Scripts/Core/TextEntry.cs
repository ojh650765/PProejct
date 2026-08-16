namespace PokeLab.Core
{
    /// <summary>
    /// Whether the player is currently typing.
    ///
    /// Dialogue advances on the space bar, and a name is typed into a field that sits over the
    /// dialogue box — so the space in "Red Jr" would both put a space in the name and skip the
    /// line asking for it. The field cannot simply swallow the key either: it is the runner,
    /// not the field, that decides a line is finished.
    ///
    /// A flag in Core rather than a check on the focused object, because the runner is in
    /// PokeLab.Overworld and every text field in the game is in PokeLab.UI — the two cannot
    /// see each other, and Core is what both already depend on.
    ///
    /// Whoever opens a field sets it, and must clear it: a stuck flag is a conversation that
    /// can never be advanced again.
    /// </summary>
    public static class TextEntry
    {
        /// <summary>True while a text field has the keyboard.</summary>
        public static bool IsTyping { get; private set; }

        /// <summary>Claims the keyboard for a field.</summary>
        public static void Begin() => IsTyping = true;

        /// <summary>Gives the keyboard back.</summary>
        public static void End() => IsTyping = false;
    }
}
