using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using PokeLab.Core;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The battle camera set: one <see cref="CinemachineCamera"/> per <see cref="BattleShot"/>,
    /// selected by priority and joined by the authored blend table in
    /// <see cref="ShotBlendRule"/>.
    ///
    /// After the HD-2D pivot this is a <b>traditional Pokémon rig</b>, and the structure
    /// changed to enforce that rather than merely to allow it:
    ///
    /// <list type="number">
    /// <item><b>There is one camera placement, not one per shot.</b> <see cref="SolveBasis"/>
    /// computes a single position and view direction from the stage axis, and every shot uses
    /// it. A shot may pan its aim a few degrees and dolly a few centimetres; it cannot choose
    /// a different anchor, and so it cannot orbit. That is what makes the fixed three-quarter
    /// layout structural instead of a convention someone has to remember.</item>
    /// <item><b>Apparent size is one number per shot and it is clamped.</b> The lens is solved
    /// against a fixed reference distance, so on-screen creature size is exactly proportional
    /// to <see cref="ShotProfile.StageScreenFraction"/>, which is clamped to a 1.92× band. The
    /// old rig varied subject size 12-16× and the only way to find that out was to measure a
    /// screenshot.</item>
    /// <item><b>Nothing may move the camera to an unauthored angle.</b> The deoccluder's
    /// obstacle avoidance is disabled on every shot. It stayed enabled in the old rig because
    /// an over-the-shoulder framing genuinely needed rescuing when a tree got in the way; with
    /// no shoulder shots left, all it can do is slide the camera to a yaw nobody authored.
    /// The decollider stays, because vertical rescue does not change what the frame looks
    /// like.</item>
    /// <item><b>Nothing is ever teleported.</b> Unchanged. The presenter asks for a shot; the
    /// rig raises that camera's priority and the brain blends.</item>
    /// </list>
    ///
    /// The rig builds its own cameras at runtime when they are not assigned, so the battle
    /// stages correctly before the integrator has wired a scene. Assign them for shipping.
    /// </summary>
    [DefaultExecutionOrder(-350)]
    public sealed class BattleCameraRig : MonoBehaviour
    {
        /// <summary>Serialized binding of a shot to a hand-authored camera.</summary>
        [Serializable]
        public struct ShotCamera
        {
            public BattleShot Shot;
            public CinemachineCamera Camera;
        }

        [Header("Scene wiring")]
        [Tooltip("The brain this rig drives. Found on the main camera when left empty.")]
        [SerializeField] private CinemachineBrain brain;
        [Tooltip("Optional hand-authored cameras. Any shot left unbound is built at runtime.")]
        [SerializeField] private ShotCamera[] authoredCameras = Array.Empty<ShotCamera>();
        [Tooltip("Midpoint of the battle field. Falls back to the midpoint of the two creature marks.")]
        [SerializeField] private Transform stageCenter;

        [Header("Traditional layout")]
        [Tooltip("Degrees the view direction is rotated off the player-to-opponent axis. This is what " +
                 "separates the two creatures diagonally in frame; at 0 they stack exactly on top of " +
                 "each other, because both marks lie on the axis by definition.")]
        [Range(12f, 55f)]
        [SerializeField] private float layoutYaw = 32f;
        [Tooltip("Downward tilt of the fixed three-quarter view, in degrees. The HD-2D diorama band is " +
                 "roughly 15-30; above that a camera-facing billboard starts to look like a standing card.")]
        [Range(8f, 32f)]
        [SerializeField] private float layoutPitch = 20f;
        [Tooltip("Horizontal standoff from the field centre, metres = x + y * field extent.")]
        [SerializeField] private Vector2 standoff = new Vector2(1.2f, 2.6f);
        [Tooltip("Mirror the layout yaw. Flip this if the arena's authored geometry reads better " +
                 "with the player's creature to the right.")]
        [SerializeField] private bool mirrorLayout;

        [Header("Occlusion")]
        [Tooltip("Layers the camera must not sink into: terrain, rocks, buildings. Used by the decollider only.")]
        [SerializeField] private LayerMask obstacleLayers = ~0;
        [Tooltip("Layers that must never trigger occlusion resolution — the creatures themselves and their VFX.")]
        [SerializeField] private LayerMask subjectLayers = 0;
        [Tooltip("Terrain layers for the decollider, which stops the camera dropping below ground.")]
        [SerializeField] private LayerMask terrainLayers = ~0;
        [Tooltip("Radius of the sphere used for collision tests. Roughly the camera's near-plane half-diagonal.")]
        [SerializeField] private float cameraRadius = 0.32f;

        [Header("Framing")]
        [Tooltip("Shot the rig rests on when nothing else is requested.")]
        [SerializeField] private BattleShot restingShot = BattleShot.Field;
        [Tooltip("Creature height used before a creature is bound, in metres.")]
        [SerializeField] private float defaultDisplayHeight = 0.8f;
        [Tooltip("Height is clamped into this range before it is used for framing, so a bad registry value cannot put the camera in orbit.")]
        [SerializeField] private Vector2 displayHeightClamp = new Vector2(0.2f, 6f);

        [Header("Tuning")]
        [Tooltip("Per-shot framing rules. Populated with the shipped library when empty.")]
        [SerializeField] private ShotProfile[] shotProfiles = Array.Empty<ShotProfile>();
        [Tooltip("Per-pair blend rules, matched top down. Populated with the shipped table when empty.")]
        [SerializeField] private ShotBlendRule[] blendRules = Array.Empty<ShotBlendRule>();
        [Tooltip("Shake and handheld noise. Created on this object when left empty.")]
        [SerializeField] private CameraShakeDirector shake;

        [Header("Diagnostics")]
        [Tooltip("Log the solved apparent-size ratio across the shot library at startup. " +
                 "This is the number the pivot exists to keep small.")]
        [SerializeField] private bool logFramingAudit = true;

        // --- Runtime state ---------------------------------------------------------------

        private readonly Dictionary<BattleShot, CinemachineCamera> _cameras = new Dictionary<BattleShot, CinemachineCamera>();
        private readonly Dictionary<CinemachineCamera, BattleShot> _shotOf = new Dictionary<CinemachineCamera, BattleShot>();
        private readonly Dictionary<BattleShot, Transform> _aimProxies = new Dictionary<BattleShot, Transform>();
        private readonly Dictionary<BattleShot, ShotProfile> _profiles = new Dictionary<BattleShot, ShotProfile>();

        private ICreatureView _playerView;
        private ICreatureView _opponentView;
        private float _playerHeight;
        private float _opponentHeight;
        private BattleSide _actor = BattleSide.Player;
        private BattleSide _receiver = BattleSide.Opponent;

        private Transform _stageProxy;
        private BattleShot _current = BattleShot.None;

        private const int RestingPriority = 10;
        private const int ActivePriority = 40;

        // Consumed by the next blend query. See Snap: the opening shot of a battle must be a
        // cut, and every shot after it must not be.
        private bool _snapNextBlend;

        /// <summary>The shot currently held. <see cref="BattleShot.None"/> means the rig is resting.</summary>
        public BattleShot Current => _current == BattleShot.None ? restingShot : _current;

        /// <summary>Shake and handheld noise, for callers that need to punch the camera directly.</summary>
        public CameraShakeDirector Shake => shake;

        /// <summary>True while the brain is mid-blend. Beats that must land on a settled frame wait on this.</summary>
        public bool IsBlending => brain != null && brain.IsBlending;

        /// <summary>
        /// The camera the brain is currently outputting. The sprite billboards resolve their
        /// front/back facing against this, so it is exposed rather than left for each view to
        /// go hunting for <see cref="Camera.main"/>.
        /// </summary>
        public Camera OutputCamera => brain != null && brain.OutputCamera != null ? brain.OutputCamera : Camera.main;

        // --- Lifecycle -------------------------------------------------------------------

        private void Awake()
        {
            if (shotProfiles == null || shotProfiles.Length == 0) shotProfiles = ShotProfile.DefaultLibrary();
            if (blendRules == null || blendRules.Length == 0) blendRules = ShotBlendRule.DefaultTable();

            // Sanitise every profile, authored or shipped. This is the enforcement point for
            // the framing band and for "no shot avoids obstacles by moving" — an inspector
            // edit cannot get round it, because nothing reads the raw array afterwards.
            for (int i = 0; i < shotProfiles.Length; i++)
            {
                shotProfiles[i] = ShotProfile.Sanitise(shotProfiles[i]);
                _profiles[shotProfiles[i].Shot] = shotProfiles[i];
            }

            if (!_profiles.ContainsKey(restingShot)) restingShot = BattleShot.Field;

            if (brain == null && Camera.main != null) brain = Camera.main.GetComponent<CinemachineBrain>();
            if (shake == null) shake = GetComponent<CameraShakeDirector>();
            if (shake == null) shake = gameObject.AddComponent<CameraShakeDirector>();

            _stageProxy = new GameObject("~StageProxy") { hideFlags = HideFlags.DontSave }.transform;
            _stageProxy.SetParent(transform, false);

            BuildCameras();
            Retarget();
            ApplyPriorities();

            if (logFramingAudit) LogFramingAudit();
        }

        private void OnEnable()
        {
            // Subscribe rather than assign. GetBlendOverride is multicast and only the last
            // handler's return value is honoured, so the handler below returns the incoming
            // default untouched for any pair it does not own — that way a second subscriber
            // added later still gets sensible input and we never silently retune a blend
            // belonging to another system.
            CinemachineCore.GetBlendOverride += OnGetBlendOverride;
        }

        private void OnDisable()
        {
            CinemachineCore.GetBlendOverride -= OnGetBlendOverride;
        }

        /// <summary>
        /// Reports the apparent-size range the live library actually produces.
        ///
        /// This exists because the number it prints is the one the whole camera pivot turns
        /// on, and before the pivot there was no way to know it without measuring pixels in a
        /// screenshot. An over-range library is logged as an error, not a warning: shipping
        /// one is a visual regression that will be blamed on the art.
        /// </summary>
        private void LogFramingAudit()
        {
            float ratio = ShotProfile.ApparentSizeRatio(shotProfiles);
            string message = $"[BattleCameraRig] {shotProfiles.Length} shots, apparent-size range {ratio:0.00}× " +
                             $"(ceiling {ShotProfile.MaxApparentSizeRatio:0.0}×, pixel-art tolerance ~6×, " +
                             "the pre-pivot orbiting rig was 12-16×).";
            if (ratio > ShotProfile.MaxApparentSizeRatio) Debug.LogError(message, this);
            else Debug.Log(message, this);
        }

        // --- Binding ---------------------------------------------------------------------

        /// <summary>
        /// Binds the two combatants and their display heights. Call on battle start and
        /// again on every switch, because framing is solved from the heights.
        /// </summary>
        public void SetSubjects(ICreatureView player, float playerHeight, ICreatureView opponent, float opponentHeight)
        {
            _playerView = player;
            _opponentView = opponent;
            _playerHeight = ClampHeight(playerHeight);
            _opponentHeight = ClampHeight(opponentHeight);
            Retarget();
        }

        /// <summary>Binds one side only, for a mid-battle replacement.</summary>
        public void SetSubject(BattleSide side, ICreatureView view, float displayHeight)
        {
            if (side == BattleSide.Player) { _playerView = view; _playerHeight = ClampHeight(displayHeight); }
            else { _opponentView = view; _opponentHeight = ClampHeight(displayHeight); }
            Retarget();
        }

        /// <summary>
        /// Declares who is acting and who is receiving this beat. Shots focused on the actor
        /// or the receiver re-aim immediately, which is why the presenter sets roles before it
        /// asks for a shot rather than after.
        ///
        /// Note what this no longer does: it does not move a camera. In the orbiting rig a
        /// role change relocated the anchor of four shots.
        /// </summary>
        public void SetRoles(BattleSide actor, BattleSide receiver)
        {
            if (_actor == actor && _receiver == receiver) return;
            _actor = actor;
            _receiver = receiver;
            Retarget();
        }

        private float ClampHeight(float h)
        {
            if (h <= 0.001f || float.IsNaN(h)) h = defaultDisplayHeight;
            return Mathf.Clamp(h, displayHeightClamp.x, displayHeightClamp.y);
        }

        // --- Shot selection --------------------------------------------------------------

        /// <summary>
        /// Makes <paramref name="shot"/> live. The change is a priority raise, so the brain
        /// applies the authored blend for this exact pair; the camera is never moved.
        /// </summary>
        public void Show(BattleShot shot)
        {
            if (shot == BattleShot.None) { Release(); return; }
            if (!_cameras.ContainsKey(shot)) shot = restingShot;
            if (_current == shot) return;
            _current = shot;
            Retarget();
            ApplyPriorities();
        }

        /// <summary>
        /// Makes a shot live <b>with no blend and no damping</b> — the first frame is the
        /// settled frame.
        ///
        /// For the opening of a battle, and only for it. <see cref="Show"/> deliberately never
        /// moves the camera: it raises a priority and lets the brain blend, and every shot
        /// profile damps its aim on top of that — the field shot for 0.8 seconds. That is
        /// correct for a cut between two beats of a running battle and wrong for the very first
        /// frame of one, where it reads as the camera still turning to look at the fight while
        /// the fight has already started. Reported exactly that way: the position is right, the
        /// rotation arrives late.
        ///
        /// Two separate easings have to be cancelled, which is why this is not one line.
        /// <c>PreviousStateIsValid = false</c> tells each camera to treat its next update as
        /// its first and skip position and aim damping; the brain's own blend from whatever was
        /// live before is suppressed by <see cref="_snapNextBlend"/>, which
        /// <see cref="OnGetBlendOverride"/> answers with a cut exactly once.
        /// </summary>
        public void Snap(BattleShot shot)
        {
            if (shot == BattleShot.None) return;
            if (!_cameras.ContainsKey(shot)) shot = restingShot;

            _current = shot;
            Retarget();

            _snapNextBlend = true;
            ApplyPriorities();

            foreach (var kv in _cameras)
            {
                if (kv.Value != null) kv.Value.PreviousStateIsValid = false;
            }

            // The brain caches the outgoing camera's state for its blend; without this the cut
            // still starts from a stale frame and the first update visibly settles.
            if (brain != null) brain.ResetState();
        }

        /// <summary>Drops back to the resting shot through its (deliberately slow) blend.</summary>
        public void Release()
        {
            if (_current == BattleShot.None) return;
            _current = BattleShot.None;
            Retarget();
            ApplyPriorities();
        }

        /// <summary>
        /// Holds a shot for a duration and then releases it. The wait is unscaled, so a
        /// critical-hit time hitch does not stretch the shot along with it.
        /// </summary>
        public IEnumerator ShowFor(BattleShot shot, float seconds)
        {
            Show(shot);
            yield return CinematicRunner.Wait(seconds);
            Release();
        }

        /// <summary>Changes the shot the rig falls back to. Used to keep the outro on the winner.</summary>
        public void SetRestingShot(BattleShot shot)
        {
            if (!_cameras.ContainsKey(shot)) return;
            restingShot = shot;
            ApplyPriorities();
        }

        /// <summary>The shot that pans toward a given side. Saves every caller a ternary.</summary>
        public static BattleShot FocusOn(BattleSide side)
            => side == BattleSide.Player ? BattleShot.PlayerFocus : BattleShot.OpponentFocus;

        /// <summary>The camera backing a shot, for callers that need to reach the transform directly.</summary>
        public CinemachineCamera CameraFor(BattleShot shot)
            => _cameras.TryGetValue(shot, out var c) ? c : null;

        /// <summary>Enables or disables every camera in the rig, for entering and leaving battle.</summary>
        public void SetRigActive(bool active)
        {
            foreach (var kv in _cameras)
            {
                if (kv.Value != null) kv.Value.gameObject.SetActive(active);
            }
        }

        private void ApplyPriorities()
        {
            BattleShot live = Current;
            foreach (var kv in _cameras)
            {
                if (kv.Value == null) continue;
                kv.Value.Priority = kv.Key == live ? ActivePriority : RestingPriority;
            }

            // Tell the shake director how much of the frame the subject fills on the live
            // shot, so it can back off before it starts wobbling a near-full-screen cutout.
            if (shake != null && _profiles.TryGetValue(live, out var profile))
            {
                shake.SetSubjectScreenFraction(profile.StageScreenFraction);
                shake.SetShotGain(profile.ShakeGain);
            }
        }

        // --- Framing ----------------------------------------------------------------------

        private void LateUpdate()
        {
            UpdateProxies();
        }

        /// <summary>
        /// Moves the aim proxies onto their subjects' framing points every frame.
        ///
        /// Aiming at a proxy rather than at <c>Anchor_Head</c> directly is what lets a shot
        /// bias its aim between the feet and the crown, and — new since the pivot — what lets
        /// a "focus" be a partial pan toward a creature rather than a jump onto it.
        /// </summary>
        private void UpdateProxies()
        {
            Vector3 playerGround = GroundPointOf(BattleSide.Player);
            Vector3 opponentGround = GroundPointOf(BattleSide.Opponent);

            // The stage proxy is the camera's follow target and sits at ground level, because
            // the camera height is solved as a pitch above the ground plane. Putting it at
            // mid-creature height would make the layout pitch drift with creature size.
            if (_stageProxy != null)
            {
                _stageProxy.position = stageCenter != null
                    ? new Vector3(stageCenter.position.x, (playerGround.y + opponentGround.y) * 0.5f, stageCenter.position.z)
                    : (playerGround + opponentGround) * 0.5f;
            }

            foreach (var kv in _aimProxies)
            {
                if (kv.Value == null) continue;
                if (!_profiles.TryGetValue(kv.Key, out var profile)) continue;
                kv.Value.position = AimPointFor(profile, playerGround, opponentGround);
            }
        }

        /// <summary>
        /// Where a shot looks: the field midpoint panned <see cref="ShotProfile.FocusStrength"/>
        /// of the way toward its focused creature, raised by
        /// <see cref="ShotProfile.AimHeightBias"/> of that creature's height.
        ///
        /// Both marks lie on the stage axis, and the camera sits well back from it, so even a
        /// full pan from one creature to the other is only ~14° of yaw; at the shipped focus
        /// strengths it is ~6°. That is the entire angular range the rig can produce, and it
        /// is what makes a discrete front/back sprite choice safe.
        /// </summary>
        private Vector3 AimPointFor(ShotProfile profile, Vector3 playerGround, Vector3 opponentGround)
        {
            BattleSide? side = ResolveSide(profile.Focus);
            Vector3 mid = (playerGround + opponentGround) * 0.5f;

            float height;
            Vector3 ground;
            if (side.HasValue)
            {
                ground = Vector3.Lerp(mid, side.Value == BattleSide.Player ? playerGround : opponentGround,
                    Mathf.Clamp01(profile.FocusStrength));
                height = HeightOf(side.Value);
            }
            else
            {
                ground = mid;
                height = Mathf.Max(_playerHeight, _opponentHeight);
            }

            if (height <= 0.01f) height = defaultDisplayHeight;
            return ground + Vector3.up * (height * Mathf.Clamp01(profile.AimHeightBias));
        }

        /// <summary>Ground position of a side's creature: its mark, or the rig if nothing is bound.</summary>
        private Vector3 GroundPointOf(BattleSide side)
        {
            Transform mark = MarkFor(side);
            return mark != null ? mark.position : transform.position;
        }

        private ICreatureView ViewOf(BattleSide side) => side == BattleSide.Player ? _playerView : _opponentView;

        private BattleSide? ResolveSide(ShotFocus focus)
        {
            switch (focus)
            {
                case ShotFocus.Player: return BattleSide.Player;
                case ShotFocus.Opponent: return BattleSide.Opponent;
                case ShotFocus.Actor: return _actor;
                case ShotFocus.Receiver: return _receiver;
                default: return null;
            }
        }

        private Transform MarkFor(BattleSide side)
        {
            var view = ViewOf(side);
            if (view != null && view.Root != null) return view.Root;
            return stageCenter != null ? stageCenter : transform;
        }

        /// <summary>
        /// The single camera placement every shot shares: position, view direction and the
        /// reference distance the lens is solved against.
        ///
        /// Expressed as a yaw off the stage axis and a downward pitch rather than as back/up/
        /// side offsets, because those are the two numbers that describe a traditional
        /// three-quarter battle view and there is no third one. The old profile's per-shot
        /// offset triple is what made every shot capable of being somewhere else entirely.
        /// </summary>
        private void SolveBasis(out Vector3 position, out Vector3 viewDirection, out float referenceDistance, out float extent)
        {
            Vector3 axis = StageAxis();
            extent = StageExtent();

            float yaw = mirrorLayout ? -layoutYaw : layoutYaw;
            viewDirection = Quaternion.AngleAxis(yaw, Vector3.up) * axis;
            viewDirection.y = 0f;
            if (viewDirection.sqrMagnitude < 1e-4f) viewDirection = axis;
            viewDirection.Normalize();

            float back = Mathf.Max(1.0f, ShotProfile.Solve(standoff, extent));
            float height = back * Mathf.Tan(Mathf.Clamp(layoutPitch, 5f, 45f) * Mathf.Deg2Rad);

            Vector3 centre = _stageProxy != null ? _stageProxy.position : transform.position;
            position = centre - viewDirection * back + Vector3.up * height;

            // The reference distance is measured to the field centre, not to whatever the
            // current shot happens to be aiming at. That is what makes on-screen subject size
            // exactly proportional to StageScreenFraction: if the solve used the aim distance,
            // a pan toward the near creature would silently enlarge it.
            referenceDistance = Mathf.Max(0.5f, Vector3.Distance(position, centre + Vector3.up * (extent * 0.35f)));
        }

        /// <summary>
        /// Re-solves placement and lens for every camera.
        ///
        /// Runs on binding changes and on shot changes rather than per frame: the placement
        /// depends only on the stage axis and the creature heights, both of which are constant
        /// between events, and re-solving each frame would fight the Cinemachine damping that
        /// makes the shot feel weighted.
        /// </summary>
        public void Retarget()
        {
            // Proxies normally move in LateUpdate, but the basis and lens solves below need
            // their positions now — otherwise the first Retarget of a battle solves against a
            // proxy still sitting at the rig origin and the opening shot is framed wrong.
            UpdateProxies();

            SolveBasis(out Vector3 basePosition, out Vector3 viewDirection, out float referenceDistance, out float extent);
            Vector3 centre = _stageProxy != null ? _stageProxy.position : transform.position;
            float baseHeight = basePosition.y - centre.y;

            foreach (var kv in _cameras)
            {
                var cam = kv.Value;
                if (cam == null) continue;
                if (!_profiles.TryGetValue(kv.Key, out var profile)) continue;

                // The shot's whole freedom of placement: a fraction of the standoff nearer or
                // further, and a fraction of the camera height up or down. Both are clamped to
                // ±25% in ShotProfile.Sanitise, and the lens re-solves against the new
                // distance so neither changes how large the creature is drawn.
                Vector3 planar = basePosition - centre;
                planar.y = 0f;
                Vector3 offset = planar * (1f - profile.Dolly);
                offset.y = baseHeight * (1f + profile.Rise);

                var follow = cam.GetComponent<CinemachineFollow>();
                if (follow != null)
                {
                    follow.TrackerSettings.BindingMode = BindingMode.WorldSpace;
                    follow.TrackerSettings.PositionDamping = Vector3.one * Mathf.Max(0.01f, profile.PositionDamping);
                    follow.FollowOffset = offset;
                }

                var composer = cam.GetComponent<CinemachineRotationComposer>();
                if (composer != null) composer.Damping = profile.AimDamping;

                cam.Target.TrackingTarget = _stageProxy;
                cam.Target.LookAtTarget = ProxyFor(kv.Key);
                cam.Target.CustomLookAtTarget = true;

                // Apparent size. Solved against the shared reference distance corrected for
                // this shot's dolly, so the dolly is a pure perspective change.
                float shotDistance = Mathf.Max(0.5f, referenceDistance * (1f - profile.Dolly));
                cam.Lens.FieldOfView = SolveFov(extent, shotDistance, profile.StageScreenFraction, profile.FovRange);
                // No Dutch, ever. A rolled frame resamples a point-filtered sprite along a
                // diagonal and the pixel grid visibly shears.
                cam.Lens.Dutch = 0f;
                cam.Lens.NearClipPlane = Mathf.Clamp(shotDistance * 0.02f, 0.05f, 0.3f);

                ConfigureNoise(cam, profile);
                ConfigureOcclusion(cam, profile, extent);
            }

            // Keep the shake director in step with whichever shot is live, so a Retarget
            // caused by a role change does not leave it scaled for the previous shot.
            ApplyShakeContext();
        }

        private void ApplyShakeContext()
        {
            if (shake == null) return;
            if (!_profiles.TryGetValue(Current, out var profile)) return;
            shake.SetSubjectScreenFraction(profile.StageScreenFraction);
            shake.SetShotGain(profile.ShakeGain);
        }

        private float HeightOf(BattleSide side) => side == BattleSide.Player ? _playerHeight : _opponentHeight;

        /// <summary>The vertical extent a field-framing shot must cover, in metres.</summary>
        private float StageExtent() => SolveStageExtent(
            Mathf.Max(_playerHeight, _opponentHeight),
            Vector3.Distance(GroundPointOf(BattleSide.Player), GroundPointOf(BattleSide.Opponent)));

        /// <summary>
        /// The framing quantity for the field.
        ///
        /// Taken as the larger of "tall enough to contain the bigger creature with headroom"
        /// and "wide enough to contain the gap between the marks", because the field framing
        /// has to satisfy both and either one alone fails at the extremes: a small pair of
        /// creatures produces a frame with nothing in it, and a large pair produces one with
        /// both of them cropped.
        ///
        /// Unchanged by the pivot, and it is now the <i>only</i> quantity the lens is ever
        /// solved against — no shot frames a single creature any more.
        /// </summary>
        public static float SolveStageExtent(float tallestCreature, float markSeparation)
            => Mathf.Max(Mathf.Max(0.2f, tallestCreature) * 1.6f, Mathf.Max(0.5f, markSeparation) * 0.55f);

        /// <summary>
        /// The horizontal player-to-opponent direction. Falls back to this rig's forward so
        /// framing is still sane before any creature is bound.
        /// </summary>
        private Vector3 StageAxis()
        {
            Transform p = MarkFor(BattleSide.Player);
            Transform o = MarkFor(BattleSide.Opponent);
            if (p == null || o == null || p == o) return transform.forward;

            Vector3 axis = o.position - p.position;
            axis.y = 0f;
            return axis.sqrMagnitude < 1e-4f ? transform.forward : axis.normalized;
        }

        /// <summary>
        /// Vertical field of view that makes a subject of <paramref name="subjectHeight"/>
        /// metres fill <paramref name="fraction"/> of the frame at <paramref name="distance"/>.
        ///
        /// This is the piece that makes the rig size-agnostic. Without it, a shot tuned so a
        /// 1 m creature fills the frame leaves a 0.3 m creature as a speck in the middle of
        /// it, and the same offsets put a 6 m creature's knees on screen.
        /// </summary>
        public static float SolveFov(float subjectHeight, float distance, float fraction, Vector2 clampRange)
        {
            fraction = Mathf.Clamp(fraction, 0.05f, 1.5f);
            float desiredFrameHeight = Mathf.Max(0.05f, subjectHeight) / fraction;
            float fov = 2f * Mathf.Atan(desiredFrameHeight * 0.5f / Mathf.Max(0.05f, distance)) * Mathf.Rad2Deg;
            float min = Mathf.Min(clampRange.x, clampRange.y);
            float max = Mathf.Max(clampRange.x, clampRange.y);
            if (max <= 0f) { min = 20f; max = 65f; }
            return Mathf.Clamp(fov, min, max);
        }

        private Transform ProxyFor(BattleShot shot)
            => _aimProxies.TryGetValue(shot, out var t) ? t : null;

        // --- Camera construction ----------------------------------------------------------

        private void BuildCameras()
        {
            var authored = new Dictionary<BattleShot, CinemachineCamera>();
            if (authoredCameras != null)
            {
                foreach (var b in authoredCameras)
                {
                    if (b.Camera != null) authored[b.Shot] = b.Camera;
                }
            }

            foreach (var profile in shotProfiles)
            {
                if (profile.Shot == BattleShot.None) continue;
                if (_cameras.ContainsKey(profile.Shot)) continue;

                CinemachineCamera cam = authored.TryGetValue(profile.Shot, out var a) ? a : CreateCamera(profile.Shot);
                if (cam == null) continue;

                EnsurePipeline(cam);
                _cameras[profile.Shot] = cam;
                _shotOf[cam] = profile.Shot;

                var proxy = new GameObject("~Aim_" + profile.Shot) { hideFlags = HideFlags.DontSave }.transform;
                proxy.SetParent(transform, false);
                _aimProxies[profile.Shot] = proxy;
            }
        }

        private CinemachineCamera CreateCamera(BattleShot shot)
        {
            var go = new GameObject("CM_Battle_" + shot);
            go.transform.SetParent(transform, false);
            var cam = go.AddComponent<CinemachineCamera>();
            cam.Lens = LensSettings.Default;
            return cam;
        }

        /// <summary>
        /// Guarantees a camera has the full Body/Aim/Noise/extension stack, whether it was
        /// authored in the scene or built here. Idempotent, so an authored camera that
        /// already has a composer keeps the inspector-tuned one.
        /// </summary>
        private void EnsurePipeline(CinemachineCamera cam)
        {
            if (cam.GetComponent<CinemachineFollow>() == null) cam.gameObject.AddComponent<CinemachineFollow>();
            if (cam.GetComponent<CinemachineRotationComposer>() == null) cam.gameObject.AddComponent<CinemachineRotationComposer>();
            if (cam.GetComponent<CinemachineBasicMultiChannelPerlin>() == null) cam.gameObject.AddComponent<CinemachineBasicMultiChannelPerlin>();
            if (cam.GetComponent<CinemachineImpulseListener>() == null)
            {
                var listener = cam.gameObject.AddComponent<CinemachineImpulseListener>();
                listener.ChannelMask = 1;
                listener.Use2DDistance = false;
                listener.UseCameraSpace = true;
            }
            if (cam.GetComponent<CinemachineDeoccluder>() == null) cam.gameObject.AddComponent<CinemachineDeoccluder>();
            if (cam.GetComponent<CinemachineDecollider>() == null) cam.gameObject.AddComponent<CinemachineDecollider>();
        }

        private void ConfigureNoise(CinemachineCamera cam, ShotProfile profile)
        {
            var perlin = cam.GetComponent<CinemachineBasicMultiChannelPerlin>();
            if (perlin == null) return;

            if (profile.NoiseAmplitude <= 0.001f)
            {
                perlin.AmplitudeGain = 0f;
            }
            else
            {
                if (perlin.NoiseProfile == null) perlin.NoiseProfile = shake != null ? shake.HandheldProfile : null;
                perlin.AmplitudeGain = profile.NoiseAmplitude * (shake != null ? shake.HandheldScale : 1f);
                perlin.FrequencyGain = Mathf.Max(0.01f, profile.NoiseFrequency);
            }

            var listener = cam.GetComponent<CinemachineImpulseListener>();
            if (listener != null) listener.Gain = Mathf.Max(0f, profile.ShakeGain);
        }

        /// <summary>
        /// Occlusion policy, and it is the one place the pivot removed a capability rather
        /// than replacing it.
        ///
        /// <b>Obstacle avoidance is off on every shot.</b> A <c>CinemachineDeoccluder</c> with
        /// <c>PreserveCameraHeight</c> slides the camera laterally when scenery blocks the
        /// view — to an angle nobody authored, triggered by a tree rather than by intent. The
        /// old rig accepted that because an over-the-shoulder framing genuinely needed
        /// rescuing; a fixed three-quarter layout with a discrete front/back sprite set cannot
        /// absorb it. The component is left attached and configured so the integrator can see
        /// the decision in the inspector instead of wondering where it went.
        ///
        /// What replaces it is geometric: the standoff is solved from the field extent, so the
        /// camera is always outside the field looking in, and the decollider still lifts it
        /// out of terrain. Vertical rescue does not change the yaw, so it does not change what
        /// the sprites look like.
        /// </summary>
        private void ConfigureOcclusion(CinemachineCamera cam, ShotProfile profile, float extent)
        {
            int exempt = EffectiveSubjectLayers();

            var deoccluder = cam.GetComponent<CinemachineDeoccluder>();
            if (deoccluder != null)
            {
                deoccluder.CollideAgainst = obstacleLayers & ~exempt;
                deoccluder.TransparentLayers = exempt;
                deoccluder.MinimumDistanceFromTarget = Mathf.Max(0.25f, extent * 0.35f);

                var avoid = deoccluder.AvoidObstacles;
                // ShotProfile.Sanitise forces this false; the field is read rather than
                // hardcoded so the intent stays visible at the point of use.
                avoid.Enabled = profile.AvoidObstacles;
                avoid.CameraRadius = cameraRadius;
                avoid.Strategy = CinemachineDeoccluder.ObstacleAvoidance.ResolutionStrategy.PreserveCameraHeight;
                avoid.MaximumEffort = 4;
                avoid.SmoothingTime = 0.25f;
                avoid.Damping = 0.5f;
                avoid.DampingWhenOccluded = 0.12f;
                avoid.MinimumOcclusionTime = 0.05f;
                deoccluder.AvoidObstacles = avoid;

                // Shot quality evaluation is for ClearShot arbitration, which this rig does
                // not use; leaving it on only costs raycasts.
                var quality = deoccluder.ShotQualityEvaluation;
                quality.Enabled = false;
                deoccluder.ShotQualityEvaluation = quality;
            }

            var decollider = cam.GetComponent<CinemachineDecollider>();
            if (decollider != null)
            {
                decollider.CameraRadius = cameraRadius;

                var decollision = decollider.Decollision;
                decollision.Enabled = true;
                decollision.ObstacleLayers = obstacleLayers & ~exempt;
                decollision.Damping = 0.4f;
                decollision.SmoothingTime = 0.2f;
                decollider.Decollision = decollision;

                var terrain = decollider.TerrainResolution;
                terrain.Enabled = true;
                terrain.TerrainLayers = terrainLayers & ~exempt;
                terrain.MaximumRaycast = 12f;
                terrain.Damping = 0.35f;
                decollider.TerrainResolution = terrain;
            }
        }

        /// <summary>
        /// The layers collision resolution must ignore.
        ///
        /// <see cref="subjectLayers"/> is the authoritative answer once the integrator has set
        /// it, but it defaults to nothing and the obstacle mask defaults to everything. Left
        /// alone that combination would make every creature an obstacle, and the decollider
        /// would shove the camera whenever a billboard's bounds crossed it. So the layers the
        /// bound creature views are actually on are folded in automatically: an unconfigured
        /// rig still behaves, and a configured one is unaffected because those layers are
        /// already exempt.
        /// </summary>
        private int EffectiveSubjectLayers()
        {
            int mask = subjectLayers;
            if (_playerView?.Root != null) mask |= 1 << _playerView.Root.gameObject.layer;
            if (_opponentView?.Root != null) mask |= 1 << _opponentView.Root.gameObject.layer;
            return mask;
        }

        // --- Blending ---------------------------------------------------------------------

        /// <summary>
        /// Supplies the authored blend for a specific pair of shots.
        ///
        /// Hooked into <see cref="CinemachineCore.GetBlendOverride"/> rather than authored as
        /// a <c>CinemachineBlenderSettings</c> asset, because this worker ships no assets and
        /// because a code table can be reasoned about and unit-checked. Anything not in the
        /// table falls through to the caller's default, so this never breaks other systems'
        /// blends.
        /// </summary>
        private CinemachineBlendDefinition OnGetBlendOverride(
            ICinemachineCamera from, ICinemachineCamera to,
            CinemachineBlendDefinition defaultBlend, UnityEngine.Object owner)
        {
            BattleShot fromShot = ShotOf(from);
            BattleShot toShot = ShotOf(to);

            // Neither camera is ours: leave the transition alone.
            if (fromShot == BattleShot.None && toShot == BattleShot.None) return defaultBlend;

            // The one cut this rig performs, armed by Snap and spent here. Claimed before the
            // table is consulted so no authored rule can quietly reinstate a blend on the
            // frame a battle opens.
            if (_snapNextBlend)
            {
                _snapNextBlend = false;
                return new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.Cut, 0f);
            }

            foreach (var rule in blendRules)
            {
                bool fromMatches = rule.From == BattleShot.None || rule.From == fromShot;
                bool toMatches = rule.To == BattleShot.None || rule.To == toShot;
                if (!fromMatches || !toMatches) continue;

                // A zero-length blend is a cut, which this project does not do. Floor it.
                float seconds = Mathf.Max(0.05f, rule.Seconds);
                return new CinemachineBlendDefinition(rule.Style, seconds);
            }

            return defaultBlend;
        }

        private BattleShot ShotOf(ICinemachineCamera cam)
        {
            if (cam is CinemachineCamera cm && _shotOf.TryGetValue(cm, out var shot)) return shot;
            return BattleShot.None;
        }

        // --- Convenience for the presenter --------------------------------------------------

        /// <summary>Punches the camera for a damage event, using the receiver's position as the origin.</summary>
        public void PunchForDamage(BattleSide target, float damageFraction, bool critical, Effectiveness effectiveness)
        {
            if (shake == null) return;
            Vector3 point = ImpactPointOf(target);
            Vector3 direction = target == BattleSide.Player ? -StageAxis() : StageAxis();
            shake.Impact(point, direction, damageFraction, critical, effectiveness);
        }

        /// <summary>World position a projectile should aim at for the given side.</summary>
        public Vector3 ImpactPointOf(BattleSide side)
        {
            var view = ViewOf(side);
            float h = HeightOf(side);
            if (h <= 0.01f) h = defaultDisplayHeight;

            if (view == null || view.Root == null) return GroundPointOf(side) + Vector3.up * (h * 0.45f);
            if (view.BodyAnchor != null) return view.BodyAnchor.position;
            return view.Root.position + Vector3.up * (h * 0.45f);
        }

        /// <summary>The horizontal player-to-opponent direction, for callers staging their own motion.</summary>
        public Vector3 Axis => StageAxis();

        /// <summary>
        /// The horizontal direction the camera looks along. Used by the creature views to pick
        /// between the front and back sprite, and by anything that needs to place an effect
        /// "toward the viewer" rather than "toward the opponent".
        /// </summary>
        public Vector3 ViewDirection
        {
            get
            {
                SolveBasis(out _, out Vector3 viewDirection, out _, out _);
                return viewDirection;
            }
        }
    }
}
