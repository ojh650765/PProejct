using System.IO;
using PokeLab.Overworld;
using PokeLab.Vfx;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Builds the things every playable scene needs and that no layout describes.
    ///
    /// The Overworld scene contained a stock Main Camera, a PlayerSpawn empty and the
    /// lighting director — and nothing else. No player, no camera rig, no input reader,
    /// no Cinemachine at all. Pressing Play gave a fixed top-down view of the level
    /// because the camera was simply sitting where it had been left, with nothing driving
    /// it.
    ///
    /// That went unnoticed for a long time for a specific reason worth recording: the
    /// walkthrough capture tool creates its own camera and positions it from the layout,
    /// so every review frame looked correct while the actual scene had no way to be
    /// played at all. A tool that builds its own version of the thing it is inspecting
    /// cannot tell you the real one is missing.
    /// </summary>
    public static class PlayerRigSetup
    {
        private const string ActionsPath = "Assets/InputSystem_Actions.inputactions";

        [MenuItem("Tools/Poké Lab/Rebuild/Create Player Rig In Open Scene", priority = 12)]
        public static void CreateRig()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (Object.FindFirstObjectByType<PlayerLocomotion>() != null)
            {
                Debug.Log($"[Rig] '{scene.name}' already has a player; nothing to do.");
                return;
            }

            var spawn = GameObject.Find("PlayerSpawn");
            var start = spawn != null ? spawn.transform.position : Vector3.zero;

            var input = BuildInput();
            var player = BuildPlayer(start, input);
            var rig = BuildCamera(player, input);
            BuildBrain();
            // Movement is camera-relative, so the locomotion needs the camera it is
            // relative *to*. Assigned after the camera exists rather than before.
            Assign(player, "_cameraTransform", Camera.main != null ? Camera.main.transform : rig.transform);
            BuildHosts(player, input, rig);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[Rig] Built the player rig in '{scene.name}' at {start}.");
        }

        private static OverworldInputReader BuildInput()
        {
            var go = new GameObject("Input");
            var reader = go.AddComponent<OverworldInputReader>();

            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ActionsPath);
            if (actions == null)
            {
                Debug.LogWarning($"[Rig] {ActionsPath} not found. The reader will have no " +
                                 "actions bound and the player will not move — assign one " +
                                 "in the inspector.");
            }
            Assign(reader, "_actions", actions);
            return reader;
        }

        private static PlayerLocomotion BuildPlayer(Vector3 start, OverworldInputReader input)
        {
            var go = new GameObject("Player");
            go.transform.position = start;
            TrySetTag(go, "Player");

            var controller = go.AddComponent<CharacterController>();
            controller.height = 1.7f;
            controller.radius = 0.28f;
            // Centred on the capsule's own middle, so the origin sits at the feet. Every
            // spawn and arrival marker in the layout is a ground position.
            controller.center = new Vector3(0f, 0.85f, 0f);
            controller.slopeLimit = 48f;
            controller.stepOffset = 0.45f;

            // What the camera follows and what the billboard hangs from. Chest height, so
            // the camera frames the character rather than their feet.
            var focus = new GameObject("CameraFocus");
            focus.transform.SetParent(go.transform, false);
            focus.transform.localPosition = new Vector3(0f, 1.15f, 0f);

            var visual = new GameObject("Visual");
            visual.transform.SetParent(go.transform, false);

            var locomotion = go.AddComponent<PlayerLocomotion>();
            Assign(locomotion, "_input", input);
            Assign(locomotion, "_visualRoot", visual.transform);

            go.AddComponent<PlayerInteractor>();

            // Grass parts around whatever walks through it; the player is the first thing
            // that does.
            go.AddComponent<FoliageInteractor>();
            return locomotion;
        }

        private static OverworldCameraRig BuildCamera(PlayerLocomotion player, OverworldInputReader input)
        {
            var focus = player.transform.Find("CameraFocus");

            var go = new GameObject("ExplorationCamera");
            var vcam = go.AddComponent<CinemachineCamera>();
            vcam.Follow = focus;
            vcam.LookAt = focus;
            vcam.Lens.FieldOfView = 40f;

            var orbital = go.AddComponent<CinemachineOrbitalFollow>();
            orbital.OrbitStyle = CinemachineOrbitalFollow.OrbitStyles.Sphere;
            orbital.Radius = 5.5f;

            var rig = go.AddComponent<OverworldCameraRig>();
            Assign(rig, "_input", input);
            Assign(rig, "_camera", vcam);
            Assign(rig, "_followTarget", focus);
            Assign(rig, "_locomotion", player);
            return rig;
        }

        private static void BuildBrain()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                camera = go.AddComponent<Camera>();
            }
            if (camera.GetComponent<CinemachineBrain>() == null)
                camera.gameObject.AddComponent<CinemachineBrain>();

            // Without this the brain has nothing to blend and the camera keeps whatever
            // transform the scene left it at — which is exactly the top-down view.
            camera.transform.SetParent(null);
        }

        private static void BuildHosts(PlayerLocomotion player, OverworldInputReader input,
            OverworldCameraRig rig)
        {
            var go = GameObject.Find("GameHosts") ?? new GameObject("GameHosts");

            if (go.GetComponent<PlayerProfileHost>() == null)
            {
                var profile = go.AddComponent<PlayerProfileHost>();
                Assign(profile, "_player", player);
            }

            if (go.GetComponent<GameFlowController>() == null)
            {
                var flow = go.AddComponent<GameFlowController>();
                Assign(flow, "_player", player);
                Assign(flow, "_input", input);
                Assign(flow, "_cameraRig", rig);
                Assign(flow, "_interactor", player.GetComponent<PlayerInteractor>());
            }

            if (go.GetComponent<FoliageInteractionDirector>() == null)
                go.AddComponent<FoliageInteractionDirector>();

            if (go.GetComponent<EpisodeRunner>() == null)
            {
                var runner = go.AddComponent<EpisodeRunner>();
                var starterGo = new GameObject("StarterSelection");
                starterGo.transform.SetParent(go.transform, false);
                Assign(runner, "_starterSelection", starterGo.AddComponent<StarterSelection>());
                Assign(runner, "_cameraRig", rig);
            }
        }

        /// <summary>
        /// Writes a private serialized field. These components expect the inspector to
        /// wire them, and the scene is built by script — so this does what a person
        /// dragging references would, without needing a public setter on every field
        /// purely so a one-time setup can reach it.
        /// </summary>
        private static void Assign(Object target, string field, object value)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);
            if (property == null)
            {
                Debug.LogWarning($"[Rig] {target.GetType().Name} has no field '{field}'; " +
                                 "it was left unassigned.");
                return;
            }
            property.objectReferenceValue = value as Object;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void TrySetTag(GameObject go, string tag)
        {
            try { go.tag = tag; }
            catch (UnityException)
            {
                Debug.LogWarning($"[Rig] Tag '{tag}' is not declared, so triggers that test " +
                                 "for it — every scene link and cave mouth — will ignore the " +
                                 "player. Add it under Tags and Layers.");
            }
        }
    }
}
