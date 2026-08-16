using System;
using System.Collections;
using PokeLab.Cinematics;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Lets anything ask for the screen to be covered, using the wipe the cinematics layer
    /// already owns.
    ///
    /// <see cref="ScreenTransitionOverlay"/> has had <c>CoverIn</c> and <c>CoverOut</c> since
    /// it was written, and nothing outside its own assembly could call them: the overworld
    /// cannot reference PokeLab.Cinematics. So a door had no way to reach the one component
    /// in the project that can cover a screen, and settled for waiting out its fade duration
    /// and then hard-cutting — the teleport.
    ///
    /// PokeLab.Boot is the assembly that can see both, which is the same reason
    /// <see cref="DialoguePresenter"/> lives here. The overworld asks through
    /// <see cref="IScreenCover"/> on the service hub and never learns what draws it.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-450)]
    public sealed class ScreenCoverHost : MonoBehaviour, IScreenCover
    {
        [SerializeField] private ScreenTransitionOverlay _overlay;

        [Tooltip("A plain fade rather than the shutter. A door is not an encounter, and " +
                 "borrowing the battle's wipe would tell the player something is about to " +
                 "start a fight.")]
        [SerializeField] private WipeStyle _style = WipeStyle.Fade;

        private Coroutine _running;

        public bool IsCovered => _overlay != null && _overlay.IsCovered;

        private static ScreenCoverHost _instance;

        private void Awake()
        {
            // First one wins. A streamed scene brings its own GameHosts, whose copy of this
            // registered over the original on the hub and was then stripped — so the next
            // door to ask for a cover called a destroyed object and threw from inside the
            // transition, with the player already committed to walking through it.
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;

            if (_overlay == null) _overlay = FindFirstObjectByType<ScreenTransitionOverlay>();
            ServiceHub.Register<IScreenCover>(this);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        public void Cover(float seconds, Action onComplete) => Run(true, seconds, onComplete);

        public void Reveal(float seconds, Action onComplete) => Run(false, seconds, onComplete);

        private void Run(bool cover, float seconds, Action onComplete)
        {
            if (_overlay == null)
            {
                // The caller is mid-transition and waiting on this. Completing immediately
                // gives them a hard cut, which is what they had before — never a hang.
                Debug.LogWarning("[Cover] No ScreenTransitionOverlay, so the screen cannot be " +
                                 "covered and the change will be visible.", this);
                onComplete?.Invoke();
                return;
            }

            if (_running != null) StopCoroutine(_running);
            _running = StartCoroutine(Play(cover, seconds, onComplete));
        }

        private IEnumerator Play(bool cover, float seconds, Action onComplete)
        {
            var wipe = cover
                ? _overlay.CoverIn(Mathf.Max(0.01f, seconds), _style)
                : _overlay.CoverOut(Mathf.Max(0.01f, seconds), _style);

            // Bounded, and the screen is forced to the state that was asked for on overrun.
            // A stalled wipe that is simply abandoned leaves a black pane over a playable
            // game, which is the worst of both.
            var elapsed = 0f;
            var budget = Mathf.Max(1f, seconds * 4f);
            while (elapsed < budget && wipe.MoveNext())
            {
                elapsed += Time.unscaledDeltaTime;
                yield return wipe.Current;
            }

            if (elapsed >= budget)
            {
                Debug.LogWarning($"[Cover] The wipe did not finish within {budget:F1}s; forcing " +
                                 "the screen to the state that was asked for.", this);
                _overlay.SetCoverage(cover ? 1f : 0f, Color.black);
            }

            _running = null;
            onComplete?.Invoke();
        }
    }
}
