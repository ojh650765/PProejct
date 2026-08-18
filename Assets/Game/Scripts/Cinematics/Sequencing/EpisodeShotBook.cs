using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Cinematics.Sequencing
{
    /// <summary>
    /// The authored shot data behind the episode runner's CameraShot and PlayTimeline beats:
    /// the JsonUtility shapes for <c>Assets/Game/Data/Story/Resources/shots.json</c>, plus the
    /// pose maths both consumers share — the runtime rig composes live cameras from it, and
    /// the editor timeline builder bakes the same numbers into scrubbable AnimationClips.
    ///
    /// The maths lives beside the data on purpose. A shot that frames differently in the
    /// Timeline window than it does in a build is worse than no preview at all, and the only
    /// way two consumers agree forever is to compute the pose in exactly one place.
    ///
    /// JsonUtility discipline, same as episodes.json: <c>Mode</c>/<c>Ease</c> are INTEGER
    /// fields because JsonUtility deserialises enums and unions from numbers only;
    /// <c>ModeName</c>/<c>EaseName</c>/<c>Note</c> sit beside them for humans and are ignored
    /// by the parser. Unknown fields are dropped silently, so any new field must be declared
    /// here before it is authored there.
    /// </summary>
    [Serializable]
    public sealed class EpisodeShotBook
    {
        public List<EpisodeShotDef> Shots = new List<EpisodeShotDef>();
        public List<EpisodeTimelineDef> Timelines = new List<EpisodeTimelineDef>();

        /// <summary>Name shots.json is looked up under once it is inside a Resources folder.</summary>
        public const string ResourceName = "shots";

        /// <summary>Resources folder the generated TimelineAssets are loaded from.</summary>
        public const string TimelineResourceFolder = "Timelines";

        public static EpisodeShotBook Load()
        {
            var text = Resources.Load<TextAsset>(ResourceName);
            if (text == null || string.IsNullOrEmpty(text.text)) return null;
            try
            {
                return JsonUtility.FromJson<EpisodeShotBook>(text.text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Shots] shots.json would not parse ({e.GetType().Name}: " +
                                 $"{e.Message}). Every CameraShot beat will degrade to the yaw swing.");
                return null;
            }
        }

        public EpisodeShotDef FindShot(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var shot in Shots)
                if (shot != null && shot.Name == name) return shot;
            return null;
        }

        public EpisodeTimelineDef FindTimeline(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var timeline in Timelines)
                if (timeline != null && timeline.Name == name) return timeline;
            return null;
        }
    }

    /// <summary>How a shot's camera pose is derived. Integer-backed for JsonUtility.</summary>
    public enum EpisodeShotMode
    {
        None = 0,
        /// <summary>Authored world position looking at an authored world point.</summary>
        Fixed = 1,
        /// <summary>
        /// Placed around a scene object resolved by name at shot time — the staged creature,
        /// the professor's bag, the player. The camera stands on the target's player-facing
        /// side, swung by an authored offset, so the composition survives the player standing
        /// anywhere the trigger radius allows.
        /// </summary>
        Orbit = 2,
    }

    [Serializable]
    public sealed class EpisodeShotDef
    {
        public string Name;
        /// <summary>See <see cref="EpisodeShotMode"/>. Integer because JsonUtility.</summary>
        public int Mode;
        public string ModeName;
        public string Note;

        // --- Fixed ---------------------------------------------------------------------------
        /// <summary>World camera position, [x, y, z], matching cast.json's array style.</summary>
        public float[] Position;
        /// <summary>World point the camera looks at, [x, y, z].</summary>
        public float[] LookAt;
        /// <summary>
        /// Optional dolly: the camera eases from <see cref="Position"/> to here over
        /// <see cref="DollySeconds"/>, holding its aim. Empty means a static shot.
        /// </summary>
        public float[] DollyTo;
        public float DollySeconds;

        // --- Orbit ---------------------------------------------------------------------------
        /// <summary>Scene object the orbit composes around, resolved by GameObject.Find.</summary>
        public string TargetName;
        /// <summary>Metres from the target, horizontally.</summary>
        public float Distance = 2.5f;
        /// <summary>Metres above the target's own position.</summary>
        public float Height = 1.4f;
        /// <summary>
        /// Degrees the camera is swung off the target-to-player bearing. Zero is a dead-on
        /// over-the-player shot; a small angle is the three-quarter view everything wants.
        /// With no player in the scene the value is read as an absolute world bearing instead.
        /// </summary>
        public float YawOffsetDegrees;
        /// <summary>Metres above the target's position the camera aims at.</summary>
        public float AimHeight = 0.3f;

        // --- Both ----------------------------------------------------------------------------
        public float Fov = 40f;
    }

    [Serializable]
    public sealed class EpisodeTimelineDef
    {
        public string Name;
        /// <summary>Lens for the whole timeline. Match the shots it blends with.</summary>
        public float Fov = 40f;
        public string Note;
        public List<TimelineSegmentDef> Segments = new List<TimelineSegmentDef>();

        public float DurationSeconds
        {
            get
            {
                var end = 0f;
                foreach (var segment in Segments)
                    if (segment != null && segment.EndSeconds > end) end = segment.EndSeconds;
                return end;
            }
        }
    }

    /// <summary>Easing of a timeline segment. Integer-backed for JsonUtility.</summary>
    public enum TimelineEase
    {
        /// <summary>Stay on the From pose for the whole segment — an authored hold.</summary>
        Hold = 0,
        /// <summary>Smoothstep between the poses. The default for camera moves.</summary>
        Smooth = 1,
        Linear = 2,
    }

    [Serializable]
    public sealed class TimelineSegmentDef
    {
        public float StartSeconds;
        public float EndSeconds;
        public float[] FromPosition;
        public float[] ToPosition;
        public float[] FromLookAt;
        public float[] ToLookAt;
        /// <summary>See <see cref="TimelineEase"/>. Integer because JsonUtility.</summary>
        public int Ease = 1;
        public string EaseName;
        public string Note;
    }

    /// <summary>The one place a shot's numbers become a camera pose.</summary>
    public static class ShotMath
    {
        public static Vector3 ToVector3(float[] xyz, Vector3 fallback)
        {
            if (xyz == null || xyz.Length < 3) return fallback;
            return new Vector3(xyz[0], xyz[1], xyz[2]);
        }

        public static bool HasVector3(float[] xyz) => xyz != null && xyz.Length >= 3;

        public static Quaternion AimFrom(Vector3 position, Vector3 lookAt)
        {
            var forward = lookAt - position;
            return forward.sqrMagnitude < 1e-6f ? Quaternion.identity
                                                : Quaternion.LookRotation(forward, Vector3.up);
        }

        /// <summary>
        /// The orbit pose: camera on the target's player-facing side, swung by the authored
        /// offset, raised and aimed by the authored heights. <paramref name="playerPosition"/>
        /// null means no player was found and the offset is read as a world bearing.
        /// </summary>
        public static void OrbitPose(EpisodeShotDef shot, Vector3 targetPosition,
            Vector3? playerPosition, out Vector3 position, out Vector3 aimPoint)
        {
            float baseBearing;
            if (playerPosition.HasValue)
            {
                var toPlayer = playerPosition.Value - targetPosition;
                toPlayer.y = 0f;
                baseBearing = toPlayer.sqrMagnitude < 1e-4f
                    ? 0f
                    : Mathf.Atan2(toPlayer.x, toPlayer.z) * Mathf.Rad2Deg;
            }
            else
            {
                baseBearing = 0f;
            }

            var bearing = (baseBearing + shot.YawOffsetDegrees) * Mathf.Deg2Rad;
            var flat = new Vector3(Mathf.Sin(bearing), 0f, Mathf.Cos(bearing));
            position = targetPosition + flat * Mathf.Max(0.5f, shot.Distance)
                       + Vector3.up * shot.Height;
            aimPoint = targetPosition + Vector3.up * shot.AimHeight;
        }

        /// <summary>Pose along a segment at eased parameter <paramref name="t01"/>.</summary>
        public static void SegmentPose(TimelineSegmentDef segment, float t01,
            out Vector3 position, out Quaternion rotation)
        {
            var s = (TimelineEase)segment.Ease switch
            {
                TimelineEase.Hold => 0f,
                TimelineEase.Linear => Mathf.Clamp01(t01),
                _ => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t01)),
            };

            var from = ToVector3(segment.FromPosition, Vector3.zero);
            var to = ToVector3(segment.ToPosition, from);
            var fromAim = ToVector3(segment.FromLookAt, from + Vector3.forward);
            var toAim = ToVector3(segment.ToLookAt, fromAim);

            position = Vector3.LerpUnclamped(from, to, s);
            var aim = Vector3.LerpUnclamped(fromAim, toAim, s);
            rotation = AimFrom(position, aim);
        }
    }
}
