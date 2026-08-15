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
            if (_frozen || sheet == null || sheet.frames <= 1) return;

            float fps = Mathf.Max(0.5f, sheet.fps) * _rateScale;
            _frameTime += Time.deltaTime * fps;
            while (_frameTime >= 1f)
            {
                _frameTime -= 1f;
                _frameIndex = (_frameIndex + 1) % Mathf.Max(1, sheet.frames);
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
                float facingDot = Vector3.Dot(forward, toCamera);

                // Hysteresis in both directions, so a beat that swings the creature past the
                // boundary and back does not produce two facing changes.
                if (_facing == SpriteFacing.Back && facingDot > facingHysteresis) _facing = SpriteFacing.Front;
                else if (_facing == SpriteFacing.Front && facingDot < -facingHysteresis) _facing = SpriteFacing.Back;

                UpdateMirror(forward, cam);
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

        private SpriteSheetInfo ActiveSheet() => _facing == SpriteFacing.Back ? _backSheet : _frontSheet;

        private Texture2D ActiveTexture() => _facing == SpriteFacing.Back ? _backTexture : _frontTexture;

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
                ? sheet.FrameST(_frameIndex, !shaderFlips && _flip)
                : (!shaderFlips && _flip ? new Vector4(-1f, 1f, 1f, 0f) : new Vector4(1f, 1f, 0f, 0f));

            _block.SetVector(ShaderProps.BaseMapST, st);
            _block.SetFloat(ShaderProps.FlipX, shaderFlips && _flip ? 1f : 0f);
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
