using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Notices when the player walks into a shoreline wall, so the wall can say why.
    ///
    /// This has to live on the player rather than on the wall. A
    /// <see cref="CharacterController"/> does not raise collision events on what it hits —
    /// it reports its own hits, to itself, through <c>OnControllerColliderHit</c>. A
    /// <c>WaterEdge</c> waiting for <c>OnCollisionEnter</c> would wait forever and the
    /// player would stop at an invisible wall with no explanation, which is worse than
    /// letting them in.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class WaterEdgeResponder : MonoBehaviour
    {
        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (hit.collider == null) return;

            // Only when actually pushing into it. Brushing past a shoreline while walking
            // along the bank is not a request to be told about the lake.
            if (Vector3.Dot(hit.moveDirection, hit.normal) > -0.35f) return;

            var edge = hit.collider.GetComponent<WaterEdge>();
            if (edge != null) edge.Prompt();
        }
    }
}
