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
    /// <summary>Exercises startup without moving the player or writing a save.</summary>
    [InitializeOnLoad]
    public static class HomeStartPlayProbe
    {
        private const string Key = "PokeLab.HomeStartProbe";
        static HomeStartPlayProbe() => EditorApplication.playModeStateChanged += Changed;

        [MenuItem("Tools/Poké Lab/Story/Play Test Home Startup")]
        public static void Begin()
        {
            if (EditorApplication.isPlaying) return;
            Directory.CreateDirectory("previews/home_start");
            File.WriteAllText("Temp/pokelab_jump.txt", "free");
            File.WriteAllText("Temp/home_start_play.json", "{\"state\":\"queued\"}");
            SessionState.SetBool(Key, true);
            EditorSceneManager.OpenScene("Assets/Game/Scenes/MainMenu.unity");
            EditorApplication.isPlaying = true;
        }

        private static void Changed(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Key, false)) return;
            SessionState.SetBool(Key, false);
            var probe = new GameObject("HomeStartPlayProbe").AddComponent<Probe>();
            UnityEngine.Object.DontDestroyOnLoad(probe.gameObject);
            probe.StartCoroutine(probe.Run());
        }

        [Serializable] public sealed class Report
        {
            public string state = "running", phase;
            public List<string> checks = new List<string>(), failures = new List<string>();
        }

        public sealed class Probe : MonoBehaviour
        {
            private readonly Report report = new Report();
            private void Save() => File.WriteAllText("Temp/home_start_play.json", JsonUtility.ToJson(report, true));
            private void Check(bool okay, string label) { (okay ? report.checks : report.failures).Add(label); Save(); }
            private void OnEnable() => Application.logMessageReceived += Logged;
            private void OnDisable() => Application.logMessageReceived -= Logged;
            private void Logged(string message, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    Check(false, message);
            }
            private void FreshMemorySession()
            {
                var host = FindFirstObjectByType<PlayerProfileHost>();
                if (host == null) host = new GameObject("ProbeProfile").AddComponent<PlayerProfileHost>();
                host.NewGame(); // Marks the session begun before Start can read a disk save.
                DebugFlow.SuppressOpening = false;
            }
            private static void AdvanceDialogue()
            {
                var dialogue = DialogueRunner.Instance;
                if (dialogue == null || !dialogue.IsPlaying) return;
                if (dialogue.CurrentLine.HasChoices) dialogue.Choose(0);
                else dialogue.Advance();
            }
            private IEnumerator Opening(string source)
            {
                report.phase = source + " startup"; Save();
                FreshMemorySession();
                yield return SceneManager.LoadSceneAsync(source);
                var deadline = Time.realtimeSinceStartup + 25;
                while (Time.realtimeSinceStartup < deadline &&
                       (SceneManager.GetActiveScene().name != "Interior_PlayerHome" ||
                        EpisodeRunner.Live == null || EpisodeRunner.Live.PlayingEpisodeId != "opening")) yield return null;
                Check(SceneManager.GetActiveScene().name == "Interior_PlayerHome", source + " starts inside the home");
                var runner = EpisodeRunner.Live;
                Check(runner != null && runner.PlayingEpisodeId == "opening", source + " starts TV opening automatically");
                var player = FindFirstObjectByType<PlayerLocomotion>();
                var tv = GameObject.Find("Spawn_WatchTV");
                Check(player != null && tv != null && Vector3.Distance(player.transform.position, tv.transform.position) < .3f,
                    source + " places player at TV without probe relocation");
                bool askedName = false;
                bool capturedDialogue = false;
                deadline = Time.realtimeSinceStartup + 50;
                float next = 0;
                while (runner != null && runner.IsPlaying && Time.realtimeSinceStartup < deadline)
                {
                    askedName |= runner.IsAwaitingName;
                    if (runner.IsAwaitingName) { Check(false, "Opening unexpectedly asks for a name"); break; }
                    if (!capturedDialogue && DialogueRunner.Instance != null && DialogueRunner.Instance.CurrentSequenceId == "home_mom_departure")
                    {
                        capturedDialogue = true;
                        AdvanceDialogue();
                        yield return new WaitForSecondsRealtime(.3f);
                        ScreenCapture.CaptureScreenshot("previews/home_start/" + source + "_dialogue.png");
                    }
                    if (Time.realtimeSinceStartup > next) { AdvanceDialogue(); next = Time.realtimeSinceStartup + .8f; }
                    yield return null;
                }
                Check(!askedName, source + " never opens name input at home");
                Check(runner != null && !runner.IsPlaying, source + " completes TV opening");
                Check(player != null && !player.IsMotionFrozen, source + " restores player control");
                ScreenCapture.CaptureScreenshot("previews/home_start/" + source + ".png");
                yield return null;
            }
            public IEnumerator Run()
            {
                Application.runInBackground = true; Save();
                yield return new WaitForSecondsRealtime(3);
                foreach (var key in new[] { "mother", "elder_woman", "fisher", "player_f", "hiker", "lass", "youngster" })
                {
                    var portrait = Resources.Load<Sprite>("Portraits/" + key);
                    Check(portrait != null && portrait.rect.width >= 250, "Dedicated dialogue portrait loads: " + key);
                }
                yield return Opening("Town");
                if (report.failures.Count > 0) { Finish(); yield break; }
                yield return Opening("Interior_PlayerHome");
                if (report.failures.Count > 0) { Finish(); yield break; }

                report.phase = "Professor asks name in lab"; Save();
                yield return SceneManager.LoadSceneAsync("Interior_Lab");
                yield return new WaitForSecondsRealtime(3);
                var runner = EpisodeRunner.Live;
                Check(runner != null && runner.Play("lab_pokedex"), "Laboratory conversation starts");
                var deadline = Time.realtimeSinceStartup + 50;
                float next = 0;
                bool questionSeen = false, nameSeen = false;
                while (runner != null && runner.IsPlaying && Time.realtimeSinceStartup < deadline)
                {
                    questionSeen |= runner.CurrentBeat == "Dialogue:lab_ask_name";
                    if (runner.IsAwaitingName)
                    {
                        Check(questionSeen, "Professor asks before name entry appears");
                        nameSeen = true;
                        runner.SubmitName("Hikari");
                        yield return null;
                    }
                    if (Time.realtimeSinceStartup > next) { AdvanceDialogue(); next = Time.realtimeSinceStartup + .8f; }
                    yield return null;
                }
                Check(nameSeen, "Name entry appears in laboratory");
                Check(ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile.TrainerName == "Hikari", "Submitted name is kept in profile");
                Check(runner != null && !runner.IsPlaying, "Laboratory conversation completes");
                Check(runner != null && !runner.Play("lab_pokedex"), "Completed lab visit does not ask name again");
                if (profile is PlayerProfile concrete) concrete.SetFlagBool("story.home_journal", true);
                report.phase = "Rival mother arrival"; Save();
                LevelTransition.PendingArrivalSpawn = "Spawn_FromDoor";
                yield return SceneManager.LoadSceneAsync("Interior_PlayerHome");
                yield return null;
                var player = FindFirstObjectByType<PlayerLocomotion>();
                var start = player.transform.position;
                float closest = float.MaxValue, pushed = 0;
                bool sawMother = false;
                deadline = Time.realtimeSinceStartup + 45;
                next = 0;
                while (Time.realtimeSinceStartup < deadline)
                {
                    runner = EpisodeRunner.Live;
                    var mother = GameObject.Find("NPC_RivalMom");
                    if (mother != null && runner != null && runner.PlayingEpisodeId == "home_parcel")
                    {
                        sawMother = true;
                        var offset = mother.transform.position - player.transform.position; offset.y = 0;
                        closest = Mathf.Min(closest, offset.magnitude);
                        offset = player.transform.position - start; offset.y = 0;
                        pushed = Mathf.Max(pushed, offset.magnitude);
                    }
                    if (Time.realtimeSinceStartup > next) { AdvanceDialogue(); next = Time.realtimeSinceStartup + .8f; }
                    if (sawMother && runner != null && !runner.IsPlaying) break;
                    yield return null;
                }
                Check(sawMother && closest >= .85f, "Rival mother keeps player clearance: " + closest.ToString("0.00") + "m");
                Check(pushed < .1f, "Rival mother does not push player: " + pushed.ToString("0.00") + "m");
                Check(runner != null && !runner.IsPlaying, "Parcel scene finishes");
                foreach (var scene in new[] { "Interior_PlayerHome", "Interior_House", "Interior_Lab", "Interior_PokeCentre" })
                {
                    report.phase = "Walk out of " + scene; Save();
                    LevelTransition.PendingArrivalSpawn = "Spawn_FromDoor";
                    yield return SceneManager.LoadSceneAsync(scene);
                    yield return new WaitForSecondsRealtime(2);
                    var entry = GameObject.Find("Entry_Floor");
                    Check(entry != null, scene + " has projecting entry floor");
                    ScreenCapture.CaptureScreenshot("previews/home_start/" + scene + "_exit.png");
                    yield return null;
                    player = FindFirstObjectByType<PlayerLocomotion>();
                    deadline = Time.realtimeSinceStartup + 10;
                    while (SceneManager.GetActiveScene().name == scene && Time.realtimeSinceStartup < deadline)
                    {
                        if (player != null && !player.IsMotionFrozen)
                            player.GetComponent<CharacterController>().Move(Vector3.back * 2 * Mathf.Min(Time.deltaTime, .05f));
                        yield return null;
                    }
                    Check(SceneManager.GetActiveScene().name == "Town", scene + " exit is walkable to Town");
                }
                yield return new WaitForSecondsRealtime(2);
                report.phase = "Uniform alpha fade"; Save();
                var overlay = FindFirstObjectByType<PokeLab.Cinematics.ScreenTransitionOverlay>();
                Check(overlay != null, "Transition overlay exists");
                if (overlay != null)
                {
                    overlay.AttachTo(Camera.main);
                    var fade = StartCoroutine(overlay.CoverIn(1f, PokeLab.Cinematics.WipeStyle.ShutterWipe));
                    yield return new WaitForSecondsRealtime(.5f);
                    var plane = GameObject.Find("FadePlane");
                    var renderer = plane != null ? plane.GetComponent<MeshRenderer>() : null;
                    var properties = new MaterialPropertyBlock();
                    if (renderer != null) renderer.GetPropertyBlock(properties);
                    var color = properties.GetColor("_BaseColor");
                    Check(renderer != null && color.r == 0 && color.g == 0 && color.b == 0 && color.a > .1f && color.a < .9f,
                        "Legacy shutter uses translucent black instead of lines");
                    ScreenCapture.CaptureScreenshot("previews/home_start/fade_half.png");
                    yield return fade;
                    Check(overlay.IsCovered, "Fade reaches full black before scene swap");
                    ScreenCapture.CaptureScreenshot("previews/home_start/fade_black.png");
                    yield return null;
                    yield return overlay.CoverOut(.4f, PokeLab.Cinematics.WipeStyle.SplitWipe);
                    Check(overlay.Coverage == 0, "Legacy split reveals with alpha fade");
                }
                Finish();
            }
            private void Finish()
            {
                report.state = report.failures.Count == 0 ? "passed" : "failed"; Save();
                EditorApplication.isPlaying = false;
            }
        }
    }
}


