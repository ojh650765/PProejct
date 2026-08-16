using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Whether the player has a Pokémon that can carry them across water, and which one.
    ///
    /// The series gates this on the Surf HM, and that is what this should eventually ask.
    /// It cannot yet: the authored move pool is 32 moves and Surf is not among them, so a
    /// check for <c>MoveId == "surf"</c> would be permanently false and the lake would be a
    /// wall with no key. Gating on the Water type instead is real, works against the data
    /// that exists, and is the same idea one step earlier — a Gyarados carries you and a
    /// Charmander does not.
    ///
    /// SEAM: when moves.json gains Surf, add the move check *first* and keep the type check
    /// as the fallback. Both are answering "can this creature take me across", and neither
    /// belongs to the water plane that asks.
    /// </summary>
    public static class SurfCapability
    {
        /// <summary>
        /// The first party member that can carry the player, or null.
        ///
        /// First rather than best on purpose: the player's lead Pokémon is the one they
        /// think of as theirs, and picking the strongest swimmer would put a creature they
        /// did not choose under their feet.
        /// </summary>
        public static CreatureInstance Mount()
        {
            if (!ServiceHub.TryGet<IPlayerProfile>(out var profile) || profile?.Party == null)
                return null;
            if (!ServiceHub.TryGet<ISpeciesRegistry>(out var species) || species == null)
                return null;

            foreach (var member in profile.Party)
            {
                if (member == null) continue;
                // A fainted Pokémon cannot be asked to swim, which is also what stops the
                // lake becoming an escape route from a party wipe.
                if (member.CurrentHp <= 0) continue;
                if (!species.TryGet(member.SpeciesId, out var data) || data == null) continue;
                if (data.Type1 == ElementType.Water || data.Type2 == ElementType.Water)
                    return member;
            }
            return null;
        }

        public static bool CanSurf() => Mount() != null;
    }
}
