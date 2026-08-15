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
    /// <b>The contract is unchanged.</b> <c>Root</c>, <c>HeadAnchor</c>, <c>BodyAnchor</c>,
    /// <c>MuzzleAnchor</c>, <c>Bind</c>, <c>Play</c> and <c>FaceTowards</c> mean exactly what
    /// they meant when a creature was a rigged mesh, and the VFX, UI and projectile systems
    /// that dereference them did not have to change. What changed is what is underneath.
    ///
    /// Owns a three-level hierarchy, and the split is load-bearing:
    /// <code>
    /// CreatureView (root)   — position on the mark, facing. Written by the stage and by FaceTowards.
    ///   MotionRoot          — procedural offsets. Written only by CreatureMotionLayer.
    ///     Sprite            — the billboard quad. Owns its own facing rotation and squash.
    ///     Anchor_Head / Anchor_Body / Anchor_Muzzle
    /// </code>
    /// Collapsing any two of those levels means two systems writing one transform, which is
    /// how creatures end up sliding off their marks or snapping back mid-animation.
    ///
    /// <b>Three things the pivot moved.</b>
    ///
    /// <list type="number">
    /// <item><b>Anchors are computed, not found.</b> A quad has no skeleton, so the three
    /// contract anchors are derived from the sprite's drawn height and its ground-contact
    /// origin — both of which come from the sprite manifest, because a padded 96×96 frame does
    /// not tell you where the feet are. A hand-authored prefab that <i>does</i> carry named
    /// anchors still wins, so a bespoke creature can override the arithmetic.</item>
    /// <item><b><c>FaceTowards</c> is now a facing <i>selection</i> as well as a turn.</b> The
    /// root still rotates — every other system reads <c>transform.forward</c> and the turn has
    /// to stay smooth and non-instant — but what the player sees change is the sprite: front
    /// or back, plus a horizontal mirror. The mirror is a UV flip in the shader, never a
    /// negative X scale, for the reasons set out on <see cref="CreatureBillboard"/>.</item>
    /// <item><b><c>Play</c> drives sheets where frames exist and transforms where they do
    /// not.</b> Two of the fourteen <see cref="CreatureAnimation"/> states are real authored
    /// frames; the mapping for all fourteen is <see cref="CreatureAnimationPlans"/>. Where a
    /// rigged prefab with an Animator is bound instead of a billboard, the Animator is still
    /// driven by state name, so a 3D actor left in the project keeps working.</item>
    /// </list>
    ///
    /// Every path here survives a missing registry, a missing manifest, a missing texture and
    /// a missing shader, because during partial integration all four are missing at once and
    /// the battle still has to be reviewable.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public sealed class CreatureView : MonoBehaviour, ICreatureView
    {
        [Header("Anchors (optional — computed from the sprite when left empty)")]
        [SerializeField] private Transform headAnchor;
        [SerializeField] private Transform bodyAnchor;
        [SerializeField] private Transform muzzleAnchor;

        [Header("Fallback")]
        [Tooltip("Spawn a stand-in body when neither the art registry nor the sprite manifest " +
                 "resolves anything. Turn this off for review builds — it is a staging aid, not shippable art.")]
        [SerializeField] private bool spawnPlaceholderWhenMissing = true;
        [Tooltip("Display height used when no registry is available, in metres.")]
        [SerializeField] private float fallbackDisplayHeight = 0.8f;

        [Header("Sprite")]
        [Tooltip("How the horizontal mirror is chosen. Off is correct for battle: the official " +
                 "sprites are drawn at the three-quarter the battle layout views them from.")]
        [SerializeField] private MirrorMode mirrorMode = MirrorMode.Off;

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
        private CreatureBillboard _billboard;
        // The quad this view built for itself, as opposed to one supplied by a prefab. Kept
        // across binds: rebuilding it every switch would leak a mesh renderer per swap and
        // throw away the material the first bind resolved a shader for.
        private CreatureBillboard _ownBillboard;
        private Animator _animator;
        private CreatureMotionLayer _motion;
        private Coroutine _turn;
        private CreatureAnimation _currentAnimation = CreatureAnimation.Idle;
        private bool _built;
        private bool _hidden;

        /// <summary>The creature currently bound, or null.</summary>
        public CreatureInstance Creature { get; private set; }

        /// <summary>Display height in metres, from the registry when available.</summary>
        public float DisplayHeight { get; private set; } = 0.8f;

        /// <summary>The procedural motion layer. The presenter drives beats through this.</summary>
        public CreatureMotionLayer Motion
        {
            get { EnsureBuilt(); return _motion; }
        }

        /// <summary>The sprite quad, or null when a rigged prefab is bound instead.</summary>
        public CreatureBillboard Billboard
        {
            get { EnsureBuilt(); return _billboard; }
        }

        /// <summary>The animation state currently requested.</summary>
        public CreatureAnimation CurrentAnimation => _currentAnimation;

        /// <summary>Which authored view is on screen. <c>Back</c> for the player's creature in a battle.</summary>
        public SpriteFacing Facing => _billboard != null ? _billboard.Facing : SpriteFacing.Front;

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
            _motion.SpriteMode = true;

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
            _hidden = false;

            name = creature == null
                ? "CreatureView (empty)"
                : $"CreatureView {speciesId} {(string.IsNullOrEmpty(creature.Nickname) ? "" : creature.Nickname)}".TrimEnd();

            // Battle idle rather than overworld idle: the view is only ever bound for battle,
            // and starting on the wrong idle produces a visible correction one frame later.
            Play(CreatureAnimation.IdleBattle, 0f);
        }

        /// <summary>
        /// Plays an animation state.
        ///
        /// Three things happen, and which of them do anything depends on what art is bound:
        /// the sprite track is applied (which sheet, how fast, frozen or not, entry flash);
        /// the Animator is driven by state name if a rigged prefab supplied one; and the
        /// caller is expected to start the matching procedural recipe on
        /// <see cref="Motion"/>. The recipe is not started here on purpose — a lunge needs a
        /// distance and a recoil needs a severity, and only the presenter knows those.
        /// <see cref="CreatureAnimationPlans"/> names the recipe each state expects.
        /// </summary>
        /// <inheritdoc />
        public void Play(CreatureAnimation animation, float crossfade = 0.15f)
        {
            EnsureBuilt();
            _currentAnimation = animation;

            CreatureAnimationPlan plan = CreatureAnimationPlans.For(animation);

            // A billboard with no resolved texture is a white rectangle, so it stays hidden and
            // the beat is carried by the placeholder volume and the motion layer alone.
            if (_billboard != null && _billboard.HasArt && !_hidden) _billboard.ApplyPlan(plan);

            if (_animator == null || _animator.runtimeAnimatorController == null) return;

            // A negative or unspecified crossfade takes the authored default for this state.
            float blend = crossfade < 0f ? DefaultCrossfade(animation) : crossfade;

            int hash = Animator.StringToHash(animation.ToString());
            if (!_animator.HasState(0, hash))
            {
                // A stub rig may not have every clip. Falling back to the battle idle is
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

        /// <summary>
        /// The arrival beat, dispatched by art form.
        ///
        /// A rigged actor falls from the burst point and compresses on contact, because it has
        /// a volume and gravity is what sells it. A sprite scales in from nothing with an
        /// overshoot, because that is what Gen 1-5 did and because a 96-pixel image dropped
        /// from two metres reads as a sticker being slid down the screen.
        ///
        /// The dispatch lives here rather than in the presenter on purpose: the choreography
        /// is meant to stay art-agnostic, so the presenter asks for "the creature arrives" and
        /// the view decides what that looks like. Deliberately not an iterator method, so the
        /// pre-roll below runs at the call rather than one frame later — otherwise the sprite
        /// is drawn once at full size before the pop starts.
        /// </summary>
        public IEnumerator PlayEntrance(float fromHeight, float fallDuration, float settleDuration)
        {
            EnsureBuilt();
            if (!_motion.SpriteMode) return _motion.Land(fromHeight, fallDuration, settleDuration);

            _motion.PrepareEntrance();
            if (_billboard != null && _billboard.HasArt) _billboard.SetAlpha(0f);
            return _motion.Pop(fallDuration + settleDuration);
        }

        /// <summary>
        /// Turns to face a world point, and with it selects which authored view of the
        /// creature is drawn.
        ///
        /// The root rotation is kept even though a billboard ignores it. Two reasons, both
        /// load-bearing: <c>transform.forward</c> is what the muzzle anchor, the projectile
        /// system and the VFX layer orient against, and the sprite's own front/back choice is
        /// made by comparing that forward with the direction to the camera. Deleting the turn
        /// and swapping the sprite directly would have made the facing correct and everything
        /// that aims off a creature wrong.
        /// </summary>
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

        // --- Per-frame publication -----------------------------------------------------------

        /// <summary>
        /// Hands the motion layer's rotation, scale and opacity to the billboard.
        ///
        /// This bridge exists because a camera-facing quad ignores its transform's rotation and
        /// shears under a parent's non-uniform scale, so the two channels have to be applied in
        /// the quad's own space instead of the motion root's. Without it the dodge roll, the
        /// recoil tilt and every squash in the library would be silent no-ops — which is the
        /// single easiest way to end up with a battle where nothing appears to be animated
        /// while every coroutine reports that it ran.
        /// </summary>
        private void LateUpdate()
        {
            if (_billboard == null || _motion == null || !_billboard.HasArt) return;
            _billboard.SetRoll(_motion.Roll);
            _billboard.SetSquash(_motion.Squash);
            if (!_hidden) _billboard.SetAlpha(_motion.Fade);
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

        private static Sprite ResolvePortrait(int speciesId)
        {
            if (ServiceHub.TryGet<ICreatureArtRegistry>(out var registry) && registry != null)
            {
                return registry.GetPortrait(speciesId);
            }
            return null;
        }

        /// <summary>
        /// Swaps in the art for a species.
        ///
        /// Resolution order, and the order is the design:
        /// <list type="number">
        /// <item>A prefab from <see cref="ICreatureArtRegistry"/>. The shipping path — a
        /// serialized reference is the only thing that reliably survives into a player build,
        /// and the contract already returns a <c>GameObject</c>, so a billboard prefab
        /// satisfies it with no change to <c>Core</c>. A prefab that already carries its own
        /// <see cref="CreatureBillboard"/> or <see cref="Animator"/> is used as authored.</item>
        /// <item>A billboard built here, fed from the sprite manifest. The integration path,
        /// for while the prefabs do not exist.</item>
        /// <item>The registry portrait on that same billboard: one static frame, no back view.
        /// Visibly wrong and still reviewable.</item>
        /// <item>The ellipsoid placeholder. Never meant to reach a build.</item>
        /// </list>
        /// </summary>
        private void SwapModel(int speciesId)
        {
            Transform oldModel = _model != null ? _model.transform : null;

            // Anchors belonging to the outgoing model, and anchors we synthesised for it,
            // must both be dropped. The synthetic ones are not children of the model, so
            // destroying the model does not take them with it — left in place they would
            // shadow the real anchors on the incoming art and the new creature would be framed
            // and shot at using the previous creature's proportions.
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
                _billboard = null;
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
                _billboard = _model.GetComponentInChildren<CreatureBillboard>();
            }

            if (DisplayHeight <= 0.01f) DisplayHeight = fallbackDisplayHeight;

            _animator = _model != null ? _model.GetComponentInChildren<Animator>() : null;
            if (_animator != null)
            {
                // Always animate, even when the view is off screen: culling an animator
                // mid-beat freezes the creature at whatever pose it held.
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.applyRootMotion = false; // the motion layer owns translation
            }

            // A rigged prefab drives its own rotation and scale through the Animator, so the
            // motion layer must go back to writing them. A billboard, or no art at all, keeps
            // sprite mode.
            bool rigged = _animator != null && _billboard == null;
            _motion.SpriteMode = !rigged;

            if (!rigged)
            {
                if (_billboard == null)
                {
                    if (_ownBillboard == null) _ownBillboard = BuildBillboard();
                    _billboard = _ownBillboard;
                }
                if (_ownBillboard != null) _ownBillboard.gameObject.SetActive(_billboard == _ownBillboard);
                _billboard.Mirror = mirrorMode;
                _billboard.Bind(speciesId, DisplayHeight, ResolvePortrait(speciesId));

                if (!_billboard.HasArt)
                {
                    // An untextured quad is a white rectangle, which is worse than the thing
                    // it replaced. Hide it and fall back to the volume stand-in, which at
                    // least reads as a creature-shaped object in a framing test.
                    _billboard.SetVisible(false);
                    if (spawnPlaceholderWhenMissing && _model == null)
                    {
                        _model = BuildPlaceholder(DisplayHeight);
                        _model.transform.SetParent(_motionRoot, false);
                        _motion.SpriteMode = false;
                    }
                }
            }
            else if (_ownBillboard != null)
            {
                // A rigged prefab took over. Park the view's own quad rather than destroying
                // it, so a later switch back to a sprite species does not have to rebuild it.
                _ownBillboard.gameObject.SetActive(false);
            }

            _motion.DisplayHeight = DisplayHeight;
        }

        private CreatureBillboard BuildBillboard()
        {
            var go = new GameObject("Sprite");
            go.transform.SetParent(_motionRoot, false);
            go.layer = gameObject.layer;
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            return go.AddComponent<CreatureBillboard>();
        }

        /// <summary>
        /// Resolves the three contract anchors.
        ///
        /// A billboard has no skeleton, so unless a hand-authored prefab supplies named
        /// anchors these are computed from the sprite's drawn height — the metre height the
        /// registry reports, which the billboard has already mapped onto the frame using the
        /// manifest's content height and ground origin. That last part is why the manifest
        /// carries those two numbers at all: a Gen 5 frame is padded, so the crown of a
        /// creature is not the top of its image and the feet are not the bottom, and an anchor
        /// placed at the frame's extents puts the health bar in empty space.
        ///
        /// Missing anchors are never left null: the camera rig, the projectile system and the
        /// VFX layer all dereference these, and a null anchor turns a missing art asset into a
        /// crash three systems away.
        /// </summary>
        private void ResolveAnchors()
        {
            Transform searchRoot = _model != null ? _model.transform : _motionRoot;

            if (headAnchor == null) headAnchor = FindDeep(searchRoot, HeadAnchorName);
            if (bodyAnchor == null) bodyAnchor = FindDeep(searchRoot, BodyAnchorName);
            if (muzzleAnchor == null) muzzleAnchor = FindDeep(searchRoot, MuzzleAnchorName);

            float h = DisplayHeight > 0.01f ? DisplayHeight : fallbackDisplayHeight;
            if (bodyAnchor == null) bodyAnchor = MakeAnchor(BodyAnchorName + " (computed)", new Vector3(0f, h * 0.52f, 0f));
            if (headAnchor == null) headAnchor = MakeAnchor(HeadAnchorName + " (computed)", new Vector3(0f, h * 0.96f, 0f));
            // The muzzle sits forward of the body so projectiles do not spawn inside the
            // sprite. On a flat subject "forward" is the creature's facing, which is why this
            // hangs off the motion root and not off the quad — the quad is turned to the
            // camera and has no forward of its own.
            if (muzzleAnchor == null) muzzleAnchor = MakeAnchor(MuzzleAnchorName + " (computed)", new Vector3(0f, h * 0.58f, h * 0.30f));
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
        /// The stand-in body, used only when neither a prefab nor the sprite manifest resolved
        /// anything. Deliberately an ellipsoid rather than a cube: it reads as a
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
        /// attacks blend fast because the wind-up is in the motion, and returns to idle blend
        /// slowest because a fast return to idle is what makes a creature look like it is
        /// popping between poses.
        ///
        /// Kept as a static function, and kept authoritative: it is what the Animator path
        /// uses for a rigged actor and what <see cref="CreatureAnimationPlans"/> mirrors for
        /// the sprite path, so the two forms of creature are timed identically.
        /// </summary>
        public static float DefaultCrossfade(CreatureAnimation animation)
            => CreatureAnimationPlans.For(animation).Crossfade;

        /// <summary>
        /// State names the Animator Controller must expose, one per <see cref="CreatureAnimation"/>.
        /// Used by the editor controller builder and by integration checks. Still meaningful
        /// after the pivot: a rigged actor bound through the registry is driven by these names.
        /// </summary>
        public static IEnumerable<string> RequiredStateNames()
        {
            foreach (CreatureAnimation a in System.Enum.GetValues(typeof(CreatureAnimation)))
                yield return a.ToString();
        }

        // --- Visibility ---------------------------------------------------------------------

        /// <summary>
        /// Hides or shows the creature without disabling this component.
        ///
        /// Disabling the GameObject would kill the coroutines mid-beat and drop the anchors
        /// out from under the camera rig, so send-out and recall toggle renderers instead.
        /// </summary>
        public void SetModelVisible(bool visible)
        {
            EnsureBuilt();
            _hidden = !visible;

            if (_billboard != null && _billboard.HasArt)
            {
                _billboard.SetVisible(visible);
                if (visible) _billboard.SetAlpha(Mathf.Max(0.01f, _motion != null ? _motion.Fade : 1f));
            }

            if (_model == null) return;
            var renderers = _model.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                // The billboard owns its own visibility above; toggling it here as well would
                // undo an alpha fade that is still running.
                if (_billboard != null && renderers[i] == _billboard.Renderer) continue;
                renderers[i].enabled = visible;
            }
        }
    }
}
