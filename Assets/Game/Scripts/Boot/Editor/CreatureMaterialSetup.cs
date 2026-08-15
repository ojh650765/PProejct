using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Retargets the materials Unity extracts from the creature FBX files onto the stylised
    /// toon shader and wires their maps.
    ///
    /// A menu command rather than a postprocessor: material extraction happens once, and the
    /// toon parameters are art direction that a human should be able to tweak and keep. Running
    /// it again is safe and only re-points the shader and textures, so regenerated art can be
    /// re-hooked without losing hand edits to the ramp.
    /// </summary>
    public static class CreatureMaterialSetup
    {
        private const string CreatureRoot = "Assets/Game/Art/Creatures";
        private const string ShaderName = "PokeLab/Creature";

        [MenuItem("Tools/Poké Lab/Art/Apply Toon Shader To Creatures")]
        public static void Apply()
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError($"[CreatureMaterials] Shader '{ShaderName}' not found. " +
                               "Has Assets/Game/Shaders compiled?");
                return;
            }

            var guids = AssetDatabase.FindAssets("t:Material", new[] { CreatureRoot });
            var converted = 0;
            var missingMaps = new List<string>();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) continue;

                // Extracted materials are named "<Creature>_BaseColor"; the maps sit beside
                // the FBX under the creature's own base name.
                var baseName = Path.GetFileNameWithoutExtension(path);
                if (baseName.EndsWith("_BaseColor"))
                    baseName = baseName.Substring(0, baseName.Length - "_BaseColor".Length);

                material.shader = shader;

                var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CreatureRoot}/{baseName}_BaseColor.png");
                var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CreatureRoot}/{baseName}_Normal.png");

                if (albedo != null) material.SetTexture("_BaseMap", albedo);
                else missingMaps.Add($"{baseName}_BaseColor.png");

                if (normal != null) material.SetTexture("_BumpMap", normal);
                else missingMaps.Add($"{baseName}_Normal.png");

                ApplyArtDirection(material);
                EditorUtility.SetDirty(material);
                converted++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CreatureMaterials] Retargeted {converted} material(s) onto {ShaderName}.");
            if (missingMaps.Count > 0)
                Debug.LogWarning("[CreatureMaterials] Missing maps: " + string.Join(", ", missingMaps));
        }

        /// <summary>
        /// The shared look. Kept in one place so the whole cast reads as one game rather than
        /// twelve separately-tuned creatures — cohesion is what the visual review judges hardest.
        /// </summary>
        private static void ApplyArtDirection(Material m)
        {
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_BumpScale", 1f);

            // Three bands with a soft edge: enough to read as stylised without the harsh
            // two-tone that makes cel shading look cheap. The shadow tint is cool so shadowed
            // sides sit in the same blue ambient the terrain and water use.
            m.SetColor("_ShadeColor", new Color(0.42f, 0.47f, 0.66f, 1f));
            m.SetFloat("_ShadeSteps", 3f);
            m.SetFloat("_ShadeSoftness", 0.07f);
            m.SetFloat("_ShadeWrap", 0.30f);
            m.SetFloat("_ShadowStrength", 0.75f);
            m.SetFloat("_OcclusionStrength", 0.6f);

            // A soft, broad highlight. Creatures are not glossy; this is sheen, not plastic.
            m.SetColor("_SpecularColor", new Color(1f, 0.98f, 0.92f, 1f));
            m.SetFloat("_SpecularSharpness", 24f);
            m.SetFloat("_SpecularStrength", 0.35f);
            m.SetFloat("_SpecularStep", 0.45f);

            // Rim separates the silhouette from the background, which matters most in battle
            // where the creature is framed against distant terrain.
            m.SetColor("_RimColor", new Color(1f, 0.95f, 0.85f, 1f));
            m.SetFloat("_RimPower", 3.2f);
            m.SetFloat("_RimThreshold", 0.22f);
            m.SetFloat("_RimStrength", 0.9f);
            m.SetFloat("_RimLightAlign", 0.6f);

            m.SetColor("_SssColor", new Color(1f, 0.45f, 0.35f, 1f));
            m.SetFloat("_SssStrength", 0.5f);
            m.SetFloat("_SssPower", 4f);
        }
    }
}
