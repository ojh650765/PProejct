using System.Collections;
using PokeLab.Core;
using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Where somebody is, and whether they are anywhere at all, according to the story flags —
    /// and not according to what happened to be true in this session's memory.
    ///
    /// <b>The bug this exists for.</b> <see cref="EpisodeRunner"/> takes an actor off the screen
    /// by switching their renderers and colliders off and recording what it switched, in a
    /// dictionary on the runner instance. That is exactly right for the length of a scene and
    /// worthless for anything longer: the dictionary is in memory, the flags are in the save,
    /// and the two part company the first time the scene is reloaded. Kes runs into Aster
    /// Grotto at the end of <c>field_professor_returns</c>, the player whites out ten minutes
    /// later, the whiteout puts them back in the town — and there he is, standing in the
    /// square, because the Town scene's copy of him was rebuilt from the .unity file, where he
    /// has always been standing in the square. 캐시는 분명히 동굴에서 보자고 하고 갔는데.
    ///
    /// The fix is to make the flags the authority. This component is asked, at every load and
    /// whenever the profile changes, one question: given the flags, where is this object and is
    /// it drawn? It answers in three states, most progressed first:
    ///
    /// <list type="bullet">
    /// <item><b>Gone</b> — <c>_leavesOnFlag</c> is set. Not drawn, not solid, not interactable.</item>
    /// <item><b>At the post</b> — <c>_movesOnFlag</c> is set and a post was configured. Drawn,
    /// standing at the post, held so the daily routine does not walk them home.</item>
    /// <item><b>Home</b> — neither. Drawn, wherever the scene put them.</item>
    /// </list>
    ///
    /// <b>Renderers and colliders, never the GameObject.</b> Everything that looks an actor up
    /// does it through <c>GameObject.Find</c>, which does not see an inactive object — so
    /// deactivating them would make the episode that wants them report that they are not in the
    /// scene at all. Same rule, and the same reason, as <c>EpisodeRunner.HideActor</c>.
    ///
    /// <b>It asserts, rather than toggling once.</b> The runner hides an actor at the end of an
    /// exit beat and never puts them back, so "the flags say he is standing at the bag" has to
    /// be able to overrule a hide this component did not perform. What is restored is the state
    /// the scene was authored with, captured in Awake — not "everything found", which would
    /// switch on something another system had deliberately switched off.
    ///
    /// <b>It never argues with a scene in progress.</b> Every assert and every hide stands down
    /// while an episode is playing. Without that, <c>kes_summons</c>' own exit beat — which
    /// hides Kes as he leaves, before his completion flag is set — would be undone on the next
    /// frame by a component that still believed he was a townsperson.
    ///
    /// <b>Nothing is removed in view.</b> A prop that blinks out while the player is looking at
    /// it is a bug on screen even when the state behind it is right, so a hide also waits until
    /// no renderer of this object has been drawn by a camera for a moment. <c>_hidesImmediately</c>
    /// opts out, for objects whose departure is covered by a fade or which the player has not
    /// seen yet this session.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StoryPresence : MonoBehaviour
    {
        [Tooltip("Once this flag is set the object is not in the world at all. Empty means it " +
                 "never leaves.")]
        [SerializeField] private string _leavesOnFlag = "";

        [Tooltip("Once this flag is set the object stands at the post below instead of where " +
                 "the scene built it. Empty means it never moves.")]
        [SerializeField] private string _movesOnFlag = "";

        [Tooltip("World position of the post. Only read when _hasPost is on — a zeroed vector " +
                 "is a legitimate coordinate and must not be confused with 'no post'.")]
        [SerializeField] private Vector3 _post;

        [SerializeField] private bool _hasPost;

        [Tooltip("Skip the out-of-view wait and hide on the spot. For objects whose departure " +
                 "is covered by a fade, or which the player has not seen yet this session.")]
        [SerializeField] private bool _hidesImmediately;

        private enum Presence { Home, AtPost, Gone }

        private PlayerProfile _profile;
        private NpcController _npc;
        private NavMeshAgent _agent;

        private Renderer[] _renderers;
        private bool[] _rendererAuthored;
        private Collider[] _colliders;
        private bool[] _colliderAuthored;

        private Presence _want = Presence.Home;
        private bool _hidden;
        private bool _placed;
        private float _outOfViewFor;


        /// <summary>Set by the level builder. These scenes are generated; nothing here is hand-placed.</summary>
        public void Configure(string leavesOnFlag, string movesOnFlag, Vector3 post, bool hasPost,
                              bool hidesImmediately)
        {
            _leavesOnFlag = leavesOnFlag ?? "";
            _movesOnFlag = movesOnFlag ?? "";
            _post = post;
            _hasPost = hasPost;
            _hidesImmediately = hidesImmediately;
        }

        private void Awake()
        {
            _npc = GetComponent<NpcController>();
            _agent = GetComponent<NavMeshAgent>();

            // The authored state, taken before anything has had a chance to change it. This is
            // what "drawn" restores to, so a renderer the scene shipped switched off stays off.
            _renderers = GetComponentsInChildren<Renderer>(true);
            _rendererAuthored = new bool[_renderers.Length];
            for (var i = 0; i < _renderers.Length; i++)
                _rendererAuthored[i] = _renderers[i] != null && _renderers[i].enabled;

            _colliders = GetComponentsInChildren<Collider>(true);
            _colliderAuthored = new bool[_colliders.Length];
            for (var i = 0; i < _colliders.Length; i++)
                _colliderAuthored[i] = _colliders[i] != null && _colliders[i].enabled;
        }

        private void OnEnable()
        {
            _wokeAt = Time.unscaledTime;
            BindProfile();
            Evaluate();
        }

        private void OnDisable()
        {
            if (_profile != null) _profile.Changed -= Evaluate;
            _profile = null;
        }

        /// <summary>
        /// The profile is registered by a host at execution order -500 and this component lives
        /// on level content, so it is normally there by our OnEnable — but a scene loaded
        /// additively during boot can beat it. Rebound from Update rather than assumed.
        /// </summary>
        private void BindProfile()
        {
            if (_profile != null) return;
            if (!ServiceHub.TryGet<IPlayerProfile>(out var found)) return;
            _profile = found as PlayerProfile;
            if (_profile == null) return;
            _profile.Changed += Evaluate;
        }

        private void Update()
        {
            if (_profile == null)
            {
                BindProfile();
                if (_profile != null) Evaluate();
                return;
            }

            // Never over a scene. A beat that is still running may be the one this object is
            // in: kes_summons hides Kes on his way out of town a moment before setting the flag
            // that says he has gone, and an episode chain hands from one link to the next with
            // the first link's flag already set, which is the seam the bag falls through.
            var runner = EpisodeRunner.Live;
            if (runner != null && runner.IsPlaying) { _outOfViewFor = 0f; return; }

            if (_want == Presence.Gone) { ServeHide(); return; }

            AssertDrawn();
            if (_want == Presence.AtPost) HoldAtPost();
        }

        /// <summary>
        /// True when the leave flag was already set when this object arrived — as opposed to
        /// flipping later, under the player's eyes.
        ///
        /// <b>It is a time window and not "the first Evaluate", and that distinction is a bug
        /// this already had.</b> <c>PlayerProfileHost</c> REGISTERS the profile in Awake and
        /// LOADS THE SAVE IN START, so a component in the initial scene binds to a profile
        /// that is real but still empty, reads every flag as false, and concludes the actor is
        /// at home. The flags arrive a frame later through <c>Changed</c> — correct, but by
        /// then a "first read" test has already been spent. Verified in play: with
        /// story.pokedex set, NPC_Rival logged
        /// <c>leaves='story.pokedex'=False moves='story.kes_summons_done'=False -&gt; Home</c>
        /// while Prop_ProfessorBag, which lives in the additively streamed Field scene and
        /// therefore woke AFTER the load, correctly read its flag as set. Same profile, same
        /// save, opposite answers, purely from which scene each was in.
        ///
        /// A short grace from this component waking covers every version of that race — the
        /// host's Start, an additive band arriving, a scene reload after a whiteout — without
        /// needing to know which one happened.
        /// </summary>
        private bool _goneOnArrival;

        /// <summary>Seconds after waking within which a set leave flag counts as "already gone".</summary>
        private const float ArrivalGraceSeconds = 3f;

        private float _wokeAt;

        /// <summary>Reads the flags and records what they mean. Update makes it so.</summary>
        private void Evaluate()
        {
            if (_profile == null) return;

            if (IsSet(_leavesOnFlag))
            {
                if (Time.unscaledTime - _wokeAt <= ArrivalGraceSeconds) _goneOnArrival = true;
                _want = Presence.Gone;
                return;
            }

            if (_hasPost && IsSet(_movesOnFlag))
            {
                _want = Presence.AtPost;
                PlaceAtPost();
                return;
            }

            _want = Presence.Home;
        }

        private bool IsSet(string flag) =>
            !string.IsNullOrEmpty(flag) && _profile != null && _profile.GetFlagBool(flag);

        // --- Drawn or not ------------------------------------------------------------------

        private const float OutOfViewSeconds = 0.35f;

        private void ServeHide()
        {
            if (_hidden) return;

            // Gone before the player ever saw this scene: nothing to pop, so nothing to wait
            // for. Only a departure that happens under the player's eyes is deferred.
            var atOnce = _hidesImmediately || _goneOnArrival;

            if (!atOnce && AnyRendererVisible())
            {
                _outOfViewFor = 0f;
                return;
            }

            _outOfViewFor += Time.deltaTime;
            if (!atOnce && _outOfViewFor < OutOfViewSeconds) return;

            _hidden = true;
            for (var i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].enabled = false;
            for (var i = 0; i < _colliders.Length; i++)
                if (_colliders[i] != null) _colliders[i].enabled = false;

            Debug.Log($"[Presence] '{name}' is out of the world: {_leavesOnFlag} is set.", this);
        }

        private bool AnyRendererVisible()
        {
            for (var i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r != null && r.enabled && r.isVisible) return true;
            }
            return false;
        }

        /// <summary>
        /// Puts the object back to the state the scene shipped it in, if something has moved it
        /// off that state. Normally a no-op; the one caller that makes it necessary is the
        /// runner's exit beat, which hides an actor and never puts them back.
        /// </summary>
        private void AssertDrawn()
        {
            _hidden = false;
            _outOfViewFor = 0f;

            for (var i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r != null && r.enabled != _rendererAuthored[i]) r.enabled = _rendererAuthored[i];
            }

            for (var i = 0; i < _colliders.Length; i++)
            {
                var c = _colliders[i];
                if (c != null && c.enabled != _colliderAuthored[i]) c.enabled = _colliderAuthored[i];
            }
        }

        // --- Standing somewhere else -------------------------------------------------------

        /// <summary>
        /// Moves the body to the post and holds it there.
        ///
        /// The post is normally in the OTHER band — Kes lives in Town.unity and his post after
        /// the send-off is beside the professor's bag, forty metres into Field.unity. Both bands
        /// are loaded into one world space, so the coordinate is simply valid; what is not
        /// guaranteed is that the far band's navmesh has finished loading when this runs, and an
        /// agent warped where there is no mesh reports itself off-mesh forever. So the warp is
        /// retried from a coroutine until it lands.
        /// </summary>
        private void PlaceAtPost()
        {
            if (_placed) return;
            _placed = true;
            StartCoroutine(SettleOnPost());
        }

        private IEnumerator SettleOnPost()
        {
            // Held first, and unconditionally: NpcController picks a fresh destination inside
            // its waypoint's drift radius the moment it believes it has arrived, and Kes'
            // waypoint is the town square. Without the hold he sets off back across the map.
            if (_npc != null) _npc.ScriptedHold = true;

            var target = SeatOnGround(_post);

            for (var attempt = 0; attempt < 240; attempt++)
            {
                if (_agent == null || !_agent.enabled)
                {
                    transform.position = target;
                    yield break;
                }

                if (NavMesh.SamplePosition(target, out var hit, 4f, NavMesh.AllAreas))
                {
                    _agent.Warp(hit.position);
                    _agent.isStopped = true;
                    _agent.ResetPath();
                    _agent.velocity = Vector3.zero;
                    Debug.Log($"[Presence] '{name}' stands at its post {hit.position} because " +
                              $"{_movesOnFlag} is set; it took {attempt + 1} frame(s) for the " +
                              "navmesh under it to be there.", this);
                    yield break;
                }

                // The far band has not arrived yet. Stand on the authored coordinate meanwhile,
                // so a player who somehow gets there early sees them in roughly the right place
                // rather than at the world origin.
                transform.position = target;
                yield return null;
            }

            Debug.LogWarning($"[Presence] '{name}' could not find navmesh under its post at " +
                             $"{_post}; it is standing on the authored coordinate instead.", this);
        }

        /// <summary>Keeps a relocated actor on their post against the routine that wants them home.</summary>
        private void HoldAtPost()
        {
            if (_npc != null && !_npc.ScriptedHold) _npc.ScriptedHold = true;
        }

        private static Vector3 SeatOnGround(Vector3 point)
        {
            var from = point + Vector3.up * 6f;
            return Physics.Raycast(from, Vector3.down, out var hit, 24f,
                       ~0, QueryTriggerInteraction.Ignore)
                ? hit.point
                : point;
        }

        private void OnDrawGizmosSelected()
        {
            if (!_hasPost) return;
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.8f);
            Gizmos.DrawWireSphere(_post, 0.5f);
            Gizmos.DrawLine(transform.position, _post);
        }
    }
}
