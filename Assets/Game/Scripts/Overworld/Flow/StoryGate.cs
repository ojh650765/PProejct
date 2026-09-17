using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>A story boundary across the road; terrain banks provide its visible edges.</summary>
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
        private bool _spoken;

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
            if (IsOpen()) { _spoken = false; return; }
            Speak();
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

            // Keep the boundary closed across low shoulders and brief airborne steps.
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
