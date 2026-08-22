using System.Collections;
using System.Collections.Generic;
using PokeLab.Audio;
using PokeLab.Cinematics;
using PokeLab.Core;
using PokeLab.Overworld;
using PokeLab.Vfx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Boot
{
    /// <summary>
    /// The runtime composition root for everything audible and most things visible.
    ///
    /// The audit's finding was blunt: the audio layer, the VFX layer, the ambience, the
    /// footsteps, the UI sounds and the cinematic hooks were all fully written and none of
    /// them existed at runtime — they were components no scene contained and listeners no
    /// event stream fed. Fixing that in the scenes would mean editing every scene and every
    /// future scene; fixing it here means one persistent object that composes the AV layer
    /// from code, which is also the only fix a worker without scene access can ship.
    ///
    /// Three jobs, in order:
    ///
    ///   1. <b>Existence.</b> Ensure exactly one of each AV system is alive. Created copies
    ///      live on children of this object; a scene that later provides its own copy wins
    ///      (its serialized mixer, catalogue and profiles are the authored versions), and
    ///      the host's stand-in is destroyed the moment a rival appears.
    ///   2. <b>Wiring.</b> Find the scene's <see cref="BattlePresenter"/> and subscribe the
    ///      audio and VFX presenters to its <c>EventPerformed</c> tap — the paced,
    ///      beat-open one, never <c>EventObserved</c>, because a sound scored on arrival
    ///      plays a whole turn as one chord seconds before the pictures.
    ///   3. <b>Binding.</b> Tell the VFX layer which <see cref="CreatureView"/> stands on
    ///      which side as send-outs are performed, so parented effects (status auras, hit
    ///      flashes) land on the actual creature rather than a fallback anchor.
    ///
    /// This assembly is the one place allowed to reference every other, which is why the
    /// bridge from the overworld's footstep event to the audio layer's footstep player —
    /// two systems forbidden from referencing each other — also lives here.
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public sealed class AvPresenterHost : MonoBehaviour
    {
        private static AvPresenterHost s_instance;

        /// <summary>
        /// Self-bootstraps after the first scene loads, so no scene needs to contain this
        /// component for the AV layer to exist. AfterSceneLoad rather than BeforeSceneLoad
        /// on purpose: scene-provided directors must have had their Awake first, so the
        /// ensure pass below sees them and never creates a competing stand-in.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (s_instance != null) return;
            var go = new GameObject("PL_AvPresenterHost");
            DontDestroyOnLoad(go);
            go.AddComponent<AvPresenterHost>();
        }

        // ---- composed systems --------------------------------------------------------

        private BattleAudioPresenter _battleAudio;
        private BattleVfxPresenter _battleVfx;
        private OverworldAudio _overworldAudio;

        /// <summary>Children this host created, so a scene-provided rival can evict them.</summary>
        private readonly List<Component> _ownedComponents = new List<Component>();

        // ---- wiring state ------------------------------------------------------------

        private BattlePresenter _presenter;
        private bool _presenterBound;
        private System.Action<BattleEvent> _vfxRelay;
        private System.Action<BattleEvent> _audioRelay;
        private PlayerLocomotion _player;
        private bool _playerBound;
        private float _nextScanAt;

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }
            s_instance = this;

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            // Started at boot, read much later: a ProfilerRecorder reports the frame AFTER it
            // is created, and reading one immediately is what made the first counter run come
            // back as twelve zeroes.
            //
            // Behind the flag because it is not free: it enumerates every profiler handle the
            // player has -- 1,974 on the web, 2,476 on a desktop -- and then holds 145 live
            // recorders for the session. Worth it while hunting; not worth it in a build a
            // player runs.
            if (PokeLab.Core.Diag.AutoCounters) MemoryCounters.Start();

            EnsureSystems();
            RebindSceneObjects();

            // The scene that was already open when this host bootstrapped never raises
            // sceneLoaded, so entering play straight into Town would otherwise leave the pool
            // cold for the whole session.
            ApplyVfxResidency(SceneManager.GetActiveScene().name);
            if (MemoryRelief.Trace) _markFrames = 12;
            if (PokeLab.Core.Diag.AutoCounters) _countersAt = Time.unscaledTime + 4f;
        }

        private void OnDestroy()
        {
            if (s_instance != this) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            UnbindPresenter();
            UnbindPlayer();
            s_instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureSystems();
            RebindSceneObjects();
            ApplyVfxResidency(scene.name);
            if (MemoryRelief.Trace) _markFrames = 12;
            if (PokeLab.Core.Diag.AutoCounters) _countersAt = Time.unscaledTime + 4f;

            // A single-mode load has just replaced everything: the previous scene's canvases,
            // creatures and their textures are all unreferenced now and none of them will be
            // freed unless somebody says so. This host already owns the scene callbacks, so
            // it is the one place the reclaim can live without every screen remembering.
            //
            // Additive loads are skipped deliberately -- the battle scene arrives that way,
            // over a world that is still needed, and the pass would cost its hitch for
            // nothing.
            // NOT reclaiming on a scene load any more, and the measurement is why.
            //
            // Driving the deployed build through login and into the menu, the wasm heap went
            // 1024 -> 1475 MB across that one transition while Unity reported "Unloading 0
            // unused Assets" three times and the managed heap sat at 13.8 MB. The pass freed
            // nothing and the heap grew by 450 MB. A WebAssembly heap never shrinks -- every
            // byte it takes is permanent for the session -- so a sweep that walks seven
            // thousand objects to build a live-object map is not free here even when it
            // reclaims nothing: whatever it allocated to do the walk raises the floor forever.
            //
            // The reclaim is kept where it can actually pay for itself: immediately before a
            // battle, where CreatureThumbnail's cache really is holding atlases open. See
            // BattleModeLauncher.
            if (mode == LoadSceneMode.Single)
            {
                MemoryRelief.Report("loaded " + scene.name);
                if (PokeLab.Core.Diag.AutoCounters) MemoryCounters.Dump("loaded " + scene.name);
                if (MemoryRelief.Trace) MemoryCensus.Dump("loaded " + scene.name);
            }
            else
                // Additive loads are the ones that kill the web build -- the arena goes on top
                // of whatever was already there -- so they are reported even though nothing is
                // reclaimed. Every path that can reach a battle passes through here, which the
                // instrumentation in BattleModeLauncher does not: the story's battles come in
                // through TransitionDirector instead.
                MemoryRelief.Report("additive load of " + scene.name);
        }

        /// <summary>
        /// Scenes that can never play a particle effect, and so have no business paying for a
        /// warm effect pool.
        ///
        /// Named rather than inferred: "does this scene contain a battle" is not something the
        /// host can ask, and getting it wrong in the permissive direction only costs a prewarm.
        /// Getting it wrong the other way would leave a battle cold.
        /// </summary>
        private static bool IsFrontendScene(string sceneName) =>
            sceneName == "Login" || sceneName == "MainMenu" || sceneName == "Boot";

        /// <summary>
        /// Keeps the effect pool resident only where effects can happen.
        ///
        /// <b>This is the OOM fix.</b> BattleVfxPresenter used to warm its whole catalogue in
        /// Start, and this host creates that presenter once at boot and never destroys it — so
        /// the pool was built during the login screen and held for the session. The census of
        /// the running web player counted 636 ParticleSystems, 636 renderers, 167 lights and
        /// 1,103 GameObjects alive behind the main menu, against 167 MB of actual loaded assets
        /// and 1,343 MB the engine said it had allocated. Two A/Bs in the live build ruled out
        /// the alternatives: a full collect plus asset unload freed 0.0 MB, and unloading the
        /// decoded audio of all 153 clips freed 0.0 MB.
        ///
        /// The cost is not the objects, it is what a ParticleSystem reserves — every emitter
        /// takes a particle buffer sized for its maximum count (up to 900 here) plus the
        /// renderer's vertex buffers, none of which appears in an asset census. And on the web
        /// the damage outlives the menu: a WebAssembly heap only ever grows, so 800 MB taken at
        /// the main menu is 800 MB still taken when the arena asks for its own.
        /// </summary>
        private void ApplyVfxResidency(string sceneName)
        {
            if (_battleVfx == null) return;

            if (IsFrontendScene(sceneName)) _battleVfx.ReleasePool();
            else _battleVfx.WarmPool();
        }

        private void OnSceneUnloaded(Scene scene)
        {
            // The battle scene unloading takes its presenter with it; rebinding now rather
            // than waiting for the next load is what clears the VFX view bindings before
            // anything can play an effect on a destroyed creature.
            RebindSceneObjects();

            // And its art goes with it. A battle is where the most texture is touched at once
            // -- two creatures' atlases plus the arena -- and the moment it unloads is the
            // moment the player is watching a transition, so the pass is free.
            //
            // The thumbnail cache is KEPT here: the menu or HUD underneath may still be
            // drawing from it, and blanking a picture the player can see is worse than
            // holding a few megabytes.
            MemoryRelief.Report("unloaded " + scene.name);
        }

        /// <summary>
        /// Takes a memory census on demand, from outside the player.
        ///
        /// The web build can only be driven from the browser, and by the time an OOM aborts
        /// there is nothing left to inspect -- so the census has to be taken at a chosen moment
        /// DURING the run, at whichever step of the flow is under suspicion. This is the only
        /// entry point that can be reached from there:
        ///
        ///     unityInstance.SendMessage('PL_AvPresenterHost', 'CensusNow', 'after the gacha')
        ///
        /// It lives on this host because this host is the one GameObject that survives every
        /// scene change (DontDestroyOnLoad, created before the first scene), so the name is
        /// valid at every step of the flow. SendMessage silently does nothing when the target
        /// name is wrong, which would look exactly like a census that found nothing.
        ///
        /// Tools/probe_web_memory.py drives it.
        /// </summary>
        public void CensusNow(string where)
        {
            MemoryRelief.Report(where);
            MemoryCounters.Dump(where);
            MemoryCensus.Dump(where);
            MemoryCensus.AudioBreakdown(where);
        }

        /// <summary>
        /// Runs the drop-audio experiment. Reached the same way as <see cref="CensusNow"/>:
        ///
        ///     unityInstance.SendMessage('PL_AvPresenterHost', 'DropAudioNow', 'main menu')
        ///
        /// Deliberate only — it silences the game for the rest of the session.
        /// </summary>
        public void DropAudioNow(string where)
        {
            MemoryCensus.DropAudio(where);
        }

        /// <summary>
        /// Collects and unloads on demand, and reports both sides.
        ///
        /// The second half of the same question the audio drop asks. Unity says it has a
        /// gigabyte allocated that the object table cannot account for; either that gigabyte is
        /// live, or it is garbage nobody has swept. A full collect plus an asset unload
        /// separates those two, and they want completely different fixes.
        ///
        ///     unityInstance.SendMessage('PL_AvPresenterHost', 'ReclaimNow', 'main menu')
        /// </summary>
        public void ReclaimNow(string where)
        {
            MemoryRelief.Report(where + " before reclaim");
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            Resources.UnloadUnusedAssets();
            MemoryRelief.Report(where + " after reclaim");
        }

        /// <summary>
        /// Seconds between memory samples while <see cref="MemoryRelief.Trace"/> is on.
        /// </summary>
        private const float MemorySampleSeconds = 2f;

        private float _nextMemorySample;

        /// <summary>
        /// Frames still to be marked one at a time after a scene load.
        ///
        /// The 800 MB lands between the last Start on the new scene and the first Update, and a
        /// quarter-second trace cannot see inside a gap that holds one frame. Marking Update and
        /// LateUpdate separately for a dozen frames splits that gap into its parts: anything
        /// that appears between one frame's LateUpdate and the next frame's Update happened
        /// during rendering, which is the only thing that runs in between.
        /// </summary>
        private int _markFrames;

        /// <summary>When to take the settled counter sample, or 0 for never.</summary>
        private float _countersAt;

        private void LateUpdate()
        {
            if (_markFrames > 0) MemoryRelief.Mark($"frame {Time.frameCount} late");
        }

        private void Update()
        {
            if (_markFrames > 0)
            {
                _markFrames--;
                MemoryRelief.Mark($"frame {Time.frameCount} update");

                // pl_nocamera: the 802 MB arrives between one frame's LateUpdate and the next
                // frame's Update, and rendering is the only thing that runs in that gap. This
                // takes the camera out before the first frame is drawn, which is the prevention
                // test for "rendering is what spends it".
                if (PokeLab.Core.Diag.NoCamera && Camera.main != null)
                {
                    Camera.main.enabled = false;
                    Debug.Log("[Diag] camera disabled: " + Camera.main.name);
                }
            }

            // A time series, because a single reading says nothing. The arena's cost is the
            // DIFFERENCE across the load, and in the editor the absolute figure is mostly the
            // editor itself. Off unless somebody asks for it; two Profiler calls every two
            // seconds is cheap but not free.
            if (MemoryRelief.Trace && Time.unscaledTime >= _nextMemorySample)
            {
                _nextMemorySample = Time.unscaledTime + MemorySampleSeconds;
                MemoryRelief.Report($"t={Time.unscaledTime:F0}s");
            }

            // A scene's cost is not visible on the frame it loads: the canvas has not drawn yet,
            // and drawing is where the 797 MB lands. Sampling a few seconds in is what makes the
            // desktop table comparable with the web one, which the probe takes at the same point.
            if (PokeLab.Core.Diag.AutoCounters && _countersAt > 0f && Time.unscaledTime >= _countersAt)
            {
                _countersAt = 0f;
                MemoryCounters.Dump("settled in " + SceneManager.GetActiveScene().name);
            }

            // Cheap self-healing for the two scene-owned dependencies. A destroyed
            // presenter or player is detected by Unity's fake-null while the managed
            // reference is still held, which is exactly the state that needs an unbind.
            if (_presenterBound && _presenter == null) UnbindPresenter();
            if (_playerBound && _player == null) UnbindPlayer();

            if ((_presenter == null || _player == null) && Time.unscaledTime >= _nextScanAt)
            {
                _nextScanAt = Time.unscaledTime + 1f;
                RebindSceneObjects();
            }
        }

        // ---- existence ---------------------------------------------------------------

        /// <summary>
        /// One of everything. Order matters only at the top: the audio director must exist
        /// before the systems that resolve it in their own Awake.
        /// </summary>
        private void EnsureSystems()
        {
            // Scene-authored where available (Field/Town/interiors carry an AudioDirector
            // with the mixer and catalogue serialized in; a host-created stand-in has
            // neither and degrades to warnings). The stand-in still matters: it keeps the
            // ServiceHub seams alive in scenes with no authored copy, so nothing upstream
            // has to care which kind it got.
            Ensure<AudioDirector>("AudioDirector");
            Ensure<MusicDirector>("MusicDirector");
            Ensure<AmbienceDirector>("AmbienceDirector");
            _overworldAudio = Ensure<OverworldAudio>("OverworldAudio");
            Ensure<UiAudio>("UiAudio");
            Ensure<ScannerAudio>("ScannerAudio");
            _battleAudio = Ensure<BattleAudioPresenter>("BattleAudioPresenter");
            _battleVfx = Ensure<BattleVfxPresenter>("BattleVfxPresenter");

            // Runs happily without scene wiring: it follows Camera.main, listens on
            // GameEvents for weather/time/biome, and no-ops until VfxApi has a presenter —
            // which the line above guarantees.
            Ensure<AmbientVfxController>("AmbientVfxController");

            // The two cinematic hook backends. Registered on ServiceHub from their own
            // Awake; CinematicHooks re-probes until it finds them.
            Ensure<CinematicAudioHookHost>("CinematicAudioHook");
            Ensure<CinematicVfxHookHost>("CinematicVfxHook");
        }

        /// <summary>
        /// Create-if-absent with an eviction rule: a copy this host created exists only
        /// until a scene provides an authored one. Both alive at once is the failure mode
        /// this guards against — two music directors is two songs.
        /// </summary>
        private T Ensure<T>(string childName) where T : Component
        {
            var all = FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            T sceneCopy = null;
            T ownedCopy = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].transform.IsChildOf(transform)) ownedCopy = all[i];
                else if (sceneCopy == null) sceneCopy = all[i];
            }

            if (sceneCopy != null)
            {
                if (ownedCopy != null)
                {
                    // The authored copy has already re-registered itself over the stand-in
                    // (its Awake ran during the scene load, before this callback), so the
                    // stand-in can go without anything ever resolving a dead reference.
                    _ownedComponents.Remove(ownedCopy);
                    Destroy(ownedCopy.gameObject);
                }
                return sceneCopy;
            }

            if (ownedCopy != null) return ownedCopy;

            var child = new GameObject(childName);
            child.transform.SetParent(transform, false);
            var created = child.AddComponent<T>();
            _ownedComponents.Add(created);
            return created;
        }

        // ---- wiring ------------------------------------------------------------------

        private void RebindSceneObjects()
        {
            // The battle presenter arrives with the additively loaded battle scene and may
            // be inactive until the transition enables the arena — subscription is a plain
            // C# event and does not care.
            var presenter = FindAnyObjectByType<BattlePresenter>(FindObjectsInactive.Include);
            if (!ReferenceEquals(presenter, _presenter))
            {
                UnbindPresenter();
                if (presenter != null) BindPresenter(presenter);
            }

            var player = FindAnyObjectByType<PlayerLocomotion>(FindObjectsInactive.Include);
            if (!ReferenceEquals(player, _player))
            {
                UnbindPlayer();
                if (player != null) BindPlayer(player);
            }
        }

        private void BindPresenter(BattlePresenter presenter)
        {
            _presenter = presenter;
            _presenterBound = true;

            // The binder subscribes first: view bindings must be in place before the VFX
            // presenter reacts to the same events, and multicast delegates invoke in
            // subscription order.
            presenter.EventPerformed += OnEventPerformedForBinding;

            // Relayed through guards rather than subscribed raw: EventPerformed is invoked
            // from inside the performance pump, so an exception escaping a listener would
            // kill the coroutine that is holding the whole battle together. Presentation is
            // advisory by contract — a broken effect must cost an effect, never the battle.
            if (_battleVfx != null)
            {
                var vfx = _battleVfx;
                _vfxRelay = evt =>
                {
                    try { vfx.OnBattleEvent(evt); }
                    catch (System.Exception e) { Debug.LogError($"[AvPresenterHost] VFX listener failed: {e}", this); }
                };
                presenter.EventPerformed += _vfxRelay;
                // A choreographed battle exists now, so the VFX presenter yields the beats
                // that arrive frame-accurately through the cinematic hook. See the property
                // for the full split.
                _battleVfx.ChoreographyOwnsBeats = true;
            }

            // EventPerformed, never EventObserved: the observed tap hands a whole turn over
            // in one frame for the HUD's benefit, and forty cues in one frame is a chord.
            if (_battleAudio != null)
            {
                var audio = _battleAudio;
                _audioRelay = evt =>
                {
                    try { audio.OnBattleEvent(evt); }
                    catch (System.Exception e) { Debug.LogError($"[AvPresenterHost] Audio listener failed: {e}", this); }
                };
                presenter.EventPerformed += _audioRelay;
            }

            // The one exception, and it proves the rule: which battle theme to play is
            // state, not performance. The transition holds the performance while the wipe
            // covers the screen, so a kind relayed on the performed tap would let a trainer
            // battle open on the wild theme for however long the hold lasts. Arrival is the
            // correct clock for it, exactly as it is for the HUD's numbers.
            presenter.EventObserved += OnEventObservedForState;
        }

        private void UnbindPresenter()
        {
            if (!_presenterBound) return;
            _presenterBound = false;

            // The managed object outlives the Unity object, so unsubscribing from a
            // destroyed presenter is safe — event add/remove never touches native state.
            if (!ReferenceEquals(_presenter, null))
            {
                _presenter.EventPerformed -= OnEventPerformedForBinding;
                if (_vfxRelay != null) _presenter.EventPerformed -= _vfxRelay;
                if (_audioRelay != null) _presenter.EventPerformed -= _audioRelay;
                _presenter.EventObserved -= OnEventObservedForState;
            }
            _presenter = null;
            _vfxRelay = null;
            _audioRelay = null;

            if (_battleVfx != null) _battleVfx.ChoreographyOwnsBeats = false;
            VfxApi.ClearCreatureViews();
        }

        /// <summary>Arrival-clock state relays. See the note where this is subscribed.</summary>
        private static void OnEventObservedForState(BattleEvent evt)
        {
            if (evt is BattleStartedEvent started && ServiceHub.TryGet(out MusicDirector music))
                music.SetBattleKind(started.Kind);
        }

        // ---- creature view binding ---------------------------------------------------

        private void OnEventPerformedForBinding(BattleEvent evt)
        {
            switch (evt)
            {
                case CreatureSentOutEvent sent:
                    StartCoroutine(BindViewWhenStaged(sent.Side));
                    break;
                case BattleEndedEvent _:
                    // Bindings cleared at the end rather than on scene unload alone, so a
                    // rematch staged into the same arena starts from a clean slate.
                    VfxApi.ClearCreatureViews();
                    break;
            }
        }

        /// <summary>
        /// One frame late on purpose. EventPerformed fires as the beat opens, and the beat
        /// is what calls <c>Stage.Occupy</c> — so on the event's own frame the view for
        /// this send-out does not exist yet. A frame later it does.
        /// </summary>
        private IEnumerator BindViewWhenStaged(BattleSide side)
        {
            yield return null;
            if (_presenter == null) yield break;

            var stage = _presenter.Stage;
            CreatureView view = stage != null ? stage.ViewOf(side) : null;
            // CreatureView implements Core's ICreatureView directly, so no adapter is
            // needed — the VFX layer sees the same anchors the choreography animates.
            if (view != null) VfxApi.BindCreatureView(side, view);
        }

        // ---- footsteps ---------------------------------------------------------------

        private void BindPlayer(PlayerLocomotion player)
        {
            _player = player;
            _playerBound = true;
            player.Footstep += OnFootstep;
        }

        private void UnbindPlayer()
        {
            if (!_playerBound) return;
            _playerBound = false;
            if (!ReferenceEquals(_player, null)) _player.Footstep -= OnFootstep;
            _player = null;
        }

        /// <summary>
        /// The bridge the ownership rules forbid either side from building: the overworld
        /// raises a stride-driven footfall, the audio layer knows how a footfall should
        /// sound, and neither assembly may reference the other. Traversal state answers
        /// the easy cases (water, tall grass); everything else asks the ground beneath the
        /// foot, via the audio layer's own name classifier, so untagged terrain still
        /// resolves to something sensible.
        /// </summary>
        private void OnFootstep(Vector3 position, TraversalState traversal)
        {
            if (_overworldAudio == null) return;

            FootstepSurface surface;
            switch (traversal)
            {
                case TraversalState.Water:
                    surface = FootstepSurface.Water;
                    break;
                case TraversalState.TallGrass:
                    surface = FootstepSurface.Grass;
                    break;
                default:
                    surface = ClassifyGround(position);
                    break;
            }

            _overworldAudio.PlayFootstep(surface, position);
        }

        private static FootstepSurface ClassifyGround(Vector3 position)
        {
            if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out var hit, 3f,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                var collider = hit.collider;
                string hint = collider.tag != "Untagged" ? collider.tag : null;
                if (string.IsNullOrEmpty(hint) && collider.sharedMaterial != null)
                    hint = collider.sharedMaterial.name;
                if (string.IsNullOrEmpty(hint)) hint = collider.name;
                return OverworldAudio.ClassifySurface(hint);
            }
            return FootstepSurface.Dirt;
        }
    }
}
