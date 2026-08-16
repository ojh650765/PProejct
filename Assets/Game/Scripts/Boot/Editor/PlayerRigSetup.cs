using System.IO;
using PokeLab.Overworld;
using PokeLab.Overworld.People;
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
                // Repair rather than refuse. This used to return here, which meant a rig
                // built by an older version of this file could never be brought up to date
                // without deleting it by hand — and the person doing that has no way to know
                // which part of it is stale.
                Repair(scene);
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

        /// <summary>
        /// Brings an already-built rig up to date, component by component.
        ///
        /// Everything here is something a previous version of this file got wrong or did
        /// not know to do, so each fix names the symptom it produces when absent.
        /// </summary>
        private static void Repair(UnityEngine.SceneManagement.Scene scene)
        {
            var repairs = 0;

            foreach (var vcam in Object.FindObjectsByType<CinemachineCamera>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                // No aim component: the camera is positioned correctly and points at world
                // +Z regardless, so the player is off the bottom of the frame.
                if (vcam.GetComponent<CinemachineRotationComposer>() == null
                    && vcam.GetComponent<CinemachineHardLookAt>() == null)
                {
                    var composer = vcam.gameObject.AddComponent<CinemachineRotationComposer>();
                    composer.Composition.ScreenPosition = new Vector2(0f, -0.12f);
                    composer.Damping = new Vector2(0.35f, 0.35f);
                    repairs++;
                }

                if (vcam.LookAt == null && vcam.Follow != null)
                {
                    vcam.LookAt = vcam.Follow;
                    repairs++;
                }

                // Framing values already serialized in the scene win over the defaults in
                // OverworldCameraRig, so changing the code alone would leave the old shot in
                // place and look like the change had not worked.
                ApplyFraming(vcam.GetComponent<CinemachineOrbitalFollow>(),
                             vcam.GetComponent<CinemachineRotationComposer>());
                repairs++;
            }

            foreach (var rig in Object.FindObjectsByType<OverworldCameraRig>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (rig.GetComponent<CameraOccluderFade>() == null)
                {
                    rig.gameObject.AddComponent<CameraOccluderFade>();
                    repairs++;
                }

                var so = new SerializedObject(rig);
                SetFloat(so, "_fixedPitch", 8f);
                SetFloat(so, "_minPitch", -12f);
                SetFloat(so, "_maxPitch", 62f);
                var lockYaw = so.FindProperty("_lockYaw");
                if (lockYaw != null) lockYaw.boolValue = false;
                SetFloat(so, "_restDistance", 8.5f);
                SetFloat(so, "_maxDistance", 14f);
                var avoid = so.FindProperty("_avoidObstacles");
                if (avoid != null) avoid.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // The player is a root object, so a level rebuild replaces the ground under it
            // and leaves it wherever it last was — which after a play session is wherever
            // the probe stopped walking. A probe found it parked on the map's north-east
            // corner, two steps from the edge of the height field.
            var spawn = GameObject.Find("PlayerSpawn");
            if (spawn != null)
            {
                foreach (var locomotion in Object.FindObjectsByType<PlayerLocomotion>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if ((locomotion.transform.position - spawn.transform.position).sqrMagnitude < 0.01f)
                        continue;
                    locomotion.transform.position = spawn.transform.position;
                    repairs++;
                }
            }

            // An empty "Visual" child: the player has no sprite and is invisible.
            foreach (var locomotion in Object.FindObjectsByType<PlayerLocomotion>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var visual = locomotion.transform.Find("Visual");
                if (visual == null || visual.GetComponent<PersonBillboard>() != null) continue;

                if (visual.GetComponent<MeshFilter>() == null) visual.gameObject.AddComponent<MeshFilter>();
                if (visual.GetComponent<MeshRenderer>() == null) visual.gameObject.AddComponent<MeshRenderer>();
                SetString(visual.gameObject.AddComponent<PersonBillboard>(), "_personKey", "player");
                repairs++;
            }

            // Billboards serialized with the lean-to-camera experiment still lie flat when
            // the player tilts the view down. The scene's value wins over the code default,
            // so it has to be written, not just changed.
            foreach (var billboard in Object.FindObjectsByType<PersonBillboard>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var bso = new SerializedObject(billboard);
                var lean = bso.FindProperty("_leanToCamera");
                var size = bso.FindProperty("_scale");
                if (lean != null) lean.floatValue = 0f;
                if (size != null) size.floatValue = 0.58f;
                bso.ApplyModifiedPropertiesWithoutUndo();
                repairs++;
            }

            // Everything BuildHosts adds. Repair used to skip this entirely, so every
            // system written after the rig was first created — the dialogue presenter, the
            // encounter director, the surf mount, the world streamer — existed in the
            // project and in no scene. The rig had been built once, so the only path that
            // could place them never ran again.
            //
            // Safe to call on an existing scene: every add in there is guarded by its own
            // GetComponent check, so this fills gaps and changes nothing already present.
            var locomotionForHosts = Object.FindFirstObjectByType<PlayerLocomotion>();
            var readerForHosts = Object.FindFirstObjectByType<OverworldInputReader>();
            var rigForHosts = Object.FindFirstObjectByType<OverworldCameraRig>();
            if (locomotionForHosts != null && readerForHosts != null && rigForHosts != null)
            {
                var before = CountHosts();
                BuildHosts(locomotionForHosts, readerForHosts, rigForHosts);
                repairs += Mathf.Max(0, CountHosts() - before);
            }
            else
            {
                Debug.LogWarning("[Rig] Could not find the player, the input reader and the " +
                                 "camera rig together, so the service hosts were not checked. " +
                                 "Delete the rig and rebuild it if systems are missing.");
            }

            // The player needs the components that answer the world back.
            foreach (var locomotion in Object.FindObjectsByType<PlayerLocomotion>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (locomotion.GetComponent<WaterEdgeResponder>() != null) continue;
                locomotion.gameObject.AddComponent<WaterEdgeResponder>();
                repairs++;
            }

            // A brain-less main camera renders the scene from wherever it was left and
            // ignores every virtual camera in it.
            var main = Camera.main;
            if (main != null && main.GetComponent<CinemachineBrain>() == null)
            {
                main.gameObject.AddComponent<CinemachineBrain>();
                repairs++;
            }

            if (repairs == 0)
            {
                Debug.Log($"[Rig] '{scene.name}' already has a complete player rig.");
                return;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[Rig] Repaired {repairs} thing(s) on the existing rig in '{scene.name}'.");
        }

        /// <summary>
        /// The screen wipe and the director that drives it.
        ///
        /// GameFlowController warns "No transition director; falling back to a timed hold"
        /// and then does exactly that: the screen does not change, the player is frozen for
        /// a fixed number of seconds, and a battle happens somewhere they cannot see. The
        /// director is what turns that hold into a transition, and it was in the project
        /// and in no scene.
        ///
        /// The overlay builds its own canvas and material in Awake, so it needs nothing
        /// authored — only to exist, and to be handed to the director.
        /// </summary>
        private static void BuildTransitions(GameObject hosts, OverworldCameraRig rig)
        {
            var existing = Object.FindFirstObjectByType<PokeLab.Cinematics.TransitionDirector>();
            if (existing == null)
            {
                var go = new GameObject("Transitions");
                go.transform.SetParent(hosts.transform, false);

                var overlay = go.AddComponent<PokeLab.Cinematics.ScreenTransitionOverlay>();
                existing = go.AddComponent<PokeLab.Cinematics.TransitionDirector>();
                Assign(existing, "overlay", overlay);
            }

            // The brain is what a transition blends through; without it the director has
            // nothing to hand the camera over to.
            var camera = Camera.main;
            if (camera != null)
                Assign(existing, "brain", camera.GetComponent<CinemachineBrain>());

            Assign(existing, "overworldFollowCamera", rig.GetComponent<CinemachineCamera>());

            var flow = hosts.GetComponent<GameFlowController>();
            if (flow != null) flow.SetTransitionDirector(existing);
        }

        /// <summary>Components on GameHosts, so Repair can tell whether it added any.</summary>
        private static int CountHosts()
        {
            var go = GameObject.Find("GameHosts");
            return go != null ? go.GetComponents<Component>().Length : 0;
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

            // The visual used to be an empty. Nothing ever put a renderer on it, so the
            // player was an invisible capsule pushing the camera around — and because the
            // capture tool renders the level rather than the game, no review frame ever
            // had a player in it to be missing.
            var visual = new GameObject("Visual");
            visual.transform.SetParent(go.transform, false);
            visual.AddComponent<MeshFilter>();
            visual.AddComponent<MeshRenderer>();
            var billboard = visual.AddComponent<PersonBillboard>();
            SetString(billboard, "_personKey", "player");

            var locomotion = go.AddComponent<PlayerLocomotion>();
            Assign(locomotion, "_input", input);
            Assign(locomotion, "_visualRoot", visual.transform);

            go.AddComponent<PlayerInteractor>();

            // A CharacterController reports its hits to itself, never to what it hit, so the
            // shoreline wall cannot notice the player walking into it. This is the half that
            // can, and without it the player stops at an invisible wall with no explanation
            // — worse than letting them in.
            go.AddComponent<WaterEdgeResponder>();

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

            // Orbital follow is a *position* component and nothing else. Without an aim
            // component the camera is placed correctly and then left pointing wherever its
            // transform happened to face — identity, i.e. down world +Z at a level horizon,
            // with the player somewhere off the bottom of the frame. A play-mode probe
            // measured it: the rig tracked the player to within a centimetre while
            // reporting yaw 0, pitch 0 for every single sample.
            var composer = go.AddComponent<CinemachineRotationComposer>();
            ApplyFraming(orbital, composer);

            var rig = go.AddComponent<OverworldCameraRig>();
            Assign(rig, "_input", input);
            Assign(rig, "_camera", vcam);
            Assign(rig, "_followTarget", focus);
            Assign(rig, "_locomotion", player);

            // The boom never shortens, so the thing in the way has to give way instead.
            go.AddComponent<CameraOccluderFade>();
            return rig;
        }

        /// <summary>
        /// The Diamond/Pearl framing: one angle, one distance, and neither of them moves.
        ///
        /// The aim is undamped on purpose. Rotation damping makes the camera lean into a
        /// turn, which is right for a third-person action camera and wrong here — combined
        /// with a boom that changed length it produced a view whose pitch wandered between
        /// 25 and 60 degrees while walking in a straight line. A fixed overhead camera earns
        /// its readability by being the same shot every time.
        /// </summary>
        private static void ApplyFraming(CinemachineOrbitalFollow orbital,
            CinemachineRotationComposer composer)
        {
            if (orbital != null)
            {
                orbital.OrbitStyle = CinemachineOrbitalFollow.OrbitStyles.Sphere;
                orbital.Radius = 8.5f;
                // Position damping stays: the camera easing after the player is the follow
                // feeling, and it does not change the angle the world is seen from.
                orbital.TrackerSettings.PositionDamping = new Vector3(0.45f, 0.45f, 0.45f);
            }

            if (composer != null)
            {
                // Off-centre vertically so the player sits low in frame and the shot spends
                // its pixels on the ground ahead rather than on sky.
                var composition = composer.Composition;
                composition.ScreenPosition = new Vector2(0f, -0.10f);
                composer.Composition = composition;
                composer.Damping = Vector2.zero;
            }
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

            // The dex has to be up before anything reads it, and GameBoot only exists in
            // the Boot scene. Playing a level scene on its own — which is how the level
            // is actually worked on — meant CreatureFactory fell back to estimated base
            // stats and said so, once per creature in the save. GameBoot is idempotent
            // (it checks PokeLabBootstrap.IsInitialized) so having one in each playable
            // scene costs nothing when the game is entered through Boot.
            if (go.GetComponent<PokeLab.Boot.GameBoot>() == null)
                go.AddComponent<PokeLab.Boot.GameBoot>();

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

            // Conversation. The runner sequences lines and the view draws them, and until
            // now no scene contained either — so every conversation in the game ran to
            // completion invisibly while the player stood frozen watching nothing happen.
            // Neither side errors, because from each one's point of view it worked.
            if (go.GetComponent<DialogueRunner>() == null)
            {
                var dialogue = go.AddComponent<DialogueRunner>();
                Assign(dialogue, "_input", input);
                Assign(dialogue, "_player", player);
                Assign(dialogue, "_interactor", player.GetComponent<PlayerInteractor>());
            }

            if (go.GetComponent<PokeLab.Boot.DialoguePresenter>() == null)
                go.AddComponent<PokeLab.Boot.DialoguePresenter>();

            // Wild encounters. Without one, walking through tall grass is walking through
            // grass.
            if (go.GetComponent<EncounterDirector>() == null)
                go.AddComponent<EncounterDirector>();

            // The creature that carries the player over water, and its wake. Watches the
            // traversal event, so every lake and river in every scene gets it without any
            // of them knowing it exists.
            if (go.GetComponent<PokeLab.Boot.SurfMount>() == null)
                go.AddComponent<PokeLab.Boot.SurfMount>();

            // Streams the neighbouring band in around the player. Both halves of the map are
            // emitted into one world space, so this turns the boundary from a scene load
            // into a place where the player keeps walking.
            if (go.GetComponent<PokeLab.Overworld.World.WorldStreamer>() == null)
                go.AddComponent<PokeLab.Overworld.World.WorldStreamer>();

            // The battle. BattleStage is pure C# and registers itself through this host, so
            // without the host in the scene IGameFlow finds no stage and every encounter —
            // wild or trainer — resolves as a silent simulation the player never sees.
            if (go.GetComponent<PokeLab.Battle.BattleStageHost>() == null)
                go.AddComponent<PokeLab.Battle.BattleStageHost>();

            BuildTransitions(go, rig);

            // Lets a door cover the screen. Without it LevelTransition waits out its fade
            // duration and then hard-cuts, which is a teleport with a pause in front of it.
            if (go.GetComponent<PokeLab.Boot.ScreenCoverHost>() == null)
                go.AddComponent<PokeLab.Boot.ScreenCoverHost>();

            if (go.GetComponent<PokeLab.Boot.StarterPresenter>() == null)
                go.AddComponent<PokeLab.Boot.StarterPresenter>();
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

        private static void SetFloat(SerializedObject so, string field, float value)
        {
            var property = so.FindProperty(field);
            if (property != null) property.floatValue = value;
        }

        /// <summary>Writes a private serialized string, the same way <see cref="Assign"/> does.</summary>
        private static void SetString(Object target, string field, string value)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);
            if (property == null)
            {
                Debug.LogWarning($"[Rig] {target.GetType().Name} has no field '{field}'.");
                return;
            }
            property.stringValue = value;
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
