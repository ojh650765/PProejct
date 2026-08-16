using System;
using System.IO;
using PokeLab.Overworld;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// TEMPORARY verification harness for the episode runner. Not shipped, not part of the game.
    ///
    /// The Overworld scene has no DialogueRunner, no cinematics layer and none of the markers
    /// cast.json names, so the beats that need them can only ever be observed degrading. These
    /// menu items add exactly those missing pieces to the *open* scene before Play, so the real
    /// EpisodeRunner in the real scene can be watched doing the real thing. Nothing is saved:
    /// PlayModeProbe reopens the scene from disk on every run.
    /// </summary>
    public static class ProbeStoryHarness
    {
        [MenuItem("PokeLab/Probe Harness/Stage Story Dependencies")]
        public static void StageStoryDependencies()
        {
            if (UnityEngine.Object.FindAnyObjectByType<DialogueRunner>() == null)
                new GameObject("~ProbeDialogue").AddComponent<DialogueRunner>();

            var directorType = Type.GetType("PokeLab.Cinematics.TransitionDirector, PokeLab.Cinematics");
            if (directorType != null && UnityEngine.Object.FindAnyObjectByType(directorType) == null)
                new GameObject("~ProbeCinematics").AddComponent(directorType);
            else if (directorType == null)
                Debug.LogWarning("[ProbeHarness] TransitionDirector type not found.");

            StageMarkers();
        }

        [MenuItem("PokeLab/Probe Harness/Play Departure Episode")]
        public static void PlayDepartureEpisode()
        {
            var runner = UnityEngine.Object.FindAnyObjectByType<EpisodeRunner>();
            if (runner == null) { Debug.LogWarning("[ProbeHarness] No EpisodeRunner."); return; }

            var so = new SerializedObject(runner);
            so.FindProperty("_openingEpisodeId").stringValue = "opening_departure";
            so.FindProperty("_dialogueStallSeconds").floatValue = 2f;
            so.FindProperty("_starterChoiceTimeoutSeconds").floatValue = 5f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("PokeLab/Probe Harness/Hurry Dialogue")]
        public static void HurryDialogue()
        {
            var runner = UnityEngine.Object.FindAnyObjectByType<EpisodeRunner>();
            if (runner == null) return;
            var so = new SerializedObject(runner);
            so.FindProperty("_dialogueStallSeconds").floatValue = 2f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void StageMarkers()
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "Assets/Game/Data/Story/cast.json");
            if (!File.Exists(path)) { Debug.LogWarning("[ProbeHarness] No cast.json."); return; }

            var cast = JsonUtility.FromJson<Cast>(File.ReadAllText(path));
            var root = new GameObject("~ProbeMarkers");
            var made = 0;
            foreach (var marker in cast?.markers ?? Array.Empty<Marker>())
            {
                if (marker == null || string.IsNullOrEmpty(marker.name)) continue;
                if (GameObject.Find(marker.name) != null) continue;
                var go = new GameObject(marker.name);
                go.transform.SetParent(root.transform, true);
                if (marker.position != null && marker.position.Length >= 3)
                    go.transform.position = new Vector3(marker.position[0], marker.position[1], marker.position[2]);
                made++;
            }
            Debug.Log($"[ProbeHarness] Staged {made} markers from cast.json.");
        }

        [Serializable] private sealed class Marker { public string name; public float[] position; }
        [Serializable] private sealed class Cast { public Marker[] markers; }
    }

    /// <summary>
    /// Logs every line the dialogue runner presents. Verification only — nothing in the scene
    /// draws a line, so this is the only way to read what was actually said, and whether
    /// {PLAYER} was substituted before it was said. A MonoBehaviour would not survive the trip
    /// into Play mode from an editor assembly; this hook does, exactly as PlayModeProbe's does.
    /// </summary>
    public static class ProbeDialogueEcho
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Hook()
        {
            var runner = UnityEngine.Object.FindAnyObjectByType<DialogueRunner>();
            if (runner == null) return;
            runner.LinePresented += line => Debug.Log($"[ProbeEcho] {line.SpeakerName}: {line.Text}");
        }
    }
}
