using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Enables and disables zone content roots so only the area you are in, and the areas you can
    /// see into, are paying for themselves.
    ///
    /// This is deliberately GameObject-activation streaming rather than additive scene loading:
    /// the slice is one scene owned by the integrator, and additive loads would fragment it into
    /// files eight workers would then contend over. Activation gives most of the win — culled
    /// renderers, dormant NavMeshAgents, no NPC AI ticking in an empty town — with no scene churn.
    ///
    /// Hysteresis on the unload distance is what stops a zone thrashing when you stand on a border.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ZoneStreamer : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private ZoneDirector _director;
        [SerializeField] private Transform _player;
        [Tooltip("Every zone the streamer manages. Leave empty to discover them at Start.")]
        [SerializeField] private List<WorldZone> _zones = new List<WorldZone>();

        [Header("Distances")]
        [Tooltip("Zones with content within this range of the player stay loaded regardless of adjacency.")]
        [SerializeField] private float _loadRadius = 90f;
        [Tooltip("Must exceed the load radius. The gap is the hysteresis band that prevents thrash.")]
        [SerializeField] private float _unloadRadius = 130f;

        [Header("Behaviour")]
        [Tooltip("Seconds between evaluations. Streaming does not need to run every frame.")]
        [SerializeField] private float _evaluationInterval = 0.5f;
        [Tooltip("Keeps neighbours of the active zone loaded so a doorway never reveals an empty room.")]
        [SerializeField] private bool _keepNeighboursLoaded = true;
        [Tooltip("Turn off to debug with everything visible.")]
        [SerializeField] private bool _streamingEnabled = true;

        private readonly HashSet<WorldZone> _shouldBeLoaded = new HashSet<WorldZone>();
        private float _timer;

        private void Start()
        {
            if (_director == null) _director = ZoneDirector.Instance;
            if (_zones.Count == 0)
                _zones.AddRange(FindObjectsByType<WorldZone>(FindObjectsSortMode.None));

            if (_unloadRadius <= _loadRadius)
            {
                _unloadRadius = _loadRadius * 1.4f;
                Debug.LogWarning("[ZoneStreamer] Unload radius must exceed load radius; widened automatically.", this);
            }

            Evaluate();
        }

        private void Update()
        {
            if (!_streamingEnabled) return;
            _timer += Time.deltaTime;
            if (_timer < _evaluationInterval) return;
            _timer = 0f;
            Evaluate();
        }

        private void Evaluate()
        {
            if (_player == null) return;

            var active = _director != null ? _director.ActiveZone : null;
            var playerPosition = _player.position;

            _shouldBeLoaded.Clear();
            if (active != null)
            {
                _shouldBeLoaded.Add(active);
                if (_keepNeighboursLoaded)
                {
                    var neighbours = active.Neighbours;
                    for (var i = 0; i < neighbours.Count; i++)
                        if (neighbours[i] != null) _shouldBeLoaded.Add(neighbours[i]);
                }
            }

            for (var i = 0; i < _zones.Count; i++)
            {
                var zone = _zones[i];
                if (zone == null || zone.ContentRoot == null) continue;

                var currentlyLoaded = zone.ContentRoot.activeSelf;
                if (_shouldBeLoaded.Contains(zone))
                {
                    if (!currentlyLoaded) zone.ContentRoot.SetActive(true);
                    continue;
                }

                var distance = DistanceTo(zone, playerPosition);
                // Asymmetric thresholds: cross _loadRadius to come in, cross _unloadRadius to go
                // out. A single threshold would toggle every frame while standing on the line.
                if (!currentlyLoaded && distance <= _loadRadius) zone.ContentRoot.SetActive(true);
                else if (currentlyLoaded && distance >= _unloadRadius) zone.ContentRoot.SetActive(false);
            }
        }

        private static float DistanceTo(WorldZone zone, Vector3 point)
        {
            // Distance to the nearest volume surface, not to the zone's pivot: a long route's
            // pivot can be a hundred metres from the end you are standing at.
            var best = float.MaxValue;
            for (var i = 0; i < zone.Volumes.Count; i++)
            {
                var volume = zone.Volumes[i];
                if (volume == null) continue;
                var collider = volume.GetComponent<Collider>();
                if (collider == null) continue;
                var distance = Vector3.Distance(collider.bounds.ClosestPoint(point), point);
                if (distance < best) best = distance;
            }
            return best == float.MaxValue ? Vector3.Distance(zone.transform.position, point) : best;
        }

        /// <summary>Loads everything and stops streaming. Used by the debug rig and by editor tooling.</summary>
        public void LoadAll()
        {
            _streamingEnabled = false;
            for (var i = 0; i < _zones.Count; i++)
                if (_zones[i] != null && _zones[i].ContentRoot != null) _zones[i].ContentRoot.SetActive(true);
        }
    }
}
