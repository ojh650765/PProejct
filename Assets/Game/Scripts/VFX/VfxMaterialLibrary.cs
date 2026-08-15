using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Caches one material per (surface, blend, texture) combination.
    ///
    /// Sharing materials is what keeps the draw call count sane: a battle can have
    /// a dozen effects alive at once, and if each one owned its material none of
    /// them would batch. Per-effect colour lives in the particle's vertex colour
    /// instead, which is free.
    ///
    /// Shaders are located with Shader.Find, and the Resources materials under
    /// Assets/Game/VFX/Resources/PokeLab exist so a player build keeps them alive:
    /// a shader referenced only through Shader.Find is a shader the build stripper
    /// is entitled to remove.
    /// </summary>
    public static class VfxMaterialLibrary
    {
        public const string ParticleShaderName = "PokeLab/VFX/Particle";
        public const string GasShaderName = "PokeLab/VFX/Gas";
        public const string TrailShaderName = "PokeLab/VFX/EnergyTrail";
        public const string ForceFieldShaderName = "PokeLab/VFX/ForceField";
        public const string DecalShaderName = "PokeLab/VFX/Decal";

        private const string ResourcePathPrefix = "PokeLab/Materials/";

        private static readonly Dictionary<int, Material> Cache = new Dictionary<int, Material>();
        private static readonly Dictionary<string, Shader> ShaderCache = new Dictionary<string, Shader>();

        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int DetailMapId = Shader.PropertyToID("_DetailMap");
        private static readonly int FlowMapId = Shader.PropertyToID("_FlowMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int SoftFadeId = Shader.PropertyToID("_SoftFadeDistance");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int EdgeFadeId = Shader.PropertyToID("_EdgeFade");
        private static readonly int NormalCutoffId = Shader.PropertyToID("_NormalCutoff");
        private static readonly int NormalFadeId = Shader.PropertyToID("_NormalFade");

        private static Material _blobShadow;

        /// <summary>
        /// The grounding blob under a sprite creature or character.
        ///
        /// A billboard quad cannot ground itself: it is vertical, the terrain under
        /// it is not flat, and a second horizontal quad at the feet would clip
        /// through every slope it stands on. PokeLab/VFX/Decal already solves this
        /// -- it lists "the shadow blob under a creature" as a supported case,
        /// reconstructs world position from scene depth and conforms to whatever
        /// geometry is actually there, and rejects surfaces that face the wrong way
        /// so the blob never smears up a wall the creature is standing beside.
        ///
        /// This is the *contact* shadow. It is not a replacement for the real cast
        /// shadow, which PokeLab/SpriteBillboard's light-facing ShadowCaster pass
        /// produces: the blob says "this thing is touching the ground here", the
        /// cast shadow says "the sun is over there". A sprite needs both, and either
        /// one alone reads as a mistake.
        ///
        /// Render it on a unit cube scaled to roughly (footprint, height, footprint)
        /// centred at the feet. The Y extent only has to be deep enough to catch the
        /// ground under a slope.
        ///
        /// REQUIRES the depth texture, and wants DepthNormals for the surface-angle
        /// rejection. Both are already on for the DOF and SSAO the grades use.
        /// </summary>
        public static Material GetSpriteBlobShadow()
        {
            if (_blobShadow != null) return _blobShadow;

            Shader shader = FindShader(DecalShaderName);
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");

            var mat = new Material(shader)
            {
                name = "PL_Decal_SpriteBlobShadow",
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = false,
            };

            mat.SetTexture(BaseMapId, ProceduralVfxTextures.Get(ProceduralVfxTextures.Kind.SoftDot));
            // Tinted toward the shared shadow colour rather than to black. Every
            // other surface in the game shades into a blue-violet, and a neutral
            // black blob under a creature is the one place that reads as a hole.
            mat.SetColor(BaseColorId, new Color(0.10f, 0.09f, 0.16f, 1f));
            mat.SetFloat(OpacityId, 0.55f);
            mat.SetFloat(EdgeFadeId, 0.20f);
            // Upward-facing surfaces only, with a wide fade so a blob on a slope
            // thins out rather than ending on a line.
            mat.SetFloat(NormalCutoffId, 0.35f);
            mat.SetFloat(NormalFadeId, 0.45f);
            // Straight alpha, and deliberately not ApplyBlend: that sets a render
            // queue of 3000 for the particle path, and this must stay on the decal
            // shader's own Transparent-50 so the blob lands before anything else
            // transparent draws over the ground.
            mat.SetFloat(SrcBlendId, (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat(DstBlendId, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);

            _blobShadow = mat;
            return _blobShadow;
        }

        public static Material Get(VfxSurface surface, VfxBlend blend, ProceduralVfxTextures.Kind texture)
        {
            int key = ((int)surface * 31 + (int)blend) * 251 + (int)texture;
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached;

            Material mat = Build(surface, blend, texture);
            Cache[key] = mat;
            return mat;
        }

        public static Shader FindShader(string name)
        {
            if (ShaderCache.TryGetValue(name, out var cached) && cached != null) return cached;

            // Prefer a material shipped in Resources: it survives build stripping and
            // it is what the integrator can inspect and tweak.
            var template = Resources.Load<Material>(ResourcePathPrefix + ResourceNameFor(name));
            Shader shader = template != null ? template.shader : Shader.Find(name);

            if (shader == null)
                Debug.LogWarning($"[VfxMaterialLibrary] Shader '{name}' not found. Effects using it " +
                                 "will fall back to a magenta material.");

            ShaderCache[name] = shader;
            return shader;
        }

        private static string ResourceNameFor(string shaderName)
        {
            // "PokeLab/VFX/Particle" -> "Vfx_Particle"
            int slash = shaderName.LastIndexOf('/');
            string leaf = slash >= 0 ? shaderName.Substring(slash + 1) : shaderName;
            return "Vfx_" + leaf;
        }

        private static Material Build(VfxSurface surface, VfxBlend blend, ProceduralVfxTextures.Kind texture)
        {
            string shaderName = surface switch
            {
                VfxSurface.Gas => GasShaderName,
                VfxSurface.Trail => TrailShaderName,
                _ => ParticleShaderName,
            };

            Shader shader = FindShader(shaderName);
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");

            var mat = new Material(shader)
            {
                name = $"PL_Vfx_{surface}_{blend}_{texture}",
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = false,
            };

            Texture2D tex = ProceduralVfxTextures.Get(texture);

            switch (surface)
            {
                case VfxSurface.Gas:
                    mat.SetTexture(BaseMapId, tex);
                    mat.SetTexture(DetailMapId, ProceduralVfxTextures.Get(ProceduralVfxTextures.Kind.NoiseTiling));
                    break;

                case VfxSurface.Trail:
                    mat.SetTexture(FlowMapId, ProceduralVfxTextures.Get(ProceduralVfxTextures.Kind.NoiseTiling));
                    break;

                default:
                    mat.SetTexture(BaseMapId, tex);
                    ApplyBlend(mat, blend);
                    mat.SetColor(BaseColorId, Color.white);
                    mat.SetFloat(SoftFadeId, blend == VfxBlend.AlphaBlend ? 0.6f : 0.35f);
                    break;
            }

            return mat;
        }

        private static void ApplyBlend(Material mat, VfxBlend blend)
        {
            switch (blend)
            {
                case VfxBlend.AlphaBlend:
                    mat.SetFloat(SrcBlendId, (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetFloat(DstBlendId, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.DisableKeyword("_ADDITIVE_FOG");
                    mat.renderQueue = 3000;
                    break;

                case VfxBlend.Premultiplied:
                    mat.SetFloat(SrcBlendId, (float)UnityEngine.Rendering.BlendMode.One);
                    mat.SetFloat(DstBlendId, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    // Premultiplied still adds light, so it must fog to black or it
                    // turns into a bright patch of fog at distance.
                    mat.EnableKeyword("_ADDITIVE_FOG");
                    mat.renderQueue = 3010;
                    break;

                default:
                    mat.SetFloat(SrcBlendId, (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetFloat(DstBlendId, (float)UnityEngine.Rendering.BlendMode.One);
                    mat.EnableKeyword("_ADDITIVE_FOG");
                    mat.renderQueue = 3020;
                    break;
            }
        }

        public static void Clear()
        {
            foreach (var kv in Cache)
            {
                if (kv.Value == null) continue;
                if (Application.isPlaying) Object.Destroy(kv.Value);
                else Object.DestroyImmediate(kv.Value);
            }
            if (_blobShadow != null)
            {
                if (Application.isPlaying) Object.Destroy(_blobShadow);
                else Object.DestroyImmediate(_blobShadow);
                _blobShadow = null;
            }
            Cache.Clear();
            ShaderCache.Clear();
        }
    }
}
