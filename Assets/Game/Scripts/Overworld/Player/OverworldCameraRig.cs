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
        [SerializeField] private float _minPitch = -25f;
        [SerializeField] private float _maxPitch = 60f;

        [Header("HD-2D framing")]
        [Tooltip("Hold the world at one angle. The camera still follows the player; it just never " +
                 "orbits. Required while characters are billboarded sprites — a yaw change swaps " +
                 "which drawn view is shown without the character having turned.")]
        [SerializeField] private bool _lockYaw = true;
        [Tooltip("The single yaw the world is viewed from. Battle uses its own layout yaw; this is exploration's.")]
        [Range(0f, 360f)]
        [SerializeField] private float _fixedYaw = 45f;
        [Tooltip("Downward angle. Shallower than a true isometric so the sprites keep their height on screen.")]
        [Range(10f, 70f)]
        [SerializeField] private float _fixedPitch = 38f;
        [Tooltip("Degrees per second the yaw eases back after a cutscene has borrowed the camera.")]
        [SerializeField] private float _yawReturnRate = 120f;
        [Tooltip("Distance the rig sits at with a clear line of sight.")]
        [SerializeField] private float _restDistance = 5.5f;
        [SerializeField] private float _minDistance = 1.4f;
        [SerializeField] private float _maxDistance = 9f;

        [Header("Occlusion")]
        [SerializeField] private bool _avoidObstacles = true;
        [SerializeField] private LayerMask _occluderMask = ~0;
        [Tooltip("Cast radius. Slightly larger than the near plane corner so the camera never clips a wall.")]
        [SerializeField] private float _probeRadius = 0.32f;
        [Tooltip("Seconds to pull in when something blocks the view. Fast — a late pull-in shows geometry.")]
        [SerializeField] private float _pullInTime = 0.05f;
        [Tooltip("Seconds to ease back out once clear. Slow, so brushing a tree does not yo-yo the camera.")]
        [SerializeField] private float _pushOutTime = 0.55f;

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
        }

        private void LateUpdate()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            ApplyLookInput(dt);
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
                AutoFollow(dt);
            }
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
        /// Points the rig at a world position over the next frames, used when a trainer spots the
        /// player. Sets the yaw target; the orbital damping does the easing so it is never a cut.
        /// </summary>
        public void LookToward(Vector3 worldPoint)
        {
            if (_orbital == null || _followTarget == null) return;
            var direction = Vector3.ProjectOnPlane(worldPoint - _followTarget.position, Vector3.up);
            if (direction.sqrMagnitude < 0.0001f) return;

            var horizontal = _orbital.HorizontalAxis;
            horizontal.Value = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            _orbital.HorizontalAxis = horizontal;
            _idleLookTimer = 0f;
        }

        /// <summary>Overrides the rest distance, e.g. tightening in a cave corridor.</summary>
        public void SetRestDistance(float distance) =>
            _restDistance = Mathf.Clamp(distance, _minDistance, _maxDistance);
    }
}
