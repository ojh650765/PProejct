using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PokeLab.Overworld;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Enters Play mode, drives the game with synthetic input, and reports what the real
    /// camera actually saw.
    ///
    /// This exists because of a failure that has now cost several rounds. The walkthrough
    /// capture tool builds its own camera and its own copy of the level, so every frame it
    /// produced was of something correct — while the scene the player would actually load
    /// had no player in it, then had no saved geometry, then framed the world from thirty
    /// metres up. A tool that reconstructs the thing it is inspecting cannot report that
    /// the real one is broken, and three separate faults hid behind exactly that.
    ///
    /// So this one touches nothing. It presses Play, feeds a gamepad through the same
    /// action asset the player uses, screenshots <see cref="Camera.main"/> — whatever
    /// Cinemachine has done to it — and records where the player and the camera really
    /// were. If the boom is wrong, the numbers say so. If input never reaches locomotion,
    /// the player's position simply does not change, and that is in the report too.
    ///
    /// Driven by a request file rather than a menu item so it can be run without a human
    /// at the keyboard:
    /// <code>
    /// Temp/pokelab_play.json
    /// { "scene": "Assets/Game/Scenes/Overworld.unity",
    ///   "outputDir": "Captures/play",
    ///   "settleSeconds": 2.5,
    ///   "legs": [ { "label": "north", "move": [0, 1], "seconds": 2.0, "shots": 3 } ] }
    /// </code>
    /// The result lands in <c>Temp/pokelab_play.result.json</c>.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayModeProbe
    {
        private static readonly string TempDir =
            Path.Combine(Directory.GetCurrentDirectory(), "Temp");

        private static readonly string RequestPath =
            Path.Combine(TempDir, "pokelab_play.json");

        private static string s_resultPath =
            Path.Combine(TempDir, "pokelab_play.result.json");

        /// <summary>
        /// Every pending request, oldest first: the shared <c>pokelab_play.json</c> and any
        /// <c>pokelab_play.&lt;name&gt;.json</c> beside it.
        ///
        /// One path was fine with one author and is not fine with three. Two workers dropped
        /// requests seconds apart, the second overwrote the first, and the run that came back
        /// answered a question nobody had asked — while the worker who asked the first one sat
        /// waiting for a result file that had already been written and consumed by someone
        /// else. Named requests give each worker its own slot, and each result lands next to
        /// its own request rather than in one shared inbox.
        /// </summary>
        private static IEnumerable<string> PendingRequests()
        {
            if (!Directory.Exists(TempDir)) yield break;

            var found = new List<string>();
            if (File.Exists(RequestPath)) found.Add(RequestPath);
            foreach (var path in Directory.EnumerateFiles(TempDir, "pokelab_play.*.json"))
            {
                if (path.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(path);
            }

            found.Sort((a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
            foreach (var path in found) yield return path;
        }

        private static string ResultPathFor(string requestPath) =>
            requestPath.Substring(0, requestPath.Length - ".json".Length) + ".result.json";

        /// <summary>
        /// Carries the request across the domain reload that entering Play mode causes.
        /// Statics do not survive it; SessionState does, and it is cleared when the editor
        /// closes, so a half-finished probe cannot haunt a later session.
        /// </summary>
        private const string PendingKey = "PokeLab.PlayModeProbe.Pending";

        /// <summary>Where this run's result belongs, carried across the same reload.</summary>
        private const string ResultKey = "PokeLab.PlayModeProbe.ResultPath";

        static PlayModeProbe()
        {
            EditorApplication.update += Poll;
        }

        /// <summary>
        /// Consecutive quiet ticks seen since the request appeared.
        ///
        /// Dropping a request file usually coincides with the script edits it is meant to
        /// test, and a refresh does not begin the instant the editor regains focus. A
        /// single <c>isCompiling</c> check fires in the gap before compilation starts, so
        /// the probe ran the *old* build of the very code it was verifying and reported a
        /// fix as not working. Requiring a run of quiet ticks closes that window.
        /// </summary>
        private static int s_quietTicks;

        private static void Poll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var requestPath = System.Linq.Enumerable.FirstOrDefault(PendingRequests());
            if (requestPath == null) { s_quietTicks = 0; return; }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                s_quietTicks = 0;
                return;
            }
            if (++s_quietTicks < 120) return;
            s_quietTicks = 0;

            string json;
            try { json = File.ReadAllText(requestPath); }
            catch (IOException) { return; } // Still being written; try again next tick.

            s_resultPath = ResultPathFor(requestPath);
            SessionState.SetString(ResultKey, s_resultPath);
            File.Delete(requestPath);

            Request request;
            try { request = JsonUtility.FromJson<Request>(json); }
            catch (Exception e)
            {
                WriteResult(new Result { ok = false, error = "Bad request JSON: " + e.Message });
                return;
            }

            if (request == null || request.legs == null || request.legs.Length == 0)
            {
                WriteResult(new Result { ok = false, error = "Request had no legs to walk." });
                return;
            }

            if (!string.IsNullOrEmpty(request.scene))
            {
                // Play mode was configured to skip the scene reload, so a play session's
                // mutations stayed in memory as the scene and the next session started
                // from them. The player fell out of the world once and then began every
                // subsequent run further down: -231, -401, -1305. Restore Unity's default
                // so Play starts from what is on disk, here as well as in ProjectSettings,
                // because the editor caches the setting it loaded at startup.
                EditorSettings.enterPlayModeOptionsEnabled = false;

                // Reopened unconditionally, and deliberately *without* saving first.
                //
                // Saving here was actively destructive. Play-mode mutations survive in
                // memory when scene reload is disabled, so the save wrote the previous
                // run's fallen player — four hundred metres below the world — over the
                // good scene on disk, and the next run then loaded that as its starting
                // position. Each probe made the next one worse. A probe must read the
                // world, never write it.
                // Through an empty scene, because OpenScene on the scene that is already
                // open returns it as-is rather than re-reading the file.
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(request.scene);
            }

            // Menu items first, so a probe can rebuild or repair what it is about to
            // measure. Without this every fix needs a human to click before it can be
            // Imports whatever has changed on disk since the editor last looked.
            //
            // Auto-refresh is off in this project, so a script edited between two probe runs is
            // simply not compiled: the probe then photographs the previous build of the game
            // and the frames disagree with the source for reasons nothing reports. Refreshing
            // here makes a run always about the code as it stands.
            AssetDatabase.Refresh(ImportAssetOptions.Default);

            // verified, and the verification arrives a round later than the fix.
            foreach (var item in request.menuItems ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(item)) continue;
                if (!EditorApplication.ExecuteMenuItem(item))
                    Debug.LogWarning($"[PlayProbe] Menu item '{item}' could not be executed.");
            }

            // Every frame this run produces will say which scene, mode and beat it is a frame
            // of. Written as a file rather than set directly because the stamp is read during
            // the play-mode domain reload, after this editor code has stopped running.
            try { File.WriteAllText(Path.Combine(TempDir, "pokelab_stamp.txt"), "on"); }
            catch (Exception e) { Debug.LogWarning($"[PlayProbe] Could not arm the state stamp: {e.Message}"); }

            SessionState.SetString(PendingKey, json);
            EditorApplication.isPlaying = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnPlayStarted()
        {
            var json = SessionState.GetString(PendingKey, null);
            if (string.IsNullOrEmpty(json)) return;
            SessionState.EraseString(PendingKey);

            var request = JsonUtility.FromJson<Request>(json);
            var host = new GameObject("~PlayModeProbe") { hideFlags = HideFlags.HideAndDontSave };
            host.AddComponent<Runner>().Begin(request);
        }

        private static void WriteResult(Result result)
        {
            var path = SessionState.GetString(ResultKey, s_resultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonUtility.ToJson(result, true), Encoding.UTF8);
        }

        // --- the in-play half -----------------------------------------------------------

        private sealed class Runner : MonoBehaviour
        {
            private Request _request;

            public void Begin(Request request)
            {
                _request = request;
                StartCoroutine(Probe());
            }

            private IEnumerator Probe()
            {
                var result = new Result { ok = true };
                var samples = new List<string>();
                var outputDir = Path.Combine(Directory.GetCurrentDirectory(),
                    string.IsNullOrEmpty(_request.outputDir) ? "Captures/play" : _request.outputDir);
                Directory.CreateDirectory(outputDir);

                // A pad rather than a keyboard: the analogue stick reaches full deflection in
                // one event, where a key needs the action's own ramp, and the report should
                // measure locomotion rather than how quickly a digital binding ramps up.
                var pad = InputSystem.AddDevice<Gamepad>("ProbePad");
                var index = 0;

                yield return new WaitForSeconds(Mathf.Max(0f, _request.settleSeconds));

                var player = FindFirstObjectByType<PlayerLocomotion>();
                var reader = FindFirstObjectByType<OverworldInputReader>();
                if (player == null) { result.ok = false; result.error = "No PlayerLocomotion in the scene."; }

                var startPosition = player != null ? player.transform.position : Vector3.zero;

                foreach (var leg in _request.legs)
                {
                    var move = leg.move != null && leg.move.Length >= 2
                        ? new Vector2(leg.move[0], leg.move[1])
                        : Vector2.zero;

                    var legStart = player != null ? player.transform.position : Vector3.zero;
                    var shots = Mathf.Max(1, leg.shots);
                    var seconds = Mathf.Max(0.1f, leg.seconds);
                    var interval = seconds / shots;

                    for (var shot = 0; shot < shots; shot++)
                    {
                        var elapsed = 0f;
                        while (elapsed < interval)
                        {
                            // Re-queued every frame: the input system consumes a state event
                            // once, so a single queue would read as a one-frame tap.
                            //
                            // Interact is held for the whole leg rather than tapped, and that
                            // is what makes it fire exactly once: OverworldInputReader reads
                            // WasPressedThisFrame, so a held button is a single press on the
                            // leg's first frame. The stick is released between legs, which
                            // releases this too, so two interact legs are two presses.
                            InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = move }
                                .WithButton(GamepadButton.North, leg.interact));
                            elapsed += Time.deltaTime;
                            yield return null;
                        }

                        var name = $"{index:000}_{Sanitise(leg.label)}_{shot}.png";
                        ScreenCapture.CaptureScreenshot(Path.Combine(outputDir, name));
                        // CaptureScreenshot completes at the end of the *next* frame.
                        yield return new WaitForEndOfFrame();
                        yield return null;

                        samples.Add(Describe(name, player, reader, move));
                        index++;
                    }

                    // Release, so the next leg does not inherit this one's stick.
                    InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = Vector2.zero });
                    yield return null;

                    var travelled = player != null
                        ? Vector3.Distance(legStart, player.transform.position)
                        : 0f;
                    result.legReports = Append(result.legReports,
                        $"{leg.label}: moved {travelled.ToString("F2", CultureInfo.InvariantCulture)} m " +
                        $"in {seconds.ToString("F1", CultureInfo.InvariantCulture)} s");
                }

                if (player != null)
                {
                    result.totalTravel = Vector3.Distance(startPosition, player.transform.position);
                    // The whole point of the probe. A camera that never moved, or a player
                    // that never moved, is the finding — not a footnote.
                    if (result.totalTravel < 0.25f)
                    {
                        result.ok = false;
                        result.error = "The player did not move. Input is not reaching locomotion, " +
                                       "or something is holding control.";
                    }
                }

                result.samples = samples.ToArray();
                InputSystem.RemoveDevice(pad);
                WriteResult(result);

                EditorApplication.isPlaying = false;
            }

            private static string Describe(string shot, PlayerLocomotion player,
                OverworldInputReader reader, Vector2 move)
            {
                var camera = Camera.main;
                var p = player != null ? player.transform.position : Vector3.zero;
                var c = camera != null ? camera.transform.position : Vector3.zero;
                var boom = camera != null && player != null
                    ? Vector3.Distance(c, p + Vector3.up * 1.15f)
                    : 0f;
                var euler = camera != null ? camera.transform.eulerAngles : Vector3.zero;

                return string.Format(CultureInfo.InvariantCulture,
                    "{0} | stick ({1:F1},{2:F1}) | player ({3:F2},{4:F2},{5:F2}) speed {6:F2} " +
                    "| camera ({7:F2},{8:F2},{9:F2}) yaw {10:F1} pitch {11:F1} boom {12:F2}m " +
                    "| inputEnabled {13}",
                    shot, move.x, move.y, p.x, p.y, p.z,
                    player != null ? player.Speed : 0f,
                    c.x, c.y, c.z, euler.y, euler.x, boom,
                    reader != null ? reader.InputEnabled.ToString() : "no reader");
            }

            private static string[] Append(string[] existing, string value)
            {
                var list = new List<string>(existing ?? Array.Empty<string>()) { value };
                return list.ToArray();
            }

            private static string Sanitise(string label)
            {
                if (string.IsNullOrEmpty(label)) return "leg";
                var sb = new StringBuilder(label.Length);
                foreach (var ch in label)
                    sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
                return sb.ToString();
            }
        }

        // --- request and result shapes ---------------------------------------------------

        [Serializable]
        private sealed class Request
        {
            public string scene;
            public string outputDir;
            public float settleSeconds = 2.5f;
            /// <summary>Menu paths run before Play, e.g. a rebuild or a rig repair.</summary>
            public string[] menuItems;
            public Leg[] legs;
        }

        [Serializable]
        private sealed class Leg
        {
            public string label;
            public float[] move;
            public float seconds = 2f;
            public int shots = 3;
            /// <summary>
            /// Press interact once as this leg starts.
            ///
            /// Walking past a thing does not prove the thing works. Everything the player
            /// can do beyond move is behind this one button — signs, doors, healing, item
            /// balls — and a probe that only feeds the stick can report that a pickup is
            /// visible while saying nothing about whether it can be picked up.
            /// </summary>
            public bool interact;
        }

        [Serializable]
        private sealed class Result
        {
            public bool ok;
            public string error;
            public float totalTravel;
            public string[] legReports;
            public string[] samples;
        }
    }
}
