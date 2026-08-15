using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Rebuilds <see cref="CreatureArtCatalog"/> from the Blender pipeline's manifest.
    ///
    /// The manifest is the art side's source of truth and is regenerated whenever creatures
    /// are rebuilt, so this reads it rather than scanning the folder — a stale FBX left on
    /// disk would otherwise silently enter the game.
    /// </summary>
    public static class CreatureArtCatalogBuilder
    {
        private const string ManifestPath = "Assets/Game/Art/Creatures/creature_manifest.json";
        private const string CatalogPath = "Assets/Game/Data/CreatureArtCatalog.asset";

        [MenuItem("Tools/Poké Lab/Art/Rebuild Creature Art Catalog")]
        public static void Rebuild()
        {
            if (!File.Exists(ManifestPath))
            {
                Debug.LogError($"[CreatureArt] Manifest not found at {ManifestPath}.");
                return;
            }

            var manifest = JsonUtility.FromJson<ManifestRoot>(
                WrapCreatureDictionary(File.ReadAllText(ManifestPath)));

            if (manifest?.creatures == null || manifest.creatures.Count == 0)
            {
                Debug.LogError("[CreatureArt] Manifest parsed but contained no creatures.");
                return;
            }

            var catalog = AssetDatabase.LoadAssetAtPath<CreatureArtCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<CreatureArtCatalog>();
                Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath) ?? "Assets/Game/Data");
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            var entries = new List<CreatureArtCatalog.Entry>();
            var missing = new List<string>();

            foreach (var c in manifest.creatures)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(c.fbx);
                if (model == null) missing.Add(c.fbx);

                // The portrait imports as a Sprite via the postprocessor; LoadAssetAtPath on
                // the texture path returns the sprite sub-asset for single-sprite textures.
                var portrait = AssetDatabase.LoadAssetAtPath<Sprite>(c.portrait);

                entries.Add(new CreatureArtCatalog.Entry
                {
                    SpeciesId = c.id,
                    NameEn = c.nameEn,
                    Model = model,
                    Portrait = portrait,
                    DisplayHeight = c.displayHeight > 0f ? c.displayHeight : 1f,
                });
            }

            entries.Sort((a, b) => a.SpeciesId.CompareTo(b.SpeciesId));
            catalog.Replace(entries);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CreatureArt] Catalogue rebuilt with {entries.Count} creature(s) → {CatalogPath}");
            if (missing.Count > 0)
                Debug.LogError("[CreatureArt] Models not found: " + string.Join(", ", missing));
        }

        /// <summary>
        /// JsonUtility cannot deserialise a dictionary, and the manifest keys creatures by id
        /// string. Rewrite that object into an array before parsing rather than pulling in a
        /// JSON library for one file.
        /// </summary>
        private static string WrapCreatureDictionary(string json)
        {
            var key = "\"creatures\"";
            var start = json.IndexOf(key, System.StringComparison.Ordinal);
            if (start < 0) return json;

            var braceStart = json.IndexOf('{', start + key.Length);
            if (braceStart < 0) return json;

            // Walk to the matching close brace so nested objects do not terminate us early.
            var depth = 0;
            var end = -1;
            for (var i = braceStart; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end < 0) return json;

            var body = json.Substring(braceStart + 1, end - braceStart - 1);

            // "1": { ... }, "5": { ... }  ->  { ... }, { ... }
            var rebuilt = System.Text.RegularExpressions.Regex.Replace(
                body, "\"\\d+\"\\s*:\\s*\\{", "{");

            return "{\"creatures\":[" + rebuilt + "]}";
        }

        [System.Serializable]
        private sealed class ManifestRoot
        {
            public List<ManifestCreature> creatures;
        }

        [System.Serializable]
        private sealed class ManifestCreature
        {
            public int id;
            public string nameEn;
            public string fbx;
            public string portrait;
            public float displayHeight;
        }
    }
}
