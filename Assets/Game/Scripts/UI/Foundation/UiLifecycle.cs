using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>
    /// Resets every static cache the UI layer owns when play mode starts.
    ///
    /// This matters because three of them hold Unity objects: the generated sprite atlas,
    /// the type icon set, and the running tween list. With domain reload disabled — which is
    /// the default for fast iteration in Unity 6 — those statics survive between play
    /// sessions while the <see cref="Texture2D"/> instances behind them do not, and the
    /// second run then draws from destroyed textures. Clearing on
    /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/> is the earliest hook
    /// that runs before any UI Awake, so nothing ever observes the stale state.
    ///
    /// Service caches are reset here too, so a boot scene re-registering services is picked
    /// up rather than a previous session's objects being held alive.
    /// </summary>
    public static class UiLifecycle
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnEnterPlayMode()
        {
            UiTween.Reset();
            UiTween.MotionEnabled = true;
            UiFocus.Reset();
            UiServices.Reset();
            UiSprites.Reset();
            UiTypeIcons.Reset();
        }

        /// <summary>
        /// Manual teardown for callers that shut the UI down mid-session, such as a scene
        /// unload that will not be followed by another play-mode entry.
        /// </summary>
        public static void Reset() => ResetOnEnterPlayMode();
    }
}
