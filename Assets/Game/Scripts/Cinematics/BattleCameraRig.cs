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
    /// Three rules this class exists to enforce:
    ///
    /// 1. <b>Nothing is ever teleported.</b> The presenter asks for a shot; the rig raises
    ///    that camera's priority and the brain blends. No code path repositions a live
    ///    camera, so there is no way to author a cut by accident.
    /// 2. <b>Framing is solved, not authored in metres.</b> Placement offsets are expressed
    ///    per unit of creature height and the lens is then solved so the subject occupies a
    ///    fixed fraction of the screen. That is what lets a 0.3 m and a 6 m creature share
    ///    one rig.
    /// 3. <b>The camera never ends up inside anything.</b> Every shot carries a deoccluder
    ///    against the environment mask and a decollider against terrain, and the placement
    ///    solve enforces a minimum standoff from the creature it is anchored to.
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

        [Header("Occlusion")]
        [Tooltip("Layers the camera must not pass through: terrain, rocks, trees, buildings.")]
        [SerializeField] private LayerMask obstacleLayers = ~0;
        [Tooltip("Layers that must never trigger occlusion resolution — the creatures themselves and their VFX.")]
        [SerializeField] private LayerMask subjectLayers = 0;
        [Tooltip("Terrain layers for the decollider, which stops the camera dropping below ground.")]
        [SerializeField] private LayerMask terrainLayers = ~0;
        [Tooltip("Radius of the sphere used for occlusion tests. Roughly the camera's near-plane half-diagonal.")]
        [SerializeField] private float cameraRadius = 0.32f;

        [Header("Framing")]
        [Tooltip("Shot the rig rests on when nothing else is requested.")]
        [SerializeField] private BattleShot restingShot = BattleShot.WideEstablishing;
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

        /// <summary>The shot currently held. <see cref="BattleShot.None"/> means the rig is resting.</summary>
        public BattleShot Current => _current == BattleShot.None ? restingShot : _current;

        /// <summary>Shake and handheld noise, for callers that need to punch the camera directly.</summary>
        public CameraShakeDirector Shake => shake;

        /// <summary>True while the brain is mid-blend. Beats that must land on a settled frame wait on this.</summary>
        public bool IsBlending => brain != null && brain.IsBlending;

        // --- Lifecycle -------------------------------------------------------------------

        private void Awake()
        {
            if (shotProfiles == null || shotProfiles.Length == 0) shotProfiles = ShotProfile.DefaultLibrary();
            if (blendRules == null || blendRules.Length == 0) blendRules = ShotBlendRule.DefaultTable();
            foreach (var p in shotProfiles) _profiles[p.Shot] = p;

            if (brain == null && Camera.main != null) brain = Camera.main.GetComponent<CinemachineBrain>();
            if (shake == null) shake = GetComponent<CameraShakeDirector>();
            if (shake == null) shake = gameObject.AddComponent<CameraShakeDirector>();

            _stageProxy = new GameObject("~StageProxy") { hideFlags = HideFlags.DontSave }.transform;
            _stageProxy.SetParent(transform, false);

            BuildCameras();
            Retarget();
            ApplyPriorities();
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
        /// Declares who is acting and who is receiving this beat. The Actor- and
        /// Receiver-anchored shots re-solve immediately, which is why the presenter sets
        /// roles before it asks for a shot rather than after.
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
        /// bias its aim between head and body: a punch-in wants the chest, a reaction wants
        /// the face, and both are framing the same creature at the same instant.
        /// </summary>
        private void UpdateProxies()
        {
            Vector3 playerPoint = FramingPoint(_playerView, 0.5f, BattleSide.Player);
            Vector3 opponentPoint = FramingPoint(_opponentView, 0.5f, BattleSide.Opponent);
            if (_stageProxy != null) _stageProxy.position = (playerPoint + opponentPoint) * 0.5f;

            foreach (var kv in _aimProxies)
            {
                if (kv.Value == null) continue;
                if (!_profiles.TryGetValue(kv.Key, out var profile)) continue;

                BattleSide? side = ResolveSide(profile.Target);
                kv.Value.position = side.HasValue
                    ? FramingPoint(ViewOf(side.Value), profile.AimBias, side.Value)
                    : Vector3.Lerp(playerPoint, opponentPoint, 0.5f);
            }
        }

        /// <summary>
        /// The world point a shot aims at: interpolated between the body and head anchors.
        /// Falls back to the mark plus half the display height when a view or an anchor is
        /// missing, so an un-rigged placeholder still frames plausibly.
        /// </summary>
        private Vector3 FramingPoint(ICreatureView view, float bias, BattleSide side)
        {
            float h = side == BattleSide.Player ? _playerHeight : _opponentHeight;
            if (h <= 0f) h = defaultDisplayHeight;

            if (view == null || view.Root == null)
            {
                Transform mark = MarkFor(side);
                return (mark != null ? mark.position : transform.position) + Vector3.up * (h * 0.5f);
            }

            Vector3 body = view.BodyAnchor != null ? view.BodyAnchor.position : view.Root.position + Vector3.up * (h * 0.5f);
            Vector3 head = view.HeadAnchor != null ? view.HeadAnchor.position : view.Root.position + Vector3.up * h;
            return Vector3.Lerp(body, head, Mathf.Clamp01(bias));
        }

        private ICreatureView ViewOf(BattleSide side) => side == BattleSide.Player ? _playerView : _opponentView;

        private BattleSide? ResolveSide(ShotSubject subject)
        {
            switch (subject)
            {
                case ShotSubject.Player: return BattleSide.Player;
                case ShotSubject.Opponent: return BattleSide.Opponent;
                case ShotSubject.Actor: return _actor;
                case ShotSubject.Receiver: return _receiver;
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
        /// Re-solves placement and lens for every camera.
        ///
        /// Runs on binding changes and on shot changes rather than per frame: the offsets
        /// depend only on the stage axis and the creature heights, both of which are constant
        /// between events, and re-solving each frame would fight the Cinemachine damping that
        /// makes the shot feel weighted.
        /// </summary>
        public void Retarget()
        {
            // Proxies normally move in LateUpdate, but the lens solve below needs their
            // positions now — otherwise the first Retarget of a battle solves the FOV against
            // a proxy still sitting at the rig origin and the opening shot is framed wrong.
            UpdateProxies();

            Vector3 axis = StageAxis();

            foreach (var kv in _cameras)
            {
                var cam = kv.Value;
                if (cam == null) continue;
                if (!_profiles.TryGetValue(kv.Key, out var profile)) continue;

                BattleSide? anchorSide = ResolveSide(profile.Anchor);
                BattleSide? targetSide = ResolveSide(profile.Target);

                Transform anchor = anchorSide.HasValue ? MarkFor(anchorSide.Value) : (Transform)_stageProxy;

                // Placement scale. A field-anchored shot scales with the field, not with a
                // creature — otherwise the establishing shot on two tiny creatures sits as
                // close as a close-up and stops establishing anything.
                float anchorHeight = anchorSide.HasValue ? HeightOf(anchorSide.Value) : StageExtent();
                float placementScale = anchorHeight;
                if (anchorSide.HasValue && targetSide.HasValue && anchorSide.Value != targetSide.Value)
                {
                    // A cross-shot is composed of both creatures, so its offsets scale with
                    // both. Using the anchor alone backs a 6 m attacker's shoulder camera so
                    // far off that a 0.5 m target across the field becomes unframeable at any
                    // sane focal length. The geometric mean is identical for an evenly matched
                    // pair and only bites at the extremes, which is exactly where it is needed.
                    placementScale = Mathf.Sqrt(Mathf.Max(0.01f, anchorHeight * HeightOf(targetSide.Value)));
                }

                // What the lens is asked to fill. For a shot aimed at a creature that is the
                // creature; for one aimed at the field it is the field. Solving the wide shot
                // against a creature's height frames a 0.3 m creature at 7% of the screen and
                // pushes both combatants out of frame at the same time — the subject of an
                // establishing shot is the space between them, not either of them.
                float targetHeight = targetSide.HasValue ? HeightOf(targetSide.Value) : StageExtent();

                // Orientation of the shot: "back" runs away from what we are looking at, so
                // the camera always sits on the far side of the anchor from the target. For
                // stage-anchored shots the stage axis stands in.
                Vector3 toTarget = axis;
                if (anchorSide.HasValue && targetSide.HasValue && anchorSide.Value != targetSide.Value)
                {
                    toTarget = FramingPoint(ViewOf(targetSide.Value), 0.5f, targetSide.Value)
                             - FramingPoint(ViewOf(anchorSide.Value), 0.5f, anchorSide.Value);
                    toTarget.y = 0f;
                    if (toTarget.sqrMagnitude < 1e-4f) toTarget = axis;
                    toTarget.Normalize();
                }

                Vector3 right = Vector3.Cross(Vector3.up, toTarget).normalized;
                if (right.sqrMagnitude < 1e-4f) right = Vector3.right;

                float back = ShotProfile.Solve(profile.Back, placementScale);
                float up = ShotProfile.Solve(profile.Up, placementScale);
                float lateral = ShotProfile.Solve(profile.Side, placementScale);

                // Standoff floor, deliberately tied to the real anchor height rather than to
                // the blended placement scale: the thing the camera can physically collide
                // with is the creature it is sitting behind. This is the geometric half of the
                // "never intersect a creature" rule.
                float minStandoff = 0.45f + 0.55f * anchorHeight + cameraRadius;
                Vector3 planar = -toTarget * back + right * lateral;
                if (planar.magnitude < minStandoff) planar = planar.normalized * minStandoff;

                Vector3 offset = planar + Vector3.up * up;

                var follow = cam.GetComponent<CinemachineFollow>();
                if (follow != null)
                {
                    follow.TrackerSettings.BindingMode = BindingMode.WorldSpace;
                    follow.TrackerSettings.PositionDamping = Vector3.one * Mathf.Max(0.01f, profile.PositionDamping);
                    follow.FollowOffset = offset;
                }

                var composer = cam.GetComponent<CinemachineRotationComposer>();
                if (composer != null) composer.Damping = profile.AimDamping;

                cam.Target.TrackingTarget = anchor;
                cam.Target.LookAtTarget = ProxyFor(kv.Key);
                cam.Target.CustomLookAtTarget = true;

                // Solve the lens so the subject occupies the intended slice of the screen.
                Vector3 camPos = anchor.position + offset;
                Vector3 aimPos = ProxyFor(kv.Key) != null ? ProxyFor(kv.Key).position : anchor.position;
                float distance = Mathf.Max(0.35f, Vector3.Distance(camPos, aimPos));
                cam.Lens.FieldOfView = SolveFov(targetHeight, distance, profile.TargetScreenFraction, profile.FovRange);
                cam.Lens.Dutch = profile.Dutch;
                // Near clip must sit inside the standoff or the camera eats its own subject.
                cam.Lens.NearClipPlane = Mathf.Clamp(minStandoff * 0.15f, 0.05f, 0.3f);

                ConfigureNoise(cam, profile);
                ConfigureOcclusion(cam, profile, targetHeight);
            }
        }

        private float HeightOf(BattleSide side) => side == BattleSide.Player ? _playerHeight : _opponentHeight;

        /// <summary>The vertical extent a field-framing shot must cover, in metres.</summary>
        private float StageExtent() => SolveStageExtent(
            Mathf.Max(_playerHeight, _opponentHeight),
            Vector3.Distance(MarkFor(BattleSide.Player).position, MarkFor(BattleSide.Opponent).position));

        /// <summary>
        /// The framing quantity for a shot aimed at the whole field.
        ///
        /// Taken as the larger of "tall enough to contain the bigger creature with headroom"
        /// and "wide enough to contain the gap between the marks", because an establishing
        /// shot has to satisfy both and either one alone fails at the extremes: a small pair
        /// of creatures produces a frame with nothing in it, and a large pair produces one
        /// with both of them cropped.
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
                return;
            }

            if (perlin.NoiseProfile == null) perlin.NoiseProfile = shake != null ? shake.HandheldProfile : null;
            perlin.AmplitudeGain = profile.NoiseAmplitude * (shake != null ? shake.HandheldScale : 1f);
            perlin.FrequencyGain = Mathf.Max(0.01f, profile.NoiseFrequency);

            var listener = cam.GetComponent<CinemachineImpulseListener>();
            if (listener != null) listener.Gain = Mathf.Max(0f, profile.ShakeGain);
        }

        /// <summary>
        /// Occlusion setup, and the reasoning behind the layer split.
        ///
        /// Creatures go in <see cref="subjectLayers"/>, which is fed to the deoccluder as
        /// transparent. That is deliberate: an over-the-shoulder shot has the near creature
        /// between the lens and the target by design, so treating it as an obstacle would
        /// make the deoccluder shove the camera forward through its own subject on every
        /// shoulder shot. Creature clipping is prevented geometrically instead, by the
        /// standoff floor and near-clip solve in <see cref="Retarget"/>.
        ///
        /// Environment goes in <see cref="obstacleLayers"/> and does push the camera, and
        /// the decollider separately stops it sinking into terrain.
        /// </summary>
        /// <summary>
        /// The layers occlusion must ignore.
        ///
        /// <see cref="subjectLayers"/> is the authoritative answer once the integrator has
        /// set it, but it defaults to nothing, and the obstacle mask defaults to everything.
        /// Left alone that combination would make every creature an obstacle and shove the
        /// camera on every over-the-shoulder shot. So the layers the bound creature views are
        /// actually on are folded in automatically: an unconfigured rig still behaves, and a
        /// configured one is unaffected because those layers are already exempt.
        /// </summary>
        private int EffectiveSubjectLayers()
        {
            int mask = subjectLayers;
            if (_playerView?.Root != null) mask |= 1 << _playerView.Root.gameObject.layer;
            if (_opponentView?.Root != null) mask |= 1 << _opponentView.Root.gameObject.layer;
            return mask;
        }

        private void ConfigureOcclusion(CinemachineCamera cam, ShotProfile profile, float targetHeight)
        {
            int exempt = EffectiveSubjectLayers();

            var deoccluder = cam.GetComponent<CinemachineDeoccluder>();
            if (deoccluder != null)
            {
                deoccluder.CollideAgainst = obstacleLayers & ~exempt;
                deoccluder.TransparentLayers = exempt;
                deoccluder.MinimumDistanceFromTarget = Mathf.Max(0.25f, targetHeight * 0.4f);

                var avoid = deoccluder.AvoidObstacles;
                avoid.Enabled = true;
                avoid.CameraRadius = cameraRadius;
                // PreserveCameraHeight, not PullCameraForward: pulling forward on an
                // over-the-shoulder shot walks the lens into the creature's back. Preserving
                // height slides around the obstacle and keeps the composition.
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
            var view = ViewOf(target);
            Vector3 point = FramingPoint(view, 0.4f, target);
            Vector3 direction = target == BattleSide.Player ? -StageAxis() : StageAxis();
            shake.Impact(point, direction, damageFraction, critical, effectiveness);
        }

        /// <summary>World position a projectile should aim at for the given side.</summary>
        public Vector3 ImpactPointOf(BattleSide side) => FramingPoint(ViewOf(side), 0.35f, side);

        /// <summary>The horizontal player-to-opponent direction, for callers staging their own motion.</summary>
        public Vector3 Axis => StageAxis();
    }
}
