using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Overworld.People
{
    /// <summary>
    /// Draws a person as a camera-facing pixel sprite.
    ///
    /// Every character in the overworld — the player included — was an invisible capsule:
    /// the rig built a "Visual" child and never put a renderer on it, and the level builder
    /// gave NPCs a collider and a controller and no art at all. The sprites had been drawn
    /// and the manifest written; nothing read either.
    ///
    /// Three drawn views cover the circle. Front and back are their own sheets; the two
    /// sides share one, mirrored, so which way that sheet is drawn matters — hence
    /// <see cref="PersonEntry.sideWalksScreenLeft"/> rather than a guess.
    ///
    /// The quad only ever yaws. Pitching it to face the camera is the usual billboard and
    /// it is wrong here: the camera looks down at 38 degrees, and a sprite that tips back
    /// to meet it slides its feet forward off the ground it is standing on.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class PersonBillboard : MonoBehaviour
    {
        private enum View { Front, Back, Side }

        [Header("Art")]
        [Tooltip("Key into people_manifest.json — 'player', 'rival', 'gardener'.")]
        [SerializeField] private string _personKey = "player";

        [Header("Motion")]
        [Tooltip("Speed above which the walk clip plays.")]
        [SerializeField] private float _walkThreshold = 0.15f;
        [Tooltip("Speed above which the run clip plays.")]
        [SerializeField] private float _runThreshold = 4.2f;

        [Tooltip("Degrees of hysteresis on the view sectors, so a character facing a boundary " +
                 "does not flicker between two drawings.")]
        [SerializeField] private float _viewHysteresis = 8f;

        [Header("Framing")]
        [Tooltip("How far the sprite leans back to meet a camera looking down at it. 0 is " +
                 "upright, which is correct for a shallow camera: the squash is cos(pitch), " +
                 "so at 20 degrees the character loses 6% of their height and nobody can " +
                 "see it. Leaning is only worth its cost under a steep camera — at full " +
                 "lean the quad lies flat on the ground, which is what it looks like from " +
                 "any other camera in the scene.")]
        [Range(0f, 1f)]
        [SerializeField] private float _leanToCamera;

        [Tooltip("Overall size on top of the manifest's height, which traces the art at a " +
                 "literal 1.68 m. Two-heads-tall chibi proportions do not survive being " +
                 "placed at a real person's height next to buildings drawn at real ones: " +
                 "0.58 puts the character at 0.97 m, under half the 2.04 m signpost they " +
                 "stand beside, which is the relationship the DS games hold. This is the " +
                 "one number to change if the cast reads wrong; nothing else depends on it.")]
        [Range(0.3f, 1.5f)]
        [SerializeField] private float _scale = 0.58f;

        private static readonly string[] ShaderCandidates =
        {
            "PokeLab/SpriteBillboard",
            "Universal Render Pipeline/Unlit",
            "Sprites/Default",
            "Unlit/Transparent Cutout",
        };

        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseMapST = Shader.PropertyToID("_BaseMap_ST");
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexST = Shader.PropertyToID("_MainTex_ST");

        private PersonEntry _entry;
        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;
        private Camera _camera;
        private Transform _motionSource;
        private Transform _agentSource;
        private NavMeshAgent _agent;

        private View _view = View.Front;
        private bool _mirror;
        private PersonClip _clip;
        private int _step;
        private float _stepTime;
        private Vector3 _lastPosition;
        private float _speed;
        private float _facingYaw;

        /// <summary>Which character is drawn. Safe to set before or after Awake.</summary>
        public string PersonKey
        {
            get => _personKey;
            set { _personKey = value; _entry = null; Rebuild(); }
        }

        /// <summary>
        /// Transform whose movement drives the walk cycle and the facing. Defaults to the
        /// parent, which is the character root for both the player rig and every NPC.
        /// </summary>
        public void SetMotionSource(Transform source) => _motionSource = source;

        private void Awake()
        {
            _renderer = GetComponent<MeshRenderer>();
            _block = new MaterialPropertyBlock();
            if (_motionSource == null) _motionSource = transform.parent != null ? transform.parent : transform;
            _lastPosition = _motionSource.position;
            Rebuild();
        }

        private void Rebuild()
        {
            if (_renderer == null) return;

            _entry = PersonSpriteLibrary.Shared.Find(_personKey);
            if (_entry == null)
            {
                _renderer.enabled = false;
                return;
            }

            _renderer.enabled = true;
            BuildQuad(_entry);

            if (_renderer.sharedMaterial == null || _renderer.sharedMaterial.name != "~PersonSprite")
                _renderer.sharedMaterial = BuildMaterial();

            _clip = ClipFor(View.Front, 0f);
            _step = 0;
            _stepTime = 0f;
            ApplyFrame();
        }

        /// <summary>
        /// A quad the size of the whole frame, offset so the drawn feet land on the
        /// transform's origin.
        /// </summary>
        private void BuildQuad(PersonEntry entry)
        {
            var height = entry.FrameHeightMetres;
            // Frames are square, so the width is the frame height, not the character's.
            var half = height * 0.5f;
            var feet = entry.groundOrigin * height;

            var mesh = new Mesh { name = "~PersonQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-half, -feet, 0f),
                new Vector3( half, -feet, 0f),
                new Vector3(-half, height - feet, 0f),
                new Vector3( half, height - feet, 0f),
            };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.normals = new[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
            mesh.RecalculateBounds();
            GetComponent<MeshFilter>().sharedMesh = mesh;
        }

        private static Material BuildMaterial()
        {
            Shader shader = null;
            foreach (var candidate in ShaderCandidates)
            {
                shader = Shader.Find(candidate);
                if (shader != null) break;
            }

            var material = new Material(shader != null ? shader : Shader.Find("Hidden/InternalErrorShader"))
            {
                name = "~PersonSprite",
            };

            // Cutout, not blend. Pixel art has hard edges and no partial alpha, and an
            // alpha-blended sprite has to be depth-sorted against the grass it is standing in.
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0f);
            if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 1f);
            if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", 0.5f);
            // The project's own shader first, and that ordering is load-bearing.
            //
            // URP/Unlit only clips when the _ALPHATEST_ON variant exists, and this material is
            // built at runtime -- so no material in the project references that variant and the
            // build strips it. EnableKeyword then sets a keyword nothing compiled against: in
            // the editor every variant is present and the sprite cuts out correctly, in a build
            // it draws its whole quad and every character is a rectangle of sheet background.
            // That is the "transparent in the dialogue box, opaque in the world" split -- the
            // box draws through a UI shader that needs no variant.
            //
            // PokeLab/SpriteBillboard clips unconditionally, so there is no variant to lose, and
            // it is in Always Included Shaders so it cannot be stripped either.
            material.EnableKeyword("_ALPHATEST_ON");

            // Double-sided. A single quad has a front and a back, and a billboard that is
            // culled from behind is a character who vanishes — which is exactly what
            // happens the instant anything but the follow camera looks at them, or during
            // the frame the yaw crosses over. The sprite already chooses which view to draw
            // from the facing, so the back of the quad showing the same frame is right.
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            material.doubleSidedGI = true;

            // Lit flat, because these are pixel drawings and not models.
            //
            // PokeLab/SpriteBillboard carries a rim light and a wide light wrap — right for a
            // creature standing in a battle diorama, wrong for a 32-pixel townsperson walking
            // down a lane. Moving world people onto this shader to get their alpha cut also
            // handed them both at their authored strengths, and every character came out
            // washed out. The shader is still the right one; the terms that model a lit
            // surface are what a drawing does not want.
            //
            // Contact darkening stays, at a third: it is the one term here that helps a flat
            // sprite sit on the ground rather than hover over it.
            if (material.HasProperty("_RimStrength")) material.SetFloat("_RimStrength", 0f);
            if (material.HasProperty("_ContactStrength")) material.SetFloat("_ContactStrength", 0.25f);
            if (material.HasProperty("_ShadeWrap")) material.SetFloat("_ShadeWrap", 0.15f);

            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            return material;
        }

        private void LateUpdate()
        {
            if (_entry == null || _renderer == null || !_renderer.enabled) return;

            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            TrackMotion();
            FaceCamera();
            ChooseView();
            Advance();
        }

        private void TrackMotion()
        {
            var source = _motionSource != null ? _motionSource : transform;
            var delta = source.position - _lastPosition;
            _lastPosition = source.position;
            delta.y = 0f;

            var dt = Mathf.Max(Time.deltaTime, 0.0001f);

            // The agent's own speed wherever there is an agent to ask.
            //
            // A smoothed positional delta cannot tell walking from being moved, and this game
            // moves people: a scripted walk that runs out of time or out of navmesh finishes
            // with NavMeshAgent.Warp, which covers tens of metres in a single frame, and the
            // smoothing then spent the best part of a second bleeding a phantom sprint back
            // down. Worse at the other end — the tail never quite reaches zero, so a character
            // an episode had parked on their mark stood there with the walk cycle still
            // running. That is what Linden was doing on the bank after he had stopped. An
            // agent reports exactly zero the moment it is stopped, which is the honest answer
            // to the only question this asks.
            var agent = ResolveAgent(source);
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                _speed = agent.velocity.magnitude;
            }
            else if (delta.magnitude <= MaxStepPerSecond * dt)
            {
                // Smoothed, because a CharacterController's per-frame delta is noisy enough on
                // slopes to rattle the clip between idle and walk while standing still.
                _speed = Mathf.Lerp(_speed, delta.magnitude / dt, 1f - Mathf.Exp(-12f * dt));
            }
            else
            {
                // Further than anybody can travel in one frame, so it was not travel. The
                // sprite holds whatever it was doing across the jump rather than reading it as
                // a sprint and facing whichever way the teleport happened to point.
                delta = Vector3.zero;
            }

            if (delta.sqrMagnitude > 1e-6f)
                _facingYaw = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
            else if (source != transform)
                _facingYaw = source.eulerAngles.y;
        }

        /// <summary>
        /// Metres per second above which a frame's movement is read as a teleport. Comfortably
        /// over the player's own run, which is the fastest anything here travels under its own
        /// legs.
        /// </summary>
        private const float MaxStepPerSecond = 12f;

        /// <summary>
        /// The agent driving this character, or null for the player and anyone walked by hand.
        /// Cached against the source rather than looked up per frame, and re-resolved if the
        /// motion source is ever changed.
        /// </summary>
        private NavMeshAgent ResolveAgent(Transform source)
        {
            if (ReferenceEquals(source, _agentSource)) return _agent;
            _agentSource = source;
            _agent = source != null ? source.GetComponent<NavMeshAgent>() : null;
            return _agent;
        }

        private void FaceCamera()
        {
            var toCamera = _camera.transform.position - transform.position;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 1e-6f) return;
            var yawOnly = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);

            // Lean the quad back until it faces the camera squarely, pivoting on the feet.
            //
            // An upright quad viewed from above projects to cos(pitch) of its height while
            // keeping its full width, so the character reads squashed and heavy — correct
            // in world height, wrong in shape. Stretching the quad to compensate fixes the
            // shape and breaks the height instead: at 55 degrees it made a 1.7 m character
            // 3.3 m tall, taller than the lamp post beside them.
            //
            // Leaning gives up neither. A quad that faces the camera is seen at its true
            // proportions *and* its true size, and because the pivot is the feet the
            // character rotates about the ground they are standing on rather than sliding
            // off it. The cost is that the sprite occupies some depth, which matters only
            // where it must sort against something at the same spot.
            var pitch = Mathf.Asin(Mathf.Clamp(-_camera.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            transform.rotation = yawOnly * Quaternion.Euler(-pitch * _leanToCamera, 0f, 0f);
            transform.localScale = Vector3.one * _scale;
        }

        /// <summary>
        /// Picks front, back or side from where the character is facing relative to the
        /// camera, with a dead band around each boundary.
        /// </summary>
        private void ChooseView()
        {
            var cameraYaw = _camera.transform.eulerAngles.y;
            // 0 means facing away from the camera (we see their back), 180 means facing it.
            var relative = Mathf.DeltaAngle(cameraYaw, _facingYaw);
            var magnitude = Mathf.Abs(relative);

            var slack = Mathf.Max(0f, _viewHysteresis);
            var next = _view;
            if (magnitude < 45f - (_view == View.Back ? -slack : slack)) next = View.Back;
            else if (magnitude > 135f + (_view == View.Front ? -slack : slack)) next = View.Front;
            else next = View.Side;

            if (next != _view)
            {
                _view = next;
                _step = 0;
                _stepTime = 0f;
            }

            // The side sheet is drawn walking one way; the other way is that sheet mirrored.
            //
            // Inverted against what the angle alone suggests, and the quad is why. BuildQuad
            // gives its vertices a normal of -Z, so the face the camera sees is the back of
            // the quad's own frame and its local +X lands on the *left* of the screen. A UV
            // flip that should have turned the character to face screen-right therefore turned
            // them to face screen-left, and every character in the game walked backwards when
            // seen from the side. Verified against the art rather than reasoned from the
            // transform: the drawn side frames face screen-left, which is what
            // sideWalksScreenLeft says, so the flag was right and the mapping was not.
            var walksScreenLeft = _entry.sideWalksScreenLeft;
            _mirror = _view == View.Side && (relative > 0f) != walksScreenLeft;
        }

        private PersonClip ClipFor(View view, float speed)
        {
            PersonClip idle, walk, run;
            switch (view)
            {
                case View.Back: idle = _entry.backIdle; walk = _entry.backWalk; run = _entry.backRun; break;
                case View.Side: idle = _entry.sideIdle; walk = _entry.sideWalk; run = _entry.sideRun; break;
                default: idle = _entry.frontIdle; walk = _entry.frontWalk; run = _entry.frontRun; break;
            }

            if (speed >= _runThreshold && run != null && run.IsValid) return run;
            if (speed >= _walkThreshold && walk != null && walk.IsValid) return walk;
            return idle != null && idle.IsValid ? idle : walk;
        }

        private void Advance()
        {
            var wanted = ClipFor(_view, _speed);
            if (wanted != _clip)
            {
                _clip = wanted;
                _step = 0;
                _stepTime = 0f;
            }

            if (_clip == null || !_clip.IsValid) return;

            _stepTime += Time.deltaTime;
            var hold = _clip.DurationOf(_step);
            while (_stepTime >= hold && _clip.sequence.Length > 1)
            {
                _stepTime -= hold;
                _step = (_step + 1) % _clip.sequence.Length;
                hold = _clip.DurationOf(_step);
            }

            ApplyFrame();
        }

        /// <summary>
        /// Points the material at one cell of the sheet through the UV scale/offset, so the
        /// whole cast shares one material and one draw call per sheet.
        /// </summary>
        private void ApplyFrame()
        {
            if (_clip == null || !_clip.IsValid || _renderer == null) return;

            var texture = PersonSpriteLibrary.Shared.Texture(_clip.texture);
            if (texture == null) return;

            // Rebuild() reaches here through the PersonKey setter, which the level builder
            // and the rig setup both call immediately after AddComponent — before Awake has
            // run and therefore before _block exists. GetPropertyBlock(null) throws
            // ArgumentNullException every LateUpdate after that, which does not stop the
            // game so much as bury the console and cost an order of magnitude of frame time.
            _block ??= new MaterialPropertyBlock();

            var frame = Mathf.Clamp(_clip.sequence[Mathf.Clamp(_step, 0, _clip.sequence.Length - 1)],
                0, Mathf.Max(0, _clip.frames - 1));
            var column = frame % _clip.columns;
            var row = frame / _clip.columns;

            var su = 1f / _clip.columns;
            var sv = 1f / _clip.rows;
            // Row 0 is the top row of the sheet; UV space counts up from the bottom.
            var offset = new Vector2(column * su, 1f - (row + 1) * sv);
            var scale = new Vector2(su, sv);

            if (_mirror)
            {
                // Flip inside the cell, not across the sheet, or the mirror lands on a
                // neighbouring frame.
                offset.x += su;
                scale.x = -su;
            }

            _renderer.GetPropertyBlock(_block);
            _block.SetTexture(BaseMap, texture);
            _block.SetTexture(MainTex, texture);
            _block.SetVector(BaseMapST, new Vector4(scale.x, scale.y, offset.x, offset.y));
            _block.SetVector(MainTexST, new Vector4(scale.x, scale.y, offset.x, offset.y));
            _renderer.SetPropertyBlock(_block);
        }
    }
}
