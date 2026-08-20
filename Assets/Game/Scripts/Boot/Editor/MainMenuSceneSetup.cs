using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Builds the title screen's scene.
    ///
    /// Generated rather than hand-authored, like every other scene in this project: a scene
    /// asset edited by hand is one nobody can regenerate, and the whole reason
    /// <see cref="LevelLayoutBuilder"/> exists is that the town used to be one of those. This
    /// one is small enough to read in a single method, which is the point — everything the
    /// title screen is, is here.
    ///
    /// <b>Why GameBoot is in it.</b> The dex, the move pool and the type chart load
    /// synchronously at boot and cost a hitch once. Before this scene existed that hitch landed
    /// on Town.unity, which is to say on the first frame of the game world. Paying it here
    /// instead means it lands while the player is reading a menu, and <c>GameBoot</c> already
    /// persists across the load and stands its own second copy down — so the town's own copy
    /// simply defers to this one.
    ///
    /// Re-running this overwrites the scene. It carries nothing hand-placed, so that is safe.
    /// </summary>
    public static class MainMenuSceneSetup
    {
        public const string ScenePath = "Assets/Game/Scenes/MainMenu.unity";

        [MenuItem("Tools/Poké Lab/Rebuild/Create Main Menu Scene", priority = 12)]
        public static void CreateScene()
        {
            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // A camera, because a scene with none renders the editor's "no cameras rendering"
            // message and a build renders nothing at all. It draws no geometry — the whole
            // screen is a Canvas — so it is a solid colour clear and nothing else.
            var cameraObject = new GameObject("MainMenuCamera", typeof(Camera));
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.043f, 0.055f, 0.078f, 1f);
            camera.orthographic = true;
            camera.cullingMask = 0;
            cameraObject.tag = "MainCamera";

            // The composition root, exactly as the town carries it.
            var boot = new GameObject("GameBoot");
            boot.AddComponent<GameBoot>();

            // The screen itself. MainMenuPresenter builds its own canvas in Start, so there is
            // nothing to wire here — which is deliberate: a serialized reference in a generated
            // scene is a reference that has to be re-made every time it is generated.
            var menu = new GameObject("MainMenu");
            menu.AddComponent<MainMenuPresenter>();

            var directory = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            SceneSetup.AddToBuildSettings();

            Debug.Log($"[Scenes] Built the title screen at {ScenePath}. It is the game's first " +
                      "scene — see GameBuilder.ResolveScenes.");
        }
    }
}
