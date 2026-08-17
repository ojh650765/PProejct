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
        private Coroutine _wipe;
        private Action _pending;

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

            // A caller blocked on a cover when the scene is torn down is a caller blocked for ever.
            // Resolving on the way out is the same promise the supersede path keeps.
            var pending = _pending;
            _pending = null;
            pending?.Invoke();
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

            Supersede();

            _pending = onComplete;
            _running = StartCoroutine(Play(cover, seconds));
        }

        /// <summary>
        /// Stands the in-flight request down and resolves its caller.
        ///
        /// A superseded request is not a cancelled one. Its caller is blocked on the callback — a
        /// door holds the player frozen until the screen is covered — and this used to StopCoroutine
        /// the play and drop that callback on the floor, which is the one thing
        /// <see cref="IScreenCover"/> promises never happens. Telling them the state was reached is
        /// true enough: the newer request owns the screen from here, and it is taking it somewhere.
        /// </summary>
        private void Supersede()
        {
            if (_wipe != null)
            {
                StopCoroutine(_wipe);
                _wipe = null;
            }
            if (_running != null)
            {
                StopCoroutine(_running);
                _running = null;
            }

            var superseded = _pending;
            _pending = null;
            superseded?.Invoke();
        }

        private IEnumerator Play(bool cover, float seconds)
        {
            var wipe = cover
                ? _overlay.CoverIn(Mathf.Max(0.01f, seconds), _style)
                : _overlay.CoverOut(Mathf.Max(0.01f, seconds), _style);

            // Two coroutines rather than one, because the budget below has to be real.
            //
            // The wipe yields a single nested enumerator and Unity is the thing that drives it, so
            // stepping it by hand here — which is what this did — called MoveNext twice for an
            // entire wipe. The budget accumulated two frames of unscaled delta against a one-second
            // floor and could not trip however long the wipe stalled, so the guard the comment
            // describes was not there at all. Handing the wipe to Unity and watching the clock
            // instead is what makes the bound the thing it claims to be.
            var finished = false;
            _wipe = StartCoroutine(RunWipe(wipe, () => finished = true));

            var budget = Mathf.Max(1f, seconds * 4f);
            var deadline = Time.realtimeSinceStartup + budget;
            while (!finished && Time.realtimeSinceStartup < deadline) yield return null;

            if (!finished)
            {
                // Forced to the state that was asked for rather than simply abandoned: a stalled
                // wipe left alone leaves a black pane over a playable game, which is the worst of
                // both.
                if (_wipe != null) StopCoroutine(_wipe);
                Debug.LogWarning($"[Cover] The wipe did not finish within {budget:F1}s; forcing " +
                                 "the screen to the state that was asked for.", this);
                _overlay.SetCoverage(cover ? 1f : 0f, Color.black);
            }

            _wipe = null;
            _running = null;

            var done = _pending;
            _pending = null;
            done?.Invoke();
        }

        private static IEnumerator RunWipe(IEnumerator wipe, Action onFinished)
        {
            yield return wipe;
            onFinished();
        }
    }
}
