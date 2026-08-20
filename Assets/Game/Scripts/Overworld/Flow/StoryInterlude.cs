namespace PokeLab.Overworld
{
    /// <summary>
    /// "Something scripted has the player right now — do not start anything."
    ///
    /// <b>Why this is not just EpisodeRunner.IsPlaying.</b> Everything that can interrupt the
    /// player already asks the runner that question: <see cref="StoryEncounter"/> checks it at
    /// the top of its distance test, <see cref="StoryGate"/> checks it before the gatekeeper
    /// speaks unprompted. But a hold does not have to be an episode. The gate walks the player
    /// back from the ramp after Bram has finished with them, and for the length of that walk the
    /// player is not in control and must not be spoken to — by Kes' proximity trigger, or by
    /// Bram again as they pass back out through his own radius. Making the gate pretend to be a
    /// running episode to buy that would give it a completion flag it does not have and a beat
    /// list it does not own.
    ///
    /// Counted rather than boolean. Two holds can overlap — a scripted retreat that ends while a
    /// dialogue is still closing — and the second one to finish must not clear the first one's
    /// hold. Anything that begins one is responsible for ending it, including on the failure
    /// path, which is why every caller does it in a finally or on the far side of a guard loop.
    /// </summary>
    public static class StoryInterlude
    {
        private static int s_depth;

        /// <summary>True while any hold is open. Read every frame; deliberately trivial.</summary>
        public static bool Active => s_depth > 0;

        public static void Begin() => s_depth++;

        public static void End()
        {
            if (s_depth > 0) s_depth--;
        }

        /// <summary>
        /// Drops every hold.
        ///
        /// For a scene teardown, where a coroutine holding one has been destroyed mid-flight and
        /// there is nobody left to balance it. Without this a leaked hold is permanent and
        /// silent: the game keeps running, and nothing ever talks to the player again.
        /// </summary>
        public static void Clear() => s_depth = 0;
    }
}
