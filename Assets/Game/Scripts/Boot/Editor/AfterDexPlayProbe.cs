using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PokeLab.Core;
using PokeLab.Overworld;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Boot.Editor
{
    [InitializeOnLoad]
    public static class AfterDexPlayProbe
    {
        private const string Key="PokeLab.AfterDexProbe";
        static AfterDexPlayProbe()=>EditorApplication.playModeStateChanged+=Changed;
        [MenuItem("Tools/Poké Lab/Story/Play Test Home And Route 202")]
        public static void Begin()=>Start(false);
        [MenuItem("Tools/Poké Lab/Story/Play Test Outdoor Connection")]
        public static void BeginConnection()=>Start(true);
        private static void Start(bool connectionOnly)
        {
            if(EditorApplication.isPlaying)return;
            SessionState.SetBool(Key+".connectionOnly",connectionOnly);
            File.WriteAllText("Temp/pokelab_jump.txt","free");
            Directory.CreateDirectory("previews/after_dex");
            File.WriteAllText("Temp/after_dex_play.json","{\"state\":\"queued\"}");
            GameViewPresentation.HideGizmos();SessionState.SetBool(Key,true);
            EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity");EditorApplication.isPlaying=true;
        }
        private static void Changed(PlayModeStateChange state)
        {
            if(state!=PlayModeStateChange.EnteredPlayMode||!SessionState.GetBool(Key,false))return;
            SessionState.SetBool(Key,false);
            var go=new GameObject("AfterDexPlayProbe");UnityEngine.Object.DontDestroyOnLoad(go);
            var probe=go.AddComponent<Probe>();probe.StartCoroutine(probe.Run());
        }
        [Serializable] public sealed class Report
        {
            public string state="running",step;
            public List<string> checks=new List<string>(),failures=new List<string>(),frames=new List<string>();
        }
        public sealed class Probe:MonoBehaviour
        {
            private readonly Report report=new Report();
            private PlayerProfile profile;
            private float nextAnswer;
            private string lastBeat;
            private bool sawDamage,sawCatch;
            private bool sawBattleCamera;
            private bool sawFieldBidoof;
            private PokeLab.Cinematics.BattlePresenter capturePresenter;
            private CaptureHudWatch captureHud;
            private int captureMessages;
            private bool captureMessageAfterResult;
            private float nextBattleCapture;
            private void Save()=>File.WriteAllText("Temp/after_dex_play.json",JsonUtility.ToJson(report,true));
            private void Check(bool okay,string message)
            { (okay?report.checks:report.failures).Add(message);Save(); }
            private PlayerLocomotion Player=>FindFirstObjectByType<PlayerLocomotion>();

            private void Update()
            {
                if(report.state!="running")return;
                var runner=EpisodeRunner.Live;
                if(runner!=null && runner.PlayingEpisodeId=="route202_capture" && runner.CurrentBeat=="CaptureLesson")
                {
                    var presenter=FindFirstObjectByType<PokeLab.Cinematics.BattlePresenter>();
                    if(presenter!=null && ServiceHub.TryGet<ICinematicHudHook>(out var hud))
                    {
                        if(capturePresenter!=presenter)
                        {
                            if(capturePresenter!=null)capturePresenter.EventObserved-=Observed;
                            capturePresenter=presenter;
                            presenter.EventObserved+=Observed;
                        }
                        if(!ReferenceEquals(hud,captureHud))
                        {
                            if(captureHud==null)captureHud=new CaptureHudWatch(hud);
                            else captureHud.Forward=hud;
                            ServiceHub.Register<ICinematicHudHook>(captureHud);
                            PokeLab.Cinematics.CinematicHooks.Reset();
                        }
                    }
                }
                var beat=runner!=null&&runner.IsPlaying?runner.PlayingEpisodeId+"_"+runner.CurrentBeat:null;
                if(!sawFieldBidoof && beat=="route202_capture_Dialogue:route202_intro")
                {
                    var bidoof=GameObject.Find("Actor_StagedCreature")?.GetComponent<RoamingCreature>();
                    if(bidoof!=null && bidoof.SpeciesId==445 && Camera.main!=null && Visible(bidoof,Camera.main))
                    {
                        sawFieldBidoof=true;
                        StartCoroutine(Capture("previews/after_dex/field_bidoof.png"));
                    }
                }
                if(beat!=null&&beat!=lastBeat)
                {
                    lastBeat=beat;
                    var file=$"previews/after_dex/{report.frames.Count:D3}.png";
                    ScreenCapture.CaptureScreenshot(file);report.frames.Add(file+": "+beat);Save();
                }
                if(ServiceHub.TryGet<IBattleStage>(out var battle) && battle.IsBattleActive && Time.realtimeSinceStartup>=nextBattleCapture)
                {
                    nextBattleCapture=Time.realtimeSinceStartup+4;
                    StartCoroutine(Capture($"previews/after_dex/lesson_{report.frames.Count:D3}.png"));
                }
                if(sawDamage && !sawBattleCamera && battle != null && battle.IsBattleActive)
                {
                    var presenter=FindFirstObjectByType<PokeLab.Cinematics.BattlePresenter>();
                    var camera=presenter!=null?presenter.Rig.OutputCamera:null;
                    var brain=camera!=null?camera.GetComponent<Unity.Cinemachine.CinemachineBrain>():null;
                    var shot=presenter!=null?presenter.Rig.CameraFor(presenter.Rig.Current):null;
                    if(brain!=null && shot!=null && ReferenceEquals(brain.ActiveVirtualCamera,shot) &&
                        Visible(presenter.Stage.ViewOf(BattleSide.Player),camera) &&
                        Visible(presenter.Stage.ViewOf(BattleSide.Opponent),camera))
                    {
                        sawBattleCamera=true;
                        StartCoroutine(Capture("previews/after_dex/lesson_combat.png"));
                    }
                }
                if(Time.realtimeSinceStartup<nextAnswer)return;
                nextAnswer=Time.realtimeSinceStartup+1.6f;
                var dialogue=DialogueRunner.Instance;
                if(dialogue!=null&&dialogue.IsPlaying)
                {if(dialogue.CurrentLine.HasChoices)dialogue.Choose(0);else dialogue.Advance();}
            }

            private sealed class CaptureHudWatch:ICinematicHudHook
            {
                public ICinematicHudHook Forward;
                public int Shakes;
                public bool Resolved;
                public CaptureHudWatch(ICinematicHudHook forward)=>Forward=forward;
                public void SetHudVisible(bool visible,float duration)=>Forward.SetHudVisible(visible,duration);
                public void PlayHudBeat(string id,float intensity)
                {
                    if(id=="capture_shake")Shakes++;
                    if(id=="capture_success" || id=="capture_failed")Resolved=true;
                    Forward.PlayHudBeat(id,intensity);
                }
            }

            private void Observed(BattleEvent e)
            {
                if(!(e is CaptureAttemptEvent capture))return;
                captureMessages++;
                captureMessageAfterResult=captureHud!=null && captureHud.Resolved && captureHud.Shakes==capture.Shakes;
                StartCoroutine(Capture("previews/after_dex/capture_result.png"));
            }

            private void OnDestroy()
            {
                if(capturePresenter!=null)capturePresenter.EventObserved-=Observed;
                if(captureHud!=null && ServiceHub.TryGet<ICinematicHudHook>(out var current) && ReferenceEquals(current,captureHud))
                {
                    ServiceHub.Unregister<ICinematicHudHook>(captureHud);
                    if(!(captureHud.Forward is UnityEngine.Object obj) || obj!=null)
                        ServiceHub.Register<ICinematicHudHook>(captureHud.Forward);
                }
                PokeLab.Cinematics.CinematicHooks.Reset();
            }

            private static bool Visible(Component actor, Camera camera)
            {
                if(actor==null)return false;
                var planes=GeometryUtility.CalculateFrustumPlanes(camera);
                foreach(var renderer in actor.GetComponentsInChildren<Renderer>())
                    if(renderer.enabled && renderer.gameObject.activeInHierarchy && GeometryUtility.TestPlanesAABB(planes,renderer.bounds))return true;
                return false;
            }

            private IEnumerator Capture(string file)
            {
                yield return new WaitForEndOfFrame();
                var texture=ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(file,texture.EncodeToPNG());Destroy(texture);report.frames.Add(file);Save();
            }

            public IEnumerator Run()
            {
                Application.runInBackground=true;Save();yield return new WaitForSeconds(3);
                ServiceHub.TryGet<IPlayerProfile>(out var found);profile=found as PlayerProfile;
                if(profile==null){Check(false,"Profile missing");Finish();yield break;}
                foreach(var flag in new[]{"story.opening_seen","story.gate_open","story.kes_summons_done","story.gate_refused","story.prof_mutter_done","story.bag_seen","story.ambush_done","story.lab_invited","story.pokedex"})profile.SetFlagBool(flag,true);
                foreach(var flag in new[]{"story.home_journal","story.home_ready","story.rival_mom_left","story.capture_learned","reward.home_journal","reward.home_parcel","reward.route202_balls"})profile.SetFlagBool(flag,false);
                if(profile.Party.Count==0)profile.TryAddToParty(PokeLab.Overworld.CreatureFactory.Create(433,5,91));
                var journalBefore=profile.ItemCount("journal");var parcelBefore=profile.ItemCount("parcel");
                var ballsBefore=profile.ItemCount("poke-ball");
                profile.Party[0].CurrentHp=1;
                if(SessionState.GetBool(Key+".connectionOnly",false))
                {
                    profile.SetFlagBool(StoryProgress.HomeReady,true);
                    profile.SetFlagBool(StoryProgress.CaptureLearned,true);
                    Player.Warp(new Vector3(-11,2.4f,0),Quaternion.identity);
                    foreach(var point in new[]{new Vector3(-12,0,4),new Vector3(-28,0,4),new Vector3(-32,0,8),new Vector3(-32,0,11)})
                        yield return WalkTo(point,18);
                    yield return WaitScene("Route202",4);
                    yield return Capture("previews/after_dex/outdoor_arrival.png");
                    Finish();yield break;
                }

                report.step="Route sign before visiting home";Save();
                var sign=FindFirstObjectByType<ChapterRouteEntrance>();
                Check(sign!=null,"Northbound route sign exists");
                if(sign==null){Finish();yield break;}
                sign.Interact(Player.gameObject);yield return WaitIdle(35);
                Check(SceneManager.GetActiveScene().name=="Town"&&!StoryProgress.Has(StoryProgress.HomeReady),"Route entrance waits for the home visit");

                report.step="Walk into player home";Save();
                yield return EnterHome();
                if(SceneManager.GetActiveScene().name!="Interior_PlayerHome"){Finish();yield break;}
                yield return WaitFlag(StoryProgress.HomeReady,95);
                yield return WaitIdle(12);
                Check(profile.ItemCount("journal")==journalBefore+1,"Mom grants exactly one Journal");
                Check(profile.ItemCount("parcel")==parcelBefore+1,"Barry's mother grants exactly one Parcel");
                Check(profile.Party[0].CurrentHp==profile.Party[0].MaxHp,"Home visit restores the party");
                Check(Player!=null&&!Player.IsMotionFrozen,"Home sequence releases controls");
                yield return Capture("previews/after_dex/home.png");
                yield return Walk(Vector3.back,"Interior_PlayerHome","Town",10);
                yield return new WaitForSeconds(1);
                var outside=GameObject.Find("Spawn_Outside_Door_Town_House_01");
                Check(outside!=null&&Player!=null&&Vector3.Distance(Player.transform.position,outside.transform.position)<1,"Home exits to its own doorstep");
                yield return EnterHome();yield return new WaitForSeconds(2);
                Check(profile.ItemCount("journal")==journalBefore+1&&profile.ItemCount("parcel")==parcelBefore+1,"Re-entering home does not repeat rewards");
                yield return Walk(Vector3.back,"Interior_PlayerHome","Town",10);yield return new WaitForSeconds(1);

                report.step="Enter Route 202 and approach mentor";Save();
                sign=FindFirstObjectByType<ChapterRouteEntrance>();
                if(sign==null){Check(false,"Route sign disappeared after return");Finish();yield break;}
                Player.Warp(sign.transform.position+new Vector3(1,.05f,-.8f),Quaternion.LookRotation(new Vector3(-1,0,.8f)));
                yield return new WaitForSeconds(.5f);
                Check(ReferenceEquals(Player.GetComponent<PlayerInteractor>().Current,sign),"Route sign offers a reachable interaction prompt");
                sign.Interact(Player.gameObject);yield return WaitIdle(10);
                var outdoorPlayer=Player;var outdoorCamera=Camera.main;
                Check(SceneManager.GetSceneByName("Town").isLoaded&&SceneManager.GetSceneByName("Field").isLoaded&&SceneManager.GetSceneByName("Route202").isLoaded,"All three outdoor regions are preloaded together");
                foreach(var point in new[]{new Vector3(-11,0,0),new Vector3(-12,0,4),new Vector3(-28,0,4),new Vector3(-32,0,8),new Vector3(-32,0,11)})
                    yield return WalkTo(point,18);
                yield return WaitScene("Route202",4);
                Check(ReferenceEquals(Player,outdoorPlayer)&&ReferenceEquals(Camera.main,outdoorCamera),"Outdoor border preserves the same player and camera");
                if(SceneManager.GetActiveScene().name!="Route202"){Finish();yield break;}
                ServiceHub.TryGet<IPlayerProfile>(out var arrived);Check(ReferenceEquals(arrived,profile),"Player data survives both new scenes");
                var countBefore=profile.Party.Count;var hpBefore=profile.Party[0].CurrentHp;var ppBefore=profile.Party[0].Moves[0].CurrentPp;var moneyBefore=profile.Money;
                ServiceHub.TryGet<IBattleStage>(out var stageService);
                var stage=stageService as PokeLab.Battle.BattleStage;
                if(stage!=null)stage.EventsProduced+=Events;
                var until=Time.realtimeSinceStartup+12;
                while(!EpisodeRunner.Live.IsPlaying&&Time.realtimeSinceStartup<until)
                {if(Player!=null&&!Player.IsMotionFrozen)Player.GetComponent<CharacterController>().Move(Vector3.forward*2*Mathf.Min(Time.deltaTime,.05f));yield return null;}
                yield return WaitFlag(StoryProgress.CaptureLearned,150);yield return WaitIdle(15);
                if(stage!=null)stage.EventsProduced-=Events;
                Check(sawDamage&&sawCatch,"Mentor weakens Bidoof and catches it");
                Check(sawBattleCamera,"Battle camera takes over the dialogue shot and frames both Pokemon");
                Check(captureMessages==1&&captureMessageAfterResult,$"Capture text follows resolution exactly once (messages={captureMessages}, shakes={captureHud?.Shakes}, resolved={captureHud?.Resolved})");
                Check(sawFieldBidoof,"A visible Bidoof stands in the grass during the mentor's introduction");
                Check(GameObject.Find("Actor_StagedCreature")==null,"Caught demonstration Bidoof is removed from the overworld");
                Check(profile.Party.Count==countBefore&&profile.Party[0].CurrentHp==hpBefore&&profile.Party[0].Moves[0].CurrentPp==ppBefore&&profile.Money==moneyBefore,"Demonstration preserves player party, HP, PP and money");
                Check(profile.ItemCount("poke-ball")==ballsBefore+5,"Lesson grants five Poké Balls without consuming player items");
                Check(Player!=null&&!Player.IsMotionFrozen,"Capture lesson restores player controls");
                yield return new WaitForSeconds(1);
                yield return Capture("previews/after_dex/route202.png");
                var restored=new PlayerProfile();
                var world=SaveSystem.Deserialise(SaveSystem.Serialise(profile,new WorldSave {SceneName="Route202"}),restored);
                Check(world!=null&&world.SceneName=="Route202"&&restored.GetFlagBool(StoryProgress.CaptureLearned)&&restored.GetFlagBool(StoryProgress.HomeReady),"Save roundtrip preserves home and capture lesson progress");
                Check(restored.ItemCount("journal")==profile.ItemCount("journal")&&restored.ItemCount("parcel")==profile.ItemCount("parcel")&&restored.ItemCount("poke-ball")==profile.ItemCount("poke-ball"),"Save roundtrip preserves all chapter rewards");
                var walkUntil=Time.realtimeSinceStartup+28;
                while(Player!=null&&Player.transform.position.z<63&&Time.realtimeSinceStartup<walkUntil)
                {
                    float z=Player.transform.position.z-8;
                    float x=-32+(z<20?0:3*Mathf.Sin((z-20)*.09f));
                    var direction=new Vector3(Mathf.Clamp((x-Player.transform.position.x)*2,-1,1),0,1).normalized;
                    Player.GetComponent<CharacterController>().Move(direction*3*Mathf.Min(Time.deltaTime,.05f));
                    yield return null;
                }
                Check(Player!=null&&Player.transform.position.z>=63,"Route center path is physically walkable to the north boundary");
                yield return Capture("previews/after_dex/route202_north.png");
                Player.Warp(new Vector3(-32,.5f,11),Quaternion.Euler(0,180,0));
                yield return Walk(Vector3.back,"Route202","Town",12);yield return new WaitForSeconds(1);
                Check(SceneManager.GetActiveScene().name=="Town","Route 202 returns to town");
                yield return WalkTo(new Vector3(-32,0,11),8);yield return WaitScene("Route202",4);
                Check(profile.ItemCount("poke-ball")==ballsBefore+5&&StoryProgress.Has(StoryProgress.CaptureLearned),"Revisiting Route 202 preserves progress and does not repeat the gift");
                Finish();
            }
            private void Events(IReadOnlyList<BattleEvent> events)
            {
                foreach(var e in events)
                {
                    if(e is DamageDealtEvent damage&&damage.Target==BattleSide.Opponent&&damage.Amount>0)sawDamage=true;
                    if(e is CaptureAttemptEvent capture&&capture.Succeeded)sawCatch=true;
                }
            }
            private IEnumerator WalkTo(Vector3 target,float seconds)
            {
                var until=Time.realtimeSinceStartup+seconds;
                while(Player!=null && Time.realtimeSinceStartup<until)
                {
                    var delta=target-Player.transform.position;delta.y=0;
                    if(delta.magnitude<.35f)break;
                    if(!Player.IsMotionFrozen)Player.GetComponent<CharacterController>().Move(delta.normalized*3*Mathf.Min(Time.deltaTime,.05f));
                    yield return null;
                }
                var remaining=Player!=null?Vector3.ProjectOnPlane(target-Player.transform.position,Vector3.up).magnitude:999;
                Check(remaining<.5f,$"Walkable outdoor connection at {target} (remaining {remaining:0.00}m)");
                if(remaining>=.5f && Player!=null)
                {
                    var details=new System.Text.StringBuilder("Player: "+Player.transform.position+"\n");
                    foreach(var solid in Physics.OverlapSphere(Player.transform.position+Vector3.up*.6f,1.5f))
                        if(!solid.isTrigger)details.AppendLine(solid.name+" "+solid.bounds);
                    File.WriteAllText("Temp/outdoor_connection_obstruction.txt",details.ToString());
                    yield return Capture("previews/after_dex/outdoor_block.png");
                }
                else if(target.z==8)yield return Capture("previews/after_dex/outdoor_connection.png");
            }
            private IEnumerator EnterHome()
            {
                var door=GameObject.Find("Door_Town_House_01");
                if(door==null){Check(false,"Home entrance missing");yield break;}
                var forward=Quaternion.Euler(0,268,0)*Vector3.forward;
                Player.Warp(door.transform.position+forward*1.6f+Vector3.up*.03f,Quaternion.LookRotation(-forward));
                yield return new WaitForSeconds(.3f);
                yield return Walk(-forward,"Town","Interior_PlayerHome",12);
                yield return new WaitForSeconds(1);
            }
            private IEnumerator Walk(Vector3 direction,string from,string to,float seconds)
            {
                var until=Time.realtimeSinceStartup+seconds;
                while(SceneManager.GetActiveScene().name==from&&Time.realtimeSinceStartup<until)
                {if(Player!=null&&!Player.IsMotionFrozen)Player.GetComponent<CharacterController>().Move(direction*2*Mathf.Min(Time.deltaTime,.05f));yield return null;}
                Check(SceneManager.GetActiveScene().name==to,"Walked from "+from+" to "+to);
            }
            private IEnumerator WaitScene(string scene,float seconds)
            {var until=Time.realtimeSinceStartup+seconds;while(SceneManager.GetActiveScene().name!=scene&&Time.realtimeSinceStartup<until)yield return null;Check(SceneManager.GetActiveScene().name==scene,"Loaded "+scene);}
            private IEnumerator WaitFlag(string flag,float seconds)
            {var until=Time.realtimeSinceStartup+seconds;while(!StoryProgress.Has(flag)&&Time.realtimeSinceStartup<until)yield return null;Check(StoryProgress.Has(flag),"Completed "+flag);}
            private IEnumerator WaitIdle(float seconds)
            {yield return null;var until=Time.realtimeSinceStartup+seconds;while(((EpisodeRunner.Live!=null&&EpisodeRunner.Live.IsPlaying)||(DialogueRunner.Instance!=null&&DialogueRunner.Instance.IsPlaying))&&Time.realtimeSinceStartup<until)yield return null;}
            private void Finish(){report.state=report.failures.Count==0?"passed":"failed";Save();EditorApplication.isPlaying=false;}
        }
    }
}
