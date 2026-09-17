using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PokeLab.Core;
using PokeLab.Overworld;
using PokeLab.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PokeLab.Boot.Editor
{
    /// <summary>Plays authored episodes and their real presenters; never writes a player save.</summary>
    [InitializeOnLoad]
    public static class StorySequencePlayProbe
    {
        private const string Key = "PokeLab.StoryProbe";
        static StorySequencePlayProbe() => EditorApplication.playModeStateChanged += Changed;

        [MenuItem("Tools/Poké Lab/Repair/Play Test Story Sequences")]
        public static void Begin()
            => BeginMode("all");

        [MenuItem("Tools/Poké Lab/Repair/Play Test Lab Sequence")]
        public static void BeginLab() => BeginMode("lab");

        [MenuItem("Tools/Poké Lab/Repair/Play Test Starter Sequence")]
        public static void BeginStarter() => BeginMode("starter");
        [MenuItem("Tools/Poké Lab/Story/Play Test TV Opening")]
        public static void BeginOpening() => BeginMode("opening");

        private static void BeginMode(string mode)
        {
            if (EditorApplication.isPlaying) return;
            File.WriteAllText("Temp/pokelab_jump.txt", "free");
            GameViewPresentation.HideGizmos();
            SessionState.SetBool(Key, true);
            SessionState.SetString(Key + ".mode", mode);
            EditorSceneManager.OpenScene("Assets/Game/Scenes/" + (mode == "lab" ? "Interior_Lab" : (mode=="opening"||mode=="all") ? "Interior_PlayerHome" : "Town") + ".unity", OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Changed(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Key, false)) return;
            SessionState.SetBool(Key, false);
            var host = new GameObject("StorySequencePlayProbe").AddComponent<Probe>();
            UnityEngine.Object.DontDestroyOnLoad(host.gameObject);
            host.StartCoroutine(host.Run());
        }

        [Serializable] public sealed class Report
        {
            public string state = "running", episode, beat;
            public List<string> completed = new List<string>();
            public List<string> failures = new List<string>();
            public List<string> frames = new List<string>();
            public List<string> poses = new List<string>();
        }

        public sealed class Probe : MonoBehaviour
        {
            private readonly Report report = new Report();
            private float nextAnswer;
            private string lastFrame;
            private float frameAt;
            private void Save() => File.WriteAllText("Temp/story_sequences_play.json", JsonUtility.ToJson(report, true));
            private void OnEnable() => Application.logMessageReceived += Logged;
            private void OnDisable() => Application.logMessageReceived -= Logged;
            private void Logged(string message, string stack, LogType type)
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
                if (!report.failures.Contains(message)) report.failures.Add(message);
                Save();
            }

            public IEnumerator Run()
            {
                Application.runInBackground = true;
                Directory.CreateDirectory("previews/story");
                Save();
                yield return new WaitForSeconds(4);
                DisableTriggers();
                if (ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile p)
                {
                    foreach (var episode in JsonUtility.FromJson<EpisodeBook>(File.ReadAllText(
                                 "Assets/Game/Data/Story/Resources/episodes.json")).Episodes)
                        p.SetFlagBool(episode.CompletionFlag, false);
                    p.SetFlagBool("story.opening_seen", false);
                }
                var mode = SessionState.GetString(Key + ".mode", "all");
                if (mode == "lab")
                {
                    yield return Play("lab_pokedex", "Spawn_FromDoor");
                    Finish();
                    yield break;
                }
                if (mode == "starter")
                {
                    FindFirstObjectByType<PlayerProfileHost>().NewGame();
                    // This targeted probe starts after the town departure. Its gate
                    // must have the same prerequisite state as a real lake visit.
                    if(ServiceHub.TryGet<IPlayerProfile>(out var starterProfile) && starterProfile is PlayerProfile ready)
                        foreach(var flag in new[]{"story.opening_seen","story.gate_open","story.kes_summons_done","story.prof_mutter_done"})
                            ready.SetFlagBool(flag,true);
                    yield return null;
                    yield return Play("field_bag_left", "Prop_ProfessorBag");
                    Finish();
                    yield break;
                }
                yield return Play("opening", "Spawn_WatchTV");
                if(mode=="opening")
                {
                    if(GameObject.Find("Television")==null)report.failures.Add("Opening television mesh is missing");
                    var player=FindFirstObjectByType<PlayerLocomotion>();
                    var until=Time.realtimeSinceStartup+10;
                    while(SceneManager.GetActiveScene().name=="Interior_PlayerHome"&&Time.realtimeSinceStartup<until)
                    {
                        if(player!=null&&!player.IsMotionFrozen)player.GetComponent<CharacterController>().Move(Vector3.back*2*Mathf.Min(Time.deltaTime,.05f));
                        yield return null;
                    }
                    if(SceneManager.GetActiveScene().name!="Town")report.failures.Add("Could not walk outside after TV opening");
                    Finish();yield break;
                }
                yield return SceneManager.LoadSceneAsync("Town",LoadSceneMode.Single);
                yield return new WaitForSeconds(3);DisableTriggers();
                yield return Play("gate_wait_for_kes", "Mark_Player_GateStand");
                yield return Play("kes_summons", "Trigger_Square");
                yield return Play("field_professor_mutters", "NPC_Professor");
                yield return Play("field_bag_left", "Prop_ProfessorBag");
                // The last call includes ambush, starter, first battle, and professor return.
                if (ServiceHub.TryGet<IPlayerProfile>(out var afterBattle))
                    foreach (var creature in afterBattle.Party)
                        if (creature.Moves.Count == 0) report.failures.Add("Starter has no learned moves");
                yield return Play("rival_first_battle", "Mark_Rival_BagSide");
                yield return SceneManager.LoadSceneAsync("Interior_Lab", LoadSceneMode.Single);
                yield return new WaitForSeconds(3);
                DisableTriggers();
                yield return Play("lab_pokedex", "Spawn_FromDoor");
                Finish();
            }

            private void Finish()
            {
                int expected = SessionState.GetString(Key + ".mode", "all") switch { "lab" => 1, "opening" => 1, "starter" => 3, _ => 9 };
                if (report.completed.Count != expected) report.failures.Add($"Expected {expected} completed episodes, got {report.completed.Count}");
                report.state = report.failures.Count == 0 ? "passed" : "failed";
                Save();
                EditorApplication.isPlaying = false;
            }

            private static void DisableTriggers()
            {
                foreach (var trigger in FindObjectsByType<StoryEncounter>(FindObjectsSortMode.None)) trigger.enabled = false;
            }

            private IEnumerator Play(string id, string marker)
            {
                DisableTriggers();
                var runner = FindFirstObjectByType<EpisodeRunner>();
                var player = FindFirstObjectByType<PlayerLocomotion>();
                var target = GameObject.Find(marker);
                float markerDeadline = Time.realtimeSinceStartup + 15f;
                while (target == null && Time.realtimeSinceStartup < markerDeadline)
                {
                    yield return null;
                    target = GameObject.Find(marker);
                }
                if (target == null) { report.failures.Add(id + ": missing staging marker " + marker); Save(); yield break; }
                if (target != null && player != null && WalkableGround.TryFind(target.transform.position + ((marker == "Spawn_FromDoor" || marker == "Spawn_WatchTV") ? Vector3.zero : Vector3.back*1.8f), 2f, out var ground))
                    player.Warp(ground, Quaternion.identity);
                report.poses.Add(id+": target="+target.transform.position+" placed="+player.transform.position);
                Save();
                yield return new WaitForSeconds(1);
                if (!WalkableGround.TrySample(player.transform.position,out _,2,2))
                {
                    report.failures.Add(id+": player has no walkable floor at "+player.transform.position);
                    Save();yield break;
                }
                if (id == "lab_pokedex" && SceneManager.GetActiveScene().name != "Interior_Lab")
                    report.failures.Add("Lab episode left its interior before starting");
                if (runner == null || !runner.Play(id))
                {
                    report.failures.Add(id + (runner == null ? ": missing runner" : ": runner refused start")); Save(); yield break;
                }
                runner.EpisodeFinished += Finished;
                var flockStart=new Dictionary<RoamingCreature,Vector3>();
                var flockSpeeds=new Dictionary<RoamingCreature,float>();
                float approachStarted=-1f;bool flockChecked=false;
                float deadline = Time.realtimeSinceStartup + 240;
                while (runner != null && runner.IsPlaying && Time.realtimeSinceStartup < deadline)
                {
                    if(player!=null && player.transform.position.y < -10)
                    {
                        report.failures.Add("Player fell below the level: "+player.transform.position);
                        break;
                    }
                    if(runner.CurrentBeat=="CreatureApproach")
                    {
                        if(approachStarted<0)approachStarted=Time.realtimeSinceStartup;
                        foreach(var bird in FindObjectsByType<RoamingCreature>(FindObjectsSortMode.None))
                        {
                            if(!bird.name.StartsWith("Actor_") || bird.SpeciesId!=442)continue;
                            if(!flockStart.ContainsKey(bird)){flockStart[bird]=bird.transform.position;flockSpeeds[bird]=0;}
                            var agent=bird.GetComponent<UnityEngine.AI.NavMeshAgent>();
                            if(agent!=null)flockSpeeds[bird]=Mathf.Max(flockSpeeds[bird],agent.velocity.magnitude);
                        }
                    }
                    else if(approachStarted>=0 && !flockChecked)
                    {
                        flockChecked=true;
                        report.poses.Add($"Flock approach: {flockStart.Count} birds, {Time.realtimeSinceStartup-approachStarted:0.00}s");
                        if(flockStart.Count!=4)report.failures.Add("Expected all four Starly in the approach");
                        foreach(var pair in flockStart)
                        {
                            float moved=pair.Key!=null ? Vector3.Distance(pair.Value,pair.Key.transform.position) : 0;
                            report.poses.Add($"Flock bird: moved {moved:0.00}m, peak {flockSpeeds[pair.Key]:0.00}m/s");
                            if(moved<.5f || flockSpeeds[pair.Key]<3f)report.failures.Add("A Starly did not scurry with the flock");
                        }
                    }
                    report.episode = runner.PlayingEpisodeId;
                    report.beat = runner.CurrentBeat;
                    var frame = report.episode + "_" + report.beat;
                    if (frame != lastFrame) { lastFrame = frame; frameAt = Time.realtimeSinceStartup + .35f; Save(); }
                    if (frameAt > 0 && Time.realtimeSinceStartup >= frameAt)
                    {
                        frameAt = 0;
                        report.poses.Add(frame+": player="+player.transform.position+" camera="+Camera.main.transform.position);
                        var file = $"previews/story/{report.frames.Count:D3}.png";
                        ScreenCapture.CaptureScreenshot(file);
                        report.frames.Add(file + ": " + frame);
                        if (id == "lab_pokedex")
                        {
                            var details = new List<string>();
                            foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                                details.Add($"camera {camera.name} enabled={camera.enabled} pos={camera.transform.position} rot={camera.transform.eulerAngles}");
                            foreach (var overlay in FindObjectsByType<PokeLab.Cinematics.ScreenTransitionOverlay>(FindObjectsSortMode.None))
                                details.Add($"overlay {overlay.name} coverage={overlay.Coverage}");
                            foreach (var ui in FindObjectsByType<Image>(FindObjectsSortMode.None))
                                if (ui.enabled && ui.color.a > .5f && ui.rectTransform.rect.width > 900)
                                    details.Add($"image {ui.name} sprite={ui.sprite?.name} color={ui.color} size={ui.rectTransform.rect.size}");
                            File.WriteAllLines("Temp/lab_camera.txt", details);
                        }
                        Save();
                    }
                    Answer(runner);
                    yield return null;
                }
                runner.EpisodeFinished -= Finished;
                if (runner.IsPlaying) report.failures.Add(id + ": exceeded 240 seconds");
                if (player != null && player.IsMotionFrozen) report.failures.Add(id + ": player remained frozen");
                Save();
                if (id == "kes_summons")
                {
                    var friend = GameObject.Find("NPC_Rival");
                    var landing = GameObject.Find("Mark_Rival_BagSide");
                    if (friend == null || landing == null || Vector3.Distance(friend.transform.position, landing.transform.position) > .8f)
                        report.failures.Add("Friend departure did not finish beside the lake bag");
                }
            }

            private void Finished(string id) { report.completed.Add(id); Save(); }
            private void Answer(EpisodeRunner runner)
            {
                if (Time.realtimeSinceStartup < nextAnswer) return;
                nextAnswer = Time.realtimeSinceStartup + 2f;
                if (runner.IsAwaitingName) runner.SubmitName("Ren");
                var dialogue = DialogueRunner.Instance;
                if (dialogue != null && dialogue.IsPlaying)
                {
                    if (dialogue.CurrentLine.HasChoices) dialogue.Choose(0);
                    else dialogue.Advance();
                }
                if (runner.IsAwaitingStarter)
                {
                    var selected = EventSystem.current?.currentSelectedGameObject;
                    var button = selected != null ? selected.GetComponent<Button>() : null;
                    if (button != null && button.IsActive() && button.IsInteractable())
                    {
                        var file = $"previews/story/{report.frames.Count:D3}_starter.png";
                        ScreenCapture.CaptureScreenshot(file);
                        report.frames.Add(file + ": starter UI " + button.name);
                        Save();
                        button.onClick.Invoke();
                    }
                }
                var hud = FindFirstObjectByType<BattleHudView>();
                if (hud != null && hud.gameObject.activeInHierarchy) hud.MoveChosen?.Invoke(0);
            }
        }
    }
}
