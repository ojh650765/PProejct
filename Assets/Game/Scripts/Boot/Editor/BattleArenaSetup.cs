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
    /// <b>The arena is staged in the live world, not in a set.</b> It is built inactive in the
    /// overworld scene and <see cref="TransitionDirector"/> stands it up at the spot the
    /// encounter fired, with the level left switched on behind it. The alternative — four
    /// authored dioramas, one per zone, switched in against a disabled overworld — was rejected
    /// for one concrete reason: the level is regenerated from the layout tool constantly, so
    /// four hand-built copies of it would be four things to notice had gone stale, and the
    /// backdrop would be a place that resembles where the player is standing rather than the
    /// place itself. Staging in the world makes a cave fight happen in the cave for free, and
    /// correctly for zones nobody has authored yet.
    ///
    /// What is authored is what the world cannot supply: the marks, the camera, and the lit
    /// field disc under each combatant — see <see cref="BattleFieldDisc"/> for why a sprite
    /// needs one.
    ///
    /// Follows the shape of <c>PlayerRigSetup</c>, including the repair path, because the scene
    /// is rebuilt constantly and a builder that only works on a scene that has never seen it
    /// runs exactly once and is then dead code.
    /// </summary>
    public static class BattleArenaSetup
    {
        private const string RootName = "BattleArena";

        /// <summary>The material the level ground is drawn with. The field discs share it so their grain continues the ground's.</summary>
        private const string TerrainMaterial =
            "Assets/Game/Art/Environment/Terrain/Materials/M_Ground_TerrainBlend.mat";

        [MenuItem("Tools/Poké Lab/Rebuild/Create Battle Arena In Open Scene", priority = 13)]
        public static void CreateArena()
        {
            var scene = EditorSceneManager.GetActiveScene();

            var existing = FindArena();
            if (existing != null)
            {
                Repair(scene, existing);
                return;
            }

            var root = new GameObject(RootName);
            SetLayer(root, "BattleStage");

            var stage = BuildStage(root.transform);
            var rig = BuildCameraRig(root.transform, stage);
            var presenter = BuildPresenter(root.transform, stage, rig);

            root.SetActive(false);
            WireTransitionDirector(root, presenter);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[Arena] Built the battle arena in '{scene.name}', inactive. " +
                      "It is placed in the world when an encounter fires.");
        }

        /// <summary>
        /// Brings an already-built arena up to date.
        ///
        /// Every fix names the symptom it produces when absent, because each one is something a
        /// serialized scene value can be silently wrong about — the scene's copy of a field
        /// always wins over the code default, so changing a default alone looks like a change
        /// that did not work.
        /// </summary>
        private static void Repair(UnityEngine.SceneManagement.Scene scene, CinematicStage stage)
        {
            var repairs = 0;
            var root = ArenaRootOf(stage);

            // A play session that ended in a battle leaves the arena enabled, standing in the
            // middle of the map where the last encounter happened.
            if (root.activeSelf)
            {
                root.SetActive(false);
                repairs++;
            }

            // An arena left standing where the last battle happened, or parked off the map by an
            // older version of this file that built a set instead of staging in the world. The
            // stage is moved to the encounter on every battle, so the authored position is only
            // ever what someone opening the scene sees.
            if (root.transform.parent == null && root.transform.position != Vector3.zero)
            {
                root.transform.position = Vector3.zero;
                repairs++;
            }
            if (stage.transform.localPosition != Vector3.zero)
            {
                stage.transform.localPosition = Vector3.zero;
                repairs++;
            }

            repairs += EnsureMarksViewsAndDiscs(stage);

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

            // Always re-wired. The level builder destroys and rebuilds the 'Level' root, which
            // nulls anything pointing at it, and the arena root is a play-mode casualty in the
            // same way — so a scene that has been rebuilt since the arena was built has a
            // director holding references to objects that no longer exist.
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
            EnsureMarksViewsAndDiscs(stage);
            return stage;
        }

        /// <summary>
        /// Creates the four marks, the stage centre, the two creature views and the two field
        /// discs, at the same local positions <see cref="CinematicStage"/> would compute for
        /// itself.
        ///
        /// The numbers are read back out of the component rather than duplicated here, so a
        /// tuned <c>creatureSeparation</c> in the inspector is what gets built. Only the layout
        /// is authored: the facings, the stage axis and the burst points are derived, and the
        /// stage rewrites the marks again on send-out to suit the two creatures actually
        /// standing on them.
        /// </summary>
        private static int EnsureMarksViewsAndDiscs(CinematicStage stage)
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
            // origin. Authored rather than left null, because the camera rig takes this transform
            // as its framing anchor and a null one silently falls back to the rig's own position
            // — several metres out, and the whole field then sits off centre.
            created += EnsureMark(so, stage, "stageCenter", "Mark_StageCenter", Vector3.zero);

            so.ApplyModifiedPropertiesWithoutUndo();

            created += EnsureView(stage, "playerView", "playerCreatureMark", "CreatureView_Player");
            created += EnsureView(stage, "opponentView", "opponentCreatureMark", "CreatureView_Opponent");

            created += EnsureDisc(stage, "playerDisc", "playerCreatureMark", "FieldDisc_Player");
            created += EnsureDisc(stage, "opponentDisc", "opponentCreatureMark", "FieldDisc_Opponent");
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

            var mark = TransformOf(so, markField);
            if (mark == null) return 0;

            var go = new GameObject(viewName);
            go.transform.SetParent(mark, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            // The Creature layer, not the arena's. The rig exempts whatever layer its subjects
            // sit on from occlusion resolution, and a creature sharing a layer with the scenery
            // would be an obstacle that shoves the camera off its authored angle.
            SetLayer(go, "Creature");

            property.objectReferenceValue = go.AddComponent<CreatureView>();
            so.ApplyModifiedPropertiesWithoutUndo();
            return 1;
        }

        private static int EnsureDisc(CinematicStage stage, string field, string markField, string discName)
        {
            var so = new SerializedObject(stage);
            var property = so.FindProperty(field);
            if (property == null) return 0;

            var material = AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterial);
            if (material == null)
            {
                Debug.LogWarning($"[Arena] {TerrainMaterial} not found, so the field discs will draw " +
                                 "with the error shader instead of the ground's own material.");
            }

            if (property.objectReferenceValue is BattleFieldDisc present)
            {
                // The material is re-applied on repair: the disc has to be made of the same thing
                // the ground is, and a level rebuild can retarget which material that is.
                Assign(present, "groundMaterial", material);
                return 0;
            }

            var mark = TransformOf(so, markField);
            if (mark == null) return 0;

            var go = new GameObject(discName);
            go.transform.SetParent(mark, false);
            go.transform.localPosition = Vector3.zero;
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            SetLayer(go, "Ground");

            var disc = go.AddComponent<BattleFieldDisc>();
            Assign(disc, "groundMaterial", material);

            property.objectReferenceValue = disc;
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
        /// The battle framing, written explicitly, and it is a different camera from the one the
        /// player explores with.
        ///
        /// The overworld rig is a fixed 8° pitch on an 8.5 m boom and is deliberately not touched
        /// here — a battle framed at exploration angles is a plan view of two sprites. The
        /// reference framing is lower, closer and turned off the axis, so the near combatant is
        /// visibly larger than the far one and the shot reads as real perspective rather than as
        /// an isometric layout.
        ///
        /// The three numbers below are the whole of it, and each one has a failure it is set
        /// against:
        ///
        /// <list type="bullet">
        /// <item><b>Yaw 18°.</b> What separates the two combatants diagonally in frame; at 0 they
        /// stack exactly, because both marks lie on the stage axis by definition. It is set well
        /// below the rig's shipped 32° for a reason that is invisible until you look at a frame:
        /// the sprite billboard chooses between the drawn front and back views by sector, and at
        /// 32° the player's creature lands within a degree of the front/back boundary and is
        /// drawn as a <i>mirrored side view</i> instead of its back. Here it sits 10° clear of
        /// the boundary, so the back/front pair the sprites are drawn for comes out right.</item>
        /// <item><b>Pitch 18°.</b> Inside the HD-2D band, and the low end of it is not free: the
        /// arena stands in the live world, so a camera at creature height looks through whatever
        /// the player was walking in. This is high enough to see over waist-high foliage and low
        /// enough that the shot is still a perspective view rather than a plan.</item>
        /// <item><b>Standoff (0.9, 2.0).</b> Close, which is what makes perspective do the work:
        /// at the shipped separation the near combatant is drawn about 1.9× the far one.</item>
        /// </list>
        ///
        /// The rig builds its own <c>CinemachineCamera</c> per shot on Awake, so there is nothing
        /// to author per shot — only the anchor every shot shares and the masks that decide what
        /// is allowed to push it.
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

            SetFloat(so, "layoutYaw", 18f);
            SetFloat(so, "layoutPitch", 18f);
            SetVector2(so, "standoff", new Vector2(0.9f, 2.0f));

            // Mirrored, which puts the player's creature near-left and the opponent far-right —
            // the series layout, and the one the sprite pair is drawn for. Unmirrored the sides
            // are swapped and the player's creature is the one on the right.
            SetBool(so, "mirrorLayout", true);

            // Occlusion masks. The defaults are "everything is an obstacle, nothing is a
            // subject", which makes the creatures themselves obstacles and lets the decollider
            // shove the camera whenever a billboard's bounds cross it. It matters more now the
            // arena stands in the live world, where there really is scenery around it.
            SetLayerMask(so, "obstacleLayers", "Ground", "Environment", "Prop");
            SetLayerMask(so, "subjectLayers", "Creature", "Vfx");
            SetLayerMask(so, "terrainLayers", "Ground");

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
        /// Hands the director the arena it stands up and the presenter it readies behind the
        /// cover.
        ///
        /// The presenter reference is the one that cannot be left to the director's own fallback:
        /// it resolves with <c>FindAnyObjectByType</c>, which does not see inactive objects, so an
        /// arena that is correctly inactive is an arena the director cannot find.
        ///
        /// <c>overworldRoot</c> is cleared rather than assigned, and that is the whole in-world
        /// decision expressed in one line: assigning it switches the level off for the duration
        /// of the battle, which is exactly the backdrop the fight is supposed to be happening in
        /// front of. It stays on the component for the other configuration — a dedicated set
        /// standing somewhere the player never walks — and this builder does not produce one.
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
            Assign(director, "overworldRoot", null);
            return 1;
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
            => TransformOf(new SerializedObject(stage), "stageCenter");

        private static Transform TransformOf(SerializedObject so, string field)
        {
            var property = so.FindProperty(field);
            return property != null ? property.objectReferenceValue as Transform : null;
        }

        /// <summary>
        /// Writes a private serialized field, the way <c>PlayerRigSetup</c> does: these
        /// components expect an inspector to wire them and this scene is built by script, so this
        /// does what a person dragging references would rather than forcing a public setter onto
        /// every field for the sake of a one-time setup.
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

        private static void SetBool(SerializedObject so, string field, bool value)
        {
            var property = so.FindProperty(field);
            if (property != null) property.boolValue = value;
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
