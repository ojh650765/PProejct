using System.Collections.Generic;
using System.IO;
using PokeLab.Cinematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using CinematicStage = PokeLab.Cinematics.BattleStage;
using Object = UnityEngine.Object;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Builds <c>Battle.unity</c>: the level a battle is performed in, and the only level in the
    /// project that is not emitted from the layout tool.
    ///
    /// <b>The arena is a set, not a spot in the world.</b> It used to be staged in the live
    /// overworld at the place the encounter fired, and the argument for that was good — a cave
    /// fight happened in the cave for free, with nothing to keep in sync with a level that is
    /// regenerated constantly. It cost one thing that could not be bought back. The battle camera
    /// has to move, and the world was not built to be moved through: ground cover is submitted
    /// with <c>Graphics.DrawMeshInstanced</c>, so it has no renderers and no colliders and there
    /// is nothing for the deoccluder to push the camera out of. In dense grass the shot framed
    /// through fronds. No amount of work on the camera fixes a backdrop that cannot be cleared,
    /// so the backdrop became something authored instead — four of them, one per zone, each built
    /// knowing exactly where the camera will stand in it.
    ///
    /// The set is authored at <see cref="ArenaOrigin"/> rather than at the scene's origin. The
    /// battle level is loaded <i>beside</i> the level the player is standing in — see
    /// <see cref="BattleArena"/> for why it cannot replace it — so the two share a world, and two
    /// kilometres is what keeps the arena out of the town's back gardens.
    ///
    /// Follows the shape of <c>PlayerRigSetup</c>, including the repair path, because the scenes
    /// are rebuilt constantly and a builder that only works on a scene that has never seen it
    /// runs exactly once and is then dead code.
    /// </summary>
    public static class BattleArenaSetup
    {
        private const string RootName = "BattleArena";
        private const string BattleScenePath = "Assets/Game/Scenes/Battle.unity";

        /// <summary>The material the level ground is drawn with. The arena floor and the field
        /// discs share it, so the discs' grain continues the floor's.</summary>
        private const string TerrainMaterial =
            "Assets/Game/Art/Environment/Terrain/Materials/M_Ground_TerrainBlend.mat";

        private const string WaterMaterial = "Assets/Game/Shaders/Materials/M_Water_Lake.mat";

        /// <summary>
        /// Where the whole set stands, in the world both scenes share.
        ///
        /// Far enough that no draw distance, no fog fade and no streamed band can reach it, and
        /// round enough to recognise in the inspector when something has been left behind there.
        /// Float precision at two kilometres is a tenth of a millimetre, which is four orders of
        /// magnitude below anything the arena measures.
        /// </summary>
        private static readonly Vector3 ArenaOrigin = new Vector3(2000f, 0f, 2000f);

        [MenuItem("Tools/Poké Lab/Rebuild/Build Battle Arena Scene", priority = 13)]
        public static void CreateArena()
        {
            var active = EditorSceneManager.GetActiveScene();

            if (IsBattleScene(active))
            {
                Build(active);
                EditorSceneManager.MarkSceneDirty(active);
                EditorSceneManager.SaveOpenScenes();
                return;
            }

            if (!File.Exists(BattleScenePath))
            {
                Debug.LogError($"[Arena] {BattleScenePath} is missing, so there is nowhere to build " +
                               "the arena. Restore the scene asset before rebuilding.");
                return;
            }

            // Opened alongside rather than instead of the scene the caller is working in. This is
            // called from PlayerRigSetup in the middle of a whole-project rebuild, which opens
            // each playable scene in turn and saves it afterwards — switching the active scene
            // out from under that loop would save the arena over the level.
            var battle = EditorSceneManager.OpenScene(BattleScenePath, OpenSceneMode.Additive);
            Build(battle);
            EditorSceneManager.MarkSceneDirty(battle);
            EditorSceneManager.SaveScene(battle);
            EditorSceneManager.CloseScene(battle, true);

            int stripped = StripInWorldArena(active);
            if (stripped > 0)
            {
                EditorSceneManager.MarkSceneDirty(active);
                EditorSceneManager.SaveOpenScenes();
            }
        }

        /// <summary>
        /// Creates or repairs the whole arena inside one scene.
        ///
        /// The layout half — marks, views, discs, rig, presenter — is repaired in place, because
        /// every one of those carries values somebody may have tuned in the inspector and the
        /// scene's copy of a serialized field always wins over the code default. The dressing
        /// half is deleted and rebuilt outright, because it is pure output: nothing in it is
        /// authored by hand, and repairing scattered scenery in place means never being able to
        /// tell what the current code produces from what an older version left behind.
        /// </summary>
        private static void Build(Scene scene)
        {
            var root = FindRoot(scene);
            if (root == null)
            {
                root = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(root, scene);
                SetLayer(root, "BattleStage");
            }

            root.SetActive(true);
            root.transform.position = ArenaOrigin;
            root.transform.rotation = Quaternion.identity;

            var stage = EnsureStage(root.transform);
            var rig = EnsureCameraRig(root.transform, stage);
            var presenter = EnsurePresenter(root.transform, stage, rig);
            var dressings = BuildDressings(root.transform);

            var arena = root.GetComponent<BattleArena>();
            if (arena == null) arena = root.AddComponent<BattleArena>();
            Assign(arena, "stage", stage);
            Assign(arena, "rig", rig);
            Assign(arena, "presenter", presenter);
            AssignArray(arena, "dressings", dressings);

            Debug.Log($"[Arena] Built the battle arena in '{scene.name}' at {ArenaOrigin}, with " +
                      $"{dressings.Length} zone dressings.");
        }

        /// <summary>
        /// Removes the arena a previous version of this file left standing in a level scene, and
        /// clears the wiring that switched it on.
        ///
        /// Not cosmetic. That arena still carries a <see cref="BattlePresenter"/>, which claims
        /// the registered battle stage the moment it is enabled — so a level scene holding a
        /// stale one would have two presenters racing to drive the same simulation, one of them
        /// standing in the middle of the map. The director's <c>battleRoot</c> and
        /// <c>overworldRoot</c> are cleared for the same reason: they are the root-swap the scene
        /// load replaces, and a serialized reference to a deleted object is not the same thing as
        /// no reference at all.
        /// </summary>
        private static int StripInWorldArena(Scene scene)
        {
            var removed = 0;

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.GetComponentInChildren<CinematicStage>(true) == null
                    && root.GetComponentInChildren<BattlePresenter>(true) == null) continue;

                Debug.Log($"[Arena] Removing '{root.name}' from '{scene.name}': the arena lives in " +
                          "Battle.unity now, and a second presenter here would fight the real one " +
                          "for the battle.");
                Object.DestroyImmediate(root);
                removed++;
            }

            foreach (var director in Object.FindObjectsByType<TransitionDirector>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (director.gameObject.scene != scene) continue;
                removed += ClearIfPresent(director, "battleRoot");
                removed += ClearIfPresent(director, "overworldRoot");
                removed += ClearIfPresent(director, "battlePresenter");
            }

            return removed;
        }

        // --- Stage ---------------------------------------------------------------------------

        private static CinematicStage EnsureStage(Transform parent)
        {
            var existing = parent.GetComponentInChildren<CinematicStage>(true);
            if (existing != null)
            {
                // The stage used to be carried to wherever the encounter fired, so a scene saved
                // mid-session can hold one parked a hundred metres off its own root.
                existing.transform.localPosition = Vector3.zero;
                existing.transform.localRotation = Quaternion.identity;
                EnsureMarksViewsAndDiscs(existing);
                return existing;
            }

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
                // the arena floor is, and a rebuild can retarget which material that is.
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

        private static BattleCameraRig EnsureCameraRig(Transform parent, CinematicStage stage)
        {
            var rig = parent.GetComponentInChildren<BattleCameraRig>(true);
            if (rig == null)
            {
                var go = new GameObject("BattleCameraRig");
                go.transform.SetParent(parent, false);
                SetLayer(go, "BattleStage");
                rig = go.AddComponent<BattleCameraRig>();
            }

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
        /// <item><b>Pitch 18°.</b> Inside the HD-2D band. It used to be the lowest angle that
        /// still cleared the foliage the arena was standing in; nothing grows in front of the
        /// camera any more, and it stays where it is because it is also the angle at which the
        /// treeline and the sky are both in frame above the far combatant.</item>
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

            // Left null on purpose, which it could not be before: the brain is on the main camera
            // and the main camera lives in the level scene, so there is no cross-scene reference
            // to make. The rig resolves it from Camera.main at Awake, by which time the battle
            // scene has been loaded beside a level that has one.
            SetObject(so, "brain", null);
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
            // shove the camera whenever a billboard's bounds cross it.
            SetLayerMask(so, "obstacleLayers", "Ground", "Environment", "Prop");
            SetLayerMask(so, "subjectLayers", "Creature", "Vfx");
            SetLayerMask(so, "terrainLayers", "Ground");

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // --- Presenter -------------------------------------------------------------------------

        private static BattlePresenter EnsurePresenter(Transform parent, CinematicStage stage, BattleCameraRig rig)
        {
            var presenter = parent.GetComponentInChildren<BattlePresenter>(true);
            if (presenter == null)
            {
                var go = new GameObject("BattlePresenter");
                go.transform.SetParent(parent, false);
                SetLayer(go, "BattleStage");
                presenter = go.AddComponent<BattlePresenter>();
            }

            // Both are found by search when left empty, and the search would now cross into the
            // level scene loaded beside this one and could find anything.
            Assign(presenter, "stage", stage);
            Assign(presenter, "rig", rig);
            return presenter;
        }

        // --- Dressings ---------------------------------------------------------------------------

        /// <summary>
        /// The four backdrops, one per <see cref="BattleZone"/>, in enum order.
        ///
        /// Each is a floor the arena stands on plus scenery arranged in a ring around it, and the
        /// ring is the whole trick: the camera sits about five and a half metres from the stage
        /// centre, so nothing is placed inside eleven and there is no way for the shot to end up
        /// inside a tree. The reference frames read as a place with depth — sky above a treeline,
        /// water behind — so the scenery is biased toward the far half of the field, which is
        /// where the frame has room for it above the far combatant.
        ///
        /// Rebuilt from scratch every time. These are output, not authoring.
        /// </summary>
        private static GameObject[] BuildDressings(Transform parent)
        {
            var built = new GameObject[4];
            for (int i = 0; i < built.Length; i++)
            {
                var zone = (BattleZone)i;
                var name = "Dressing_" + zone;

                var existing = parent.Find(name);
                if (existing != null) Object.DestroyImmediate(existing.gameObject);

                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                go.transform.localPosition = Vector3.zero;

                switch (zone)
                {
                    case BattleZone.Route: BuildRoute(go.transform); break;
                    case BattleZone.Town: BuildTown(go.transform); break;
                    case BattleZone.Cave: BuildCave(go.transform); break;
                    default: BuildLakeside(go.transform); break;
                }

                // Only the zone the encounter asks for is switched on, and BattleArena.Dress does
                // that at runtime. Route is left visible so opening the scene shows something.
                go.SetActive(zone == BattleZone.Route);
                built[i] = go;
            }
            return built;
        }

        private static void BuildRoute(Transform root)
        {
            BuildFloor(root, "Floor_Route", 70f, -70f, 70f,
                BattleFieldSurface.Grass, BattleFieldSurface.Dirt, 34f);

            // Two rings at different radii and scales. One ring reads as a fence of trees; two
            // gives the treeline a near edge and a far one, which is what makes it depth rather
            // than a wall of cutouts.
            Scatter(root, "Treeline_Near", BroadleafTrees, 22, 13f, 20f, 0.85f, 1.25f, 91);
            Scatter(root, "Treeline_Far", ConiferTrees, 26, 21f, 34f, 1.0f, 1.6f, 92);
            Scatter(root, "Rocks", Boulders, 9, 12f, 19f, 0.6f, 1.1f, 93);
            Scatter(root, "Undergrowth", Bushes, 30, 8.5f, 17f, 0.7f, 1.3f, 94);
            Scatter(root, "Flowers", Flowers, 44, 6.5f, 15f, 0.8f, 1.2f, 95);
        }

        private static void BuildTown(Transform root)
        {
            BuildFloor(root, "Floor_Town", 70f, -70f, 70f,
                BattleFieldSurface.Dirt, BattleFieldSurface.Grass, 30f);

            // Buildings face the field. A ring of houses all pointing at world +Z would show the
            // player three gable ends and one front door.
            Scatter(root, "Buildings", Buildings, 9, 17f, 26f, 1f, 1f, 71, faceCentre: true);
            Scatter(root, "Fences", Fences, 16, 11f, 12.5f, 1f, 1f, 72, faceCentre: true);
            Scatter(root, "StreetProps", TownProps, 14, 12f, 18f, 0.9f, 1.15f, 73);
            Scatter(root, "Trees", BroadleafTrees, 12, 22f, 33f, 0.9f, 1.4f, 74);
            Scatter(root, "Planting", Flowers, 26, 9f, 16f, 0.8f, 1.2f, 75);
        }

        private static void BuildCave(Transform root)
        {
            BuildFloor(root, "Floor_Cave", 40f, -40f, 40f,
                BattleFieldSurface.Rock, BattleFieldSurface.Dirt, 18f);

            // A ceiling, and it is what makes the room a cave rather than a quarry at night. It
            // hangs above the camera's own height, so it frames the top of the shot without ever
            // being something the camera can be pushed into.
            BuildCeiling(root, "Ceiling_Cave", 40f, 7.5f);

            Scatter(root, "Walls", CliffWalls, 26, 13f, 15f, 1.1f, 1.6f, 51, faceCentre: true);
            Scatter(root, "Stalagmites", Stalagmites, 20, 9f, 16f, 0.8f, 1.7f, 52);
            Scatter(root, "Stalactites", Stalactites, 16, 8f, 16f, 0.9f, 1.6f, 53, height: 7.2f, upsideDown: true);
            Scatter(root, "Rubble", CaveRubble, 18, 8f, 17f, 0.7f, 1.3f, 54);
            Scatter(root, "Moss", CaveMoss, 22, 7.5f, 15f, 0.8f, 1.3f, 55);
        }

        private static void BuildLakeside(Transform root)
        {
            // The shore stops short of the water rather than running under it. A floor that
            // continued would z-fight the lake surface along the whole waterline, which is the
            // most visible artefact this dressing could have.
            BuildFloor(root, "Floor_Shore", 70f, -70f, 13f,
                BattleFieldSurface.Grass, BattleFieldSurface.Sand, 4f);
            BuildWater(root, "Water_Lake", 150f, 11.5f, 150f, -0.35f);

            Scatter(root, "Reeds", Reeds, 34, 8f, 12.5f, 0.8f, 1.4f, 31, arcFrom: -70f, arcTo: 70f);
            Scatter(root, "Lilypads", Lilypads, 22, 16f, 40f, 1.2f, 2.2f, 32, arcFrom: -55f, arcTo: 55f, height: -0.3f);
            Scatter(root, "Willows", WillowTrees, 14, 13f, 22f, 0.9f, 1.4f, 33, arcFrom: 60f, arcTo: 300f);
            Scatter(root, "FarShore", BroadleafTrees, 20, 55f, 78f, 1.2f, 1.8f, 34, arcFrom: -60f, arcTo: 60f);
            Scatter(root, "WetRocks", Boulders, 10, 10f, 18f, 0.7f, 1.2f, 35, arcFrom: 70f, arcTo: 290f);
            Scatter(root, "Bank", Bushes, 20, 9f, 16f, 0.7f, 1.2f, 36, arcFrom: 80f, arcTo: 280f);
        }

        // --- Dressing pieces ------------------------------------------------------------------------

        private static readonly string[] BroadleafTrees =
        {
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Broadleaf_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Broadleaf_B.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Broadleaf_C.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Birch_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Birch_B.fbx",
        };

        private static readonly string[] ConiferTrees =
        {
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Conifer_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Conifer_B.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Conifer_C.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Birch_C.fbx",
        };

        private static readonly string[] WillowTrees =
        {
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Willow_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Willow_B.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Tree_Willow_C.fbx",
        };

        private static readonly string[] Bushes =
        {
            "Assets/Game/Art/Environment/Foliage/Env_Bush_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Bush_B.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Bush_C.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Fern_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Fern_B.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_TallGrass_Cluster_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_TallGrass_Cluster_C.fbx",
        };

        private static readonly string[] Flowers =
        {
            "Assets/Game/Art/Environment/Foliage/Env_Flower_White.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Flower_Yellow.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Flower_Purple.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Flower_Red.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Grass_Clump_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Grass_Clump_D.fbx",
        };

        private static readonly string[] Boulders =
        {
            "Assets/Game/Art/Environment/Terrain/Env_Rock_Boulder_A.fbx",
            "Assets/Game/Art/Environment/Terrain/Env_Rock_Boulder_B.fbx",
            "Assets/Game/Art/Environment/Terrain/Env_Rock_Boulder_C.fbx",
            "Assets/Game/Art/Environment/Terrain/Env_Rock_Scatter_A.fbx",
        };

        private static readonly string[] Buildings =
        {
            "Assets/Game/Art/Environment/Town/Env_House_Cottage_A.fbx",
            "Assets/Game/Art/Environment/Town/Env_House_Townhouse_B.fbx",
            "Assets/Game/Art/Environment/Town/Env_House_Farmhouse_C.fbx",
            "Assets/Game/Art/Environment/Town/Env_Building_PokeCentre.fbx",
            "Assets/Game/Art/Environment/Town/Env_Building_PokeLab.fbx",
        };

        private static readonly string[] Fences =
        {
            "Assets/Game/Art/Environment/Town/Env_Fence_Picket_2m.fbx",
            "Assets/Game/Art/Environment/Town/Env_Fence_Picket_1m.fbx",
            "Assets/Game/Art/Environment/Town/Env_Wall_Stone_2m.fbx",
        };

        private static readonly string[] TownProps =
        {
            "Assets/Game/Art/Environment/Town/Env_Lamp_Post.fbx",
            "Assets/Game/Art/Environment/Town/Env_Well.fbx",
            "Assets/Game/Art/Environment/Town/Env_Cart.fbx",
            "Assets/Game/Art/Environment/Town/Env_Crate.fbx",
            "Assets/Game/Art/Environment/Town/Env_Barrel.fbx",
            "Assets/Game/Art/Environment/Town/Env_Market_Stall.fbx",
            "Assets/Game/Art/Environment/Town/Env_Planter.fbx",
        };

        private static readonly string[] CliffWalls =
        {
            "Assets/Game/Art/Environment/Terrain/Env_Cliff_Wall_4m.fbx",
            "Assets/Game/Art/Environment/Terrain/Env_Cliff_Wall_6m.fbx",
            "Assets/Game/Art/Environment/Terrain/Env_Cliff_Wall_Tall_4m.fbx",
        };

        private static readonly string[] Stalagmites =
        {
            "Assets/Game/Art/Environment/Terrain/Env_Cave_Stalagmite_A.fbx",
            "Assets/Game/Art/Environment/Terrain/Env_Cave_Stalagmite_B.fbx",
        };

        private static readonly string[] Stalactites =
        {
            "Assets/Game/Art/Environment/Terrain/Env_Cave_Stalactite_A.fbx",
            "Assets/Game/Art/Environment/Terrain/Env_Cave_Stalactite_B.fbx",
        };

        private static readonly string[] CaveRubble =
        {
            "Assets/Game/Art/Environment/Terrain/Env_Cave_Rubble.fbx",
            "Assets/Game/Art/Environment/Terrain/Env_Rock_Scatter_B.fbx",
            "Assets/Game/Art/Environment/Terrain/Env_Rock_Boulder_D.fbx",
        };

        private static readonly string[] CaveMoss =
        {
            "Assets/Game/Art/Environment/Foliage/Env_Moss_Cave_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Moss_Cave_B.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Vine_Hanging_A.fbx",
        };

        private static readonly string[] Reeds =
        {
            "Assets/Game/Art/Environment/Foliage/Env_Reed_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Reed_B.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Grass_Clump_B.fbx",
        };

        private static readonly string[] Lilypads =
        {
            "Assets/Game/Art/Environment/Foliage/Env_Lilypad_A.fbx",
            "Assets/Game/Art/Environment/Foliage/Env_Lilypad_B.fbx",
        };

        /// <summary>
        /// Places one library of models in a ring around the field.
        ///
        /// Seeded rather than random: a dressing that comes out differently every rebuild cannot
        /// be reviewed, because nobody can tell a change in this file from a change in the dice.
        /// </summary>
        private static void Scatter(Transform parent, string name, string[] models, int count,
            float radiusMin, float radiusMax, float scaleMin, float scaleMax, int seed,
            bool faceCentre = false, float arcFrom = 0f, float arcTo = 360f,
            float height = 0f, bool upsideDown = false)
        {
            if (models == null || models.Length == 0 || count <= 0) return;

            var group = new GameObject(name);
            group.transform.SetParent(parent, false);

            var random = new System.Random(seed);
            var missing = new HashSet<string>();

            for (int i = 0; i < count; i++)
            {
                // Stratified rather than uniformly random: pure noise leaves gaps a ring of
                // twenty trees cannot afford, and the gap is always the one behind the far
                // combatant because that is where the eye goes.
                float t = (i + (float)random.NextDouble() * 0.7f) / count;
                float degrees = Mathf.Lerp(arcFrom, arcTo, t);
                float radius = Mathf.Lerp(radiusMin, radiusMax, (float)random.NextDouble());
                float scale = Mathf.Lerp(scaleMin, scaleMax, (float)random.NextDouble());

                Vector3 direction = Quaternion.Euler(0f, degrees, 0f) * Vector3.forward;
                Vector3 position = direction * radius + Vector3.up * height;

                string path = models[random.Next(models.Length)];
                var instance = Instantiate(path, group.transform, missing);
                if (instance == null) continue;

                instance.transform.localPosition = position;
                instance.transform.localRotation = faceCentre
                    ? Quaternion.LookRotation(-direction, Vector3.up)
                    : Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f);
                if (upsideDown)
                    instance.transform.localRotation *= Quaternion.Euler(180f, 0f, 0f);
                instance.transform.localScale = Vector3.one * scale;
            }

            foreach (var path in missing)
                Debug.LogWarning($"[Arena] {path} is missing, so part of the {name} dressing is empty.");
        }

        /// <summary>
        /// Instantiates a module and gives it the atlas material its folder implies.
        ///
        /// The material is assigned rather than inherited because every environment FBX in this
        /// project is imported with materials off — the pipeline paints them from one atlas per
        /// folder — so an instantiated module arrives with Unity's grey default on every
        /// renderer, which is a backdrop with no colour in it at all.
        ///
        /// No colliders: nothing in the backdrop is walked into, and a mesh collider per canopy
        /// on eighty trees is eighty colliders the battle never asks a question of.
        /// </summary>
        private static GameObject Instantiate(string path, Transform parent, HashSet<string> missing)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { missing.Add(path); return null; }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            if (instance == null) { missing.Add(path); return null; }

            var material = AtlasFor(path);
            if (material != null)
            {
                foreach (var renderer in instance.GetComponentsInChildren<MeshRenderer>(true))
                    renderer.sharedMaterial = material;
            }

            instance.isStatic = true;
            SetLayer(instance, "Environment");
            return instance;
        }

        private static Material AtlasFor(string path)
        {
            string atlas;
            if (path.Contains("/Foliage/")) atlas = "Assets/Game/Art/Environment/Foliage/Materials/M_Env_Foliage.mat";
            else if (path.Contains("/Town/")) atlas = "Assets/Game/Art/Environment/Town/Materials/M_Env_Town.mat";
            else if (path.Contains("/Props/")) atlas = "Assets/Game/Art/Props/Materials/M_Env_Props.mat";
            else atlas = "Assets/Game/Art/Environment/Terrain/Materials/M_Env_Terrain.mat";

            var material = AssetDatabase.LoadAssetAtPath<Material>(atlas);
            if (material == null)
                Debug.LogWarning($"[Arena] {atlas} not found, so {Path.GetFileName(path)} will draw grey.");
            return material;
        }

        // --- Ground ------------------------------------------------------------------------------

        /// <summary>
        /// The floor the arena stands on: one flat grid painted with the terrain shader's blend
        /// weights, exactly the way the level's own ground is.
        ///
        /// Flat, and it has to be. Every mark, disc and burst point in <see cref="CinematicStage"/>
        /// is a y = 0 position, so a rolling floor would leave one combatant buried and the other
        /// hovering. The variation comes from the blend instead: the core surface underfoot easing
        /// out to a second one at the edges, which is what stops fifty metres of one texture
        /// reading as a bedsheet.
        ///
        /// It carries a collider because the player is warped onto it and would otherwise fall
        /// through the world for the length of the battle.
        /// </summary>
        private static void BuildFloor(Transform parent, string name, float halfWidth,
            float zNear, float zFar, BattleFieldSurface core, BattleFieldSurface edge, float blendFrom)
        {
            const int Steps = 24;

            var vertices = new Vector3[(Steps + 1) * (Steps + 1)];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            var colours = new Color[vertices.Length];
            var triangles = new int[Steps * Steps * 6];

            Color coreWeights = WeightsFor(core);
            Color edgeWeights = WeightsFor(edge);

            for (int z = 0; z <= Steps; z++)
            {
                for (int x = 0; x <= Steps; x++)
                {
                    int i = z * (Steps + 1) + x;
                    float px = Mathf.Lerp(-halfWidth, halfWidth, x / (float)Steps);
                    float pz = Mathf.Lerp(zNear, zFar, z / (float)Steps);

                    vertices[i] = new Vector3(px, 0f, pz);
                    normals[i] = Vector3.up;
                    uvs[i] = new Vector2(px, pz);

                    float distance = Mathf.Max(Mathf.Abs(px), Mathf.Abs(pz));
                    float blend = Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(blendFrom, Mathf.Max(blendFrom + 1f, halfWidth), distance));
                    colours[i] = Color.Lerp(coreWeights, edgeWeights, blend);
                }
            }

            int t = 0;
            for (int z = 0; z < Steps; z++)
            {
                for (int x = 0; x < Steps; x++)
                {
                    int a = z * (Steps + 1) + x;
                    int b = a + 1;
                    int c = a + Steps + 1;
                    int d = c + 1;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }
            }

            var go = MeshObject(parent, name, vertices, normals, uvs, colours, triangles,
                AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterial), "Ground");
            go.AddComponent<MeshCollider>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
        }

        /// <summary>
        /// The cave roof. Same grid as the floor, wound the other way so its faces point down at
        /// the arena, and with no collider — the only thing that could ever touch it is the
        /// camera, and the camera is four metres below it.
        /// </summary>
        private static void BuildCeiling(Transform parent, string name, float halfWidth, float height)
        {
            const int Steps = 12;

            var vertices = new Vector3[(Steps + 1) * (Steps + 1)];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            var colours = new Color[vertices.Length];
            var triangles = new int[Steps * Steps * 6];

            Color rock = WeightsFor(BattleFieldSurface.Rock);

            for (int z = 0; z <= Steps; z++)
            {
                for (int x = 0; x <= Steps; x++)
                {
                    int i = z * (Steps + 1) + x;
                    float px = Mathf.Lerp(-halfWidth, halfWidth, x / (float)Steps);
                    float pz = Mathf.Lerp(-halfWidth, halfWidth, z / (float)Steps);

                    // Sagging toward the middle, so the roof reads as rock rather than as a lid.
                    float sag = 1f - Mathf.Clamp01(new Vector2(px, pz).magnitude / halfWidth);
                    vertices[i] = new Vector3(px, height - sag * 1.6f, pz);
                    normals[i] = Vector3.down;
                    uvs[i] = new Vector2(px, pz);
                    colours[i] = rock;
                }
            }

            int t = 0;
            for (int z = 0; z < Steps; z++)
            {
                for (int x = 0; x < Steps; x++)
                {
                    int a = z * (Steps + 1) + x;
                    int b = a + 1;
                    int c = a + Steps + 1;
                    int d = c + 1;
                    triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
                    triangles[t++] = b; triangles[t++] = d; triangles[t++] = c;
                }
            }

            MeshObject(parent, name, vertices, normals, uvs, colours, triangles,
                AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterial), "Environment");
        }

        /// <summary>The lake behind the far combatant, drawn with the level's own water material.</summary>
        private static void BuildWater(Transform parent, string name,
            float halfWidth, float zNear, float zFar, float y)
        {
            var vertices = new[]
            {
                new Vector3(-halfWidth, y, zNear), new Vector3(halfWidth, y, zNear),
                new Vector3(-halfWidth, y, zFar), new Vector3(halfWidth, y, zFar),
            };
            var normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            var uvs = new[]
            {
                new Vector2(-halfWidth, zNear), new Vector2(halfWidth, zNear),
                new Vector2(-halfWidth, zFar), new Vector2(halfWidth, zFar),
            };
            var colours = new[] { Color.white, Color.white, Color.white, Color.white };
            var triangles = new[] { 0, 2, 1, 1, 2, 3 };

            var material = AssetDatabase.LoadAssetAtPath<Material>(WaterMaterial);
            if (material == null)
                Debug.LogWarning($"[Arena] {WaterMaterial} not found, so the lake will draw grey.");

            MeshObject(parent, name, vertices, normals, uvs, colours, triangles, material, "Water");
        }

        private static GameObject MeshObject(Transform parent, string name, Vector3[] vertices,
            Vector3[] normals, Vector2[] uvs, Color[] colours, int[] triangles,
            Material material, string layer)
        {
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.colors = colours;
            mesh.triangles = triangles;
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;
            go.isStatic = true;
            SetLayer(go, layer);
            return go;
        }

        /// <summary>
        /// The terrain shader's four blend weights packed as a vertex colour. RGBA is grass,
        /// dirt, sand, rock — the order the level's own ground meshes are painted in, and the
        /// order <see cref="BattleFieldDisc"/> writes, so a disc laid on this floor matches it.
        /// </summary>
        private static Color WeightsFor(BattleFieldSurface value)
        {
            switch (value)
            {
                case BattleFieldSurface.Dirt: return new Color(0f, 1f, 0f, 0f);
                case BattleFieldSurface.Sand: return new Color(0f, 0f, 1f, 0f);
                case BattleFieldSurface.Rock: return new Color(0f, 0f, 0f, 1f);
                default: return new Color(1f, 0f, 0f, 0f);
            }
        }

        // --- Scene helpers -------------------------------------------------------------------------

        private static bool IsBattleScene(Scene scene) =>
            string.Equals(scene.path, BattleScenePath, System.StringComparison.OrdinalIgnoreCase);

        private static GameObject FindRoot(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == RootName || root.GetComponent<BattleArena>() != null) return root;
                if (root.GetComponentInChildren<CinematicStage>(true) != null) return root;
            }
            return null;
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

        private static void AssignArray(Object target, string field, Object[] values)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);
            if (property == null)
            {
                Debug.LogWarning($"[Arena] {target.GetType().Name} has no field '{field}'; " +
                                 "it was left unassigned.");
                return;
            }
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Clears a serialized reference, reporting whether there had been one.</summary>
        private static int ClearIfPresent(Object target, string field)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);
            if (property == null || property.objectReferenceValue == null) return 0;

            property.objectReferenceValue = null;
            so.ApplyModifiedPropertiesWithoutUndo();
            return 1;
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
