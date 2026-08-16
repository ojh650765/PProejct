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
                 "never be streamed in alongside it. Overworld is built from the town layout, " +
                 "so loading Town on top of it would draw the whole town twice.")]
        [SerializeField] private string[] _alreadyContained = { "Town" };

        [Header("Distance")]
        [Tooltip("Metres before a band's edge at which it starts loading. Has to be more " +
                 "than the player can cover during the load, or they reach the boundary first.")]
        [SerializeField] private float _loadMargin = 45f;

        [Tooltip("Metres past a band's edge before it is unloaded. Deliberately larger than " +
                 "the load margin: equal values thrash a scene in and out while the player " +
                 "stands on the line.")]
        [SerializeField] private float _unloadMargin = 80f;

        [Tooltip("Off until the duplicate-owner problem below is solved. Turning this on " +
                 "loads a second copy of everything that owns the session, and their Awakes " +
                 "run during the additive load — before this component can strip them.")]
        [SerializeField] private bool _enabled;

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

        private void Start()
        {
            // Deliberately inert by default, and this is not caution — it is a defect that
            // has to be fixed before this is switched on.
            //
            // Every playable scene is built to stand alone, so each carries a GameBoot, an
            // EventSystem, a camera, a player rig and a set of scene links. Loading one
            // additively runs all of their Awakes *during the load*, before StripDuplicateHosts
            // gets a turn, and four things break in that window: the second GameBoot
            // re-initialises ServiceHub and wipes the battle stage the first one registered;
            // two EventSystems fight; the screen wipe parents its bars to whichever camera
            // Camera.main answered with and they are destroyed under it; and the streamed
            // scene's own To_Town trigger sits in the ten-metre overlap and hard-loads Town
            // as the player walks past their friend, which is the teleport that was reported.
            //
            // The fix is not more stripping, which is always too late. Each session owner
            // needs to check for an existing one in its own Awake and stand down — the
            // ordinary singleton guard — and that means touching GameBoot and the rig, which
            // is a change worth making on its own rather than inside this.
            if (!_enabled) return;

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
                    || root.name == "GameHosts"
                    || root.name == "EventSystem";

                if (isSessionOwner) Destroy(root);
            }
        }
    }
}
