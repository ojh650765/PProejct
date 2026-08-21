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

            EnsureSystems();
            RebindSceneObjects();
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
                MemoryRelief.Report("loaded " + scene.name);
            else
                // Additive loads are the ones that kill the web build -- the arena goes on top
                // of whatever was already there -- so they are reported even though nothing is
                // reclaimed. Every path that can reach a battle passes through here, which the
                // instrumentation in BattleModeLauncher does not: the story's battles come in
                // through TransitionDirector instead.
                MemoryRelief.Report("additive load of " + scene.name);
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
        /// Seconds between memory samples while <see cref="MemoryRelief.Trace"/> is on.
        /// </summary>
        private const float MemorySampleSeconds = 2f;

        private float _nextMemorySample;

        private void Update()
        {
            // A time series, because a single reading says nothing. The arena's cost is the
            // DIFFERENCE across the load, and in the editor the absolute figure is mostly the
            // editor itself. Off unless somebody asks for it; two Profiler calls every two
            // seconds is cheap but not free.
            if (MemoryRelief.Trace && Time.unscaledTime >= _nextMemorySample)
            {
                _nextMemorySample = Time.unscaledTime + MemorySampleSeconds;
                MemoryRelief.Report($"t={Time.unscaledTime:F0}s");
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
