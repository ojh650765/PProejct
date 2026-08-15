using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The scene-side creature: the concrete <see cref="ICreatureView"/> the battle presenter
    /// drives.
    ///
    /// Owns a three-level hierarchy, and the split is load-bearing:
    /// <code>
    /// CreatureView (root)   — position on the mark, facing. Written by the stage and by FaceTowards.
    ///   MotionRoot          — procedural offsets. Written only by CreatureMotionLayer.
    ///     Model             — the registry prefab. Written only by the Animator.
    ///       Anchor_Head / Anchor_Body / Anchor_Muzzle
    /// </code>
    /// Collapsing any two of those levels means two systems writing one transform, which is
    /// how creatures end up sliding off their marks or snapping back mid-animation.
    ///
    /// Every path here survives a missing registry, a missing prefab, a missing Animator and
    /// missing anchors, because during partial integration all four are missing at once and
    /// the battle still has to be reviewable.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public sealed class CreatureView : MonoBehaviour, ICreatureView
    {
        [Header("Anchors (optional — resolved by name when left empty)")]
        [SerializeField] private Transform headAnchor;
        [SerializeField] private Transform bodyAnchor;
        [SerializeField] private Transform muzzleAnchor;

        [Header("Fallback")]
        [Tooltip("Spawn a stand-in body when the art registry has no prefab. Turn this off " +
                 "for review builds — it is a staging aid, not shippable art.")]
        [SerializeField] private bool spawnPlaceholderWhenMissing = true;
        [Tooltip("Display height used when no registry is available, in metres.")]
        [SerializeField] private float fallbackDisplayHeight = 0.8f;

        [Header("Turning")]
        [Tooltip("Degrees per second cap on FaceTowards. Prevents a large turn finishing instantly.")]
        [SerializeField] private float maxTurnSpeed = 540f;

        // --- Anchor names, from Docs/CONTRACTS.md. Do not localise or rename. ---
        private const string HeadAnchorName = "Anchor_Head";
        private const string BodyAnchorName = "Anchor_Body";
        private const string MuzzleAnchorName = "Anchor_Muzzle";

        private readonly List<Transform> _syntheticAnchors = new List<Transform>();
        private Transform _motionRoot;
        private GameObject _model;
        private Animator _animator;
        private CreatureMotionLayer _motion;
        private Coroutine _turn;
        private CreatureAnimation _currentAnimation = CreatureAnimation.Idle;
        private bool _built;

        /// <summary>The creature currently bound, or null.</summary>
        public CreatureInstance Creature { get; private set; }

        /// <summary>Display height in metres, from the registry when available.</summary>
        public float DisplayHeight { get; private set; } = 0.8f;

        /// <summary>The procedural motion layer. The presenter drives beats through this.</summary>
        public CreatureMotionLayer Motion
        {
            get { EnsureBuilt(); return _motion; }
        }

        /// <summary>The animation state currently requested.</summary>
        public CreatureAnimation CurrentAnimation => _currentAnimation;

        // --- ICreatureView ----------------------------------------------------------------

        /// <inheritdoc />
        public Transform Root => transform;

        /// <inheritdoc />
        public Transform HeadAnchor { get { EnsureBuilt(); return headAnchor; } }

        /// <inheritdoc />
        public Transform BodyAnchor { get { EnsureBuilt(); return bodyAnchor; } }

        /// <inheritdoc />
        public Transform MuzzleAnchor { get { EnsureBuilt(); return muzzleAnchor; } }

        private void Awake() => EnsureBuilt();

        private void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            _motionRoot = transform.Find("MotionRoot");
            if (_motionRoot == null)
            {
                var go = new GameObject("MotionRoot");
                _motionRoot = go.transform;
                _motionRoot.SetParent(transform, false);
            }

            _motion = _motionRoot.GetComponent<CreatureMotionLayer>();
            if (_motion == null) _motion = _motionRoot.gameObject.AddComponent<CreatureMotionLayer>();
            _motion.DisplayHeight = DisplayHeight;

            ResolveAnchors();
        }

        /// <inheritdoc />
        public void Bind(CreatureInstance creature)
        {
            EnsureBuilt();
            Creature = creature;

            int speciesId = creature?.SpeciesId ?? 0;
            float registryHeight = ResolveDisplayHeight(speciesId);
            DisplayHeight = registryHeight > 0.01f ? registryHeight : fallbackDisplayHeight;
            _motion.DisplayHeight = DisplayHeight;
            _motion.ResetLayer();

            SwapModel(speciesId);
            ResolveAnchors();

            name = creature == null
                ? "CreatureView (empty)"
                : $"CreatureView {speciesId} {(string.IsNullOrEmpty(creature.Nickname) ? "" : creature.Nickname)}".TrimEnd();

            // Battle idle rather than overworld idle: the view is only ever bound for battle,
            // and starting on the wrong idle produces a visible correction one frame later.
            Play(CreatureAnimation.IdleBattle, 0f);
        }

        /// <inheritdoc />
        public void Play(CreatureAnimation animation, float crossfade = 0.15f)
        {
            EnsureBuilt();
            _currentAnimation = animation;
            if (_animator == null || _animator.runtimeAnimatorController == null) return;

            // A negative or unspecified crossfade takes the authored default for this state.
            float blend = crossfade < 0f ? DefaultCrossfade(animation) : crossfade;

            int hash = Animator.StringToHash(animation.ToString());
            if (!_animator.HasState(0, hash))
            {
                // A stub rig may not have every clip yet. Falling back to the battle idle is
                // better than an exception spam loop, and the procedural layer still carries
                // the beat, so the choreography remains readable.
                int idle = Animator.StringToHash(CreatureAnimation.IdleBattle.ToString());
                if (!_animator.HasState(0, idle)) return;
                hash = idle;
            }

            // CrossFadeInFixedTime, not CrossFade: the normalised-time overload scales the
            // blend by the destination clip's length, so the same value produces a different
            // blend for a 0.4 s attack and a 2 s idle. Fixed time keeps the feel consistent.
            _animator.CrossFadeInFixedTime(hash, blend, 0);
        }

        /// <summary>
        /// Plays a state using the authored default crossfade from
        /// <see cref="DefaultCrossfade"/>. Named rather than overloaded because an overload
        /// taking one argument would silently shadow the interface's defaulted parameter,
        /// and callers holding an <see cref="ICreatureView"/> would get different timing from
        /// callers holding a <see cref="CreatureView"/>.
        /// </summary>
        public void PlayAuthored(CreatureAnimation animation) => Play(animation, -1f);

        /// <summary>
        /// Plays a one-shot state and returns to the battle idle after
        /// <paramref name="hold"/> seconds, so no beat can leave the creature stuck in an
        /// attack pose when the next event arrives.
        /// </summary>
        public IEnumerator PlayThenIdle(CreatureAnimation animation, float hold)
        {
            PlayAuthored(animation);
            yield return CinematicRunner.Wait(hold);
            PlayAuthored(CreatureAnimation.IdleBattle);
        }

        /// <inheritdoc />
        public void FaceTowards(Vector3 worldPoint, float duration = 0.3f)
        {
            EnsureBuilt();

            Vector3 flat = worldPoint - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-5f) return;

            // Contract: models face +Z. LookRotation with world up gives exactly that.
            Quaternion target = Quaternion.LookRotation(flat.normalized, Vector3.up);

            if (_turn != null) CinematicRunner.Halt(_turn);
            _turn = CinematicRunner.Run(TurnRoutine(target, duration));
        }

        /// <summary>Awaitable form of <see cref="FaceTowards"/>, for beats that must not start until the turn lands.</summary>
        public IEnumerator FaceTowardsAndWait(Vector3 worldPoint, float duration = 0.3f)
        {
            FaceTowards(worldPoint, duration);
            yield return _turn;
        }

        private IEnumerator TurnRoutine(Quaternion target, float duration)
        {
            Quaternion start = transform.rotation;
            float delta = Quaternion.Angle(start, target);

            // Two guards, both of which have to be here or FaceTowards stops being smooth:
            // a floor so a tiny correction is not a single-frame snap, and a speed cap so a
            // 180-degree turn is not crammed into the same duration as a 5-degree one.
            float minDuration = delta / Mathf.Max(1f, maxTurnSpeed);
            float d = Mathf.Max(Mathf.Max(duration, minDuration), 0.08f);

            yield return CinematicRunner.Tween(d, CinematicEase.InOutCubic,
                p => transform.rotation = Quaternion.Slerp(start, target, p));

            transform.rotation = target;
            _turn = null;
        }

        // --- Model resolution ---------------------------------------------------------------

        private static float ResolveDisplayHeight(int speciesId)
        {
            if (ServiceHub.TryGet<ICreatureArtRegistry>(out var registry) && registry != null)
            {
                float h = registry.GetDisplayHeight(speciesId);
                if (h > 0.01f && !float.IsNaN(h)) return h;
            }
            return 0f; // caller substitutes the serialized fallback
        }

        private void SwapModel(int speciesId)
        {
            Transform oldModel = _model != null ? _model.transform : null;

            // Anchors belonging to the outgoing model, and anchors we synthesised for it,
            // must both be dropped. The synthetic ones are not children of the model, so
            // destroying the model does not take them with it — left in place they would
            // shadow the real anchors on the incoming rig and the new creature would be
            // framed and shot at using the previous creature's proportions.
            // Anchors assigned by hand in the inspector and living outside the model are
            // deliberately left alone.
            headAnchor = KeepAnchor(headAnchor, oldModel);
            bodyAnchor = KeepAnchor(bodyAnchor, oldModel);
            muzzleAnchor = KeepAnchor(muzzleAnchor, oldModel);

            for (int i = 0; i < _syntheticAnchors.Count; i++)
            {
                if (_syntheticAnchors[i] != null) Destroy(_syntheticAnchors[i].gameObject);
            }
            _syntheticAnchors.Clear();

            if (_model != null)
            {
                Destroy(_model);
                _model = null;
                _animator = null;
            }

            GameObject prefab = null;
            if (ServiceHub.TryGet<ICreatureArtRegistry>(out var registry) && registry != null)
            {
                prefab = registry.GetCreaturePrefab(speciesId);
            }

            if (prefab != null)
            {
                _model = Instantiate(prefab, _motionRoot);
                _model.transform.localPosition = Vector3.zero;
                _model.transform.localRotation = Quaternion.identity;
                _model.transform.localScale = Vector3.one;
                _model.name = "Model";
            }
            else if (spawnPlaceholderWhenMissing)
            {
                _model = BuildPlaceholder(DisplayHeight > 0.01f ? DisplayHeight : fallbackDisplayHeight);
                _model.transform.SetParent(_motionRoot, false);
            }

            if (DisplayHeight <= 0.01f) DisplayHeight = fallbackDisplayHeight;
            _motion.DisplayHeight = DisplayHeight;

            _animator = _model != null ? _model.GetComponentInChildren<Animator>() : null;
            if (_animator != null)
            {
                // Always animate, even when the view is off screen behind a punch-in: culling
                // an animator mid-beat freezes the creature at whatever pose it held.
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.applyRootMotion = false; // the motion layer owns translation
            }
        }

        /// <summary>
        /// Resolves the three contract anchors by name, deepest-first search.
        ///
        /// Missing anchors are synthesised from the display height rather than left null:
        /// the camera rig, the projectile system and the VFX layer all dereference these,
        /// and a null anchor turns a missing art asset into a crash three systems away.
        /// </summary>
        private void ResolveAnchors()
        {
            Transform searchRoot = _model != null ? _model.transform : _motionRoot;

            if (headAnchor == null) headAnchor = FindDeep(searchRoot, HeadAnchorName);
            if (bodyAnchor == null) bodyAnchor = FindDeep(searchRoot, BodyAnchorName);
            if (muzzleAnchor == null) muzzleAnchor = FindDeep(searchRoot, MuzzleAnchorName);

            float h = DisplayHeight > 0.01f ? DisplayHeight : fallbackDisplayHeight;
            if (bodyAnchor == null) bodyAnchor = MakeAnchor(BodyAnchorName + " (synthetic)", new Vector3(0f, h * 0.55f, 0f));
            if (headAnchor == null) headAnchor = MakeAnchor(HeadAnchorName + " (synthetic)", new Vector3(0f, h, 0f));
            // Muzzle sits forward of the body so projectiles do not spawn inside the model.
            if (muzzleAnchor == null) muzzleAnchor = MakeAnchor(MuzzleAnchorName + " (synthetic)", new Vector3(0f, h * 0.6f, h * 0.35f));
        }

        /// <summary>Returns the anchor if it should survive a model swap, otherwise null.</summary>
        private Transform KeepAnchor(Transform anchor, Transform oldModel)
        {
            if (anchor == null) return null;
            if (_syntheticAnchors.Contains(anchor)) return null;
            if (oldModel != null && anchor.IsChildOf(oldModel)) return null;
            return anchor;
        }

        private Transform MakeAnchor(string anchorName, Vector3 localPosition)
        {
            var go = new GameObject(anchorName);
            go.transform.SetParent(_motionRoot != null ? _motionRoot : transform, false);
            go.transform.localPosition = localPosition;
            _syntheticAnchors.Add(go.transform);
            return go.transform;
        }

        private static Transform FindDeep(Transform root, string targetName)
        {
            if (root == null) return null;
            if (root.name == targetName) return root;

            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), targetName);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// The stand-in body. Deliberately an ellipsoid rather than a cube: it reads as a
        /// creature-shaped volume in a framing test, which is what this is for. It is never
        /// meant to reach a build — see <see cref="spawnPlaceholderWhenMissing"/>.
        /// </summary>
        private static GameObject BuildPlaceholder(float height)
        {
            var root = new GameObject("Model (placeholder)");

            var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.name = "PlaceholderBody";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
            body.transform.localScale = new Vector3(height * 0.62f, height * 0.78f, height * 0.7f);
            DestroyColliderOn(body);

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "PlaceholderHead";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, height * 0.86f, height * 0.06f);
            head.transform.localScale = Vector3.one * (height * 0.44f);
            DestroyColliderOn(head);

            // A nose marks +Z so an orientation bug is visible instead of subtle.
            var snout = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            snout.name = "PlaceholderSnout";
            snout.transform.SetParent(root.transform, false);
            snout.transform.localPosition = new Vector3(0f, height * 0.84f, height * 0.24f);
            snout.transform.localScale = Vector3.one * (height * 0.20f);
            DestroyColliderOn(snout);

            return root;
        }

        private static void DestroyColliderOn(GameObject go)
        {
            // Placeholder geometry must never participate in camera occlusion or physics.
            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
        }

        // --- Animation timing -------------------------------------------------------------

        /// <summary>
        /// Authored crossfade durations, in seconds.
        ///
        /// These are the numbers the "animation snapping" QA item is really about. The rule
        /// applied: reactive states blend fastest because a delayed reaction reads as lag,
        /// attacks blend fast because the wind-up is in the clip, and returns to idle blend
        /// slowest because a fast return to idle is what makes a creature look like it is
        /// popping between poses.
        /// </summary>
        public static float DefaultCrossfade(CreatureAnimation animation)
        {
            switch (animation)
            {
                case CreatureAnimation.Hit: return 0.045f;   // must be on the impact frame
                case CreatureAnimation.Dodge: return 0.05f;  // must beat the incoming attack
                case CreatureAnimation.AttackPhysical: return 0.07f;
                case CreatureAnimation.AttackSpecial: return 0.10f;
                case CreatureAnimation.AttackStatus: return 0.12f;
                case CreatureAnimation.SentOut: return 0.06f; // lands on the burst frame
                case CreatureAnimation.Recalled: return 0.08f;
                case CreatureAnimation.Faint: return 0.13f;   // reads as losing control, not as a cut
                case CreatureAnimation.Run: return 0.16f;
                case CreatureAnimation.Walk: return 0.20f;
                case CreatureAnimation.Celebrate: return 0.22f;
                case CreatureAnimation.IdleBattle: return 0.26f;
                case CreatureAnimation.Idle: return 0.30f;
                case CreatureAnimation.Sleep: return 0.40f;
                default: return 0.18f;
            }
        }

        /// <summary>
        /// State names the Animator Controller must expose, one per <see cref="CreatureAnimation"/>.
        /// Used by the editor controller builder and by integration checks.
        /// </summary>
        public static IEnumerable<string> RequiredStateNames()
        {
            foreach (CreatureAnimation a in System.Enum.GetValues(typeof(CreatureAnimation)))
                yield return a.ToString();
        }

        // --- Visibility ---------------------------------------------------------------------

        /// <summary>
        /// Hides or shows the model without disabling this component.
        ///
        /// Disabling the GameObject would kill the coroutines mid-beat and drop the anchors
        /// out from under the camera rig, so send-out and recall toggle renderers instead.
        /// </summary>
        public void SetModelVisible(bool visible)
        {
            EnsureBuilt();
            if (_model == null) return;
            var renderers = _model.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = visible;
        }
    }
}
