using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Draws one foliage field as GPU-instanced batches instead of as GameObjects.
    ///
    /// An encounter field only reads as cover you are walking *through* when the grass is
    /// dense enough to hide the ground, and this slice asks for about 4,500 tall grass
    /// clusters across five patches. As GameObjects that is 4,500 transforms, renderers
    /// and culling entries; as instances it is a handful of draw calls, because the whole
    /// foliage family shares one material (see CreatureImportSettings — routing every
    /// foliage mesh to M_Env_Foliage is what makes that true).
    ///
    /// Culling is per batch rather than per instance. Instances are grouped by locality
    /// when the batch list is built, so a batch's bounds stay tight and a batch that is
    /// behind the camera costs one plane test rather than 511 matrix uploads.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class InstancedFoliage : MonoBehaviour
    {
        /// <summary>Unity's instancing limit per DrawMeshInstanced call.</summary>
        private const int MaxPerBatch = 1023;

        [System.Serializable]
        public sealed class Group
        {
            public Mesh Mesh;
            public Material Material;
            [HideInInspector] public List<Matrix4x4> Transforms = new List<Matrix4x4>();

            [System.NonSerialized] public Matrix4x4[][] Batches;
            [System.NonSerialized] public Bounds[] BatchBounds;
        }

        [SerializeField] private List<Group> _groups = new List<Group>();

        [Tooltip("Metres beyond which a batch is not drawn. Grass an entire screen away " +
                 "is sub-pixel detail that costs the same as grass at your feet.")]
        [SerializeField] private float _drawDistance = 70f;

        [Tooltip("Shadow casting. Dense grass casting real shadows is expensive and, at " +
                 "this camera angle, mostly invisible under the blades that cast it.")]
        [SerializeField] private ShadowCastingMode _shadows = ShadowCastingMode.Off;

        [SerializeField] private bool _receiveShadows = true;

        private readonly Plane[] _frustum = new Plane[6];
        private Camera _lastCamera;

        public IReadOnlyList<Group> Groups => _groups;

        /// <summary>Called by the level builder. Positions are world space.</summary>
        public void SetGroup(Mesh mesh, Material material, List<Matrix4x4> transforms)
        {
            if (mesh == null || material == null || transforms == null || transforms.Count == 0) return;
            _groups.Add(new Group { Mesh = mesh, Material = material, Transforms = transforms });
            Rebuild();
        }

        public void Rebuild()
        {
            foreach (var group in _groups)
            {
                if (group.Mesh == null || group.Transforms == null || group.Transforms.Count == 0)
                {
                    group.Batches = System.Array.Empty<Matrix4x4[]>();
                    group.BatchBounds = System.Array.Empty<Bounds>();
                    continue;
                }

                // Sorting by a coarse spatial key before batching is what keeps batch
                // bounds tight. In scatter order the instances are effectively random
                // across the field, so every batch would have bounds covering the whole
                // patch and per-batch culling would never reject anything.
                var sorted = new List<Matrix4x4>(group.Transforms);
                sorted.Sort((a, b) =>
                {
                    var pa = a.GetColumn(3);
                    var pb = b.GetColumn(3);
                    var ka = Mathf.FloorToInt(pa.z / 8f) * 4096 + Mathf.FloorToInt(pa.x / 8f);
                    var kb = Mathf.FloorToInt(pb.z / 8f) * 4096 + Mathf.FloorToInt(pb.x / 8f);
                    return ka.CompareTo(kb);
                });

                var batchCount = Mathf.CeilToInt(sorted.Count / (float)MaxPerBatch);
                group.Batches = new Matrix4x4[batchCount][];
                group.BatchBounds = new Bounds[batchCount];

                var meshExtent = group.Mesh.bounds.extents.magnitude;
                for (var b = 0; b < batchCount; b++)
                {
                    var start = b * MaxPerBatch;
                    var count = Mathf.Min(MaxPerBatch, sorted.Count - start);
                    var batch = new Matrix4x4[count];
                    var bounds = new Bounds(sorted[start].GetColumn(3), Vector3.zero);
                    for (var i = 0; i < count; i++)
                    {
                        batch[i] = sorted[start + i];
                        bounds.Encapsulate((Vector3)batch[i].GetColumn(3));
                    }
                    // Grown by the mesh's own reach so a cluster whose pivot is just
                    // outside the frustum does not pop out while its blades are still
                    // on screen.
                    bounds.Expand(meshExtent * 2f);
                    group.Batches[b] = batch;
                    group.BatchBounds[b] = bounds;
                }
            }
        }

        private void OnEnable() => Rebuild();

        private void LateUpdate()
        {
            var cam = Camera.main;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var view = UnityEditor.SceneView.lastActiveSceneView;
                if (view != null && view.camera != null) cam = view.camera;
            }
#endif
            if (cam == null) return;

            if (cam != _lastCamera) _lastCamera = cam;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustum);
            var eye = cam.transform.position;
            var maxSqr = _drawDistance * _drawDistance;

            foreach (var group in _groups)
            {
                if (group.Batches == null) Rebuild();
                if (group.Batches == null || group.Mesh == null || group.Material == null) continue;

                for (var b = 0; b < group.Batches.Length; b++)
                {
                    var bounds = group.BatchBounds[b];
                    if ((bounds.ClosestPoint(eye) - eye).sqrMagnitude > maxSqr) continue;
                    if (!GeometryUtility.TestPlanesAABB(_frustum, bounds)) continue;

                    Graphics.DrawMeshInstanced(
                        group.Mesh, 0, group.Material, group.Batches[b], group.Batches[b].Length,
                        null, _shadows, _receiveShadows, gameObject.layer, cam);
                }
            }
        }
    }
}
