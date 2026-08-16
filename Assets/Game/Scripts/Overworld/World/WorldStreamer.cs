using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Overworld.World
{
    /// <summary>
    /// Keeps neighbouring parts of the map loaded around the player, so walking between
    /// them is walking rather than a cut.
    ///
    /// The map was split into Town and Field because one scene meant one height field for
    /// two zones and everything in both loaded at once. That split was right, but the way
    /// it was joined was not: a trigger at the boundary did
    /// <c>LoadSceneAsync(Single)</c> — tear the world down, build the other one, put the
    /// player at a marker. However fast that is, it is a teleport, and it happens at a
    /// gate the player is walking through at four metres a second.
    ///
    /// It does not have to be. Both halves are emitted **into the same world space** — the
    /// Town band runs z -50 to 8 and the Field band z -2 to 70, overlapping by ten metres,
    /// and every object in both carries its true world coordinate. So loading the second
    /// one additively does not need anything reconciled: the two meshes meet along the
    /// overlap and the player simply keeps walking. This is Unity's equivalent of Unreal's
    /// level streaming, and the reason it works here at all is that overlap.
    ///
    /// Hard loads remain for places that are genuinely elsewhere — cave interiors, house
    /// interiors, the battle scene. Those are not adjacent to anything and have their own
    /// coordinate space, so a cut is the honest presentation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldStreamer : MonoBehaviour
    {
        [Serializable]
        public sealed class Band
        {
            public string scene;
            [Tooltip("The z range this scene's content occupies, in world space.")]
            public float zMin;
            public float zMax;
        }

        [Header("Bands")]
        [Tooltip("Matches SCENES in Tools/Level/emit_unity_layout.py. If those move, these " +
                 "must move with them — a band that lies about where its scene is loads it " +
                 "too late, and the player walks into a hole where the ground has not " +
                 "arrived yet.")]
        [SerializeField]
        private Band[] _bands =
        {
            new Band { scene = "Town",  zMin = -50f, zMax = 8f },
            new Band { scene = "Field", zMin = -2f,  zMax = 70f },
        };

        [Tooltip("Scenes whose content this scene already contains, and which must therefore " +
                 "never be streamed in alongside it. Empty now that Town and Overworld are not " +
                 "two copies of one place; kept because the next duplicate will want it.")]
        [SerializeField] private string[] _alreadyContained = new string[0];

        [Header("Distance")]
        [Tooltip("Metres before a band's edge at which it starts loading. Has to be more " +
                 "than the player can cover during the load, or they reach the boundary first.")]
        [SerializeField] private float _loadMargin = 45f;

        [Tooltip("Metres past a band's edge before it is unloaded. Deliberately larger than " +
                 "the load margin: equal values thrash a scene in and out while the player " +
                 "stands on the line.")]
        [SerializeField] private float _unloadMargin = 80f;

        [Tooltip("On. Every system a streamed scene duplicates now refuses to be the second " +
                 "instance in its own Awake, which is the only place that can work — see the " +
                 "note in Start.")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("Load every band at startup and never unload. On a map this size that is " +
                 "the right call: the whole world is 122 x 120 m, so holding both halves " +
                 "costs a few thousand triangles and removes every chance of the player " +
                 "outrunning a load.")]
        [SerializeField] private bool _preloadAll = true;

        [SerializeField] private float _checkInterval = 0.5f;

        private Transform _player;
        private readonly HashSet<string> _busy = new HashSet<string>();

        /// <summary>Scenes this streamer currently has loaded, for anything that needs to ask.</summary>
        public static readonly HashSet<string> Streamed = new HashSet<string>();

        /// <summary>
        /// Scenes this streamer is responsible for, whether or not they have arrived yet.
        ///
        /// A door must stand down on this rather than on <see cref="Streamed"/>. Preloading
        /// takes time, and the player walking into the boundary before the neighbour has
        /// finished arriving would find it "not loaded" and hard-cut to it — the teleport,
        /// back again, and only sometimes, which is the worst kind.
        /// </summary>
        public static readonly HashSet<string> Claimed = new HashSet<string>();

        private void Start()
        {
            // Every playable scene stands alone, so each carries a GameBoot, an EventSystem,
            // a camera, a player rig and its own scene links. Unity runs all of their Awakes
            // *during* the additive load, before this component can strip anything — so
            // stripping was never going to be the fix, and this was switched off until each
            // system could refuse to be the second instance in its own Awake.
            //
            // That is now true of every one that mattered: GameBoot records which instance
            // owns the services rather than letting any copy tear them down; DialogueRunner,
            // EncounterDirector, DayNightCycle and WeatherDirector all stand down as
            // duplicates, as ZoneDirector already did; the screen wipe rebuilds its bars
            // instead of writing to destroyed ones; and LevelTransition refuses to load a
            // scene that is already loaded or is the one it is standing in.
            //
            // StripDuplicateHosts still runs, for the objects that own nothing shared but
            // would be visible twice — the second player, the second camera. It runs in the
            // frame the load completes, before anything renders.
            if (!_enabled) return;

            // Claimed up front, before a single load begins, so a door can refuse from the
            // first frame rather than from whenever the load happens to finish.
            var active = SceneManager.GetActiveScene().name;
            foreach (var band in _bands)
            {
                if (band == null || string.IsNullOrEmpty(band.scene)) continue;
                if (band.scene == active) continue;
                if (Array.IndexOf(_alreadyContained, band.scene) >= 0) continue;
                Claimed.Add(band.scene);
            }

            if (_preloadAll) StartCoroutine(PreloadAll());
            else StartCoroutine(Watch());
        }

        /// <summary>
        /// Brings every band in before the player takes a step.
        ///
        /// Streaming on approach still has a moment where the neighbour is arriving, and on
        /// a walk toward the boundary that moment is visible as ground appearing. The user's
        /// requirement was that the join not be felt at all, and the cheapest way to
        /// guarantee that is to never do it while they are watching.
        /// </summary>
        private IEnumerator PreloadAll()
        {
            var active = SceneManager.GetActiveScene().name;
            foreach (var band in _bands)
            {
                if (band == null || string.IsNullOrEmpty(band.scene)) continue;
                if (band.scene == active) continue;
                if (Array.IndexOf(_alreadyContained, band.scene) >= 0) continue;
                if (SceneManager.GetSceneByName(band.scene).isLoaded) continue;

                yield return Load(band.scene);
            }
        }

        private IEnumerator Watch()
        {
            var wait = new WaitForSeconds(Mathf.Max(0.1f, _checkInterval));
            while (true)
            {
                if (_player == null)
                {
                    var found = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
                    _player = found != null ? found.transform : null;
                }

                if (_player != null) Evaluate(_player.position.z);
                yield return wait;
            }
        }

        private void Evaluate(float playerZ)
        {
            var active = SceneManager.GetActiveScene().name;

            foreach (var band in _bands)
            {
                if (band == null || string.IsNullOrEmpty(band.scene)) continue;
                if (band.scene == active) continue;
                if (Array.IndexOf(_alreadyContained, band.scene) >= 0) continue;
                if (_busy.Contains(band.scene)) continue;

                // Distance to the band, zero inside it.
                var distance = playerZ < band.zMin ? band.zMin - playerZ
                             : playerZ > band.zMax ? playerZ - band.zMax
                             : 0f;

                var loaded = SceneManager.GetSceneByName(band.scene).isLoaded;

                if (!loaded && distance <= _loadMargin) StartCoroutine(Load(band.scene));
                else if (loaded && distance >= _unloadMargin) StartCoroutine(Unload(band.scene));
            }
        }

        private IEnumerator Load(string scene)
        {
            if (!Application.CanStreamedLevelBeLoaded(scene))
            {
                // Said once, not once per check: a missing scene would otherwise fill the
                // console twice a second for the rest of the session.
                _busy.Add(scene);
                Debug.LogError($"[Streaming] '{scene}' is not in the build settings, so the " +
                               "world stops at its edge. Add it under File > Build Settings.", this);
                yield break;
            }

            _busy.Add(scene);
            var op = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Additive);
            while (op != null && !op.isDone) yield return null;

            StripDuplicateHosts(scene);
            Streamed.Add(scene);
            _busy.Remove(scene);
        }

        private IEnumerator Unload(string scene)
        {
            _busy.Add(scene);
            Streamed.Remove(scene);
            var op = SceneManager.UnloadSceneAsync(scene);
            while (op != null && !op.isDone) yield return null;
            _busy.Remove(scene);
        }

        /// <summary>
        /// Removes the second copy of everything a scene carries that must be unique.
        ///
        /// Every playable scene is built to stand alone, so each one has its own player,
        /// camera rig, input reader, lighting director and service hosts. Loaded additively
        /// that means two players, two cameras fighting over the brain, and two input
        /// readers both driving. The streamed scene keeps its ground, its props and its
        /// people; everything that owns the session is dropped, because the scene the
        /// player is standing in already has one.
        /// </summary>
        private void StripDuplicateHosts(string scene)
        {
            var loaded = SceneManager.GetSceneByName(scene);
            if (!loaded.isLoaded) return;

            foreach (var root in loaded.GetRootGameObjects())
            {
                var isSessionOwner =
                    root.GetComponentInChildren<PlayerLocomotion>(true) != null
                    || root.GetComponentInChildren<OverworldCameraRig>(true) != null
                    || root.GetComponentInChildren<OverworldInputReader>(true) != null
                    || root.GetComponent<Camera>() != null
                    || root.GetComponentInChildren<UnityEngine.EventSystems.EventSystem>(true) != null
                    || root.name == "GameHosts";

                if (isSessionOwner) Destroy(root);
            }
        }
    }
}
