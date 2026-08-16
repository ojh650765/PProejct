using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Renders the level from the player's own viewpoint at a list of stations and writes
    /// the frames to disk.
    ///
    /// Level review has to happen at eye level and on foot. Reviewing from a far overhead
    /// orbit is how a town full of z-fighting walls, doorless doors and floating props got
    /// signed off: at that range everything reads as "a town", and none of the defects are
    /// more than a pixel. So this reproduces the exploration camera exactly — same locked
    /// yaw, same pitch, same field of view, same boom distance — and moves it along the
    /// paths the player actually walks.
    ///
    /// It is driven by a request file rather than only by a menu item so a review sweep can
    /// be scripted end to end: write the stations, wait for the frames, look at them. The
    /// request lives outside Assets/ deliberately; a file dropped inside it would start an
    /// asset import, which would reload the domain and cancel the capture that was asked
    /// for.
    ///
    /// Note that Unity ticks the editor update loop slowly when its window is not focused.
    /// If a request seems to hang, click the Unity window once.
    /// </summary>
    [InitializeOnLoad]
    public static class LevelWalkthroughCapture
    {
        private static readonly string RequestPath =
            Path.Combine(Directory.GetCurrentDirectory(), "Temp", "pokelab_capture.json");
        private static readonly string ResultPath =
            Path.Combine(Directory.GetCurrentDirectory(), "Temp", "pokelab_capture.result.json");

        static LevelWalkthroughCapture()
        {
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (!File.Exists(RequestPath)) return;

            string json;
            try
            {
                json = File.ReadAllText(RequestPath);
            }
            catch (IOException)
            {
                // Still being written. Try again next tick.
                return;
            }

            File.Delete(RequestPath);

            var result = new Result();
            try
            {
                Run(JsonUtility.FromJson<Request>(json), result);
                result.ok = true;
            }
            catch (Exception e)
            {
                result.ok = false;
                result.error = e.ToString();
                Debug.LogError($"[Capture] {e}");
            }

            File.WriteAllText(ResultPath, JsonUtility.ToJson(result, true));
        }

        [MenuItem("Tools/Poké Lab/Level/Capture Walkthrough (default stations)")]
        private static void CaptureDefault()
        {
            var result = new Result();
            Run(new Request { outputDirectory = "Captures/manual", width = 1600, height = 900 }, result);
            Debug.Log($"[Capture] wrote {result.frames.Count} frame(s) to {result.directory}");
        }

        private static void Run(Request request, Result result)
        {
            if (request == null) throw new ArgumentException("empty capture request");

            if (!string.IsNullOrEmpty(request.scene) &&
                EditorSceneManager.GetActiveScene().path != request.scene)
            {
                EditorSceneManager.OpenScene(request.scene, OpenSceneMode.Single);
            }

            if (request.build) LevelLayoutBuilder.Build();

            var stations = request.stations;
            if (stations == null || stations.Length == 0)
                throw new ArgumentException("capture request carried no stations");

            var directory = Path.Combine(Directory.GetCurrentDirectory(),
                string.IsNullOrEmpty(request.outputDirectory) ? "Captures/latest" : request.outputDirectory);
            Directory.CreateDirectory(directory);
            result.directory = directory;

            var width = request.width > 0 ? request.width : 1600;
            var height = request.height > 0 ? request.height : 900;

            var rigGo = new GameObject("~CaptureCamera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = rigGo.AddComponent<Camera>();
            var urp = rigGo.AddComponent<UniversalAdditionalCameraData>();
            urp.renderPostProcessing = true;
            urp.renderShadows = true;
            urp.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 600f;

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.DefaultHDR)
            {
                antiAliasing = 1
            };
            var readback = new Texture2D(width, height, TextureFormat.RGB24, false);

            try
            {
                for (var i = 0; i < stations.Length; i++)
                {
                    var station = stations[i];
                    Frame(camera, station, request);

                    RenderTo(camera, rt);

                    var previous = RenderTexture.active;
                    RenderTexture.active = rt;
                    readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    readback.Apply(false);
                    RenderTexture.active = previous;
                    camera.targetTexture = null;

                    var name = string.IsNullOrEmpty(station.name)
                        ? i.ToString("D3", CultureInfo.InvariantCulture)
                        : $"{i:D3}_{Sanitise(station.name)}";
                    var file = Path.Combine(directory, name + ".png");
                    File.WriteAllBytes(file, readback.EncodeToPNG());
                    result.frames.Add(file);
                }
            }
            finally
            {
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(readback);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(rigGo);
            }
        }

        /// <summary>
        /// Renders one frame into the target texture.
        ///
        /// Goes through the pipeline's render request when it is available. Calling
        /// Camera.Render directly is the old built-in path and URP honours it only
        /// partially — post-processing and some passes are driven by the pipeline, so a
        /// frame captured that way can differ from the frame the player sees, which would
        /// make this whole tool lie about the thing it exists to check.
        /// </summary>
        private static void RenderTo(Camera camera, RenderTexture target)
        {
            // Camera.SubmitRenderRequest is the entry point the pipeline itself supports;
            // RenderPipeline.IsRenderRequestSupported is protected and SubmitRenderRequest
            // on the pipeline is static, so neither is callable from here. There is no
            // public "is this supported" query, so the fallback is guarded by catch rather
            // than by a test.
            try
            {
                camera.SubmitRenderRequest(new RenderPipeline.StandardRequest { destination = target });
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Capture] Render request rejected ({e.GetType().Name}); " +
                                 "falling back to Camera.Render, which URP honours only " +
                                 "partially — check the frames for missing post-processing.");
            }

            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = null;
        }

        /// <summary>
        /// Places the camera where the exploration rig would put it for a player standing
        /// at this station.
        ///
        /// The rig is an orbital follow at a locked yaw, so the camera sits back along that
        /// yaw from a target at chest height, not at the player's feet. Framing it any
        /// other way would review a picture the player never sees.
        /// </summary>
        private static void Frame(Camera camera, Station station, Request request)
        {
            var yaw = station.overrideYaw ? station.yaw : Defaulted(request.yaw, 45f);
            var pitch = station.overridePitch ? station.pitch : Defaulted(request.pitch, 38f);
            var distance = station.overrideDistance ? station.distance : Defaulted(request.distance, 5.5f);
            camera.fieldOfView = Defaulted(request.fov, 40f);

            var focus = new Vector3(station.position[0], station.position[1], station.position[2])
                        + Vector3.up * Defaulted(request.eyeHeight, 1.15f);
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            camera.transform.rotation = rotation;
            camera.transform.position = focus - rotation * Vector3.forward * distance;
        }

        private static float Defaulted(float value, float fallback) =>
            Mathf.Approximately(value, 0f) ? fallback : value;

        private static string Sanitise(string value)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value.Replace(' ', '_');
        }

        [Serializable]
        private sealed class Request
        {
            public string scene;
            public bool build;
            public string outputDirectory;
            public int width;
            public int height;
            public float yaw;
            public float pitch;
            public float distance;
            public float fov;
            public float eyeHeight;
            public Station[] stations;
        }

        [Serializable]
        private sealed class Station
        {
            public string name;
            public float[] position;
            public bool overrideYaw;
            public float yaw;
            public bool overridePitch;
            public float pitch;
            public bool overrideDistance;
            public float distance;
        }

        [Serializable]
        private sealed class Result
        {
            public bool ok;
            public string error;
            public string directory;
            public List<string> frames = new List<string>();
        }
    }
}
