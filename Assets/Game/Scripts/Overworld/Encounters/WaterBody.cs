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

        [Header("Ripple")]
        [SerializeField] private ParticleSystem _rippleEffect;
        [SerializeField] private float _rippleDuration = 0.65f;
        [SerializeField] private Vector3Event _rippleStarted = new Vector3Event();

        private Collider _collider;
        private PlayerLocomotion _player;
        private bool _playerInside;

        public EncounterSourceKind SourceKind => EncounterSourceKind.Water;

        public Vector3 TelegraphPosition =>
            _player != null ? _player.transform.position : transform.position;

        /// <summary>World Y the player rides at while on this surface.</summary>
        public float SurfaceY =>
            (_overrideSurfaceHeight ? _surfaceHeight : (_collider != null ? _collider.bounds.max.y : transform.position.y)) - _rideDepth;

        private void Awake()
        {
            _collider = GetComponent<Collider>();
            if (_collider != null && !_collider.isTrigger)
            {
                _collider.isTrigger = true;
                Debug.LogWarning($"[WaterBody] Collider on '{name}' was not a trigger; forced isTrigger.", this);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag(OverworldNames.PlayerTag)) return;
            if (!IsAllowed()) return;

            _player = other.GetComponentInParent<PlayerLocomotion>();
            _playerInside = true;
            if (_player != null) _player.SetTraversal(TraversalState.Water, SurfaceY);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!other.CompareTag(OverworldNames.PlayerTag)) return;
            if (_player != null && _player.Traversal == TraversalState.Water)
                _player.SetTraversal(TraversalState.Ground);
            _playerInside = false;
            _player = null;
        }

        private void Update()
        {
            if (!_playerInside || _player == null) return;

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
