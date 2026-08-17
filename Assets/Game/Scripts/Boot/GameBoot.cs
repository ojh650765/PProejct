using System.Diagnostics;
using PokeLab.Core;
using PokeLab.Intelligence;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PokeLab.Boot
{
    /// <summary>
    /// The composition root. This is the only assembly allowed to reference every system, and
    /// the only place that decides what is registered and in what order.
    ///
    /// It exists because ordering by <c>DefaultExecutionOrder</c> alone is not enough: the dex
    /// loads several megabytes from StreamingAssets, and <c>PokeLabInstaller</c> does that on a
    /// coroutine. Anything that resolves <see cref="ISpeciesRegistry"/> during its own
    /// <c>Awake</c>/<c>Start</c> — <c>PlayerProfileHost</c> builds the starter party at
    /// execution order -500 — would run first and silently fall back to estimated stats.
    ///
    /// So the load is deliberately synchronous and happens before any other script wakes. It
    /// costs a hitch exactly once, at boot, where a loading screen already belongs. Everything
    /// downstream can then assume the services exist rather than each inventing its own guard.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class GameBoot : MonoBehaviour
    {
        [Tooltip("Log how long the dex and forest took to load. Useful when the hitch grows.")]
        [SerializeField] private bool _logTimings = true;

        [Tooltip("Keep the services alive across the load into the overworld scene.")]
        [SerializeField] private bool _persist = true;

        /// <summary>True once every service the game needs is on the hub.</summary>
        public static bool ServicesReady { get; private set; }

        private void Awake()
        {
            // A second GameBoot stands down rather than re-initialising over the first. It
            // is not enough for it to be harmless on the way in: on the way out it would
            // take the shared ServiceHub with it.
            if (_owner != null && _owner != this)
            {
                enabled = false;
                return;
            }
            _owner = this;

            if (_persist) DontDestroyOnLoad(gameObject);
            InitializeServices();
        }

        /// <summary>
        /// Loads the Poké Lab data the way a browser can, then finishes booting.
        ///
        /// Its own coroutine because the load is genuinely asynchronous there — five files
        /// fetched over HTTP — and everything after it in InitializeServices assumes the
        /// registries exist. Running that assumption a frame early is what produced a whole
        /// session of estimated stats.
        /// </summary>
        private System.Collections.IEnumerator InitialiseDataThenServices()
        {
            var task = PokeLabBootstrap.InitializeAsync();
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                UnityEngine.Debug.LogError("[Boot] The Poké Lab data could not be loaded: " +
                                           task.Exception?.GetBaseException().Message +
                                           " Every creature will be built from estimated stats.", this);
            }

            InitializeServices();
        }

        private void InitializeServices()
        {
            var sw = Stopwatch.StartNew();

            // Registers IPokeLabOracle, ISpeciesRegistry, IMoveRegistry and ITypeChart itself.
            //
            // The synchronous path reads the data off disk with File.Exists and File.ReadAllText,
            // which is correct in the editor and on desktop and cannot work in a browser: there
            // StreamingAssets is a URL served over HTTP, not a directory, and every file reads
            // as missing. The deployed build threw FileNotFoundException on the very first
            // frame and then ran the whole game on estimated stats — creatures with the wrong
            // numbers, a dex with nothing in it, and one line in a console nobody sees.
            //
            // The async path already handles both, so on the web the boot waits for it. That
            // costs the boot scene a few frames without a loading screen, which is a great deal
            // better than a game running on invented data.
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!PokeLabBootstrap.IsInitialized) StartCoroutine(InitialiseDataThenServices());
            return;
#else
            if (!PokeLabBootstrap.IsInitialized) PokeLabBootstrap.InitializeNow();

            sw.Stop();

#endif
            var ok = ServiceHub.Has<ISpeciesRegistry>()
                     && ServiceHub.Has<IMoveRegistry>()
                     && ServiceHub.Has<ITypeChart>()
                     && ServiceHub.Has<IPokeLabOracle>();

            ServicesReady = ok;

            if (!ok)
            {
                Debug.LogError(
                    "[GameBoot] Poké Lab services failed to register. The dex, move pool and " +
                    "forest live in Assets/StreamingAssets/pokelab — check they were built.");
                return;
            }

            // Registered here rather than by the spawner, because a roamer that resolves the
            // factory in its own Awake would find nothing on the first frame of a scene load and
            // spend its whole life invisible.
            RoamerBillboardArtFactory.RegisterIfNothingElseHas();

            if (_logTimings)
            {
                var species = ServiceHub.Get<ISpeciesRegistry>();
                Debug.Log($"[GameBoot] Services ready in {sw.ElapsedMilliseconds} ms — {species.Count} species loaded.");
            }
        }

        private void OnApplicationQuit() => Teardown();

        private static GameBoot _owner;

        private void OnDestroy()
        {
            // Only the instance that actually initialised the services may tear them down.
            //
            // The guard used to be `if (ServicesReady)`, which is a static and therefore true
            // for every copy — so a second GameBoot arriving with an additively loaded scene
            // and then being unloaded wiped registrations the first one still owned. The
            // comment claimed to handle exactly that case and could not, because nothing
            // recorded which instance was the owner.
            if (_owner == this && ServicesReady) Teardown();
        }

        private static void Teardown()
        {
            // Statics survive domain reload when the editor has it disabled, so a stale forest
            // and stale event subscribers would leak into the next play session.
            PokeLabBootstrap.Reset();
            ServiceHub.Reset();
            GameEvents.Reset();
            ServicesReady = false;
            // Released with the services it owned, or the next session finds a stale owner
            // that no longer exists and never initialises at all.
            _owner = null;
        }
    }
}
