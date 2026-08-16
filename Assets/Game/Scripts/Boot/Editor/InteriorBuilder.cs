using System.Collections.Generic;
using System.IO;
using PokeLab.Overworld;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Builds the rooms behind the town's front doors.
    ///
    /// <b>A separate scene, not a room off to one side of the world.</b> The battle arena is
    /// staged at (2000, 0, 2000) inside the scene the player is standing in, and that is right
    /// for a battle — it is loaded beside the overworld because the overworld has to still be
    /// there to come back to. An interior is the opposite case on every count. The town and the
    /// field are streamed together into one world space by <see cref="WorldStreamer"/>, so a
    /// room hidden two kilometres out is a room lit by the town's sun, baked into the town's
    /// navmesh, and carried in memory the whole time the player is outdoors. And seven of the
    /// nine doors lead to the same Interior_House: as a scene that is one room built once, and
    /// as a corner of the world it is seven copies or one room the player is teleported into,
    /// which is the seam the brief rules out. The scene load is also what lets the interior have
    /// its own lighting mood without fighting the LightingDirector for the town's.
    ///
    /// <b>The dark the player steps into is the room, not the doorway.</b> A cave mouth gets a
    /// black quad across it because it is a real hole in a hillside with nothing behind it —
    /// see LevelLayoutBuilder.BuildCaveBlackout. A front door is not that: gen_town models a
    /// door leaf, its reveal and its architrave, and blacking that out would paint a black
    /// rectangle over a painted door. So the darkness lives where the player walks into it. The
    /// room carries a <see cref="WorldZone"/> whose biome id contains "interior", which is what
    /// LightingDirector.OnBiomeEntered keys the CaveInterior grade off — the same grade a cave
    /// gets — and the only light in it is the lamps on its walls. The doorway you can see from
    /// inside is a black plane, for the cave's reason: there is genuinely nothing behind it.
    ///
    /// Everything here is generated. Anything hand-placed in the .unity file is destroyed by
    /// the next Rebuild Everything, which is the same rule the town is built under.
    ///
    /// The shell is built as meshes UV-mapped into the town atlas rather than assembled out of
    /// wall modules. Env_Wall_Stone_2m is the only wall in the kit and it is a 1.11 m dry-stone
    /// garden wall with a coping course on top: three of them stacked to reach a room's height
    /// is three coping courses up a wall, which reads as three garden walls, not as a room. The
    /// atlas cells are the same ones the buildings' own exteriors are painted from, so the
    /// plaster and the paving inside match the plaster and the paving outside.
    /// </summary>
    public static class InteriorBuilder
    {
        private const string SceneDir = "Assets/Game/Scenes/";

        /// <summary>
        /// Copied rather than created empty, for the reason <see cref="SceneSetup"/> copies it:
        /// Town carries the camera rig, the lighting director, the player and the service hosts,
        /// and a blank scene would need every one of them rebuilt by hand.
        /// </summary>
        private const string Template = SceneDir + "Town.unity";

        private const string RootName = "Interior";

        private const string TownAtlas =
            "Assets/Game/Art/Environment/Town/Materials/M_Env_Town.mat";

        // --- the atlas -------------------------------------------------------------------
        //
        // Cell indices into the 4x4 town atlas, in the order TOWN_CELLS declares them in
        // Tools/Blender/environment/textures.py. They are part of the baked texture, so they
        // only change when the atlas is rebuilt — and if it is, this list moves with it.

        private const int CellPlaster = 0;    // plaster_cream
        private const int CellBeam = 3;       // wood_beam
        private const int CellPaving = 10;    // paving
        private const int CellStone = 11;     // stone_wall
        private const int CellTrim = 15;      // trim_white

        /// <summary>
        /// One room, and which door in the town it is the far side of.
        ///
        /// <c>FallbackDoorstep</c> is only used when the interior was opened directly instead of
        /// walked into — which is how it is worked on in the editor. Walked into, the way out
        /// aims at the door the player actually used, which <see cref="LevelTransition"/>
        /// remembers across the load.
        /// </summary>
        private readonly struct Room
        {
            public readonly string Scene;
            public readonly string BiomeId;
            public readonly float HalfWidth;
            public readonly float HalfDepth;
            public readonly float WallHeight;
            public readonly string FallbackDoorstep;

            public Room(string scene, string biomeId, float halfWidth, float halfDepth,
                        float wallHeight, string fallbackDoorstep)
            {
                Scene = scene;
                BiomeId = biomeId;
                HalfWidth = halfWidth;
                HalfDepth = halfDepth;
                WallHeight = wallHeight;
                FallbackDoorstep = fallbackDoorstep;
            }
        }

        /// <summary>
        /// The interiors, matching the scene names emit_unity_layout.py writes into
        /// <c>buildingDoors</c>. A door whose scene is not in this list — and therefore not on
        /// disk — is skipped by the level builder rather than built as a door into nothing.
        /// </summary>
        private static readonly Room[] Rooms =
        {
            new Room("Interior_Lab", "interior_lab", 5.5f, 4.5f, 3.6f,
                     "Spawn_Outside_Door_Town_Lab_01"),
            new Room("Interior_House", "interior_house", 4f, 3.25f, 2.9f,
                     "Spawn_Outside_Door_Town_House_01"),
            new Room("Interior_PokeCentre", "interior_pokecentre", 6f, 4.5f, 3.6f,
                     "Spawn_Outside_Door_Town_PokeCentre_01"),
        };

        /// <summary>Door opening in the room's south wall. Wider than the outside door on
        /// purpose: this one is walked through in both directions and at an angle.</summary>
        private const float DoorWidth = 1.8f;
        private const float DoorHeight = 2.4f;

        /// <summary>Scene names every interior contributes to the build settings.</summary>
        public static IEnumerable<string> SceneNames()
        {
            foreach (var room in Rooms) yield return room.Scene;
        }

        [MenuItem("Tools/Poké Lab/Rebuild/Create Interior Scenes", priority = 14)]
        public static void CreateScenes()
        {
            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();

            var made = EnsureScenesExist();
            SceneSetup.AddToBuildSettings();
            var built = BuildAll();

            Debug.Log($"[Interior] Created {made} interior scene(s) and built {built} room(s).");
        }

        /// <summary>
        /// Copies the scene assets that do not exist yet, and builds nothing.
        ///
        /// Split from the build because of an ordering trap. The level builder skips a door
        /// whose interior scene is not on disk — a door into a missing scene covers the screen
        /// and then fails — so on a project where the interiors have never been created, a whole
        /// rebuild would emit a town with no doors in it and only grow them on the *second*
        /// run. The files have to exist before the town is built, and the rooms inside them can
        /// be filled afterwards.
        /// </summary>
        public static int EnsureScenesExist()
        {
            if (!File.Exists(Template))
            {
                Debug.LogError($"[Interior] {Template} is missing, so there is nothing to copy from.");
                return 0;
            }

            var made = 0;
            foreach (var room in Rooms)
            {
                var path = SceneDir + room.Scene + ".unity";
                if (File.Exists(path)) continue;
                if (!AssetDatabase.CopyAsset(Template, path))
                {
                    Debug.LogError($"[Interior] Could not copy {Template} to {path}.");
                    continue;
                }
                made++;
            }

            if (made > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            return made;
        }

        /// <summary>Rebuilds every interior that exists. Called by Rebuild Everything.</summary>
        public static int BuildAll()
        {
            var built = 0;
            foreach (var room in Rooms)
            {
                if (!File.Exists(SceneDir + room.Scene + ".unity")) continue;
                BuildInOwnScene(room);
                built++;
            }
            return built;
        }

        private static void BuildInOwnScene(Room room)
        {
            var path = SceneDir + room.Scene + ".unity";
            if (!File.Exists(path)) return;

            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Build(room);

            // After the room, not before: the rig setup moves the player onto whatever
            // "PlayerSpawn" it can find, and the one that matters is the marker this build has
            // just stood inside the room. Run the other way round it drops them at the town's
            // plaza coordinates, which in here is somewhere outside the walls.
            PlayerRigSetup.CreateRig();
            EditorSceneManager.SaveOpenScenes();
        }

        /// <summary>Builds the room matching the open scene, if it is an interior.</summary>
        [MenuItem("Tools/Poké Lab/Level/Build Interior In Open Scene")]
        public static void BuildOpen()
        {
            var open = EditorSceneManager.GetActiveScene().name;
            foreach (var room in Rooms)
            {
                if (room.Scene != open) continue;
                Build(room);
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                return;
            }
            Debug.LogWarning($"[Interior] '{open}' is not one of the interior scenes.");
        }

        private static void Build(Room room)
        {
            // The scene was copied from the town, so it arrives carrying the whole town. The
            // level builder empties its own root the same way for the same reason: a rebuild
            // that added to what was there could never be told apart from a stale one.
            var town = GameObject.Find("Level");
            if (town != null) Object.DestroyImmediate(town);

            var existing = GameObject.Find(RootName);
            if (existing != null) Object.DestroyImmediate(existing);

            var root = new GameObject(RootName);
            var material = AssetDatabase.LoadAssetAtPath<Material>(TownAtlas);
            if (material == null)
            {
                Debug.LogWarning($"[Interior] {TownAtlas} not found, so {room.Scene} will draw " +
                                 "grey. The room is still built — it is the texture that is missing.");
            }

            BuildShell(room, root.transform, material);
            BuildThreshold(room, root.transform);
            BuildDressing(room, root.transform);
            BuildLamps(room, root.transform);
            BuildDoorAndSpawns(room, root.transform);
            BuildZone(room, root.transform);
            BuildNavigation(root);

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[Interior] Built '{room.Scene}': a {room.HalfWidth * 2f:0.#} x " +
                      $"{room.HalfDepth * 2f:0.#} m room, exit falling back to " +
                      $"{room.FallbackDoorstep}.");
        }

        // --- shell -------------------------------------------------------------------------

        /// <summary>
        /// Floor, walls and ceiling.
        ///
        /// Two objects, not one: the floor is on the Ground layer and the rest on Environment,
        /// which is the split the navmesh bake and the camera's occlusion both already work in.
        /// One combined mesh would put the ceiling in whatever layer the floor chose.
        ///
        /// The walls are banded — a stone dado, plaster above it, a timber cornice under the
        /// ceiling — because a single flat plaster field three and a half metres tall has no
        /// scale in it at all, and scale is the only thing that tells the player the room is a
        /// room rather than a backdrop.
        /// </summary>
        private static void BuildShell(Room room, Transform parent, Material material)
        {
            var hx = room.HalfWidth;
            var hz = room.HalfDepth;
            var top = room.WallHeight;
            const float Dado = 0.95f;
            const float Cornice = 0.24f;

            var floor = new MeshData();
            floor.Panel(new Vector3(-hx, 0f, -hz), Vector3.right, Vector3.forward,
                        hx * 2f, hz * 2f, CellPaving, 1.6f);

            var shell = new MeshData();

            // Walls, each wound so its face points into the room. The south wall carries the
            // doorway, so it is built as two piers and a lintel rather than as one panel.
            Wall(shell, new Vector3(-hx, 0f, hz), Vector3.right, hx * 2f, top, Dado, Cornice);      // north
            Wall(shell, new Vector3(-hx, 0f, -hz), Vector3.forward, hz * 2f, top, Dado, Cornice);   // west
            Wall(shell, new Vector3(hx, 0f, hz), Vector3.back, hz * 2f, top, Dado, Cornice);        // east

            var pier = hx - DoorWidth * 0.5f;
            Wall(shell, new Vector3(hx, 0f, -hz), Vector3.left, pier, top, Dado, Cornice);
            Wall(shell, new Vector3(-DoorWidth * 0.5f, 0f, -hz), Vector3.left, pier, top, Dado, Cornice);
            // The lintel over the door. Plain plaster: a dado band four inches above head
            // height would read as a shelf across the doorway.
            shell.Panel(new Vector3(DoorWidth * 0.5f, DoorHeight, -hz), Vector3.left, Vector3.up,
                        DoorWidth, top - DoorHeight, CellPlaster, 1.7f);

            // The door reveal, so the wall has thickness where the player walks through it.
            // Without it the doorway is a hole in a plane and the wall is visibly paper.
            const float Reveal = 0.3f;
            shell.Panel(new Vector3(-DoorWidth * 0.5f, 0f, -hz - Reveal), Vector3.forward,
                        Vector3.up, Reveal, DoorHeight, CellPlaster, 0.9f);
            shell.Panel(new Vector3(DoorWidth * 0.5f, 0f, -hz), Vector3.back,
                        Vector3.up, Reveal, DoorHeight, CellPlaster, 0.9f);
            shell.Panel(new Vector3(-DoorWidth * 0.5f, DoorHeight, -hz), Vector3.right,
                        Vector3.back, DoorWidth, Reveal, CellBeam, 0.9f);

            // The ceiling, facing down. Boards rather than plaster: the beam cell has a
            // direction in it, which is what stops a flat lid reading as fog.
            //
            // Every surface here is one-sided, and that is load bearing rather than a saving.
            // The exploration camera sits 8.5 m back and 38 degrees up, which in a room this
            // size is a camera standing outside the wall behind the player — so the walls and
            // the lid have to disappear when seen from behind, or the shot is the inside of a
            // closed box. Backface culling gives that for free and gives the room the open
            // dollhouse read the genre uses, without CameraOccluderFade having to dissolve a
            // wall every time the player turns round.
            shell.Panel(new Vector3(hx, top, -hz), Vector3.left, Vector3.forward,
                        hx * 2f, hz * 2f, CellBeam, 2.2f);

            MeshObject(parent, "Floor", floor, material, "Ground", collide: true);
            MeshObject(parent, "Shell", shell, material, "Environment", collide: true);
        }

        /// <summary>
        /// One wall: dado, field, cornice. <paramref name="along"/> is the direction the wall
        /// runs in from <paramref name="corner"/>, and the face points into the room.
        /// </summary>
        private static void Wall(MeshData mesh, Vector3 corner, Vector3 along, float length,
            float height, float dado, float cornice)
        {
            if (length <= 0.01f) return;

            mesh.Panel(corner, along, Vector3.up, length, dado, CellStone, 1.1f);
            mesh.Panel(corner + Vector3.up * dado, along, Vector3.up, length,
                       height - dado - cornice, CellPlaster, 1.7f);
            mesh.Panel(corner + Vector3.up * (height - cornice), along, Vector3.up, length,
                       cornice, CellBeam, 1.2f);
        }

        /// <summary>
        /// The black the player sees through their own doorway.
        ///
        /// Same reasoning as the cave mouth's blackout, and the same material: unlit, so it
        /// cannot pick up the sun or the ambient and turn into a very dark grey that changes
        /// through the day — which is exactly what a player reads as a wall they might get
        /// past. Behind this plane there is nothing, because the town is a different scene.
        /// </summary>
        private static void BuildThreshold(Room room, Transform parent)
        {
            var go = new GameObject("Threshold_Blackout");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, DoorHeight * 0.5f, -room.HalfDepth - 0.32f);

            var mesh = new Mesh { name = "InteriorThreshold" };
            var hw = DoorWidth * 0.5f;
            var hh = DoorHeight * 0.5f;
            mesh.vertices = new[]
            {
                new Vector3(-hw, -hh, 0f), new Vector3(hw, -hh, 0f),
                new Vector3(-hw,  hh, 0f), new Vector3(hw,  hh, 0f),
            };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            // Wound both ways: the player walks through this plane's own position on their way
            // out, and a one-sided quad flashes as they cross it.
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1, 1, 2, 0, 1, 3, 2 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = BlackoutMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private static Material s_blackout;

        private static Material BlackoutMaterial()
        {
            if (s_blackout != null) return s_blackout;

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            s_blackout = new Material(shader) { name = "~InteriorBlackout" };
            if (s_blackout.HasProperty("_BaseColor")) s_blackout.SetColor("_BaseColor", Color.black);
            if (s_blackout.HasProperty("_Color")) s_blackout.SetColor("_Color", Color.black);
            return s_blackout;
        }

        // --- dressing ----------------------------------------------------------------------

        private const string TownArt = "Assets/Game/Art/Environment/Town/";
        private const string PropArt = "Assets/Game/Art/Props/";

        /// <summary>One placed module. Yaw is the kit's own convention: models face +Z.</summary>
        private readonly struct Fixture
        {
            public readonly string Path;
            public readonly Vector3 Position;
            public readonly float Yaw;
            public readonly bool Solid;

            public Fixture(string path, float x, float z, float yaw, bool solid = true)
            {
                Path = path;
                Position = new Vector3(x, 0f, z);
                Yaw = yaw;
                Solid = solid;
            }
        }

        /// <summary>
        /// What stands in each room.
        ///
        /// The lab's terminals and the Centre's healing machine are not new assets: the emitter
        /// holds both back from the town on purpose — INTERIOR_PROPS in emit_unity_layout.py —
        /// because "a healing machine standing in the open street is a Pokemon Centre with no
        /// Centre around it". These rooms are the interiors it was holding them for, so this is
        /// where they finally stand.
        ///
        /// Positions are metres in room space: +Z is the back wall, the door is at -Z. Nothing
        /// is placed within a metre of the door's own line, because a prop there is the first
        /// thing the player walks into on the way in.
        /// </summary>
        private static Fixture[] FixturesFor(Room room)
        {
            var hx = room.HalfWidth;
            var hz = room.HalfDepth;

            if (room.Scene == "Interior_Lab")
            {
                return new[]
                {
                    new Fixture(PropArt + "Env_Prop_ResearchTerminal.fbx", -2.6f, hz - 0.55f, 180f),
                    new Fixture(PropArt + "Env_Prop_ResearchTerminal.fbx", 0f, hz - 0.55f, 180f),
                    new Fixture(PropArt + "Env_Prop_ResearchTerminal.fbx", 2.6f, hz - 0.55f, 180f),
                    new Fixture(TownArt + "Env_Bench.fbx", -hx + 0.55f, 0.6f, 90f),
                    new Fixture(TownArt + "Env_Bench.fbx", -hx + 0.55f, -1.2f, 90f),
                    new Fixture(TownArt + "Env_Crate.fbx", hx - 0.6f, hz - 1.1f, 24f),
                    new Fixture(TownArt + "Env_Crate.fbx", hx - 1.25f, hz - 0.8f, -12f),
                    new Fixture(TownArt + "Env_Barrel.fbx", hx - 0.6f, hz - 2f, 0f),
                    new Fixture(TownArt + "Env_Planter.fbx", -hx + 0.7f, hz - 0.8f, 0f),
                    new Fixture(TownArt + "Env_Notice_Board.fbx", hx - 0.45f, -1.4f, -90f),
                    // On the crate, not on the floor: a ball lying loose in the middle of a
                    // room is a pickup, and this one is scenery.
                    new Fixture(PropArt + "Env_Prop_CaptureBall.fbx", hx - 0.6f, hz - 1.1f, 0f, false),
                };
            }

            if (room.Scene == "Interior_PokeCentre")
            {
                return new[]
                {
                    new Fixture(PropArt + "Env_Prop_HealingMachine.fbx", 0f, hz - 0.6f, 180f),
                    new Fixture(PropArt + "Env_Prop_ResearchTerminal.fbx", hx - 0.7f, hz - 0.6f, 200f),
                    new Fixture(TownArt + "Env_Bench.fbx", -2.2f, -0.4f, 0f),
                    new Fixture(TownArt + "Env_Bench.fbx", 2.2f, -0.4f, 0f),
                    new Fixture(TownArt + "Env_Planter.fbx", -hx + 0.7f, hz - 0.7f, 0f),
                    new Fixture(TownArt + "Env_Planter.fbx", -hx + 0.7f, -hz + 1.2f, 0f),
                    new Fixture(TownArt + "Env_Notice_Board.fbx", -hx + 0.45f, 1.6f, 90f),
                };
            }

            return new[]
            {
                new Fixture(TownArt + "Env_Bench.fbx", -hx + 0.55f, 0.4f, 90f),
                new Fixture(TownArt + "Env_Crate.fbx", hx - 0.6f, hz - 0.7f, 18f),
                new Fixture(TownArt + "Env_Crate.fbx", hx - 0.55f, hz - 1.4f, -30f),
                new Fixture(TownArt + "Env_Barrel.fbx", -hx + 0.6f, hz - 0.7f, 0f),
                new Fixture(TownArt + "Env_Planter.fbx", hx - 0.7f, -hz + 1.1f, 0f),
                new Fixture(TownArt + "Env_Notice_Board.fbx", 0f, hz - 0.35f, 180f),
            };
        }

        private static void BuildDressing(Room room, Transform parent)
        {
            var group = new GameObject("Dressing");
            group.transform.SetParent(parent, false);

            var missing = new HashSet<string>();
            foreach (var fixture in FixturesFor(room))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fixture.Path);
                if (prefab == null) { missing.Add(fixture.Path); continue; }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, group.transform);
                if (instance == null) { missing.Add(fixture.Path); continue; }

                instance.transform.localPosition = fixture.Position;

                // The kit is imported with materials off — the pipeline paints from one atlas
                // per folder — so a module arrives with Unity's grey default on every renderer.
                var atlas = AtlasFor(fixture.Path);
                if (atlas != null)
                {
                    foreach (var renderer in instance.GetComponentsInChildren<MeshRenderer>(true))
                        renderer.sharedMaterial = atlas;
                }

                // Collided before it is turned, so the box is measured on the module's own axes.
                // Renderer bounds are a world-space AABB: measured after a 24 degree yaw, a
                // crate's box comes out a third wider than the crate and the two beside it
                // become one solid the player walks around.
                if (fixture.Solid) AddBoxCollision(instance);
                instance.transform.localRotation = Quaternion.Euler(0f, fixture.Yaw, 0f);
                instance.isStatic = true;
                SetLayer(instance, "Environment");
            }

            foreach (var path in missing)
                Debug.LogWarning($"[Interior] {path} is missing, so part of {room.Scene} is empty.");
        }

        /// <summary>
        /// A box round what a module actually occupies, rather than a mesh collider on its
        /// geometry.
        ///
        /// A room is small and everything in it is within arm's reach of a wall, so the cost of
        /// getting collision wrong here is the player wedged between a barrel and a plinth with
        /// no way out. A box also cannot have the concave pockets a mesh collider on a bench or
        /// a planter has, which are exactly the shapes a character controller gets caught in.
        /// </summary>
        private static void AddBoxCollision(GameObject instance)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;

            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);

            var box = instance.AddComponent<BoxCollider>();
            box.center = instance.transform.InverseTransformPoint(bounds.center);
            box.size = new Vector3(bounds.size.x, bounds.size.y, bounds.size.z);
        }

        private static Material AtlasFor(string path)
        {
            var atlas = path.Contains("/Props/")
                ? "Assets/Game/Art/Props/Materials/M_Env_Props.mat"
                : TownAtlas;
            return AssetDatabase.LoadAssetAtPath<Material>(atlas);
        }

        /// <summary>
        /// The only light in the room.
        ///
        /// A wall lamp module and a real point light at its head, because one without the other
        /// is either a lamp that lights nothing or light with no source. The room is graded by
        /// the CaveInterior key — see the note on this class — so these are what the player
        /// actually sees by, and they are warm and short-ranged on purpose: a room lit evenly to
        /// the corners is a room with no depth in it.
        /// </summary>
        private static void BuildLamps(Room room, Transform parent)
        {
            var group = new GameObject("Lamps");
            group.transform.SetParent(parent, false);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TownArt + "Env_Lamp_Wall.fbx");
            var atlas = AssetDatabase.LoadAssetAtPath<Material>(TownAtlas);
            var hx = room.HalfWidth;
            var hz = room.HalfDepth;

            // The module is 0.68 m deep about its own origin, so half of that is what stands it
            // against the wall rather than half inside it. Height clears the cornice on the
            // lowest of the three rooms — a lamp buried in the beam course is a lamp with its
            // shade cut off.
            const float Mount = 0.34f;
            const float Height = 1.9f;

            var stations = new[]
            {
                new Vector3(-hx + Mount, Height, hz * 0.45f),
                new Vector3(hx - Mount, Height, hz * 0.45f),
                new Vector3(-hx + Mount, Height, -hz * 0.5f),
                new Vector3(hx - Mount, Height, -hz * 0.5f),
                new Vector3(0f, Height, hz - Mount),
            };
            var yaws = new[] { 90f, -90f, 90f, -90f, 180f };

            for (var i = 0; i < stations.Length; i++)
            {
                if (prefab != null)
                {
                    var lamp = (GameObject)PrefabUtility.InstantiatePrefab(prefab, group.transform);
                    if (lamp != null)
                    {
                        lamp.transform.localPosition = stations[i];
                        lamp.transform.localRotation = Quaternion.Euler(0f, yaws[i], 0f);
                        if (atlas != null)
                            foreach (var r in lamp.GetComponentsInChildren<MeshRenderer>(true))
                                r.sharedMaterial = atlas;
                        lamp.isStatic = true;
                        SetLayer(lamp, "Environment");
                    }
                }

                var glow = new GameObject($"Lamplight_{i:D2}");
                glow.transform.SetParent(group.transform, false);
                // Off the wall by a third of a metre. A point light inside the wall it is
                // mounted on spends most of its range lighting the far side of it.
                glow.transform.localPosition = stations[i]
                    + Quaternion.Euler(0f, yaws[i], 0f) * Vector3.forward * 0.35f;

                var light = glow.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.87f, 0.68f);
                light.intensity = 1.7f;
                light.range = 7.5f;
                // No shadows. Five shadow-casting point lights in one room is five extra
                // cubemap passes to soften a shadow nobody is looking at.
                light.shadows = LightShadows.None;
            }
        }

        // --- the way out ---------------------------------------------------------------------

        /// <summary>
        /// The door back out, the marker the player arrives on, and the spawn the rig uses when
        /// this scene is opened on its own.
        ///
        /// The arrival stands two metres inside the room rather than on the threshold. A trigger
        /// fires when a collider is placed inside it, not only when one walks in, so an arrival
        /// on the doorway is a loop: in through the town door, straight back out, one screen
        /// wipe per lap and no input that can break it.
        /// </summary>
        private static void BuildDoorAndSpawns(Room room, Transform parent)
        {
            var inside = new Vector3(0f, 0f, -room.HalfDepth + 2f);

            var arrival = new GameObject("Spawn_FromDoor");
            arrival.transform.SetParent(parent, false);
            arrival.transform.localPosition = inside;
            // Facing into the room, which is the way the player was walking when they stepped
            // through the door outside.
            arrival.transform.localRotation = Quaternion.identity;

            // What PlayerRigSetup puts the rig on when the scene is opened by itself. Same
            // place, because arriving and starting here are the same moment from the room's
            // point of view.
            var spawn = new GameObject("PlayerSpawn");
            spawn.transform.SetParent(parent, false);
            spawn.transform.localPosition = inside;

            var exit = new GameObject("Door_Out");
            exit.transform.SetParent(parent, false);
            exit.transform.localPosition = new Vector3(0f, 0f, -room.HalfDepth + 0.5f);

            var box = exit.AddComponent<BoxCollider>();
            box.size = new Vector3(DoorWidth, DoorHeight, 1f);
            box.center = new Vector3(0f, DoorHeight * 0.5f, 0f);
            box.isTrigger = true;

            exit.AddComponent<LevelTransition>().ConfigureReturn("Town", room.FallbackDoorstep);
            SetLayer(exit, "ZoneTrigger");
        }

        /// <summary>
        /// What tells the rest of the game this is indoors.
        ///
        /// The biome id is what LightingDirector.OnBiomeEntered matches on — it looks for "cave"
        /// or "interior" in the string and raises the CaveInterior grade — so the id is load
        /// bearing, not a label. The zone also closes down three things that only make sense
        /// outdoors: wild encounters, weather, and the roaming creature population, which
        /// otherwise reads its budget from the default and tries to walk Pidgey through the
        /// professor's lab.
        ///
        /// This is the first ZoneDirector in the project. Nothing builds one into the town or
        /// the field, which is why RoamingCreatureSpawner's own comment records that it had to
        /// stop treating a zone as a requirement.
        /// </summary>
        private static void BuildZone(Room room, Transform parent)
        {
            var go = new GameObject("Zone_" + room.Scene);
            go.transform.SetParent(parent, false);

            var zone = go.AddComponent<WorldZone>();
            var so = new SerializedObject(zone);
            SetString(so, "_biomeId", room.BiomeId);
            SetString(so, "_displayName", room.Scene);
            SetInt(so, "_kind", (int)ZoneKind.Cave);
            SetFloat(so, "_encounterRateMultiplier", 0f);
            SetBool(so, "_suppressWeather", true);
            SetInt(so, "_roamerBudget", 0);
            so.ApplyModifiedPropertiesWithoutUndo();

            var director = go.AddComponent<ZoneDirector>();
            var dso = new SerializedObject(director);
            var field = dso.FindProperty("_defaultZone");
            if (field != null) field.objectReferenceValue = zone;
            dso.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Navigation over the room.
        ///
        /// Baked even though nothing walks in here yet. An interior is where an NPC belongs, and
        /// the failure when the surface is missing names the agent rather than the missing bake
        /// — "not close enough to the NavMesh" — which is a long way from the cause. Same
        /// settings as the level's own bake so what an agent can walk is what the player can.
        /// </summary>
        private static void BuildNavigation(GameObject root)
        {
            var surface = root.GetComponent<NavMeshSurface>();
            if (surface == null) surface = root.AddComponent<NavMeshSurface>();

            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = MaskOf("Ground") | MaskOf("Environment") | MaskOf("Interactable");
            surface.overrideVoxelSize = true;
            surface.voxelSize = 0.12f;
            surface.BuildNavMesh();

            if (surface.navMeshData == null)
            {
                Debug.LogWarning("[Interior] The bake produced nothing, so anyone standing in " +
                                 "this room will not be able to move.");
                return;
            }

            const string Dir = "Assets/Game/Data/Navigation";
            if (!AssetDatabase.IsValidFolder(Dir))
                AssetDatabase.CreateFolder("Assets/Game/Data", "Navigation");

            var scene = EditorSceneManager.GetActiveScene();
            var path = $"{Dir}/NavMesh-{scene.name}.asset";
            if (AssetDatabase.LoadAssetAtPath<NavMeshData>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(surface.navMeshData, path);
            AssetDatabase.SaveAssets();
        }

        private static int MaskOf(string layerName)
        {
            var layer = LayerMask.NameToLayer(layerName);
            return layer >= 0 ? 1 << layer : 0;
        }

        // --- mesh plumbing ---------------------------------------------------------------------

        /// <summary>
        /// One generated surface, accumulated panel by panel.
        ///
        /// Everything in a room shares the town atlas, so the whole shell is one mesh with one
        /// material and one draw call. That is only possible because the UVs carry which cell
        /// each surface is painted from — see <see cref="CellRect"/>.
        /// </summary>
        private sealed class MeshData
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int> Triangles = new List<int>();

            /// <summary>
            /// A rectangle, subdivided so its texture repeats about every
            /// <paramref name="tile"/> metres.
            ///
            /// Subdivision is how a cell atlas tiles at all: UVs outside a cell's own rectangle
            /// land in the neighbouring cell, so a wall cannot simply be given a UV of 6 — it
            /// would be painted with six different materials in strips. Each sub-quad gets the
            /// whole cell instead, and the count is rounded so the tiles divide the panel evenly
            /// rather than being cut off at the end.
            ///
            /// The face points along Cross(up, right), which is Unity's own winding rule for the
            /// triangles emitted below.
            /// </summary>
            public void Panel(Vector3 corner, Vector3 right, Vector3 up, float width, float height,
                int cell, float tile)
            {
                if (width <= 0.001f || height <= 0.001f) return;

                var nx = Mathf.Max(1, Mathf.RoundToInt(width / tile));
                var ny = Mathf.Max(1, Mathf.RoundToInt(height / tile));
                var sx = width / nx;
                var sy = height / ny;
                var normal = Vector3.Cross(up, right).normalized;
                var rect = CellRect(cell);

                for (var iy = 0; iy < ny; iy++)
                {
                    for (var ix = 0; ix < nx; ix++)
                    {
                        var origin = corner + right * (ix * sx) + up * (iy * sy);
                        var index = Vertices.Count;

                        Vertices.Add(origin);
                        Vertices.Add(origin + right * sx);
                        Vertices.Add(origin + right * sx + up * sy);
                        Vertices.Add(origin + up * sy);
                        for (var k = 0; k < 4; k++) Normals.Add(normal);

                        Uvs.Add(new Vector2(rect.x, rect.y));
                        Uvs.Add(new Vector2(rect.x + rect.width, rect.y));
                        Uvs.Add(new Vector2(rect.x + rect.width, rect.y + rect.height));
                        Uvs.Add(new Vector2(rect.x, rect.y + rect.height));

                        Triangles.Add(index); Triangles.Add(index + 3); Triangles.Add(index + 2);
                        Triangles.Add(index); Triangles.Add(index + 2); Triangles.Add(index + 1);
                    }
                }
            }
        }

        /// <summary>
        /// Where one cell of the town atlas sits in UV space.
        ///
        /// The same arithmetic as envlib.cell_rect, inset by the same 1.2% of a cell. The inset
        /// is not decoration: without it the mip chain and bilinear filtering sample across the
        /// cell boundary, and a plaster wall gets a line of roof tile down its edge.
        /// </summary>
        private static Rect CellRect(int index)
        {
            const int Grid = 4;
            const float Inset = 0.012f;
            const float Size = 1f / Grid;

            var column = index % Grid;
            var row = index / Grid;   // cell row 0 is the bottom of UV space
            return new Rect(column * Size + Inset * Size, row * Size + Inset * Size,
                            Size * (1f - 2f * Inset), Size * (1f - 2f * Inset));
        }

        private static void MeshObject(Transform parent, string name, MeshData data,
            Material material, string layer, bool collide)
        {
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(data.Vertices);
            mesh.SetNormals(data.Normals);
            mesh.SetUVs(0, data.Uvs);
            mesh.SetTriangles(data.Triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;
            if (collide) go.AddComponent<MeshCollider>().sharedMesh = mesh;

            go.isStatic = true;
            SetLayer(go, layer);
        }

        private static void SetLayer(GameObject go, string layerName)
        {
            var layer = LayerMask.NameToLayer(layerName);
            if (layer < 0) return;
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        private static void SetString(SerializedObject so, string field, string value)
        {
            var property = so.FindProperty(field);
            if (property != null) property.stringValue = value;
        }

        private static void SetFloat(SerializedObject so, string field, float value)
        {
            var property = so.FindProperty(field);
            if (property != null) property.floatValue = value;
        }

        private static void SetInt(SerializedObject so, string field, int value)
        {
            var property = so.FindProperty(field);
            if (property != null) property.intValue = value;
        }

        private static void SetBool(SerializedObject so, string field, bool value)
        {
            var property = so.FindProperty(field);
            if (property != null) property.boolValue = value;
        }
    }
}
