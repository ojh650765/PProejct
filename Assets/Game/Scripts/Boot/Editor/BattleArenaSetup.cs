using PokeLab.Cinematics;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using CinematicStage = PokeLab.Cinematics.BattleStage;
using Object = UnityEngine.Object;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Builds the arena a battle is performed in, and nothing else in the project builds.
    ///
    /// The simulation was complete and invisible. <c>PokeLab.Battle.BattleStage</c> ran every
    /// encounter to a result, <c>GameFlowController</c> resolved it, and the player watched a
    /// covered screen for the duration — because the entire presentation half
    /// (<see cref="BattlePresenter"/>, <see cref="BattleCameraRig"/>,
    /// <see cref="CinematicStage"/>, two <see cref="CreatureView"/>s) existed in the project
    /// and in no scene. Nothing errored: from the simulation's point of view a battle with no
    /// audience is a battle.
    ///
    /// The arena is built <b>inactive, in the overworld scene</b>, because that is what
    /// <see cref="TransitionDirector"/>'s <c>battleRoot</c>/<c>overworldRoot</c> pair is for —
    /// the director swaps two roots rather than loading a battle scene, which is why
    /// <c>Battle.unity</c> is empty and is meant to stay that way.
    ///
    /// It stands well away from the map rather than on it. The director disables the level
    /// root during a battle, but the player, the NPCs and the lighting director are all
    /// separate roots that stay live, so an arena built on top of the town would have the
    /// frozen player standing in the middle of the shot.
    /// </summary>
    public static class BattleArenaSetup
    {
        private const string RootName = "BattleArena";
        private const string LevelRootName = "Level";

        private const string TerrainMaterial =
            "Assets/Game/Art/Environment/Terrain/Materials/M_Ground_TerrainBlend.mat";

        /// <summary>
        /// Where the arena stands, in world space. Far enough from the 200 m slice that nothing
        /// authored can reach it, and at ground level rather than buried: the terrain shader
        /// derives its wetness and shore band from world height, so an arena sunk below the
        /// map would be lit and shaded as lake bed.
        /// </summary>
        private static readonly Vector3 ArenaOrigin = new Vector3(1000f, 0f, 1000f);

        /// <summary>
        /// Radius of the arena floor, in metres. Solved rather than guessed: at the shipped
        /// framing the camera sits ~7 m back and 2.6 m up, and the top of its frame meets the
        /// ground plane about 24 m out. A disc smaller than that puts a hard edge and a band of
        /// skybox across the back of every battle.
        /// </summary>
        private const float FloorRadius = 30f;

        private const int FloorSegments = 64;

        [MenuItem("Tools/Poké Lab/Rebuild/Create Battle Arena In Open Scene", priority = 13)]
        public static void CreateArena()
        {
            var scene = EditorSceneManager.GetActiveScene();

            var existing = FindArena();
            if (existing != null)
            {
                // Repair rather than refuse, for the same reason PlayerRigSetup does: the scene
                // is rebuilt constantly, and a builder that only works on a scene that has never
                // seen it runs exactly once and is then dead code.
                Repair(scene, existing);
                return;
            }

            var root = new GameObject(RootName);
            root.transform.position = ArenaOrigin;
            SetLayer(root, "BattleStage");

            BuildFloor(root.transform);
            var stage = BuildStage(root.transform);
            var rig = BuildCameraRig(root.transform, stage);
            var presenter = BuildPresenter(root.transform, stage, rig);

            // Last, and it matters that it is last: activating a child of an inactive root does
            // nothing, but adding a component to an *active* root runs nothing either way in the
            // editor, so the only thing this ordering protects is the reader's expectations.
            root.SetActive(false);

            WireTransitionDirector(root, presenter);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[Arena] Built the battle arena in '{scene.name}' at {ArenaOrigin}, inactive.");
        }

        /// <summary>
        /// Brings an already-built arena up to date.
        ///
        /// Every fix here names the symptom it produces when absent, because each one is
        /// something a serialized scene value can be silently wrong about — the scene's copy of
        /// a field always wins over the code default, so changing a default alone looks like a
        /// change that did not work.
        /// </summary>
        private static void Repair(UnityEngine.SceneManagement.Scene scene, CinematicStage stage)
        {
            var repairs = 0;
            var root = ArenaRootOf(stage);

            // A play session that ended in a battle leaves the arena enabled and the level root
            // disabled. Loading that scene gives an overworld with no ground in it.
            if (root.activeSelf)
            {
                root.SetActive(false);
                repairs++;
            }

            if (root.transform.parent == null &&
                (root.transform.position - ArenaOrigin).sqrMagnitude > 0.01f)
            {
                root.transform.position = ArenaOrigin;
                repairs++;
            }

            if (root.transform.Find("Floor") == null)
            {
                BuildFloor(root.transform);
                repairs++;
            }

            // Marks and views. The stage creates whatever is missing at runtime, but only in
            // memory: an arena repaired here can be inspected and nudged, and one that is not
            // has an empty inspector that says nothing about where anything stands.
            repairs += EnsureMarksAndViews(stage);

            var rig = root.GetComponentInChildren<BattleCameraRig>(true);
            if (rig == null)
            {
                rig = BuildCameraRig(root.transform, stage);
                repairs++;
            }
            else
            {
                ApplyRigFraming(rig, StageCenterOf(stage));
                repairs++;
            }

            var presenter = root.GetComponentInChildren<BattlePresenter>(true);
            if (presenter == null)
            {
                presenter = BuildPresenter(root.transform, stage, rig);
                repairs++;
            }
            else
            {
                Assign(presenter, "stage", stage);
                Assign(presenter, "rig", rig);
            }

            repairs += WireTransitionDirector(root, presenter);

            if (repairs == 0)
            {
                Debug.Log($"[Arena] '{scene.name}' already has a complete battle arena.");
                return;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[Arena] Repaired {repairs} thing(s) on the existing arena in '{scene.name}'.");
        }

        // --- Stage ---------------------------------------------------------------------------

        private static CinematicStage BuildStage(Transform parent)
        {
            var go = new GameObject("Stage");
            go.transform.SetParent(parent, false);
            SetLayer(go, "BattleStage");

            var stage = go.AddComponent<CinematicStage>();
            EnsureMarksAndViews(stage);
            return stage;
        }

        /// <summary>
        /// Creates the four marks, the stage centre and the two creature views, at the same
        /// local positions <see cref="CinematicStage"/> would compute for itself.
        ///
        /// The numbers are read back out of the component rather than duplicated here, so a
        /// tuned <c>creatureSeparation</c> in the inspector is what gets built. Only the layout
        /// is authored: everything the presenter needs beyond it — facings, the stage axis, the
        /// burst points — is derived, and the stage rewrites the marks again on send-out to
        /// suit the two creatures actually standing on them.
        /// </summary>
        private static int EnsureMarksAndViews(CinematicStage stage)
        {
            var created = 0;
            var so = new SerializedObject(stage);

            float separation = FloatOf(so, "creatureSeparation", 5.2f);
            float stagger = FloatOf(so, "lateralStagger", 1.15f);
            float setback = FloatOf(so, "trainerSetback", 3.1f);
            float half = separation * 0.5f;

            created += EnsureMark(so, stage, "playerCreatureMark", "Mark_PlayerCreature",
                new Vector3(-stagger, 0f, -half));
            created += EnsureMark(so, stage, "opponentCreatureMark", "Mark_OpponentCreature",
                new Vector3(stagger, 0f, half));
            created += EnsureMark(so, stage, "playerTrainerMark", "Mark_PlayerTrainer",
                new Vector3(-stagger * 1.25f, 0f, -half - setback));
            created += EnsureMark(so, stage, "opponentTrainerMark", "Mark_OpponentTrainer",
                new Vector3(stagger * 1.25f, 0f, half + setback));

            // The midpoint of the two creature marks, which by construction is the stage's own
            // origin. Authored rather than left null, because the camera rig takes this
            // transform as its framing anchor and a null one silently falls back to the rig's
            // own position — several metres out, and the whole field then sits off centre.
            created += EnsureMark(so, stage, "stageCenter", "Mark_StageCenter", Vector3.zero);

            so.ApplyModifiedPropertiesWithoutUndo();

            created += EnsureView(stage, "playerView", "playerCreatureMark", "CreatureView_Player");
            created += EnsureView(stage, "opponentView", "opponentCreatureMark", "CreatureView_Opponent");
            return created;
        }

        private static int EnsureMark(SerializedObject so, CinematicStage stage,
            string field, string markName, Vector3 localPosition)
        {
            var property = so.FindProperty(field);
            if (property == null)
            {
                Debug.LogWarning($"[Arena] BattleStage has no field '{field}'; the mark was not created.");
                return 0;
            }
            if (property.objectReferenceValue != null) return 0;

            var go = new GameObject(markName);
            go.transform.SetParent(stage.transform, false);
            go.transform.localPosition = localPosition;
            SetLayer(go, "BattleStage");
            property.objectReferenceValue = go.transform;
            return 1;
        }

        /// <summary>
        /// Creates a side's creature view on its mark.
        ///
        /// A <see cref="CreatureView"/> needs nothing authored. It builds its own motion root,
        /// its own billboard quad and its own anchors on first use, and the species art arrives
        /// through <c>Bind</c> when the stage seats a creature on the mark — resolved from the
        /// art registry's prefab when there is one and from the sprite manifest when there is
        /// not. What it does need is to <i>exist</i> and to be referenced by the stage: the
        /// stage would otherwise create a pair at runtime, which works and leaves nothing to
        /// inspect, position or override.
        /// </summary>
        private static int EnsureView(CinematicStage stage, string field, string markField, string viewName)
        {
            var so = new SerializedObject(stage);
            var property = so.FindProperty(field);
            if (property == null)
            {
                Debug.LogWarning($"[Arena] BattleStage has no field '{field}'; the view was not created.");
                return 0;
            }
            if (property.objectReferenceValue != null) return 0;

            var markProperty = so.FindProperty(markField);
            var mark = markProperty != null ? markProperty.objectReferenceValue as Transform : null;
            if (mark == null) return 0;

            var go = new GameObject(viewName);
            go.transform.SetParent(mark, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            // The Creature layer, not the arena's. The rig exempts whatever layer its subjects
            // sit on from occlusion resolution, and a creature on the same layer as the floor
            // would make every billboard an obstacle that shoves the camera.
            SetLayer(go, "Creature");

            property.objectReferenceValue = go.AddComponent<CreatureView>();
            so.ApplyModifiedPropertiesWithoutUndo();
            return 1;
        }

        // --- Camera rig -----------------------------------------------------------------------

        private static BattleCameraRig BuildCameraRig(Transform parent, CinematicStage stage)
        {
            var go = new GameObject("BattleCameraRig");
            go.transform.SetParent(parent, false);
            SetLayer(go, "BattleStage");

            var rig = go.AddComponent<BattleCameraRig>();
            ApplyRigFraming(rig, StageCenterOf(stage));
            return rig;
        }

        /// <summary>
        /// The battle framing, written explicitly.
        ///
        /// This is a separate camera set from the exploration rig and shares nothing with it.
        /// The overworld camera is a fixed 8° pitch on an 8.5 m boom and is deliberately not
        /// touched here: a battle framed at exploration angles is a top-down view of two
        /// sprites, and an exploration camera pitched for battle is unplayable.
        ///
        /// The rig builds its own <c>CinemachineCamera</c> per shot on Awake, so there is
        /// nothing to author per shot — only the four numbers that place the single anchor
        /// every shot shares, and the masks that decide what is allowed to push it.
        /// </summary>
        private static void ApplyRigFraming(BattleCameraRig rig, Transform stageCenter)
        {
            var so = new SerializedObject(rig);

            // The brain is what a shot change blends through. Left null the rig finds it on
            // Camera.main at Awake, which works and depends on the tag being right in a scene
            // where two cameras exist.
            var brain = Camera.main != null ? Camera.main.GetComponent<CinemachineBrain>() : null;
            SetObject(so, "brain", brain);
            SetObject(so, "stageCenter", stageCenter);

            SetFloat(so, "layoutYaw", 32f);
            SetFloat(so, "layoutPitch", 20f);
            SetVector2(so, "standoff", new Vector2(1.2f, 2.6f));

            // Occlusion masks. The defaults are "everything is an obstacle, nothing is a
            // subject", which makes the creatures themselves obstacles and lets the decollider
            // shove the camera whenever a billboard's bounds cross it.
            SetLayerMask(so, "obstacleLayers", "Ground", "Environment", "Prop");
            SetLayerMask(so, "subjectLayers", "Creature", "Vfx");
            SetLayerMask(so, "terrainLayers", "Ground", "BattleStage");

            so.ApplyModifiedPropertiesWithoutUndo();

            if (brain == null)
            {
                Debug.LogWarning("[Arena] No CinemachineBrain on the main camera, so the battle rig " +
                                 "was left without one. Build the player rig first — without a brain " +
                                 "nothing blends and the battle cameras are inert.");
            }
        }

        // --- Presenter -------------------------------------------------------------------------

        private static BattlePresenter BuildPresenter(Transform parent, CinematicStage stage, BattleCameraRig rig)
        {
            var go = new GameObject("BattlePresenter");
            go.transform.SetParent(parent, false);
            SetLayer(go, "BattleStage");

            var presenter = go.AddComponent<BattlePresenter>();
            // Both are found by search when left empty, and the search does not look at inactive
            // objects — which the whole arena is until a battle starts.
            Assign(presenter, "stage", stage);
            Assign(presenter, "rig", rig);
            return presenter;
        }

        // --- Transition director ------------------------------------------------------------------

        /// <summary>
        /// Hands the director the two roots it swaps and the presenter it readies behind the
        /// cover.
        ///
        /// The presenter reference is the one that cannot be left to the director's own
        /// fallback: it resolves with <c>FindAnyObjectByType</c>, which does not see inactive
        /// objects, so an arena that is correctly inactive is an arena the director cannot find.
        /// </summary>
        private static int WireTransitionDirector(GameObject battleRoot, BattlePresenter presenter)
        {
            var director = Object.FindFirstObjectByType<TransitionDirector>(FindObjectsInactive.Include);
            if (director == null)
            {
                Debug.LogWarning("[Arena] No TransitionDirector in the scene, so the arena is built " +
                                 "but nothing will ever show it. Run Create Player Rig In Open Scene " +
                                 "first — it is what places the director.");
                return 0;
            }

            Assign(director, "battleRoot", battleRoot);
            Assign(director, "battlePresenter", presenter);

            // The level geometry, and only that. The player, the input reader, the game hosts
            // and the lighting director are separate roots on purpose: disabling the one that
            // carries GameFlowController would stop the coroutine that is waiting for the
            // battle result, and the player would never come back out of the encounter.
            var level = GameObject.Find(LevelRootName);
            if (level != null) Assign(director, "overworldRoot", level);
            else
                Debug.LogWarning($"[Arena] No '{LevelRootName}' root in the scene, so overworldRoot was " +
                                 "left unassigned and the town will be visible behind the battle.");

            return 1;
        }

        // --- Floor -----------------------------------------------------------------------------

        /// <summary>
        /// The ground the battle is fought on.
        ///
        /// It has to be built rather than borrowed because the director disables the level root
        /// for the duration, so the arena takes its own floor with it or the creatures stand in
        /// the skybox. Painted fully grass through vertex colour, which is how the terrain
        /// shader's four layers are weighted, and given the same material the level ground uses
        /// so the battle reads as somewhere in this world rather than as a separate stage.
        /// </summary>
        private static void BuildFloor(Transform parent)
        {
            var mesh = new Mesh { name = "BattleArenaFloor" };

            var vertices = new Vector3[FloorSegments + 1];
            var normals = new Vector3[FloorSegments + 1];
            var uvs = new Vector2[FloorSegments + 1];
            var colours = new Color[FloorSegments + 1];
            var triangles = new int[FloorSegments * 3];

            vertices[0] = Vector3.zero;
            for (var i = 0; i < FloorSegments; i++)
            {
                float angle = i / (float)FloorSegments * Mathf.PI * 2f;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * FloorRadius, 0f, Mathf.Sin(angle) * FloorRadius);

                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = (i + 1) % FloorSegments + 1;
            }

            for (var i = 0; i < vertices.Length; i++)
            {
                normals[i] = Vector3.up;
                // The layer textures are sampled from world position, not from these, so the UVs
                // only ever address the optional control map — which this floor does not use.
                uvs[i] = new Vector2(vertices[i].x, vertices[i].z) * 0.05f;
                colours[i] = new Color(1f, 0f, 0f, 0f);
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.colors = colours;
            mesh.triangles = triangles;
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            var go = new GameObject("Floor");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            var material = AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterial);
            if (material != null) renderer.sharedMaterial = material;
            else
                Debug.LogWarning($"[Arena] {TerrainMaterial} not found, so the arena floor will draw " +
                                 "with the error shader. The battle is still reviewable; the ground is not.");

            // The decollider raycasts down against the terrain layers to keep the camera above
            // ground. Without a collider it has nothing to hit and the rescue silently does
            // nothing, which only shows up as a camera inside the floor on a low shot.
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            SetLayer(go, "Ground");
        }

        // --- Scene helpers -------------------------------------------------------------------------

        private static CinematicStage FindArena()
        {
            var stages = Object.FindObjectsByType<CinematicStage>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            return stages.Length > 0 ? stages[0] : null;
        }

        private static GameObject ArenaRootOf(CinematicStage stage)
        {
            // The stage sits one level under the root the director toggles. Falling back to the
            // stage's own object keeps a hand-authored arena working rather than repairing the
            // wrong object.
            Transform parent = stage.transform.parent;
            return parent != null ? parent.gameObject : stage.gameObject;
        }

        private static Transform StageCenterOf(CinematicStage stage)
        {
            var property = new SerializedObject(stage).FindProperty("stageCenter");
            return property != null ? property.objectReferenceValue as Transform : null;
        }

        /// <summary>
        /// Writes a private serialized field, the way <c>PlayerRigSetup</c> does: these
        /// components expect an inspector to wire them and this scene is built by script, so
        /// this does what a person dragging references would rather than forcing a public
        /// setter onto every field for the sake of a one-time setup.
        /// </summary>
        private static void Assign(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);
            if (property == null)
            {
                Debug.LogWarning($"[Arena] {target.GetType().Name} has no field '{field}'; " +
                                 "it was left unassigned.");
                return;
            }
            property.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetObject(SerializedObject so, string field, Object value)
        {
            var property = so.FindProperty(field);
            if (property != null) property.objectReferenceValue = value;
        }

        private static void SetFloat(SerializedObject so, string field, float value)
        {
            var property = so.FindProperty(field);
            if (property != null) property.floatValue = value;
        }

        private static float FloatOf(SerializedObject so, string field, float fallback)
        {
            var property = so.FindProperty(field);
            return property != null ? property.floatValue : fallback;
        }

        private static void SetVector2(SerializedObject so, string field, Vector2 value)
        {
            var property = so.FindProperty(field);
            if (property != null) property.vector2Value = value;
        }

        private static void SetLayerMask(SerializedObject so, string field, params string[] layers)
        {
            var property = so.FindProperty(field);
            if (property == null) return;

            var mask = 0;
            foreach (var name in layers)
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0) mask |= 1 << layer;
                else Debug.LogWarning($"[Arena] Layer '{name}' is not declared, so it was left out of " +
                                      $"{field}. Add it under Tags and Layers.");
            }
            property.intValue = mask;
        }

        private static void SetLayer(GameObject go, string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer < 0)
            {
                Debug.LogWarning($"[Arena] Layer '{layerName}' is not declared; '{go.name}' was left " +
                                 "on Default. Camera occlusion will treat it as an obstacle.");
                return;
            }
            go.layer = layer;
        }
    }
}
