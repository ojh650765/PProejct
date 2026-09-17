using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    public static class SpritePixelRepair
    {
        [MenuItem("Tools/Poké Lab/Repair/Preserve Sprite Atlas Pixels")]
        public static void Apply()
        {
            int changed = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Game/Art/Sprites" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.filterMode != FilterMode.Point ||
                    importer.npotScale == TextureImporterNPOTScale.None) continue;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.SaveAndReimport();
                changed++;
            }
            Debug.Log($"[Sprite repair] Preserved original texel dimensions on {changed} atlases.");
        }
    }
}
