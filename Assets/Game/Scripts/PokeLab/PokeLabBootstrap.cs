using System;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using PokeLab.Core;
using PokeLab.Intelligence.Data;
using UnityEngine;
using UnityEngine.Networking;

namespace PokeLab.Intelligence
{
    /// <summary>
    /// The explicit entry point the integrator calls to bring the Poké Lab online.
    ///
    /// The forest is 4.5 MB and the dex is another 360 KB of JSON, which is far too much to
    /// touch on the first frame. Reading and parsing therefore happen off the main thread;
    /// only the ServiceHub registration comes back to it. Nothing here assumes a scene, so
    /// a boot scene, an editor test, or a command-line harness can all drive it.
    /// </summary>
    public static class PokeLabBootstrap
    {
        private static PokeLabOracle _oracle;

        /// <summary>
        /// The load in flight, or null. One task shared by every caller: it is what a second
        /// caller waits on, and it publishes before it completes, so anyone who has awaited
        /// it can read <see cref="Oracle"/>.
        /// </summary>
        private static Task<PokeLabOracle> _load;

        public static bool IsInitialized => _oracle != null;

        /// <summary>Null until initialisation completes.</summary>
        public static PokeLabOracle Oracle => _oracle;

        /// <summary>Raised on the main thread once the services are registered.</summary>
        public static event Action<PokeLabOracle> Ready;

        /// <summary>StreamingAssets/pokelab, as a path or a platform URL.</summary>
        public static string DataRoot =>
            Path.Combine(Application.streamingAssetsPath, PokeLabData.Files.Directory);

        /// <summary>
        /// Coroutine form, for a boot scene: <c>yield return
        /// StartCoroutine(PokeLabBootstrap.InitializeRoutine())</c>. Safe to call twice.
        /// </summary>
        public static IEnumerator InitializeRoutine(Action<Exception> onError = null)
        {
            if (_oracle != null) yield break;

            // A second caller waits for the load already in flight rather than returning
            // straight away. It used to yield break on the in-flight flag, so the caller
            // resumed on the same frame it started and read Oracle as null — which is the
            // one thing yielding on this was supposed to make impossible.
            var load = BeginLoad();
            while (!load.IsCompleted) yield return null;

            if (load.IsFaulted)
            {
                if (ReferenceEquals(_load, load)) _load = null;
                var error = Flatten(load.Exception);
                Debug.LogError($"[PokeLab] initialisation failed: {error}");
                onError?.Invoke(error);
            }
        }

        /// <summary>Task form, for callers that already have an async context.</summary>
        public static async Task<PokeLabOracle> InitializeAsync()
        {
            if (_oracle != null) return _oracle;
            var load = BeginLoad();
            try
            {
                return await load;
            }
            catch
            {
                // A load that failed must not become the permanent answer: the export may
                // simply not have been run yet, and a later attempt should get to try.
                if (ReferenceEquals(_load, load)) _load = null;
                throw;
            }
        }

        /// <summary>
        /// The one load, started on demand. Publishing happens inside it rather than in the
        /// callers, so "the task completed" and "the services are registered" are the same
        /// moment however many callers are waiting.
        /// </summary>
        private static Task<PokeLabOracle> BeginLoad()
        {
            if (_load == null) _load = LoadAndPublish(DataRoot);
            return _load;
        }

        private static async Task<PokeLabOracle> LoadAndPublish(string root)
        {
            Publish(await LoadAsync(root));
            return _oracle;
        }

        /// <summary>
        /// Synchronous form. Only for EditMode tests and tooling — it blocks for the full
        /// several-megabyte read and must never be called from a live frame.
        /// </summary>
        public static PokeLabOracle InitializeNow(string dataRoot = null)
        {
            if (_oracle != null) return _oracle;
            Publish(ReadFrom(dataRoot ?? DataRoot));
            return _oracle;
        }

        /// <summary>Drops the loaded data. Pair with ServiceHub.Reset on play mode exit.</summary>
        public static void Reset()
        {
            _oracle = null;
            _load = null;
            Ready = null;
        }

        private static void Publish(PokeLabData data)
        {
            _oracle = new PokeLabOracle(data);

            ServiceHub.Register<IPokeLabOracle>(_oracle);
            ServiceHub.Register<ISpeciesRegistry>(data.Species);
            ServiceHub.Register<IMoveRegistry>(data.Moves);
            ServiceHub.Register<ITypeChart>(data.TypeChart);

            Ready?.Invoke(_oracle);
        }

