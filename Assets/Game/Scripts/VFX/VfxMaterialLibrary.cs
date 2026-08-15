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
            Cache.Clear();
            ShaderCache.Clear();
        }
    }
}
