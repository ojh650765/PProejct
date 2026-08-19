using System.Collections;
using PokeLab.Overworld.People;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The person who throws the ball, standing on a trainer mark for exactly as long as the
    /// throw takes.
    ///
    /// The throw itself was already built — <see cref="BallActor"/> arcs out of
    /// <c>BattleStage.TrainerMarkOf</c> on every send-out and on every capture — and the marks
    /// were empty transforms, so the ball came out of thin air. This is the missing half and
    /// nothing more: a sprite, an entrance and an exit.
    ///
    /// <b>Only around the throw.</b> The series puts the trainer on screen for the wind-up and
    /// takes them off once the creature is out, and that is not decoration: the shot is framed on
    /// two combatants, and a person standing at the near mark for the whole battle is a
    /// foreground occluder in every frame of it. So this defaults to hidden and
    /// <see cref="BattlePresenter"/> brackets the throw with <see cref="Enter"/> and
    /// <see cref="Exit"/>.
    ///
    /// <b>The drawing is the battle art, and falls back to the overworld's.</b> This used to bind
    /// a <see cref="PersonBillboard"/> and nothing else, which put a 32-pixel walking sprite —
    /// the map marker — in the foreground of a shot whose creatures are drawn at 96. The battle
    /// art is <c>Resources/Portraits</c>: the games' own 80-pixel trainer sprites, front-facing
    /// for somebody looked at across the field and <c>_back</c> for somebody the camera is
    /// standing behind. When neither resolves the <see cref="PersonBillboard"/> path is used
    /// exactly as before — it resolves a person key against <c>people_manifest.json</c>, picks
    /// front/back/side by sector and mirrors the side sheet correctly, and a character with no
    /// portrait is better drawn small than not at all.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    [DisallowMultipleComponent]
    public sealed class TrainerView : MonoBehaviour
    {
        /// <summary>Resources sub-folder the battle trainer art is loaded from.</summary>
        private const string PortraitRoot = "Portraits/";

        /// <summary>Suffix on the key of the view drawn from behind — see <see cref="DrawnFrom"/>.</summary>
        private const string BackSuffix = "_back";

        /// <summary>Poses in a back sheet, matching Tools/Sprites/build_portraits.py.</summary>
        private const int ThrowFrameCount = 5;

        [Tooltip("The billboard that draws the person when no portrait resolves. Built on this " +
                 "object's Visual child when empty.")]
        [SerializeField] private PersonBillboard billboard;

        [Tooltip("Metres the trainer slides sideways to leave the frame. Sideways rather than " +
                 "backwards: the camera stands behind the player's mark, so a trainer walking " +
                 "back would walk into the lens.")]
        [SerializeField] private float exitOffset = 3.0f;

        [Tooltip("Metres from the ground to the top of the drawn portrait. The figure is sized " +
                 "from the crown down rather than from the feet up because the back view has no " +
                 "feet — see portraitHem. At 1.3 the trainer stands about twice a starter's " +
                 "height, which is the relationship the DS games hold in the send-out shot.")]
        [SerializeField] private float portraitCrown = 1.3f;

        [Tooltip("Metres from the ground to the bottom edge of a back portrait — the hip the art " +
                 "is cut at. No game in the series draws a trainer's back below the waist: the " +
                 "sprite is designed to run off the bottom of the screen, and standing the cut " +
                 "edge on the ground instead would read as a person sunk into the floor. Zero " +
                 "for a front portrait, which is a whole standing figure and does have feet.")]
        [SerializeField] private float portraitHem = 0.5f;

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

        private Transform _visual;
        private Transform _portrait;
        private MeshRenderer _portraitRenderer;
        private MaterialPropertyBlock _block;
        private Camera _camera;
        private bool _usingPortrait;

        /// <summary>True when a person key resolved to art. False means nothing will be drawn.</summary>
        public bool HasArt { get; private set; }

        /// <summary>
        /// Which drawn view of the trainer this mark shows.
        ///
        /// Pinned by <c>BattleStage</c> rather than recovered from the angle to the camera, for
        /// the same reason the two creatures' views are pinned: the layout is fixed, the art is
        /// drawn for it, and the player's trainer is behind the lens while the opponent's is
        /// across the field. Deriving it would make a camera tuning change several files away
        /// able to turn the thrower round.
        /// </summary>
        public SpriteFacing DrawnFrom { get; set; } = SpriteFacing.Front;

        private void Awake()
        {
            EnsureBuilt();
            // Hidden until somebody throws. A trainer visible on the first frame of a battle is
            // one standing in shot through the whole opening, which is the thing this is not.
            SetShown(false);
        }

        private void EnsureBuilt()
        {
            if (_visual == null)
            {
                Transform found = transform.Find("Visual");
                if (found == null)
                {
                    var go = new GameObject("Visual");
                    go.transform.SetParent(transform, false);
                    // The mark's own layer, not Default. The camera rig exempts its subjects'
                    // layer from occlusion resolution, and a thrower standing between the lens
                    // and the field is otherwise an obstacle that shoves the shot off its
                    // authored angle at the exact moment it is framed on them.
                    go.layer = gameObject.layer;
                    go.AddComponent<MeshFilter>();
                    go.AddComponent<MeshRenderer>();
                    found = go.transform;
                }
                _visual = found;
            }

            if (billboard == null) billboard = _visual.GetComponent<PersonBillboard>();
            if (billboard == null) billboard = _visual.gameObject.AddComponent<PersonBillboard>();

            // The billboard reads its facing off its motion source, and this object is the one
            // whose yaw the stage aligns with the field. Left to default it would take the
            // Visual child's own parent — which is this object anyway — but saying so keeps the
            // sector maths below true if the hierarchy ever gains a level.
            billboard.SetMotionSource(transform);

            if (_portrait == null)
            {
                // A second child rather than a second renderer on the first. PersonBillboard
                // owns the Visual child's mesh, material and property block outright and writes
                // all three every LateUpdate, so a portrait sharing them would be overwritten on
                // the frame after it was bound.
                Transform found = transform.Find("Portrait");
                if (found == null)
                {
                    var go = new GameObject("Portrait");
                    go.transform.SetParent(transform, false);
                    go.layer = gameObject.layer;
                    found = go.transform;
                }
                _portrait = found;

                if (_portrait.GetComponent<MeshFilter>() == null) _portrait.gameObject.AddComponent<MeshFilter>();
                _portraitRenderer = _portrait.GetComponent<MeshRenderer>();
                if (_portraitRenderer == null) _portraitRenderer = _portrait.gameObject.AddComponent<MeshRenderer>();
                _block = new MaterialPropertyBlock();
            }
        }

        /// <summary>
        /// Binds the character to draw.
        ///
        /// The key is lower-cased first: the manifest is keyed in lower case and the ids that
        /// reach here come from trainer data written as "Youngster", which the library's alias
        /// table cannot match and would report as a character with no art.
        /// </summary>
        public void Bind(string personKey)
        {
            EnsureBuilt();

            if (string.IsNullOrEmpty(personKey))
            {
                HasArt = false;
                _usingPortrait = false;
                SetShown(false);
                return;
            }

            string key = personKey.ToLowerInvariant();

            _usingPortrait = BindPortrait(key);
            if (!_usingPortrait)
            {
                billboard.PersonKey = key;
                HasArt = PersonSpriteLibrary.Shared.Find(key) != null;
            }
            else
            {
                HasArt = true;
            }

            SetShown(false);
        }

        /// <summary>
        /// Resolves and sizes the battle portrait for a key, or reports that there is none.
        ///
        /// A mark drawn from behind asks for the <c>_back</c> art and nothing else: falling
        /// through to the front portrait would put the player face-on to a camera standing
        /// behind them, which is worse than the small sprite this replaced because the small
        /// sprite at least has a real back view. So the fallback for a missing back is the
        /// <see cref="PersonBillboard"/>, not the wrong picture.
        /// </summary>
        private bool BindPortrait(string key)
        {
            bool back = DrawnFrom == SpriteFacing.Back;
            Texture2D texture = LoadPortrait(back ? key + BackSuffix : key, out Rect uvRect);
            if (texture == null) return false;

            // A back sheet is the throw; a front portrait is one drawing. Told rather than
            // measured, because a five-cell sheet and a single wide portrait have the same
            // aspect if the figure happens to be drawn wide enough.
            _throwFrames = back ? ThrowFrameCount : 1;

            BuildPortraitQuad(texture, uvRect, back);
            SetThrowFrame(0);
            return true;
        }

        /// <summary>
        /// Loads one portrait and reports which part of its texture the drawing occupies.
        ///
        /// Asked for as a <see cref="Sprite"/> first, because that is what
        /// <c>CreatureImportSettings</c>' PortraitRoot rule makes these — and then as a plain
        /// texture, because that rule only reaches a file when the file is imported. Art added
        /// while the editor was not looking, or added before the rule existed, comes in as a
        /// Texture2D and <c>Resources.Load&lt;Sprite&gt;</c> returns null for a PNG sitting
        /// right there. That failure has already cost this project one round of "it did not
        /// change", so the second lookup is here rather than a reimport being a prerequisite.
        /// </summary>
        private static Texture2D LoadPortrait(string key, out Rect uvRect)
        {
            var sprite = Resources.Load<Sprite>(PortraitRoot + key);
            if (sprite != null && sprite.texture != null)
            {
                Rect r = sprite.textureRect;
                uvRect = new Rect(
                    r.x / sprite.texture.width, r.y / sprite.texture.height,
                    r.width / sprite.texture.width, r.height / sprite.texture.height);
                return sprite.texture;
            }

            var texture = Resources.Load<Texture2D>(PortraitRoot + key);
            uvRect = new Rect(0f, 0f, 1f, 1f);
            return texture;
        }

        /// <summary>
        /// Builds the quad the portrait is drawn on, spanning hem to crown in world metres and
        /// as wide as the art's own aspect makes it.
        ///
        /// Width from the aspect, never from the height: the portraits are cropped to each
        /// figure's own bounds and padded, so they are not square and not even a consistent
        /// shape — Rowan's frame is twice as tall as it is wide and Lucas' back view is wider
        /// than it is tall. A quad sized as a square would squash one and stretch the other.
        /// </summary>
        private void BuildPortraitQuad(Texture2D texture, Rect uvRect, bool back)
        {
            float top = Mathf.Max(0.05f, portraitCrown);
            float bottom = back ? Mathf.Clamp(portraitHem, 0f, top - 0.05f) : 0f;
            float height = top - bottom;

            // Sized to one cell of the sheet, not to the sheet.
            //
            // A back view is five drawn poses laid side by side — the throw the games play.
            // Measuring the quad against the whole texture would make the trainer five times
            // too wide and show all five at once.
            float pixelHeight = Mathf.Max(1f, texture.height * uvRect.height);
            float aspect = (texture.width * uvRect.width) / (pixelHeight * _throwFrames);
            float half = height * aspect * 0.5f;

            var mesh = new Mesh { name = "~TrainerPortraitQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-half, bottom, 0f),
                new Vector3( half, bottom, 0f),
                new Vector3(-half, top, 0f),
                new Vector3( half, top, 0f),
            };
            // The back sheet is mirrored: drawn throwing with the arm on the side away from
            // the enemy, so the ball visibly left the wrong hand. Flipping U on the quad
            // mirrors every throw pose; the front portrait stays as drawn.
            mesh.uv = back
                ? new[] { Vector2.right, Vector2.zero, Vector2.one, Vector2.up }
                : new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.normals = new[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
            mesh.RecalculateBounds();
            _portrait.GetComponent<MeshFilter>().sharedMesh = mesh;

            if (_portraitRenderer.sharedMaterial == null
                || _portraitRenderer.sharedMaterial.name != "~TrainerPortrait")
            {
                _portraitRenderer.sharedMaterial = BuildMaterial();
            }

            _cellUv = new Vector4(uvRect.width / _throwFrames, uvRect.height, uvRect.x, uvRect.y);
            var st = _cellUv;
            _portraitRenderer.GetPropertyBlock(_block);
            _block.SetTexture(BaseMap, texture);
            _block.SetTexture(MainTex, texture);
            _block.SetVector(BaseMapST, st);
            _block.SetVector(MainTexST, st);
            _portraitRenderer.SetPropertyBlock(_block);
        }

        /// <summary>How many poses the loaded art holds. One for a still portrait.</summary>
        private int _throwFrames = 1;

        /// <summary>Scale-offset for a single cell, so a frame change is one add.</summary>
        private Vector4 _cellUv = new Vector4(1f, 1f, 0f, 0f);

        /// <summary>Shows one pose of the throw.</summary>
        private void SetThrowFrame(int index)
        {
            if (_portraitRenderer == null || _throwFrames <= 1) return;

            index = Mathf.Clamp(index, 0, _throwFrames - 1);
            _portraitRenderer.GetPropertyBlock(_block);
            var st = new Vector4(_cellUv.x, _cellUv.y, _cellUv.z + _cellUv.x * index, _cellUv.w);
            _block.SetVector(BaseMapST, st);
            _block.SetVector(MainTexST, st);
            _portraitRenderer.SetPropertyBlock(_block);
        }

        /// <summary>
        /// Plays the throw and leaves the trainer on the last pose.
        ///
        /// Held on the follow-through rather than snapped back to the wind-up, because the
        /// ball is in the air by then and a thrower who has already reset their arm reads as
        /// not having thrown it. Exit() takes them off a moment later anyway.
        /// </summary>
        public IEnumerator PlayThrow(float seconds)
        {
            if (_throwFrames <= 1) yield break;

            var perFrame = Mathf.Max(0.02f, seconds / _throwFrames);
            for (int i = 0; i < _throwFrames; i++)
            {
                SetThrowFrame(i);
                yield return CinematicRunner.Wait(perFrame);
            }
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
                name = "~TrainerPortrait",
            };

            // Cutout rather than blend, matching every other pixel sprite in the arena: the art
            // has hard edges and no partial alpha, and a blended quad standing a metre in front
            // of the lens would have to be depth-sorted against the ball crossing it.
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

            // Double-sided. The trainer stands nearly on the camera's own axis, so the yaw the
            // quad is turned to crosses over during the blend into the send-out shot, and a
            // back-face-culled quad is a thrower who vanishes for those frames.
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            material.doubleSidedGI = true;

            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            return material;
        }

        /// <summary>
        /// Turns the portrait quad to the camera, yaw only.
        ///
        /// Never pitch, for <see cref="PersonBillboard"/>'s reason: a sprite that tips back to
        /// meet a camera looking down at it slides its feet forward off the ground it is
        /// standing on. It matters more here than there, because the trainer stands closer to
        /// the lens than anything else in the shot.
        /// </summary>
        private void LateUpdate()
        {
            if (!_usingPortrait || _portrait == null || !_portrait.gameObject.activeSelf) return;

            if (_camera == null || !_camera.isActiveAndEnabled) _camera = Camera.main;
            if (_camera == null) return;

            Vector3 toCamera = _camera.transform.position - _portrait.position;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 1e-6f) return;
            _portrait.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
        }

        /// <summary>
        /// Walks the trainer in from off frame to their mark.
        ///
        /// The facing is not tweened and does not need to be. On the portrait path which view is
        /// drawn is pinned by <see cref="DrawnFrom"/> and no angle enters into it; on the
        /// <see cref="PersonBillboard"/> fallback the mark already looks down the stage axis and
        /// the battle camera is 18° off that axis, which puts the trainer 27° clear of that
        /// component's 45° back/side boundary — the margin that keeps the player drawn from
        /// behind rather than as a mirrored side view.
        /// </summary>
        public IEnumerator Enter(float seconds)
        {
            if (!HasArt) yield break;

            Vector3 from = OffMark;
            transform.localPosition = from;
            SetShown(true);

            yield return CinematicRunner.Tween(Mathf.Max(0.01f, seconds), CinematicEase.OutCubic,
                p => transform.localPosition = Vector3.Lerp(from, Vector3.zero, p));
            transform.localPosition = Vector3.zero;
        }

        /// <summary>Stands the trainer on their mark with no entrance, for a beat already in progress.</summary>
        public void Present()
        {
            if (!HasArt) return;
            transform.localPosition = Vector3.zero;
            SetShown(true);
        }

        /// <summary>
        /// Walks the trainer out of frame and switches them off.
        ///
        /// Switched off rather than left standing at the offset: the exit distance is tuned for
        /// this camera, and any shot that pushes in or changes angle would find them parked just
        /// outside the frame they were supposed to have left.
        /// </summary>
        public IEnumerator Exit(float seconds)
        {
            if (!HasArt || !IsShown) yield break;

            Vector3 from = transform.localPosition;
            Vector3 to = OffMark;

            yield return CinematicRunner.Tween(Mathf.Max(0.01f, seconds), CinematicEase.InOutCubic,
                p => transform.localPosition = Vector3.Lerp(from, to, p));

            SetShown(false);
            transform.localPosition = Vector3.zero;
        }

        /// <summary>Hides the trainer at once. For tearing a battle down, never mid-beat.</summary>
        public void Hide()
        {
            transform.localPosition = Vector3.zero;
            SetShown(false);
        }

        /// <summary>
        /// Where the trainer stands when they are off the frame, in the mark's own space.
        ///
        /// Local rather than world, and that is what makes one number serve both sides: the two
        /// trainer marks face each other, so local -X is screen-left for the player and
        /// screen-right for the opponent — each of them leaves by their own edge of the frame.
        /// Working in world space would also mean re-solving every time the stage rescales itself
        /// to the creatures standing in it, which it does on every send-out.
        /// </summary>
        private Vector3 OffMark => new Vector3(-Mathf.Abs(exitOffset), 0f, 0f);

        private bool IsShown
        {
            get
            {
                Transform drawn = _usingPortrait ? _portrait : _visual;
                return drawn != null && drawn.gameObject.activeSelf;
            }
        }

        /// <summary>
        /// Shows exactly one of the two drawings and switches the other off.
        ///
        /// Both are switched every time rather than only the live one: a bind that changes which
        /// path is in use would otherwise leave the previous battle's drawing standing on the
        /// mark beside this one's.
        /// </summary>
        private void SetShown(bool shown)
        {
            EnsureBuilt();
            if (_visual != null) _visual.gameObject.SetActive(shown && !_usingPortrait);
            if (_portrait != null) _portrait.gameObject.SetActive(shown && _usingPortrait);
        }
    }
}
