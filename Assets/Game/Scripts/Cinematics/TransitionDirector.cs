using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Cinemachine;
using PokeLab.Core;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The implementation of <see cref="ITransitionDirector"/>: the only object in the
    /// project allowed to change what the player is looking at between modes.
    ///
    /// The flow this participates in is owned by <c>GameFlowController</c> and runs
    /// <b>freeze → cover → stage → await → restore → reveal</b>. The division of labour is
    /// exact, and getting it wrong is visible:
    ///
    /// <list type="bullet">
    /// <item><see cref="PlayEncounterIntro"/> plays the camera move and covers the screen,
    /// then calls back. The flow stages the battle after that callback, so the callback must
    /// not fire until the cover is opaque — call back early and the player watches the arena
    /// assemble. This routine then keeps running: it holds the cover while staging happens,
    /// reveals onto the battle, brings the grade up and flies the HUD in.</item>
    /// <item><see cref="PlayBattleOutro"/> covers and calls back, and deliberately does
    /// <b>not</b> reveal — the flow repositions the player after this callback.</item>
    /// <item><see cref="PlayReveal"/> uncovers, once the player is already back where they
    /// belong.</item>
    /// </list>
    ///
    /// Every callback fires exactly once, including on failure: each sequence fires from a
    /// <c>finally</c> so a destroyed object or a stopped coroutine still releases the flow,
    /// and a watchdog fires it anyway if a sequence overruns. A dropped callback freezes the
    /// player with no way out, which is the worst failure this system can produce.
    ///
    /// The encounter swing is derived from <see cref="EncounterRequest.WorldPosition"/> and
    /// <see cref="EncounterRequest.PlayerRotation"/>, not from wherever the camera happened
    /// to be, so the geography stays continuous: the player sees the ground they were
    /// standing on rotate away underneath the move.
    ///
    /// <b>The battle is a scene load, not a root swap.</b> Both halves of the round trip carry
    /// one under the cover: <c>Battle.unity</c> is loaded additively on the way in and unloaded
    /// on the way out, and the player is carried across to the arena and back. Toggling roots in
    /// the level scene was the old mechanism and it could not work, because the thing being
    /// staged has to stand somewhere the battle camera can move without clipping through
    /// scenery, and the level has no such place in it. See <see cref="BattleArena"/> for why the
    /// load is additive rather than single.
    /// </summary>
    [DefaultExecutionOrder(-450)]
    public sealed class TransitionDirector : MonoBehaviour, ITransitionDirector
    {
        [Header("Scene wiring")]
        [Tooltip("The brain. Found on the main camera when empty.")]
        [SerializeField] private CinemachineBrain brain;
        [Tooltip("The overworld follow rig. Re-prioritised on the way back out.")]
        [SerializeField] private CinemachineCamera overworldFollowCamera;
        [Tooltip("Wipe layer. Created on this object when empty.")]
        [SerializeField] private ScreenTransitionOverlay overlay;

        [Header("Battle level")]
        [Tooltip("Scene holding the arena. Loaded additively under the cover and unloaded on the " +
                 "way out; must be in File > Build Settings or no encounter can be shown.")]
        [SerializeField] private string battleSceneName = "Battle";
        [Tooltip("Seconds the load may take before the sequence gives up and shows the encounter " +
                 "in whatever state it reached. A stalled load must never strand the player.")]
        [SerializeField] private float sceneLoadTimeout = 8f;

        [Header("Encounter swing")]
        [Tooltip("Seconds spent swinging around the player onto the creature.")]
        [SerializeField] private float swingDuration = 1.05f;
        [Tooltip("Degrees the camera arcs around the player during the swing.")]
        [SerializeField] private float swingArc = 145f;
        [Tooltip("Radius of the swing, in metres from the player.")]
        [SerializeField] private float swingRadius = 4.2f;
        [Tooltip("Camera height above the player during the swing.")]
        [SerializeField] private float swingHeight = 1.9f;
        [Tooltip("Metres the camera dollies in over the course of the swing.")]
        [SerializeField] private float swingDolly = 1.3f;

        [Header("Wipe")]
        [SerializeField] private float coverDuration = 0.42f;
        [SerializeField] private float revealDuration = 0.55f;
        [Tooltip("Held covered after the stage callback fires, so staging has frames to settle " +
                 "before the reveal catches it mid-assembly.")]
        [SerializeField] private float stagingHold = 0.4f;

        [Header("World freeze")]
        [Tooltip("Ramp world time to zero for the duration of a transition.")]
        [SerializeField] private bool freezeWorld = true;
        [Tooltip("Seconds spent ramping time to zero. A hard stop reads as a hitch.")]
        [SerializeField] private float freezeRamp = 0.22f;
        [Tooltip("Seconds spent ramping time back up on the way out.")]
        [SerializeField] private float thawRamp = 0.4f;

        [Header("Battle intro")]
        [Tooltip("Held while the trainers and creatures take their marks.")]
        [SerializeField] private float marksHold = 0.7f;
        [Tooltip("Held on the battle grade card.")]
        [SerializeField] private float gradeHold = 1.0f;
        [Tooltip("HUD fly-in duration.")]
        [SerializeField] private float hudFlyIn = 0.5f;
        [Tooltip("Seconds the outro will wait for the presenter to finish its last beats before it " +
                 "covers. Capped well under the watchdog: an outro that waits too long is a bug, an " +
                 "outro that never fires strands the player.")]
        [SerializeField] private float outroDrain = 8f;

        [Header("Safety")]
        [Tooltip("Seconds after which a sequence releases the flow regardless of its own progress. " +
                 "Generous — this is a last resort, not a pacing control.")]
        [SerializeField] private float watchdogSeconds = 12f;

        // --- Runtime state ---------------------------------------------------------------

        private Transform _player;
        private Coroutine _running;
        private readonly Stack<GameMode> _overlayStack = new Stack<GameMode>();
        private CinemachineCamera _swingCamera;
        private float _timeScaleBeforeFreeze = 1f;
        private bool _worldFrozen;

        // Where the player was standing when the encounter fired, kept here so they can be put
        // back before the arena's floor is unloaded from under them. The game flow captures the
        // same pose and restores it again a moment later through PlayerLocomotion.Warp; that
        // remains the authoritative restore and this one only has to survive the unload.
        private Vector3 _homePosition;
        private Quaternion _homeRotation = Quaternion.identity;
        private bool _playerCarried;
        private string _biomeBeforeBattle;

        /// <summary>The presenter in the loaded battle scene, or null when no arena is up.</summary>
        private BattlePresenter Presenter =>
            BattleArena.Current != null ? BattleArena.Current.Presenter : null;

        /// <inheritdoc />
        public bool IsTransitioning { get; private set; }

        /// <summary>The mode this director believes it has staged. Diagnostic.</summary>
        public GameMode StagedMode { get; private set; } = GameMode.Boot;

        private void Awake()
        {
            if (brain == null && Camera.main != null) brain = Camera.main.GetComponent<CinemachineBrain>();
            if (overlay == null) overlay = GetComponent<ScreenTransitionOverlay>();
            if (overlay == null) overlay = gameObject.AddComponent<ScreenTransitionOverlay>();

            AttachOverlay();
        }

        private void OnEnable()
        {
            // GameFlowController resolves this through ServiceHub first and only falls back to
            // an inspector reference, so registering here is what removes the manual wiring.
            ServiceHub.Register<ITransitionDirector>(this);
        }

        private void LateUpdate() => AttachOverlay();

        /// <summary>
        /// Keeps the wipe parented to whichever camera the brain is currently outputting.
        /// The output camera changes when the brain does, and a wipe left on a stale camera
        /// is a wipe the player cannot see.
        /// </summary>
        private void AttachOverlay()
        {
            if (overlay == null) return;
            Camera output = brain != null ? brain.OutputCamera : Camera.main;
            if (output != null) overlay.AttachTo(output);
        }

        /// <summary>
        /// Binds the player transform, used as the pivot of the encounter swing when the
        /// request carries no position. This director never moves the player: the game flow
        /// owns capturing and restoring the return point, and two systems writing that
        /// transform is how a player ends up a metre off where they started.
        /// </summary>
        public void BindPlayer(Transform player) => _player = player;

        /// <summary>Assigns the overworld follow rig so the return trip can settle into it.</summary>
        public void BindOverworldCamera(CinemachineCamera followCamera) => overworldFollowCamera = followCamera;

        // --- ITransitionDirector -----------------------------------------------------------

        /// <inheritdoc />
        public void PlayEncounterIntro(EncounterRequest request, Action onComplete)
        {
            StartSequence("EncounterIntro", onComplete, once => EncounterIntroRoutine(request, once));
        }

        /// <inheritdoc />
        public void PlayBattleOutro(EncounterResult result, Action onComplete)
        {
            StartSequence("BattleOutro", onComplete, once => BattleOutroRoutine(result, once));
        }

        /// <inheritdoc />
        public void PlayReveal(Action onComplete)
        {
            StartSequence("Reveal", onComplete, RevealRoutine);
        }

        // --- Sequences -----------------------------------------------------------------------

        /// <summary>
        /// Exploration to encounter intro.
        ///
        /// The callback fires once the screen is opaque <i>and the battle level has arrived</i>,
        /// and nothing before it. The second half of that is new and is load-bearing: the flow
        /// calls <c>BeginEncounter</c> on the frame after this callback, and the stage only waits
        /// to be driven a turn at a time if something is already subscribed to it. The presenter
        /// subscribes in its own <c>OnEnable</c>, which runs during the scene load — so releasing
        /// the flow before the load finished would hand the whole battle to the stage's
        /// unattended auto-play and the player would watch an empty arena while it resolved.
        ///
        /// Everything after the callback — the hold, the reveal, the marks, the grade, the HUD —
        /// is this routine continuing to run while the flow stages the battle behind the cover.
        /// </summary>
        private IEnumerator EncounterIntroRoutine(EncounterRequest request, CallbackOnce stageReady)
        {
            try
            {
                // Where the encounter actually fired. Using the request rather than the live
                // player transform matters because the flow may already have frozen or nudged
                // the player by the time we run.
                Vector3 origin = request != null
                    ? request.WorldPosition
                    : (_player != null ? _player.position : transform.position);
                Quaternion facing = request != null
                    ? request.PlayerRotation
                    : (_player != null ? _player.rotation : Quaternion.identity);

                CinematicHooks.Audio(CinematicAudioCues.EncounterSting, origin);
                AnnounceTrainer(request);

                if (freezeWorld) yield return FreezeTime();

                // The swing: a real arc around the player's real position, so the ground under
                // them stays put and the world rotates around them rather than cutting.
                yield return SwingAroundPlayer(origin, facing);

                yield return overlay.CoverIn(coverDuration, WipeStyle.ShutterWipe);
                CinematicHooks.Audio(CinematicAudioCues.TransitionWhoosh, origin);

                // Opaque. The level may now be swapped unseen.
                yield return LoadBattleLevel();
                PrepareArena(request);

                // Released here, with the arena standing and the presenter subscribed.
                stageReady.Fire();

                yield return CinematicRunner.Wait(stagingHold);

                // The rig is retargeted while still covered, so the first visible frame is
                // correctly framed rather than damping toward correct over half a second.
                var presenter = Presenter;
                if (presenter != null)
                {
                    presenter.Rig.SetRigActive(true);
                    presenter.Rig.Show(BattleShot.Field);
                    presenter.Rig.Retarget();
                }
                if (_swingCamera != null)
                {
                    _swingCamera.Priority = 0;
                    _swingCamera.gameObject.SetActive(false);
                }

                // Cut to the battle rig rather than blending onto it. The arena stands a long
                // way from the map, and the authored None-to-Field blend is 0.95 s against a
                // 0.55 s wipe — so the reveal would open on a camera still flying in across the
                // world. A cut is free here because the screen is opaque, which is the one moment
                // the "never teleport the camera" rule is not about anything the player can see.
                if (brain != null) brain.ResetState();
                yield return null;

                yield return overlay.CoverOut(revealDuration, WipeStyle.SplitWipe);

                // The battle may perform now. Released here rather than before the wipe because
                // the send-out is the one beat the player only ever sees once per encounter, and
                // behind an opaque cover it is spent on nothing.
                if (presenter != null) presenter.ReleasePerformance();

                // Marks: trainers and creatures settle before anything else is asked of the frame.
                yield return CinematicRunner.Wait(marksHold);

                // Grade card. The UI worker renders it; the camera holds the establishing shot
                // underneath rather than moving, so the card stays legible.
                CinematicHooks.HudBeat("battle_grade", 1f);
                yield return CinematicRunner.Wait(gradeHold);

                // HUD last, once the field has been read.
                CinematicHooks.HudVisible(true, hudFlyIn);
                yield return CinematicRunner.Wait(hudFlyIn);

                if (freezeWorld) yield return ThawTime();
                StagedMode = GameMode.Battle;
            }
            finally
            {
                // Belt and braces. If anything above threw, or this coroutine was stopped by
                // the object being destroyed, the flow is still released rather than frozen.
                stageReady.Fire("sequence ended without reaching the cover");
                IsTransitioning = false;
            }
        }

        /// <summary>
        /// Battle back toward exploration. Covers and hands back; it does not reveal, because
        /// the flow repositions the player after this callback and
        /// <see cref="PlayReveal"/> is what shows the result.
        ///
        /// The unload happens <b>before</b> the callback rather than after it, and the player is
        /// walked home first. Two reasons, both of which are visible when they are got wrong: the
        /// callback is what starts the flow's reveal, so anything left running past it is racing
        /// a screen that is about to open; and the player is standing on the arena's floor, so
        /// unloading the level while they are still on it drops them through the world.
        /// </summary>
        private IEnumerator BattleOutroRoutine(EncounterResult result, CallbackOnce worldRestorable)
        {
            try
            {
                // The stage resolves on the turn that ends the battle, so the flow asks for the
                // outro while the knockout, the experience and the victory pose are still queued.
                // Covering on that frame throws away the end of every battle the player wins.
                var presenter = Presenter;
                if (presenter != null)
                    yield return presenter.WaitUntilIdle(Mathf.Max(0f, outroDrain));

                CinematicHooks.HudVisible(false, 0.35f);
                yield return CinematicRunner.Wait(0.35f);

                if (freezeWorld && !_worldFrozen) yield return FreezeTime();

                yield return overlay.CoverIn(coverDuration, WipeStyle.SplitWipe);
                CinematicHooks.Audio(CinematicAudioCues.TransitionWhoosh, transform.position);

                if (presenter != null)
                {
                    presenter.FlushQueue();
                    presenter.Rig.SetRigActive(false);
                }
                if (_swingCamera != null) _swingCamera.Priority = 0;

                CarryPlayerHome();
                yield return UnloadBattleLevel();

                // Opaque. The flow may now put the player back and reveal.
                worldRestorable.Fire();

                StagedMode = GameMode.Exploring;
            }
            finally
            {
                worldRestorable.Fire("sequence ended without reaching the cover");
                IsTransitioning = false;
            }
        }

        /// <summary>
        /// Uncovers, once the player has been repositioned. Settles the follow rig onto the
        /// restored pose before the first visible frame, so the reveal shows a camera already
        /// in place rather than one drifting toward it.
        /// </summary>
        private IEnumerator RevealRoutine(CallbackOnce done)
        {
            try
            {
                if (overworldFollowCamera != null)
                {
                    overworldFollowCamera.gameObject.SetActive(true);
                    overworldFollowCamera.Priority = 100;
                    if (_player != null)
                    {
                        // Tells Cinemachine the target moved discontinuously, so its damping
                        // does not smear a swoop across the whole restore distance.
                        overworldFollowCamera.OnTargetObjectWarped(_player, Vector3.zero);
                        overworldFollowCamera.PreviousStateIsValid = false;
                    }
                }

                // Same cut as on the way in, for the same reason in reverse: the shot being left
                // behind is a kilometre away, so a blend back would smear the whole return trip
                // across the reveal.
                if (brain != null) brain.ResetState();

                // One frame so the rig evaluates its new pose before it is revealed.
                yield return null;

                yield return overlay.CoverOut(revealDuration, WipeStyle.ShutterWipe);
                if (freezeWorld) yield return ThawTime();

                StagedMode = GameMode.Exploring;

                // Fired here as well as in the finally. Without it the only path that ever
                // released this sequence was the finally's abnormal one, so every successful
                // reveal logged "released abnormally" — a warning that is always present is a
                // warning nobody reads when it starts meaning something.
                done.Fire();
            }
            finally
            {
                // Uncover unconditionally. A transition that fails with the screen still
                // covered is indistinguishable from a crash.
                if (overlay != null) overlay.SetCoverage(0f, Color.black);
                RestoreTimeScale();
                done.Fire("sequence ended before the reveal completed");
                IsTransitioning = false;
            }
        }

        // --- The battle level ------------------------------------------------------------

        /// <summary>
        /// Brings <c>Battle.unity</c> in beside the level the player is standing in.
        ///
        /// Additive, and the alternative is worse in a way that is easy to miss: a single-mode
        /// load tears down the level scene, and with it <c>GameFlowController</c> — the coroutine
        /// that is at this moment holding the player frozen and waiting for a battle result. It
        /// would never be released and the encounter's whole outcome would be thrown away. So the
        /// two scenes coexist for the length of a battle, and the arena is authored far enough
        /// from the map that nothing in either can be seen from the other.
        ///
        /// A scene that is missing from the build settings is the failure this guards hardest:
        /// it fails silently in a player and the symptom is a battle in an empty room, a long way
        /// from the cause.
        /// </summary>
        private IEnumerator LoadBattleLevel()
        {
            if (BattleArena.Current != null) yield break;   // already up, e.g. an aborted outro

            if (string.IsNullOrEmpty(battleSceneName))
            {
                Debug.LogError("[TransitionDirector] No battle scene name, so there is no arena to " +
                               "load and the encounter will play in the overworld.", this);
                yield break;
            }

            if (!Application.CanStreamedLevelBeLoaded(battleSceneName))
            {
                Debug.LogError($"[TransitionDirector] Scene '{battleSceneName}' is not in the build " +
                               "settings, so the battle has nowhere to happen. Add it under " +
                               "File > Build Settings.", this);
                yield break;
            }

            var load = SceneManager.LoadSceneAsync(battleSceneName, LoadSceneMode.Additive);
            if (load == null) yield break;

            // Unscaled and bounded. The world is frozen at this point, so a WaitForSeconds would
            // never return, and a load that never finishes has to hand back rather than sit under
            // an opaque screen until the flow's own watchdog fires.
            float elapsed = 0f;
            while (!load.isDone && elapsed < Mathf.Max(1f, sceneLoadTimeout))
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!load.isDone)
                Debug.LogError($"[TransitionDirector] '{battleSceneName}' did not finish loading in " +
                               $"{sceneLoadTimeout:0.0}s; showing the encounter anyway.", this);
        }

        /// <summary>Takes the battle level away again. Safe when none is loaded.</summary>
        private IEnumerator UnloadBattleLevel()
        {
            var scene = SceneManager.GetSceneByName(battleSceneName);
            if (!scene.isLoaded) yield break;

            var unload = SceneManager.UnloadSceneAsync(scene);
            float elapsed = 0f;
            while (unload != null && !unload.isDone && elapsed < Mathf.Max(1f, sceneLoadTimeout))
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            // The grade follows the place. Put back what the player was standing in, or a fight
            // that happened in a cave leaves the overworld lit like one.
            if (!string.IsNullOrEmpty(_biomeBeforeBattle)) GameEvents.RaiseBiomeEntered(_biomeBeforeBattle);
        }

        /// <summary>
        /// Dresses the arena for the zone the encounter fired in and moves the player into it.
        ///
        /// The dressing is the whole of "a cave fight happens in a cave" now that the fight is
        /// not staged in the cave itself: four authored backdrops, chosen by the request's biome
        /// id, with the field discs made of that zone's ground.
        /// </summary>
        private void PrepareArena(EncounterRequest request)
        {
            // The reveal leaves the follow rig on 100 on the way out, which outranks every
            // battle camera in the rig. Left alone, the first encounter of a session is framed
            // correctly and every one after it is framed by the exploration camera, looking at a
            // player who is no longer standing where it is pointing.
            if (overworldFollowCamera != null) overworldFollowCamera.Priority = 0;

            var arena = BattleArena.Current;
            if (arena == null)
            {
                Debug.LogError("[TransitionDirector] The battle scene loaded without a BattleArena " +
                               "in it, so there is nothing to stage. Run Create Battle Arena In " +
                               "Open Scene with Battle.unity open.", this);
                return;
            }

            BattleZone zone = BattleArena.ZoneFor(request != null ? request.BiomeId : null);
            arena.Dress(zone);
            arena.Stage.SetTrainers(PlayerPersonKey, OpponentPersonKey(request));

            _biomeBeforeBattle = request != null && !string.IsNullOrEmpty(request.BiomeId)
                ? request.BiomeId
                : "route_01";
            GameEvents.RaiseBiomeEntered(BattleArena.GradeIdFor(zone));

            CarryPlayerTo(arena.PlayerStand, request);

            var presenter = arena.Presenter;
            if (presenter == null) return;
            presenter.ResetForNewBattle();

            // Ordered after the reset, which clears any hold left by a previous encounter.
            presenter.HoldPerformance(coverDuration + stagingHold + revealDuration + 4f);
        }

        // --- Carrying the player ------------------------------------------------------------

        /// <summary>
        /// Stands the player on the arena's trainer mark, remembering the step they were on.
        ///
        /// This is what makes the battle somewhere the player <i>is</i> rather than a camera
        /// pointed at a diorama: they are visible behind their own creature, and the balls are
        /// thrown from where they are standing rather than from a bare transform.
        /// </summary>
        private void CarryPlayerTo(Transform stand, EncounterRequest request)
        {
            _playerCarried = false;
            if (stand == null) return;

            Transform player = ResolvePlayer();
            if (player == null) return;

            // The request's pose, not the live transform's: the flow froze the player and may
            // already have nudged them, and this pose is the one the return has to match.
            _homePosition = request != null ? request.WorldPosition : player.position;
            _homeRotation = request != null ? request.PlayerRotation : player.rotation;

            Vector3 facing = stand.forward;
            facing.y = 0f;
            Quaternion look = facing.sqrMagnitude < 1e-5f
                ? stand.rotation
                : Quaternion.LookRotation(facing.normalized, Vector3.up);

            _playerCarried = Warp(player, stand.position, look);
        }

        /// <summary>Puts the player back on the step they were standing on, before the arena goes.</summary>
        private void CarryPlayerHome()
        {
            if (!_playerCarried) return;
            _playerCarried = false;

            Transform player = ResolvePlayer();
            if (player != null) Warp(player, _homePosition, _homeRotation);
        }

        /// <summary>
        /// Repositions the player through <c>PlayerLocomotion.Warp</c>, by name.
        ///
        /// Reflection, and deliberately: the player lives in <c>PokeLab.Overworld</c>, which this
        /// assembly cannot reference and must not start to. Rewriting the transform directly is
        /// not an option — the <see cref="CharacterController"/> caches its own position and
        /// reasserts the old one on the next Move — and reimplementing the disable-write-enable
        /// dance here would still miss the rest of what Warp resets: the previous-position sample
        /// the distance accumulator differences, which feeds the step counter wild encounters are
        /// rolled against. Two kilometres of it would fire an encounter the moment the player
        /// took their next step home.
        ///
        /// Returns false when there is nothing to call, in which case the battle simply happens
        /// with the player still standing in the overworld — worse-looking, never broken.
        /// </summary>
        private static bool Warp(Transform player, Vector3 position, Quaternion rotation)
        {
            foreach (var component in player.GetComponents<MonoBehaviour>())
            {
                if (component == null) continue;
                MethodInfo warp = component.GetType().GetMethod("Warp",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) }, null);
                if (warp == null) continue;

                warp.Invoke(component, new object[] { position, rotation, false });
                return true;
            }

            Debug.LogWarning("[TransitionDirector] The player has no Warp(Vector3, Quaternion, bool), " +
                             "so they were left in the overworld for the duration of the battle.");
            return false;
        }

        /// <summary>
        /// The player's transform.
        ///
        /// Found by tag rather than held, because <see cref="BindPlayer"/> is on the interface
        /// and nothing has ever called it — a cached null would mean the player is silently never
        /// carried, which looks exactly like the arena being empty.
        /// </summary>
        private Transform ResolvePlayer()
        {
            if (_player != null) return _player;

            var tagged = GameObject.FindGameObjectWithTag("Player");
            if (tagged != null) _player = tagged.transform;
            return _player;
        }

        /// <summary>
        /// Surfaces the opposing trainer's name and opening line for the intro, when the
        /// encounter has one. Read through <see cref="ITrainerRegistry"/> and skipped entirely
        /// when no registry is present, so a wild encounter and a partially integrated build
        /// both behave.
        /// </summary>
        private void AnnounceTrainer(EncounterRequest request)
        {
            if (request == null || request.Kind != BattleKind.Trainer || string.IsNullOrEmpty(request.TrainerId)) return;
            if (!ServiceHub.TryGet<ITrainerRegistry>(out var registry) || registry == null) return;
            if (!registry.TryGetProfile(request.TrainerId, out var profile) || profile == null) return;

            CinematicHooks.HudBeat("trainer_intro:" + profile.DisplayName, 1f);
            if (!string.IsNullOrEmpty(profile.IntroLine)) CinematicHooks.HudBeat("trainer_line:" + profile.IntroLine, 1f);
            if (!string.IsNullOrEmpty(profile.ArtKey)) CinematicHooks.Vfx("trainer_" + profile.ArtKey, transform.position, Quaternion.identity);
        }

        // --- Encounter swing --------------------------------------------------------------

        /// <summary>
        /// Arcs a camera around the player and dollies in.
        ///
        /// Driven as a transform rather than as a Cinemachine blend on purpose: an arc is not
        /// a blend between two poses, and asking the brain to interpolate between a start and
        /// an end pose produces a straight line through the middle of the player's head. It is
        /// still a real CinemachineCamera, so the brain owns the hand-off at both ends.
        /// </summary>
        private IEnumerator SwingAroundPlayer(Vector3 playerPosition, Quaternion playerFacing)
        {
            EnsureSwingCamera();

            Vector3 pivot = playerPosition + Vector3.up * 1.1f;

            // Start behind the player, where a follow camera would already be.
            Vector3 startDirection = -(playerFacing * Vector3.forward);
            startDirection.y = 0f;
            if (startDirection.sqrMagnitude < 1e-4f) startDirection = -Vector3.forward;
            startDirection.Normalize();

            Transform camT = _swingCamera.transform;
            // Seed from the live camera so the brain has no distance to cover at the hand-off.
            Camera output = brain != null ? brain.OutputCamera : Camera.main;
            if (output != null) camT.SetPositionAndRotation(output.transform.position, output.transform.rotation);

            _swingCamera.gameObject.SetActive(true);
            _swingCamera.Priority = 200;

            float startRadius = swingRadius;
            float endRadius = Mathf.Max(1.2f, swingRadius - swingDolly);

            yield return CinematicRunner.Tween(swingDuration, CinematicEase.InOutQuint, p =>
            {
                float angle = swingArc * p;
                Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * startDirection;
                float radius = Mathf.Lerp(startRadius, endRadius, CinematicEase.OutCubic(p));
                // Rise through the move, so the arc reads as a lift as well as a turn.
                float height = Mathf.Lerp(swingHeight, swingHeight + 0.45f, Mathf.Sin(p * Mathf.PI));

                camT.position = pivot + direction * radius + Vector3.up * (height - 1.1f);
                camT.rotation = Quaternion.LookRotation((pivot - camT.position).normalized, Vector3.up);
            });
        }

        private void EnsureSwingCamera()
        {
            if (_swingCamera != null) return;

            var go = new GameObject("CM_TransitionSwing");
            go.transform.SetParent(transform, false);
            _swingCamera = go.AddComponent<CinemachineCamera>();
            _swingCamera.Lens = LensSettings.Default;
            _swingCamera.Lens.FieldOfView = 52f;
            // No Body or Aim components: the transform is driven directly by the swing
            // routine, and a procedural component would fight it every frame.
            go.SetActive(false);
        }

        // --- Overlay modes (menu, dialogue, cutscene) -----------------------------------------

        /// <summary>
        /// Pushes an overlay mode with its own framing adjustment. Not part of
        /// <see cref="ITransitionDirector"/>, which only covers the encounter round trip;
        /// the UI layer drives these directly.
        /// </summary>
        public IEnumerator PushMode(GameMode mode)
        {
            IsTransitioning = true;
            _overlayStack.Push(StagedMode);

            switch (mode)
            {
                case GameMode.Menu:
                    // A menu does not need to hide the world, only to quiet it: a partial dim
                    // keeps the player oriented in the space they paused in.
                    yield return CinematicRunner.Progress(0.28f, p =>
                        overlay.SetCoverage(CinematicEase.OutQuad(p) * 0.55f, new Color(0.02f, 0.03f, 0.06f, 1f)));
                    break;

                case GameMode.Dialogue:
                    CinematicHooks.HudBeat("dialogue_open", 1f);
                    yield return CinematicRunner.Wait(0.2f);
                    break;

                case GameMode.Cutscene:
                    CinematicHooks.HudVisible(false, 0.3f);
                    yield return overlay.CoverIn(0.3f, WipeStyle.Fade);
                    yield return CinematicRunner.Wait(0.15f);
                    yield return overlay.CoverOut(0.45f, WipeStyle.Fade);
                    break;
            }

            StagedMode = mode;
            IsTransitioning = false;
        }

        /// <summary>Pops the topmost overlay mode and restores the framing underneath it.</summary>
        public IEnumerator PopMode()
        {
            IsTransitioning = true;
            GameMode previous = _overlayStack.Count > 0 ? _overlayStack.Pop() : GameMode.Exploring;

            float startCoverage = overlay.Coverage;
            if (startCoverage > 0.001f)
            {
                yield return CinematicRunner.Progress(0.32f, p =>
                    overlay.SetCoverage(startCoverage * (1f - CinematicEase.InOutCubic(p)),
                        new Color(0.02f, 0.03f, 0.06f, 1f)));
            }

            if (previous == GameMode.Battle) CinematicHooks.HudVisible(true, 0.35f);

            StagedMode = previous;
            IsTransitioning = false;
        }

        // --- Shared plumbing --------------------------------------------------------------------

        /// <summary>
        /// Starts a sequence with a guaranteed single callback and a watchdog.
        ///
        /// A running sequence is stopped first rather than the incoming one being refused.
        /// The encounter intro deliberately keeps running past its own callback — it holds the
        /// cover, reveals, and flies the HUD in while the battle plays — so a short battle can
        /// legitimately request the outro while that tail is still going. Stopping it disposes
        /// its iterator, which runs its <c>finally</c> and releases its callback, so nothing is
        /// left dangling. Refusing the new request instead would drop a transition, and
        /// dropping the outro strands the player in the battle scene.
        /// </summary>
        private void StartSequence(string label, Action onComplete, Func<CallbackOnce, IEnumerator> body)
        {
            var once = new CallbackOnce(onComplete, label);

            if (_running != null)
            {
                var previous = _running;
                _running = null;
                CinematicRunner.Halt(previous);
            }

            // Set after the halt: the stopped sequence's finally clears this flag, and doing it
            // in the other order would leave the incoming sequence marked as not running.
            IsTransitioning = true;
            AttachOverlay();

            _running = CinematicRunner.Run(body(once));
            CinematicRunner.Fork(Watchdog(once, label));
        }

        /// <summary>
        /// Releases the caller if a sequence overruns. <see cref="CallbackOnce"/> makes this
        /// harmless in the normal case, so the watchdog can be unconditional.
        /// </summary>
        private IEnumerator Watchdog(CallbackOnce once, string label)
        {
            float elapsed = 0f;
            while (!once.HasFired && elapsed < watchdogSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            if (once.HasFired) yield break;

            Debug.LogError($"[TransitionDirector] {label} exceeded {watchdogSeconds:0.0}s without reaching " +
                           "its callback. Releasing the flow and forcing a safe visual state.", this);
            AbortToMode(StagedMode);
            once.Fire("watchdog");
        }

        private IEnumerator FreezeTime()
        {
            if (_worldFrozen) yield break;
            _timeScaleBeforeFreeze = Time.timeScale > 0.001f ? Time.timeScale : 1f;
            _worldFrozen = true;

            float from = _timeScaleBeforeFreeze;
            yield return CinematicRunner.Tween(freezeRamp, CinematicEase.OutQuad,
                p => Time.timeScale = Mathf.Lerp(from, 0f, p));
            Time.timeScale = 0f;
        }

        private IEnumerator ThawTime()
        {
            if (!_worldFrozen) yield break;
            float to = _timeScaleBeforeFreeze <= 0.001f ? 1f : _timeScaleBeforeFreeze;
            yield return CinematicRunner.Tween(thawRamp, CinematicEase.InOutCubic,
                p => Time.timeScale = Mathf.Lerp(0f, to, p));
            Time.timeScale = to;
            _worldFrozen = false;
        }

        private void RestoreTimeScale()
        {
            if (!_worldFrozen) return;
            Time.timeScale = _timeScaleBeforeFreeze <= 0.001f ? 1f : _timeScaleBeforeFreeze;
            _worldFrozen = false;
        }

        /// <summary>
        /// Emergency exit. Forces the visual state to match <paramref name="mode"/> at once.
        /// This is the one path allowed to look bad; it exists so a failed transition leaves a
        /// playable game rather than a black screen.
        /// </summary>
        public void AbortToMode(GameMode mode)
        {
            if (_running != null) { CinematicRunner.Halt(_running); _running = null; }
            IsTransitioning = false;

            RestoreTimeScale();
            Time.timeScale = _timeScaleBeforeFreeze <= 0.001f ? 1f : _timeScaleBeforeFreeze;
            if (overlay != null) overlay.SetCoverage(0f, Color.black);
            if (_swingCamera != null) _swingCamera.Priority = 0;

            bool battle = mode == GameMode.Battle || mode == GameMode.EncounterIntro || mode == GameMode.BattleOutro;
            if (overworldFollowCamera != null) overworldFollowCamera.Priority = battle ? 0 : 100;

            // Aborting out of a battle has to take the level with it, and has to bring the player
            // back first — an arena left loaded is a second world standing beside this one with
            // the player still on its floor, and the next encounter would load a scene that is
            // already there and find two arenas.
            if (!battle && SceneManager.GetSceneByName(battleSceneName).isLoaded)
            {
                CarryPlayerHome();
                CinematicRunner.Fork(UnloadBattleLevel());
            }

            _overlayStack.Clear();
            StagedMode = mode;
        }

        private void OnDisable()
        {
            // Never leave the game frozen or covered because this object was disabled
            // mid-transition. The sequences' finally blocks fire their callbacks; this
            // restores the visual state they were in the middle of changing.
            if (IsTransitioning) AbortToMode(StagedMode);
        }
    }

    /// <summary>
    /// A callback that can be invoked from several places but runs at most once.
    ///
    /// Every transition callback goes through one of these. The flow's watchdog, the
    /// sequence's own <c>finally</c> and the happy path all call <see cref="Fire"/>, and the
    /// contract — exactly once, including on failure — holds without any of them having to
    /// know about the others.
    /// </summary>
    public sealed class CallbackOnce
    {
        private Action _action;
        private readonly string _label;

        public CallbackOnce(Action action, string label)
        {
            _action = action;
            _label = label;
        }

        /// <summary>True once the callback has been invoked, or once it has been found to be null.</summary>
        public bool HasFired { get; private set; }

        /// <summary>
        /// Invokes the callback if it has not already run.
        /// <paramref name="abnormalReason"/> is logged when supplied and the callback had not
        /// yet fired — that combination is always a bug worth seeing.
        /// </summary>
        public void Fire(string abnormalReason = null)
        {
            if (HasFired) return;
            HasFired = true;

            if (!string.IsNullOrEmpty(abnormalReason))
                Debug.LogWarning($"[TransitionDirector] {_label} released abnormally: {abnormalReason}.");

            Action action = _action;
            _action = null;
            if (action == null) return;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                // The caller's continuation threw. Log it rather than letting it unwind through
                // a coroutine's finally, where it would be swallowed silently.
                Debug.LogException(ex);
            }
        }
    }
}
