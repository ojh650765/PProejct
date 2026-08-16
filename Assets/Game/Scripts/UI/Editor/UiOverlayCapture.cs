using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI.Editor
{
    /// <summary>
    /// Renders a runtime-built UI screen to a PNG, without entering play mode.
    ///
    /// The level capture next door photographs the world camera, which is exactly the wrong
    /// tool for a HUD: a screen-space overlay canvas is composited after the camera has
    /// finished and never appears in that frame. So this builds the view into a throwaway
    /// canvas driven by its own camera, which is the only arrangement a RenderTexture can
    /// see, and puts a real game frame behind it — a UI screenshot on a flat grey card
    /// proves nothing about a translucent overlay whose entire job is to sit on top of
    /// bright, busy pixel art.
    ///
    /// Motion is disabled for the duration. Every tween in this UI completes synchronously
    /// when <see cref="UiTween.MotionEnabled"/> is false, which is what lets a capture that
    /// runs entirely inside one editor tick still photograph a settled screen rather than
    /// the first frame of half a dozen animations.
    ///
    /// Driven by a request file so a review can be scripted end to end. The request lives in
    /// Temp/ rather than under Assets/, because a file dropped in Assets/ starts an import,
    /// which reloads the domain and cancels the capture that was asked for.
    /// </summary>
    [InitializeOnLoad]
    public static class UiOverlayCapture
    {
        private static readonly string RequestPath =
            Path.Combine(Directory.GetCurrentDirectory(), "Temp", "pokelab_ui_capture.json");
        private static readonly string ResultPath =
            Path.Combine(Directory.GetCurrentDirectory(), "Temp", "pokelab_ui_capture.result.json");

        private const string RefreshedKey = "PokeLab.UiCapture.RefreshedFor";

        static UiOverlayCapture()
        {
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (!File.Exists(RequestPath)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            // Unity only imports and compiles when its window regains focus. A request served
            // by stale code produces a plausible screenshot of the version being replaced,
            // which is worse than no screenshot. Ask the pipeline to catch up once per
            // request and let the reload bring us back here with current code.
            var stamp = File.GetLastWriteTimeUtc(RequestPath).Ticks.ToString(CultureInfo.InvariantCulture);
            if (SessionState.GetString(RefreshedKey, string.Empty) != stamp)
            {
                SessionState.SetString(RefreshedKey, stamp);
                AssetDatabase.Refresh(ImportAssetOptions.Default);
                return;
            }

            string json;
            try
            {
                json = File.ReadAllText(RequestPath);
            }
            catch (IOException)
            {
                return; // Still being written. Try again next tick.
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
                Debug.LogError($"[UiCapture] {e}");
            }

            File.WriteAllText(ResultPath, JsonUtility.ToJson(result, true));
        }

        [MenuItem("Tools/Poké Lab/UI/Capture Dialogue Overlay")]
        private static void CaptureDefault()
        {
            var result = new Result();
            Run(DefaultRequest(), result);
            Debug.Log($"[UiCapture] wrote {result.frames.Count} frame(s) to {result.directory}");
        }

        private static Request DefaultRequest() => new Request
        {
            outputDirectory = "Captures/ui",
            width = 1920,
            height = 1080,
            shots = new[]
            {
                new Shot
                {
                    name = "dialogue",
                    speaker = "연구원 오크",
                    subtitle = "포켓몬 연구소",
                    body = "이 근처 풀숲에는 야생 포켓몬이 자주 나타나. 몬스터볼 없이 들어가면 위험하니까 조심하렴.",
                    auto = true,
                    menu = true,
                },
            },
        };

        private static void Run(Request request, Result result)
        {
            if (request == null) throw new ArgumentException("empty ui capture request");
            if (request.shots == null || request.shots.Length == 0)
                throw new ArgumentException("ui capture request carried no shots");

            var directory = Path.Combine(Directory.GetCurrentDirectory(),
                string.IsNullOrEmpty(request.outputDirectory) ? "Captures/ui" : request.outputDirectory);
            Directory.CreateDirectory(directory);
            result.directory = directory;

            var width = request.width > 0 ? request.width : 1920;
            var height = request.height > 0 ? request.height : 1080;

            EnsureTmpSettings();

            var motionWas = UiTween.MotionEnabled;
            UiTween.MotionEnabled = false;

            var backdrop = LoadBackdrop(request.background);

            try
            {
                for (var i = 0; i < request.shots.Length; i++)
                {
                    var file = Capture(request.shots[i], i, backdrop, directory, width, height);
                    result.frames.Add(file);
                }
            }
            finally
            {
                UiTween.MotionEnabled = motionWas;
                if (backdrop != null) UnityEngine.Object.DestroyImmediate(backdrop);
            }
        }

        /// <summary>
        /// Stands a TMP_Settings object up in memory when the project does not have one.
        ///
        /// This project has never imported TMP Essential Resources — they are still sitting
        /// as a .unitypackage inside com.unity.ugui — so <c>Resources.Load("TMP Settings")</c>
        /// returns null and <c>TMP_Text.SetText</c> throws a NullReferenceException on the
        /// first character of the first label. That is a real project-level gap and importing
        /// the package is the real fix, but it is a decision about what goes into the repo,
        /// not something a screenshot tool gets to make on the author's behalf.
        ///
        /// So the stub exists only inside this capture: a bare settings object with the two
        /// fields TMP dereferences unconditionally filled in. It is never serialized, never
        /// enters Resources, and disappears with the domain. If the real asset is ever
        /// imported this does nothing at all.
        /// </summary>
        private static void EnsureTmpSettings()
        {
            var instanceField = typeof(TMPro.TMP_Settings).GetField("s_Instance",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (instanceField == null) return;
            if (instanceField.GetValue(null) != null) return;
            if (Resources.Load<TMPro.TMP_Settings>("TMP Settings") != null) return;

            var stub = ScriptableObject.CreateInstance<TMPro.TMP_Settings>();
            stub.name = "~UiCaptureTmpSettingsStub";
            stub.hideFlags = HideFlags.HideAndDontSave;

            // Line breaking rules are loaded from two TextAssets that this stub does not
            // have; pre-filling the table stops TMP from trying, which it would do on the
            // first Korean line because CJK wrapping is exactly what the table is for.
            Set(stub, "m_linebreakingRules", new TMPro.TMP_Settings.LineBreakingTable
            {
                leadingCharacters = new HashSet<uint>(),
                followingCharacters = new HashSet<uint>(),
            });
            Set(stub, "m_defaultFontAsset", UiType.EnsureFont());

            instanceField.SetValue(null, stub);
            Debug.LogWarning("[UiCapture] No 'TMP Settings' asset in this project — TextMesh Pro "
                             + "Essential Resources have never been imported. Using an in-memory "
                             + "stub for this capture only; the game itself will still throw on "
                             + "the first TMP label until the package is imported.");
        }

        private static void Set(object target, string field, object value)
        {
            var info = target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            info?.SetValue(target, value);
        }

        /// <summary>
        /// Loads a PNG straight off disk rather than through the asset database. The frames
        /// worth compositing against are the level captures, which live outside Assets/ and
        /// would have to be imported — and importing one just to look at it would leave a
        /// stray asset behind after every review.
        /// </summary>
        private static Texture2D LoadBackdrop(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var full = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
            if (!File.Exists(full))
            {
                Debug.LogWarning($"[UiCapture] backdrop not found: {full}");
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            if (texture.LoadImage(File.ReadAllBytes(full))) return texture;

            UnityEngine.Object.DestroyImmediate(texture);
            Debug.LogWarning($"[UiCapture] backdrop is not a readable image: {full}");
            return null;
        }

        private static string Capture(Shot shot, int index, Texture2D backdrop, string directory,
            int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
            };
            var readback = new Texture2D(width, height, TextureFormat.RGB24, false);

            var cameraGo = new GameObject("~UiCaptureCamera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.cullingMask = ~0;
            // The canvas reads its pixel size off the camera, so the target has to be bound
            // before anything lays out — otherwise the whole screen is built for the Game
            // view's resolution and then photographed at this one.
            camera.targetTexture = rt;

            var canvasGo = new GameObject("~UiCaptureCanvas", typeof(RectTransform))
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(canvas, 0);
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 10f;

            try
            {
                if (backdrop != null)
                {
                    var raw = UiBuilder.Rect("Backdrop", canvasGo.transform).gameObject.AddComponent<RawImage>();
                    raw.texture = backdrop;
                    raw.raycastTarget = false;
                    UiBuilder.Stretch(raw.rectTransform);
                }

                var viewGo = new GameObject("DialogueView", typeof(RectTransform));
                viewGo.transform.SetParent(canvasGo.transform, false);
                var view = viewGo.AddComponent<DialogueView>();
                view.BuildRuntime();

                view.AutoAdvance = shot.auto;

                if (shot.choices != null && shot.choices.Length > 0)
                    view.ShowChoices(shot.speaker, shot.body, shot.choices, _ => { }, null, shot.subtitle);
                else
                    view.Show(shot.speaker, shot.body, null, () => { }, shot.subtitle);

                // Two passes: the first resolves content-size fitters, the second lets the
                // layout groups above them react to the sizes the fitters just settled on.
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)canvasGo.transform);
                Canvas.ForceUpdateCanvases();

                RenderTo(camera, rt);

                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply(false);
                RenderTexture.active = previous;

                var name = string.IsNullOrEmpty(shot.name)
                    ? index.ToString("D3", CultureInfo.InvariantCulture)
                    : $"{index:D3}_{Sanitise(shot.name)}";
                var file = Path.Combine(directory, name + ".png");
                File.WriteAllBytes(file, readback.EncodeToPNG());
                return file;
            }
            finally
            {
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(canvasGo);
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(readback);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        /// <summary>
        /// Renders one frame into the target texture, preferring the pipeline's own request
        /// path. <c>Camera.Render</c> is the built-in entry point and URP honours it only
        /// partially, so it is the fallback rather than the default.
        /// </summary>
        private static void RenderTo(Camera camera, RenderTexture target)
        {
            try
            {
                camera.SubmitRenderRequest(
                    new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target });
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UiCapture] Render request rejected ({e.GetType().Name}); " +
                                 "falling back to Camera.Render.");
            }

            camera.targetTexture = target;
            camera.Render();
        }

        private static string Sanitise(string value)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value.Replace(' ', '_');
        }

        [Serializable]
        private sealed class Request
        {
            public string outputDirectory;
            public int width;
            public int height;
            /// <summary>Path to a PNG composited behind the overlay. Relative to the project root.</summary>
            public string background;
            public Shot[] shots;
        }

        [Serializable]
        private sealed class Shot
        {
            public string name;
            public string speaker;
            public string subtitle;
            public string body;
            public string[] choices;
            public bool auto;
            public bool menu;
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
