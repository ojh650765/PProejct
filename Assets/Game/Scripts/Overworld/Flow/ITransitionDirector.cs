using System;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The shape of the transition the cinematics layer provides.
    ///
    /// <b>This interface should live in <c>Core</c>, not here.</b> It is declared in this assembly
    /// only because the frozen contract does not include one and no worker may edit <c>Core</c>.
    /// The consequence is that the cinematics worker cannot implement it without referencing
    /// <c>PokeLab.Overworld</c>, which the ownership table forbids — so
    /// <see cref="GameFlowController"/> resolves it two ways: through
    /// <see cref="ServiceHub.TryGet{T}"/> for the day the integrator promotes it into <c>Core</c>,
    /// and through a duck-typed <see cref="ServiceBridge"/> against a serialized component in the
    /// meantime. Both paths call the same method names with the same argument shapes.
    ///
    /// Every method is asynchronous by callback rather than blocking, and every implementation
    /// must invoke its callback exactly once — including on failure. A callback that never fires
    /// leaves the player frozen with no way out, which is why
    /// <see cref="GameFlowController"/> also runs a watchdog.
    /// </summary>
    public interface ITransitionDirector
    {
        /// <summary>True while a transition is on screen. The flow will not start a second one.</summary>
        bool IsTransitioning { get; }

        /// <summary>
        /// Plays the exploration-to-battle transition. Invoke <paramref name="onComplete"/> once
        /// the screen is covered and the battle may be staged unseen.
        /// </summary>
        void PlayEncounterIntro(EncounterRequest request, Action onComplete);

        /// <summary>
        /// Plays the battle-to-exploration transition. Invoke <paramref name="onComplete"/> once
        /// the overworld may be restored unseen; the flow reveals afterwards.
        /// </summary>
        void PlayBattleOutro(EncounterResult result, Action onComplete);

        /// <summary>
        /// Reveals the world again after the overworld has been restored. Called last, so the
        /// player never sees the frame where they were repositioned.
        /// </summary>
        void PlayReveal(Action onComplete);
    }
}
