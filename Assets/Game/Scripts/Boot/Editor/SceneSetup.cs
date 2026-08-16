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
        /// Reimports the art, rebuilds every playable scene from the current layout, repairs
        /// each one's player rig, and leaves the overworld open.
        ///
        /// One button because the order matters and getting it wrong is silent. The art has
        /// to be reimported before the scenes are built, or the ground modules keep the
        /// material they were last bound to — a whole atlas drawn across a ledge. The rig
        /// has to be repaired after the level is built, because building destroys and
        /// recreates the Level root and the spawn marker inside it. And every scene needs
        /// both, not just whichever one happened to be open.
        ///
        /// This is also the answer to "the scene looks stale": foliage is placed against the
        /// height field at emit time and baked into the scene, so terrain that moved after
        /// the last build leaves trees standing in the air until this is run.
        /// </summary>
        [MenuItem("Tools/Poké Lab/Rebuild/Rebuild Everything", priority = 1)]
        public static void RebuildEverything()
        {
            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();

            CreatureImportSettings.ReimportAll();
            AddToBuildSettings();

            var built = 0;
            foreach (var name in AllPlayableScenes())
            {
                var path = SceneDir + name + ".unity";
                if (!File.Exists(path)) continue;

                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                LevelLayoutBuilder.Build(name);
                PlayerRigSetup.CreateRig();
                EditorSceneManager.SaveOpenScenes();
                built++;
            }

            // Finish on the overworld: it is the scene that gets pressed Play on, and
            // leaving whichever scene happened to be last in the list open is how a review
            // ends up looking at the wrong one.
            var overworld = SceneDir + "Overworld.unity";
            if (File.Exists(overworld)) EditorSceneManager.OpenScene(overworld, OpenSceneMode.Single);

            Debug.Log($"[Scenes] Rebuilt {built} scene(s) end to end.");
        }

        private static string[] AllPlayableScenes()
        {
            var all = new System.Collections.Generic.List<string> { "Overworld" };
            all.AddRange(Scenes);
            return all.ToArray();
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
