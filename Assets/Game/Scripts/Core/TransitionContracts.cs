using System;
using System.Collections.Generic;

namespace PokeLab.Core
{
    /// <summary>
    /// The shape of the transition the cinematics layer provides.
    ///
    /// Every method is asynchronous by callback rather than blocking, and every implementation
    /// must invoke its callback exactly once — including on failure. A callback that never fires
    /// leaves the player frozen with no way out, so callers are expected to run a watchdog.
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

    /// <summary>
    /// Whoever stages and runs an actual battle. <see cref="IGameFlow"/> knows how to request an
    /// encounter but deliberately knows nothing about how one is built — this is the seam between
    /// the overworld and the battle scene.
    ///
    /// <paramref name="onResolved"/> must be invoked exactly once, including on failure. Returning
    /// a <see cref="BattleOutcome.Fled"/> result is the correct way to abort; never silently drop
    /// the callback, because the player is frozen until it fires.
    /// </summary>
    public interface IBattleStage
    {
        /// <summary>True once a battle is staged and running.</summary>
        bool IsBattleActive { get; }

        void BeginEncounter(EncounterRequest request, Action<EncounterResult> onResolved);
    }

    /// <summary>
    /// A trainer's identity and roster, resolved from the string id carried by
    /// <see cref="EncounterRequest.TrainerId"/>.
    /// </summary>
    [Serializable]
    public sealed class TrainerProfile
    {
        public string TrainerId;
        public string DisplayName;
        /// <summary>Art key for the trainer's model and portrait, resolved by the art registry.</summary>
        public string ArtKey;
        /// <summary>Prize money awarded on defeat.</summary>
        public int Reward;
        /// <summary>Line spoken when the battle starts.</summary>
        public string IntroLine;
        /// <summary>Line spoken on defeat.</summary>
        public string DefeatLine;
    }

    /// <summary>
    /// Resolves trainer ids to profiles and freshly built parties. The overworld authors trainers;
    /// the battle layer consumes them without knowing where they were defined.
    /// </summary>
    public interface ITrainerRegistry
    {
        bool TryGetProfile(string trainerId, out TrainerProfile profile);

        /// <summary>
        /// Builds a fresh party for the trainer. Returns a new list every call — the battle engine
        /// mutates what it is given, so handing out a shared instance would corrupt the definition.
        /// Returns an empty list for an unknown id rather than throwing.
        /// </summary>
        IReadOnlyList<CreatureInstance> BuildParty(string trainerId, int levelOffset = 0);
    }
}
