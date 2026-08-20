using System.Collections;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The way out of town, closed until the story opens it — and closed by a <b>line on the
    /// ground</b> rather than by a solid.
    ///
    /// <b>Why there is no collider here any more.</b> There was one, and every attempt to make
    /// it harmless to the NavMesh failed in a new way. Baked in, it carved the ramp out of the
    /// town mesh permanently, so every agent asked to leave town pathed around it into the
    /// fence beside Bram — that is Kes veering right at the gatekeeper. Kept out of the bake
    /// with a <c>NavMeshModifier</c>, the baked asset was continuous but a carve-era
    /// <c>NavMeshObstacle</c> stayed serialized on the object and re-cut the same hole at
    /// runtime. Each fix was correct and each left the same symptom, because the shape of the
    /// thing was wrong: a wall is level geometry, and this is a story rule. The user called it:
    /// "플레이어를 이 콜리전으로 막는게 아니라 좌표값으로 막도록 변경시키셈."
    ///
    /// So the rule is now arithmetic. The gate is a point, a facing, a width and a stand-off.
    /// Each frame, after the player has moved, their offset along the gate's forward axis is
    /// measured; if they are inside the width and have reached the line from the town side,
    /// the outward component of that step is taken back off them. Nothing is in the physics
    /// scene, nothing is in the navmesh, and there is nothing left for a bake to find.
    ///
    /// What that buys, beyond the bug: agents were always meant to pass ("플레이어만 못
    /// 지나가게 하고 에이전트는 괜찮도록") and now they pass through ground that has never
    /// been cut, on a mesh with no runtime carving on it at all. The visual trade the collider
    /// era accepted — an NPC clipping through a wall — is gone with the wall.
    ///
    /// Not a story beat and deliberately not a <see cref="StoryEncounter"/>. An encounter fires
    /// once and retires; a gate has to keep refusing, every time, until the flag turns.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StoryGate : MonoBehaviour
    {
        [Tooltip("Flag that opens the way. Until it is set the line refuses the player.")]
        [SerializeField] private string _opensOnFlag = "story.gate_open";

        [Tooltip("Who explains the refusal. Optional — without one the line is silent, which " +
                 "reads as the player being stuck rather than as somebody stopping them.")]
        [SerializeField] private NpcController _keeper;

        [Tooltip("Metres. How wide the refusal is, centred on this object and measured across " +
                 "its right axis. ZERO OR LESS MEANS NO LIMIT — the refusal is the whole line, " +
                 "which is what the town's northern boundary actually is.")]
        [SerializeField] private float _width;

        [Tooltip("Metres town-side of this object at which the player is stopped. Roughly where " +
                 "the face of the old wall plus the player's own radius put them.")]
        [SerializeField] private float _standOff = 0.6f;

        [Tooltip("Metres above and below this object the refusal applies. Outside the band the " +
                 "player is somewhere the gate is not about — a rooftop, the cave floor.")]
        [SerializeField] private float _heightBelow = 3f;
        [SerializeField] private float _heightAbove = 4f;

        [Tooltip("Metres. How close the player gets before the keeper speaks up unprompted.")]
        [SerializeField] private float _speakRadius = 3.2f;

        [Tooltip("Metres beyond the speak radius the player must retreat before the keeper " +
                 "will say it again. Without the gap he re-triggers on every step taken on " +
                 "the boundary itself.")]
        [SerializeField] private float _rearmMargin = 1.8f;

        private Transform _player;
        private PlayerLocomotion _playerLocomotion;
        private bool _spoken;

        /// <summary>
        /// Where the player last stood properly outside the keeper's earshot -- the spot the
        /// refusal walks them back to.
        ///
        /// Sampled continuously rather than latched on the crossing, because "before they came
        /// up to him" is a place, not an event, and which frame the radius is crossed on is a
        /// coin toss at four metres a second.
        /// </summary>
        private Vector3 _approachFrom;
        private bool _hasApproachFrom;

        /// <summary>True while the keeper has a dialogue open. Its falling edge is the cue.</summary>
        private bool _keeperTalking;

        /// <summary>True for the length of the scripted walk back. See <see cref="Retreat"/>.</summary>
        private bool _retreating;

        /// <summary>
        /// Which side of the line the player is on: -1 town, +1 field, 0 not yet observed.
        ///
        /// The refusal only ever applies to somebody arriving from the town — a player who is
        /// genuinely out there (a save restored in the field, a walk around the end of the
        /// wall) must not be shoved backwards through a gate they are standing past.
        ///
        /// <b>It is only ever set to +1 by a legitimate route</b>, and that distinction is the
        /// whole correctness of this component. An earlier version promoted it to +1 from the
        /// player's position alone, before deciding whether to block — so one long frame was
        /// enough to break the gate for the rest of the session. Under the capture harness a
        /// screenshot stalls the loop for a few hundred milliseconds; at four metres a second
        /// that is over a metre of travel in a single Update, which stepped the player clean
        /// past the stand-off, latched this to +1, and switched the refusal off permanently.
        /// Verified in play: the player walked from 3.2 m town-side to 4.8 m field-side without
        /// being touched. A frame that long is rare in a real session and not impossible — a
        /// shader compile, an autosave, a browser tab regaining focus — and the failure it
        /// produces is silent and total.
        /// </summary>
        private int _side;

        /// <summary>Set by the level builder. The town is generated; nothing here is hand-placed.</summary>
        public void Configure(string opensOnFlag, NpcController keeper, float width, float standOff)
        {
            _opensOnFlag = opensOnFlag ?? "";
            _keeper = keeper;
            _width = width;
            _standOff = standOff;

            StripSolids();
        }

        private void Awake()
        {
            StripSolids();
        }

        /// <summary>
        /// Removes anything from earlier eras of this gate that would touch the navmesh or the
        /// physics scene.
        ///
        /// Both fossils are real and both are in scenes on disk: the carve-era
        /// <c>NavMeshObstacle</c> that cut the ramp permanently, and the wall <c>BoxCollider</c>
        /// this class no longer uses. Destroyed on sight so an old scene heals at load; the
        /// builder creates neither, so a rebuilt scene has nothing to destroy.
        /// </summary>
        private void StripSolids()
        {
            foreach (var fossil in GetComponents<UnityEngine.AI.NavMeshObstacle>())
            {
                Debug.Log("[Gate] Removing a fossil NavMeshObstacle from the story gate — it " +
                          "carved the ramp permanently. The gate is a coordinate now.", this);
                Destroy(fossil);
            }

            foreach (var wall in GetComponents<BoxCollider>())
            {
                Debug.Log("[Gate] Removing the story gate's wall collider — the player is held " +
                          "back by coordinate, and a solid here only ever cost the navmesh.", this);
                Destroy(wall);
            }
        }

        private void Update()
        {
            if (IsOpen()) { _spoken = false; _keeperTalking = false; return; }
            RememberApproach();
            WatchKeeper();
            Speak();
        }

        /// <summary>
        /// Notes the last place the player stood outside the keeper's radius.
        ///
        /// Only while they are genuinely clear of him -- past the rearm margin as well as the
        /// speak radius -- so the remembered spot is somewhere the refusal can set them down
        /// without immediately triggering itself again.
        /// </summary>
        private void RememberApproach()
        {
            if (_retreating || !ResolvePlayer()) return;

            var flat = _player.position - transform.position;
            flat.y = 0f;
            if (flat.magnitude <= _speakRadius + _rearmMargin) return;

            _approachFrom = _player.position;
            _hasApproachFrom = true;
        }

        /// <summary>
        /// Starts the walk back on the frame the keeper stops talking.
        ///
        /// Watching the dialogue rather than hooking the call that opened it, and that is the
        /// point: the player can reach this conversation two ways -- by walking into the radius
        /// and having Bram speak up, or by deliberately pressing interact on him -- and the
        /// refusal is the same either way. A hook on <see cref="Speak"/> would only have caught
        /// the first, which is not the one the user described ("말을 걸어서 대화가 종료되면").
        ///
        /// Only HIS conversation counts. CurrentSpeaker is compared against the keeper's own
        /// object so that a villager two metres away, or a scripted scene playing over the top,
        /// does not end with the player being marched down the ramp.
        /// </summary>
        private void WatchKeeper()
        {
            if (_retreating || _keeper == null) return;

            var dialogue = DialogueRunner.Instance;
            var speaker = dialogue != null && dialogue.IsPlaying ? dialogue.CurrentSpeaker : null;
            var his = speaker != null &&
                      (speaker == _keeper.gameObject ||
                       speaker.transform.IsChildOf(_keeper.transform));

            if (his) { _keeperTalking = true; return; }
            if (!_keeperTalking) return;

            _keeperTalking = false;

            // Not on top of a scripted scene. The opening walks the player to a mark beside this
            // gate and Bram speaks his lines there as part of it; marching them off mid-episode
            // would be this component fighting the runner for the same body.
            var runner = EpisodeRunner.Live;
            if (runner != null && runner.IsPlaying) return;

            StartCoroutine(Retreat());
        }

        /// <summary>
        /// After the player has moved, and before the camera reads their position.
        ///
        /// <see cref="PlayerLocomotion"/> moves in Update, so a correction applied in Update
        /// races it — half the time it would be overwritten in the same frame and the player
        /// would walk through. LateUpdate is the only place the step is finished and nothing
        /// has drawn yet.
        /// </summary>
        private void LateUpdate()
        {
            if (IsOpen()) { _side = 0; return; }
            HoldPlayerBack();
        }

        /// <summary>
        /// The whole of the refusal: measure, decide, take the outward step back.
        ///
        /// <b>The correction is a coordinate clamp, not a move.</b> It used to go through
        /// <see cref="CharacterController.Move"/>, on the reasoning that Move is what the
        /// controller understands — it keeps the cached position in step and slides along the
        /// ground rather than stuttering. What it also does is collide and slide, and that is a
        /// sweep: pressing diagonally into the line gave the sweep a wall to slide along, and it
        /// walked the player sideways down the line, off the edge of the ramp, into the ground
        /// beside it — 가상벽에서 플레이어가 길 옆으로 떨어져서 다시는 길 위로 못올라옴. A refusal
        /// that can relocate the player laterally is not a refusal, it is a shove.
        ///
        /// Writing <c>transform.position</c> is the correct way to reposition a
        /// <see cref="CharacterController"/> — it reads the transform at the start of each Move
        /// rather than caching across frames, which is exactly why the old fallback path below
        /// did this already. The write moves them along one axis only, back onto ground they
        /// occupied a frame earlier, so there is nothing for a sweep to resolve.
        /// </summary>
        private void HoldPlayerBack()
        {
            if (!ResolvePlayer()) return;

            var offset = _player.position - transform.position;

            // Height first: the ramp is a slope and the world above and below it is not this
            // gate's business. Leaving the band forgets which side they were on, so re-entering
            // it is read fresh rather than against a stale memory of a different journey.
            var up = Vector3.Dot(offset, transform.up);
            if (up > _heightAbove || up < -_heightBelow) { _side = 0; return; }

            // A player in the air is falling past the gate, not walking through it, and the
            // one thing a refusal must never do is act on somebody who is not standing up. The
            // correction is horizontal; applied mid-fall it moves them sideways through the
            // air, which is how a fall beside the ramp becomes a landing somewhere they cannot
            // climb out of. Their side is left alone rather than forgotten — they land where
            // they left from, and the refusal resumes underneath them.
            if (_playerLocomotion != null && !_playerLocomotion.IsGrounded) return;

            var across = Mathf.Abs(Vector3.Dot(offset, transform.right));
            var out_ = Vector3.Dot(offset, transform.forward);

            // First frame we have seen them in the band. Believe wherever they are — a save
            // restored past the gate, a debug jump to the lake — and block nothing yet.
            if (_side == 0)
            {
                _side = out_ > 0f ? 1 : -1;
                return;
            }

            // Beyond the ends of the wall is not refused, and walking round them is how a
            // player legitimately changes side. Recorded here so that coming back INWARD from
            // the field is met by a gate that already knows they are outside it.
            //
            // A width of zero switches this off entirely, and that is the town's setting.
            // MEASURED, not chosen: the north fence (Barrier_TownNorth_01) ends 4.2 m west of
            // the ramp mouth and the ground north of it is open, so a 6.5 m box let the player
            // walk up to the line, slide along it, and round its end onto the route — verified
            // in play, lateral 5 m to 21 m, ending 24 m west of the gate on open grass. Every
            // wider box has the same hole further out, because the field really is open over
            // there; the containment is not a doorway, it is a boundary. So the refusal is the
            // whole line: north of it is not town, and until the flag turns the player does not
            // go north of it. Everything in the town is south of it, the arrival marker for a
            // traveller coming back is 2.5 m south of it, and by the time any Field content is
            // reachable the flag has been set.
            if (_width > 0f && across > _width * 0.5f)
            {
                _side = out_ > 0f ? 1 : -1;
                return;
            }

            if (_side > 0)
            {
                // Out there by some route this gate allowed. It re-arms the moment they are
                // properly back on the town side, so the refusal works again next time.
                if (out_ < -_standOff) _side = -1;
                return;
            }

            if (out_ <= -_standOff) { return; }   // still short of the line

            // They are on or past the line, having come from the town. How far past does not
            // matter and is deliberately not treated as "they got out": the correction is the
            // whole overshoot, so a frame long enough to step a metre through the gate is put
            // back exactly like a frame that stepped a centimetre through it. _side is NOT
            // promoted here — that is the bug this shape exists to make impossible.
            var overshoot = out_ + _standOff;
            _player.position -= transform.forward * overshoot;
        }

        private bool ResolvePlayer()
        {
            if (_player != null) return true;

            var found = GameObject.FindGameObjectWithTag(OverworldNames.PlayerTag);
            if (found == null) return false;

            _player = found.transform;
            _playerLocomotion = found.GetComponent<PlayerLocomotion>();
            return true;
        }

        private bool IsOpen()
        {
            if (string.IsNullOrEmpty(_opensOnFlag)) return true;
            var profile = ServiceHub.TryGet<IPlayerProfile>(out var found) ? found as PlayerProfile : null;
            // No profile yet means the game is still booting, not that the gate is open.
            return profile != null && profile.GetFlagBool(_opensOnFlag);
        }

        /// <summary>
        /// Has the keeper say why, once per approach.
        ///
        /// Which of his three lines he says is decided by NpcController against the same flags,
        /// so walking into the line and talking to him say the same thing — there is no second
        /// copy of the gating logic here to drift out of step with dialogue.json.
        /// </summary>
        private void Speak()
        {
            if (_keeper == null) return;
            if (_retreating) return;
            if (StoryInterlude.Active) return;
            if (!ResolvePlayer()) return;

            var flat = _player.position - transform.position;
            flat.y = 0f;
            var distance = flat.magnitude;

            if (distance > _speakRadius + _rearmMargin) _spoken = false;
            if (_spoken || distance > _speakRadius) return;

            // Never over a scene or a conversation already in progress. The opening walks the
            // player to a mark a few metres from here and Bram speaks his own lines there as
            // part of it; a second, unscripted copy of him talking over that is the failure
            // this check exists for.
            var runner = EpisodeRunner.Live;
            if (runner != null && runner.IsPlaying) return;
            var dialogue = DialogueRunner.Instance;
            if (dialogue != null && dialogue.IsPlaying) return;

            _spoken = true;
            _keeper.Interact(_player.gameObject);
        }

        /// <summary>
        /// Bram finishes, and then the player is walked back down the ramp.
        ///
        /// <b>Why the refusal is a scene rather than a shove.</b> The coordinate clamp above is
        /// a boundary, and a boundary is the wrong tool for the moment a person tells you no.
        /// Leaning on it, the player stood in the one spot the level has least to say about,
        /// slid along it, and walked off the side of the ramp into ground they could not climb
        /// out of. The user named the cause exactly -- 밀어내는게 문제라기 보단 유저가 컨트롤이
        /// 가능해서 그럼 -- so control is taken away instead: the conversation ends, the game
        /// walks them back to where they came from, and hands the pad back there.
        ///
        /// Nothing may speak to them while it runs. <see cref="StoryInterlude"/> holds Kes off,
        /// because his trigger sits in the square they are being walked into, and it holds the
        /// keeper himself off so that walking back out through his radius is not met by a second
        /// copy of the line they just heard.
        ///
        /// The hold and the pad are given back on every path out, including the early one: a
        /// refusal that leaves the player frozen is worse than one that does nothing.
        /// </summary>
        private IEnumerator Retreat()
        {
            if (_retreating) yield break;
            _retreating = true;
            StoryInterlude.Begin();

            if (ResolvePlayer())
            {
                SetPlayerInput(false);
                yield return WalkBack();
            }

            SetPlayerInput(true);
            StoryInterlude.End();
            _retreating = false;
        }

        /// <summary>
        /// Puts the hold and the pad back if this object dies mid-walk.
        ///
        /// A coroutine on a destroyed component stops where it is; it does not run to its end.
        /// The scene unloading during a refusal would otherwise leak the hold, and a leaked hold
        /// is silent and total -- the game keeps running and nothing ever speaks to the player
        /// again -- while a leaked input disable is a game that no longer responds.
        /// </summary>
        private void OnDisable()
        {
            if (!_retreating) return;
            _retreating = false;
            SetPlayerInput(true);
            StoryInterlude.End();
        }

        /// <summary>
        /// The walk itself: back to <see cref="_approachFrom"/>, or failing that straight away
        /// from the line far enough that the keeper re-arms.
        ///
        /// Driven by Warp rather than by feeding locomotion a synthetic stick, for the reason
        /// EpisodeRunner drives scripted walks the same way: the input path is switched off here
        /// deliberately, and a second writer on it is the class of bug this change exists to
        /// remove. Each step is seated with the locomotion's own standing test, so the walk
        /// cannot finish inside a fence.
        /// </summary>
        private IEnumerator WalkBack()
        {
            var target = RetreatTarget();
            var spent = 0f;

            while (spent < RetreatCeiling)
            {
                spent += Time.deltaTime;

                var here = _player.position;
                var flat = new Vector3(target.x - here.x, 0f, target.z - here.z);
                var remaining = flat.magnitude;
                if (remaining <= RetreatArrive) break;

                var heading = flat / remaining;
                var step = here + heading * Mathf.Min(RetreatSpeed * Time.deltaTime, remaining);

                if (_playerLocomotion != null)
                {
                    if (_playerLocomotion.TryResolveStandingPosition(step, out var seated)) step = seated;
                    _playerLocomotion.Warp(step, Quaternion.LookRotation(heading));
                }
                else
                {
                    _player.position = step;
                    _player.rotation = Quaternion.LookRotation(heading);
                }

                yield return null;
            }

            // Left facing the gate. Turning them back round is the difference between having
            // been sent back and having been dropped.
            var facing = transform.position - _player.position;
            facing.y = 0f;
            if (facing.sqrMagnitude > 1e-4f)
                _player.rotation = Quaternion.LookRotation(facing.normalized);
        }

        /// <summary>
        /// Where the walk ends.
        ///
        /// The remembered approach spot, pushed out along its own bearing when it is nearer than
        /// the keeper's rearm distance -- otherwise the player is set down inside earshot, Speak
        /// arms again the instant the hold lifts, and the refusal becomes a loop they cannot
        /// leave. With nothing remembered -- a save restored on the ramp -- the bearing is simply
        /// back from the gate.
        /// </summary>
        private Vector3 RetreatTarget()
        {
            var clear = _speakRadius + _rearmMargin + RetreatClearance;

            var bearing = _hasApproachFrom
                ? _approachFrom - transform.position
                : _player.position - transform.position;
            bearing.y = 0f;

            if (bearing.sqrMagnitude < 1e-4f) bearing = -transform.forward;

            var distance = Mathf.Max(bearing.magnitude, clear);
            var at = transform.position + bearing.normalized * distance;
            return new Vector3(at.x, _hasApproachFrom ? _approachFrom.y : _player.position.y, at.z);
        }

        private void SetPlayerInput(bool enabled)
        {
            foreach (var reader in FindObjectsByType<OverworldInputReader>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                reader.InputEnabled = enabled;
            }
        }

        private const float RetreatSpeed = 3.2f;
        private const float RetreatArrive = 0.35f;
        private const float RetreatCeiling = 10f;

        /// <summary>Metres past the rearm distance the walk ends, so it ends OUTSIDE it.</summary>
        private const float RetreatClearance = 0.5f;

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.9f, 0.3f, 0.3f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, _speakRadius);

            // The refusal itself: the line the player is held at, drawn where it actually is.
            Gizmos.color = new Color(0.95f, 0.5f, 0.2f, 0.9f);
            var centre = transform.position - transform.forward * _standOff;
            var half = transform.right * (_width * 0.5f);
            var top = transform.up * _heightAbove;
            var bottom = -transform.up * _heightBelow;
            Gizmos.DrawLine(centre - half + bottom, centre + half + bottom);
            Gizmos.DrawLine(centre - half + top, centre + half + top);
            Gizmos.DrawLine(centre - half + bottom, centre - half + top);
            Gizmos.DrawLine(centre + half + bottom, centre + half + top);
        }
    }
}
