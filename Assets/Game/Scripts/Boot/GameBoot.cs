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
            if (_persist) DontDestroyOnLoad(gameObject);
            InitializeServices();
        }

        private void InitializeServices()
        {
            var sw = Stopwatch.StartNew();

            // Registers IPokeLabOracle, ISpeciesRegistry, IMoveRegistry and ITypeChart itself.
            // InitializeNow is the synchronous path; the async one is for a loading screen that
            // wants to keep rendering, which the boot scene does not yet have.
            if (!PokeLabBootstrap.IsInitialized) PokeLabBootstrap.InitializeNow();

            sw.Stop();

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

            if (_logTimings)
            {
                var species = ServiceHub.Get<ISpeciesRegistry>();
                Debug.Log($"[GameBoot] Services ready in {sw.ElapsedMilliseconds} ms — {species.Count} species loaded.");
            }
        }

        private void OnApplicationQuit() => Teardown();

        private void OnDestroy()
        {
            // Only the root tears down, and only when it is the object leaving play mode —
            // a scene unload that destroys a copy must not wipe live registrations.
            if (ServicesReady) Teardown();
        }

        private static void Teardown()
        {
            // Statics survive domain reload when the editor has it disabled, so a stale forest
            // and stale event subscribers would leak into the next play session.
            PokeLabBootstrap.Reset();
            ServiceHub.Reset();
            GameEvents.Reset();
            ServicesReady = false;
        }
    }
}
