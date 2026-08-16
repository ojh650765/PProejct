using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// How far the opening act has got, as one named state rather than a flag test copied into
    /// every system that has to care.
    ///
    /// <b>The prologue is the stretch between a new game and the Pokédex.</b> Across it the
    /// player is walked from the town gate to the lake bank by a script, and three ambient
    /// systems have to stand down for the length of it:
    ///
    /// <list type="bullet">
    /// <item>grass and water encounters (<see cref="EncounterDirector.AccumulatePressure"/>),</item>
    /// <item>the visible roamer population (<see cref="RoamingCreatureSpawner"/>), and</item>
    /// <item>trainer challenges (<see cref="TrainerController"/>).</item>
    /// </list>
    ///
    /// Each of those has its own reason, and each failure is one somebody would have to
    /// reproduce by playing the act:
    ///
    /// <list type="bullet">
    /// <item>Kes is a trainer standing in the field before the player owns a single Pokémon.
    /// His sight cone does not know that. Left armed, he challenges a player with an empty
    /// party — a battle that cannot be fought and cannot be left.</item>
    /// <item>The staged ambush is the moment the whole act is built to deliver, and a random
    /// grass encounter fires from the same three metres of ground. Two encounters competing for
    /// one moment means the scripted one is refused (<c>_sequenceRunning</c> is already true) and
    /// the player fights a Pidgey instead of the thing the script placed in front of them.</item>
    /// <item>A roamer wandering the bank during Linden's muttering is a second creature in a
    /// scene that is about the first one.</item>
    /// </list>
    ///
    /// It is one boolean, read from one flag, exposed under names that say what each caller
    /// means by it — so the three cannot half-lift. <c>story.pokedex</c> is the key because it is
    /// the thing the act is *for*: it is granted by <c>field_professor_returns</c>, it survives a
    /// save, and it is already what the rest of the game reads to know the player is a trainer
    /// rather than a child who was told to stay in town.
    ///
    /// No profile means the game is still booting, not that the prologue is over — the
    /// conservative reading, matching <see cref="StoryGate"/> and <see cref="StoryEncounter"/>.
    /// Booting with the ambient systems briefly live is how a wild battle starts underneath the
    /// prologue's first fade.
    /// </summary>
    public static class StoryPhase
    {
        /// <summary>The flag that ends the prologue. Granted by <c>field_professor_returns</c>.</summary>
        public const string PokedexFlag = "story.pokedex";

        /// <summary>
        /// True from a new game until the player holds the Pokédex. The single source of truth;
        /// everything below is this under another name.
        /// </summary>
        public static bool PrologueActive
        {
            get
            {
                var profile = ServiceHub.TryGet<IPlayerProfile>(out var found)
                    ? found as PlayerProfile
                    : null;
                return profile == null || !profile.GetFlagBool(PokedexFlag);
            }
        }

        /// <summary>Grass, water and roamer-contact encounters stand down while this is true.</summary>
        public static bool WildEncountersSuppressed => PrologueActive;

        /// <summary>The roamer population is held at zero while this is true.</summary>
        public static bool RoamersSuppressed => PrologueActive;

        /// <summary>No trainer notices, challenges or can be talked into a fight while this is true.</summary>
        public static bool TrainerChallengesSuppressed => PrologueActive;
    }
}
