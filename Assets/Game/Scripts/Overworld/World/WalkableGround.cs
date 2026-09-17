using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Overworld
{
    /// <summary>Standing surfaces are explicitly assigned to Ground; water and props are not floors.</summary>
    public static class WalkableGround
    {
        public static bool TrySample(Vector3 point, out Vector3 ground,
            float rise = 2f, float drop = 4f)
        {
            ground = point;
            int mask = LayerMask.GetMask("Ground");
            if (mask == 0 || !Physics.Raycast(point + Vector3.up * rise, Vector3.down,
                    out var hit, rise + drop, mask, QueryTriggerInteraction.Ignore)) return false;
            if (hit.normal.y < Mathf.Cos(45f * Mathf.Deg2Rad)) return false;
            ground = hit.point;
            // Include trigger water meshes, even before WaterBody.Awake has registered them.
            int water = LayerMask.GetMask("Water");
            if (water != 0 && Physics.Raycast(ground + Vector3.up * 20f, Vector3.down,
                    out var surface, 20.05f, water, QueryTriggerInteraction.Collide)
                && surface.point.y > ground.y + 0.02f) return false;
            return true;
        }

        public static bool TryFind(Vector3 desired, float radius, out Vector3 ground)
        {
            if (TrySample(desired, out ground)) return true;
            // Deterministic outward search: never relocate across the entire map.
            for (float r = 0.5f; r <= radius; r += 0.5f)
                for (int i = 0; i < 16; i++)
                {
                    float angle = i * Mathf.PI / 8f;
                    var candidate = desired + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * r;
                    if (TrySample(candidate, out ground)) return true;
                }
            ground = desired;
            return false;
        }

        public static bool TryNavMesh(Vector3 desired, float radius, int areaMask, out NavMeshHit hit)
        {
            if (!NavMesh.SamplePosition(desired, out hit, radius, areaMask)) return false;
            return TrySample(hit.position, out var ground, 0.5f, 1f)
                && Mathf.Abs(ground.y - hit.position.y) <= 0.35f;
        }

        public static bool TryStep(Vector3 from, Vector3 candidate, out Vector3 ground)
        {
            if (!TrySample(candidate, out ground, 0.5f, 1f)) return false;
            float travel = new Vector2(candidate.x - from.x, candidate.z - from.z).magnitude;
            return Mathf.Abs(ground.y - from.y) <= Mathf.Max(0.15f, travel);
        }
    }
}
