using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PokeLab.Audio;
using PokeLab.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Boot.Editor
{
    [InitializeOnLoad]
    public static class MusicPlaybackProbe
    {
        private const string Key="PokeLab.MusicProbe";
        static MusicPlaybackProbe()=>EditorApplication.playModeStateChanged+=Changed;
        [MenuItem("Tools/Pok\u00e9 Lab/Verification/Play Test Music Ownership")]
        public static void Begin()
        {
            if(EditorApplication.isPlaying)return;
            File.WriteAllText("Temp/pokelab_jump.txt","free");
            Directory.CreateDirectory("previews/music");
            File.WriteAllText("Temp/music_play.json","{\"state\":\"queued\"}");
            SessionState.SetBool(Key,true);
            EditorSceneManager.OpenScene("Assets/Game/Scenes/MainMenu.unity");
            EditorApplication.isPlaying=true;
        }
        private static void Changed(PlayModeStateChange state)
        {
            if(state!=PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Key,false))return;
            SessionState.SetBool(Key,false);
            var probe=new GameObject("MusicPlaybackProbe").AddComponent<Probe>();
            UnityEngine.Object.DontDestroyOnLoad(probe.gameObject);probe.StartCoroutine(probe.Run());
        }
        [Serializable] public sealed class Report
        {
            public string state="running",phase;
            public int frames,maximumConcurrentMusic;
            public List<string> checks=new List<string>(),failures=new List<string>();
        }
        public sealed class Probe:MonoBehaviour
        {
            private readonly Report report=new Report();
            private float nextScan;
            private MusicDirector Music=>ServiceHub.TryGet<MusicDirector>(out var music) ? music : null;
            private void Save()=>File.WriteAllText("Temp/music_play.json",JsonUtility.ToJson(report,true));
            private void Check(bool okay,string label){(okay?report.checks:report.failures).Add(label);Save();}
            private void Update()
            {
                if(report.state!="running")return;
                report.frames++;
                if(Time.unscaledTime<nextScan)return;
                nextScan=Time.unscaledTime+.03f;
                int playing=0;
                foreach(var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
                    if(source.isPlaying && ((source.clip!=null && source.clip.name.StartsWith("Music_")) || (source.outputAudioMixerGroup!=null && source.outputAudioMixerGroup.name=="Music")))playing++;
                report.maximumConcurrentMusic=Mathf.Max(report.maximumConcurrentMusic,playing);
                if(playing>1 && !report.failures.Contains("Multiple music sources played together"))
                    Check(false,"Multiple music sources played together");
            }
            private IEnumerator Settled(string track,float seconds=1.8f)
            {
                yield return new WaitForSecondsRealtime(seconds);
                ServiceHub.TryGet<AudioDirector>(out var director);
                if(director!=null && director.Catalog.TryGet(track,out var entry) && entry.Disabled)
                {
                    Check(Music!=null && Music.PlayingSourceCount==0 && Music.CurrentTrack==null,
                        "Retired procedural cue stays silent: "+track);
                    yield break;
                }
                var deadline=Time.realtimeSinceStartup+6f;
                while(Music!=null && (Music.CurrentTrack!=track || Music.PlayingSourceCount!=1) && Time.realtimeSinceStartup<deadline)yield return null;
                var okay=Music!=null && Music.CurrentTrack==track && Music.PlayingSourceCount==1;
                Check(okay,"Current music is "+track+(okay ? "" : " (actual="+Music?.CurrentTrack+", pending="+Music?.PendingTrack+")"));
            }
            private IEnumerator Load(string scene)
            {
                report.phase="Load "+scene;Save();
                yield return SceneManager.LoadSceneAsync(scene,LoadSceneMode.Single);
                yield return new WaitForSecondsRealtime(3);
                Check(Music!=null && FindObjectsByType<MusicDirector>(FindObjectsSortMode.None).Length==1,"One registered music owner in "+scene);
            }
            public IEnumerator Run()
            {
                Application.runInBackground=true;Save();
                yield return new WaitForSecondsRealtime(4);
                Check(Music!=null,"Menu creates a music controller");
                if(Music==null){Finish();yield break;}
                yield return Settled(AudioIds.MusicTitle,.3f);
                yield return Load("Town");
                GameEvents.RaiseModeChanged(GameMode.Boot,GameMode.Exploring);
                GameEvents.RaiseBiomeEntered(AudioIds.BiomeTown);
                Music.SetCinematicHold(false);
                yield return Settled(Music.SelectExplorationTrack());
                Check(SceneManager.GetSceneByName("Field").isLoaded && SceneManager.GetSceneByName("Route202").isLoaded,"Additive outdoor scenes retain one music owner");

                report.phase="Rapid retarget";Save();
                Music.PlayTrack(AudioIds.MusicRouteDay,.5f);
                yield return new WaitForSecondsRealtime(.05f);
                Music.PlayTrack(AudioIds.MusicTownDay,.5f);
                yield return new WaitForSecondsRealtime(.05f);
                Music.PlayTrack(AudioIds.MusicLakeside,.25f);
                yield return Settled(AudioIds.MusicLakeside,.7f);
                for(int i=0;i<5;i++)Music.PlayTrack(AudioIds.MusicLakeside,.25f);
                yield return Settled(AudioIds.MusicLakeside,.3f);

                report.phase="Cinematic priority";Save();
                Music.SetCinematicHold(true);
                Music.PlayTrack(AudioIds.MusicOpeningIntroduction,.2f);
                GameEvents.RaiseBiomeEntered(AudioIds.BiomeTown);
                yield return Settled(AudioIds.MusicOpeningIntroduction,.6f);
                Music.SetCinematicHold(false);
                yield return Settled(Music.SelectExplorationTrack());

                report.phase="Encounter to battle";Save();
                GameEvents.RaiseModeChanged(GameMode.Exploring,GameMode.EncounterIntro);
                yield return new WaitForSecondsRealtime(.03f);
                GameEvents.RaiseModeChanged(GameMode.EncounterIntro,GameMode.Battle);
                Music.SetBattleKind(BattleKind.Trainer);
                GameEvents.RaiseBiomeEntered(AudioIds.BiomeLakeside);
                yield return Settled(AudioIds.MusicBattleTrainer);

                report.phase="Victory to field";Save();
                GameEvents.RaiseModeChanged(GameMode.Battle,GameMode.BattleOutro);
                Music.EndBattle(BattleOutcome.PlayerVictory);
                yield return Settled(AudioIds.MusicVictoryFanfare,.5f);
                Check(Music.IsOneShot,"Victory uses the same exclusive music channel");
                GameEvents.RaiseModeChanged(GameMode.BattleOutro,GameMode.Exploring);
                yield return Settled(AudioIds.MusicLakeside);
                yield return Settled(AudioIds.MusicLakeside,3.6f);

                report.phase="Cancel fanfare and pending loads";Save();
                ServiceHub.TryGet<AudioDirector>(out var audio);
                audio.PlaySfx(AudioIds.MusicVictoryFanfare);
                yield return Settled(AudioIds.MusicVictoryFanfare,.4f);
                Check(Music.IsOneShot,"Legacy SFX music requests route to the music controller");
                Music.PlaySting(AudioIds.MusicEncounterSting);
                yield return new WaitForSecondsRealtime(.02f);
                Music.FadeOutAll(.1f);
                yield return new WaitForSecondsRealtime(.4f);
                Check(Music.PlayingSourceCount==0 && Music.CurrentTrack==null && Music.PendingTrack==null,"Stop cancels both themes and one-shot starts");
                Music.PlaySting(AudioIds.MusicVictoryFanfare);
                yield return Load("MainMenu");
                yield return Settled(AudioIds.MusicTitle,.5f);
                yield return Load("Interior_PlayerHome");
                yield return Load("Town");
                GameEvents.RaiseModeChanged(GameMode.Boot,GameMode.Exploring);
                GameEvents.RaiseBiomeEntered(AudioIds.BiomeTown);
                yield return Settled(Music.SelectExplorationTrack());

                var duplicate=new GameObject("DuplicateMusicProbe");duplicate.AddComponent<MusicDirector>();
                yield return null;
                Check(FindObjectsByType<MusicDirector>(FindObjectsSortMode.None).Length==1,"Duplicate controller creates no extra transport");
                Destroy(duplicate);
                Music.enabled=false;yield return null;
                Check(Music.PlayingSourceCount==0,"Disabling the owner stops its music source");
                Check(report.maximumConcurrentMusic==1,"At most one music clip played throughout every transition");
                Finish();
            }
            private void Finish(){report.state=report.failures.Count==0 ? "passed" : "failed";Save();EditorApplication.isPlaying=false;}
        }
    }
}
