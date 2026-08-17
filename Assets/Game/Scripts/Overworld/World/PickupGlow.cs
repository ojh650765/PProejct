using UnityEngine;

namespace PokeLab.Overworld.World
{
    /// <summary>
    /// Marks a prop as collectable by lighting its edges.
    ///
    /// An item lying in the world has to be distinguishable from the scenery it is lying
    /// among, and a Poké Ball on a dirt path at a 55-degree camera angle is about forty
    /// pixels of the same brown-adjacent palette as everything around it. The alternative
    /// to a highlight is a floating marker, which puts UI in the world; this keeps the
    /// signal on the object.
    ///
    /// The glow is written through a <see cref="MaterialPropertyBlock"/> rather than by
    /// giving items their own material. Every prop in the level shares one material, and
    /// that is what collapses them into a handful of draw calls — instancing them apart to
    /// make eight of them glow would be an expensive way to pay for a highlight. A property
    /// block overrides per renderer and leaves the batch intact.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Renderer))]
    public sealed class PickupGlow : MonoBehaviour
    {
        private static readonly int GlowId = Shader.PropertyToID("_PickupGlow");
        private static readonly int ColorId = Shader.PropertyToID("_PickupColor");
        private static readonly int PulseSpeedId = Shader.PropertyToID("_PickupPulseSpeed");

        [Tooltip("Emissive strength. 0 is an ordinary prop.")]
        [Range(0f, 4f)]
        [SerializeField] private float _glow = 1.35f;

        [SerializeField] private Color _colour = new Color(1f, 0.86f, 0.42f, 1f);

        [Tooltip("Breaths per second, roughly. Slow enough to be an invitation rather than " +
                 "an alarm.")]
        [Range(0f, 8f)]
        [SerializeField] private float _pulseSpeed = 2.4f;

        private MaterialPropertyBlock _block;
        private Renderer[] _renderers;

        /// <summary>True while the item is still there to be picked up.</summary>
        public bool Active { get; private set; } = true;

        private void OnEnable()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _block ??= new MaterialPropertyBlock();
            Apply(Active ? _glow : 0f);
        }

        /// <summary>
        /// Turns the highlight off — after collection, or while a cutscene owns the screen
        /// and a pulsing item in the background would pull the eye off the actor.
        /// </summary>
        public void SetActive(bool active)
        {
            if (Active == active) return;
            Active = active;
            Apply(active ? _glow : 0f);
        }

        private void Apply(float glow)
        {
            // The block as well as the renderers. OnValidate fires on a component whose Awake
            // has not run — a prefab instantiated by the level builder, a domain reload mid-play
            // — and GetPropertyBlock(null) throws ArgumentNullException from deep inside the
            // engine, once per renderer per validate, with the item's highlight silently dead
            // for the rest of the session.
            _block ??= new MaterialPropertyBlock();
            if (_renderers == null) return;
            foreach (var renderer in _renderers)
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(_block);
                _block.SetFloat(GlowId, glow);
                _block.SetColor(ColorId, _colour);
                _block.SetFloat(PulseSpeedId, _pulseSpeed);
                renderer.SetPropertyBlock(_block);
            }
        }

        private void OnValidate()
        {
            if (!Application.isPlaying || _renderers == null) return;
            Apply(Active ? _glow : 0f);
        }
    }
}
