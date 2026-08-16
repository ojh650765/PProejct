using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// Which of the terrain material's four layers the field disc is made of.
    ///
    /// Deliberately the same four the ground shader already blends — grass, dirt, sand, rock —
    /// rather than a new set of surface types, because the disc is supposed to be made of
    /// whatever the ground there is made of, and the ground already answers that question in
    /// exactly these terms.
    /// </summary>
    public enum BattleFieldSurface
    {
        Grass = 0,
        Dirt = 1,
        Sand = 2,
        Rock = 3,
    }

    /// <summary>
    /// The lit patch of ground a combatant stands on.
    ///
    /// It exists for two reasons and they are separate. The visible one is staging: the
    /// reference framing puts each side on a disc, and that is what makes the two of them read
    /// as <i>placed</i> in the location rather than as sprites standing in front of a
    /// photograph. The structural one is contact — a camera-facing quad has no footprint, so
    /// without something drawn under it the creature meets the ground along a single line of
    /// pixels and the eye cannot tell what it is standing on, or whether it is standing at all.
    ///
    /// <b>Constant geometry, zone surface.</b> The disc is the same flat circle everywhere; only
    /// the terrain weights change, so a cave disc and a lakeside disc are one mesh with
    /// different vertex colours rather than two props to keep in sync with a level that is
    /// regenerated constantly. The terrain shader samples its layer textures from world
    /// position, which is what makes this work at all: the disc's grain lines up with the grain
    /// of the ground it lies in, so it reads as a trodden patch of that ground instead of a
    /// decal sitting on top of it.
    ///
    /// The rim blends toward dirt rather than fading out, because the shipped ground shader has
    /// no per-vertex tint and an alpha fade would need a second material — and a ring of bare
    /// earth is what a trodden hollow actually looks like.
    /// </summary>
    [DefaultExecutionOrder(-140)]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class BattleFieldDisc : MonoBehaviour
    {
        [Header("Surface")]
        [Tooltip("Terrain material the disc is drawn with. Assign the one the level ground uses, " +
                 "or the disc will not match the ground it lies in.")]
        [SerializeField] private Material groundMaterial;
        [Tooltip("Which terrain layer the disc is made of. Set from the zone the encounter fired in.")]
        [SerializeField] private BattleFieldSurface surface = BattleFieldSurface.Grass;

        [Header("Size")]
        [Tooltip("Disc radius as a multiple of the occupant's display height. The roster runs from " +
                 "a 0.3 m Caterpie to a 6.5 m Gyarados, so a fixed radius is either a dot or a field.")]
        [SerializeField] private float radiusPerMetre = 0.85f;
        [Tooltip("Radius is clamped into this band, so a bad registry height cannot produce a disc " +
                 "the size of the arena or one too small to see.")]
        [SerializeField] private Vector2 radiusClamp = new Vector2(0.45f, 4.5f);
        [Tooltip("Fraction of the radius given over to the darker rim.")]
        [Range(0.05f, 0.6f)]
        [SerializeField] private float rimFraction = 0.28f;

        [Header("Placement")]
        [Tooltip("Metres the disc is lifted off the ground plane. Enough to beat depth precision on " +
                 "the terrain it lies in, small enough that nothing reads as floating.")]
        [SerializeField] private float groundOffset = 0.02f;

        private const int Segments = 48;

        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Mesh _mesh;
        private float _radius = -1f;
        private BattleFieldSurface _builtSurface = (BattleFieldSurface)(-1);

        /// <summary>Current radius in metres. Diagnostic.</summary>
        public float Radius => _radius;

        private void Awake() => Fit(0.8f);

        /// <summary>
        /// Sizes the disc to the creature standing on it. Called on every send-out and every
        /// switch, because the replacement is rarely the same size as what it replaced.
        /// </summary>
        public void Fit(float displayHeight)
        {
            float radius = Mathf.Clamp(
                Mathf.Max(0.05f, displayHeight) * Mathf.Max(0.05f, radiusPerMetre),
                Mathf.Min(radiusClamp.x, radiusClamp.y),
                Mathf.Max(radiusClamp.x, radiusClamp.y));

            // Rebuilt rather than scaled. A uniform scale would stretch the world-space texture
            // lookup off the ground it is meant to match, and the whole point of the disc is that
            // its grain continues the grain around it.
            if (Mathf.Abs(radius - _radius) < 0.01f && surface == _builtSurface) return;
            _radius = radius;
            _builtSurface = surface;
            Rebuild();
        }

        /// <summary>Sets which terrain layer the disc is made of. Rebuilt on the next <see cref="Fit"/>.</summary>
        public void SetSurface(BattleFieldSurface value)
        {
            if (surface == value) return;
            surface = value;
            _builtSurface = (BattleFieldSurface)(-1);
            if (_radius > 0f) { _builtSurface = surface; Rebuild(); }
        }

        /// <summary>Assigns the ground material at build time, from the editor setup.</summary>
        public void SetGroundMaterial(Material material) => groundMaterial = material;

        private void Rebuild()
        {
            EnsureComponents();

            var vertices = new Vector3[Segments * 2 + 1];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            var colours = new Color[vertices.Length];
            var triangles = new int[Segments * 3 * 3];

            float inner = _radius * (1f - Mathf.Clamp(rimFraction, 0.05f, 0.6f));
            Color core = WeightsFor(surface);
            // The rim is the same ground worn through to earth. Mixed rather than replaced, so a
            // rock disc keeps reading as rock at its edge instead of turning into a mud ring.
            Color rim = Color.Lerp(core, WeightsFor(BattleFieldSurface.Dirt), 0.65f);

            vertices[0] = Vector3.zero;
            colours[0] = core;

            for (int i = 0; i < Segments; i++)
            {
                float angle = i / (float)Segments * Mathf.PI * 2f;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                vertices[1 + i] = direction * inner;
                vertices[1 + Segments + i] = direction * _radius;
                colours[1 + i] = core;
                colours[1 + Segments + i] = rim;
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                normals[i] = Vector3.up;
                uvs[i] = new Vector2(vertices[i].x, vertices[i].z);
            }

            int t = 0;
            for (int i = 0; i < Segments; i++)
            {
                int a = 1 + i;
                int b = 1 + (i + 1) % Segments;
                // Wound so the face normal — cross(b-a, c-a), which is what Unity treats as the
                // front — points up. Reversed, the disc is culled from every angle anyone will
                // ever look at it from, and the only symptom is that it is not there.
                triangles[t++] = 0; triangles[t++] = b; triangles[t++] = a;

                int c = 1 + Segments + i;
                int d = 1 + Segments + (i + 1) % Segments;
                triangles[t++] = a; triangles[t++] = d; triangles[t++] = c;
                triangles[t++] = a; triangles[t++] = b; triangles[t++] = d;
            }

            _mesh.Clear();
            _mesh.vertices = vertices;
            _mesh.normals = normals;
            _mesh.uv = uvs;
            _mesh.colors = colours;
            _mesh.triangles = triangles;
            _mesh.RecalculateTangents();
            _mesh.RecalculateBounds();

            // Lifted off the mark rather than sitting on it. The mark is the creature's ground
            // contact point, so a disc at exactly that height z-fights the terrain under it.
            transform.localPosition = new Vector3(0f, groundOffset, 0f);

            if (groundMaterial != null && _renderer.sharedMaterial != groundMaterial)
                _renderer.sharedMaterial = groundMaterial;
        }

        /// <summary>
        /// The terrain shader's four blend weights, packed as a vertex colour. RGBA is grass,
        /// dirt, sand, rock — the order the ground meshes are already painted in.
        /// </summary>
        private static Color WeightsFor(BattleFieldSurface value)
        {
            switch (value)
            {
                case BattleFieldSurface.Dirt: return new Color(0f, 1f, 0f, 0f);
                case BattleFieldSurface.Sand: return new Color(0f, 0f, 1f, 0f);
                case BattleFieldSurface.Rock: return new Color(0f, 0f, 0f, 1f);
                default: return new Color(1f, 0f, 0f, 0f);
            }
        }

        private void EnsureComponents()
        {
            if (_filter == null) _filter = GetComponent<MeshFilter>();
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "~BattleFieldDisc", hideFlags = HideFlags.DontSave };
                _filter.sharedMesh = _mesh;
            }

            // A disc casting a shadow onto the ground it is lying on produces a dark ring the
            // art direction does not ask for; it receives shadows so the creature's own can land.
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = true;
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
