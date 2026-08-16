using System;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Third-person ground movement.
    ///
    /// A raw <see cref="CharacterController"/> feels stiff because it has no acceleration model,
    /// snaps its capsule up steps, and loses contact on downhill slopes. This solves all three:
    /// velocity is integrated with separate accelerate/brake/turn rates, slopes are handled by
    /// projecting motion onto the ground plane plus an explicit stick-down force, and step-ups
    /// are detected ahead of the capsule and then *visually* smoothed so the mesh glides rather
    /// than pops.
    ///
    /// The visual smoothing is applied to <see cref="_visualRoot"/>, never to the controller, so
    /// physics and gameplay positions stay exact — the integrator restores the player after a
    /// battle to the controller position and it is frame-accurate.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public sealed class PlayerLocomotion : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private OverworldInputReader _input;
        [Tooltip("Transform the movement direction is taken relative to. Defaults to the main camera.")]
        [SerializeField] private Transform _cameraTransform;
        [Tooltip("Child holding the visible mesh. Step and landing smoothing is applied here only.")]
        [SerializeField] private Transform _visualRoot;

        [Header("Speeds (m/s)")]
        [SerializeField] private float _walkSpeed = 3.2f;
        [SerializeField] private float _runSpeed = 6.0f;
        [SerializeField] private float _swimSpeed = 2.4f;
        [Tooltip("Speed floor applied the instant input starts, so the first step is not mushy.")]
        [SerializeField] private float _initialImpulseSpeed = 1.1f;

        [Header("Acceleration (m/s^2)")]
        [Tooltip("Ramp up toward the target speed.")]
        [SerializeField] private float _acceleration = 26f;
        [Tooltip("Ramp down to a stop. Higher than acceleration gives crisp stops without ice-skating.")]
        [SerializeField] private float _braking = 38f;
        [Tooltip("Extra lateral authority when redirecting at speed. Makes hard turns feel planted.")]
        [SerializeField] private float _turnAcceleration = 34f;
        [Tooltip("Fraction of ground acceleration available while airborne.")]
        [Range(0f, 1f)][SerializeField] private float _airControl = 0.35f;

        [Header("Rotation")]
        [Tooltip("Seconds to settle the facing onto the movement direction. Small = snappy.")]
        [SerializeField] private float _turnSmoothTime = 0.08f;
        [Tooltip("Hard cap so a 180 never spins visibly faster than the rig can animate.")]
        [SerializeField] private float _maxTurnRate = 900f;

        [Header("Gravity and ground")]
        [SerializeField] private float _gravity = -24f;
        [Tooltip("Constant downward push while grounded. Keeps the capsule glued to downhill slopes.")]
        [SerializeField] private float _stickToGroundForce = 6f;
        [SerializeField] private float _groundProbeDistance = 0.35f;
        [SerializeField] private LayerMask _groundMask = ~0;
        [Tooltip("Grace window after leaving the ground before the controller reports airborne.")]
        [SerializeField] private float _coyoteTime = 0.12f;
        [Tooltip("Metres below the last solid ground before the player is put back on it. " +
                 "Falling out of the world has to be recoverable however the level is built.")]
        [SerializeField] private float _fallRecoveryDrop = 12f;

        [Tooltip("Seconds of asking to move and not moving, while standing lower than the " +
                 "ground last walked on, before the player is lifted out. This is the pit " +
                 "case: grounded, alive, and walled in by slopes past the climb limit.")]
        [SerializeField] private float _stuckSeconds = 1.6f;

        [Tooltip("How far below the last freely-walked ground counts as being down a hole. " +
                 "Above this the player is just pushing on a wall, which is allowed.")]
        [SerializeField] private float _stuckDepth = 0.6f;

        [Header("Slopes")]
        [Tooltip("Above this angle the player slides instead of walking. Matches CharacterController.slopeLimit.")]
        [SerializeField] private float _slopeLimit = 48f;
        [SerializeField] private float _slideAcceleration = 14f;
        [SerializeField] private float _maxSlideSpeed = 9f;

        [Header("Step-up")]
        [SerializeField] private bool _stepAssistEnabled = true;
        [SerializeField] private float _maxStepHeight = 0.45f;
        [SerializeField] private float _minStepHeight = 0.05f;
        [Tooltip("How far ahead of the capsule the ledge is probed.")]
        [SerializeField] private float _stepProbeDistance = 0.45f;
        [Tooltip("Metres per second the capsule is lifted onto a ledge.")]
        [SerializeField] private float _stepUpSpeed = 6f;
        [Tooltip("Seconds the mesh lags behind a vertical jolt. 0 disables the smoothing.")]
        [SerializeField] private float _visualStepSmoothing = 0.09f;

        [Header("Water")]
        [Tooltip("Y the capsule is held at while surfing. Set by the water body on entry.")]
        [SerializeField] private float _waterBuoyancyDamping = 8f;

        private readonly RaycastHit[] _groundHits = new RaycastHit[8];
        private Vector3 _lastGroundedPosition;
        private bool _hasGroundedPosition;
        private Vector3 _lastFreePosition;
        private bool _hasFreePosition;
        private float _stuckTimer;
        private CharacterController _controller;
        private Vector3 _horizontalVelocity;
        private float _verticalVelocity;
        private Vector3 _groundNormal = Vector3.up;
        private float _timeSinceGrounded;
        private float _turnVelocity;
        private float _pendingStepUp;
        private float _visualYOffset;
        private float _visualYVelocity;
        private Vector3 _previousPosition;
        private CollisionFlags _collisionFlags;
        private bool _motionFrozen;
        private float _surfaceY;
        private Vector3 _lastVisualLocalPosition;

        /// <summary>Planar speed in m/s this frame.</summary>
        public float Speed => _horizontalVelocity.magnitude;

        /// <summary>Speed as a 0-1 fraction of the current mode's run speed — the animator blend input.</summary>
        public float NormalisedSpeed
        {
            get
            {
                var top = Mathf.Max(0.01f, Traversal == TraversalState.Water ? _swimSpeed : _runSpeed);
                return Mathf.Clamp01(Speed / top);
            }
        }

        public bool IsGrounded => _timeSinceGrounded <= _coyoteTime;
        public bool IsRunning => _input != null && _input.Run && Speed > _walkSpeed * 0.6f;
        public Vector3 Velocity => _horizontalVelocity + Vector3.up * _verticalVelocity;
        public Vector3 GroundNormal => _groundNormal;

        /// <summary>Signed slope in degrees: positive uphill relative to the facing, negative downhill.</summary>
        public float SignedSlope { get; private set; }

        /// <summary>Degrees per second the body turned this frame, for lean and foot-plant blends.</summary>
        public float TurnRate { get; private set; }

        public TraversalState Traversal { get; private set; } = TraversalState.Ground;

        /// <summary>Planar metres covered this frame. Encounter pressure integrates this.</summary>
        public float DistanceThisFrame { get; private set; }

        /// <summary>Planar metres covered since the scene loaded.</summary>
        public float TotalDistance { get; private set; }

        /// <summary>Raised each time the accumulated stride distance passes a footfall.</summary>
        public event Action<Vector3, TraversalState> Footstep;

        [SerializeField] private float _strideLength = 1.35f;
        private float _strideAccumulator;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _controller.slopeLimit = _slopeLimit;
            // We do our own, smoothed step handling; leaving Unity's large step offset on top of
            // it produces a double lift on the same ledge.
            _controller.stepOffset = Mathf.Min(_controller.stepOffset, _minStepHeight * 2f);

            if (_cameraTransform == null && Camera.main != null) _cameraTransform = Camera.main.transform;
            if (_input == null) _input = GetComponent<OverworldInputReader>();
            if (_visualRoot != null) _lastVisualLocalPosition = _visualRoot.localPosition;

            _previousPosition = transform.position;
        }

        private void Update()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            ProbeGround();
            RecoverFromFall();
            RecoverFromPit(dt);

            if (_motionFrozen)
            {
                // Decay rather than zero: a hard stop on a transition reads as a hitch.
                _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, Vector3.zero, _braking * 2f * dt);
                _verticalVelocity = IsGrounded ? -_stickToGroundForce : _verticalVelocity + _gravity * dt;
            }
            else
            {
                var desired = ReadDesiredVelocity();
                IntegrateHorizontal(desired, dt);
                IntegrateVertical(dt);
                UpdateFacing(dt);
                UpdateStepAssist(dt);
            }

            ApplyMotion(dt);
            UpdateVisualSmoothing(dt);
            AccumulateDistance();
        }

        private Vector3 ReadDesiredVelocity()
        {
            if (_input == null || !_input.InputEnabled) return Vector3.zero;

            var raw = _input.Move;
            if (raw.sqrMagnitude < 0.0001f) return Vector3.zero;

            // Camera-relative on the horizontal plane. Using the camera's flattened basis keeps
            // "forward" meaning "away from the camera" even when it is pitched steeply down.
            Vector3 forward, right;
            if (_cameraTransform != null)
            {
                forward = Vector3.ProjectOnPlane(_cameraTransform.forward, Vector3.up);
                if (forward.sqrMagnitude < 0.0001f) forward = Vector3.ProjectOnPlane(_cameraTransform.up, Vector3.up);
                forward.Normalize();
                right = Vector3.Cross(Vector3.up, forward);
            }
            else
            {
                forward = Vector3.forward;
                right = Vector3.right;
            }

            var direction = forward * raw.y + right * raw.x;
            // Clamp rather than normalise so analogue sticks keep their fine speed control.
            if (direction.sqrMagnitude > 1f) direction.Normalize();

            var targetSpeed = Traversal == TraversalState.Water
                ? _swimSpeed
                : (_input.Run ? _runSpeed : _walkSpeed);

            return direction * targetSpeed;
        }

        private void IntegrateHorizontal(Vector3 desired, float dt)
        {
            var wantsMove = desired.sqrMagnitude > 0.0001f;

            // Sliding down a too-steep face takes control away and adds gravity along the slope.
            if (IsGrounded && Vector3.Angle(_groundNormal, Vector3.up) > _slopeLimit)
            {
                var slideDir = Vector3.ProjectOnPlane(Vector3.down, _groundNormal).normalized;
                _horizontalVelocity += slideDir * (_slideAcceleration * dt);
                if (_horizontalVelocity.magnitude > _maxSlideSpeed)
                    _horizontalVelocity = _horizontalVelocity.normalized * _maxSlideSpeed;
                return;
            }

            float rate;
            if (!wantsMove)
            {
                rate = _braking;
            }
            else if (Speed > 0.2f)
            {
                // Redirecting costs more authority than accelerating in a straight line, which is
                // what makes a hard turn read as weight rather than as a pivot on ice.
                var alignment = Vector3.Dot(_horizontalVelocity.normalized, desired.normalized);
                rate = Mathf.Lerp(_turnAcceleration, _acceleration, Mathf.InverseLerp(-1f, 1f, alignment));
            }
            else
            {
                rate = _acceleration;
                // Kick off the standstill so the first frame of input produces visible motion.
                if (_horizontalVelocity.sqrMagnitude < 0.0001f)
                    _horizontalVelocity = desired.normalized * Mathf.Min(_initialImpulseSpeed, desired.magnitude);
            }

            if (!IsGrounded) rate *= _airControl;

            _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, desired, rate * dt);

            // Following the ground plane rather than the world plane stops the capsule from
            // launching off the crest of a ramp and skipping down the far side.
            if (IsGrounded && Traversal != TraversalState.Water)
            {
                var planar = Vector3.ProjectOnPlane(_horizontalVelocity, _groundNormal);
                SignedSlope = Vector3.SignedAngle(Vector3.ProjectOnPlane(_groundNormal, transform.right), Vector3.up, transform.right);
                if (planar.sqrMagnitude > 0.0001f)
                    _horizontalVelocity = planar.normalized * _horizontalVelocity.magnitude;
            }
            else
            {
                SignedSlope = 0f;
            }
        }

        private void IntegrateVertical(float dt)
        {
            if (Traversal == TraversalState.Water)
            {
                // Critically-damped pull back to the surface height instead of gravity, so
                // entering water from a bank settles rather than bobbing.
                var error = _surfaceY - transform.position.y;
                _verticalVelocity = Mathf.Lerp(_verticalVelocity, error * _waterBuoyancyDamping, 1f - Mathf.Exp(-_waterBuoyancyDamping * dt));
                return;
            }

            if (IsGrounded && _verticalVelocity <= 0f)
            {
                _verticalVelocity = -_stickToGroundForce;
            }
            else
            {
                _verticalVelocity += _gravity * dt;
                // Terminal velocity keeps a long fall from tunnelling the capsule through geometry.
                _verticalVelocity = Mathf.Max(_verticalVelocity, _gravity * 2f);
            }
        }

        private void UpdateFacing(float dt)
        {
            if (_horizontalVelocity.sqrMagnitude < 0.04f)
            {
                TurnRate = Mathf.MoveTowards(TurnRate, 0f, 720f * dt);
                return;
            }

            var target = Mathf.Atan2(_horizontalVelocity.x, _horizontalVelocity.z) * Mathf.Rad2Deg;
            var current = transform.eulerAngles.y;
            var next = Mathf.SmoothDampAngle(current, target, ref _turnVelocity, _turnSmoothTime, _maxTurnRate, dt);
            TurnRate = Mathf.DeltaAngle(current, next) / dt;
            transform.rotation = Quaternion.Euler(0f, next, 0f);
        }

        /// <summary>
        /// Probes for a ledge in front of the capsule and queues a lift. Doing this ourselves
        /// rather than raising <c>CharacterController.stepOffset</c> means we control the rate,
        /// which is what stops the classic instant teleport onto a kerb.
        /// </summary>
        private void UpdateStepAssist(float dt)
        {
            if (!_stepAssistEnabled || Traversal == TraversalState.Water)
            {
                _pendingStepUp = 0f;
                return;
            }

            if (_pendingStepUp <= 0f && IsGrounded && (_collisionFlags & CollisionFlags.Sides) != 0 && Speed > 0.3f)
            {
                var dir = _horizontalVelocity.normalized;
                var feet = transform.position - Vector3.up * (_controller.height * 0.5f - _controller.radius);
                var probeOrigin = feet + Vector3.up * (_maxStepHeight + 0.05f) + dir * _stepProbeDistance;

                if (Physics.Raycast(probeOrigin, Vector3.down, out var hit, _maxStepHeight + 0.15f, _groundMask, QueryTriggerInteraction.Ignore))
                {
                    var rise = hit.point.y - (transform.position.y - _controller.height * 0.5f);
                    var walkable = Vector3.Angle(hit.normal, Vector3.up) <= _slopeLimit;
                    if (walkable && rise > _minStepHeight && rise <= _maxStepHeight)
                    {
                        // A hair of overshoot so the capsule clears the lip instead of catching it.
                        _pendingStepUp = rise + 0.02f;
                    }
                }
            }

            if (_pendingStepUp > 0f)
            {
                var lift = Mathf.Min(_pendingStepUp, _stepUpSpeed * dt);
                _pendingStepUp -= lift;
                _controller.Move(Vector3.up * lift);
                _visualYOffset -= lift; // the mesh stays put; the capsule rises under it
                _verticalVelocity = 0f;
            }
        }

        private void ApplyMotion(float dt)
        {
            var motion = _horizontalVelocity * dt + Vector3.up * (_verticalVelocity * dt);
            _collisionFlags = _controller.Move(motion);

            // Cancel accumulated fall speed on landing; without this the stick-to-ground force
            // fights a large negative value for several frames and the landing reads as a bounce.
            if ((_collisionFlags & CollisionFlags.Below) != 0 && _verticalVelocity < 0f)
            {
                if (_verticalVelocity < -8f) _visualYOffset -= 0.06f; // hard landing dip
                _verticalVelocity = -_stickToGroundForce;
            }
            if ((_collisionFlags & CollisionFlags.Above) != 0 && _verticalVelocity > 0f) _verticalVelocity = 0f;
        }

        private void UpdateVisualSmoothing(float dt)
        {
            if (_visualRoot == null || _visualStepSmoothing <= 0f) return;

            _visualYOffset = Mathf.SmoothDamp(_visualYOffset, 0f, ref _visualYVelocity, _visualStepSmoothing, Mathf.Infinity, dt);
            if (Mathf.Abs(_visualYOffset) < 0.0005f) _visualYOffset = 0f;

            var local = _lastVisualLocalPosition;
            local.y += _visualYOffset;
            _visualRoot.localPosition = local;
        }

        /// <summary>
        /// Puts the player back on the last ground they stood on if they leave the world.
        ///
        /// A play-mode probe walked north-east out of the town and off the edge of the
        /// height field, and then kept falling: y went -1.7, -7.5, -49, -109, -231 with no
        /// floor anywhere and no way back. Whether the edge itself should be walled off is
        /// a level question, but "the player fell out of the world and the session is over"
        /// must not be reachable however the level is built, so the recovery lives here.
        ///
        /// The threshold is measured from the last grounded height rather than from a fixed
        /// world Y, because the map has real relief and a constant floor would either sit
        /// above the gorge or below nothing.
        /// </summary>
        private void RecoverFromFall()
        {
            if (IsGrounded)
            {
                _lastGroundedPosition = transform.position;
                _hasGroundedPosition = true;
                return;
            }

            if (!_hasGroundedPosition) return;
            if (transform.position.y > _lastGroundedPosition.y - _fallRecoveryDrop) return;

            Debug.LogWarning($"[Player] Fell {_fallRecoveryDrop:F0} m below the last solid " +
                             "ground and was put back. Something in the level has a hole in it " +
                             $"near {_lastGroundedPosition}.");
            Warp(_lastGroundedPosition, transform.rotation);
            _timeSinceGrounded = 0f;
        }

        /// <summary>
        /// Lifts the player out of a hole they cannot climb.
        ///
        /// The fall recovery above only triggers on a long drop, because that is what
        /// leaving the world looks like. A pit is the opposite shape and just as final: the
        /// player is grounded, unhurt and completely stuck, because every wall around them
        /// is past the 48-degree climb limit and there is no jump in this game. The world is
        /// generated, so holes like that are not authored and cannot be reviewed away — a
        /// dip between two rocks the scatter happened to place is enough.
        ///
        /// Two conditions together, and both are needed. Asking to move and not moving is
        /// also what pushing into a wall looks like, and that is legitimate; standing lower
        /// than the ground last walked freely is what makes it a hole rather than a wall.
        /// </summary>
        private void RecoverFromPit(float dt)
        {
            var wants = _input != null && _input.InputEnabled && _input.Move.sqrMagnitude > 0.04f;

            if (IsGrounded && Speed > 0.5f)
            {
                _lastFreePosition = transform.position;
                _hasFreePosition = true;
                _stuckTimer = 0f;
                return;
            }

            if (!wants || !IsGrounded || !_hasFreePosition)
            {
                _stuckTimer = 0f;
                return;
            }

            if (transform.position.y > _lastFreePosition.y - _stuckDepth)
            {
                // Level with where they were walking: this is a wall, and walls are allowed.
                _stuckTimer = 0f;
                return;
            }

            _stuckTimer += dt;
            if (_stuckTimer < _stuckSeconds) return;

            Debug.LogWarning($"[Player] Stuck at {transform.position} for {_stuckSeconds:F1}s " +
                             "in a hole with no way out, and was lifted back to " +
                             $"{_lastFreePosition}. The scatter has left a trap there.");
            Warp(_lastFreePosition, transform.rotation);
            _stuckTimer = 0f;
        }

        private void ProbeGround()
        {
            var origin = transform.position + Vector3.up * (_controller.radius + 0.02f);
            var radius = Mathf.Max(0.01f, _controller.radius - 0.02f);
            var distance = _controller.radius + _groundProbeDistance;

            // The probe starts inside the character's own capsule, so it has to skip itself.
            // A self-hit comes back at distance 0 with a zero normal, which reads as ground
            // with an undefined slope: the player is permanently "grounded" on nothing, and
            // the slide check compares against a normal that is not a direction.
            var count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, _groundHits,
                distance, _groundMask, QueryTriggerInteraction.Ignore);
            var nearest = float.PositiveInfinity;
            var normal = Vector3.up;
            for (var i = 0; i < count; i++)
            {
                var candidate = _groundHits[i];
                if (candidate.collider == null) continue;
                if (candidate.collider.transform.root == transform) continue;
                if (candidate.distance <= 0.0001f) continue;
                if (candidate.distance >= nearest) continue;
                nearest = candidate.distance;
                normal = candidate.normal;
            }

            if (!float.IsPositiveInfinity(nearest))
            {
                _groundNormal = normal;
                _timeSinceGrounded = 0f;
            }
            else if (_controller.isGrounded)
            {
                // Fall back to the controller flag: a sphere cast misses thin ledges the capsule
                // is genuinely resting on.
                _groundNormal = Vector3.up;
                _timeSinceGrounded = 0f;
            }
            else
            {
                _groundNormal = Vector3.up;
                _timeSinceGrounded += Time.deltaTime;
            }
        }

        private void AccumulateDistance()
        {
            var delta = transform.position - _previousPosition;
            delta.y = 0f;
            DistanceThisFrame = delta.magnitude;
            TotalDistance += DistanceThisFrame;
            _previousPosition = transform.position;

            if (DistanceThisFrame <= 0f) return;

            _strideAccumulator += DistanceThisFrame;
            var stride = Mathf.Max(0.2f, IsRunning ? _strideLength * 1.35f : _strideLength);
            while (_strideAccumulator >= stride)
            {
                _strideAccumulator -= stride;
                Footstep?.Invoke(transform.position, Traversal);
            }
        }

        /// <summary>True while motion is externally frozen.</summary>
        public bool IsMotionFrozen => _motionFrozen;

        /// <summary>
        /// Freezes motion without disabling the component, so the animator keeps ticking and the
        /// idle blend stays live during a transition. Traversal state is deliberately preserved —
        /// a player frozen on the lake must still be surfing when they are unfrozen.
        /// </summary>
        public void SetMotionFrozen(bool frozen) => _motionFrozen = frozen;

        /// <summary>Sets the traversal mode. Water bodies and grass patches call this on overlap.</summary>
        public void SetTraversal(TraversalState state, float surfaceY = 0f)
        {
            if (state == TraversalState.Water) _surfaceY = surfaceY;
            if (Traversal == state) return;
            Traversal = state;
            OverworldEvents.RaiseTraversalChanged(state);
        }

        /// <summary>
        /// Repositions the player exactly. The controller has to be disabled around the write or
        /// its internal transform cache reasserts the old position on the next Move.
        /// </summary>
        public void Warp(Vector3 position, Quaternion rotation, bool preserveVelocity = false)
        {
            var wasEnabled = _controller.enabled;
            _controller.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            _controller.enabled = wasEnabled;

            _previousPosition = position;
            _visualYOffset = 0f;
            _visualYVelocity = 0f;
            _pendingStepUp = 0f;
            if (!preserveVelocity)
            {
                _horizontalVelocity = Vector3.zero;
                _verticalVelocity = 0f;
                _turnVelocity = 0f;
            }
        }
    }
}
