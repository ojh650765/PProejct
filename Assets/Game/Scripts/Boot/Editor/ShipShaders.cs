using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Makes sure every shader this project looks up by name survives into a build.
    ///
    /// A build strips shaders that nothing references. That is right for a project whose
    /// materials are all authored into scenes, and wrong for this one: the creature
    /// billboards, the starter cards and the trainer portraits all build their materials at
    /// runtime and reach for a shader with <c>Shader.Find</c>, so from the build's point of
    /// view nobody wants them.
    ///
    /// The failure is invisible in the editor, where every shader is always loaded. The web
    /// build reported it plainly and only there:
    ///
    ///   [CreatureBillboard] 'PokeLab/SpriteBillboard' not found; using
    ///   'Universal Render Pipeline/Unlit'. Sprites will draw but will not take the synthetic
    ///   sphere normals, the light-facing shadow caster or the rim.
    ///
    /// It degrades rather than breaks, which is why it went unnoticed: every creature and
    /// every person in the shipped game was drawn by a fallback, lit differently from the
    /// world around them, and nothing on screen said so.
    /// </summary>
    public static class ShipShaders
    {
        /// <summary>
        /// Shaders resolved by name at runtime. Kept in one list because the alternative is a
        /// list that drifts from the <c>Shader.Find</c> calls it exists to cover.
        /// </summary>
        private static readonly string[] LookedUpByName =
        {
            "PokeLab/SpriteBillboard",
            "PokeLab/Creature",
            "PokeLab/PropGroundBlend",
            "PokeLab/Foliage",
            "PokeLab/TerrainBlend",
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Lit",
            "Sprites/Default",
        };

        [MenuItem("Tools/Poké Lab/Build/Ship Runtime Shaders", priority = 313)]
        public static void Apply()
        {
            var graphics = AssetDatabase.LoadAllAssetsAtPath(
                "ProjectSettings/GraphicsSettings.asset").FirstOrDefault();
            if (graphics == null)
            {
                Debug.LogError("[Build] Could not open GraphicsSettings, so the runtime shaders " +
                               "cannot be pinned and a build will strip them.");
                return;
            }

            var serialized = new SerializedObject(graphics);
            var list = serialized.FindProperty("m_AlwaysIncludedShaders");
            if (list == null)
            {
                Debug.LogError("[Build] GraphicsSettings has no always-included shader list.");
                return;
            }

            var already = new HashSet<Object>();
            for (var i = 0; i < list.arraySize; i++)
            {
                var value = list.GetArrayElementAtIndex(i).objectReferenceValue;
                if (value != null) already.Add(value);
            }

            var added = 0;
            var missing = new List<string>();

            foreach (var name in LookedUpByName)
            {
                var shader = Shader.Find(name);
                if (shader == null) { missing.Add(name); continue; }
                if (!already.Add(shader)) continue;

                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                added++;
            }

            serialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            Debug.Log($"[Build] Always-included shaders: {added} added, {already.Count} total.");

            // Named individually. A shader that cannot be found here cannot be found in a build
            // either, and the message that reaches the player is a silent downgrade.
            foreach (var name in missing)
                Debug.LogWarning($"[Build] '{name}' is looked up by name at runtime and does not " +
                                 "exist in the project, so that lookup will always fall back.");
        }
    }
}
