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
    /// </summary>
    public static class CameraFlowWatch
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Hook()
        {
            CinemachineCore.CameraActivatedEvent.AddListener(OnActivated);
        }

        private static void OnActivated(ICinemachineCamera.ActivationEventParams e)
        {
            var incoming = e.IncomingCamera;
            if (incoming == null) return;
            var pos = incoming.State.GetFinalPosition();
            var from = e.OutgoingCamera != null ? e.OutgoingCamera.Name : "(none)";
            Debug.Log($"[CamFlow] {from} -> {incoming.Name} at ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})" +
                      (e.IsCut ? " cut" : " blend") + $" t={Time.time:F1}");
        }
    }
}
