using UnityEditor;
using UnityEngine;

namespace PokeLab.Audio.Editor
{
    /// <summary>
    /// Applies the right import settings to everything under Assets/Game/Audio
    /// automatically, so nobody has to click through a hundred and sixteen inspectors --
    /// and so the settings cannot silently drift back to Unity's defaults when the WAVs
    /// are regenerated.
    ///
    /// The policy, and why:
    ///   * SFX are short, fire in bursts, and must not cost a decode when they trigger,
    ///     so they are PCM, decompressed on load, preloaded and forced to mono. Forcing
    ///     mono is free here because the generator already writes them mono; the flag is
    ///     belt and braces against a stereo file sneaking in.
    ///   * Ambience is up to ten simultaneous looping layers, which is too many streams,
    ///     so it is Vorbis held compressed in memory -- one decoder per layer, no disk.
    ///   * Music is at most three simultaneous decks of long stereo material, which is
    ///     exactly what streaming is for.
    ///   * Nothing is resampled: the whole set is authored at 44.1 kHz and the verifier
    ///     asserts it.
    ///
    /// WebGL gets an explicit platform override for every category, because two of the
    /// defaults are wrong in the browser:
    ///   * Music must NOT stream -- Unity does not support the Streaming load type on
    ///     WebGL, and shipping it anyway is how the live build's music turned to noise.
    ///     It becomes Vorbis held compressed in memory (quality 0.5: long stereo
    ///     material, keep the .data download sane), loaded in the background.
    ///   * SFX must NOT be PCM -- 100+ uncompressed WAVs inflate the .data download for
    ///     no gain. They become Vorbis (quality 0.8) still decompressed on load, so the
    ///     trigger cost stays zero after load.
    ///   * Ambience is already right (Vorbis, compressed in memory) but gets the same
    ///     explicit override anyway, so drift in the defaults can never reintroduce
    ///     Streaming on this platform.
    /// </summary>
    public sealed class AudioImportSettings : AssetPostprocessor
    {
        public const string AudioRoot = "Assets/Game/Audio/";
        private const string MusicDir = AudioRoot + "Music/";
        private const string AmbienceDir = AudioRoot + "Ambience/";
        private const string SfxDir = AudioRoot + "SFX/";

        private void OnPreprocessAudio()
        {
            var path = assetPath.Replace('\\', '/');
            if (!path.StartsWith(AudioRoot, System.StringComparison.Ordinal)) return;

            var importer = (AudioImporter)assetImporter;
            Apply(importer, path);
        }

        public static bool Apply(AudioImporter importer, string path)
        {
            var settings = importer.defaultSampleSettings;
            settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;

            bool isMusic = path.StartsWith(MusicDir, System.StringComparison.Ordinal);
            bool isAmbience = path.StartsWith(AmbienceDir, System.StringComparison.Ordinal);
            bool isSfx = path.StartsWith(SfxDir, System.StringComparison.Ordinal);

            if (isMusic)
            {
                settings.loadType = AudioClipLoadType.Streaming;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = 0.7f;
                importer.forceToMono = false;
                importer.loadInBackground = true;
                settings.preloadAudioData = false;
            }
            else if (isAmbience)
            {
                settings.loadType = AudioClipLoadType.CompressedInMemory;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = 0.75f;
                importer.forceToMono = false;
                importer.loadInBackground = true;
                settings.preloadAudioData = false;
            }
            else if (isSfx)
            {
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.PCM;
                settings.quality = 1f;
                importer.forceToMono = true;
                importer.loadInBackground = false;
                settings.preloadAudioData = true;
            }
            else
            {
                return false;
            }

            // Ambience emitters are 3D point sources; Unity spatialises mono only, and the
            // waterfall is the one ambience clip the generator writes mono for that reason.
            importer.ambisonic = false;
            importer.defaultSampleSettings = settings;

            // WebGL override -- see the class comment. Start from the category's default
            // settings and change only what the browser cannot take.
            var webgl = settings;
            if (isMusic)
            {
                // Streaming is not supported on the WebGL platform; keep it compressed
                // in memory instead, at a leaner quality than desktop.
                webgl.loadType = AudioClipLoadType.CompressedInMemory;
                webgl.compressionFormat = AudioCompressionFormat.Vorbis;
                webgl.quality = 0.5f;
                webgl.preloadAudioData = false;
            }
            else if (isSfx)
            {
                // PCM WAVs would bloat the .data download; Vorbis decompressed on load
                // keeps the wire size small and the trigger cost zero.
                webgl.compressionFormat = AudioCompressionFormat.Vorbis;
                webgl.quality = 0.8f;
            }
            // Ambience keeps its default settings verbatim -- the override is set anyway
            // so the platform can never fall back to a drifted default.
            importer.SetOverrideSampleSettings(BuildTargetGroup.WebGL, webgl);
            return true;
        }

        [MenuItem("Tools/Poke Lab/Audio/Reapply Import Settings")]
        public static void ReapplyAll()
        {
            var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Game/Audio" });
            int changed = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (AssetImporter.GetAtPath(path) is not AudioImporter importer) continue;
                    if (!Apply(importer, path)) continue;
                    EditorUtility.SetDirty(importer);
                    importer.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
            Debug.Log($"[PokeLab.Audio] Reapplied import settings to {changed} clip(s).");
        }
    }
}
