using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    public static class OutdoorJoinBuilder
    {
        private static readonly Vector3[] Road = {
            new Vector3(-11,2.31f,0), new Vector3(-12,2.09f,4),
            new Vector3(-28,.261f,4), new Vector3(-32,.237f,8)
        };
        [Serializable] private sealed class GroundBook { public GroundChunk[] ground; }
        [Serializable] private sealed class GroundChunk { public float[] vertices; }
        private static float[] edge;

        public static bool RouteFootprint(float x,float z)=>x>-48 && x<-16 && z>=8 && z<=77;

        public static float RoadDistance(float x,float z,out float height)
        {
            float best=float.MaxValue; height=0;
            for(int i=1;i<Road.Length;i++)
            {
                var a=Road[i-1];var b=Road[i];var ab=new Vector2(b.x-a.x,b.z-a.z);
                var t=Mathf.Clamp01(Vector2.Dot(new Vector2(x-a.x,z-a.z),ab)/ab.sqrMagnitude);
                var distance=Vector2.Distance(new Vector2(x,z),new Vector2(a.x,a.z)+ab*t);
                if(distance>=best)continue;
                best=distance;height=Mathf.Lerp(a.y,b.y,t);
            }
            return best;
        }

        public static bool ClearForRoad(float x,float z)=>RoadDistance(x,z,out _) < 2.8f;

        public static void ShapeGround(ref Vector3 point,ref Color color)
        {
            float distance=RoadDistance(point.x,point.z,out float height);
            if(distance>=4 || point.z>8)return;
            float blend=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(1.8f,4,distance));
            point.y=Mathf.Lerp(point.y,height,blend);
            float dirt=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(1.35f,2.1f,distance));
            color=Color.Lerp(color,new Color(0,1,0,0),dirt);
        }

        public static int[] GroundTriangles(string scene,Vector3[] vertices,int[] source)
        {
            if(scene!="Field" && scene!="Town")return source;
            var result=new List<int>(source.Length);
            for(int i=0;i<source.Length;i+=3)
            {
                var center=(vertices[source[i]]+vertices[source[i+1]]+vertices[source[i+2]])/3;
                if(RouteFootprint(center.x,center.z))continue;
                result.Add(source[i]);result.Add(source[i+1]);result.Add(source[i+2]);
            }
            return result.ToArray();
        }

        // Read the same authored seam vertices as Town. Route202 starts on that
        // exact edge, then eases into its own low terrain over the first 8 metres.
        public static float EntranceHeight(float localX)
        {
            if(edge==null)
            {
                edge=new float[33];
                var book=JsonUtility.FromJson<GroundBook>(File.ReadAllText("Assets/Game/Data/Levels/slice_town_unity.json"));
                foreach(var chunk in book.ground)
                    for(int i=0;i<chunk.vertices.Length;i+=3)
                    {
                        var v=chunk.vertices;
                        if(Mathf.Abs(v[i+2]-8)>.01f)continue;
                        int index=Mathf.RoundToInt(v[i]+48);
                        if(index>=0&&index<33)
                        {
                            var point=new Vector3(v[i],v[i+1],v[i+2]);var color=Color.white;
                            ShapeGround(ref point,ref color);edge[index]=point.y;
                        }
                    }
            }
            float indexX=Mathf.Clamp(localX+16,0,32);
            int left=Mathf.FloorToInt(indexX),right=Mathf.Min(32,left+1);
            return Mathf.Lerp(edge[left],edge[right],indexX-left);
        }

        [MenuItem("Tools/Poké Lab/Story/Rebuild Connected Outdoor World")]
        public static void Build()
        {
            if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop Play mode before rebuilding outdoor scenes.");
            foreach(var scene in new[]{"Town","Field"})
            {
                EditorSceneManager.OpenScene($"Assets/Game/Scenes/{scene}.unity");
                LevelLayoutBuilder.Build(scene);
                PlayerRigSetup.CreateRig();
                EditorSceneManager.SaveOpenScenes();
            }
            Route202Builder.Build();
            SceneSetup.AddToBuildSettings();
            WorldNavigationBuilder.Build();
            Debug.Log("[Outdoor] Town, lake field and Route202 share connected terrain and navigation.");
        }
    }
}
