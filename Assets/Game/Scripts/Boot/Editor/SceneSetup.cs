using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Creates the per-scene files the split layout expects.
    ///
    /// The emitter writes one layout per scene — Town and Field — but a layout is data
    /// and Unity needs an actual scene asset to build it into. There was only
    /// Overworld.unity, so the field layout had nowhere to go and the town's own scene
    /// did not exist either.
    ///
    /// Each new scene is copied from Overworld rather than created empty, because
    /// Overworld already carries the pieces neither layout describes: the camera rig,
    /// the lighting director, the player, the flow controller and the service hosts. A
    /// blank scene would need all of that rebuilt by hand and would drift from the
    /// original the moment either was edited.
    /// </summary>
    public static class SceneSetup
    {
        private const string SceneDir = "Assets/Game/Scenes/";
        private const string Template = SceneDir + "Overworld.unity";

        private static readonly string[] Scenes = { "Town", "Field" };

        [MenuItem("Tools/Poké Lab/Rebuild/Create Town and Field Scenes", priority = 10)]
        public static void CreateScenes()
        {
            if (!File.Exists(Template))
            {
                Debug.LogError($"[Scenes] {Template} is missing, so there is nothing to copy from.");
                return;
            }

            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();

            var made = 0;
            foreach (var name in Scenes)
            {
                var path = SceneDir + name + ".unity";
                if (File.Exists(path))
                {
                    Debug.Log($"[Scenes] {path} already exists; left alone.");
                    continue;
                }

                if (!AssetDatabase.CopyAsset(Template, path))
                {
                    Debug.LogError($"[Scenes] Could not copy {Template} to {path}.");
                    continue;
                }
                made++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            AddToBuildSettings();

            // Built after the copies exist, so each scene gets its own half of the level
            // rather than whatever the template happened to be carrying.
            foreach (var name in Scenes)
            {
                var path = SceneDir + name + ".unity";
                if (!File.Exists(path)) continue;
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                LevelLayoutBuilder.Build(name);
                EditorSceneManager.SaveOpenScenes();
            }

            Debug.Log($"[Scenes] Created {made} scene(s) and built each one's layout.");
        }

        /// <summary>
        /// Every scene a LevelTransition can load has to be in the build settings, or
        /// walking into a door does nothing and the failure surfaces a long way from its
        /// cause. LevelTransition checks for this at runtime and says so; this stops it
        /// having to.
        /// </summary>
        [MenuItem("Tools/Poké Lab/Rebuild/Add Scenes To Build Settings", priority = 11)]
        public static void AddToBuildSettings()
        {
            var wanted = new System.Collections.Generic.List<string> { Template };
            foreach (var name in Scenes)
            {
                var path = SceneDir + name + ".unity";
                if (File.Exists(path)) wanted.Add(path);
            }
            foreach (var extra in new[] { SceneDir + "Boot.unity", SceneDir + "Battle.unity" })
                if (File.Exists(extra)) wanted.Add(extra);

            var existing = new System.Collections.Generic.List<EditorBuildSettingsScene>(
                EditorBuildSettings.scenes);
            var added = 0;
            foreach (var path in wanted)
            {
                if (existing.Exists(s => s.path == path)) continue;
                existing.Add(new EditorBuildSettingsScene(path, true));
                added++;
            }

            EditorBuildSettings.scenes = existing.ToArray();
            Debug.Log($"[Scenes] Build settings hold {existing.Count} scene(s); added {added}.");
        }
    }
}
