using System;
using System.Collections.Generic;
using System.IO;
using PokeLab.Overworld;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace PokeLab.Boot.Editor
{
    public static class Route202Builder
    {
        private const string ScenePath = "Assets/Game/Scenes/Route202.unity";
        private const string Art = "Assets/Game/Art/Environment/";
        private const string GroundMaterial = Art + "Terrain/Materials/M_Ground_TerrainBlend.mat";

        [MenuItem("Tools/Poké Lab/Story/Rebuild Home And Route 202")]
        public static void Rebuild()
        {
            EnsureScene();
            InteriorBuilder.EnsureScenesExist();
            SceneSetup.AddToBuildSettings();
            EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity");
            LevelLayoutBuilder.Build("Town");
            PlayerRigSetup.CreateRig();
            EditorSceneManager.SaveOpenScenes();
            WorldNavigationBuilder.Build();
            InteriorBuilder.BuildAll();
            Build();
            EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity");
            Debug.Log("[Route202] Home, signed junction, route terrain and story actors rebuilt.");
        }

        public static void EnsureScene()
        {
            if (!File.Exists(ScenePath) && !AssetDatabase.CopyAsset("Assets/Game/Scenes/Town.unity", ScenePath))
                throw new InvalidOperationException("Could not create Route202 scene.");
        }

        public static void TownJunction(Transform level)
        {
            var position = new Vector3(-11.1f, 2.5f, -.3f);
            Physics.SyncTransforms();
            if (Physics.Raycast(position + Vector3.up * 20, Vector3.down, out var hit, 40, LayerMask.GetMask("Ground")))
                position.y = hit.point.y;
            var sign = Prop(level, "Route202_Sign", "Town/Env_Signpost.fbx", position, 180, 1);
            sign.layer = LayerMask.NameToLayer("Interactable");
            foreach (var collider in sign.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(collider);
            var trigger = sign.AddComponent<BoxCollider>();
            trigger.isTrigger = true; trigger.center = Vector3.up * .7f; trigger.size = new Vector3(1, 1.4f, .7f);
            sign.AddComponent<ChapterRouteEntrance>();
            Marker(level, "Spawn_FromRoute202", new Vector3(-9.5f, position.y + .05f, -1.4f));
        }

        public static void HomeMarkers(Transform interior)
        {
            Marker(interior, "Mark_RivalMom_Talk", new Vector3(.3f, .02f, -.75f));
        }

        [MenuItem("Tools/Poké Lab/Story/Rebuild Route 202 Only")]
        public static void Build()
        {
            EnsureScene();
            EditorSceneManager.OpenScene(ScenePath);
            foreach (var name in new[] { "Level", "Interior", "Route202", "RouteNavigation" })
            { var old = GameObject.Find(name); if (old != null) Object.DestroyImmediate(old); }
            // Remove the copied outdoor bake and streamers before making the independent route.
            foreach (var surface in Object.FindObjectsByType<NavMeshSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.DestroyImmediate(surface);
            var root = new GameObject("Route202").transform;
            BuildTerrain(root);
            for (var z = 3; z <= 65; z += 5)
                foreach (var side in new[] { -1, 1 })
                {
                    var x = side * (11.3f + .6f * Mathf.Sin(z * .4f));
                    Prop(root, $"Tree_{side}_{z}", "Foliage/Env_Tree_Broadleaf_A.fbx", new Vector3(x, Height(x,z), z), z * 17, 1.15f);
                    // The raised boundary is visible terrain; rocks close gaps without walkable tops.
                    if (z % 10 == 2 || z == -3)
                        Prop(root, $"Rock_{side}_{z}", "Terrain/Env_Rock_Boulder_C.fbx", new Vector3(side*13,Height(side*13,z),z), z*11,1.3f);
                }
            foreach (var z in new[] { 14f, 30f, 46f }) BuildGrass(root, z);
            for (var x = -12; x <= 12; x += 3)
                Prop(root, "NorthBank_"+x, "Terrain/Env_Rock_Boulder_C.fbx", new Vector3(x,Height(x,64),64), x*23,1.9f);
            Marker(root, "PlayerSpawn", new Vector3(0,Height(0,3)+.05f,3));
            Marker(root, "Spawn_FromTown", new Vector3(0,Height(0,3)+.05f,3));
            Marker(root, "Mark_Mentor_North", new Vector3(0,Height(0,24)+.02f,24));
            Marker(root, "Mark_Lesson_Bidoof", new Vector3(5.1f,Height(5.1f,14),14));
            Marker(root, "Mark_Mentor_Out", new Vector3(PathX(57),Height(PathX(57),57)+.02f,57));
            InteriorStoryBuilder.Build("Route202",root);
            var mentor=root.Find("NPC_Mentor");
            if(mentor!=null)mentor.localPosition=new Vector3(1,Height(1,12)+.02f,12);
            BuildZone(root);
            root.position=PokeLab.Overworld.World.OutdoorRegion.RouteOrigin;
            root.gameObject.AddComponent<PokeLab.Overworld.World.OutdoorRegion>();
            Bake(root.gameObject);
            PlayerRigSetup.CreateRig();
            foreach(var body in Object.FindObjectsByType<PlayerLocomotion>(FindObjectsSortMode.None))
                body.transform.position=root.Find("PlayerSpawn").position;
            foreach (var rig in Object.FindObjectsByType<OverworldCameraRig>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var camera = new SerializedObject(rig);
                camera.FindProperty("_lockYaw").boolValue = true;
                camera.FindProperty("_fixedYaw").floatValue = 0;
                camera.FindProperty("_fixedPitch").floatValue = 38;
                camera.FindProperty("_minPitch").floatValue = 38;
                camera.FindProperty("_maxPitch").floatValue = 38;
                camera.FindProperty("_restDistance").floatValue = 11;
                camera.ApplyModifiedPropertiesWithoutUndo();
                var orbit = rig.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();
                if (orbit != null)
                {
                    var yaw = orbit.HorizontalAxis; yaw.Value = 0; orbit.HorizontalAxis = yaw;
                    var pitch = orbit.VerticalAxis; pitch.Value = 38; orbit.VerticalAxis = pitch;
                }
            }
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
        }

        private static float PathX(float z) => z < 20 ? 0 : 3f*Mathf.Sin((z-20)*.09f);
        private static float Height(float x,float z)
        {
            var verge = Mathf.Max(0,Mathf.Abs(x)-8);
            var end = Mathf.Max(0,z-60);
            float route=.24f+.12f*Mathf.Sin(Mathf.Max(0,z-18)*.07f)+verge*verge*.11f+end*end*.22f;
            return Mathf.Lerp(OutdoorJoinBuilder.EntranceHeight(x),route,Mathf.SmoothStep(0,1,z/8));
        }

        private static void BuildTerrain(Transform parent)
        {
            var vertices=new List<Vector3>(); var colors=new List<Color>(); var triangles=new List<int>();
            const int width=33, depth=70;
            for(var iz=0;iz<depth;iz++) for(var ix=0;ix<width;ix++)
            {
                float x=ix-16, z=iz;
                vertices.Add(new Vector3(x,Height(x,z),z));
                var road=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(2,3.1f,Mathf.Abs(x-PathX(z))));
                colors.Add(new Color(1-road,road,0,0));
                if(ix==width-1 || iz==depth-1)continue;
                var i=iz*width+ix;
                triangles.AddRange(new[]{i,i+width,i+1,i+1,i+width,i+width+1});
            }
            var mesh=new Mesh{name="Route202_LowTerrain"}; mesh.SetVertices(vertices);mesh.SetColors(colors);mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();mesh.RecalculateBounds();
            const string path="Assets/Game/Data/Navigation/Route202_Terrain.asset";
            var existing=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(existing==null)AssetDatabase.CreateAsset(mesh,path);
            else {EditorUtility.CopySerialized(mesh,existing);Object.DestroyImmediate(mesh);mesh=existing;EditorUtility.SetDirty(mesh);}
            var ground=new GameObject("Ground_Route202");ground.transform.SetParent(parent,false);ground.layer=LayerMask.NameToLayer("Ground");
            ground.AddComponent<MeshFilter>().sharedMesh=mesh;
            ground.AddComponent<MeshRenderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>(GroundMaterial);
            ground.AddComponent<MeshCollider>().sharedMesh=mesh;
        }

        private static void BuildGrass(Transform parent,float z)
        {
            foreach(var side in new[]{-1,1})
            {
                float centerX=PathX(z)+side*5.7f;
                var group=Marker(parent,"Grass_"+z+"_"+side,new Vector3(centerX,Height(centerX,z),z));
                var box=group.AddComponent<BoxCollider>();box.isTrigger=true;box.size=new Vector3(4.2f,2,6);box.center=Vector3.up;
                group.AddComponent<TallGrassPatch>();group.AddComponent<CaptureLessonGrass>();
                var meshes=new List<CombineInstance>();
                for(int ix=0;ix<4;ix++)for(int iz=0;iz<6;iz++)
                {
                    float x=centerX+(ix-1.5f)*.9f, depth=z+(iz-2.5f)*.9f;
                    var tuft=Prop(parent,"GrassTuft","Foliage/Env_TallGrass_Cluster_A.fbx",new Vector3(x,Height(x,depth),depth),ix*73+iz*37,.9f,false);
                    foreach(var filter in tuft.GetComponentsInChildren<MeshFilter>())
                        for(int sub=0;sub<filter.sharedMesh.subMeshCount;sub++)
                            meshes.Add(new CombineInstance {mesh=filter.sharedMesh,subMeshIndex=sub,transform=group.transform.worldToLocalMatrix*filter.transform.localToWorldMatrix});
                    Object.DestroyImmediate(tuft);
                }
                var mesh=new Mesh{name=group.name};mesh.CombineMeshes(meshes.ToArray(),true,true);mesh.RecalculateBounds();
                var path=$"Assets/Game/Data/Navigation/Route202_{group.name}.asset";
                var old=AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if(old==null)AssetDatabase.CreateAsset(mesh,path);
                else{EditorUtility.CopySerialized(mesh,old);Object.DestroyImmediate(mesh);mesh=old;EditorUtility.SetDirty(mesh);}
                group.AddComponent<MeshFilter>().sharedMesh=mesh;
                group.AddComponent<MeshRenderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>(Art+"Foliage/Materials/M_Env_Foliage.mat");
            }
        }

        private static void BuildZone(Transform parent)
        {
            var go=Marker(parent,"Zone_Route202",Vector3.zero);
            var zone=go.AddComponent<WorldZone>();var so=new SerializedObject(zone);
            so.FindProperty("_biomeId").stringValue="route_202";so.FindProperty("_displayName").stringValue="202번도로";
            so.FindProperty("_roamerBudget").intValue=0;so.ApplyModifiedPropertiesWithoutUndo();
            var table=ScriptableObject.CreateInstance<EncounterTable>();var ts=new SerializedObject(table);
            ts.FindProperty("_tableId").stringValue="route202_grass";
            var entries=ts.FindProperty("_entries");entries.arraySize=2;
            for(var i=0;i<2;i++)
            {var e=entries.GetArrayElementAtIndex(i);e.FindPropertyRelative("SpeciesId").intValue=i==0?442:445;e.FindPropertyRelative("MinLevel").intValue=2;e.FindPropertyRelative("MaxLevel").intValue=4;e.FindPropertyRelative("Weight").floatValue=1;e.FindPropertyRelative("Times").intValue=15;e.FindPropertyRelative("Weathers").intValue=63;}
            ts.ApplyModifiedPropertiesWithoutUndo();
            const string path="Assets/Game/Data/Navigation/Route202_Encounters.asset";
            var old=AssetDatabase.LoadAssetAtPath<EncounterTable>(path);
            if(old==null)AssetDatabase.CreateAsset(table,path);else{EditorUtility.CopySerialized(table,old);Object.DestroyImmediate(table);table=old;EditorUtility.SetDirty(table);}
            so.FindProperty("_encounterTable").objectReferenceValue=table;so.ApplyModifiedPropertiesWithoutUndo();
            var director=go.AddComponent<ZoneDirector>();var ds=new SerializedObject(director);ds.FindProperty("_defaultZone").objectReferenceValue=zone;ds.ApplyModifiedPropertiesWithoutUndo();
            var volumeGo=Marker(go.transform,"Route202_Volume",new Vector3(0,8,34));
            var box=volumeGo.AddComponent<BoxCollider>();box.isTrigger=true;box.size=new Vector3(32,40,68);
            var volume=volumeGo.AddComponent<ZoneVolume>();var vs=new SerializedObject(volume);
            vs.FindProperty("_zone").objectReferenceValue=zone;vs.FindProperty("_priority").intValue=20;vs.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Bake(GameObject root)
        {
            Physics.SyncTransforms();var surface=new GameObject("RouteNavigation").AddComponent<NavMeshSurface>();surface.collectObjects=CollectObjects.All;
            surface.useGeometry=NavMeshCollectGeometry.PhysicsColliders;surface.layerMask=LayerMask.GetMask("Ground","Environment","Interactable");surface.overrideVoxelSize=true;surface.voxelSize=.12f;
            surface.BuildNavMesh();var data=surface.navMeshData;if(data==null)throw new InvalidOperationException("Route 202 navigation bake failed.");
            const string path="Assets/Game/Data/Navigation/NavMesh-Route202.asset";
            var existing=AssetDatabase.LoadAssetAtPath<NavMeshData>(path);surface.RemoveData();
            if(existing==null)AssetDatabase.CreateAsset(data,path);else{EditorUtility.CopySerialized(data,existing);Object.DestroyImmediate(data);data=existing;EditorUtility.SetDirty(data);}
            surface.navMeshData=data;surface.AddData();AssetDatabase.SaveAssets();
        }

        private static GameObject Marker(Transform parent,string name,Vector3 position)
        {var go=new GameObject(name);go.transform.SetParent(parent,false);go.transform.localPosition=position;return go;}

        private static GameObject Prop(Transform parent,string name,string path,Vector3 position,float yaw,float scale,bool solid=true)
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Art+path);
            if(prefab==null)throw new InvalidOperationException("Missing route prop: "+path);
            var go=(GameObject)PrefabUtility.InstantiatePrefab(prefab,parent);go.name=name;go.transform.localPosition=position;go.transform.localRotation=Quaternion.Euler(0,yaw,0);go.transform.localScale=Vector3.one*scale;
            var family=path.Split('/')[0];var mat=AssetDatabase.LoadAssetAtPath<Material>(Art+family+"/Materials/M_Env_"+family+".mat");
            foreach(var r in go.GetComponentsInChildren<Renderer>()) {var slots=r.sharedMaterials;for(var i=0;i<slots.Length;i++)slots[i]=mat;r.sharedMaterials=slots;}
            foreach(var t in go.GetComponentsInChildren<Transform>())t.gameObject.layer=LayerMask.NameToLayer("Environment");
            if(solid)
            {
                foreach(var filter in go.GetComponentsInChildren<MeshFilter>())filter.gameObject.AddComponent<MeshCollider>().sharedMesh=filter.sharedMesh;
                var modifier=go.AddComponent<NavMeshModifier>();modifier.overrideArea=true;modifier.area=NavMesh.GetAreaFromName("Not Walkable");
            }
            return go;
        }
    }
}
