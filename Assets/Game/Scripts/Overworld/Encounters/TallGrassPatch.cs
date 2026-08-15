using System.Collections;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// A patch of tall grass. Walking through it builds encounter pressure; when the director
    /// decides to fire, this patch plays the rustle that warns the player a half-second before
    /// the transition begins.
    ///
    /// That warning beat is doing real work. Without it an encounter reads as the game
    /// interrupting you; with it, it reads as something noticing you first.
    ///
    /// The rustle is driven three ways so it works at every stage of integration: a procedural
    /// sway of a serialized transform (always available), an Animator trigger, and a UnityEvent
    /// the VFX worker binds a particle burst to. All three are optional.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [DisallowMultipleComponent]
    public sealed class TallGrassPatch : MonoBehaviour, IEncounterSource
    {
        [Header("Encounter rate")]
        [Tooltip("Multiplier on pressure gained here. Dense, deep grass earns more than a verge.")]
        [Range(0f, 3f)][SerializeField] private float _densityMultiplier = 1f;
        [Tooltip("Turn off for decorative grass the player should be able to cross safely.")]
        [SerializeField] private bool _generatesEncounters = true;

        [Header("Player feedback")]
        [Tooltip("Sets the player's traversal state so the animator can play a wading gait.")]
        [SerializeField] private bool _setsTraversalState = true;

        [Header("Rustle")]
        [Tooltip("Transform swayed procedurally during the rustle. Point this at the grass mesh root.")]
        [SerializeField] private Transform _rustleTransform;
        [Tooltip("Animator on the grass, if the artist authored a rustle clip.")]
        [SerializeField] private Animator _rustleAnimator;
        [SerializeField] private string _rustleTrigger = "Rustle";
        [Tooltip("Seconds the rustle plays before the director hands off to the transition.")]
        [SerializeField] private float _rustleDuration = 0.75f;
        [SerializeField] private float _rustleAmplitudeDegrees = 11f;
        [SerializeField] private float _rustleFrequency = 13f;
        [Tooltip("Wire the VFX worker's leaf burst here.")]
        [SerializeField] private Vector3Event _rustleStarted = new Vector3Event();

        private PlayerLocomotion _player;
        private Coroutine _rustleRoutine;
        private Quaternion _rustleRestRotation;
        private bool _playerInside;

        public EncounterSourceKind SourceKind => EncounterSourceKind.TallGrass;

        /// <summary>Where the rustle happens — the player's feet, not the patch pivot, because a
        /// patch may be twenty metres long.</summary>
        public Vector3 TelegraphPosition =>
            _player != null ? _player.transform.position : transform.position;

        private void Awake()
        {
            var collider = GetComponent<Collider>();
            if (collider != null && !collider.isTrigger)
            {
                collider.isTrigger = true;
                Debug.LogWarning($"[TallGrassPatch] Collider on '{name}' was not a trigger; forced isTrigger.", this);
            }
            if (_rustleTransform != null) _rustleRestRotation = _rustleTransform.localRotation;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag(OverworldNames.PlayerTag)) return;
            _player = other.GetComponentInParent<PlayerLocomotion>();
            _playerInside = true;
            if (_setsTraversalState && _player != null) _player.SetTraversal(TraversalState.TallGrass);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!other.CompareTag(OverworldNames.PlayerTag)) return;
            if (_setsTraversalState && _player != null && _player.Traversal == TraversalState.TallGrass)
                _player.SetTraversal(TraversalState.Ground);
            _playerInside = false;
            _player = null;
        }

        private void Update()
        {
            if (!_playerInside || !_generatesEncounters || _player == null) return;
            var director = EncounterDirector.Instance;
            if (director == null) return;

            // Distance, not time: standing still in grass is safe, sprinting through it is not.
            director.AccumulatePressure(this, _player.DistanceThisFrame, _densityMultiplier);
        }

        /// <summary>Plays the rustle and reports how long the director should hold for it.</summary>
        public float PlayApproachTelegraph()
        {
            _rustleStarted.Invoke(TelegraphPosition);

            if (_rustleAnimator != null && !string.IsNullOrEmpty(_rustleTrigger))
                _rustleAnimator.SetTrigger(_rustleTrigger);

            if (_rustleTransform != null)
            {
                if (_rustleRoutine != null) StopCoroutine(_rustleRoutine);
                _rustleRoutine = StartCoroutine(RustleSway());
            }

            return _rustleDuration;
        }

        /// <summary>
        /// A damped shake around the rest rotation. Decaying the amplitude rather than holding it
        /// is what makes it read as something moving through the grass and settling, instead of a
        /// looping wobble.
        /// </summary>
        private IEnumerator RustleSway()
        {
            var elapsed = 0f;
            while (elapsed < _rustleDuration)
            {
                elapsed += Time.deltaTime;
                var decay = 1f - Mathf.Clamp01(elapsed / _rustleDuration);
                var angle = Mathf.Sin(elapsed * _rustleFrequency) * _rustleAmplitudeDegrees * decay * decay;
                _rustleTransform.localRotation = _rustleRestRotation * Quaternion.Euler(angle, 0f, angle * 0.6f);
                yield return null;
            }
            _rustleTransform.localRotation = _rustleRestRotation;
            _rustleRoutine = null;
        }
    }
}