        private static async Task<PokeLabData> LoadAsync(string root)
        {
            // UnityWebRequest is only needed where StreamingAssets is not a real directory
            // (Android's APK, WebGL). On Windows standalone and in the editor it is a plain
            // path, so the whole load — IO and parse — can go to a worker thread.
            if (root.Contains("://"))
            {
                var species = await ReadTextViaWebRequest(root, PokeLabData.Files.Species);
                var moves = await ReadTextViaWebRequest(root, PokeLabData.Files.Moves);
                var chart = await ReadTextViaWebRequest(root, PokeLabData.Files.TypeChart);
                var forest = await ReadBytesViaWebRequest(root, PokeLabData.Files.Forest);

                // Optional here exactly as it is on the file path: combats.bin is the
                // head-to-head history, and PokeLabData.Parse accepts it missing. Fetching
                // it like everything else meant a 404 on the one artefact documented as
                // optional took the species, the moves, the type chart and the forest down
                // with it, and left the scanner offline for the whole session.
                var combats = await ReadBytesViaWebRequest(root, PokeLabData.Files.Combats,
                                                           required: false);

                // Parsed on the calling thread in a web player, because there is no other one.
                //
                // A WebGL build without threads support has no thread pool behind Task.Run.
                // The work is never scheduled, so the task never completes, so the boot
                // coroutine that yields on it yields forever — the game sits on a loading
                // screen with an empty console and nothing to say it is stuck. Where IL2CPP
                // throws instead, it throws from inside a task nobody observes, which is the
                // same silence by a different route.
                //
                // Parsing the 4.5 MB forest inline costs a visible hitch on the loading
                // screen. A hitch is a far better failure than a hang, and it is the only
                // option this platform offers.
#if UNITY_WEBGL && !UNITY_EDITOR
                return PokeLabData.Parse(species, moves, chart, forest, combats);
#else
                // Parsing is pure CPU, so keep it off the main thread even on these platforms.
                return await Task.Run(() => PokeLabData.Parse(species, moves, chart, forest, combats));
#endif
            }

            return await Task.Run(() => ReadFrom(root));
        }

        private static PokeLabData ReadFrom(string root)
        {
            string Text(string file)
            {
                var path = Path.Combine(root, file);
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        $"Poké Lab data missing: {path}. Run Tools/Export/export_pokelab.py.", path);
                return File.ReadAllText(path, Encoding.UTF8);
            }

            byte[] Bytes(string file, bool required)
            {
                var path = Path.Combine(root, file);
                if (!File.Exists(path))
                {
                    if (required)
                        throw new FileNotFoundException(
                            $"Poké Lab data missing: {path}. Run Tools/Export/export_pokelab.py.", path);
                    return null;
                }
                return File.ReadAllBytes(path);
            }

            return PokeLabData.Parse(
                Text(PokeLabData.Files.Species),
                Text(PokeLabData.Files.Moves),
                Text(PokeLabData.Files.TypeChart),
                Bytes(PokeLabData.Files.Forest, required: true),
                Bytes(PokeLabData.Files.Combats, required: false));
        }

        private static async Task<string> ReadTextViaWebRequest(string root, string file)
        {
            using var request = UnityWebRequest.Get(root + "/" + file);
            await SendAsync(request, file);
            return request.downloadHandler.text;
        }

        private static async Task<byte[]> ReadBytesViaWebRequest(string root, string file,
                                                                 bool required = true)
        {
            using var request = UnityWebRequest.Get(root + "/" + file);
            if (!await SendAsync(request, file, required)) return null;
            return request.downloadHandler.data;
        }

        /// <summary>
        /// Runs the request. Returns false only when an optional artefact could not be
        /// fetched; anything required still throws.
        /// </summary>
        private static async Task<bool> SendAsync(UnityWebRequest request, string file,
                                                  bool required = true)
        {
            var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();
            if (request.result == UnityWebRequest.Result.Success) return true;
            if (required)
                throw new IOException($"Poké Lab could not read {file}: {request.error}");

            Debug.LogWarning($"[PokeLab] Optional data {file} was not served ({request.error}). " +
                             "Predictions carry on without the head-to-head history.");
            return false;
        }

        private static Exception Flatten(AggregateException exception)
        {
            var flattened = exception?.Flatten();
            return flattened?.InnerExceptions.Count == 1 ? flattened.InnerExceptions[0] : flattened;
        }
    }

    /// <summary>
    /// Drop-on component for the boot scene, for integrators who would rather wire this in
    /// the inspector than call <see cref="PokeLabBootstrap"/> from their own boot script.
    /// </summary>
    public sealed class PokeLabInstaller : MonoBehaviour
    {
        [Tooltip("Begin loading in Start. Turn off to drive initialisation from your own boot sequence.")]
        [SerializeField] private bool _initializeOnStart = true;

        [Tooltip("Keep this object alive across the scene load into the overworld.")]
        [SerializeField] private bool _dontDestroyOnLoad = true;

        private void Awake()
        {
            if (_dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (_initializeOnStart && !PokeLabBootstrap.IsInitialized) StartCoroutine(Load());
        }

        /// <summary>Start the load explicitly; yields until the services are registered.</summary>
        public IEnumerator Load() => PokeLabBootstrap.InitializeRoutine();
    }
}
