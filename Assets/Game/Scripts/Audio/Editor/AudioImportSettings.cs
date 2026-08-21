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
    ///     so they are PCM, decompressed on load and forced to mono. Forcing mono is
    ///     free here because the generator already writes them mono; the flag is belt
    ///     and braces against a stereo file sneaking in. They are NOT engine-preloaded:
    ///     on the web the engine's scene-load preload queries each clip's length before
    ///     the browser has decoded it -- one warning per clip, once per boot, ninety-odd
    ///     lines before any script runs -- so preload is off everywhere and AudioDirector
    ///     warms the catalogue itself the moment it wakes, a few clips per frame.
    ///   * Ambience is up to ten simultaneous looping layers, which is too many streams,
    ///     so it is Vorbis held compressed in memory -- one decoder per layer, no disk.
    ///   * Music is at most three simultaneous decks of long stereo material, which is
    ///     exactly what streaming is for -- ON A DESKTOP. The build target here is WebGL,
    ///     where Unity does not implement the Streaming load type at all: the clip ends up
    ///     behind an HTML media element that is handed something the browser will not decode,
    ///     and the whole player dies with "NotSupportedError: Failed to load because no
    ///     supported source was found" at the moment the first track is asked for. That
    ///     moment is entering story mode, because the prologue opens on the professor's
    ///     introduction. So music is held compressed in memory instead, which WebGL does
    ///     implement; preloadAudioData stays false, so a track still costs nothing until
    ///     somebody asks for it.
    ///   * Nothing is resampled: the whole set is authored at 44.1 kHz and the verifier
    ///     asserts it.
    ///
    /// There is deliberately no WebGL platform override. The importer refuses one --
    /// SetOverrideSampleSettings returns false for "WebGL"/"Web" while accepting
    /// "Standalone" (verified against this install, 6000.3.6f1) -- because the web
    /// pipeline re-encodes audio for the browser's decoder and ignores load type and
    /// quality per clip. Most browser-side faults that looked like import problems were
    /// runtime ones -- AudioSources seeked into or started clips whose data had not
    /// loaded yet -- but one was genuinely import-borne: preloadAudioData had the
    /// engine itself asking every SFX clip its length during scene deserialization,
    /// which is why nothing here preloads any more.
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
                // NOT Streaming. See the class comment: WebGL has no implementation of it and
                // the player aborts on the first track rather than degrading.
                settings.loadType = AudioClipLoadType.CompressedInMemory;
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
                importer.loadInBackground = true;
                settings.preloadAudioData = false;
            }
            else
            {
                return false;
            }

            // Ambience emitters are 3D point sources; Unity spatialises mono only, and the
            // waterfall is the one ambience clip the generator writes mono for that reason.
            importer.ambisonic = false;
            importer.defaultSampleSettings = settings;
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
