using System.Collections;
using PokeLab.Core;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Cinematics.Editor
{
    /// <summary>
    /// TEMPORARY. Fires one wild encounter a fixed number of seconds into Play mode so a probe
    /// can photograph a battle without having to walk into grass. Delete after the review.
    /// </summary>
    public static class TempEncounterHarness
    {
        [MenuItem("Tools/Poké Lab/Temp/Arm Encounter Harness")]
        public static void Arm() => SessionState.SetBool("PokeLab.TempEncounterHarness", true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (!SessionState.GetBool("PokeLab.TempEncounterHarness", false)) return;
            var host = new GameObject("~TempEncounterHarness") { hideFlags = HideFlags.HideAndDontSave };
            host.AddComponent<Runner>();
        }

        private sealed class Runner : MonoBehaviour
        {
            private IEnumerator Start()
            {
                yield return new WaitForSecondsRealtime(
                    SessionState.GetFloat("PokeLab.TempEncounterHarness.Delay", 6f));

                if (!ServiceHub.TryGet<IGameFlow>(out var flow) || flow == null)
                {
                    Debug.LogError("[TempHarness] No IGameFlow registered; no encounter was requested.");
                    yield break;
                }

                var player = GameObject.FindWithTag("Player");
                var request = new EncounterRequest
                {
                    Kind = BattleKind.Wild,
                    WildSpeciesId = 21,
                    WildLevel = 7,
                    Seed = 20260816,
                    WorldPosition = player != null ? player.transform.position : Vector3.zero,
                    PlayerRotation = player != null ? player.transform.rotation : Quaternion.identity,
                };

                Debug.Log("[TempHarness] Requesting a wild encounter.");
                flow.RequestEncounter(request, r =>
                    Debug.Log($"[TempHarness] Encounter resolved: {r?.Outcome}"));
            }
        }
    }
}
