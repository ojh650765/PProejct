using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Builds the login screen's scene.
    ///
    /// Generated rather than hand-authored, exactly like <see cref="MainMenuSceneSetup"/> and
    /// for the same reason: a scene asset edited by hand is one nobody can regenerate, and this
    /// one carries nothing that is not in this file — a camera, the composition root, and one
    /// presenter that builds its own canvas in <c>Start</c>.
    ///
    /// <b>Why GameBoot is in it.</b> The dex, the move pool and the type chart load
    /// synchronously at boot and cost a hitch once. That hitch used to land on the title screen;
    /// with the login screen in front of it, it lands here instead — while the player is reading
    /// a form and typing a name, which is the best place in the game for it. <c>GameBoot</c>
    /// persists across the load and stands its own second copy down, so the title's copy simply
    /// defers to this one.
    ///
    /// Re-running this overwrites the scene. It carries nothing hand-placed, so that is safe.
    /// </summary>
    public static class LoginSceneSetup
    {
        public const string ScenePath = "Assets/Game/Scenes/Login.unity";

        [MenuItem("Tools/Poké Lab/Rebuild/Create Login Scene", priority = 15)]
        public static void CreateScene()
        {
            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // A camera, because a scene with none renders the editor's "no cameras rendering"
            // message and a build renders nothing at all. It draws no geometry — the whole
            // screen is a Canvas — so it is a solid colour clear and nothing else.
            var cameraObject = new GameObject("LoginCamera", typeof(Camera));
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.043f, 0.055f, 0.078f, 1f);
            camera.orthographic = true;
            camera.cullingMask = 0;
            cameraObject.tag = "MainCamera";

            // The composition root, exactly as the title screen and the town carry it.
            var boot = new GameObject("GameBoot");
            boot.AddComponent<GameBoot>();

            // LoginPresenter builds its own canvas in Start, so there is nothing to wire — which
            // is deliberate: a serialized reference in a generated scene is a reference that has
            // to be re-made every time it is generated.
            var login = new GameObject("Login");
            login.AddComponent<LoginPresenter>();

            var directory = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            SceneSetup.AddToBuildSettings();
            AddSelfToBuildSettings();

            Debug.Log($"[Scenes] Built the login screen at {ScenePath}. Every exit from it leads " +
                      "to MainMenu — see LoginPresenter.");
        }

        /// <summary>
        /// Adds this scene to the build settings.
        ///
        /// Separate from <see cref="SceneSetup.AddToBuildSettings"/> rather than listed inside
        /// it because that method is shared with two other workers right now, and a scene that
        /// is missing from the list is a <c>SceneManager.LoadScene</c> that logs and does
        /// nothing. Adding it from the builder that creates it keeps the two facts — the scene
        /// exists, the scene is buildable — in one place. It is idempotent, so it can also be
        /// folded into the shared list later without producing a duplicate.
        /// </summary>
        private static void AddSelfToBuildSettings()
        {
            if (!File.Exists(ScenePath)) return;

            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Exists(s => s != null && s.path == ScenePath)) return;

            scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            Debug.Log($"[Scenes] Added {ScenePath} to the build settings. It is not yet the " +
                      "FIRST scene — GameBuilder.ResolveScenes still promotes MainMenu, so a " +
                      "WebGL build opens on the title until that is changed.");
        }
    }
}
