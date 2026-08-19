using UnityEngine;
using Unity.Cinemachine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Drives the exploration follow camera.
    ///
    /// Cinemachine owns the actual framing and damping; this component only feeds it input and
    /// solves occlusion. Occlusion is solved by pulling in the orbital *radius* rather than by
    /// bolting on a deoccluder, for two reasons: the radius axis is already damped by Cinemachine
    /// so the pull-in inherits the rig's smoothing, and it never rotates the camera, which is what
    /// makes deoccluders feel like they are wrestling you for control near walls.
    ///
    /// This is exploration framing only. Encounter and battle cameras belong to the cinematics
    /// worker and are blended to through <see cref="IGameFlow"/>, never cut to from here.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldCameraRig : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private OverworldInputReader _input;
        [Tooltip("The exploration CinemachineCamera. Needs a CinemachineOrbitalFollow to be steerable.")]
        [SerializeField] private CinemachineCamera _camera;
        [Tooltip("Transform the camera follows and looks at — a chest/head-height child of the player.")]
        [SerializeField] private Transform _followTarget;

        [Header("Orbit limits")]
        [SerializeField] private float _minPitch = -12f;
        [SerializeField] private float _maxPitch = 62f;

        [Header("HD-2D framing")]
        [Tooltip("Hold the world at one angle instead of letting the player orbit. Off: the " +
                 "billboards carry three drawn views and mirror the sides, so they answer a " +
                 "moving camera correctly — walk away from it and you see the character's " +
                 "back, which is what makes free look affordable here at all.")]
        [SerializeField] private bool _lockYaw;
        [Tooltip("The single yaw the world is viewed from. Battle uses its own layout yaw; this is exploration's.")]
        [Range(0f, 360f)]
        [SerializeField] private float _fixedYaw = 45f;
        [Tooltip("Downward angle, and it is a narrow band. Too shallow and the sky takes " +
                 "half the frame; 55 was tried and put the camera on the rooftops, with the " +
                 "signboards edge-on and the sprites seen from above the angle they were " +
                 "drawn at. This shows building fronts and the road ahead, which is what a " +
                 "Sinnoh town is composed to be read from.")]
        [Range(-12f, 70f)]
        [SerializeField] private float _fixedPitch = 8f;
        [Tooltip("Degrees per second the yaw eases back after a cutscene has borrowed the camera.")]
        [SerializeField] private float _yawReturnRate = 120f;
        [Tooltip("Distance the rig sits at with a clear line of sight.")]
        [SerializeField] private float _restDistance = 8.5f;
        [SerializeField] private float _minDistance = 1.4f;
        [SerializeField] private float _maxDistance = 14f;

        [Header("Occlusion")]
        [Tooltip("Off by default, and that is the design rather than a compromise. A spring " +
                 "arm that shortens behind walls is a third-person convention; a fixed " +
                 "overhead camera is what makes a Pokémon town legible, because the distance " +
                 "the world is read at never changes. With it on, walking past a building " +
                 "swung the boom between 1.4 m and 16 m and the framing lurched with it.")]
        [SerializeField] private bool _avoidObstacles;
        [SerializeField] private LayerMask _occluderMask = ~0;
        [Tooltip("Cast radius. Slightly larger than the near plane corner so the camera never clips a wall.")]
        [SerializeField] private float _probeRadius = 0.32f;
        [Tooltip("Seconds to pull in when something blocks the view. Fast — a late pull-in shows geometry.")]
        [SerializeField] private float _pullInTime = 0.05f;
        [Tooltip("Seconds to ease back out once clear. Slow, so brushing a tree does not yo-yo the camera.")]
        [SerializeField] private float _pushOutTime = 0.55f;

        [Header("Ground clamp")]
        [Tooltip("Stop the camera from tilting down through the hill behind the player. This " +
                 "is not the spring arm coming back: the boom never changes length, so the " +
                 "world is always read from the same distance. Only how far down you may " +
                 "look is limited, and only where the ground is actually in the way.")]
        [SerializeField] private bool _clampToGround = true;

        [Tooltip("Metres of air kept under the camera.")]
        [SerializeField] private float _groundClearance = 0.6f;

        [SerializeField] private LayerMask _groundClampMask = ~0;

        [Header("Mouse look")]
        [Tooltip("Capture the cursor so the mouse turns the camera instead of sliding a " +
                 "pointer off the window. Escape releases it; the editor does that for you.")]
        [SerializeField] private bool _captureCursor = true;

        [Header("Auto-frame")]
        [Tooltip("Degrees per second the yaw drifts behind the player when there is no look input.")]
        [SerializeField] private float _autoFollowRate = 70f;
        [Tooltip("Seconds of no look input before auto-follow engages.")]
        [SerializeField] private float _autoFollowDelay = 1.6f;
        [SerializeField] private PlayerLocomotion _locomotion;

        private readonly RaycastHit[] _hits = new RaycastHit[8];
        private CinemachineOrbitalFollow _orbital;
        private float _currentDistance;
        private float _distanceVelocity;
        private float _idleLookTimer;

        /// <summary>Flat world-space direction the camera is looking, for camera-relative movement.</summary>
        public Vector3 PlanarForward
        {
            get
            {
                var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                return forward.sqrMagnitude < 0.0001f ? Vector3.forward : forward.normalized;
            }
        }

        /// <summary>Suspends player steering, e.g. while a trainer approach cutscene plays.</summary>
        public bool ControlEnabled { get; set; } = true;

        /// <summary>
        /// The chest-height point on the player the camera frames.
        ///
        /// Exposed for <see cref="CameraOccluderFade"/>, which needs the far end of the
        /// boom rather than the rig. This component lives on the CinemachineCamera, so
        /// its own transform is where the camera ends up — reading it instead gives a
        /// probe that starts and ends at the same point.
        /// </summary>
        public Transform FollowTarget => _followTarget;

        private void Awake()
        {
            if (_camera == null) _camera = GetComponent<CinemachineCamera>();
            if (_camera != null)
            {
                _orbital = _camera.GetComponent<CinemachineOrbitalFollow>();
                if (_followTarget != null)
                {
                    _camera.Follow = _followTarget;
                    _camera.LookAt = _followTarget;
                }
            }

            _currentDistance = _restDistance;
            ApplyDistance(_restDistance);
            ApplyStartingPitch();
        }

        /// <summary>
        /// Puts the camera at its resting height before the first frame is drawn.
        ///
        /// With the yaw unlocked, <see cref="ApplyLookInput"/> no longer writes the pitch
        /// every frame — it is the player's to move — so whatever the axis was serialized
        /// with is where the game opens. That was 17.5 degrees from a Cinemachine default
        /// nobody chose, and it is the first shot of the game.
        /// </summary>
        private void ApplyStartingPitch()
        {
            if (_orbital == null) return;
            var vertical = _orbital.VerticalAxis;
            vertical.Range = new Vector2(_minPitch, _maxPitch);
            vertical.Center = Mathf.Clamp(_fixedPitch, _minPitch, _maxPitch);
            vertical.Value = vertical.Center;
            _orbital.VerticalAxis = vertical;
        }

        /// <summary>
        /// Holds the cursor only while the player is actually steering.
        ///
        /// Mouse look needs a captured cursor; a menu needs a real one. Capturing it once at
        /// startup and leaving it captured meant the dialogue box's choice buttons could not
        /// be clicked at all — the pointer was locked to the centre of the screen and
        /// invisible, so the boy/girl question in the prologue had no answerable answer and
        /// the opening waited on a choice the player had no way to make.
        ///
        /// Tied to InputEnabled rather than to a menu flag, because that is already the one
        /// thing every system sets when it takes control: dialogue, transitions, episodes and
        /// battles all go through it.
        /// </summary>
        private void UpdateCursorCapture()
        {
            if (!_captureCursor || _lockYaw) return;

            var steering = ControlEnabled && (_input == null || _input.InputEnabled);
            if (steering == (Cursor.lockState == CursorLockMode.Locked)) return;
            SetCursorCaptured(steering);
        }

        private static void SetCursorCaptured(bool captured)
        {
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
        }

        private void LateUpdate()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            UpdateCursorCapture();
            ApplyLookInput(dt);
            if (_clampToGround) ClampPitchToGround();
            if (_avoidObstacles) SolveOcclusion(dt);
        }

        private void ApplyLookInput(float dt)
        {
            if (_orbital == null) return;

            // HD-2D holds the world at one angle. The camera still follows the player, but it
            // never orbits, because the characters are billboarded sprites with a small set of
            // drawn views: swinging the yaw would change which view is shown without the
            // character having turned, and would keep redefining which way "north" is. So the
            // yaw is a constant here and steering is disabled outright rather than damped.
            if (_lockYaw)
            {
                var locked = _orbital.HorizontalAxis;
                if (!Mathf.Approximately(locked.Value, _fixedYaw))
                {
                    // Ease rather than snap, so a scripted camera move that borrowed the yaw
                    // hands it back without a visible jump.
                    locked.Value = Mathf.MoveTowardsAngle(locked.Value, _fixedYaw, _yawReturnRate * dt);
                    _orbital.HorizontalAxis = locked;
                }

                var pitchLocked = _orbital.VerticalAxis;
                pitchLocked.Range = new Vector2(_minPitch, _maxPitch);
                pitchLocked.Value = Mathf.Clamp(_fixedPitch, _minPitch, _maxPitch);
                _orbital.VerticalAxis = pitchLocked;
                return;
            }

            var look = (ControlEnabled && _input != null && _input.InputEnabled) ? _input.Look : Vector2.zero;

            if (look.sqrMagnitude > 0.000001f)
            {
                _idleLookTimer = 0f;
                // The stick cancels a scripted turn outright: two writers easing the same
                // axis in different directions is the camera fighting the player.
                _lookTowardEasing = false;

                var horizontal = _orbital.HorizontalAxis;
                horizontal.Value += look.x;
                _orbital.HorizontalAxis = horizontal;

                var vertical = _orbital.VerticalAxis;
                vertical.Range = new Vector2(_minPitch, _maxPitch);
                vertical.Value = Mathf.Clamp(vertical.Value - look.y, _minPitch, _maxPitch);
                _orbital.VerticalAxis = vertical;
            }
            else
            {
                _idleLookTimer += dt;
                ApplyLookToward(dt);
                if (!_lookTowardEasing) AutoFollow(dt);
            }
        }

        /// <summary>
        /// Raises the lowest pitch the player may look from until the camera clears the
        /// ground beneath it.
        ///
        /// With the boom fixed and the yaw free, tilting down swings the camera in an arc
        /// that goes underground the moment the player is standing above anything — which
        /// on a map with a gorge and a plateau is most of it. The view then shows the
        /// underside of the terrain, which is unlit black or, worse, backface-culled into
        /// the skybox.
        ///
        /// Solved by moving the limit rather than the camera. Pushing the camera up after
        /// Cinemachine has placed it fights the rig for the transform and stutters; raising
        /// the axis floor means the player simply cannot look past the hill, and the input
        /// they are giving keeps mapping onto the view they get.
        /// </summary>
        private void ClampPitchToGround()
        {
            if (_orbital == null || _followTarget == null) return;

            var vertical = _orbital.VerticalAxis;
            var boom = Mathf.Max(0.01f, _orbital.Radius * Mathf.Max(0.01f, _orbital.RadialAxis.Value));
            var pivot = _followTarget.position;

            // Where the boom would put the camera at the current yaw, projected onto the
            // ground plane. Sampling under the pivot instead would clamp against the ground
            // the player is standing on, which is never the ground in the way.
            var yaw = _orbital.HorizontalAxis.Value * Mathf.Deg2Rad;
            var back = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
            var probe = pivot + back * (boom * Mathf.Cos(vertical.Value * Mathf.Deg2Rad));

            var from = probe + Vector3.up * (boom + 2f);
            if (!Physics.Raycast(from, Vector3.down, out var hit, boom * 2f + 4f,
                    _groundClampMask, QueryTriggerInteraction.Ignore))
            {
                vertical.Range = new Vector2(_minPitch, _maxPitch);
                _orbital.VerticalAxis = vertical;
                return;
            }

            // The pitch at which the boom's tip sits exactly the clearance above that ground.
            var needed = (hit.point.y + _groundClearance) - pivot.y;
            var floor = Mathf.Asin(Mathf.Clamp(needed / boom, -1f, 1f)) * Mathf.Rad2Deg;
            floor = Mathf.Clamp(floor, _minPitch, _maxPitch);

            vertical.Range = new Vector2(floor, _maxPitch);
            vertical.Value = Mathf.Max(vertical.Value, floor);
            _orbital.VerticalAxis = vertical;
        }

        /// <summary>
        /// Eases the yaw behind the player when they have not touched the stick for a while. Rate
        /// scales with speed so it is invisible at a walk and helpful at a run — a constant rate
        /// reads as the camera fighting the player.
        /// </summary>
        private void AutoFollow(float dt)
        {
            if (_orbital == null || _locomotion == null) return;
            if (_idleLookTimer < _autoFollowDelay) return;
            if (_locomotion.Speed < 0.5f) return;

            var horizontal = _orbital.HorizontalAxis;
            var desiredYaw = _locomotion.transform.eulerAngles.y;
            var delta = Mathf.DeltaAngle(horizontal.Value, desiredYaw);
            var speedFactor = Mathf.Clamp01(_locomotion.NormalisedSpeed);
            var step = _autoFollowRate * speedFactor * dt;
            horizontal.Value += Mathf.Clamp(delta, -step, step);
            _orbital.HorizontalAxis = horizontal;
        }

        /// <summary>
        /// Sphere-casts from the follow target out along the camera's boom and clamps the orbit
        /// radius to the first blocker. Asymmetric damping — snap in, ease out — is what stops the
        /// camera oscillating when the player skims a wall.
        /// </summary>
        private void SolveOcclusion(float dt)
        {
            if (_followTarget == null) return;

            var target = _restDistance;
            var origin = _followTarget.position;
            var back = -transform.forward;

            // The cast starts at chest height, which is inside the player's own capsule.
            // A plain SphereCast reports that capsule at distance 0 and the boom collapses
            // to the minimum every frame, so the nearest hit that is not the player is the
            // one that matters. Masking by layer alone is not enough while the player and
            // the world can share a layer.
            var count = Physics.SphereCastNonAlloc(origin, _probeRadius, back, _hits,
                _restDistance, _occluderMask, QueryTriggerInteraction.Ignore);
            var nearest = float.PositiveInfinity;
            var self = _followTarget.root;
            for (var i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider == null || hit.collider.transform.root == self) continue;
                // distance 0 means the cast began already overlapping this collider, which
                // says nothing about where its surface is.
                if (hit.distance <= 0.0001f) continue;
                if (hit.distance < nearest) nearest = hit.distance;
            }
            if (!float.IsPositiveInfinity(nearest))
                target = Mathf.Max(_minDistance, nearest - _probeRadius * 0.5f);

            var smoothing = target < _currentDistance ? _pullInTime : _pushOutTime;
            _currentDistance = Mathf.SmoothDamp(_currentDistance, target, ref _distanceVelocity, smoothing, Mathf.Infinity, dt);
            ApplyDistance(_currentDistance);
        }

        /// <summary>
        /// Sets the boom length in metres.
        ///
        /// <c>RadialAxis</c> is not a distance. Cinemachine calls it "Orbit Scale" and uses
        /// it as a multiplier: in sphere mode the camera sits at
        /// <c>rotation * (0, 0, -Radius * RadialAxis.Value)</c>. Writing metres into it
        /// therefore squares the boom — a 5.5 m rest distance against a 5.5 m radius put
        /// the camera 30 m away, which is why exploration opened on a map view of the town
        /// rather than behind the player, and why walking looked like nothing was moving.
        /// </summary>
        private void ApplyDistance(float distance)
        {
            if (_orbital == null) return;
            var boom = Mathf.Max(0.01f, _orbital.Radius);
            var radial = _orbital.RadialAxis;
            radial.Range = new Vector2(_minDistance / boom, _maxDistance / boom);
            radial.Value = Mathf.Clamp(distance, _minDistance, _maxDistance) / boom;
            _orbital.RadialAxis = radial;
        }

        /// <summary>
        /// Turns the rig toward a world position over the next frames, used when a trainer or a
        /// story trigger wants the player's attention. Only the yaw target is set — pitch,
        /// distance and the camera's current pose are left alone — and the turn is eased from
        /// wherever the player happened to leave the camera. Writing the axis directly was a
        /// one-frame teleport to the far side of the player, which is exactly the jump this
        /// game's camera must never make.
        /// </summary>
        public void LookToward(Vector3 worldPoint)
        {
            if (_orbital == null || _followTarget == null) return;
            var direction = Vector3.ProjectOnPlane(worldPoint - _followTarget.position, Vector3.up);
            if (direction.sqrMagnitude < 0.0001f) return;

            _lookTowardYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            _lookTowardEasing = true;
            _idleLookTimer = 0f;
        }

        [Tooltip("Degrees per second a LookToward turn moves the yaw. Fast enough to arrive " +
                 "before a speaker's first line lands, slow enough to read as a turn.")]
        [SerializeField] private float _lookTowardRate = 150f;

        private float _lookTowardYaw;
        private bool _lookTowardEasing;

        /// <summary>Eases a pending LookToward. The player's own stick always wins mid-turn.</summary>
        private void ApplyLookToward(float dt)
        {
            if (!_lookTowardEasing || _orbital == null) return;

            var horizontal = _orbital.HorizontalAxis;
            horizontal.Value = Mathf.MoveTowardsAngle(horizontal.Value, _lookTowardYaw, _lookTowardRate * dt);
            _orbital.HorizontalAxis = horizontal;
            if (Mathf.Abs(Mathf.DeltaAngle(horizontal.Value, _lookTowardYaw)) < 0.5f)
                _lookTowardEasing = false;
        }

        /// <summary>Overrides the rest distance, e.g. tightening in a cave corridor.</summary>
        public void SetRestDistance(float distance) =>
            _restDistance = Mathf.Clamp(distance, _minDistance, _maxDistance);
    }
}
