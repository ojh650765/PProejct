using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Audio.Editor
{
    /// <summary>
    /// Builds <see cref="AudioClipCatalog"/> from <c>Assets/Game/Audio/audio_manifest.json</c>.
    ///
    /// The manifest is the single source of truth: it is written by the same script that
    /// synthesises the WAVs, so it cannot drift from what is actually on disk. Rebuilding
    /// is idempotent and safe to run after every regeneration.
    ///
    /// The validator alongside it checks the other direction -- that every clip name
    /// spelled in <see cref="AudioIds"/> actually resolves -- which is what catches a
    /// generator rename before it becomes a silent missing sound in a build.
    /// </summary>
    public static class AudioCatalogBuilder
    {
        public const string ManifestPath = "Assets/Game/Audio/audio_manifest.json";
        /// <summary>
        /// Inside a Resources folder, because that is the only lookup a player has.
        ///
        /// It used to sit one level up. Nothing referenced it from a scene either, so a build
        /// contained no catalogue at all and every sound in the game was a silent no-op —
        /// reported once, into a console nobody reads, as "No AudioClipCatalog assigned".
        /// The same shape as the dialogue book and the string table before them.
        /// </summary>
        public const string CatalogPath = "Assets/Game/Audio/Resources/AudioClipCatalog.asset";

        /// <summary>Name the catalogue is loaded under at runtime.</summary>
        public const string CatalogResourceName = "AudioClipCatalog";

        [Serializable]
        private sealed class ManifestClip
        {
            public string name;
            public string path;
            public string category;
            public bool loop;
            public float duration;
            public int channels;
            public int sample_rate;
            public string trigger;
        }

        [Serializable]
        private sealed class Manifest
        {
            public int schema;
            public int sample_rate;
            public int clip_count;
            public List<ManifestClip> clips = new List<ManifestClip>();
        }

        [MenuItem("Tools/Poke Lab/Audio/Rebuild Catalogue")]
        public static void Rebuild()
        {
            if (!File.Exists(ManifestPath))
            {
                Debug.LogError($"[PokeLab.Audio] No manifest at {ManifestPath}. " +
                               "Run Tools/Audio/build_all.py first.");
                return;
            }

            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
            if (manifest?.clips == null || manifest.clips.Count == 0)
            {
                Debug.LogError("[PokeLab.Audio] Manifest parsed but contained no clips.");
                return;
            }

            var catalog = AssetDatabase.LoadAssetAtPath<AudioClipCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<AudioClipCatalog>();
                Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath) ?? "Assets");
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            var entries = new List<AudioClipCatalog.Entry>(manifest.clips.Count);
            var missing = new List<string>();

            foreach (var clip in manifest.clips)
            {
                if (string.IsNullOrEmpty(clip.name) || string.IsNullOrEmpty(clip.path)) continue;
                var asset = AssetDatabase.LoadAssetAtPath<AudioClip>(clip.path);
                if (asset == null)
                {
                    missing.Add($"{clip.name} ({clip.path})");
                    continue;
                }

                if (Mathf.Abs(asset.length - clip.duration) > 0.02f)
                    Debug.LogWarning($"[PokeLab.Audio] {clip.name}: imported length {asset.length:0.000}s " +
                                     $"differs from manifest {clip.duration:0.000}s. Stale import?");

                entries.Add(new AudioClipCatalog.Entry
                {
                    Name = clip.name,
                    Clip = asset,
                    Bus = BusFor(clip.category),
                    Loop = clip.loop,
                    Gain = 1f,
                });
            }

            catalog.SetEntries(entries);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[PokeLab.Audio] Catalogue rebuilt: {entries.Count} clip(s) at {CatalogPath}.");
            if (missing.Count > 0)
                Debug.LogError($"[PokeLab.Audio] {missing.Count} manifest clip(s) not found as assets:\n" +
                               string.Join("\n", missing));
        }

        /// <summary>
        /// Category to bus. Scanner cues ride the UI bus deliberately: the scanner is the
        /// player's device rather than something happening on the field, so it should
        /// follow the UI volume slider and stay clear of the SFX ducking.
        /// </summary>
        private static AudioBus BusFor(string category)
        {
            if (string.IsNullOrEmpty(category)) return AudioBus.Sfx;
            if (category.StartsWith("Music", StringComparison.OrdinalIgnoreCase)) return AudioBus.Music;
            if (category.StartsWith("Ambience", StringComparison.OrdinalIgnoreCase)) return AudioBus.Ambience;
            if (category.EndsWith("/UI", StringComparison.OrdinalIgnoreCase)) return AudioBus.Ui;
            if (category.EndsWith("/Scanner", StringComparison.OrdinalIgnoreCase)) return AudioBus.Ui;
            return AudioBus.Sfx;
        }

        [MenuItem("Tools/Poke Lab/Audio/Validate Catalogue")]
        public static void Validate()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AudioClipCatalog>(CatalogPath);
            if (catalog == null)
            {
                Debug.LogError($"[PokeLab.Audio] No catalogue at {CatalogPath}. Rebuild it first.");
                return;
            }

            var problems = new List<string>();

            foreach (var broken in catalog.FindBrokenEntries())
                problems.Add($"entry '{broken}' has no clip assigned");

            // Every constant and array in AudioIds must resolve. This is the check that
            // turns a rename in the Python generator into an editor error rather than a
            // sound that silently never plays.
            foreach (var name in AllDeclaredIds())
            {
                if (!catalog.TryGet(name, out _)) problems.Add($"AudioIds references missing clip '{name}'");
            }

            foreach (var type in Enum.GetValues(typeof(Core.ElementType)).Cast<Core.ElementType>())
            {
                if (type == Core.ElementType.None) continue;
                if (!catalog.TryGet(AudioIds.MoveCast(type), out _))
                    problems.Add($"no cast cue resolves for ElementType.{type}");
                if (!catalog.TryGet(AudioIds.MoveImpact(type), out _))
                    problems.Add($"no impact cue resolves for ElementType.{type}");
            }

            foreach (FootstepSurface surface in Enum.GetValues(typeof(FootstepSurface)))
            {
                for (int v = 0; v < AudioIds.FootstepVariants; v++)
                {
                    var id = AudioIds.Footstep(surface, v);
                    if (!catalog.TryGet(id, out _)) problems.Add($"missing footstep '{id}'");
                }
            }

            if (problems.Count == 0)
                Debug.Log($"[PokeLab.Audio] Catalogue valid: {catalog.Entries.Count} clips, " +
                          "every AudioIds reference resolves.");
            else
                Debug.LogError($"[PokeLab.Audio] Catalogue has {problems.Count} problem(s):\n" +
                               string.Join("\n", problems));
        }

        /// <summary>Every clip-name constant and string array declared on <see cref="AudioIds"/>.</summary>
        private static IEnumerable<string> AllDeclaredIds()
        {
            var t = typeof(AudioIds);
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType == typeof(string) && f.IsLiteral)
                {
                    var value = (string)f.GetRawConstantValue();
                    // biome ids are not clip names
                    if (value != null && (value.StartsWith("Music_") || value.StartsWith("SFX_") ||
                                          value.StartsWith("Amb_")))
                        yield return value;
                }
                else if (f.FieldType == typeof(string[]))
                {
                    var arr = (string[])f.GetValue(null);
                    if (arr == null) continue;
                    foreach (var v in arr) yield return v;
                }
            }
        }
    }
}
