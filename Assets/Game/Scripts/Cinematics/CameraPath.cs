using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>Shared collision and easing rules for authored camera moves.</summary>
    public static class CameraPath
    {
        public static int GeometryMask => LayerMask.GetMask("Ground", "Environment");

        public static Vector3 ClearPosition(Vector3 position, Vector3 aim, float radius = 0.18f)
        {
            var delta = position - aim;
            var distance = delta.magnitude;
            if (distance > radius && Physics.SphereCast(aim, radius, delta / distance,
                    out var hit, distance, GeometryMask, QueryTriggerInteraction.Ignore))
                position = aim + delta / distance * Mathf.Max(0.35f, hit.distance - 0.05f);
            // Keep the lens above the ground, including dolly endpoints and low case shots.
            if (Physics.Raycast(position + Vector3.up * 0.6f, Vector3.down, out var ground,
                    1f, LayerMask.GetMask("Ground"), QueryTriggerInteraction.Ignore))
                position.y = Mathf.Max(position.y, ground.point.y + radius);
            return position;
        }

        public static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }
    }
}
