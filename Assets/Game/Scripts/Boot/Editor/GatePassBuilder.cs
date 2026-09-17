using System;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>Imports the Blender-authored banks at their height-grid world coordinates.</summary>
    public static class GatePassBuilder
    {
        private const string DataPath = "Assets/Game/Art/Environment/Terrain/GatePass/gate_pass_meshes.json";
        [Serializable] private sealed class MeshBook { public MeshData[] meshes; }
        [Serializable] private sealed class MeshData
        {
            public string name;
            public float[] vertices, colors, uvs;
            public int[] triangles;
        }

        public static void Build(Transform root, Material material)
        {
            if (!File.Exists(DataPath)) return;
            var book = JsonUtility.FromJson<MeshBook>(File.ReadAllText(DataPath));
            foreach (var data in book.meshes)
            {
                var mesh = new Mesh { name = data.name };
                var vertices = new Vector3[data.vertices.Length / 3];
                var colors = new Color[vertices.Length];
                var uvs = new Vector2[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = new Vector3(data.vertices[i*3], data.vertices[i*3+1], data.vertices[i*3+2]);
                    colors[i] = new Color(data.colors[i*4],data.colors[i*4+1],data.colors[i*4+2],data.colors[i*4+3]);
                    uvs[i] = new Vector2(data.uvs[i*2],data.uvs[i*2+1]);
                }
                mesh.vertices = vertices;
                mesh.triangles = data.triangles;
                mesh.colors = colors;
                mesh.uv = uvs;
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                var go = new GameObject(data.name);
                go.transform.SetParent(root, false);
                go.isStatic = true;
                go.layer = LayerMask.NameToLayer("Environment");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                var modifier = go.AddComponent<NavMeshModifier>();
                modifier.overrideArea = true;
                modifier.area = 1; // Not Walkable: a wall cap is never an NPC destination.
            }
        }
    }
}
