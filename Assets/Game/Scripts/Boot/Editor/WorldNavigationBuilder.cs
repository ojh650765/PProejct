using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Boot.Editor
{
    public static class WorldNavigationBuilder
    {
        [MenuItem("Tools/Poké Lab/Repair/Bake Connected World Navigation")]
        public static void Build()
        {
            var town = EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity", OpenSceneMode.Single);
            var field = EditorSceneManager.OpenScene("Assets/Game/Scenes/Field.unity", OpenSceneMode.Additive);
            var route = EditorSceneManager.OpenScene("Assets/Game/Scenes/Route202.unity", OpenSceneMode.Additive);
            var surfaces = new List<NavMeshSurface>();
            foreach (var scene in new[] { town, field, route })
                foreach (var root in scene.GetRootGameObjects())
                    if (root.TryGetComponent<NavMeshSurface>(out var surface))
                    { surfaces.Add(surface); surface.RemoveData(); }
            var host = new GameObject("~WorldNavigationBake");
            var bake = host.AddComponent<NavMeshSurface>();
            bake.collectObjects = CollectObjects.All;
            bake.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            bake.layerMask = LayerMask.GetMask("Ground", "Environment", "Interactable");
            bake.overrideVoxelSize = true;
            bake.voxelSize = .12f;
            Physics.SyncTransforms();
            bake.BuildNavMesh();
            var data = bake.navMeshData;
            bake.RemoveData();
            Object.DestroyImmediate(host);
            if (data == null) throw new System.InvalidOperationException("World navigation bake produced no data.");
            const string path = "Assets/Game/Data/Navigation/NavMesh-World.asset";
            var existing = AssetDatabase.LoadAssetAtPath<NavMeshData>(path);
            if (existing == null) AssetDatabase.CreateAsset(data, path);
            else
            {
                EditorUtility.CopySerialized(data, existing);
                Object.DestroyImmediate(data);
                data = existing;
                EditorUtility.SetDirty(data);
            }
            foreach (var surface in surfaces)
            {
                surface.navMeshData = data;
                EditorUtility.SetDirty(surface);
                EditorSceneManager.MarkSceneDirty(surface.gameObject.scene);
            }
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveOpenScenes();
            EditorSceneManager.OpenScene(town.path, OpenSceneMode.Single);
        }
    }
}
