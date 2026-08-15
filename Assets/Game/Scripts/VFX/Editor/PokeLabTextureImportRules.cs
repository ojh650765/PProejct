using System;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Vfx.Editor
{
    /// <summary>
    /// Texture import policy for the HD-2D look, applied per folder.
    ///
    /// Filtering genuinely differs by family, and getting it uniform is a mistake
    /// in either direction:
    ///
    ///   Sprites        Point + mipmaps, aniso 1, alpha-to-coverage OFF.
    ///                  Point is the pixel look. Mips are not optional: the wide
    ///                  establishing shot minifies a 256 px sprite to roughly 0.3x
    ///                  and an unmipped point-sampled sprite shimmers violently at
    ///                  that scale. Alpha-to-coverage would soften the intended
    ///                  hard pixel edge, so PokeLab/SpriteBillboard deliberately
    ///                  has no AlphaToMask command.
    ///
    ///   Foliage        Bilinear + aniso 4, mipmaps, alpha-to-coverage ON.
    ///                  Point-filtering an alpha-clip mask gives jagged crawling
    ///                  leaf edges. A-to-C against the existing MSAA 4x gives a
    ///                  hard but stable silhouette instead. PokeLab/Foliage already
    ///                  ships _AlphaToMask on by default; this file is the other
    ///                  half of that pair.
    ///
    ///   Hard surfaces  Bilinear + aniso 8, mipmaps, opaque.
    ///                  A point-filtered ground plane receding to the horizon is a
    ///                  shimmering mess that mips cannot fix on the magnification
    ///                  side. Octopath does not point-filter its environments, and
    ///                  neither do we -- the "pixel" read in HD-2D environments
    ///                  comes from low colour count, not from low texel count or
    ///                  from nearest-neighbour sampling.
    ///
    /// Deliberately conservative about when it fires. Import rules that stamp
    /// settings on every reimport fight every manual tweak forever, so the
    /// automatic path only runs when Unity has no meta for the asset yet
    /// (importSettingsMissing). To re-apply the policy across the project on
    /// demand, use PokeLab &gt; Render &gt; Reapply Texture Import Rules.
    /// </summary>
    public sealed class PokeLabTextureImportRules : AssetPostprocessor
    {
        private enum Family
        {
            None,
            Sprite,
            Foliage,
            HardSurface,
            Creature,
        }

        private const string MenuPath = "PokeLab/Render/Reapply Texture Import Rules";

        private void OnPreprocessTexture()
        {
            var importer = (TextureImporter)assetImporter;

            // First import only. See the class comment: a rule that reasserts
            // itself on every reimport is a rule nobody can override.
            if (!importer.importSettingsMissing)
                return;

            Apply(importer, assetPath);
        }

        [MenuItem(MenuPath)]
        private static void ReapplyAll()
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Game/Art" });
            int changed = 0;

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    EditorUtility.DisplayProgressBar("PokeLab texture rules", path,
                                                     (float)i / Mathf.Max(1, guids.Length));

                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null)
                        continue;
                    if (Classify(path) == Family.None)
                        continue;

                    Apply(importer, path);
                    importer.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"[PokeLab] Texture import rules applied to {changed} texture(s).");
        }

        private static void Apply(TextureImporter importer, string path)
        {
            Family family = Classify(path);
            if (family == Family.None)
                return;

            bool isNormal = path.IndexOf("_Normal", StringComparison.OrdinalIgnoreCase) >= 0;

            importer.textureType = isNormal ? TextureImporterType.NormalMap
                                            : TextureImporterType.Default;
            importer.mipmapEnabled = true;
            importer.sRGBTexture = !isNormal;

            switch (family)
            {
                case Family.Sprite:
                    // The billboard shader samples a plain Texture2D atlas, not a
                    // Unity Sprite: sprite import mode would cost the mip chain the
                    // wide shot needs and buy nothing, because nothing here goes
                    // through the 2D renderer.
                    importer.filterMode = FilterMode.Point;
                    importer.anisoLevel = 1;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.alphaIsTransparency = true;
                    // Block compression eats a 1 px keyline, which is the whole
                    // line language of the art. 256x256 x 8 directions x RGBA is
                    // about 2 MB per creature uncompressed -- affordable for a
                    // twelve-creature slice, and revisit it if the roster grows.
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    break;

                case Family.Foliage:
                    importer.filterMode = FilterMode.Bilinear;
                    importer.anisoLevel = 4;
                    importer.wrapMode = TextureWrapMode.Repeat;
                    importer.alphaIsTransparency = true;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    break;

                case Family.Creature:
                    // Any 3D actor still on PokeLab/Creature. Seen close up in
                    // battle, never at a grazing angle, so aniso 4 is enough.
                    importer.filterMode = FilterMode.Bilinear;
                    importer.anisoLevel = 4;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    break;

                case Family.HardSurface:
                    importer.filterMode = FilterMode.Bilinear;
                    importer.anisoLevel = 8;
                    importer.wrapMode = TextureWrapMode.Repeat;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    break;
            }
        }

        private static Family Classify(string path)
        {
            string p = path.Replace('\\', '/');

            // UI portraits are authored as Sprites for the scanner and the party
            // list. They are not our business and must keep their sprite import
            // mode, so bail before anything else.
            if (p.IndexOf("_Portrait", StringComparison.OrdinalIgnoreCase) >= 0)
                return Family.None;

            if (Contains(p, "/Art/Sprites/") || Contains(p, "/Sprites/Creatures/") ||
                Contains(p, "/Sprites/Characters/"))
                return Family.Sprite;

            if (Contains(p, "/Environment/Foliage/"))
                return Family.Foliage;

            if (Contains(p, "/Environment/Terrain/") || Contains(p, "/Environment/Town/") ||
                Contains(p, "/Art/Props/") || Contains(p, "/Environment/Characters/"))
                return Family.HardSurface;

            if (Contains(p, "/Art/Creatures/"))
                return Family.Creature;

            return Family.None;
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
