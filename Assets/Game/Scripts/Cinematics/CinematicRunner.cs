using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The coroutine host for every cinematic in the game.
    ///
    /// Cinematics outlive the objects they animate: a creature faints and its view is
    /// disabled mid-beat, a mode transition destroys the overworld camera halfway through
    /// the blend. Running those routines on the object being animated means the routine
    /// dies with it and the camera is left stranded, which is exactly the "abrupt cut" the
    /// brief forbids. So everything runs here, on a persistent hidden object.
    ///
    /// Everything is driven off <see cref="Time.unscaledDeltaTime"/> by default, because
    /// transitions deliberately freeze world time (<see cref="Time.timeScale"/> ramps to
    /// zero when the encounter starts) and a scaled-time coroutine would freeze with it.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class CinematicRunner : MonoBehaviour
    {
        private static CinematicRunner s_instance;
        private static bool s_quitting;

        /// <summary>
        /// The shared runner, created on demand. Returns null only while the application is
        /// shutting down, so callers can safely null-check instead of guarding with flags.
        /// </summary>
        public static CinematicRunner Instance
        {
            get
            {
                if (s_quitting) return null;
                if (s_instance != null) return s_instance;

                var go = new GameObject("~CinematicRunner") { hideFlags = HideFlags.HideAndDontSave };
                s_instance = go.AddComponent<CinematicRunner>();
                DontDestroyOnLoad(go);
                return s_instance;
            }
        }

        private void OnApplicationQuit() => s_quitting = true;

        private void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }

        // --- Running -------------------------------------------------------------------

        /// <summary>Starts a routine on the persistent host. Null-safe; returns null if shutting down.</summary>
        public static Coroutine Run(IEnumerator routine)
        {
            if (routine == null) return null;
            var inst = Instance;
            return inst == null ? null : inst.StartCoroutine(routine);
        }

        /// <summary>Stops a routine started by <see cref="Run"/>. Safe with a null handle.</summary>
        public static void Halt(Coroutine handle)
        {
            if (handle == null || s_instance == null) return;
            s_instance.StopCoroutine(handle);
        }

        // --- Waiting -------------------------------------------------------------------

        /// <summary>Unscaled-time wait. Non-negative durations only; zero yields one frame.</summary>
        public static IEnumerator Wait(float seconds)
        {
            if (seconds <= 0f) { yield return null; yield break; }
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        /// <summary>Waits until the predicate is true, with a safety timeout so a bad predicate cannot hang a battle.</summary>
        public static IEnumerator WaitUntil(Func<bool> predicate, float timeout = 10f)
        {
            float elapsed = 0f;
            while (predicate != null && !predicate() && elapsed < timeout)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        // --- Tweening ------------------------------------------------------------------

        /// <summary>
        /// Drives <paramref name="apply"/> with eased normalised progress for
        /// <paramref name="duration"/> seconds, guaranteeing a final call at exactly 1.
        /// The terminal call matters: without it a tween can end at 0.998 and leave a
        /// visible sub-pixel offset that reads as a permanently misaligned pose.
        /// </summary>
        public static IEnumerator Tween(float duration, CinematicEase.Function ease, Action<float> apply)
        {
            if (apply == null) yield break;
            ease ??= CinematicEase.InOutCubic;

            if (duration <= 0f)
            {
                apply(1f);
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                apply(ease(elapsed / duration));
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            apply(1f);
        }

        /// <summary>
        /// Like <see cref="Tween"/> but reports raw normalised time, for callers that shape
        /// the value themselves (pulses, oscillations, multi-segment beats).
        /// </summary>
        public static IEnumerator Progress(float duration, Action<float> apply)
            => Tween(duration, CinematicEase.Linear, apply);

        // --- Composition ---------------------------------------------------------------

        /// <summary>Runs routines one after another. Nulls are skipped.</summary>
        public static IEnumerator Sequence(params IEnumerator[] routines)
        {
            if (routines == null) yield break;
            for (int i = 0; i < routines.Length; i++)
            {
                if (routines[i] != null) yield return Run(routines[i]);
            }
        }

        /// <summary>
        /// Runs routines simultaneously and completes when the last one does.
        /// Implemented by starting real coroutines rather than hand-stepping enumerators,
        /// so nested <c>yield return</c> of sub-routines behaves exactly as it would inline.
        /// </summary>
        public static IEnumerator Parallel(params IEnumerator[] routines)
        {
            if (routines == null) yield break;

            var live = new List<Coroutine>(routines.Length);
            int remaining = 0;
            for (int i = 0; i < routines.Length; i++)
            {
                if (routines[i] == null) continue;
                remaining++;
                live.Add(Run(Counted(routines[i], () => remaining--)));
            }
            if (live.Count == 0) yield break;

            while (remaining > 0) yield return null;
        }

        private static IEnumerator Counted(IEnumerator inner, Action onDone)
        {
            yield return inner;
            onDone?.Invoke();
        }

        /// <summary>
        /// Starts <paramref name="routine"/> and returns immediately. Use for garnish that
        /// must not gate the beat — a dust puff, a secondary settle — never for anything
        /// the next event depends on.
        /// </summary>
        public static void Fork(IEnumerator routine) => Run(routine);

        /// <summary>Runs <paramref name="routine"/> after a delay, without blocking the caller.</summary>
        public static void ForkDelayed(float delay, IEnumerator routine)
            => Run(Sequence(Wait(delay), routine));
    }
}
