using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PokeLab.Overworld;
using PokeLab.Core;
using PokeLab.Vfx;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace PokeLab.Boot.Editor
{
    [InitializeOnLoad]
    public static class BankAndPalettePlayProbe
    {
        const string Key="PokeLab.BankPaletteProbe";
        static BankAndPalettePlayProbe()=>EditorApplication.playModeStateChanged+=Changed;
        [MenuItem("Tools/Poké Lab/Verification/Play Test Banks And Palette")]
        public static void Begin()
        {
            if(EditorApplication.isPlaying)return;
            Directory.CreateDirectory("previews/banks_palette");
            File.WriteAllText("Temp/pokelab_jump.txt","free");SessionState.SetBool(Key,true);
            EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity");EditorApplication.isPlaying=true;
        }
        static void Changed(PlayModeStateChange state)
        {
            if(state!=PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Key,false))return;
            SessionState.SetBool(Key,false);new GameObject("BankPaletteProbe").AddComponent<Probe>().StartCoroutine("Run");
        }
        [Serializable] public class Report { public string state="running";public List<string> checks=new(),failures=new(); }
        public class Probe:MonoBehaviour
        {
            Report report=new(); Gamepad pad;
            void Save()=>File.WriteAllText("Temp/banks_palette_play.json",JsonUtility.ToJson(report,true));
            void Check(bool okay,string note){(okay?report.checks:report.failures).Add(note);Save();}
            IEnumerator Walk(Vector3 start,Vector3 target,string label)
            {
                var player=FindFirstObjectByType<PlayerLocomotion>();
                if(!Physics.Raycast(start+Vector3.up*20,Vector3.down,out var floor,50,LayerMask.GetMask("Ground"))) {Check(false,label+" has no ground");yield break;}
                Check(true,label+" floor "+floor.collider.name+" at "+floor.point);
                player.Warp(floor.point+Vector3.up*.4f,Quaternion.identity);
                yield return new WaitForSecondsRealtime(.6f);
                var placement=player.transform.position-floor.point;placement.y=0;
                Check(placement.magnitude<.4f,label+" closed distant gate does not relocate player");
                var initial=player.transform.position;float deadline=Time.realtimeSinceStartup+9;
                while(Time.realtimeSinceStartup<deadline)
                {
                    var delta=target-player.transform.position;delta.y=0;if(delta.magnitude<.45f)break;
                    var f=Camera.main.transform.forward;f.y=0;f.Normalize();var r=Camera.main.transform.right;r.y=0;r.Normalize();
                    var dir=delta.normalized;
                    InputSystem.QueueStateEvent(pad,new GamepadState{leftStick=new Vector2(Vector3.Dot(dir,r),Vector3.Dot(dir,f))});
                    yield return null;
                }
                InputSystem.QueueStateEvent(pad,new GamepadState());yield return null;
                var remaining=target-player.transform.position;remaining.y=0;
                Check(remaining.magnitude<.6f,label+" walks uphill through real input: "+initial+" -> "+player.transform.position);
                ScreenCapture.CaptureScreenshot("previews/banks_palette/"+label+".png");yield return new WaitForSecondsRealtime(.3f);
            }
            IEnumerator Run()
            {
                Application.runInBackground=true;Save();yield return new WaitForSecondsRealtime(5);
                while(!UnityEngine.SceneManagement.SceneManager.GetSceneByName("Field").isLoaded || !UnityEngine.SceneManagement.SceneManager.GetSceneByName("Route202").isLoaded)yield return null;
                yield return new WaitForSecondsRealtime(2);
                foreach(var trigger in FindObjectsByType<StoryEncounter>(FindObjectsSortMode.None))trigger.enabled=false;
                pad=InputSystem.AddDevice<Gamepad>();
                Check(FindFirstObjectByType<PlayerLocomotion>()!=null,"Player locomotion present");
                yield return Walk(new Vector3(30,0,19),new Vector3(26,0,23),"west_bank");
                yield return Walk(new Vector3(36,0,11),new Vector3(40,0,7),"east_bank");
                var gate=FindFirstObjectByType<StoryGate>();
                var barrier=gate!=null?gate.GetComponent<BoxCollider>():null;
                Check(barrier!=null && barrier.enabled && barrier.size.x<=12f,"Closed gate has a bounded physical barrier");
                if(PokeLab.Core.ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile actual)
                {
                    actual.SetFlagBool("story.gate_open",true);yield return null;
                    Check(barrier!=null && !barrier.enabled,"Story flag opens physical passage");
                }
                var light=FindFirstObjectByType<LightingDirector>();
                if(light!=null){light.SetProgress(.4f);light.ApplyImmediate();light.enabled=false;}
                int original=QualitySettings.GetQualityLevel();
                for(int tier=0;tier<QualitySettings.names.Length;tier++)
                {
                    QualitySettings.SetQualityLevel(tier,true);yield return new WaitForSecondsRealtime(1);
                    ScreenCapture.CaptureScreenshot("previews/banks_palette/palette_"+QualitySettings.names[tier]+".png");
                    Check(Camera.main.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing,"Postprocessing enabled in "+QualitySettings.names[tier]);
                    Check(QualitySettings.activeColorSpace==ColorSpace.Linear,"Linear colour in "+QualitySettings.names[tier]);
                    yield return new WaitForSecondsRealtime(.3f);
                }
                QualitySettings.SetQualityLevel(original,true);if(light!=null)light.enabled=true;
                InputSystem.RemoveDevice(pad);report.state=report.failures.Count==0?"passed":"failed";Save();EditorApplication.isPlaying=false;
            }
        }
    }
}
