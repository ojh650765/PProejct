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

        /// <summary>
        /// Last known extent of each zone's trigger volumes, in world space.
        ///
        /// The volumes deregister themselves when their content root is switched off, which is
        /// to say that the geometry a zone's distance is measured from disappears at exactly
        /// the moment the zone is unloaded. Distance then fell back to the zone pivot, and a
        /// route whose pivot is a hundred metres from the end you are standing at could never
        /// come back within the load radius — its volumes were off, so it could not regain
        /// active or neighbour status either, and the area was simply gone for the rest of the
        /// session. Remembering where it was is what breaks that circle.
        /// </summary>
        private readonly Dictionary<WorldZone, Bounds> _extents = new Dictionary<WorldZone, Bounds>();

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

                // Taken every pass while the volumes are still live, because the pass that
                // unloads the zone is the last one that can see them.
                RememberExtent(zone);

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

        /// <summary>Records the zone's volume extent while there is still one to record.</summary>
        private void RememberExtent(WorldZone zone)
        {
            var found = false;
            var extent = new Bounds();

            for (var i = 0; i < zone.Volumes.Count; i++)
            {
                var volume = zone.Volumes[i];
                if (volume == null) continue;
                var collider = volume.GetComponent<Collider>();
                // A disabled collider reports an empty box at the origin, which would poison
                // the cache with a shape the zone has never had.
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;

                if (!found) { extent = collider.bounds; found = true; }
                else extent.Encapsulate(collider.bounds);
            }

            if (found)
            {
                _extents[zone] = extent;
                return;
            }

            // Nothing live to measure and nothing remembered — a zone whose content root was
            // already off when the streamer started, which is the case the live reading can
            // never reach. Derived from the collider shapes and their transforms instead,
            // which are readable on a disabled object even though bounds is not.
            if (!_extents.ContainsKey(zone) && TryComputeStaticExtent(zone, out var authored))
                _extents[zone] = authored;
        }

        /// <summary>Volume extent taken from collider geometry rather than from live bounds.</summary>
        private static bool TryComputeStaticExtent(WorldZone zone, out Bounds extent)
        {
            extent = new Bounds();
            var found = false;

            foreach (var volume in zone.GetComponentsInChildren<ZoneVolume>(true))
            {
                if (volume == null) continue;
                Bounds one;

                switch (volume.GetComponent<Collider>())
                {
                    case BoxCollider box:
                        one = BoxBounds(box);
                        break;
                    case SphereCollider sphere:
                        var scale = sphere.transform.lossyScale;
                        var radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x),
                            Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                        one = new Bounds(sphere.transform.TransformPoint(sphere.center),
                            Vector3.one * (radius * 2f));
                        break;
                    default:
                        continue;
                }

                if (!found) { extent = one; found = true; }
                else extent.Encapsulate(one);
            }

            return found;
        }

        private static Bounds BoxBounds(BoxCollider box)
        {
            var half = box.size * 0.5f;
            var bounds = new Bounds(box.transform.TransformPoint(box.center), Vector3.zero);
            for (var corner = 0; corner < 8; corner++)
            {
                var local = box.center + new Vector3(
                    (corner & 1) == 0 ? -half.x : half.x,
                    (corner & 2) == 0 ? -half.y : half.y,
                    (corner & 4) == 0 ? -half.z : half.z);
                bounds.Encapsulate(box.transform.TransformPoint(local));
            }
            return bounds;
        }

        private float DistanceTo(WorldZone zone, Vector3 point)
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
            if (best < float.MaxValue) return best;

            // Unloaded, so its volumes have deregistered. The remembered extent is coarser
            // than the volumes were — one box around all of them rather than the nearest face
            // of the nearest — and coarse in the safe direction: it can only bring a zone back
            // early, never leave it stranded.
            if (_extents.TryGetValue(zone, out var extent))
                return Vector3.Distance(extent.ClosestPoint(point), point);

            return Vector3.Distance(zone.transform.position, point);
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
