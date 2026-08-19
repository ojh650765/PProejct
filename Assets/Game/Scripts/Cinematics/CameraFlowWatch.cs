using Unity.Cinemachine;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// One console line per live-camera change: which camera the brain handed the frame
    /// to, from where, at what position. The camera arbitration bugs this project keeps
    /// hitting are invisible in a screenshot older than the transition and unreachable
    /// by a debugger in a deployed browser build — the console is the one channel that
    /// survives both, and the deploy gate's film captures it whole.
    ///
    /// Polls the brain instead of subscribing to CinemachineCore.CameraActivatedEvent:
    /// the event-based first version shipped, verifiably present in the build, and never
    /// printed a line — whatever the activation event's firing semantics are on this
    /// version, a per-frame reference compare has none.
    /// </summary>
    [DefaultExecutionOrder(2000)]
    public sealed class CameraFlowWatch : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var host = new GameObject("~CameraFlowWatch") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(host);
            host.AddComponent<CameraFlowWatch>();
            Debug.Log("[CamFlow] watching");
        }

        private ICinemachineCamera _last;

        private void LateUpdate()
        {
            var brain = CinemachineBrain.GetActiveBrain(0);
            if (brain == null) return;
            var live = brain.ActiveVirtualCamera;
            if (ReferenceEquals(live, _last)) return;

            var pos = live != null ? live.State.GetFinalPosition() : Vector3.zero;
            var from = _last != null ? _last.Name : "(none)";
            var to = live != null ? live.Name : "(none)";
            Debug.Log($"[CamFlow] {from} -> {to} at ({pos.x:F1}, {pos.y:F1}, {pos.z:F1}) t={Time.time:F1}");
            _last = live;
        }
    }
}
