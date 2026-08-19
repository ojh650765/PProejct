using System;
using UnityEngine;
using Unity.Cinemachine;
using PokeLab.Cinematics.Sequencing;
using PokeLab.Core;
using PokeLab.Overworld;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The conversation camera: whenever an overworld conversation has a speaker standing in
    /// the world, the camera leaves the exploration rig and blends to a close, low two-shot
    /// of the speaker and the player, then blends back out when the box closes.
    ///
    /// The exploration rig frames the town from 8.5 m up a 8-degree boom, which is the right
    /// distance to read streets from and the wrong one to meet a person at — the user's
    /// verified complaint was speakers entirely hidden behind trees from the game's very
    /// first scenes. The fix is not a deoccluder bolted onto the gameplay camera; it is a
    /// different shot, chosen the way a camera operator would choose it: stand where you can
    /// see both faces.
    ///
    /// So the core of this class is side selection, not framing. Before the shot is placed,
    /// several candidate positions — both sides of the speakers' axis at two angles, plus
    /// elevated fallbacks — are probed with sphere casts against both heads, and the first
    /// candidate that sees both wins. A fixed-side two-shot in this town is inside a canopy
    /// half the time; a probed one almost never is. The <see cref="CinemachineDeoccluder"/>
    /// stays on the camera as a safety net for the frames the probe cannot anticipate, but
    /// it is the net, never the plan.
    ///
    /// Integration is by polling and priority only. <see cref="DialogueRunner"/> is never
    /// edited or referenced back from: this component watches <c>Instance.IsPlaying</c> and
    /// <c>CurrentSpeaker</c>, claims <see cref="ShotPriorities.Dialogue"/> for the length of
    /// the exchange, and hands the frame back by dropping to zero — which returns it to the
    /// exploration rig. A cutscene, though, is owned by its authored shots: while
    /// <see cref="EpisodeShotRig.ShotIsLive"/> this director refuses to engage and releases
    /// if already live, so the 130-over-120 rung is spent only on dialogue that other
    /// systems chain into free play, never on an episode's own lines. Blend lengths are
    /// enforced through <see cref="CinemachineCore.GetBlendOverride"/> for exactly the pairs
    /// that involve this camera, the same etiquette <see cref="BattleCameraRig"/> established.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-320)]
    public sealed class DialogueCameraDirector : MonoBehaviour
    {
        // --- Tuning -------------------------------------------------------------------------

        [Header("Framing")]
        [Tooltip("Eye height on the player, in metres. The player half of the two-shot is aimed here.")]
        [SerializeField] private float playerEyeHeight = 1.5f;
        [Tooltip("Eye height assumed for a speaker with no collider to measure. Speakers with a " +
                 "collider are measured from its bounds instead, so children and tall NPCs frame correctly.")]
        [SerializeField] private float fallbackSpeakerEyeHeight = 1.4f;
        [Tooltip("Base distance from the midpoint of the two speakers, before the gap widening.")]
        [SerializeField] private float baseDistance = 2.6f;
        [Tooltip("Extra distance per metre of gap between the speakers, so a far pair is not cropped.")]
        [SerializeField] private float distancePerGap = 0.35f;
        [Tooltip("Preferred angle off the speakers' axis, in degrees. Zero is a flat, dull two-shot.")]
        [SerializeField] private float conversationAngle = 38f;
        [Tooltip("Wider angle tried when the preferred one is blocked on both sides.")]
        [SerializeField] private float wideAngle = 62f;
        [Tooltip("Metres the camera sits above the speakers' eye line. Low is what makes it intimate.")]
        [SerializeField] private float heightAboveEyes = 0.25f;
        [Tooltip("Extra rise for the last-resort elevated candidates, which look over low blockers.")]
        [SerializeField] private float elevatedRise = 1.9f;
        [Tooltip("Field of view for the two-shot. Tighter than gameplay.")]
        [SerializeField] private float conversationFov = 40f;

        [Header("Blends")]
        [Tooltip("Seconds the brain takes to blend into the two-shot. Short and eased, never a cut.")]
        [SerializeField] private float enterSeconds = 0.6f;
        [Tooltip("Seconds to blend back out when the box closes.")]
        [SerializeField] private float exitSeconds = 0.6f;
        [Tooltip("Smoothing time for tracking actors that move mid-conversation. NPCs walk in this " +
                 "game; the shot follows with this ease rather than ever hard-cutting.")]
        [SerializeField] private float trackSmoothing = 0.45f;

        [Header("Push-in")]
        [Tooltip("Metres per second of the very slow push-in over the conversation. Animal Crossing " +
                 "does this subtly; at this rate it is felt, not seen.")]
        [SerializeField] private float pushInPerSecond = 0.03f;
        [Tooltip("Metres of push-in the shot will never exceed.")]
        [SerializeField] private float maxPushIn = 0.35f;

        [Header("Occlusion probing")]
        [Tooltip("What can block the view. Everything by default; actors are skipped by identity.")]
        [SerializeField] private LayerMask occluderMask = ~0;
        [Tooltip("Radius of the sight-line sphere casts. Head-sized, so a twig does not veto a shot.")]
        [SerializeField] private float probeRadius = 0.22f;
        [Tooltip("Metres short of a head the sight probe stops, so the actor's own body never blocks itself.")]
        [SerializeField] private float headClearance = 0.45f;
        [Tooltip("Radius the candidate position itself must have clear, so the camera never starts inside a trunk.")]
        [SerializeField] private float cameraClearRadius = 0.3f;
        [Tooltip("Seconds between sight re-checks while the shot holds.")]
        [SerializeField] private float recheckInterval = 0.35f;
        [Tooltip("Seconds a held shot may stay blocked before the side is re-chosen. The deoccluder " +
                 "carries these moments; this is how long it is allowed to.")]
        [SerializeField] private float blockedPatience = 0.5f;
        [Tooltip("Minimum seconds between side re-selections, so a probe flickering on a leaf edge " +
                 "cannot swing the camera back and forth.")]
        [SerializeField] private float reselectCooldown = 1.5f;
        [Tooltip("Metres the speakers' gap must change before the side is reconsidered — actors " +
                 "walking apart can turn a clear side into a blocked one.")]
        [SerializeField] private float reselectGapDelta = 1.0f;

        // --- State --------------------------------------------------------------------------

        private enum State { Idle, Live, Settling }

        private static DialogueCameraDirector s_first;

        private readonly RaycastHit[] _hits = new RaycastHit[16];
        private readonly Collider[] _overlaps = new Collider[8];

        private CinemachineCamera _cam;
        private Transform _aimAnchor;
        private DialogueRunner _runner;
        private Transform _player;
        private float _nextPlayerScan;

        private State _state = State.Idle;
        private bool _sequenceEnded;
        private bool _overrideHooked;
        private float _speakerHeadHeight;
        private float _holdClock;
        private float _settleClock;
        private float _recheckTimer;
        private float _blockedFor;
        private float _lastSelectAt;
        private float _gapAtSelect;
        private SideCandidate _shot;

        private Vector3 _curPos, _curAim, _posVel, _aimVel;
        private float _curFov, _fovVel;
        private int _consecutiveFailures;

        /// <summary>The chosen shot, as parameters rather than a pose, so it re-solves from live anchors.</summary>
        private struct SideCandidate
        {
            public float Angle;
            public float Sign;
            public float ExtraHeight;
            public float DistanceScale;

            public SideCandidate(float angle, float sign, float extraHeight, float distanceScale)
            {
                Angle = angle;
                Sign = sign;
                ExtraHeight = extraHeight;
                DistanceScale = distanceScale;
            }
        }

        private struct ShotGoal
        {
            public Vector3 Position;
            public Vector3 Aim;
            public float Fov;
        }

        /// <summary>True while the conversation camera owns the frame. Diagnostic.</summary>
        public bool IsEngaged => _state == State.Live;

        // --- Lifecycle ----------------------------------------------------------------------

        /// <summary>First one wins, exactly like <see cref="DialogueRunner"/>: everything polling
        /// this feature keeps working through an additive scene load.</summary>
        private void Awake()
        {
            if (s_first != null && s_first != this)
            {
                Destroy(this);
                return;
            }
            s_first = this;
        }

        private void OnDisable() => ReleaseImmediate();

        private void OnDestroy()
        {
            ReleaseImmediate();
            if (_runner != null) _runner.SequenceEnded -= OnSequenceEnded;
            _runner = null;
            if (s_first == this) s_first = null;
        }

        private void LateUpdate()
        {
            // A conversation camera left claimed is a frozen game, so no exception may leave
            // the state machine holding the frame. Three strikes disables the feature rather
            // than letting a per-frame throw release-and-reclaim forever.
            try
            {
                Tick(Time.unscaledDeltaTime);
                _consecutiveFailures = 0;
            }
            catch (Exception e)
            {
                _consecutiveFailures++;
                Debug.LogError($"[DialogueCamera] Tick failed ({_consecutiveFailures}): {e}", this);
                try { ReleaseImmediate(); } catch { /* the release itself must never throw us out of here */ }
                if (_consecutiveFailures >= 3)
                {
                    Debug.LogError("[DialogueCamera] Disabled after repeated failures; conversations " +
                                   "fall back to the exploration camera.", this);
                    enabled = false;
                }
            }
        }

        // --- State machine ------------------------------------------------------------------

        private void Tick(float dt)
        {
            RebindRunner();
            RefreshPlayer();

            switch (_state)
            {
                case State.Idle:
                    // An ended flag with nothing playing is the tail of a conversation this
                    // camera never engaged (the prologue over black, the ambient bulletin).
                    _sequenceEnded = false;
                    if (ShouldEngage()) Engage();
                    break;

                case State.Live:
                    TickLive(dt);
                    break;

                case State.Settling:
                    TickSettling(dt);
                    break;
            }
        }

        private void TickLive(float dt)
        {
            // A battle transition's wipe must never find this camera still claimed: release
            // instantly, because the wipe is already covering the switch.
            if (InBattleFlow()) { ReleaseImmediate(); return; }

            // An authored shot going live mid-conversation takes the frame. Through the
            // normal release, not the emergency one: the brain still owes a blend out, and
            // no wipe is covering this switch.
            if (EpisodeShotRig.ShotIsLive) { BeginRelease(); return; }

            bool alive = _runner != null && _runner.IsPlaying;
            GameObject speaker = alive ? _runner.CurrentSpeaker : null;
            if (!alive || speaker == null || !speaker.activeInHierarchy || _player == null)
            {
                BeginRelease();
                return;
            }

            // The old conversation ended and a new one is already playing — a chained
            // exchange. Recompose in place rather than bouncing out and back in.
            if (_sequenceEnded)
            {
                _sequenceEnded = false;
                EngageInPlace(speaker);
            }

            Transform speakerT = speaker.transform;
            Vector3 a = _player.position;
            Vector3 headA = a + Vector3.up * playerEyeHeight;
            Vector3 headB = speakerT.position + Vector3.up * _speakerHeadHeight;

            _holdClock += dt;

            // Periodic sight re-check. The deoccluder absorbs a blocked beat or two; past
            // its patience the side is re-chosen, gently, through the same damped tracking
            // that follows walking actors — never a cut.
            _recheckTimer += dt;
            if (_recheckTimer >= recheckInterval)
            {
                _recheckTimer = 0f;
                bool blocked = !HasLineOfSight(_curPos, headB, speakerT)
                            || !HasLineOfSight(_curPos, headA, speakerT);
                _blockedFor = blocked ? _blockedFor + recheckInterval : 0f;
            }

            float gap = FlatDistance(a, speakerT.position);
            bool gapShifted = Mathf.Abs(gap - _gapAtSelect) > reselectGapDelta;
            if ((_blockedFor >= blockedPatience || gapShifted)
                && Time.unscaledTime - _lastSelectAt >= reselectCooldown)
            {
                SelectShot(speakerT);
                _blockedFor = 0f;
            }

            ShotGoal goal = ComputeGoal(speakerT);
            _curPos = Vector3.SmoothDamp(_curPos, goal.Position, ref _posVel, trackSmoothing, Mathf.Infinity, dt);
            _curAim = Vector3.SmoothDamp(_curAim, goal.Aim, ref _aimVel, trackSmoothing * 0.8f, Mathf.Infinity, dt);
            _curFov = Mathf.SmoothDamp(_curFov, goal.Fov, ref _fovVel, trackSmoothing, Mathf.Infinity, dt);
            Apply();
        }

        private void TickSettling(float dt)
        {
            if (InBattleFlow()) { ReleaseImmediate(); return; }

            // A new conversation opening while the last blend-out is still in the air —
            // reclaim from here and the brain blends forward again, no bounce to gameplay.
            if (ShouldEngage()) { Engage(); return; }

            _settleClock += dt;
            if (_settleClock >= exitSeconds + 0.9f) FinishRelease();
        }

        // --- Engage / release ---------------------------------------------------------------

        private bool ShouldEngage()
        {
            if (_runner == null || !_runner.IsPlaying) return false;

            // No speaker, no shot: the prologue plays over black and the plaza bulletin is
            // ambient — both start with nothing standing in the world to frame.
            GameObject speaker = _runner.CurrentSpeaker;
            if (speaker == null || !speaker.activeInHierarchy) return false;

            if (InBattleFlow()) return false;

            // A cutscene is owned by its authored shots: dialogue lines inside an episode
            // play under the episode's composition, and the two-shot must not outbid it.
            // This also keeps Settling from reclaiming over a shot that went live mid-blend.
            if (EpisodeShotRig.ShotIsLive) return false;

            if (_player == null) return false;

            // A degenerate axis (speaker on top of the player, or the player itself) has no
            // sides to choose between and no two-shot to compose — and neither has a pair
            // standing a street apart. A conversation anchor that far out is a staging or
            // wiring fault, and composing on it parks the camera in whatever lies between;
            // staying out leaves the exploration frame, which is always readable.
            Vector3 axis = speaker.transform.position - _player.position;
            axis.y = 0f;
            return axis.sqrMagnitude > 0.09f && axis.sqrMagnitude < 64f;
        }

        private void Engage()
        {
            EnsureCamera();
            GameObject speaker = _runner.CurrentSpeaker;
            Transform speakerT = speaker.transform;

            _sequenceEnded = false;
            _speakerHeadHeight = MeasureSpeakerEyeHeight(speaker);
            _holdClock = 0f;
            _recheckTimer = 0f;
            _blockedFor = 0f;

            SelectShot(speakerT);

            // The camera snaps to the composed pose and the *brain* performs the entry
            // blend, at exactly enterSeconds via the blend override. Seeding at the old
            // output pose and hand-tweening would fight the brain's own weight ramp.
            ShotGoal goal = ComputeGoal(speakerT);
            _curPos = goal.Position;
            _curAim = goal.Aim;
            _curFov = goal.Fov;
            _posVel = _aimVel = Vector3.zero;
            _fovVel = 0f;
            Apply();

            Subscribe();
            _cam.gameObject.SetActive(true);
            _cam.Priority = ShotPriorities.Dialogue;
            _state = State.Live;
        }

        /// <summary>A chained conversation: keep the claim, re-measure the (possibly new) speaker,
        /// re-choose the side, and let the damped tracking carry the move.</summary>
        private void EngageInPlace(GameObject speaker)
        {
            _speakerHeadHeight = MeasureSpeakerEyeHeight(speaker);
            _holdClock = 0f;
            _blockedFor = 0f;
            SelectShot(speaker.transform);
        }

        /// <summary>Drops the priority and lets the brain blend back to whoever holds the frame —
        /// the exploration rig in free play, or the authored shot this camera is yielding to
        /// when <see cref="EpisodeShotRig.ShotIsLive"/> went true mid-conversation.</summary>
        private void BeginRelease()
        {
            _sequenceEnded = false;
            if (_cam != null) _cam.Priority = 0;
            _settleClock = 0f;
            _state = State.Settling;
        }

        /// <summary>The camera stayed active through the blend-out — disabling it mid-blend removes
        /// the shot the brain is blending from and produces a cut. Now it can go.</summary>
        private void FinishRelease()
        {
            if (_cam != null) _cam.gameObject.SetActive(false);
            Unsubscribe();
            _state = State.Idle;
        }

        /// <summary>The unconditional hand-back for wipes, teardown and exception paths: priority
        /// gone, camera gone, override unhooked, no blend owed to anyone.</summary>
        private void ReleaseImmediate()
        {
            if (_cam != null)
            {
                _cam.Priority = 0;
                _cam.gameObject.SetActive(false);
            }
            Unsubscribe();
            _sequenceEnded = false;
            _state = State.Idle;
        }

        // --- Side selection -----------------------------------------------------------------

        /// <summary>
        /// Chooses which side of the speakers' axis the camera stands on, by probing.
        ///
        /// Candidates are tried in preference order: the near side at the preferred angle
        /// (near meaning wherever the camera already is, so the entry blend travels least),
        /// then the far side, both sides again at the wider angle, and finally two elevated
        /// poses that look over low blockers. The first candidate that sees both heads with
        /// a clear standing position wins; if none does, the best partial view is taken —
        /// a seen speaker outranks a seen player, because the occluder fade already keeps
        /// the player readable — and the deoccluder net carries the rest.
        /// </summary>
        private void SelectShot(Transform speakerT)
        {
            Vector3 a = _player.position;
            Vector3 b = speakerT.position;
            Vector3 headA = a + Vector3.up * playerEyeHeight;
            Vector3 headB = b + Vector3.up * _speakerHeadHeight;
            float s = PreferredSign(a, b);

            SideCandidate[] candidates =
            {
                new SideCandidate(conversationAngle, s, 0f, 1f),
                new SideCandidate(conversationAngle, -s, 0f, 1f),
                new SideCandidate(wideAngle, s, 0f, 1f),
                new SideCandidate(wideAngle, -s, 0f, 1f),
                new SideCandidate(conversationAngle, s, elevatedRise, 1.12f),
                new SideCandidate(conversationAngle, -s, elevatedRise, 1.12f),
            };

            int bestScore = int.MinValue;
            SideCandidate best = candidates[0];
            for (int i = 0; i < candidates.Length; i++)
            {
                Vector3 pos = PoseFor(candidates[i], a, b, 0f).Position;
                int score = ScoreCandidate(pos, headA, headB, speakerT);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidates[i];
                    if (score >= 3) break; // sees both heads; earlier order is higher preference
                }
            }

            _shot = best;
            _gapAtSelect = FlatDistance(a, b);
            _lastSelectAt = Time.unscaledTime;
        }

        /// <summary>Speaker visibility scores 2, player 1, a buried camera position −1: a shot
        /// that sees both is 3, and among partials the speaker is the face that matters.</summary>
        private int ScoreCandidate(Vector3 pos, Vector3 playerHead, Vector3 speakerHead, Transform speakerT)
        {
            if (!PositionClear(pos, speakerT)) return -1;
            int score = 0;
            if (HasLineOfSight(pos, speakerHead, speakerT)) score += 2;
            if (HasLineOfSight(pos, playerHead, speakerT)) score += 1;
            return score;
        }

        /// <summary>Which side of the axis the live camera is on, so the first candidate is the
        /// one the blend reaches soonest.</summary>
        private float PreferredSign(Vector3 a, Vector3 b)
        {
            Camera output = Camera.main;
            if (output == null) return 1f;
            Vector3 lateral = Vector3.Cross(Vector3.up, FlatAxis(a, b));
            Vector3 mid = Vector3.Lerp(a, b, 0.5f);
            return Vector3.Dot(output.transform.position - mid, lateral) >= 0f ? 1f : -1f;
        }

        private bool HasLineOfSight(Vector3 from, Vector3 head, Transform speakerT)
        {
            Vector3 delta = head - from;
            float length = delta.magnitude - headClearance;
            if (length <= 0.05f) return true;
            Vector3 dir = delta / delta.magnitude;

            int count = Physics.SphereCastNonAlloc(from, probeRadius, dir, _hits, length,
                occluderMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hits[i];
                if (hit.collider == null) continue;
                // Distance zero means the cast began overlapping this collider, which says
                // nothing about where its surface is — same rule the exploration rig uses.
                if (hit.distance <= 0.001f) continue;
                if (IsActor(hit.collider.transform, speakerT)) continue;
                return false;
            }
            return true;
        }

        private bool PositionClear(Vector3 pos, Transform speakerT)
        {
            int count = Physics.OverlapSphereNonAlloc(pos, cameraClearRadius, _overlaps,
                occluderMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider c = _overlaps[i];
                if (c == null) continue;
                if (IsActor(c.transform, speakerT)) continue;
                return false;
            }
            return true;
        }

        /// <summary>Child-of, not shared-root: on a map where props and people can hang under one
        /// container, comparing roots would file the whole level as "actor" and probe nothing.</summary>
        private bool IsActor(Transform t, Transform speakerT)
        {
            if (_player != null && (t == _player || t.IsChildOf(_player))) return true;
            return speakerT != null && (t == speakerT || t.IsChildOf(speakerT));
        }

        // --- Pose solving -------------------------------------------------------------------

        private ShotGoal ComputeGoal(Transform speakerT)
        {
            float pushIn = Mathf.Min(maxPushIn, _holdClock * pushInPerSecond);
            return PoseFor(_shot, _player.position, speakerT.position, pushIn);
        }

        /// <summary>
        /// The two-shot pose for a candidate: off the axis between the speakers by the signed
        /// angle, widened with their gap so neither head is cropped, sitting just above the
        /// eye line and aimed at the midpoint of the two heads.
        /// </summary>
        private ShotGoal PoseFor(SideCandidate side, Vector3 playerPos, Vector3 speakerPos, float pushIn)
        {
            Vector3 axis = FlatAxis(playerPos, speakerPos);
            Vector3 headA = playerPos + Vector3.up * playerEyeHeight;
            Vector3 headB = speakerPos + Vector3.up * _speakerHeadHeight;
            Vector3 aim = Vector3.Lerp(headA, headB, 0.5f);

            float gap = FlatDistance(playerPos, speakerPos);
            float distance = Mathf.Max(0.8f,
                (baseDistance + gap * distancePerGap) * side.DistanceScale - pushIn);

            Vector3 offsetDir = Quaternion.AngleAxis(side.Sign * (90f - side.Angle), Vector3.up) * axis;
            Vector3 mid = Vector3.Lerp(playerPos, speakerPos, 0.5f);
            Vector3 pos = mid + offsetDir * distance;
            pos.y = aim.y + heightAboveEyes + side.ExtraHeight;

            return new ShotGoal { Position = pos, Aim = aim, Fov = conversationFov };
        }

        private static Vector3 FlatAxis(Vector3 a, Vector3 b)
        {
            Vector3 axis = b - a;
            axis.y = 0f;
            return axis.sqrMagnitude < 1e-4f ? Vector3.forward : axis.normalized;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private void Apply()
        {
            Vector3 look = _curAim - _curPos;
            Quaternion rot = look.sqrMagnitude > 1e-5f
                ? Quaternion.LookRotation(look.normalized, Vector3.up)
                : _cam.transform.rotation;
            _cam.transform.SetPositionAndRotation(_curPos, rot);
            _cam.Lens.FieldOfView = _curFov;
            _aimAnchor.position = _curAim;
        }

        // --- Wiring -------------------------------------------------------------------------

        private void EnsureCamera()
        {
            if (_cam != null) return;

            var go = new GameObject("CM_Dialogue_TwoShot");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<CinemachineCamera>();
            _cam.Lens = LensSettings.Default;

            // The aim anchor exists for the deoccluder, which needs a LookAt target to know
            // whose sight line it is preserving. Nothing procedural aims the camera — the
            // transform set in Apply is the shot.
            var anchor = new GameObject("DialogueAim");
            anchor.transform.SetParent(transform, false);
            _aimAnchor = anchor.transform;
            _cam.LookAt = _aimAnchor;

            var deoccluder = go.AddComponent<CinemachineDeoccluder>();
            var avoid = deoccluder.AvoidObstacles;
            avoid.Enabled = true;
            avoid.CameraRadius = 0.3f;
            avoid.Strategy = CinemachineDeoccluder.ObstacleAvoidance.ResolutionStrategy.PreserveCameraHeight;
            avoid.Damping = 0.4f;
            avoid.SmoothingTime = 0.2f;
            deoccluder.AvoidObstacles = avoid;

            go.SetActive(false);
        }

        private void RebindRunner()
        {
            DialogueRunner runner = DialogueRunner.Instance;
            if (ReferenceEquals(runner, _runner)) return;
            if (_runner != null) _runner.SequenceEnded -= OnSequenceEnded;
            _runner = runner;
            if (_runner != null) _runner.SequenceEnded += OnSequenceEnded;
        }

        private void RefreshPlayer()
        {
            if (_player != null || Time.unscaledTime < _nextPlayerScan) return;
            _nextPlayerScan = Time.unscaledTime + 1f;
            var locomotion = FindAnyObjectByType<PlayerLocomotion>(FindObjectsInactive.Exclude);
            if (locomotion != null) _player = locomotion.transform;
        }

        /// <summary>Noted, not acted on: the event fires mid-frame from inside the runner, and
        /// camera work belongs in this component's own LateUpdate.</summary>
        private void OnSequenceEnded(string sequenceId) => _sequenceEnded = true;

        private static bool InBattleFlow()
        {
            if (!ServiceHub.TryGet<IGameFlow>(out var flow) || flow == null) return false;
            GameMode mode = flow.Mode;
            return mode == GameMode.Battle
                || mode == GameMode.EncounterIntro
                || mode == GameMode.BattleOutro;
        }

        private float MeasureSpeakerEyeHeight(GameObject speaker)
        {
            var collider = speaker.GetComponentInChildren<Collider>();
            if (collider != null)
            {
                float height = collider.bounds.max.y - speaker.transform.position.y - 0.15f;
                if (height > 0.4f) return Mathf.Clamp(height, 0.6f, 2.2f);
            }
            return fallbackSpeakerEyeHeight;
        }

        private void Subscribe()
        {
            if (_overrideHooked) return;
            CinemachineCore.GetBlendOverride += OnGetBlendOverride;
            _overrideHooked = true;
        }

        private void Unsubscribe()
        {
            if (!_overrideHooked) return;
            CinemachineCore.GetBlendOverride -= OnGetBlendOverride;
            _overrideHooked = false;
        }

        /// <summary>
        /// Enforces this camera's blend lengths for exactly the pairs it appears in, and
        /// returns the incoming default untouched for every pair it does not — the multicast
        /// etiquette <see cref="BattleCameraRig"/> documents. Hooked only while the camera
        /// is claimed, so the subscription cannot shadow anyone outside a conversation.
        /// </summary>
        private CinemachineBlendDefinition OnGetBlendOverride(
            ICinemachineCamera from, ICinemachineCamera to,
            CinemachineBlendDefinition defaultBlend, UnityEngine.Object owner)
        {
            if (_cam == null) return defaultBlend;
            bool entering = ReferenceEquals(to, _cam);
            bool leaving = ReferenceEquals(from, _cam);
            if (!entering && !leaving) return defaultBlend;

            float seconds = Mathf.Max(0.05f, entering ? enterSeconds : exitSeconds);
            var blend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, seconds);

            // This handler hooks on engage, after the rig's bootstrap-time subscription, and
            // a multicast delegate returns only its LAST handler's result — so for a pair
            // shared with an episode camera (the release toward an authored shot) returning
            // here would silently discard the rig's cut-when-blocked. The rig's path rule
            // must outrank this camera's timing, or the yield blends through the hillside.
            return EpisodeShotRig.EnforceBlendPath(from, to, blend);
        }
    }
}
