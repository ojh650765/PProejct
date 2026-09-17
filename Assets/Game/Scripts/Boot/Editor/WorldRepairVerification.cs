using System;
using System.Collections.Generic;
using System.IO;
using PokeLab.Overworld;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Boot.Editor
{
    public static class WorldRepairVerification
    {
        private const string ReportPath = "Temp/world_repair_verification.json";
        [Serializable] private sealed class Report
        {
            public string state;
            public List<string> checks = new List<string>();
            public List<string> failures = new List<string>();
        }

        [MenuItem("Tools/Poké Lab/Repair/Rebuild And Verify World")]
        public static void QueueRebuild()
        {
            File.WriteAllText(ReportPath, "{\"state\":\"queued\"}");
            Rebuild();
        }

        [MenuItem("Tools/Poké Lab/Repair/Inspect Open Scene")]
        public static void Inspect()
        {
            Physics.SyncTransforms();
            var report = new Report { state = "inspected" };
            CheckScene(report, EditorSceneManager.GetActiveScene().name);
            Write(report);
        }

        private static void Rebuild()
        {
            var report = new Report { state = "building" };
            Write(report);
            try
            {
                Route202Builder.EnsureScene();
                InteriorBuilder.EnsureScenesExist();
                SceneSetup.AddToBuildSettings();
                foreach (var scene in new[] { "Town", "Field" })
                {
                    EditorSceneManager.OpenScene($"Assets/Game/Scenes/{scene}.unity", OpenSceneMode.Single);
                    LevelLayoutBuilder.Build(scene);
                    PlayerRigSetup.CreateRig();
                    EditorSceneManager.SaveOpenScenes();
                    Physics.SyncTransforms();

                    Write(report);
                }
                WorldNavigationBuilder.Build();
                var fieldScene = EditorSceneManager.OpenScene("Assets/Game/Scenes/Field.unity", OpenSceneMode.Additive);
                foreach (var root in fieldScene.GetRootGameObjects())
                    if (root.TryGetComponent<Unity.AI.Navigation.NavMeshSurface>(out var navigation)) navigation.enabled = false;
                Physics.SyncTransforms();
                CheckScene(report, "Town");
                CheckScene(report, "Field");
                InteriorBuilder.BuildAll();
                foreach (var name in InteriorBuilder.SceneNames())
                {
                    EditorSceneManager.OpenScene($"Assets/Game/Scenes/{name}.unity", OpenSceneMode.Single);
                    if (GameObject.Find("Spawn_FromDoor") == null)
                        report.failures.Add(name + ": missing arrival spawn");
                    if (UnityEngine.Object.FindObjectsByType<LevelTransition>(FindObjectsSortMode.None).Length == 0)
                        report.failures.Add(name + ": missing exit");
                    if (name == "Interior_Lab" && GameObject.Find("NPC_Professor") == null)
                        report.failures.Add(name + ": missing professor");
                    report.checks.Add(name + ": generated room, arrival and return transition checked");
                }
                Route202Builder.Build();
                report.state = report.failures.Count == 0 ? "passed" : "failed";
            }
            catch (Exception ex)
            {
                report.state = "failed";
                report.failures.Add(ex.ToString());
                Debug.LogException(ex);
            }
            finally
            {
                Write(report);
                EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity", OpenSceneMode.Single);
            }
        }

        private static void CheckScene(Report report, string scene)
        {
            var loaded = UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene);
            var root = Array.Find(loaded.GetRootGameObjects(), go => go.name == "Level");
            if (root == null) { report.failures.Add(scene + ": no level root"); return; }
            int doors = 0, pickups = 0, testedProps = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>())
            {
                if (t.name.StartsWith("Door_Town_", StringComparison.Ordinal))
                {
                    doors++;
                    var box = t.GetComponent<BoxCollider>();
                    if (box == null || !box.isTrigger || t.GetComponent<LevelTransition>() == null)
                        report.failures.Add(t.name + ": missing door trigger/transition");
                    var approach = t.position + t.forward * 1.25f;
                    if (!WalkableGround.TrySample(approach, out var floor))
                        report.failures.Add(t.name + ": approach has no dry floor");
                    else if (!WalkableGround.TryNavMesh(floor, .8f, NavMesh.AllAreas, out _))
                    {
                        report.failures.Add(t.name + ": approach has no walkable NavMesh");
                        NavMesh.SamplePosition(floor, out var nearest, 5f, NavMesh.AllAreas);
                        var blockers = Physics.OverlapCapsule(floor+Vector3.up*.3f,
                            floor+Vector3.up*1.3f, .3f, LayerMask.GetMask("Environment", "Ground"),
                            QueryTriggerInteraction.Ignore);
                        var names = new List<string>();
                        foreach (var b in blockers) names.Add(b.name);
                        report.checks.Add($"{t.name}: floor={floor}, nearestNav={nearest.position}, overlaps={string.Join(",", names)}");
                    }
                }
                var pickup = t.GetComponent<ItemPickup>();
                if (pickup != null)
                {
                    pickups++;
                    if (t.gameObject.layer != LayerMask.NameToLayer("Interactable"))
                        report.failures.Add(t.name + ": pickup layer overwritten");
                    var spawn = GameObject.Find(scene == "Field" ? "Spawn_FromTown" : "PlayerSpawn");
                    if (spawn == null || !TryReachPickup(spawn.transform.position, pickup, out var stand))
                        report.failures.Add(t.name + ": no complete dry route and clear interaction line from the scene entrance");
                    else report.checks.Add(t.name + $": reachable pickup, stand at {stand}");
                }
                if (!t.name.Contains("Bin") && !t.name.Contains("Trash")) continue;
                foreach (var collider in t.GetComponentsInChildren<Collider>())
                {
                    if (collider.isTrigger) continue;
                    testedProps++;
                    var top = collider.bounds.center;
                    top.y = collider.bounds.max.y;
                    if (WalkableGround.TryNavMesh(top, .12f, NavMesh.AllAreas, out _))
                        report.failures.Add(t.name + ": trash container top is walkable");
                }
            }
            if (scene == "Town" && doors != 9) report.failures.Add($"Town: expected 9 doors, found {doors}");
            int expectedPickups = scene == "Town" ? 3 : 2;
            if (pickups != expectedPickups) report.failures.Add($"{scene}: expected {expectedPickups} pickups, found {pickups}");
            report.checks.Add($"{scene}: {doors} entrances, {pickups} pickups, {testedProps} bin colliders checked");
        }

        private static void Write(Report report) => File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));

        internal static bool TryReachPickup(Vector3 spawn, ItemPickup pickup, out Vector3 standing)
        {
            standing = default;
            if (!WalkableGround.TryNavMesh(spawn, 3f, NavMesh.AllAreas, out var start)) return false;
            var path = new NavMeshPath();
            var collider = pickup.GetComponent<Collider>();
            var target = collider != null ? collider.bounds.center : pickup.transform.position;
            for (float radius = .6f; radius <= 1.81f; radius += .6f)
                for (int i = 0; i < 16; i++)
                {
                    float angle = i*Mathf.PI/8;
                    var candidate = target + new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle))*radius;
                    if (!WalkableGround.TryNavMesh(candidate, .5f, NavMesh.AllAreas, out var hit)) continue;
                    if (!NavMesh.CalculatePath(start.position, hit.position, NavMesh.AllAreas, path)
                        || path.status != NavMeshPathStatus.PathComplete) continue;
                    var eye = hit.position + Vector3.up*.8f;
                    if (Vector3.Distance(eye, target) > 2.5f) continue;
                    if (Physics.Linecast(eye, target, out var blocked, LayerMask.GetMask("Ground", "Environment"),
                        QueryTriggerInteraction.Ignore)
                        && !blocked.transform.IsChildOf(pickup.transform)) continue;
                    standing = hit.position;
                    return true;
                }
            return false;
        }
    }
}
