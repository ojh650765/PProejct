using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Boot.Editor
{
    public static class RouteNavigationInspection
    {
        [MenuItem("Tools/Poké Lab/Repair/Inspect Bridge Navigation")]
        public static void Inspect()
        {
            EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity", OpenSceneMode.Single);
            var scene = EditorSceneManager.OpenScene("Assets/Game/Scenes/Field.unity", OpenSceneMode.Additive);
            foreach (var root in scene.GetRootGameObjects())
                if (root.TryGetComponent<NavMeshSurface>(out var surface)) surface.enabled = false;
            Physics.SyncTransforms();
            var lines = new List<string>();
            var bridge = GameObject.Find("Route_Bridge_01").transform;
            NavMesh.SamplePosition(new Vector3(8, 0, 12), out var start, 3, NavMesh.AllAreas);
            for (float x = -6; x <= 6; x += .25f)
            {
                var point = bridge.TransformPoint(new Vector3(x, 1.25f, 0));
                var hits = Physics.RaycastAll(point + Vector3.up * 4, Vector3.down, 10,
                    LayerMask.GetMask("Ground", "Environment", "Water"), QueryTriggerInteraction.Collide);
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                var names = new List<string>();
                foreach (var hit in hits) names.Add($"{hit.collider.name}/{LayerMask.LayerToName(hit.collider.gameObject.layer)} y={hit.point.y:F3}");
                bool found = NavMesh.SamplePosition(point, out var nav, .6f, NavMesh.AllAreas);
                var path = new NavMeshPath();
                if (found) NavMesh.CalculatePath(start.position, nav.position, NavMesh.AllAreas, path);
                lines.Add($"x={x:F2} world={point} nav={found}/{nav.position}/{path.status}: {string.Join(", ", names)}");
            }
            File.WriteAllLines("Temp/bridge_navigation.txt", lines);
        }
    }
}
