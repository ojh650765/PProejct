using System;
using System.IO;
using System.Linq;
using PokeLab.Overworld;
using PokeLab.Overworld.World;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace PokeLab.Boot.Editor
{
    /// <summary>Build and validate data-authored routes without duplicating scene-building code.</summary>
    public static class TemplateLevelBuilder
    {
        [Serializable] private sealed class Definition
        {
            public int templateVersion;
            public string scene, biome, displayName;
            public float[] playerSpawn;
            public Checkpoint[] checkpoints;
        }
        [Serializable] private sealed class Checkpoint { public string name; public float[] position; }

        [MenuItem("Tools/Poké Lab/Level/Build Selected Template")]
        public static void BuildSelected() => Build(AssetDatabase.GetAssetPath(Selection.activeObject));

        [MenuItem("Tools/Poké Lab/Level/Verify Meadow Template")]
        public static void VerifyExample() => Build("Assets/Game/Data/Levels/template_templatemeadow.json");

        public static void Build(string path)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before generating a scene.");
            if (!File.Exists(path)) throw new InvalidOperationException("Select a JSON emitted by Tools/Level/template_level.py.");
            var data = JsonUtility.FromJson<Definition>(File.ReadAllText(path));
            if (data.templateVersion != 1 || !System.Text.RegularExpressions.Regex.IsMatch(data.scene ?? "", "^[A-Z][A-Za-z0-9_]{2,48}$")
                || new[] { "Town", "Field", "Route202", "MainMenu", "Battle", "Boot", "Login", "Overworld", "Interior_House", "Interior_Lab", "Interior_PlayerHome", "Interior_PokeCentre" }.Contains(data.scene))
                throw new InvalidOperationException("Invalid template scene name or version.");
            string target = "Assets/Game/Scenes/" + data.scene + ".unity";
            if (!File.Exists(target) && !AssetDatabase.CopyAsset("Assets/Game/Scenes/Town.unity", target))
                throw new InvalidOperationException("Unable to copy the common gameplay rig.");
            EditorSceneManager.OpenScene(target);
            // The shared rig is reusable; the source town's world streaming and navigation are not.
            foreach (var component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (component is WorldStreamer || component is ZoneStreamer || component is OutdoorRegion || component is NavMeshSurface)
                    Object.DestroyImmediate(component);
            foreach (var zone in Object.FindObjectsByType<WorldZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.DestroyImmediate(zone.gameObject);
            LevelLayoutBuilder.BuildFrom(path, data.scene);
            PlayerRigSetup.CreateRig();
            var root = GameObject.Find("Level");
            var zoneHost = new GameObject("Zone_" + data.scene);zoneHost.transform.SetParent(root.transform, false);
            var world = zoneHost.AddComponent<WorldZone>();
            var serialized = new SerializedObject(world);
            serialized.FindProperty("_biomeId").stringValue = data.biome;
            serialized.FindProperty("_displayName").stringValue = data.displayName;
            serialized.FindProperty("_roamerBudget").intValue = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var box = zoneHost.AddComponent<BoxCollider>();box.isTrigger = true;
            var groundBounds = root.GetComponentsInChildren<MeshCollider>().Where(c => c.gameObject.layer == LayerMask.NameToLayer("Ground")).Select(c => c.bounds).ToArray();
            var bounds = groundBounds[0];foreach (var b in groundBounds) bounds.Encapsulate(b);
            box.center = bounds.center + Vector3.up*3;box.size = bounds.size + Vector3.up*10;
            zoneHost.AddComponent<ZoneVolume>();
            var spawn = new Vector3(data.playerSpawn[0], data.playerSpawn[1], data.playerSpawn[2]);
            foreach (var player in Object.FindObjectsByType<PlayerLocomotion>(FindObjectsSortMode.None)) player.transform.position = spawn;
            Physics.SyncTransforms();
            if (!WalkableGround.TryNavMesh(spawn, 1, NavMesh.AllAreas, out var origin))
                throw new InvalidOperationException("Template spawn is not walkable.");
            var navPath = new NavMeshPath();
            foreach (var check in data.checkpoints ?? Array.Empty<Checkpoint>())
            {
                var p = new Vector3(check.position[0], check.position[1], check.position[2]);
                var pickup = root.GetComponentsInChildren<ItemPickup>().FirstOrDefault(x => x.name == check.name);
                bool reachable = pickup != null ? WorldRepairVerification.TryReachPickup(spawn,pickup,out _)
                    : WalkableGround.TryNavMesh(p, 1.5f, NavMesh.AllAreas, out var destination)
                      && NavMesh.CalculatePath(origin.position,destination.position,NavMesh.AllAreas,navPath)
                      && navPath.status == NavMeshPathStatus.PathComplete;
                if (!reachable) throw new InvalidOperationException("Unreachable template placement: " + check.name);
            }
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();AssetDatabase.SaveAssets();
            // Draft templates stay out of the published game until a story link is authored.
            File.WriteAllText("Temp/template_level_verification.json", "{\"state\":\"passed\",\"scene\":\""+data.scene+"\",\"checkpoints\":"+(data.checkpoints?.Length??0)+"}");
            Debug.Log("[Template] Built terrain, props, NPCs, pickups, exits and checked complete walking paths: " + data.scene);
        }
    }
}
