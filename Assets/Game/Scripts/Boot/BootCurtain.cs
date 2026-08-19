using System.Collections;
using PokeLab.Cinematics;
using PokeLab.Core;
using PokeLab.Overworld;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Covers the screen from the first engine frame of a fresh boot, and keeps it covered
    /// until whoever legitimately owns the reveal has taken over.
    ///
    /// <see cref="ScreenTransitionOverlay"/> boots transparent — its Awake sets coverage to
    /// zero — and nothing raises it until the opening episode's FadeOut beat, seconds later,
    /// after <see cref="EpisodeRunner"/> has waited out the profile service. The deployed
    /// build showed the plaza, the player and a camera half buried in trees before the
    /// prologue's black. This host closes that hole from code, which is the only fix that
    /// needs no scene edit and survives every future scene.
    ///
    /// Fresh boot only, by construction: RuntimeInitializeOnLoadMethod runs once per
    /// process. Covers raised for later scene loads belong to LevelTransition and
    /// ArrivalPlacer and are never touched from here.
    ///
    /// The handoff to the opening is not "stand back": the FadeOut beat drives
    /// <see cref="ScreenTransitionOverlay.CoverIn"/>, and CinematicRunner.Tween applies
    /// progress zero on its first frame — from an already-covered screen the beat would snap
    /// the world visible and re-fade it over 0.6s, which is the very flash this exists to
    /// remove. So the curtain holds through that beat and releases at the first beat past it.
    /// </summary>
    [DefaultExecutionOrder(1000)] // LateUpdate must be the frame's last coverage write: after
                                  // the overlay's own LateUpdate (-400) and after every
                                  // coroutine-driven wipe, which all run before LateUpdate.
    public sealed class BootCurtain : MonoBehaviour
    {
        // Real seconds throughout: the boot may run at timescale zero under a debug pause.
        private const float OverlaySearchSeconds = 2f;   // the overlay can arrive with a streamed scene a frame or two late
        private const float RunnerSearchSeconds = 2.5f;  // no EpisodeRunner by then means no episode ever starts
        private const float DecisionSeconds = 10f;       // generous roof over the runner's own 5s profile wait
        private const float ProfileGraceSeconds = 1f;    // frames the runner needs to act once the profile registers
        private const float BridgeCapSeconds = 12f;      // the FadeOut beat is itself bounded at ~8.6s by RunBounded
        private const float StrandedSeconds = 2.5f;      // covered, nothing playing, nobody coming: reveal
        private const float RevealSeconds = 0.55f;       // ArrivalPlacer's _revealSeconds, so boot feels like every other arrival

        // The serialized default of EpisodeRunner._openingEpisodeId, which is private. Only
        // the opening manages the screen itself; a chain link resumed at startup plays in
        // full view and must be revealed for, so the id decides who owns the reveal.
        private const string OpeningEpisodeId = "opening";

        private static BootCurtain s_instance;

        private ScreenTransitionOverlay _overlay;
        private bool _hold;
        private bool _selfFade;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (s_instance != null) return;
            var go = new GameObject("PL_BootCurtain");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<BootCurtain>();
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }
            s_instance = this;

            // Everything up to the first yield runs synchronously inside Bootstrap, which is
            // still before the first rendered frame — the cover lands before anything shows.
            StartCoroutine(Run());
        }

        private void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }

        private void LateUpdate()
        {
            // While holding, out-write everyone. The overlay's own LateUpdate re-stamps its
            // last applied state and the FadeOut beat writes rising progress from a coroutine;
            // both happen earlier in the frame than this, so the rendered frame stays covered
            // whatever they wrote.
            if (_hold && _overlay != null) _overlay.SetCoverage(1f, Color.black);
        }

        private IEnumerator Run()
        {
            var bootAt = Time.realtimeSinceStartup;

            // The overlay usually exists by AfterSceneLoad — TransitionDirector creates one in
            // the first scene — but it may ride in on a streamed scene a frame later.
            while (_overlay == null && Time.realtimeSinceStartup - bootAt < OverlaySearchSeconds)
            {
                _overlay = FindFirstObjectByType<ScreenTransitionOverlay>();
                if (_overlay == null) yield return null;
            }

            if (_overlay == null)
            {
                // A scene with no cinematics layer — an editor scene opened directly. Nothing
                // to cover with, nothing to do, and not worth a log line every play session.
                Destroy(gameObject);
                yield break;
            }

            _overlay.SetCoverage(1f, Color.black);
            _hold = true;

            // --- Decide who reveals -----------------------------------------------------

            EpisodeRunner runner = null;
            IPlayerProfile profile = null;
            var profileSeenAt = 0f;
            var openingOwns = false;

            while (Time.realtimeSinceStartup - bootAt < DecisionSeconds)
            {
                if (runner == null) runner = FindFirstObjectByType<EpisodeRunner>();

                if (runner != null && runner.IsPlaying)
                {
                    // Play() assigns _playingId before starting its coroutine, so the id is
                    // trustworthy on the same frame IsPlaying first reads true.
                    openingOwns = runner.PlayingEpisodeId == OpeningEpisodeId;
                    break;
                }

                // A debug jump wants the state it asked for, on screen, now.
                if (DebugFlow.SuppressOpening) break;

                // The runner decides synchronously on the frame the profile registers. Once
                // the profile has been visible for a grace window and nothing is playing, the
                // runner has declined (opening already seen, or it does not exist) — the
                // curtain owns the reveal. Same when no runner ever appears.
                if (profile == null && ServiceHub.TryGet<IPlayerProfile>(out profile))
                    profileSeenAt = Time.realtimeSinceStartup;
                if (profile != null && Time.realtimeSinceStartup - profileSeenAt >= ProfileGraceSeconds) break;
                if (runner == null && Time.realtimeSinceStartup - bootAt >= RunnerSearchSeconds) break;

                yield return null;
            }

            if (!openingOwns)
            {
                Reveal();
                yield return CleanUp();
                yield break;
            }

            // --- The opening owns the screen: bridge its FadeOut, then stand down ---------

            var bridgeCap = Time.realtimeSinceStartup + BridgeCapSeconds;
            while (Time.realtimeSinceStartup < bridgeCap && runner != null && runner.IsPlaying)
            {
                // CurrentBeat is "Kind" or "Kind:id"; null until the first beat performs.
                // Held through TakeControl and the FadeOut itself; the first beat past them
                // (CameraTo, in the authored opening) means the wipe has finished at full
                // coverage by its own hand and the episode's FadeIn owns the reveal from here.
                var beat = runner.CurrentBeat;
                if (!string.IsNullOrEmpty(beat)
                    && !beat.StartsWith("TakeControl")
                    && !beat.StartsWith("FadeOut"))
                    break;
                yield return null;
            }
            _hold = false;

            // --- Watchdog: degrade, never strand ------------------------------------------
            //
            // The opening can die between its FadeOut and its FadeIn — an exception in a
            // beat, a StopCoroutine — and leave a black pane over a playable game. Covered
            // with nothing playing for a sustained stretch has no legitimate owner left, so
            // the curtain reveals. While an episode is playing, black is presumed authored
            // (the prologue holds it for half a minute) and the wait is indefinite.
            var strandedFor = 0f;
            while (_overlay != null && _overlay.IsCovered)
            {
                var playing = runner != null && runner.IsPlaying;
                strandedFor = playing ? 0f : strandedFor + Time.unscaledDeltaTime;
                if (strandedFor >= StrandedSeconds)
                {
                    Debug.Log("[BootCurtain] The opening left the screen covered with nothing " +
                              "playing; revealing so the game is playable.");
                    Reveal();
                    break;
                }
                yield return null;
            }

            yield return CleanUp();
        }

        /// <summary>
        /// The plain fade, matching ArrivalPlacer's feel. Preferring <see cref="IScreenCover"/>
        /// keeps the supersede semantics: a door opened mid-reveal takes the screen over
        /// cleanly instead of fighting a second writer.
        /// </summary>
        private void Reveal()
        {
            _hold = false;
            if (ServiceHub.TryGet<IScreenCover>(out var cover) && cover != null)
            {
                cover.Reveal(RevealSeconds, null);
            }
            else if (_overlay != null)
            {
                // No ScreenCoverHost registered — a partially composed scene. The overlay's
                // own sequence is driven from here, and CleanUp force-clears if it stalls,
                // because this coroutine dies with this host.
                _selfFade = true;
                StartCoroutine(_overlay.CoverOut(RevealSeconds, WipeStyle.Fade));
            }
        }

        /// <summary>
        /// Waits for any reveal this component started to land before the host goes away —
        /// a coroutine dies with its host — then removes the host: the curtain's whole job
        /// is over by the first reveal, and nothing of it should keep running.
        /// </summary>
        private IEnumerator CleanUp()
        {
            var deadline = Time.realtimeSinceStartup + RevealSeconds * 4f + 1f;
            while (_overlay != null && _overlay.Coverage > 0.005f
                   && Time.realtimeSinceStartup < deadline)
                yield return null;

            // Only a fade this host drives itself is forced on a stall — ScreenCoverHost
            // already forces its own stalls, and residual coverage on that path can be a
            // door legitimately covering the screen, which must not be cut out from under.
            if (_selfFade && _overlay != null && _overlay.Coverage > 0.005f)
                _overlay.SetCoverage(0f, Color.black);

            Destroy(gameObject);
        }
    }
}
