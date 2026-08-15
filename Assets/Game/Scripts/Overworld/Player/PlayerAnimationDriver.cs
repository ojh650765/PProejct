using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Pushes locomotion state into the Animator as parameters.
    ///
    /// This exists as its own component, and reads only public state off
    /// <see cref="PlayerLocomotion"/>, so the environment artist's humanoid rig can be swapped in
    /// without touching movement code. Parameter names are serialized rather than hardcoded
    /// because the controller asset is authored by the integrator, not here.
    ///
    /// Every parameter is looked up once and skipped if the controller does not declare it, so a
    /// half-built Animator logs nothing and still animates whatever it does have.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerAnimationDriver : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private Animator _animator;
        [SerializeField] private PlayerLocomotion _locomotion;

        [Header("Parameter names")]
        [Tooltip("Float 0-1. Drive a walk/run blend tree from this.")]
        [SerializeField] private string _moveBlendParam = "MoveBlend";
        [Tooltip("Float, metres per second. For rigs that scale playback to real speed.")]
        [SerializeField] private string _speedParam = "Speed";
        [SerializeField] private string _groundedParam = "IsGrounded";
        [SerializeField] private string _runningParam = "IsRunning";
        [Tooltip("Float, degrees per second, signed. Drives lean and foot pivots.")]
        [SerializeField] private string _turnRateParam = "TurnRate";
        [Tooltip("Float, signed slope in degrees. Drives uphill/downhill posture.")]
        [SerializeField] private string _slopeParam = "Slope";
        [SerializeField] private string _swimmingParam = "IsSwimming";
        [SerializeField] private string _verticalSpeedParam = "VerticalSpeed";
        [SerializeField] private string _interactTrigger = "Interact";
        [SerializeField] private string _emoteTrigger = "Emote";

        [Header("Smoothing")]
        [Tooltip("Damping applied to the blend value. The Animator, not the controller, owns this "
                 + "so movement stays responsive while the animation stays smooth.")]
        [SerializeField] private float _blendDamping = 0.09f;
        [SerializeField] private float _turnRateDamping = 0.12f;
        [Tooltip("Degrees per second that maps to a turn parameter value of 1.")]
        [SerializeField] private float _turnRateScale = 220f;

        private int _moveBlendHash, _speedHash, _groundedHash, _runningHash, _turnRateHash;
        private int _slopeHash, _swimmingHash, _verticalHash, _interactHash, _emoteHash;
        private bool _hasMoveBlend, _hasSpeed, _hasGrounded, _hasRunning, _hasTurnRate;
        private bool _hasSlope, _hasSwimming, _hasVertical, _hasInteract, _hasEmote;
        private float _smoothedTurn;

        private void Awake()
        {
            if (_animator == null) _animator = GetComponentInChildren<Animator>();
            if (_locomotion == null) _locomotion = GetComponentInParent<PlayerLocomotion>();
            CacheParameters();
        }

        private void CacheParameters()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return;

            _moveBlendHash = Animator.StringToHash(_moveBlendParam); _hasMoveBlend = HasParam(_moveBlendParam);
            _speedHash = Animator.StringToHash(_speedParam); _hasSpeed = HasParam(_speedParam);
            _groundedHash = Animator.StringToHash(_groundedParam); _hasGrounded = HasParam(_groundedParam);
            _runningHash = Animator.StringToHash(_runningParam); _hasRunning = HasParam(_runningParam);
            _turnRateHash = Animator.StringToHash(_turnRateParam); _hasTurnRate = HasParam(_turnRateParam);
            _slopeHash = Animator.StringToHash(_slopeParam); _hasSlope = HasParam(_slopeParam);
            _swimmingHash = Animator.StringToHash(_swimmingParam); _hasSwimming = HasParam(_swimmingParam);
            _verticalHash = Animator.StringToHash(_verticalSpeedParam); _hasVertical = HasParam(_verticalSpeedParam);
            _interactHash = Animator.StringToHash(_interactTrigger); _hasInteract = HasParam(_interactTrigger);
            _emoteHash = Animator.StringToHash(_emoteTrigger); _hasEmote = HasParam(_emoteTrigger);
        }

        private bool HasParam(string named)
        {
            if (string.IsNullOrEmpty(named) || _animator == null) return false;
            var parameters = _animator.parameters;
            for (var i = 0; i < parameters.Length; i++)
                if (parameters[i].name == named) return true;
            return false;
        }

        private void Update()
        {
            if (_animator == null || _locomotion == null) return;
            var dt = Time.deltaTime;

            if (_hasMoveBlend) _animator.SetFloat(_moveBlendHash, _locomotion.NormalisedSpeed, _blendDamping, dt);
            if (_hasSpeed) _animator.SetFloat(_speedHash, _locomotion.Speed, _blendDamping, dt);
            if (_hasGrounded) _animator.SetBool(_groundedHash, _locomotion.IsGrounded);
            if (_hasRunning) _animator.SetBool(_runningHash, _locomotion.IsRunning);
            if (_hasSwimming) _animator.SetBool(_swimmingHash, _locomotion.Traversal == TraversalState.Water);
            if (_hasVertical) _animator.SetFloat(_verticalHash, _locomotion.Velocity.y);
            if (_hasSlope) _animator.SetFloat(_slopeHash, _locomotion.SignedSlope, _blendDamping, dt);

            if (_hasTurnRate)
            {
                var target = Mathf.Clamp(_locomotion.TurnRate / Mathf.Max(1f, _turnRateScale), -1f, 1f);
                _smoothedTurn = Mathf.Lerp(_smoothedTurn, target, 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, _turnRateDamping)));
                _animator.SetFloat(_turnRateHash, _smoothedTurn);
            }
        }

        /// <summary>Fires the interact trigger. Called by <see cref="PlayerInteractor"/> on a hit.</summary>
        public void PlayInteract()
        {
            if (_animator != null && _hasInteract) _animator.SetTrigger(_interactHash);
        }

        /// <summary>Fires a generic emote trigger, used for the encounter telegraph beat.</summary>
        public void PlayEmote()
        {
            if (_animator != null && _hasEmote) _animator.SetTrigger(_emoteHash);
        }

        /// <summary>Re-caches parameter presence. Call after swapping the runtime controller.</summary>
        public void Rebind()
        {
            if (_animator != null) _animator.Rebind();
            CacheParameters();
        }
    }
}
