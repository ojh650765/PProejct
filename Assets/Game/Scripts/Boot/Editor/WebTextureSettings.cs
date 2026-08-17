using System;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Compresses the project's textures for the web target.
    ///
    /// 262 of 359 textures carry an explicit WebGL platform override of RGBA32 with
    /// compression off — 565 MB of uncompressed texture data, 155 MB of it inside
    /// <c>Resources/</c> where nothing can strip it. Brotli over uncompressed RGBA projects to
    /// a single <c>.data</c> file of 120–160 MB, and GitHub rejects any file over 100 MB
    /// outright. So the build would succeed, the commit would succeed, and the push would fail
    /// — after twenty minutes of work, with nothing before that point saying it was doomed.
    ///
    /// Two different settings, because the art is two different kinds:
    ///
    /// Pixel art is crunched DXT. Crunch is a second, lossy pass on top of the block
    /// compression that ships much smaller and decompresses to the same DXT at load. Sprites
    /// are flat colour with hard edges, which is the case crunch handles best, and they are
    /// drawn point-filtered at small sizes where block artefacts have nowhere to show.
    ///
    /// Environment atlases are plain DXT, not crunched. They are 2048² and sampled triplanar
    /// across large faces at grazing angles — the case where crunch's extra quantisation shows
    /// as banding, and this project has already spent a round chasing banding on a distant
    /// wall.
    ///
    /// Normal maps stay uncrunched for the usual reason: crunch quantises the two channels a
    /// normal map stores independently, and the error shows up as faceting on smooth surfaces.
    /// </summary>
    public static class WebTextureSettings
    {
        private const string SpriteRoot = "Assets/Game/Art/Sprites/";
        private const string WebGLPlatform = "WebGL";

        [MenuItem("Tools/Poké Lab/Build/Compress Textures For Web", priority = 311)]
        public static void CompressForWeb() => Apply(compress: true);

        /// <summary>
        /// Puts the overrides back to uncompressed.
        ///
        /// Here because the compression above is a decision made for one target, and somebody
        /// comparing the two needs to be able to get back without hand-editing 359 importers.
        /// </summary>
        [MenuItem("Tools/Poké Lab/Build/Restore Uncompressed Web Textures", priority = 312)]
        public static void RestoreUncompressed() => Apply(compress: false);

        private static void Apply(bool compress)
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Game/Art" });
            var changed = 0;
            var skipped = 0;

            try
            {
                for (var i = 0; i < guids.Length; i++)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Compressing for the web", path, (float)i / Mathf.Max(1, guids.Length)))
                        break;

                    if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
                    {
                        skipped++;
                        continue;
                    }

                    var settings = importer.GetPlatformTextureSettings(WebGLPlatform);
                    settings.overridden = true;
                    settings.maxTextureSize = 2048;

                    if (!compress)
                    {
                        settings.format = TextureImporterFormat.RGBA32;
                        settings.textureCompression = TextureImporterCompression.Uncompressed;
                        settings.crunchedCompression = false;
                    }
                    else
                    {
                        // The format is named from the texture's own alpha, not left to
                        // Automatic.
                        //
                        // Automatic was the first attempt, on the reasoning that the importer
                        // already knows whether a texture has alpha. It does not use that
                        // knowledge the way this needs: on WebGL it resolved to DXT1 for the
                        // sprite sheets, DXT1 has no alpha channel, and the cut-out shader's
                        // clip then discarded nothing — so every character in the web build
                        // was drawn as a black rectangle with a sprite inside it. The alpha
                        // was not dimmed or wrong; it was absent, and the sheet's own black
                        // background was what showed.
                        var hasAlpha = importer.DoesSourceTextureHaveAlpha();
                        var crunch = IsPixelArt(path) && !IsNormalMap(path);

                        settings.format = hasAlpha
                            ? (crunch ? TextureImporterFormat.DXT5Crunched : TextureImporterFormat.DXT5)
                            : (crunch ? TextureImporterFormat.DXT1Crunched : TextureImporterFormat.DXT1);
                        settings.textureCompression = TextureImporterCompression.Compressed;
                        settings.crunchedCompression = crunch;
                        settings.compressionQuality = crunch ? 60 : 50;
                    }

                    importer.SetPlatformTextureSettings(settings);
                    importer.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();
            Debug.Log($"[Build] WebGL texture overrides: {changed} set to " +
                      $"{(compress ? "compressed" : "uncompressed")}, {skipped} skipped.");
        }

        private static bool IsPixelArt(string path) =>
            path.StartsWith(SpriteRoot, StringComparison.Ordinal);

        private static bool IsNormalMap(string path) =>
            path.EndsWith("_Normal.png", StringComparison.OrdinalIgnoreCase);
    }
}
