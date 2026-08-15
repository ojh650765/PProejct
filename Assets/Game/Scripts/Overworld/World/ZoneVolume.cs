using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// A trigger that reports the player entering or leaving a <see cref="WorldZone"/>.
    ///
    /// Zones are made of several volumes so an area can be an L-shaped route or a cave with two
    /// mouths. The volume itself carries no state beyond "is the player inside me" — the
    /// arbitration of which zone is *active* lives in <see cref="ZoneDirector"/>, because
    /// overlapping volumes at a doorway would otherwise fight each other.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [DisallowMultipleComponent]
    public sealed class ZoneVolume : MonoBehaviour
    {
        [SerializeField] private WorldZone _zone;
        [Tooltip("Higher wins where volumes overlap. Give a cave mouth a higher priority than the "
                 + "route it is carved into, or standing in the entrance flickers between the two.")]
        [SerializeField] private int _priority;
        [Tooltip("Extra transition seconds when entering through this volume. Use on cave mouths "
                 + "so the interior handover has time to breathe.")]
        [SerializeField] private float _transitionDurationOverride = -1f;

        private Collider _collider;

        public WorldZone Zone => _zone;
        public int Priority => _priority;

        /// <summary>Negative when this volume does not override the director's default duration.</summary>
        public float TransitionDurationOverride => _transitionDurationOverride;

        public bool PlayerInside { get; private set; }

        private void Awake()
        {
            _collider = GetComponent<Collider>();
            if (_collider != null && !_collider.isTrigger)
            {
                // A solid zone volume is a wall the player cannot walk through — always a mistake,
                // and one that is very confusing to debug in a scene, so fix it and say so.
                _collider.isTrigger = true;
                Debug.LogWarning($"[ZoneVolume] Collider on '{name}' was not a trigger; forced isTrigger.", this);
            }
            if (_zone == null) _zone = GetComponentInParent<WorldZone>();
        }

        private void OnEnable()
        {
            if (_zone != null) _zone.RegisterVolume(this);
        }

        private void OnDisable()
        {
            if (_zone != null) _zone.UnregisterVolume(this);
            if (PlayerInside)
            {
                PlayerInside = false;
                ZoneDirector.NotifyVolumeExit(this);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_zone == null || !IsPlayer(other)) return;
            if (PlayerInside) return;
            PlayerInside = true;
            ZoneDirector.NotifyVolumeEnter(this);
        }

        private void OnTriggerExit(Collider other)
        {
            if (_zone == null || !IsPlayer(other)) return;
            if (!PlayerInside) return;
            PlayerInside = false;
            ZoneDirector.NotifyVolumeExit(this);
        }

        private static bool IsPlayer(Collider other) =>
            other != null && other.CompareTag(OverworldNames.PlayerTag);

        /// <summary>Uniform point inside the volume's bounds, for roamer spawn sampling.</summary>
        public bool TryGetRandomPoint(ref DeterministicRandom rng, out Vector3 point)
        {
            if (_collider == null) _collider = GetComponent<Collider>();
            if (_collider == null)
            {
                point = transform.position;
                return false;
            }

            var bounds = _collider.bounds;
            point = new Vector3(
                rng.Range(bounds.min.x, bounds.max.x),
                bounds.max.y,
                rng.Range(bounds.min.z, bounds.max.z));
            return true;
        }

        private void OnDrawGizmosSelected()
        {
            var collider = GetComponent<Collider>();
            if (collider == null) return;
            Gizmos.color = new Color(0.2f, 1f, 0.6f, 0.15f);
            Gizmos.DrawCube(collider.bounds.center, collider.bounds.size);
        }
    }
}
