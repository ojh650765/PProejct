using System;
using PokeLab.Core;
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

        [Header("Getting unstuck")]
        [Tooltip("Seconds of asking to move and going nowhere before the player is examined " +
                 "for being trapped. Long enough that leaning on a fence is not a rescue.")]
        [SerializeField] private float _stuckSeconds = 1.6f;

        [Tooltip("Planar metres inside that window that count as going somewhere. Walking " +
                 "downhill covers this in a few frames; the floor of a hole does not.")]
        [SerializeField] private float _stuckTravel = 0.4f;

        [Tooltip("How far a direction has to be clear before it counts as a way out. About a " +
                 "stride: any shorter and the lip of the hole reads as an exit.")]
        [SerializeField] private float _escapeProbeDistance = 0.9f;

        [Tooltip("Furthest the search for standing ground looks when the player has to be put " +
                 "somewhere legal. Deliberately short, so a rescue is a step and not a journey.")]
        [SerializeField] private float _escapeSearchRadius = 3.5f;

        [Tooltip("Seconds the capsule may sit inside solid geometry before it is moved out. " +
                 "Not zero, because a single frame of overlap is a normal contact artefact.")]
        [SerializeField] private float _embeddedSeconds = 0.3f;

        [Header("Slopes")]
        [Tooltip("Above this angle the player slides instead of walking. Matches CharacterController.slopeLimit.")]
        [SerializeField] private float _slopeLimit = 48f;
        [SerializeField] private float _slideAcceleration = 14f;
        [SerializeField] private float _maxSlideSpeed = 9f;
        [Tooltip("Seconds a slide may cover no ground before the player gets the controls " +
                 "back. A real slide moves, so this never interrupts one; a slide jammed " +
                 "against a wall at the foot of the slope is not a slide and must not hold " +
                 "the player there.")]
        [SerializeField] private float _slideStallSeconds = 0.35f;

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

        private const string WaterLayerName = "Water";

        /// <summary>How far above a candidate a water surface still means "under the water".</summary>
        private const float WaterHeadroom = 8f;

        /// <summary>
        /// How far below the player a step down still counts as a way out. Generous, because
        /// walking off a bank always works; a real drop is caught by the fall recovery.
        /// </summary>
        private const float EscapeDropAllowance = 1.5f;

        /// <summary>Metres per second below which a slide counts as having stopped sliding.</summary>
        private const float SlideStallSpeed = 0.5f;

        /// <summary>
        /// Multiplier on the search radius for the second sweep, used only when the first has
        /// failed. Somewhere out over the middle of a lake the nearest dry ground is nowhere
        /// near a rescue's usual few metres, and refusing to look further would leave the
        /// player standing on water with the log explaining that nothing was close enough.
        /// </summary>
        private const float WideSearchMultiplier = 6f;

        /// <summary>
        /// How much narrower than the real capsule the overlap queries are.
        ///
        /// One skin width, and it has to be exactly that. Unity lets a controller sink into
        /// what it leans on by its skin width, so a query at the full radius would report every
        /// wall the player is touching as something they are buried in; a query narrower than
        /// that would miss a zero-thickness curtain the capsule has settled almost on top of,
        /// which is the case this whole apparatus exists for.
        ///
        /// The same figure answers both questions asked of it — "is the capsule buried in
        /// something" and "would the capsule fit here" — and that is deliberate. Using one
        /// number for both is what guarantees that a spot the search accepts cannot be reported
        /// as buried the next frame, so a rescue can never land somewhere that asks for another.
        /// </summary>
        private float CapsuleInset => _controller.skinWidth;

        private readonly RaycastHit[] _groundHits = new RaycastHit[8];
        private readonly Collider[] _overlaps = new Collider[16];
        private Vector3 _lastGroundedPosition;
        private bool _hasGroundedPosition;
        private Vector3 _lastFreePosition;
        private bool _hasFreePosition;
        private Vector3 _windowStart;
        private float _windowTimer;
        private float _embeddedTimer;
        private bool _reportedEmbedding;
        private bool _reportedNoGround;
        private bool _groundIsWater;
        private float _slideStallTimer;
        private int _waterMask;
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

            // Resolved by name rather than exposed as a mask, because a rescue that put the
            // player down in the lake would be undoing the wall that keeps them out of it, and
            // that is not a decision to leave to whoever last touched the Inspector. A project
            // without the layer simply gets no water test, which is the old behaviour.
            var water = LayerMask.NameToLayer(WaterLayerName);
            _waterMask = water >= 0 ? 1 << water : 0;

            _previousPosition = transform.position;
            _windowStart = transform.position;
        }

        private void Update()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            ProbeGround();
            // Before anything reads the position: a capsule inside a solid cannot move, so
            // every test below it would be measuring a player who is not where they should be.
            ResolveOverlap(dt);
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
            //
            // For as long as it is actually a slide. Taking control away is only fair while the
            // player is being carried somewhere: it says "you lost your footing", and losing
            // your footing ends, one way or another. Where it does not end is at the bottom,
            // against something — and the lake shore is exactly that shape, a face past the
            // climb limit with a wall at the foot of it. The slide runs into the wall, covers
            // no ground, and keeps discarding input every frame forever. The player is left
            // holding a direction on a shelf they could easily walk off, watching nothing
            // happen, which is the "못움직여" in the report and is not a slide by any reading.
            //
            // So the slide has to be producing motion to keep its claim on the controls. When
            // it stops producing any, the footing is not lost, it is simply steep here, and the
            // player gets to walk again.
            // Measured as a speed rather than as metres in a frame, so the test does not mean
            // something different at 30 fps than it does at 144. Half a metre a second is well
            // under anything a slide settles at and well over anything a jammed one produces:
            // the slide passes it a twenty-fifth of a second after it starts.
            var steep = IsGrounded && Vector3.Angle(_groundNormal, Vector3.up) > _slopeLimit;
            if (!steep || DistanceThisFrame > dt * SlideStallSpeed) _slideStallTimer = 0f;
            else _slideStallTimer += dt;

            if (steep && _slideStallTimer < _slideStallSeconds)
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
        ///
        /// Both the probe and the rise are measured off <see cref="CharacterController.center"/>
        /// rather than off the transform. The two are not the same on this rig — the capsule is
        /// centred 0.85 m up and the transform sits at the feet — and taking the transform for
        /// the middle of the capsule put the probe below the ground the player was standing on
        /// and read every rise as most of a metre taller than it is. Nothing ever qualified, so
        /// the only step the player had was <c>stepOffset</c>, which <c>Awake</c> deliberately
        /// clamps to a tenth of a metre on the understanding that this code does the rest. A
        /// ten-centimetre climb limit is enough on its own to make a shallow dip a pit.
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
                var feetY = FootHeight(transform.position);
                var probeOrigin = new Vector3(transform.position.x, feetY + _maxStepHeight + 0.05f, transform.position.z)
                                  + dir * _stepProbeDistance;

                if (Physics.Raycast(probeOrigin, Vector3.down, out var hit, _maxStepHeight + 0.15f, _groundMask, QueryTriggerInteraction.Ignore))
                {
                    var rise = hit.point.y - feetY;
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
        /// What makes it a hole is measured by asking, and that is the change. The test used
        /// to be "standing lower than the ground last walked freely", with the remembered
        /// ground allowed to descend five centimetres at a time so it would not follow the
        /// player down. Walking briskly down to the shore descends faster than that, so the
        /// anchor stayed up the hill, the player was measured against a point they had left
        /// on purpose, and after a second and a half they were hauled back up it — on
        /// perfectly ordinary ground, over and over. Height is simply not the question.
        ///
        /// Being unable to leave is. Two things have to hold: the player is on the ground, and
        /// they have covered almost nothing in the last <see cref="_stuckSeconds"/>. Only then
        /// is it worth the cost of looking, and the looking is what decides: standing on water,
        /// inside solid geometry, or no direction that leads out. Pushing into a wall reaches
        /// the looking and fails all three of those — the world behind you is open — so leaning
        /// on a fence stays exactly as legal as it was.
        ///
        /// It used to require the player to be asking to move, and that was the hole the last
        /// report fell through. Walking into the shoreline opens a prompt box, the box turns
        /// input off, and "is the player asking to move" is false for as long as the box is up
        /// — so the one moment the player is most likely to be stuck is the one moment this
        /// stopped watching. Standing still is not evidence of being fine, and the expensive
        /// part is behind a window that idling satisfies anyway. What the input check was
        /// really protecting is a scene that is moving the character itself, and
        /// <see cref="PlayerOwnsCharacter"/> says that directly.
        /// </summary>
        private void RecoverFromPit(float dt)
        {
            if (IsGrounded && Speed > 0.5f && Traversal != TraversalState.Water)
            {
                // Kept for one purpose: somewhere to put the player if the search below finds
                // nowhere at all. There is no height rule on it any more, because the height
                // rule existed to make this position safe to *compare against*, and nothing
                // compares against it now.
                _lastFreePosition = transform.position;
                _hasFreePosition = true;
            }

            if (!PlayerOwnsCharacter || !IsGrounded || Traversal == TraversalState.Water)
            {
                _windowTimer = 0f;
                _windowStart = transform.position;
                return;
            }

            if (_windowTimer <= 0f) _windowStart = transform.position;
            _windowTimer += dt;

            var travelled = transform.position - _windowStart;
            travelled.y = 0f;
            if (travelled.magnitude >= _stuckTravel)
            {
                // Going somewhere. Downhill, along the floor of a gully, or scraping sideways
                // along a fence all land here, and not one of them is a trap.
                _windowTimer = 0f;
                _windowStart = transform.position;
                _reportedNoGround = false;
                return;
            }

            if (_windowTimer < _stuckSeconds) return;
            _windowTimer = 0f;
            _windowStart = transform.position;

            // Standing on the lake is its own trap and does not look like any of the others.
            // Nothing is overlapping, the surface is flat and reads as perfectly good walkable
            // ground, and there may well be a way out in the sense of somewhere to put a foot —
            // it is simply that the player must not be there, and the wall that exists to keep
            // them out cannot help once they are already past it. A save written out over the
            // water reopens exactly there, so this cannot assume they walked in through a gap.
            var onWater = _groundIsWater && !SurfCapability.CanSurf();

            var embedded = OverlapAt(transform.position, CapsuleInset) > 0;
            if (!onWater && !embedded && HasWayOut()) return;

            var reason = onWater ? "standing on water with no way to cross it"
                : embedded ? "inside solid geometry"
                : "in a hole with nothing they could climb to";

            Vector3 destination;
            if (TryFindStandingPosition(transform.position, out var nearby))
            {
                destination = nearby;
            }
            else if (_hasFreePosition && OverlapAt(_lastFreePosition, CapsuleInset) == 0)
            {
                destination = _lastFreePosition;
            }
            else
            {
                // Once per episode of being stuck, not once every window: it is retried
                // regardless, because the wall that closed may yet open.
                if (_reportedNoGround) return;
                _reportedNoGround = true;
                Debug.LogWarning($"[Player] Stuck at {transform.position}, {reason}, and there " +
                                 "is no ground they could stand on within " +
                                 $"{_escapeSearchRadius * WideSearchMultiplier:F0} m of them. " +
                                 "Nothing was moved. Holding R still works; the level has a trap " +
                                 "here that is wider than the search.");
                return;
            }

            _reportedNoGround = false;

            Debug.LogWarning($"[Player] Stuck at {transform.position} for {_stuckSeconds:F1}s, " +
                             $"{reason}, and was lifted to {destination}. If this happens twice " +
                             "in the same place the level has a trap there.");
            Warp(destination, transform.rotation);
        }

        /// <summary>
        /// Pushes the capsule out of anything it is standing inside.
        ///
        /// A <see cref="CharacterController"/> has no depenetration pass of its own. It sweeps
        /// its capsule when asked to move and refuses the move when the sweep is blocked, so a
        /// capsule that is *already* inside a solid is not pushed out — it simply has nowhere
        /// it is allowed to go, in any direction, forever. That is reachable without the player
        /// doing anything wrong: the shoreline wall is toggled from a polled party check and
        /// can come up around them, a warp can land on a marker the generator has since grown a
        /// rock over, and the scatter never asks where the player is standing.
        ///
        /// <c>Physics.ComputePenetration</c> is the exact answer and covers the boxes, spheres,
        /// capsules and convex hulls the props are built from. It does not support a non-convex
        /// mesh collider, and the shoreline is exactly that: a double-sided curtain with no
        /// volume at all, which is also the shape that pins a capsule hardest, because every
        /// triangle has a twin facing the other way and the controller is held from both sides
        /// at once. So when the exact push cannot resolve it, the search takes over and puts
        /// the player on the nearest ground they could have walked to.
        /// </summary>
        private void ResolveOverlap(float dt)
        {
            var count = OverlapAt(transform.position, CapsuleInset);
            if (count == 0)
            {
                _embeddedTimer = 0f;
                _reportedEmbedding = false;
                return;
            }

            var push = Vector3.zero;
            for (var i = 0; i < count; i++)
            {
                var other = _overlaps[i];
                if (other == null) continue;
                // Asked only where it can answer. A non-convex mesh has no inside for a
                // penetration depth to be measured against, so PhysX declines, and the
                // shoreline curtain is the whole reason this method has a fallback at all.
                if (other is MeshCollider mesh && !mesh.convex) continue;
                if (!Physics.ComputePenetration(
                        _controller, transform.position, transform.rotation,
                        other, other.transform.position, other.transform.rotation,
                        out var direction, out var distance)) continue;
                push += direction * distance;
            }

            if (push.sqrMagnitude > 0.000001f)
            {
                // Clamped because the pushes are summed and two walls meeting at a corner will
                // each ask for the whole way out. A hair of overshoot on top, so the next
                // frame's query does not find the same contact and push a second time.
                var far = Vector3.ClampMagnitude(push, 1f);
                SetPositionImmediate(transform.position + far + far.normalized * 0.01f);
                if (OverlapAt(transform.position, CapsuleInset) == 0)
                {
                    _embeddedTimer = 0f;
                    _reportedEmbedding = false;
                    return;
                }
            }

            _embeddedTimer += dt;
            if (_embeddedTimer < _embeddedSeconds) return;
            _embeddedTimer = 0f;

            // The exact push above runs whatever is happening, because it is small and being
            // inside a wall is never right. Moving the player somewhere else is a bigger claim,
            // and during a cutscene the character is where a shot wants it — so that half waits
            // until the player owns the character again, which includes while a box is open.
            if (!PlayerOwnsCharacter) return;

            if (!TryFindStandingPosition(transform.position, out var clear))
            {
                // Said once and then kept quiet, but still retried: the search runs again every
                // few tenths of a second because the thing that closed around the player may be
                // about to open, and a console filling up helps nobody read the frame it did.
                if (_reportedEmbedding) return;
                _reportedEmbedding = true;
                Debug.LogWarning($"[Player] Inside solid geometry at {transform.position} and " +
                                 $"nothing within {_escapeSearchRadius:F1} m is clear enough to " +
                                 "stand on. Holding R is the way out of this one.");
                return;
            }

            Debug.LogWarning($"[Player] Was inside solid geometry at {transform.position} — " +
                             "which a CharacterController cannot leave on its own — and was " +
                             $"moved to {clear}.");
            Warp(clear, transform.rotation);
        }

        /// <summary>
        /// True when there is at least one direction the player could actually walk out of.
        ///
        /// This is the whole difference between a hole and a wall. A wall leaves seven of the
        /// eight directions open; a hole answers no in all of them, because that is what a hole
        /// is. Clear air alone is not enough — the far side of a crevice is clear too — so each
        /// direction also has to end somewhere the player could stand.
        ///
        /// And somewhere they could stand is not the same as somewhere they could *get to*,
        /// which is the correction. The first version of this asked only whether there was
        /// walkable ground a stride away, and accepted it anywhere within two and a half metres
        /// of vertical — so the bank of the lake, a metre up a face far past the climb limit,
        /// counted as a way out. The player stood on a ledge at the waterline with the basin
        /// dropping away on one side and that bank on the other three, and this function
        /// cheerfully reported four ways out of a place with none. Ground you cannot climb to
        /// is scenery, not an exit.
        /// </summary>
        private bool HasWayOut()
        {
            const int Directions = 8;
            for (var i = 0; i < Directions; i++)
            {
                var heading = Quaternion.Euler(0f, i * (360f / Directions), 0f) * Vector3.forward;
                if (CanWalkOut(heading)) return true;
            }
            return false;
        }

        private bool CanWalkOut(Vector3 heading)
        {
            // Swept from the height a step-up would carry the capsule to, so a kerb the step
            // assist can climb is not mistaken for a wall.
            GetCapsule(transform.position + Vector3.up * (_maxStepHeight + 0.02f),
                       CapsuleInset, out var bottom, out var top, out var radius);

            if (Physics.CapsuleCast(bottom, top, radius, heading, out var blocked, _escapeProbeDistance,
                    _groundMask, QueryTriggerInteraction.Ignore)
                && blocked.distance < _escapeProbeDistance)
                return false;

            if (!TryStandAt(transform.position + heading * _escapeProbeDistance, out var landing))
                return false;

            // Up only as far as the step assist can lift, because that is the entire climb this
            // game has — there is no jump. Down is allowed much further: stepping off a bank is
            // always available and always works, and being strict about it would call the top of
            // every slope a trap.
            var rise = landing.y - FootHeight(transform.position);
            return rise <= _maxStepHeight && rise >= -EscapeDropAllowance;
        }

        /// <summary>
        /// The nearest place around <paramref name="around"/> the player could legally be
        /// standing, searched outward in rings.
        ///
        /// Nearest rather than safest, on purpose. Being moved somewhere you did not walk is
        /// what reads as the game glitching, so a rescue that sets you on the lip of the hole
        /// you were in costs the player a step, while one that returns you to the top of the
        /// hill undoes a minute of walking and will happen again the moment you walk back down.
        /// </summary>
        private bool TryFindStandingPosition(Vector3 around, out Vector3 result)
        {
            // Near first, and only then far. Two sweeps rather than one wide one because the
            // near sweep is what almost every rescue needs and it must stay cheap, while the
            // far sweep exists for the case where the player is standing somewhere with no dry
            // land for twenty metres — out on the lake — and a rescue that gives up at three
            // and a half would leave them there.
            if (SweepForStandingPosition(around, _escapeSearchRadius, 0.7f, out result)) return true;
            return SweepForStandingPosition(around, _escapeSearchRadius * WideSearchMultiplier,
                1.6f, out result);
        }

        private bool SweepForStandingPosition(Vector3 around, float radius, float ringSpacing, out Vector3 result)
        {
            const int Spokes = 12;

            result = around;
            var rings = Mathf.Max(1, Mathf.CeilToInt(radius / ringSpacing));

            for (var ring = 0; ring <= rings; ring++)
            {
                var reach = ring * (radius / rings);
                var spokes = ring == 0 ? 1 : Spokes;
                // Rotated half a spoke further each ring, so successive rings do not line up
                // and leave the same wedge of the world unsampled all the way out.
                var bias = ring * (180f / Spokes);

                var bestDistance = float.MaxValue;
                var found = false;

                for (var i = 0; i < spokes; i++)
                {
                    var heading = Quaternion.Euler(0f, bias + i * (360f / spokes), 0f) * Vector3.forward;
                    if (!TryStandAt(around + heading * reach, out var candidate)) continue;

                    // Within a ring, the closest in three dimensions: two spots the same
                    // distance away across the ground are not the same rescue if one of them
                    // is two metres up a bank.
                    var offset = (candidate - around).sqrMagnitude;
                    if (offset >= bestDistance) continue;
                    bestDistance = offset;
                    result = candidate;
                    found = true;
                }

                if (found) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the column through <paramref name="column"/> has ground in it the player
        /// could stand on, and where.
        ///
        /// Four tests, and all four are needed: something is under it, that something is
        /// walkable rather than a cliff face, it is not the water, and the capsule fits there
        /// without being inside anything. The last is the one the old recovery skipped, which
        /// is how it could answer being wedged in the shoreline by putting the player back
        /// inside it every time.
        /// </summary>
        private bool TryStandAt(Vector3 column, out Vector3 standing)
        {
            // Up far enough to find the rim of a hole the player is at the bottom of, down
            // only a little, because a rescue that drops the player a long way is a fall.
            const float Lift = 2.5f;
            const float Drop = 1.2f;

            standing = column;

            var from = column + Vector3.up * Lift;
            if (!Physics.Raycast(from, Vector3.down, out var hit, Lift + Drop, _groundMask,
                    QueryTriggerInteraction.Ignore)) return false;
            if (Vector3.Angle(hit.normal, Vector3.up) > _slopeLimit) return false;

            var candidate = hit.point + Vector3.up * 0.02f;

            // Never into the water. A shoreline wall exists so that a step is not taken at all,
            // and a rescue that answered being wedged in one by setting the player down on the
            // wet side of it would be undoing the wall to get past the wall. Asked twice
            // because the water surfaces are solid colliders today and may be triggers
            // tomorrow: the first test catches standing on the surface, the second catches
            // standing under it on the bed once a ray can pass through.
            if (_waterMask != 0 && Traversal != TraversalState.Water)
            {
                if ((_waterMask & (1 << hit.collider.gameObject.layer)) != 0) return false;
                if (Physics.Raycast(candidate, Vector3.up, WaterHeadroom, _waterMask,
                        QueryTriggerInteraction.Collide)) return false;
            }

            if (OverlapAt(candidate, CapsuleInset) > 0) return false;

            standing = candidate;
            return true;
        }

        /// <summary>
        /// Colliders the capsule is inside if it stands at <paramref name="footPosition"/>,
        /// left in <see cref="_overlaps"/> and counted. See <see cref="CapsuleInset"/> for why
        /// every caller passes that and nothing else.
        /// </summary>
        private int OverlapAt(Vector3 footPosition, float inset)
        {
            GetCapsule(footPosition, inset, out var bottom, out var top, out var radius);
            var found = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, _overlaps,
                _groundMask, QueryTriggerInteraction.Ignore);

            var kept = 0;
            for (var i = 0; i < found; i++)
            {
                var candidate = _overlaps[i];
                if (candidate == null) continue;
                // The player's own controller is a collider on this very object, and every
                // query finds it first.
                if (candidate.transform.IsChildOf(transform)) continue;
                _overlaps[kept++] = candidate;
            }
            return kept;
        }

        /// <summary>
        /// The controller's capsule in world space, as the two sphere centres and a radius.
        /// Taken from <see cref="CharacterController.center"/> because the transform is at the
        /// player's feet and the capsule is not.
        /// </summary>
        private void GetCapsule(Vector3 footPosition, float inset, out Vector3 bottom, out Vector3 top, out float radius)
        {
            radius = Mathf.Max(0.02f, _controller.radius - inset);
            var centre = footPosition + transform.rotation * _controller.center;
            var spine = Mathf.Max(0f, _controller.height * 0.5f - _controller.radius);
            bottom = centre - Vector3.up * spine;
            top = centre + Vector3.up * spine;
        }

        /// <summary>World Y of the sole of the capsule when it stands at the given position.</summary>
        private float FootHeight(Vector3 footPosition)
        {
            GetCapsule(footPosition, 0f, out var bottom, out _, out var radius);
            return bottom.y - radius;
        }

        /// <summary>
        /// Moves the capsule without touching velocity, and republishes it to the physics scene.
        ///
        /// The controller has to be disabled around the write or its internal cache reasserts
        /// the old position on the next Move, and the project runs with automatic transform
        /// syncing off, so a query made later in the same frame would otherwise still be asking
        /// about where the player used to be.
        /// </summary>
        private void SetPositionImmediate(Vector3 position)
        {
            var wasEnabled = _controller.enabled;
            _controller.enabled = false;
            transform.position = position;
            _controller.enabled = wasEnabled;
            Physics.SyncTransforms();

            // Not distance the player walked, so the stride and the encounter pressure it
            // feeds must not see it.
            _previousPosition = position;
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
            var onWater = false;
            for (var i = 0; i < count; i++)
            {
                var candidate = _groundHits[i];
                if (candidate.collider == null) continue;
                if (candidate.collider.transform.root == transform) continue;
                if (candidate.distance <= 0.0001f) continue;
                if (candidate.distance >= nearest) continue;
                nearest = candidate.distance;
                normal = candidate.normal;
                // Noted rather than filtered out. Dropping the water from the mask would let
                // the player fall through the lake into the basin under it, which is a worse
                // answer than standing on it; what is wanted is to know that the thing holding
                // them up is water, so the recovery can treat it as a place to be got out of.
                onWater = _waterMask != 0 && (_waterMask & (1 << candidate.collider.gameObject.layer)) != 0;
            }

            if (!float.IsPositiveInfinity(nearest))
            {
                _groundNormal = normal;
                _groundIsWater = onWater;
                _timeSinceGrounded = 0f;
            }
            else if (_controller.isGrounded)
            {
                // Fall back to the controller flag: a sphere cast misses thin ledges the capsule
                // is genuinely resting on.
                _groundNormal = Vector3.up;
                _groundIsWater = false;
                _timeSinceGrounded = 0f;
            }
            else
            {
                _groundNormal = Vector3.up;
                _groundIsWater = false;
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
        /// Whether the player is the one driving the character right now.
        ///
        /// Deliberately not <see cref="OverworldInputReader.InputEnabled"/>. That flag is
        /// cleared by anything at all that wants the character to hold still, and a one-line
        /// prompt box is one of those things — walking into the shoreline opens a box, the box
        /// clears the flag, and every safety net that asked "is the player trying to move"
        /// silently switched itself off at exactly the moment the player was standing against
        /// a wall wondering why they could not move. Being unable to get out of a hole is not
        /// less true because there is a box on the screen.
        ///
        /// A cutscene, a battle, an encounter intro or a scene transition is the opposite case
        /// and the one the flag was really guarding: something else is moving the character,
        /// and a rescue in the middle of one strands the scene rather than the player. The
        /// mode says which of the two is happening, and unlike the flag it is not a shared
        /// switch that four unrelated systems write to.
        ///
        /// No flow service means no reason to think the player is not in control, and locking
        /// the rescues out on a missing service is the failure this whole file exists to stop.
        /// </summary>
        public static bool PlayerOwnsCharacter
        {
            get
            {
                if (!ServiceHub.TryGet<IGameFlow>(out var flow) || flow == null) return true;
                switch (flow.Mode)
                {
                    case GameMode.Exploring:
                    case GameMode.Dialogue:
                    case GameMode.Menu:
                        return true;
                    default:
                        return false;
                }
            }
        }

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
        /// The nearest position around <paramref name="desired"/> the capsule can legally stand,
        /// or <paramref name="desired"/> unchanged when it is already clear.
        ///
        /// False means nothing within the search radius works, and the honest response to that
        /// is to leave the player where they are: moving them anyway only swaps one trap for
        /// another, and at least the one they are in is the one they can see.
        /// </summary>
        public bool TryResolveStandingPosition(Vector3 desired, out Vector3 resolved)
        {
            resolved = desired;
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_controller == null) return true;
            if (OverlapAt(desired, CapsuleInset) == 0) return true;
            return TryFindStandingPosition(desired, out resolved);
        }

        /// <summary>
        /// Repositions the player exactly. The controller has to be disabled around the write or
        /// its internal transform cache reasserts the old position on the next Move.
        ///
        /// "Exactly" stops at the edge of solid geometry. Every caller that places the player —
        /// an arrival marker, a battle return, a cutscene mark, the water turning them back —
        /// is stating where they should be, not asserting that a capsule fits there, and a
        /// capsule that does not fit is a capsule that cannot move again. Checking it here
        /// rather than in each of them means none of them has to know that.
        /// </summary>
        public void Warp(Vector3 position, Quaternion rotation, bool preserveVelocity = false)
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();

            if (_controller != null && OverlapAt(position, CapsuleInset) > 0)
            {
                if (TryFindStandingPosition(position, out var clear))
                {
                    Debug.LogWarning($"[Player] Asked to be placed at {position}, which is " +
                                     $"inside something solid, and was put at {clear} instead.");
                    position = clear;
                }
                else
                {
                    Debug.LogWarning($"[Player] Placed at {position}, which is inside something " +
                                     "solid, with nowhere clear nearby to put them instead. " +
                                     "Holding R will move them to a spawn marker.");
                }
            }

            var wasEnabled = _controller.enabled;
            _controller.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            _controller.enabled = wasEnabled;
            // Queries later in the frame — the ground probe, the next overlap test — read the
            // physics scene, and this project has automatic transform syncing switched off.
            Physics.SyncTransforms();

            // The player has just been put somewhere; whatever they were failing to do at the
            // old position is no longer being asked about, and leaving the clocks running would
            // let a rescue land and immediately count towards the next one.
            _windowTimer = 0f;
            _windowStart = position;
            _embeddedTimer = 0f;

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
