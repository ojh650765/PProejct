using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>Which authored view of a creature is on screen.</summary>
    public enum SpriteFacing
    {
        /// <summary>Drawn from in front. The opponent's creature, always, in a traditional battle.</summary>
        Front = 0,
        /// <summary>Drawn from behind. The player's creature, always, in a traditional battle.</summary>
        Back = 1,
        /// <summary>Turned so the subject's left side is toward the lens.</summary>
        Left = 2,
        /// <summary>Turned so the subject's right side is toward the lens.</summary>
        Right = 3,
    }

    /// <summary>How the horizontal mirror is chosen.</summary>
    public enum MirrorMode
    {
        /// <summary>
        /// Never mirror. The correct setting for battle: the official sprites are already
        /// drawn at the three-quarter the battle layout views them from, so mirroring one of
        /// the two combatants would turn it away from the fight.
        /// </summary>
        Off = 0,
        /// <summary>
        /// Mirror when the creature is turned far enough across the camera to warrant it, with
        /// a deadzone and hysteresis. For the overworld, where a creature really does face
        /// left and right.
        /// </summary>
        Auto = 1,
        /// <summary>Always mirrored. For a caller that has already made the decision.</summary>
        On = 2,
    }

    /// <summary>
    /// The pixel-art creature: one camera-facing quad, a sprite sheet, and the handful of
    /// per-frame values the sprite shader needs.
    ///
    /// <b>The mirror is a UV flip, never a negative scale.</b> An odd number of negative scale
    /// axes mirrors the object into left-handed space: winding order inverts so backface
    /// culling culls the wrong face, and normals and normal maps flip — a long-standing open
    /// Unity behaviour, unchanged in URP. With a directional key that moves through the day
    /// and synthetic sphere normals on the sprites, a creature that re-lights itself backwards
    /// the instant it turns around is more obviously broken than one with no lighting at all.
    /// So the flip goes through the shader's <c>_FlipX</c> where that exists, and otherwise
    /// through the texture's tiling/offset, which evaluates to <c>uv.x = 1 - uv.x</c> inside
    /// <c>TRANSFORM_TEX</c>. Geometry, winding and normals are untouched either way.
    ///
    /// <b>The quad is oriented on the CPU even when the shader billboards.</b> That is
    /// deliberate and it is not redundant: a shader-side billboard leaves the mesh's real
    /// bounds pointing wherever the transform does, so culling and shadow bounds go wrong; and
    /// orienting first makes the shader's own billboarding idempotent rather than conflicting.
    /// It is also what keeps this component reviewable before
    /// <c>PokeLabSpriteBillboard</c> exists — that shader belongs to the rendering worker and
    /// is referenced here by name only.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class CreatureBillboard : MonoBehaviour
    {
        /// <summary>
        /// The shader property names this component drives. Collected here because they are a
        /// coordination surface with the rendering worker, not an implementation detail — a
        /// rename on either side should be a one-line change found by searching this class.
        /// Every one is written through a <see cref="MaterialPropertyBlock"/>, which ignores
        /// names the bound shader does not declare, so a partial shader is harmless.
        /// </summary>
        public static class ShaderProps
        {
            public const string BaseMap = "_BaseMap";
            public const string BaseMapST = "_BaseMap_ST";
            public const string BaseColor = "_BaseColor";
            public const string FlipX = "_FlipX";
            public const string FlashColor = "_FlashColor";
            public const string FlashAmount = "_FlashAmount";
            public const string Roll = "_Roll";
            public const string ContactFade = "_ContactFade";
        }

        /// <summary>
        /// The billboard shader, owned by the rendering worker. Referenced by name; when it is
        /// absent the fallbacks below are used and everything except the sprite-specific
        /// lighting still works.
        /// </summary>
        private static readonly string[] ShaderCandidates =
        {
            "PokeLab/SpriteBillboard",
            "Shader Graphs/PokeLabSpriteBillboard",
            "Universal Render Pipeline/Unlit",
            "Unlit/Transparent Cutout",
            "Sprites/Default",
        };

        [Header("Facing")]
        [Tooltip("How the horizontal mirror is chosen. Off is correct for battle.")]
        [SerializeField] private MirrorMode mirrorMode = MirrorMode.Off;
        [Tooltip("Dot product the facing must cross before front/back swaps. Keeps a grazing angle " +
                 "from flickering; in the battle layout the real value sits near ±0.85, so this is slack, not a threshold.")]
        [Range(0.02f, 0.5f)]
        [SerializeField] private float facingHysteresis = 0.15f;
        [Tooltip("Lateral component the creature must be turned by before Auto mirroring engages.")]
        [Range(0.1f, 0.9f)]
        [SerializeField] private float mirrorDeadzone = 0.62f;

        [Header("Grounding")]
        [Tooltip("Tilt the quad back by this fraction of the camera's pitch so its bottom edge sits " +
                 "nearer than its top. Standard HD-2D practice: it stops the quad z-fighting the ground " +
                 "along the contact line and stops it going razor-thin seen from above.")]
        [Range(0f, 1f)]
        [SerializeField] private float groundTilt = 0.35f;

        // --- Runtime state ---------------------------------------------------------------

        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;
        private Material _material;
        private Camera _camera;

        private CreatureSpriteEntry _entry;
        private Texture2D _frontTexture;
        private Texture2D _backTexture;
        private SpriteSheetInfo _frontSheet;
        private SpriteSheetInfo _backSheet;

        // Side views are optional. When a creature has none — which is every creature drawn
        // from the official battle sprites — a side sector borrows front or back and mirrors
        // it, and these record that choice so the sheet and the flip stay in agreement.
        private SpriteSheetInfo _sideSheet;
        private Texture2D _sideTexture;
        private bool _sideBorrowsBack;
        private bool _sideFlip;

        private SpriteFacing _facing = SpriteFacing.Front;
        private bool _flip;
        private float _frameTime;
        private int _frameIndex;
        private float _rateScale = 1f;
        private bool _frozen;
        private float _flash;
        private float _alpha = 1f;
        private float _roll;
        private float _displayHeight = 0.8f;
        private bool _built;
        private bool _hasArt;
        private bool _ownsMaterial;
        private Vector3 _baseScale = Vector3.one;
        private Vector3 _squash = Vector3.one;

        private static Mesh s_quad;

        /// <summary>Height of the drawn creature, feet to crown, in metres. Anchors are derived from it.</summary>
        public float DisplayHeight => _displayHeight;

        /// <summary>Which authored view is currently on screen.</summary>
        public SpriteFacing Facing => _facing;

        /// <summary>True when the mirror is engaged. Diagnostic.</summary>
        public bool Mirrored => _flip;

        /// <summary>True when real sprite art was resolved, as opposed to the untextured stand-in.</summary>
        public bool HasArt => _hasArt;

        /// <summary>The renderer, for callers that need to change layers or shadow settings.</summary>
        public MeshRenderer Renderer { get { EnsureBuilt(); return _renderer; } }

        /// <summary>How the horizontal mirror is chosen.</summary>
        public MirrorMode Mirror
        {
            get => mirrorMode;
            set => mirrorMode = value;
        }

        /// <summary>Overrides the camera the quad faces. Left null, <c>Camera.main</c> is used.</summary>
        public void SetCamera(Camera camera) => _camera = camera;

        /// <summary>
        /// Pins which drawn view is shown, overriding the sector test.
        ///
        /// The sector test is right for the overworld, where a creature really does turn through
        /// every angle and the drawn view has to follow it. A battle is not that: the layout is
        /// fixed, the two sides are drawn for it — the player's from behind, the opponent's from
        /// the front — and which one is shown is a decision, not a measurement. Left to the
        /// sector test it is a measurement taken a few degrees from a boundary, and a camera
        /// tuning change several files away silently turns the player's creature round to face
        /// the camera it is supposed to have its back to.
        ///
        /// Null restores the sector test, which is what the victory beat wants: turning the
        /// winner out toward the lens is the one moment the back sprite is meant to give way.
        /// </summary>
        public SpriteFacing? ForcedFacing { get; set; }

        private void Awake() => EnsureBuilt();

        private void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
            _block = new MaterialPropertyBlock();

            if (_filter.sharedMesh == null) _filter.sharedMesh = QuadMesh();

            if (_renderer.sharedMaterial == null)
            {
                _material = new Material(ResolveShader()) { name = "~SpriteBillboard" };
                // Opaque queue with alpha clip, not transparent: transparent objects sort
                // per-object by camera distance and pop in front of and behind foliage cards
                // unpredictably. Opaque plus clip gives correct per-pixel depth for free.
                _material.SetFloat("_Surface", 0f);
                _material.SetFloat("_AlphaClip", 1f);
                _material.SetFloat("_Cutoff", 0.5f);
                _material.EnableKeyword("_ALPHATEST_ON");
                _material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                _renderer.sharedMaterial = _material;
                _ownsMaterial = true;
            }
            else
            {
                // An authored prefab brought its own material. Use it and, critically, do not
                // destroy it later — it is a shared asset, not an instance made here.
                _material = _renderer.sharedMaterial;
                _ownsMaterial = false;
            }

            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _renderer.receiveShadows = true;
        }

        private static Shader ResolveShader()
        {
            for (int i = 0; i < ShaderCandidates.Length; i++)
            {
                var shader = Shader.Find(ShaderCandidates[i]);
                if (shader != null)
                {
                    if (i >= 2)
                    {
                        Debug.LogWarning($"[CreatureBillboard] '{ShaderCandidates[0]}' not found; using " +
                                         $"'{ShaderCandidates[i]}'. Sprites will draw but will not take the " +
                                         "synthetic sphere normals, the light-facing shadow caster or the fog " +
                                         "the art direction calls for. That shader belongs to the rendering worker.");
                    }
                    return shader;
                }
            }
            return Shader.Find("Hidden/InternalErrorShader");
        }

        /// <summary>
        /// A unit quad with the pivot at the bottom centre, so the transform origin is the
        /// creature's ground contact point. Winding and normals match Unity's own Quad, which
        /// faces -Z, so a transform whose forward points away from the camera shows its face.
        /// </summary>
        private static Mesh QuadMesh()
        {
            if (s_quad != null) return s_quad;

            s_quad = new Mesh { name = "~CreatureBillboardQuad", hideFlags = HideFlags.DontSave };
            s_quad.vertices = new[]
            {
                new Vector3(-0.5f, 0f, 0f),
                new Vector3( 0.5f, 0f, 0f),
                new Vector3(-0.5f, 1f, 0f),
                new Vector3( 0.5f, 1f, 0f),
            };
            s_quad.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f),
            };
            s_quad.normals = new[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
            s_quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            s_quad.RecalculateBounds();
            return s_quad;
        }

        // --- Binding -----------------------------------------------------------------------

        /// <summary>
        /// Binds a species' sprite art and sizes the quad so the drawn creature is
        /// <paramref name="displayHeight"/> metres tall with its feet on the transform origin.
        ///
        /// <paramref name="fallbackPortrait"/> is used when the manifest has no entry. It is a
        /// single static frame with no back view, so the creature will show the same picture
        /// from both sides — visibly wrong, and still far better than a grey ellipsoid,
        /// because everything else in the battle stays reviewable around it.
        /// </summary>
        public void Bind(int speciesId, float displayHeight, Sprite fallbackPortrait)
        {
            EnsureBuilt();
            _displayHeight = Mathf.Max(0.05f, displayHeight);

            _entry = CreatureSpriteLibrary.Shared.Entry(speciesId);
            _frontSheet = null;
            _backSheet = null;
            _frontTexture = null;
            _backTexture = null;

            if (_entry != null)
            {
                _frontSheet = _entry.frontAnim != null && _entry.frontAnim.IsUsable ? _entry.frontAnim : null;
                _backSheet = _entry.backAnim != null && _entry.backAnim.IsUsable ? _entry.backAnim : null;

                _frontTexture = CreatureSpriteLibrary.Shared.Texture(_frontSheet?.texture ?? _entry.front);
                _backTexture = CreatureSpriteLibrary.Shared.Texture(_backSheet?.texture ?? _entry.back);

                // A missing back view is survivable and a missing front view is not, so the
                // front stands in for the back rather than the other way round.
                if (_backTexture == null) { _backTexture = _frontTexture; _backSheet = _frontSheet; }
                if (_frontTexture == null) { _frontTexture = _backTexture; _frontSheet = _backSheet; }
            }

            if (_frontTexture == null && fallbackPortrait != null)
            {
                _frontTexture = fallbackPortrait.texture;
                _backTexture = _frontTexture;
            }

            _hasArt = _frontTexture != null;
            _frameIndex = 0;
            _frameTime = 0f;

            ResizeQuad();
            ApplyBlock();
        }

        /// <summary>
        /// Sizes and offsets the quad from the manifest's framing metadata.
        ///
        /// Two numbers do the work and both have to come from the art, not from a guess.
        /// <c>contentHeight</c> is how much of the padded square frame the creature actually
        /// fills, and it is what maps the sprite onto the registry's metre height; without it
        /// a heavily padded Pidgey and a tightly cropped Gastly are drawn at different scales
        /// despite sharing a source resolution. <c>groundOrigin</c> is where the feet sit
        /// inside the frame, and it is what stops the creature floating or sinking.
        /// </summary>
        private void ResizeQuad()
        {
            float contentFraction = _entry != null ? Mathf.Clamp(_entry.contentHeight, 0.05f, 1f) : 0.86f;
            float groundOrigin = _entry != null ? Mathf.Clamp01(_entry.groundOrigin) : 0.07f;

            float frameHeight = _displayHeight / contentFraction;

            float aspect = 1f; // Gen 5 frames are square, and the pivot keeps them square.
            if (_frontTexture != null && _frontTexture.height > 0)
            {
                var sheet = _frontSheet;
                float cellW = _frontTexture.width / (float)Mathf.Max(1, sheet?.columns ?? 1);
                float cellH = _frontTexture.height / (float)Mathf.Max(1, sheet?.rows ?? 1);
                if (cellH > 0.001f) aspect = cellW / cellH;
            }

            _baseScale = new Vector3(frameHeight * aspect, frameHeight, 1f);
            transform.localScale = Vector3.Scale(_baseScale, _squash);
            transform.localPosition = new Vector3(0f, -groundOrigin * frameHeight, 0f);
        }

        /// <summary>
        /// Applies the procedural squash from <see cref="CreatureMotionLayer"/>.
        ///
        /// It lands here rather than on the motion root because a non-uniform scale on a parent
        /// combined with this quad's facing rotation produces a shear — Unity's documented
        /// behaviour, and on a sprite it shows up as the creature skewing as the camera moves.
        /// Applied in the quad's own local space, X is always screen-horizontal and Y always
        /// screen-vertical, which is exactly what a squash-and-stretch is supposed to mean.
        /// </summary>
        public void SetSquash(Vector3 squash)
        {
            _squash = new Vector3(
                Mathf.Max(0.01f, squash.x),
                Mathf.Max(0.01f, squash.y),
                Mathf.Max(0.01f, squash.z));
        }

        // --- Playback ------------------------------------------------------------------------

        /// <summary>
        /// Applies an animation plan: which track to draw, how fast, and the entry flash.
        /// The <i>motion</i> half of a plan belongs to <see cref="CreatureMotionLayer"/>; this
        /// only owns what the sprite itself does.
        /// </summary>
        public void ApplyPlan(CreatureAnimationPlan plan)
        {
            EnsureBuilt();

            switch (plan.Track)
            {
                case SpriteTrack.Hidden:
                    SetVisible(false);
                    return;
                case SpriteTrack.IdleFrozen:
                    _frozen = true;
                    _rateScale = 1f;
                    break;
                default:
                    _frozen = false;
                    _rateScale = Mathf.Max(0.05f, plan.FrameRateScale);
                    break;
            }

            SetVisible(true);
            if (plan.Flash > 0.001f) Flash(plan.Flash);

            // Deliberately not SetAlpha(plan.Alpha). The plan's alpha is where the beat *ends*,
            // not where it starts, and applying it here would make a faint vanish on the frame
            // the collapse begins instead of over the course of it. Opacity has exactly one
            // owner — CreatureMotionLayer.Fade, published every frame by CreatureView.
        }

        /// <summary>Sets the white entry flash. Decays on its own; see <see cref="LateUpdate"/>.</summary>
        public void Flash(float strength) => _flash = Mathf.Clamp01(Mathf.Max(_flash, strength));

        /// <summary>Sets the sprite's alpha. Used by the faint fade and the recall.</summary>
        public void SetAlpha(float alpha) => _alpha = Mathf.Clamp01(alpha);

        /// <summary>
        /// Screen-plane roll in degrees.
        ///
        /// This exists because a billboard discards its transform's rotation for orientation
        /// purposes, so every rotation-based beat in <see cref="CreatureMotionLayer"/> — the
        /// dodge roll, the recoil tilt, the collapse — would otherwise be a silent no-op. The
        /// motion layer publishes its roll and this applies it about the view axis, which is
        /// what those beats meant all along on a flat subject.
        /// </summary>
        public void SetRoll(float degrees) => _roll = degrees;

        /// <summary>Shows or hides the sprite without disabling the object, so coroutines survive.</summary>
        public void SetVisible(bool visible)
        {
            EnsureBuilt();
            _renderer.enabled = visible;
        }

        /// <summary>True when the sprite is being drawn.</summary>
        public bool IsVisible => _renderer != null && _renderer.enabled;

        private void LateUpdate()
        {
            EnsureBuilt();
            AdvanceFrame();
            FaceCamera();
            transform.localScale = Vector3.Scale(_baseScale, _squash);

            // The flash decays fast — it is a single-frame event stretched just far enough to
            // survive a dropped frame. A slow decay reads as the creature glowing.
            if (_flash > 0.001f) _flash = Mathf.Max(0f, _flash - Time.deltaTime * 6f);

            ApplyBlock();
        }

        private void AdvanceFrame()
        {
            var sheet = ActiveSheet();
            if (_frozen || sheet == null || sheet.StepCount <= 1) return;

            // Accumulate real seconds and spend them against each step's own duration, rather
            // than ticking a single frame rate. The source loops are not uniform — a flat rate
            // destroys Pidgey's 1900 ms hold and speeds up the 50 ms flutters.
            _frameTime += Time.deltaTime * Mathf.Max(0.01f, _rateScale);

            int guard = 0;
            while (_frameTime >= sheet.StepSeconds(_frameIndex) && guard++ < 64)
            {
                _frameTime -= sheet.StepSeconds(_frameIndex);
                _frameIndex = (_frameIndex + 1) % sheet.StepCount;
            }
        }

        /// <summary>
        /// Orients the quad and picks between the front and back sprite.
        ///
        /// The facing test is the creature's own forward against the direction to the camera,
        /// not the camera's yaw. That matters: it means the choice survives the camera panning
        /// and dollying, and in the traditional layout it is not a close call — the player's
        /// creature faces its opponent and therefore almost directly away from the lens, and
        /// the opponent faces almost directly into it. The dot sits near ±0.85 either way,
        /// against a ±0.15 hysteresis band, so nothing the rig is allowed to do can flip it.
        /// </summary>
        private void FaceCamera()
        {
            Camera cam = ResolveCamera();
            if (cam == null) return;

            Vector3 toCamera = cam.transform.position - transform.position;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 1e-6f) return;
            toCamera.Normalize();

            Vector3 forward = transform.parent != null ? transform.parent.forward : transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 1e-6f)
            {
                forward.Normalize();
                SelectFacing(forward, toCamera, cam);
            }

            // Face the camera, then tilt back by a fraction of its pitch. The tilt puts the
            // quad's bottom edge nearer than its top, which is what keeps the contact line off
            // the ground plane's depth and stops the sprite thinning out from a high angle.
            Quaternion look = Quaternion.LookRotation(-toCamera, Vector3.up);
            // Degrees the camera is looking down by. A positive rotation about the quad's own
            // right axis then tips its top away from the lens and its bottom toward it.
            float camPitch = Mathf.Asin(Mathf.Clamp(-cam.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            Quaternion tilt = Quaternion.AngleAxis(camPitch * groundTilt, Vector3.right);
            Quaternion roll = Quaternion.AngleAxis(_roll, Vector3.forward);
            transform.rotation = look * tilt * roll;
        }

        /// <summary>
        /// Chooses which drawn view to show, as a discrete function of where the subject is
        /// actually facing relative to the camera.
        ///
        /// The quad always turns to the lens, so on its own it would show the same picture no
        /// matter which way the creature had turned — a character walking east would still be
        /// looking straight out of the screen. Sprite games solve that by swapping the image
        /// rather than rotating the geometry, and that is what this does.
        ///
        /// The angle is bucketed into four sectors rather than tested as a single dot product,
        /// because two views cannot express a side-on stance: without a sector for it, a
        /// creature turning through profile snaps directly from front to back. Sectors are
        /// widened by <see cref="facingHysteresis"/> in whichever one is already held, so a
        /// subject hovering on a boundary does not flutter between two images.
        ///
        /// With only front and back drawn, the side sectors borrow the nearer of the two and
        /// mirror it. That is the classic compromise and it holds up because the official
        /// sprites are drawn at a three-quarter, so they already read as slightly turned.
        /// </summary>
        private void SelectFacing(Vector3 forward, Vector3 toCamera, Camera cam)
        {
            if (ForcedFacing.HasValue)
            {
                _facing = ForcedFacing.Value;
                UpdateMirror(forward, cam);
                return;
            }

            // Signed angle from "facing the lens" — 0 means looking at the camera, ±180 away.
            //
            // +90 is the subject's *left* flank toward the camera, not its right. Worked
            // through: a subject at the origin facing +X seen from +Z gives +90 here,
            // and for forward = (1,0,0) with up = (0,1,0) the transform's right is
            // cross(up, forward) = (0,0,-1) — pointing away from the lens. This read the
            // other way round for a long time, harmlessly, because both side sectors fall
            // back to a mirrored front or back view; it would have surfaced as characters
            // walking backwards the first time a real side sheet was bound.
            float angle = Vector3.SignedAngle(toCamera, forward, Vector3.up);

            // Hold the current sector a little past its true edge.
            float bias = facingHysteresis * 45f;
            float front = 45f + (_facing == SpriteFacing.Front ? bias : -bias);
            float back = 135f - (_facing == SpriteFacing.Back ? bias : -bias);

            float a = Mathf.Abs(angle);
            if (a <= front) _facing = SpriteFacing.Front;
            else if (a >= back) _facing = SpriteFacing.Back;
            else _facing = angle > 0f ? SpriteFacing.Left : SpriteFacing.Right;

            // A side sector with no drawn side view falls back to whichever of front and back
            // it is nearer to, so the creature at least faces the correct half of the world.
            if (_facing == SpriteFacing.Left || _facing == SpriteFacing.Right)
            {
                bool haveSide = SideSheet(_facing) != null;
                if (!haveSide)
                {
                    _sideBorrowsBack = a > 90f;
                    // Side sheets are drawn with the subject's left flank to the lens —
                    // the Gen 4 field sprites walk screen-left as authored — so the right
                    // facing is the one that needs mirroring.
                    //
                    // Inverted again when the borrowed sheet is the back one, because a view
                    // from behind reverses the subject's left and right: the flip that turns a
                    // borrowed front view toward the opponent turns a borrowed back view away
                    // from it. Left uncorrected this was half of what made the two combatants
                    // face the same way.
                    _sideFlip = (_facing == SpriteFacing.Right) ^ _sideBorrowsBack;
                }
                else
                {
                    _sideBorrowsBack = false;
                    _sideFlip = _facing == SpriteFacing.Left && SideSheet(SpriteFacing.Left) == SideSheet(SpriteFacing.Right);
                }
            }

            UpdateMirror(forward, cam);
        }

        private void UpdateMirror(Vector3 forward, Camera cam)
        {
            switch (mirrorMode)
            {
                case MirrorMode.Off: _flip = false; return;
                case MirrorMode.On: _flip = true; return;
            }

            Vector3 right = cam.transform.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-6f) return;
            right.Normalize();

            float lateral = Vector3.Dot(forward, right);
            // The deadzone is wide on purpose. The official sprites are drawn at a
            // three-quarter, so a creature standing at the layout's own yaw is already
            // depicted correctly and mirroring it would turn it away from the fight. Only a
            // genuine turn across the camera should engage the mirror.
            if (!_flip && lateral < -mirrorDeadzone) _flip = true;
            else if (_flip && lateral > -mirrorDeadzone * 0.6f) _flip = false;
        }

        private Camera ResolveCamera()
        {
            if (_camera != null && _camera.isActiveAndEnabled) return _camera;
            _camera = Camera.main;
            return _camera;
        }

        /// <summary>The drawn side view, or null when the creature has none.</summary>
        private SpriteSheetInfo SideSheet(SpriteFacing facing) => _sideSheet;

        private bool IsSideSector => _facing == SpriteFacing.Left || _facing == SpriteFacing.Right;

        private SpriteSheetInfo ActiveSheet()
        {
            if (IsSideSector)
            {
                if (_sideSheet != null) return _sideSheet;
                return _sideBorrowsBack ? _backSheet : _frontSheet;
            }
            return _facing == SpriteFacing.Back ? _backSheet : _frontSheet;
        }

        private Texture2D ActiveTexture()
        {
            if (IsSideSector)
            {
                if (_sideTexture != null) return _sideTexture;
                return _sideBorrowsBack ? _backTexture : _frontTexture;
            }
            return _facing == SpriteFacing.Back ? _backTexture : _frontTexture;
        }

        /// <summary>
        /// The mirror actually applied: the authored mode's choice, flipped again when a side
        /// sector is borrowing the opposite-handed view. Two flips cancel, which is correct —
        /// a left-facing subject borrowing an already-mirrored front should end up unmirrored.
        ///
        /// <see cref="MirrorMode.Off"/> stops the borrow flip as well, and that is the point of
        /// it. The mode's whole promise is "never mirror", and until this read the mode the
        /// side-sector fallback mirrored underneath it: at the arena's original 32° layout yaw
        /// the player's creature sat at 131° against a 135° boundary, landed in a side sector,
        /// borrowed the back sheet and had it flipped — so it faced the same way as the
        /// opponent instead of at it, which is the fault this pair of lines was reported for.
        /// The pin in <c>BattleStage.Occupy</c> keeps a battle out of the side sectors
        /// altogether; this is what makes the beats that release the pin — the victory turn —
        /// and any arena tuned to a different yaw safe as well.
        /// </summary>
        private bool EffectiveFlip
        {
            get
            {
                if (mirrorMode == MirrorMode.Off) return false;
                return IsSideSector ? _flip ^ _sideFlip : _flip;
            }
        }

        private void ApplyBlock()
        {
            if (_renderer == null || _block == null) return;

            _renderer.GetPropertyBlock(_block);

            Texture2D texture = ActiveTexture();
            if (texture != null) _block.SetTexture(ShaderProps.BaseMap, texture);

            var sheet = ActiveSheet();
            // The flip lives in whichever mechanism the bound shader actually offers. Doing
            // both would cancel out, so this is an either/or and the shader's own property
            // wins when it exists.
            bool shaderFlips = _material != null && _material.HasProperty(ShaderProps.FlipX);
            Vector4 st = sheet != null
                ? sheet.FrameST(sheet.CellAt(_frameIndex), !shaderFlips && EffectiveFlip)
                : (!shaderFlips && EffectiveFlip ? new Vector4(-1f, 1f, 1f, 0f) : new Vector4(1f, 1f, 0f, 0f));

            _block.SetVector(ShaderProps.BaseMapST, st);
            _block.SetFloat(ShaderProps.FlipX, shaderFlips && EffectiveFlip ? 1f : 0f);
            _block.SetFloat(ShaderProps.Roll, _roll);
            _block.SetFloat(ShaderProps.ContactFade, 1f);

            if (_material != null && _material.HasProperty(ShaderProps.FlashAmount))
            {
                _block.SetColor(ShaderProps.BaseColor, new Color(1f, 1f, 1f, _alpha));
                _block.SetColor(ShaderProps.FlashColor, Color.white);
                _block.SetFloat(ShaderProps.FlashAmount, _flash);
            }
            else
            {
                // No flash channel on the bound shader: approximate it by over-driving the
                // tint. On an unlit fallback this reads as a white pop, which is the point.
                float k = 1f + _flash * 2.5f;
                _block.SetColor(ShaderProps.BaseColor, new Color(k, k, k, _alpha));
            }

            _renderer.SetPropertyBlock(_block);
        }

        private void OnDestroy()
        {
            // Only a material this component created is ours to release. Destroying one that
            // came off a prefab would take the shared asset down with the view.
            if (_ownsMaterial && _material != null) Destroy(_material);
        }
    }
}
