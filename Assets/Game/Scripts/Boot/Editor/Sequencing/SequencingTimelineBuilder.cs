using System.Collections.Generic;
using System.IO;
using PokeLab.Cinematics.Sequencing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace PokeLab.Sequencing.Editor
{
    /// <summary>
    /// Bakes the camera timelines authored in shots.json into TimelineAsset .playable files,
    /// so the opening act's fixed choreography — the dome reveal push-in, the square
    /// send-off turn, the lake-bank arrival — can be SCRUBBED in the Timeline window instead
    /// of judged one Play-mode run at a time.
    ///
    /// The project convention, same as the level: the asset is generated, the JSON is the
    /// source of truth, and this menu is rerun whenever the JSON changes. Regeneration is
    /// idempotent — existing assets are rebuilt in place so their GUIDs (and any scene
    /// bindings pointed at them) survive.
    ///
    /// The bake and the runtime rig share one pose function (<see cref="ShotMath"/>), which
    /// is the whole guarantee that what the Timeline window shows is what the build plays.
    /// Curves are dense-sampled (10 Hz plus exact segment boundaries) position + quaternion
    /// keys: at that density the linear gaps between quaternion keys are fractions of a
    /// degree, and dense keys survive every interpolation mode Timeline has.
    ///
    /// This lives in its own editor assembly because PokeLab.Boot.Editor does not reference
    /// Unity.Timeline and its asmdef belongs to another owner.
    /// </summary>
    public static class SequencingTimelineBuilder
    {
        private const string ShotsJsonPath = "Assets/Game/Data/Story/Resources/shots.json";
        private const string OutputFolder = "Assets/Game/Data/Story/Resources/Timelines";
        private const string TrackName = "ShotCamera";
        private const string PreviewObjectName = "~SequencingPreview";
        private const float SampleHz = 10f;

        [MenuItem("Tools/Poké Lab/Rebuild/Sequencing Timelines (from shots.json)", priority = 22)]
        public static void BuildTimelines()
        {
            var book = LoadBook();
            if (book == null) return;
            if (book.Timelines == null || book.Timelines.Count == 0)
            {
                Debug.LogWarning("[Sequencing] shots.json has no Timelines; nothing to build.");
                return;
            }

            Directory.CreateDirectory(OutputFolder);

            var built = new List<string>();
            foreach (var definition in book.Timelines)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Name)) continue;
                if (definition.Segments == null || definition.Segments.Count == 0)
                {
                    Debug.LogWarning($"[Sequencing] Timeline '{definition.Name}' has no segments " +
                                     "and was skipped.");
                    continue;
                }
                BuildOne(definition);
                built.Add(definition.Name);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[Sequencing] Built {built.Count} timeline(s) into {OutputFolder}: " +
                      string.Join(", ", built) + ". Scrub them via the " + PreviewObjectName +
                      " object (Tools/Poké Lab/Rebuild/Sequencing Preview Rig).");
        }

        /// <summary>
        /// Creates or refreshes a preview object in the open scene: an Animator, a disabled
        /// Camera and a PlayableDirector with every generated timeline's track already bound.
        /// Select it, open the Timeline window, pick an asset on the director, and scrub —
        /// the camera frustum in the Scene view is the shot. Tagged EditorOnly so a build
        /// strips it.
        /// </summary>
        [MenuItem("Tools/Poké Lab/Rebuild/Sequencing Preview Rig (into open scene)", priority = 23)]
        public static void BuildPreviewRig()
        {
            var book = LoadBook();

            var go = GameObject.Find(PreviewObjectName);
            if (go == null) go = new GameObject(PreviewObjectName);
            go.tag = "EditorOnly";
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;

            var animator = go.GetComponent<Animator>();
            if (animator == null) animator = go.AddComponent<Animator>();

            var camera = go.GetComponent<Camera>();
            if (camera == null) camera = go.AddComponent<Camera>();
            camera.enabled = false; // frustum gizmo only; enable by hand to see the shot in Game view
            camera.nearClipPlane = 0.1f;

            var director = go.GetComponent<PlayableDirector>();
            if (director == null) director = go.AddComponent<PlayableDirector>();
            director.playOnAwake = false;

            var bound = 0;
            if (book?.Timelines != null)
            {
                foreach (var definition in book.Timelines)
                {
                    if (definition == null || string.IsNullOrEmpty(definition.Name)) continue;
                    var asset = AssetDatabase.LoadAssetAtPath<TimelineAsset>(AssetPath(definition.Name));
                    if (asset == null) continue;

                    foreach (var output in asset.outputs)
                    {
                        if (output.outputTargetType == typeof(Animator) && output.sourceObject != null)
                            director.SetGenericBinding(output.sourceObject, animator);
                    }
                    if (director.playableAsset == null)
                    {
                        director.playableAsset = asset;
                        camera.fieldOfView = definition.Fov > 1f ? definition.Fov : 40f;
                    }
                    bound++;
                }
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            Debug.Log($"[Sequencing] {PreviewObjectName} ready with {bound} timeline(s) bound. " +
                      "Open the Timeline window with this object selected and scrub; swap the " +
                      "director's Playable to preview another. Run the Sequencing Timelines " +
                      "menu first if bindings are missing.");
        }

        // --- The bake ------------------------------------------------------------------------

        private static EpisodeShotBook LoadBook()
        {
            if (!File.Exists(ShotsJsonPath))
            {
                Debug.LogError($"[Sequencing] No {ShotsJsonPath}; nothing to build from.");
                return null;
            }
            var book = JsonUtility.FromJson<EpisodeShotBook>(File.ReadAllText(ShotsJsonPath));
            if (book == null)
                Debug.LogError("[Sequencing] shots.json would not parse as an EpisodeShotBook.");
            return book;
        }

        private static string AssetPath(string timelineName) =>
            $"{OutputFolder}/{timelineName}.playable";

        private static void BuildOne(EpisodeTimelineDef definition)
        {
            var path = AssetPath(definition.Name);
            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
            var fresh = timeline == null;
            if (fresh)
            {
                timeline = ScriptableObject.CreateInstance<TimelineAsset>();
                timeline.name = definition.Name;
                AssetDatabase.CreateAsset(timeline, path);
            }
            else
            {
                // Rebuild in place: drop every track, then every orphaned sub-asset, so the
                // main asset (and its GUID) is the only survivor.
                var stale = new List<TrackAsset>(timeline.GetRootTracks());
                foreach (var track in stale) timeline.DeleteTrack(track);
                foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (sub is AnimationClip orphan)
                        Object.DestroyImmediate(orphan, true);
                }
            }

            var clip = BakeClip(definition);
            clip.name = definition.Name + "_cam";
            AssetDatabase.AddObjectToAsset(clip, timeline);

            var animationTrack = timeline.CreateTrack<AnimationTrack>(null, TrackName);
            var timelineClip = animationTrack.CreateClip(clip);
            timelineClip.start = 0.0;
            timelineClip.displayName = "Camera";

            EditorUtility.SetDirty(timeline);
            if (!fresh) AssetDatabase.ImportAsset(path);
        }

        /// <summary>
        /// Samples every segment through the shared pose maths into transform curves on the
        /// bound Animator's own GameObject (path ""), which is how the runtime rig's timeline
        /// camera — Animator and CinemachineCamera on one child at an identity root — reads
        /// world poses out of local curves.
        /// </summary>
        private static AnimationClip BakeClip(EpisodeTimelineDef definition)
        {
            var px = new List<Keyframe>(); var py = new List<Keyframe>(); var pz = new List<Keyframe>();
            var rx = new List<Keyframe>(); var ry = new List<Keyframe>();
            var rz = new List<Keyframe>(); var rw = new List<Keyframe>();

            var lastTime = float.NegativeInfinity;
            var lastRotation = Quaternion.identity;
            var haveLast = false;

            foreach (var segment in definition.Segments)
            {
                if (segment == null) continue;
                var start = Mathf.Max(0f, segment.StartSeconds);
                var end = Mathf.Max(start, segment.EndSeconds);
                var span = end - start;

                var steps = span <= 0.0001f ? 1 : Mathf.Max(1, Mathf.CeilToInt(span * SampleHz));
                for (var i = 0; i <= steps; i++)
                {
                    var t = span <= 0.0001f ? start : start + span * i / steps;
                    if (t <= lastTime + 0.0005f && haveLast) continue; // shared boundary key

                    var t01 = span <= 0.0001f ? 1f : (t - start) / span;
                    ShotMath.SegmentPose(segment, t01, out var position, out var rotation);

                    // Quaternion sign continuity: q and -q are one rotation, but a curve that
                    // lerps between them swings the camera the long way round.
                    if (haveLast && Quaternion.Dot(lastRotation, rotation) < 0f)
                        rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);

                    px.Add(new Keyframe(t, position.x));
                    py.Add(new Keyframe(t, position.y));
                    pz.Add(new Keyframe(t, position.z));
                    rx.Add(new Keyframe(t, rotation.x));
                    ry.Add(new Keyframe(t, rotation.y));
                    rz.Add(new Keyframe(t, rotation.z));
                    rw.Add(new Keyframe(t, rotation.w));

                    lastTime = t;
                    lastRotation = rotation;
                    haveLast = true;
                }
            }

            var clip = new AnimationClip { frameRate = 60f };
            clip.SetCurve("", typeof(Transform), "localPosition.x", Smooth(px));
            clip.SetCurve("", typeof(Transform), "localPosition.y", Smooth(py));
            clip.SetCurve("", typeof(Transform), "localPosition.z", Smooth(pz));
            clip.SetCurve("", typeof(Transform), "localRotation.x", Smooth(rx));
            clip.SetCurve("", typeof(Transform), "localRotation.y", Smooth(ry));
            clip.SetCurve("", typeof(Transform), "localRotation.z", Smooth(rz));
            clip.SetCurve("", typeof(Transform), "localRotation.w", Smooth(rw));
            clip.EnsureQuaternionContinuity();
            return clip;
        }

        private static AnimationCurve Smooth(List<Keyframe> keys)
        {
            var curve = new AnimationCurve(keys.ToArray());
            for (var i = 0; i < curve.length; i++)
                curve.SmoothTangents(i, 0f);
            return curve;
        }
    }
}
