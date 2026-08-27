using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PokeLab.Overworld;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
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

        /// <summary>
        /// Fills the editor window with the Game view for the length of the run.
        ///
        /// A screenshot is the size of the Game view, and in the docked layout this project is
        /// usually left in that is 263x148 — small enough that the capture state stamp alone
        /// covers the whole frame, and no dialogue line, portrait or HUD label in it can be
        /// read. Runs were being judged from pictures that could not show the thing being
        /// judged. superSize would multiply the resolution but drops the overlay UI, which is
        /// most of what needs looking at here, so the window is enlarged instead and the same
        /// frame the player would see is what lands on disk.
        ///
        /// Restored afterwards: leaving somebody's editor maximised is a side effect the probe
        /// has no business having.
        /// </summary>
        private static bool MaximiseGameView(bool maximised)
        {
            var type = System.Type.GetType("UnityEditor.GameView,UnityEditor");
            if (type == null) return false;

            var windows = Resources.FindObjectsOfTypeAll(type);
            if (windows == null || windows.Length == 0) return false;

            var view = windows[0] as EditorWindow;
            if (view == null) return false;

            var was = view.maximized;
            view.maximized = maximised;
            view.Repaint();
            return was;
        }

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
                // The probe runs unattended; without this, the operator switching windows
                // pauses the player loop and the run silently stops mid-legs.
                Application.runInBackground = true;
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

                var wasMaximised = MaximiseGameView(true);
                // A frame for the layout to rebuild at the new size before anything is taken.
                yield return null;

                yield return new WaitForSeconds(Mathf.Max(0f, _request.settleSeconds));

                var player = FindFirstObjectByType<PlayerLocomotion>();
                var reader = FindFirstObjectByType<OverworldInputReader>();
                if (player == null) { result.ok = false; result.error = "No PlayerLocomotion in the scene."; }

                // Warp is for reaching a distant trigger without steering there. Camera-relative
                // stick input feeds back through the rig's own swing — a long guided walk spins
                // in circles — so a run that needs "the player approaches X on foot" warps to
                // just outside X and walks the last honest metres.
                if (player != null && _request.warpTo != null && _request.warpTo.Length >= 3)
                {
                    player.Warp(new Vector3(_request.warpTo[0], _request.warpTo[1], _request.warpTo[2]),
                        player.transform.rotation);
                    yield return null;
                }

                var startPosition = player != null ? player.transform.position : Vector3.zero;

                // Started after the warp, so the first row is the run's real starting point
                // and not a 40-metre teleport that would read as an impossible velocity.
                Trace trace = null;
                if (_request.trace)
                {
                    trace = gameObject.AddComponent<Trace>();
                    trace.Begin(player, _request.traceActors);
                }

                foreach (var leg in _request.legs)
                {
                    var move = leg.move != null && leg.move.Length >= 2
                        ? new Vector2(leg.move[0], leg.move[1])
                        : Vector2.zero;

                    var legStart = player != null ? player.transform.position : Vector3.zero;
                    if (trace != null) trace.Beat(leg.label);

                    // A leg is either a few stills or a filmstrip, never both: the burst holds
                    // the same stick for the same span, so taking the stills as well would
                    // photograph the same seconds twice at two different rates.
                    var burst = leg.burstFps > 0 && trace != null;
                    var shots = burst ? 0 : Mathf.Max(1, leg.shots);
                    var seconds = Mathf.Max(0.1f, leg.seconds);
                    var interval = shots > 0 ? seconds / shots : seconds;

                    if (burst)
                    {
                        var prefix = index.ToString("000") + "_" + Sanitise(leg.label);
                        yield return trace.Burst(outputDir, prefix, leg.burstFps,
                            leg.burstSeconds > 0f ? leg.burstSeconds : seconds,
                            // Re-queued per captured frame for the same reason the stills loop
                            // re-queues per rendered frame: the input system consumes a state
                            // event once, and a stick queued only at the start of a burst is
                            // released for every frame of it but the first.
                            () => InputSystem.QueueStateEvent(pad,
                                new GamepadState { leftStick = move }
                                    .WithButton(GamepadButton.North, leg.interact)));

                        samples.Add(Describe(prefix + "_b###.png", player, reader, move));
                        index++;
                    }

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
                        if (trace != null) trace.Mark(name);

                        // Upscaled, because a shot the size of the Game view is not evidence.
                        //
                        // CaptureScreenshot takes whatever the Game view happens to be, and in
                        // this layout that is 263x148 — small enough that the state stamp alone
                        // covers the frame and no dialogue line, portrait or HUD label can be
                        // read at all. Runs were being judged from pictures that could not show
                        // the thing being judged. superSize multiplies the same framing rather
                        // than changing it, so what lands on disk is the same shot at a size
                        // where the text in it is legible.
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

                if (trace != null)
                    result.legReports = Append(result.legReports, trace.Write(outputDir));

                result.samples = samples.ToArray();
                MaximiseGameView(wasMaximised);
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

        }

        // --- the trace ------------------------------------------------------------------

        /// <summary>
        /// One row per rendered frame: where everything was, and how fast it was going.
        ///
        /// <b>Why this exists.</b> A screenshot answers "what was on screen". It cannot answer
        /// "how fast", "how long", or "did it stutter", because every one of those is a
        /// difference between two moments and a picture is one moment. Runs were being judged
        /// on three stills spread across two seconds -- a sample rate at which a smooth walk,
        /// a stutter, a hitch and a teleport all look the same.
        ///
        /// So each frame is written down. Speed then falls out as a difference between rows,
        /// hit-stop as a dip in timeScale, screen shake as the high-frequency part of the
        /// camera's path. None of that needs a human to look at anything.
        ///
        /// <b>Declared and measured, side by side.</b> Every mover is recorded twice: what it
        /// says it is doing (the agent's own velocity, which is what the animator reads) and
        /// what actually happened to its transform between frames. When those two disagree the
        /// disagreement IS the bug -- an NPC whose agent reports 1.4 m/s while its transform
        /// has not moved is walking on the spot, and one number alone can never show that.
        ///
        /// Sampled at end of frame, which is the only point at which the camera is final:
        /// Cinemachine writes in LateUpdate, so anything reading Camera.main earlier records a
        /// pose that was never rendered.
        /// </summary>
        private sealed class Trace : MonoBehaviour
        {
            private struct Actor
            {
                public string Id;
                public Transform Transform;
                public NavMeshAgent Agent;
                public Vector3 Previous;
            }

            private readonly List<string> _rows = new List<string>();
            private readonly List<Actor> _actors = new List<Actor>();
            private PlayerLocomotion _player;
            private Vector3 _playerPrevious;
            private string _header = "";
            private string _beat = "";
            private string _mark = "";
            private string _pendingShot;
            private int _shots;
            private int _frame;
            private float _origin;

            /// <summary>Labels the rows, so one CSV can hold a whole run and stay readable.</summary>
            public void Beat(string beat) { _beat = Sanitise(beat ?? ""); }

            /// <summary>Notes that a screenshot was taken; lands on the next row.</summary>
            public void Mark(string shot) { _mark = shot ?? ""; }

            public void Begin(PlayerLocomotion player, int cap)
            {
                _player = player;
                _origin = Time.timeSinceLevelLoad;
                _playerPrevious = player != null ? player.transform.position : Vector3.zero;

                // Nearest first, because the ones worth measuring are the ones close enough to
                // be on screen. A populated level has dozens, and a CSV with dozens of actors
                // in it is one that nobody reads.
                var anchor = _playerPrevious;
                var found = new List<Actor>();
                foreach (var npc in FindObjectsByType<NpcController>(FindObjectsSortMode.None))
                    found.Add(Make("npc." + npc.name, npc.transform));
                foreach (var trainer in FindObjectsByType<TrainerController>(FindObjectsSortMode.None))
                    found.Add(Make("trainer." + trainer.name, trainer.transform));

                found.Sort((a, b) => Vector3.SqrMagnitude(a.Transform.position - anchor)
                    .CompareTo(Vector3.SqrMagnitude(b.Transform.position - anchor)));
                for (var i = 0; i < found.Count && i < Mathf.Max(0, cap); i++) _actors.Add(found[i]);

                var head = new StringBuilder(
                    "frame,t,dt,timeScale,beat,shot," +
                    "player_x,player_y,player_z,player_declared,player_measured," +
                    "cam_x,cam_y,cam_z,cam_yaw,cam_pitch,cam_fov,boom");
                foreach (var actor in _actors)
                {
                    head.Append(',').Append(actor.Id).Append("_x")
                        .Append(',').Append(actor.Id).Append("_z")
                        .Append(',').Append(actor.Id).Append("_declared")
                        .Append(',').Append(actor.Id).Append("_measured");
                }
                _header = head.ToString();

                StartCoroutine(Loop());
            }

            private static Actor Make(string id, Transform t)
            {
                return new Actor
                {
                    Id = Sanitise(id),
                    Transform = t,
                    Agent = t != null ? t.GetComponent<NavMeshAgent>() : null,
                    Previous = t != null ? t.position : Vector3.zero,
                };
            }

            private IEnumerator Loop()
            {
                while (true)
                {
                    yield return new WaitForEndOfFrame();

                    // Capture happens here rather than in the caller so that the frame written
                    // to disk and the row describing it are the same frame. Two coroutines both
                    // waiting on end-of-frame resume in an order nobody controls, and that is
                    // how a filmstrip ends up captioned with the telemetry of the frame after.
                    var shot = "";
                    if (!string.IsNullOrEmpty(_pendingShot))
                    {
                        var path = _pendingShot;
                        _pendingShot = null;
                        var texture = ScreenCapture.CaptureScreenshotAsTexture();
                        try
                        {
                            File.WriteAllBytes(path, texture.EncodeToPNG());
                            shot = Path.GetFileName(path);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning("[PlayProbe] Frame not written: " + e.Message);
                        }
                        finally
                        {
                            Destroy(texture);
                            _shots++;
                        }
                    }

                    Sample(string.IsNullOrEmpty(shot) ? _mark : shot);
                    _mark = "";
                }
            }

            /// <summary>
            /// An evenly spaced filmstrip, however long, at however many frames per second.
            ///
            /// Time.captureDeltaTime is what makes it even: the game advances by exactly 1/fps
            /// per frame regardless of how long encoding a PNG takes. Without it the strip is
            /// spaced by disk speed, which is the one thing it must not be measuring.
            /// </summary>
            public IEnumerator Burst(string dir, string prefix, int fps, float seconds, Action pump)
            {
                var restore = Time.captureDeltaTime;
                Time.captureDeltaTime = 1f / Mathf.Max(1, fps);

                var count = Mathf.Max(1, Mathf.RoundToInt(seconds * fps));
                for (var i = 0; i < count; i++)
                {
                    if (pump != null) pump();
                    var target = _shots + 1;
                    _pendingShot = Path.Combine(dir, prefix + "_b" + i.ToString("000") + ".png");
                    // Waits for the sampling loop to have taken it, so the next pump cannot
                    // overwrite a request that has not been served yet.
                    while (_shots < target) yield return null;
                }

                Time.captureDeltaTime = restore;
            }

            private void Sample(string shot)
            {
                var dt = Time.unscaledDeltaTime;
                var camera = Camera.main;
                var p = _player != null ? _player.transform.position : Vector3.zero;
                var c = camera != null ? camera.transform.position : Vector3.zero;
                var euler = camera != null ? camera.transform.eulerAngles : Vector3.zero;
                var boom = camera != null && _player != null
                    ? Vector3.Distance(c, p + Vector3.up * 1.15f) : 0f;

                // Measured speed is distance actually covered over time actually taken.
                // Unscaled, so a hit-stop reads as timeScale dipping rather than as every
                // speed in the file quietly going wrong at the same moment.
                var measured = dt > 0.0001f ? Vector3.Distance(p, _playerPrevious) / dt : 0f;
                _playerPrevious = p;

                var row = new StringBuilder(256);
                row.Append(_frame++).Append(',')
                   .Append(F(Time.timeSinceLevelLoad - _origin)).Append(',')
                   .Append(F(dt)).Append(',')
                   .Append(F(Time.timeScale)).Append(',')
                   .Append(_beat).Append(',')
                   .Append(shot).Append(',')
                   .Append(F(p.x)).Append(',').Append(F(p.y)).Append(',').Append(F(p.z)).Append(',')
                   .Append(F(_player != null ? _player.Speed : 0f)).Append(',')
                   .Append(F(measured)).Append(',')
                   .Append(F(c.x)).Append(',').Append(F(c.y)).Append(',').Append(F(c.z)).Append(',')
                   .Append(F(euler.y)).Append(',').Append(F(euler.x)).Append(',')
                   .Append(F(camera != null ? camera.fieldOfView : 0f)).Append(',')
                   .Append(F(boom));

                for (var i = 0; i < _actors.Count; i++)
                {
                    var actor = _actors[i];
                    var at = actor.Transform != null ? actor.Transform.position : Vector3.zero;
                    var moved = dt > 0.0001f ? Vector3.Distance(at, actor.Previous) / dt : 0f;
                    actor.Previous = at;
                    _actors[i] = actor;

                    row.Append(',').Append(F(at.x)).Append(',').Append(F(at.z)).Append(',')
                       .Append(F(actor.Agent != null ? actor.Agent.velocity.magnitude : 0f))
                       .Append(',').Append(F(moved));
                }

                _rows.Add(row.ToString());
            }

            private static string F(float value)
            {
                return value.ToString("F4", CultureInfo.InvariantCulture);
            }

            /// <summary>Writes the file, and reports in one line what is worth saying.</summary>
            public string Write(string dir)
            {
                StopAllCoroutines();
                if (_rows.Count == 0) return "trace: no frames recorded";

                var path = Path.Combine(dir, "trace.csv");
                var text = new StringBuilder(_header.Length + _rows.Count * 200);
                text.Append(_header).Append('\n');
                foreach (var row in _rows) text.Append(row).Append('\n');
                File.WriteAllText(path, text.ToString(), Encoding.UTF8);

                return "trace: " + _rows.Count + " frames, " + _actors.Count
                     + " tracked actors, " + _shots + " burst frames -> " + path;
            }
        }

        /// <summary>
        /// Safe for a filename and for a CSV cell alike.
        ///
        /// Lives on the enclosing type because both nested classes need it, and a private
        /// member of one of them is invisible to the other.
        /// </summary>
        private static string Sanitise(string label)
        {
            if (string.IsNullOrEmpty(label)) return "leg";
            var sb = new StringBuilder(label.Length);
            foreach (var ch in label)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '.' ? ch : '_');
            return sb.ToString();
        }

        // --- request and result shapes ---------------------------------------------------

        [Serializable]
        private sealed class Request
        {
            public string scene;
            public string outputDir;
            public float settleSeconds = 2.5f;
            /// <summary>World position the player is warped to after settle, before the legs.</summary>
            public float[] warpTo;
            /// <summary>Menu paths run before Play, e.g. a rebuild or a rig repair.</summary>
            public string[] menuItems;

            /// <summary>
            /// Write one row per rendered frame to trace.csv beside the frames.
            ///
            /// Screenshots are taken a few per leg, which is roughly two per second, and that
            /// is far too coarse for anything that moves: a stutter, a shake, a hit-stop and a
            /// clean run all look identical at that sample rate. The trace is what makes
            /// motion answerable at all -- speed is a difference between rows, and you cannot
            /// take a difference from a picture.
            /// </summary>
            public bool trace = true;

            /// <summary>
            /// How many actors besides the player to follow, nearest first.
            ///
            /// Capped because a populated level has dozens and a CSV with dozens of actors is
            /// one nobody reads. The ones that matter are the ones near enough to be on screen.
            /// </summary>
            public int traceActors = 8;

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

            /// <summary>
            /// Capture this leg as an even filmstrip at this many frames per second.
            ///
            /// For anything judged by how it moves rather than by what is on screen. Three
            /// stills spread over two seconds cannot show a squash, a screen shake decaying or
            /// a hit-stop -- those live between the stills. At 30 they are visible, and the
            /// trace beside them puts numbers on the same interval.
            ///
            /// Implemented with Time.captureDeltaTime, so the game advances by exactly 1/fps
            /// per frame no matter how long the encode takes. The filmstrip is therefore
            /// evenly spaced in GAME time even though it is not in wall-clock time -- which is
            /// the honest way round: a frame is where the game was, not where the disk was.
            /// </summary>
            public int burstFps;

            /// <summary>Seconds of burst. Falls back to the leg's own length.</summary>
            public float burstSeconds;
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
