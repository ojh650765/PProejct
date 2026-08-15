using System;
using System.Collections.Generic;
using System.Text;

namespace PokeLab.Core
{
    /// <summary>
    /// Deterministic identity for creatures. Replaces the <c>Guid.NewGuid()</c> that used to
    /// default <see cref="CreatureInstance.InstanceId"/> — a random id silently made any battle
    /// event stream carrying it unreproducible, defeating the engine's replay guarantee.
    /// </summary>
    public static class CreatureIds
    {
        /// <summary>
        /// Builds a stable id from the facts that identify a creature at creation time.
        /// The same arguments always produce the same id, so a replay from a seed reproduces
        /// the whole event stream including <see cref="ExperienceGainedEvent.InstanceId"/>.
        /// </summary>
        /// <param name="speciesId">Species this creature is an instance of.</param>
        /// <param name="level">Level at creation.</param>
        /// <param name="seed">The creating system's deterministic seed.</param>
        /// <param name="ordinal">Distinguishes creatures made from the same seed, e.g. party slot.</param>
        public static string Derive(int speciesId, int level, int seed, int ordinal)
        {
            // FNV-1a over the four fields. Cheap, allocation-free, and stable across runs and
            // platforms — unlike string.GetHashCode, which is randomised per process in .NET.
            unchecked
            {
                var hash = 2166136261u;
                hash = Mix(hash, (uint)speciesId);
                hash = Mix(hash, (uint)level);
                hash = Mix(hash, (uint)seed);
                hash = Mix(hash, (uint)ordinal);

                var sb = new StringBuilder(20);
                sb.Append('c').Append(speciesId.ToString("D4"));
                sb.Append('l').Append(level.ToString("D3"));
                sb.Append('h').Append(hash.ToString("X8"));
                return sb.ToString();
            }
        }

        private static uint Mix(uint hash, uint value)
        {
            unchecked
            {
                for (var i = 0; i < 4; i++)
                {
                    hash ^= (value >> (i * 8)) & 0xFF;
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        /// <summary>Assigns a derived id only when one is missing, so restored saves keep theirs.</summary>
        public static void EnsureId(CreatureInstance creature, int seed, int ordinal)
        {
            if (creature == null || !string.IsNullOrEmpty(creature.InstanceId)) return;
            creature.InstanceId = Derive(creature.SpeciesId, creature.Level, seed, ordinal);
        }
    }

    /// <summary>
    /// Lets the player choose which creature comes in after theirs faints, instead of the
    /// engine sending out the first healthy party member.
    ///
    /// Optional by design: the engine resolves this through <see cref="ServiceHub.TryGet{T}"/>
    /// and falls back to its own choice when nothing is registered, so battles still run
    /// headlessly and in tests. The UI registers an implementation that prompts the player;
    /// the choice must still be made synchronously from the engine's point of view, so an
    /// implementation that needs player input has to pump its own wait.
    /// </summary>
    public interface IReplacementChooser
    {
        /// <summary>
        /// Returns the party index to send out. <paramref name="legalIndices"/> lists the
        /// healthy, non-active members in party order and is never empty when this is called.
        /// Returning an index outside that set makes the engine fall back to the first legal one,
        /// so a confused implementation degrades instead of corrupting the battle.
        /// </summary>
        int ChooseReplacement(BattleSide side, IBattleStateView state, IReadOnlyList<int> legalIndices);
    }
}
