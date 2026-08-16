using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Clears this assembly's static state when play mode starts.
    ///
    /// Unity's "Enter Play Mode Options" can skip the domain reload, which leaves every static
    /// field holding values from the previous session — stale event subscribers, a dead
    /// <see cref="TrainerRegistry"/>, roamers in <see cref="RoamingCreature.AllLive"/> that no
    /// longer exist. Those produce bugs that only appear on the second play, which is a
    /// spectacularly bad way to lose an afternoon.
    ///
    /// <see cref="ServiceHub"/> and <see cref="GameEvents"/> are Core's to reset and the boot
    /// scene owns that; this only clears what this assembly owns.
    /// </summary>
    public static class OverworldLifecycle
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            OverworldEvents.Reset();
            TrainerRegistry.Reset();
            EncounterTable.ResetBuiltIns();
            RoamingCreature.ResetRegistry();
            WaterBody.ResetRegistry();
        }
    }
}
