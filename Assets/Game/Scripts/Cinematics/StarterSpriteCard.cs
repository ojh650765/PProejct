using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// One pixel sprite standing in the world on a yaw-only quad: the briefcase, the player
    /// reaching into it.
    ///
    /// <c>PersonBillboard</c> already does this and does it
    /// well, and this is not a rewrite of it — it is the same quad with the manifest lookup
    /// taken out. That component resolves its art through <c>PersonSpriteLibrary</c>, which
    /// lives in PokeLab.Overworld; this assembly cannot see that one and is not going to be
    /// given a reference to it for two props. So the caller — which is in Boot, the assembly
    /// that can see both — hands over a texture and the grid it is cut on, and this draws it.
    ///
    /// Yaw only, never pitch, for <c>PersonBillboard</c>'s
    /// reason: a sprite that tips back to meet a camera looking down at it slides its feet
    /// forward off the ground it is standing on. The case is standing on the ground and the
    /// shot looks down into it, which is exactly the case that shows.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class StarterSpriteCard : MonoBehaviour
    {
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

        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;
        private Camera _camera;

        private Texture2D _texture;
        private int _columns = 1;
        private int _rows = 1;
        private int _cell;

        /// <summary>Height of the whole frame in metres, margins included. Zero until bound.</summary>
        public float FrameHeight { get; private set; }

        public MeshRenderer Renderer
        {
            get { EnsureBuilt(); return _renderer; }
        }

        private void Awake() => EnsureBuilt();

        private void EnsureBuilt()
        {
            if (_renderer != null) return;
            _renderer = GetComponent<MeshRenderer>();
            _block = new MaterialPropertyBlock();
        }

        /// <summary>
        /// Binds one sheet and sizes the quad so the drawn content's feet land on the
        /// transform origin.
        ///
        /// <paramref name="frameHeightMetres"/> is the whole frame, not the drawn content:
        /// the sprite's empty margins are part of the image, and sizing the quad to the
        /// content scales the subject up by the margin and lifts it off the ground.
        /// </summary>
        public void Bind(Texture2D texture, int columns, int rows, float frameHeightMetres,
            float groundOrigin)
        {
            EnsureBuilt();

            _texture = texture;
            _columns = Mathf.Max(1, columns);
            _rows = Mathf.Max(1, rows);
            FrameHeight = Mathf.Max(0.01f, frameHeightMetres);

            _renderer.enabled = texture != null;
            if (texture == null) return;

            BuildQuad(FrameHeight, Mathf.Clamp01(groundOrigin));

            if (_renderer.sharedMaterial == null || _renderer.sharedMaterial.name != "~StarterSprite")
                _renderer.sharedMaterial = BuildMaterial();

            SetCell(_cell);
        }

        /// <summary>Which cell of the sheet is drawn. Out-of-range wraps rather than throwing.</summary>
        public void SetCell(int cell)
        {
            EnsureBuilt();
            if (_texture == null || _renderer == null || _block == null) return;

            var count = Mathf.Max(1, _columns * _rows);
            _cell = ((cell % count) + count) % count;

            var scale = new Vector2(1f / _columns, 1f / _rows);
            var column = _cell % _columns;
            // Row 0 is the top of the sheet; UV space counts up from the bottom.
            var row = _rows - 1 - (_cell / _columns);
            var st = new Vector4(scale.x, scale.y, column * scale.x, row * scale.y);

            _renderer.GetPropertyBlock(_block);
            _block.SetTexture(BaseMap, _texture);
            _block.SetTexture(MainTex, _texture);
            _block.SetVector(BaseMapST, st);
            _block.SetVector(MainTexST, st);
            _renderer.SetPropertyBlock(_block);
        }

        public void SetVisible(bool visible)
        {
            EnsureBuilt();
            if (_renderer != null) _renderer.enabled = visible && _texture != null;
        }

        private void BuildQuad(float height, float groundOrigin)
        {
            // Frames are square, so the width is the frame's height and not the subject's.
            var half = height * 0.5f;
            var feet = groundOrigin * height;

            var mesh = new Mesh { name = "~StarterSpriteQuad" };
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
                name = "~StarterSprite",
            };

            // Cutout rather than blend, matching every other pixel sprite in the world: the
            // art has hard edges and no partial alpha, and a blended quad would have to be
            // depth-sorted against the ball models sitting a few centimetres in front of it.
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

            // Double-sided, because the yaw crosses over during the camera blend into the
            // close shot and a back-face-culled quad vanishes for those frames.
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            material.doubleSidedGI = true;

            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            return material;
        }

        private void LateUpdate()
        {
            if (_renderer == null || !_renderer.enabled) return;
            if (_camera == null || !_camera.isActiveAndEnabled) _camera = Camera.main;
            if (_camera == null) return;

            var toCamera = _camera.transform.position - transform.position;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 1e-6f) return;
            transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
        }
    }
}
