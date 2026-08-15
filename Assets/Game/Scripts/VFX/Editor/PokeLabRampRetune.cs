using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Vfx.Editor
{
    /// <summary>
    /// Stamps the one HD-2D lighting language onto every material that uses a
    /// PokeLab shader.
    ///
    /// The shader defaults in Assets/Game/Shaders already carry these numbers, but
    /// a Unity material serialises a value for every property it has ever seen, so
    /// existing materials keep whatever the shader default was on the day they were
    /// created. The creature materials under Assets/Game/Art, for example, still
    /// carry _ShadeSoftness 0.07 and _SpecularStrength 0.35 from the pre-pivot
    /// look. Editing those files by hand is not this worker's to do -- they belong
    /// to the art pipeline -- so the retune ships as a tool the integrator runs.
    ///
    /// Why these numbers:
    ///   _ShadeSteps 3 / _ShadeSoftness 0.02
    ///       Flat banded colour with a hard terminator is why Sugimori art reads as
    ///       Sugimori art. The value must be identical on sprites and environment
    ///       or the frame reads as two different renderers stitched together, which
    ///       is the single most common failure of a 2D-in-3D pivot.
    ///   Specular near zero
    ///       A Blinn-Phong highlight sliding across a surface as the camera moves
    ///       is the strongest "this is a lit 3D model" cue there is. What is left is
    ///       a small, fully stepped highlight that reads as a painted shape. Water
    ///       is exempt: its sun sparkle is the read of water, not a shading artefact.
    /// </summary>
    public static class PokeLabRampRetune
    {
        private const float ShadeSteps = 3f;
        private const float ShadeSoftness = 0.02f;

        private sealed class Recipe
        {
            public float SpecularStrength;
            public float SpecularSharpness;
            public float SpecularStep;
            public bool HasSpecularStep;
        }

        // Keyed by Shader.name.
        private static readonly Dictionary<string, Recipe> Recipes = new Dictionary<string, Recipe>
        {
            { "PokeLab/Creature",         new Recipe { SpecularStrength = 0.08f, SpecularSharpness = 48f, SpecularStep = 1f, HasSpecularStep = true } },
            { "PokeLab/SpriteBillboard",  new Recipe { SpecularStrength = 0f,    SpecularSharpness = 0f } },
            { "PokeLab/PropGroundBlend",  new Recipe { SpecularStrength = 0.05f, SpecularSharpness = 40f } },
            { "PokeLab/TerrainBlend",     new Recipe { SpecularStrength = 0.03f, SpecularSharpness = 32f } },
            { "PokeLab/Foliage",          new Recipe { SpecularStrength = 0.08f, SpecularSharpness = 36f } },
            { "PokeLab/Dissolve",         new Recipe { SpecularStrength = 0.08f, SpecularSharpness = 48f } },
        };

        [MenuItem("PokeLab/Render/Apply HD-2D Ramp To All Materials")]
        private static void Apply()
        {
            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets/Game" });
            int touched = 0;

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    EditorUtility.DisplayProgressBar("PokeLab HD-2D ramp", path,
                                                     (float)i / Mathf.Max(1, guids.Length));

                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null || material.shader == null)
                        continue;

                    if (!Recipes.TryGetValue(material.shader.name, out Recipe recipe))
                        continue;

                    bool dirty = false;
                    dirty |= SetFloat(material, "_ShadeSteps", ShadeSteps);
                    dirty |= SetFloat(material, "_ShadeSoftness", ShadeSoftness);
                    dirty |= SetFloat(material, "_SpecularStrength", recipe.SpecularStrength);

                    if (recipe.SpecularSharpness > 0f)
                        dirty |= SetFloat(material, "_SpecularSharpness", recipe.SpecularSharpness);
                    if (recipe.HasSpecularStep)
                        dirty |= SetFloat(material, "_SpecularStep", recipe.SpecularStep);

                    if (dirty)
                    {
                        EditorUtility.SetDirty(material);
                        touched++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[PokeLab] HD-2D ramp applied to {touched} material(s).");
        }

        private static bool SetFloat(Material material, string property, float value)
        {
            if (!material.HasProperty(property))
                return false;
            if (Mathf.Approximately(material.GetFloat(property), value))
                return false;

            material.SetFloat(property, value);
            return true;
        }
    }
}
