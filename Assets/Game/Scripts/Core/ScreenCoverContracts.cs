using System;

namespace PokeLab.Core
{
    /// <summary>
    /// Covering and uncovering the screen, for anything that is not a battle.
    ///
    /// <see cref="ITransitionDirector"/> already exists and is deliberately battle-shaped:
    /// its three entry points are the encounter intro, the battle outro and the reveal
    /// afterwards. Walking through a door is none of those, and widening that interface to
    /// carry doors would make every implementer of a battle transition answer questions
    /// about level loading.
    ///
    /// So this is the plain half. A door covers the screen, the scene is swapped behind it,
    /// and it uncovers — the presentation that makes a transition a transition rather than
    /// a cut. Without it, <see cref="ITransitionDirector"/>'s absence left the door code
    /// waiting out a fade duration and then hard-cutting anyway, which is a teleport with a
    /// pause in front of it.
    /// </summary>
    public interface IScreenCover
    {
        /// <summary>True once nothing behind the cover is visible.</summary>
        bool IsCovered { get; }

        /// <summary>
        /// Fades to the cover colour. <paramref name="onComplete"/> fires when the screen is
        /// fully covered — not before, or the swap happens in view.
        /// </summary>
        void Cover(float seconds, Action onComplete);

        /// <summary>Fades back. Called after whatever was hidden has finished changing.</summary>
        void Reveal(float seconds, Action onComplete);
    }
}
