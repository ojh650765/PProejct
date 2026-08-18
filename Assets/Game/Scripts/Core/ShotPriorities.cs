namespace PokeLab.Core
{
    /// <summary>
    /// The one ladder every Cinemachine camera in the overworld climbs.
    ///
    /// Cinemachine arbitrates by priority, which means the numbers are a contract between
    /// systems that must never reference each other: the exploration rig, the episode
    /// runner's authored shots, the dialogue two-shot, and the starter close-up all take
    /// the camera by outbidding whoever holds it and give it back by dropping out. Before
    /// this file the ladder existed only as folklore — 100 in a private field here, 120 in
    /// a doc comment there, 150 as a magic number two files away — and folklore is how two
    /// workers pick the same rung.
    ///
    /// The gaps are deliberate. A future shot that must sit between two rungs has room
    /// without renumbering anybody.
    /// </summary>
    public static class ShotPriorities
    {
        /// <summary>The exploration follow rig. The resting state everything returns to.</summary>
        public const int Exploration = 100;

        /// <summary>
        /// Authored scene shots: an episode's marker-framed composition, a trainer-approach
        /// cinematic, a Timeline-driven sequence. Above exploration, below conversation —
        /// a dialogue that opens mid-episode takes the frame.
        /// </summary>
        public const int EpisodeShot = 120;

        /// <summary>
        /// The conversation two-shot that frames speaker and player while dialogue runs.
        /// Above the episode's own shot for the length of the exchange.
        /// </summary>
        public const int Dialogue = 130;

        /// <summary>
        /// The starter-case close-up. The one shot nothing may steal: a trainer noticing
        /// the player mid-choice must not pull the camera off the case. Matches the 150
        /// StarterCaseStage has always claimed.
        /// </summary>
        public const int Closeup = 150;
    }
}
