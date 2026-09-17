using PokeLab.UI;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Hands the UI layer a way to draw a creature.
    ///
    /// <b>The problem this solves.</b> Two halves of the game resolved creature art by different
    /// routes and only one of them worked. The menus -- 가챠, 내 포켓몬 -- go through
    /// <see cref="CreatureThumbnail"/>, which loads the sprite sheet the build actually ships and
    /// draws a real creature. The battle screens ask <c>ICreatureArtRegistry</c>, which nothing
    /// registers with real art, so every one of them fell back to a coloured type glyph. The swap
    /// list in the middle of a battle is the place a player most needs to recognise a creature at
    /// a glance, and it was the place showing the least.
    ///
    /// <b>Why a bridge and not a reference.</b> PokeLab.Boot already references PokeLab.UI, so the
    /// UI assembly cannot reference Boot back without a cycle. A one-line resolver pushed in at
    /// startup crosses that line in the only direction it can, and leaves the UI assembly buildable
    /// and testable on its own.
    ///
    /// <b>Why RuntimeInitializeOnLoadMethod.</b> No scene has to remember to wire it, which is the
    /// failure mode that left the registry empty in the first place.
    /// </summary>
    public static class CreaturePortraitBridge
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            UiServices.PortraitResolver = CreatureThumbnail.Front;
        }
    }
}
