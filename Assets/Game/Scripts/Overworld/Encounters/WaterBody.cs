using System.Collections;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// A lake or river surface. Entering it puts the player into the water traversal state and
    /// starts building water-table encounter pressure; the telegraph is a ripple rather than a
    /// rustle, but the beat serves the same purpose.
    ///
    /// The surface height is taken from the collider's top face rather than from the transform,
    /// so a scaled or offset water plane still floats the player at the visible waterline.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [DisallowMultipleComponent]
    public sealed class WaterBody : MonoBehaviour, IEncounterSource
    {
        [Header("Encounter rate")]
        [Range(0f, 3f)][SerializeField] private float _densityMultiplier = 1f;
        [SerializeField] private bool _generatesEncounters = true;

        [Header("Surface")]
        [Tooltip("Override the surface height instead of reading the collider's top face.")]
        [SerializeField] private bool _overrideSurfaceHeight;
        [SerializeField] private float _surfaceHeight;
        [Tooltip("How far below the surface the player's pivot rides while surfing.")]
        [SerializeField] private float _rideDepth = 0.35f;

        [Header("Requirements")]
        [Tooltip("Gate entering the water on an item or badge the player must own. Empty = always allowed.")]
        [SerializeField] private string _requiredItemId = "";

        [Tooltip("Require a Pokémon that can carry the player. Without one they are stopped " +
                 "at the waterline — which is what makes the lake a boundary the player can " +
                 "see past and come back to, rather than a hole they fall in.")]
        [SerializeField] private bool _requiresSurf = true;

        [Header("Ripple")]
        [SerializeField] private ParticleSystem _rippleEffect;
        [SerializeField] private float _rippleDuration = 0.65f;
        [SerializeField] private Vector3Event _rippleStarted = new Vector3Event();

        private Collider _collider;
        private PlayerLocomotion _player;
        private bool _playerInside;

        /// <summary>Last position the blocked player held on dry land, to put them back on.</summary>
        private Vector3 _lastDryPosition;
        private bool _hasDryPosition;

        public EncounterSourceKind SourceKind => EncounterSourceKind.Water;

        public Vector3 TelegraphPosition =>
            _player != null ? _player.transform.position : transform.position;

        /// <summary>World Y the player rides at while on this surface.</summary>
        public float SurfaceY =>
            (_overrideSurfaceHeight ? _surfaceHeight : (_collider != null ? _collider.bounds.max.y : transform.position.y)) - _rideDepth;

        private static readonly System.Collections.Generic.List<WaterBody> Live =
            new System.Collections.Generic.List<WaterBody>();

        /// <summary>Every water surface currently loaded. Used to place and contain swimmers.</summary>
        public static System.Collections.Generic.IReadOnlyList<WaterBody> AllLive => Live;

        /// <summary>Clears the live registry on play mode start; see OverworldLifecycle.</summary>
        internal static void ResetRegistry() => Live.Clear();

        /// <summary>World-space bounds of the surface, for picking candidate points over it.</summary>
        public Bounds SurfaceBounds =>
            _collider != null ? _collider.bounds : new Bounds(transform.position, Vector3.one);

        private void Awake()
        {
            _collider = GetComponent<Collider>();
            if (_collider != null && !_collider.isTrigger)
            {
                _collider.isTrigger = true;
                Debug.LogWarning($"[WaterBody] Collider on '{name}' was not a trigger; forced isTrigger.", this);
            }
        }

        private void OnEnable() => Live.Add(this);

        private void OnDisable() => Live.Remove(this);

        /// <summary>
        /// True when the column through <paramref name="worldPoint"/> passes through this surface,
        /// and reports where the waterline is there.
        ///
        /// A downward raycast rather than a bounds test, because the lake mesh is an irregular
        /// polygon inside a rectangular bounding box — a bounds test puts swimmers on the bank at
        /// every concave corner, which is exactly where the player is standing to look at them.
        /// The cast asks for triggers explicitly: the surface is a trigger so that the player is
        /// stopped by the bank rather than by the water, and the default query mode would miss it
        /// entirely and report every lake as dry land.
        /// </summary>
        public bool TryGetSurfaceAt(Vector3 worldPoint, out Vector3 surfacePoint)
        {
            surfacePoint = worldPoint;
            if (_collider == null) return false;

            var bounds = _collider.bounds;
            var origin = new Vector3(worldPoint.x, bounds.max.y + 1f, worldPoint.z);
            var distance = bounds.size.y + 2f;

            if (!_collider.Raycast(new Ray(origin, Vector3.down), out var hit, distance)) return false;
            surfacePoint = new Vector3(worldPoint.x, hit.point.y - _rideDepth, worldPoint.z);
            return true;
        }

        /// <summary>
        /// Finds the surface under a point across every loaded body. Returns false on dry land,
        /// which is what keeps a swimmer from wandering out of its lake.
        /// </summary>
        public static bool TrySampleAnySurface(Vector3 worldPoint, out Vector3 surfacePoint)
        {
            for (var i = 0; i < Live.Count; i++)
            {
                var body = Live[i];
                if (body == null) continue;
                if (body.TryGetSurfaceAt(worldPoint, out surfacePoint)) return true;
            }
            surfacePoint = worldPoint;
            return false;
        }

        /// <summary>
        /// A point actually on this body's surface, for spawning a water creature.
        ///
        /// Rejection sampling over the bounding box, because the alternative — picking a random
        /// triangle by area — needs the mesh, and the mesh belongs to a MeshCollider that may be
        /// marked non-readable in a build. Four attempts is enough for the lake shapes in this
        /// project and costs nothing when it fails, since the caller retries next tick.
        /// </summary>
        public bool TryGetRandomSurfacePoint(ref DeterministicRandom rng, out Vector3 point)
        {
            point = transform.position;
            if (_collider == null) return false;

            var bounds = _collider.bounds;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var candidate = new Vector3(
                    rng.Range(bounds.min.x, bounds.max.x),
                    bounds.max.y,
                    rng.Range(bounds.min.z, bounds.max.z));
                if (TryGetSurfaceAt(candidate, out point)) return true;
            }
            return false;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag(OverworldNames.PlayerTag)) return;

            var locomotion = other.GetComponentInParent<PlayerLocomotion>();
            if (locomotion == null) return;
            _player = locomotion;
            _playerInside = true;
        }

        private void OnTriggerExit(Collider other)
        {
            if (!other.CompareTag(OverworldNames.PlayerTag)) return;
            if (_player != null && _player.Traversal == TraversalState.Water)
                _player.SetTraversal(TraversalState.Ground);
            _playerInside = false;
        }

        /// <summary>Raised when the player is turned back, so a bump or a line can play.</summary>
        public event System.Action<Vector3> Refused;

        /// <summary>
        /// Puts the player back where they last stood on land.
        ///
        /// A push-out along the surface normal is the usual answer and is wrong on a lake:
        /// the surface is horizontal, so its normal points up and pushing along it launches
        /// the player. Restoring the last dry position is exact, needs no shoreline geometry,
        /// and reads as the step simply not being taken — which is what the series does.
        /// </summary>
        private void Refuse()
        {
            if (_player == null || !_hasDryPosition) return;
            _player.Warp(_lastDryPosition, _player.transform.rotation);
            Refused?.Invoke(_lastDryPosition);
        }

        private void Update()
        {
            // The player has to be tracked before they reach the water, not from the trigger:
            // by the time OnTriggerEnter fires they are already standing in it, and the last
            // position on dry land — the only one worth putting them back on — is gone.
            if (_player == null)
            {
                var found = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
                if (found == null) return;
                _player = found.GetComponentInParent<PlayerLocomotion>();
                if (_player == null) return;
            }

            if (!_playerInside)
            {
                if (_player.Traversal != TraversalState.Water && _player.IsGrounded)
                {
                    _lastDryPosition = _player.transform.position;
                    _hasDryPosition = true;
                }
                return;
            }

            if (!IsAllowed())
            {
                Refuse();
                return;
            }

            // Re-assert the surface each frame: a lake with a sloped bed or a moving platform
            // would otherwise drift the ride height after the entry frame.
            _player.SetTraversal(TraversalState.Water, SurfaceY);

            if (!_generatesEncounters) return;
            var director = EncounterDirector.Instance;
            if (director == null) return;
            director.AccumulatePressure(this, _player.DistanceThisFrame, _densityMultiplier);
        }

        /// <summary>
        /// Gates entry on an item, so the lake can be a soft progression wall. Absent a profile
        /// the water is open — a missing service must never lock the player out of content.
        /// </summary>
        private bool IsAllowed()
        {
            if (_requiresSurf && !SurfCapability.CanSurf()) return false;

            if (string.IsNullOrEmpty(_requiredItemId)) return true;
            if (!PokeLab.Core.ServiceHub.TryGet<PokeLab.Core.IPlayerProfile>(out var profile)) return true;
            return profile.Inventory != null
                   && profile.Inventory.TryGetValue(_requiredItemId, out var count)
                   && count > 0;
        }

        public float PlayApproachTelegraph()
        {
            _rippleStarted.Invoke(TelegraphPosition);
            if (_rippleEffect != null)
            {
                _rippleEffect.transform.position = TelegraphPosition;
                _rippleEffect.Play();
                StartCoroutine(StopRipple());
            }
            return _rippleDuration;
        }

        private IEnumerator StopRipple()
        {
            yield return new WaitForSeconds(_rippleDuration);
            if (_rippleEffect != null) _rippleEffect.Stop();
        }
    }
}
