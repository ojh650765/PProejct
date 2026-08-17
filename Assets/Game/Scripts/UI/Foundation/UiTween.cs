using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>Easing curves. Named for intent rather than for the maths.</summary>
    public enum Ease
    {
        Linear = 0,
        /// <summary>Standard UI motion — fast out of the gate, settles gently. Use for most things.</summary>
        OutCubic = 1,
        InCubic = 2,
        InOutCubic = 3,
        /// <summary>Overshoots and settles. For elements that appear, never for values that must read exactly.</summary>
        OutBack = 4,
        /// <summary>Springy settle used by the probability gauge so a big jump feels physical.</summary>
        OutElastic = 5,
        OutExpo = 6,
        InOutQuad = 7,
    }

    /// <summary>
    /// A running tween. Hold one to cancel or complete it; a view that retargets a value
    /// every frame must kill the previous handle or the two will fight over the setter.
    /// </summary>
    public sealed class TweenHandle
    {
        internal float Elapsed;
        internal float Delay;
        internal float Duration;
        internal Ease Ease;
        internal bool Unscaled;
        internal Action<float> Apply;      // receives eased 0-1
        internal Action Completed;
        internal bool Alive = true;

        public bool IsAlive => Alive;

        /// <summary>Stops immediately, leaving the target wherever it currently sits.</summary>
        public void Kill()
        {
            Alive = false;
            Apply = null;
            Completed = null;
        }

        /// <summary>Jumps to the end value and fires the completion callback.</summary>
        public void Complete()
        {
            if (!Alive) return;
            Apply?.Invoke(1f);
            Alive = false;
            var done = Completed;
            Apply = null;
            Completed = null;
            done?.Invoke();
        }
    }

    /// <summary>
    /// A tiny tween engine.
    ///
    /// The project has no tween package and the UI brief requires that no numeric value
    /// ever snaps, so this exists rather than scattering coroutines through thirty views.
    /// One hidden runner ticks every active tween, which also means a single place to make
    /// the whole UI honour unscaled time while the game is paused.
    /// </summary>
    public static class UiTween
    {
        private static readonly List<TweenHandle> Active = new List<TweenHandle>(64);
        private static readonly List<TweenHandle> Buffer = new List<TweenHandle>(64);
        private static UiTweenRunner _runner;

        /// <summary>Set false by the options screen; every tween then completes instantly.</summary>
        public static bool MotionEnabled = true;

        private static void EnsureRunner()
        {
            if (_runner != null) return;
            var go = new GameObject("~UiTweenRunner") { hideFlags = HideFlags.HideAndDontSave };
            _runner = go.AddComponent<UiTweenRunner>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        /// <summary>Raw tween over normalised time. Prefer the typed helpers below.</summary>
        public static TweenHandle Run(float duration, Action<float> apply, Ease ease = Ease.OutCubic,
            float delay = 0f, bool unscaled = true, Action onComplete = null)
        {
            if (apply == null) return null;

            var handle = new TweenHandle
            {
                Duration = Mathf.Max(0.0001f, duration),
                Delay = Mathf.Max(0f, delay),
                Ease = ease,
                Unscaled = unscaled,
                Apply = apply,
                Completed = onComplete,
            };

            if (!MotionEnabled || duration <= 0f)
            {
                // Reduced motion removes motion, not time. A tween with nothing scheduled in
                // front of it lands on its final value this frame; one that was asked to happen
                // *later* still happens later, because the delay is pacing rather than
                // animation. The overlay director builds a banner's hold, a result card's hold
                // and the whole capture beat out of Delay, and collapsing those made the outcome
                // of a battle appear and vanish inside a single frame.
                if (handle.Delay <= 0f)
                {
                    handle.Complete();
                    return handle;
                }
                handle.Duration = 0.0001f;
            }

            EnsureRunner();
            Active.Add(handle);
            return handle;
        }

        /// <summary>Tweens a float and pushes each step to <paramref name="setter"/>.</summary>
        public static TweenHandle Float(float from, float to, float duration, Action<float> setter,
            Ease ease = Ease.OutCubic, float delay = 0f, bool unscaled = true, Action onComplete = null)
        {
            return Run(duration, t => setter(Mathf.LerpUnclamped(from, to, t)), ease, delay, unscaled, onComplete);
        }

        public static TweenHandle Color(Color from, Color to, float duration, Action<Color> setter,
            Ease ease = Ease.OutCubic, float delay = 0f, bool unscaled = true, Action onComplete = null)
        {
            return Run(duration, t => setter(UnityEngine.Color.LerpUnclamped(from, to, t)), ease, delay, unscaled, onComplete);
        }

        public static TweenHandle Fade(CanvasGroup group, float to, float duration,
            Ease ease = Ease.OutCubic, float delay = 0f, Action onComplete = null)
        {
            if (group == null) return null;
            var from = group.alpha;
            return Run(duration, t => { if (group != null) group.alpha = Mathf.LerpUnclamped(from, to, t); },
                ease, delay, true, onComplete);
        }

        public static TweenHandle Scale(Transform target, Vector3 to, float duration,
            Ease ease = Ease.OutBack, float delay = 0f, Action onComplete = null)
        {
            if (target == null) return null;
            var from = target.localScale;
            return Run(duration, t => { if (target != null) target.localScale = Vector3.LerpUnclamped(from, to, t); },
                ease, delay, true, onComplete);
        }

        public static TweenHandle AnchoredMove(RectTransform target, Vector2 to, float duration,
            Ease ease = Ease.OutCubic, float delay = 0f, Action onComplete = null)
        {
            if (target == null) return null;
            var from = target.anchoredPosition;
            return Run(duration, t => { if (target != null) target.anchoredPosition = Vector2.LerpUnclamped(from, to, t); },
                ease, delay, true, onComplete);
        }

        /// <summary>
        /// A one-shot "punch": scales up and settles back. Used for delta arrows and threat
        /// rows so a change is caught peripherally even if the player is reading elsewhere.
        /// </summary>
        public static TweenHandle Punch(Transform target, float magnitude = 0.18f, float duration = 0.34f)
        {
            if (target == null) return null;
            var baseScale = Vector3.one;
            return Run(duration, t =>
            {
                if (target == null) return;
                // Half sine gives a clean out-and-back with no discontinuity at either end.
                var pulse = Mathf.Sin(t * Mathf.PI) * magnitude;
                target.localScale = baseScale * (1f + pulse);
            }, Ease.Linear, 0f, true, () => { if (target != null) target.localScale = baseScale; });
        }

        /// <summary>Fires <paramref name="action"/> after a delay, on the shared runner.</summary>
        public static TweenHandle Delay(float seconds, Action action)
        {
            return Run(0.0001f, _ => { }, Ease.Linear, seconds, true, action);
        }

        /// <summary>
        /// Fades a <see cref="CanvasGroup"/> and switches its GameObject off once it has
        /// finished fading out. The one shape every open/close in this UI is built from.
        ///
        /// The caller's handle is taken by reference and killed on entry, because the case
        /// that matters is the reversal. A close fade still running when the same surface is
        /// shown again would otherwise finish against the state it captured when it started
        /// and deactivate an object that is now logically open — visible state contradicting
        /// <c>IsOpen</c> with nothing left to correct it, which is how a conversation or a
        /// battle HUD ends up invisible mid-scene. Superseding the tween a new call replaces
        /// is what keeps the two in step, so every caller here holds a handle rather than
        /// firing and forgetting.
        /// </summary>
        public static TweenHandle FadeActive(ref TweenHandle handle, CanvasGroup group, bool visible,
            float duration, Ease ease = Ease.OutCubic, Action onComplete = null)
        {
            Kill(ref handle);
            if (group == null)
            {
                onComplete?.Invoke();
                return null;
            }

            var target = group.gameObject;
            if (visible) target.SetActive(true);

            handle = Fade(group, visible ? 1f : 0f, duration, ease, 0f, () =>
            {
                if (!visible && target != null) target.SetActive(false);
                onComplete?.Invoke();
            });
            return handle;
        }

        /// <summary>Kills a handle and nulls the caller's reference in one call.</summary>
        public static void Kill(ref TweenHandle handle)
        {
            handle?.Kill();
            handle = null;
        }

        internal static void Tick(float scaled, float unscaled)
        {
            if (Active.Count == 0) return;

            // Copy first: an Apply callback is allowed to start new tweens (chained motion).
            Buffer.Clear();
            Buffer.AddRange(Active);
            Active.Clear();

            for (var i = 0; i < Buffer.Count; i++)
            {
                var tween = Buffer[i];
                if (!tween.Alive) continue;

                var dt = tween.Unscaled ? unscaled : scaled;
                if (tween.Delay > 0f)
                {
                    tween.Delay -= dt;
                    if (tween.Delay > 0f) { Active.Add(tween); continue; }
                    dt = -tween.Delay;
                    tween.Delay = 0f;
                }

                tween.Elapsed += dt;
                var normalised = Mathf.Clamp01(tween.Elapsed / tween.Duration);
                var eased = Evaluate(tween.Ease, normalised);

                try
                {
                    tween.Apply?.Invoke(eased);
                }
                catch (Exception e)
                {
                    // A throwing setter (usually a destroyed target) must not take the whole
                    // UI's motion down with it.
                    Debug.LogException(e);
                    tween.Kill();
                    continue;
                }

                if (normalised >= 1f)
                {
                    tween.Alive = false;
                    var done = tween.Completed;
                    tween.Apply = null;
                    tween.Completed = null;
                    done?.Invoke();
                }
                else
                {
                    Active.Add(tween);
                }
            }
        }

        /// <summary>Evaluates an easing curve at normalised time <paramref name="t"/> in 0-1.</summary>
        public static float Evaluate(Ease ease, float t)
        {
            t = Mathf.Clamp01(t);
            switch (ease)
            {
                case Ease.Linear:
                    return t;
                case Ease.OutCubic:
                    return 1f - Mathf.Pow(1f - t, 3f);
                case Ease.InCubic:
                    return t * t * t;
                case Ease.InOutCubic:
                    return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
                case Ease.InOutQuad:
                    return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;
                case Ease.OutExpo:
                    return Mathf.Approximately(t, 1f) ? 1f : 1f - Mathf.Pow(2f, -10f * t);
                case Ease.OutBack:
                {
                    const float c1 = 1.70158f;
                    const float c3 = c1 + 1f;
                    var p = t - 1f;
                    return 1f + c3 * p * p * p + c1 * p * p;
                }
                case Ease.OutElastic:
                {
                    if (t <= 0f) return 0f;
                    if (t >= 1f) return 1f;
                    const float c4 = (2f * Mathf.PI) / 3f;
                    return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
                }
                default:
                    return t;
            }
        }

        /// <summary>Drops every running tween. Called on play mode exit alongside ServiceHub.Reset.</summary>
        public static void Reset()
        {
            for (var i = 0; i < Active.Count; i++) Active[i].Kill();
            Active.Clear();
            Buffer.Clear();
        }
    }

    /// <summary>Hidden ticker. Created on demand; never authored into a scene.</summary>
    [DefaultExecutionOrder(-100)]
    internal sealed class UiTweenRunner : MonoBehaviour
    {
        private void Update() => UiTween.Tick(Time.deltaTime, Time.unscaledDeltaTime);
    }
}
