using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Anything that should push grass aside as it moves: the player, a following
    /// creature, an NPC crossing a field.
    ///
    /// Registering is the whole job. The component does not touch the shader itself —
    /// <see cref="FoliageInteractionDirector"/> collects every live source once a frame
    /// and uploads them together, because the globals are a fixed-size array and a
    /// per-component upload would have each source overwriting the others.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FoliageInteractor : MonoBehaviour
    {
        private static readonly List<FoliageInteractor> Live = new List<FoliageInteractor>();

        [Tooltip("How wide a patch this parts, in metres. Roughly the walker's shoulders " +
                 "plus a little, not their whole body — too wide and the field bows away " +
                 "from them like a force field.")]
        [SerializeField] private float _radius = 0.85f;

        [Tooltip("Scales the push. A heavy creature disturbs more grass than a Rattata.")]
        [Range(0f, 3f)]
        [SerializeField] private float _weight = 1f;

        private Vector3 _previousPosition;
        private Vector3 _velocity;

        public static IReadOnlyList<FoliageInteractor> All => Live;
        public float Radius => _radius * Mathf.Max(_weight, 0.01f);
        public Vector3 Velocity => _velocity;

        private void OnEnable()
        {
            _previousPosition = transform.position;
            _velocity = Vector3.zero;
            Live.Add(this);
        }

        private void OnDisable() => Live.Remove(this);

        private void LateUpdate()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            var position = transform.position;
            // Smoothed, because a single frame's delta is noisy enough that the grass
            // would jitter about which way it is leaning.
            var instant = (position - _previousPosition) / dt;
            _velocity = Vector3.Lerp(_velocity, instant, 1f - Mathf.Exp(-12f * dt));
            _previousPosition = position;
        }
    }

    /// <summary>
    /// Uploads every live <see cref="FoliageInteractor"/> to the shader globals once a
    /// frame.
    ///
    /// Sorted by distance to the camera and truncated, so when there are more sources
    /// than the array holds it is the ones on screen that survive rather than whichever
    /// happened to enable first.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class FoliageInteractionDirector : MonoBehaviour
    {
        private readonly List<Vector4> _positions = new List<Vector4>(VfxShaderGlobals.MaxInteractors);
        private readonly List<Vector4> _velocities = new List<Vector4>(VfxShaderGlobals.MaxInteractors);
        private readonly List<FoliageInteractor> _sorted = new List<FoliageInteractor>();

        private void OnDisable()
        {
            // Clear on the way out, or the last frame's sources stay flattened into the
            // grass for as long as the scene is loaded.
            VfxShaderGlobals.SetFoliageInteractors(_positions, _velocities, 0);
        }

        private void LateUpdate()
        {
            _sorted.Clear();
            _sorted.AddRange(FoliageInteractor.All);

            var camera = Camera.main;
            if (camera != null && _sorted.Count > VfxShaderGlobals.MaxInteractors)
            {
                var eye = camera.transform.position;
                _sorted.Sort((a, b) =>
                    (a.transform.position - eye).sqrMagnitude
                    .CompareTo((b.transform.position - eye).sqrMagnitude));
            }

            _positions.Clear();
            _velocities.Clear();
            var count = Mathf.Min(_sorted.Count, VfxShaderGlobals.MaxInteractors);
            for (var i = 0; i < count; i++)
            {
                var source = _sorted[i];
                var p = source.transform.position;
                _positions.Add(new Vector4(p.x, p.y, p.z, source.Radius));
                var v = source.Velocity;
                _velocities.Add(new Vector4(v.x, v.y, v.z, 0f));
            }

            VfxShaderGlobals.SetFoliageInteractors(_positions, _velocities, count);
        }
    }
}
