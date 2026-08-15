using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>
    /// Signed-distance-field primitives in pixel space, plus the coverage helper that
    /// turns a distance into an antialiased alpha.
    ///
    /// Everything the UI draws is generated from these at runtime. The alternative was
    /// shipping PNGs, but committed binaries cannot be reviewed in a diff and this project
    /// has no editor session to author them in — an SDF composition is readable, tweakable,
    /// and resolution independent.
    /// </summary>
    public static class UiShapes
    {
        /// <summary>
        /// Converts a signed distance (negative inside) to 0-1 coverage with roughly one
        /// pixel of antialiasing. This is the only place smoothing happens, so every shape
        /// in the game shares an identical edge softness.
        /// </summary>
        public static float Coverage(float distance, float softness = 1f)
        {
            return Mathf.Clamp01(0.5f - distance / Mathf.Max(0.0001f, softness));
        }

        /// <summary>Distance to a circle of <paramref name="radius"/> centred on <paramref name="centre"/>.</summary>
        public static float Circle(Vector2 p, Vector2 centre, float radius)
        {
            return (p - centre).magnitude - radius;
        }

        /// <summary>Distance to an axis-aligned box with rounded corners. <paramref name="half"/> is the half-extent.</summary>
        public static float RoundedBox(Vector2 p, Vector2 centre, Vector2 half, float radius)
        {
            radius = Mathf.Min(radius, Mathf.Min(half.x, half.y));
            var d = new Vector2(
                Mathf.Abs(p.x - centre.x) - (half.x - radius),
                Mathf.Abs(p.y - centre.y) - (half.y - radius));
            var outside = new Vector2(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f)).magnitude;
            var inside = Mathf.Min(Mathf.Max(d.x, d.y), 0f);
            return outside + inside - radius;
        }

        /// <summary>Distance to a thick line segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
        public static float Segment(Vector2 p, Vector2 a, Vector2 b, float thickness)
        {
            var pa = p - a;
            var ba = b - a;
            var h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Mathf.Max(0.0001f, Vector2.Dot(ba, ba)));
            return (pa - ba * h).magnitude - thickness * 0.5f;
        }

        /// <summary>Distance to a triangle outline-or-fill defined by three points.</summary>
        public static float Triangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var e0 = b - a; var v0 = p - a;
            var e1 = c - b; var v1 = p - b;
            var e2 = a - c; var v2 = p - c;

            var pq0 = v0 - e0 * Mathf.Clamp01(Vector2.Dot(v0, e0) / Vector2.Dot(e0, e0));
            var pq1 = v1 - e1 * Mathf.Clamp01(Vector2.Dot(v1, e1) / Vector2.Dot(e1, e1));
            var pq2 = v2 - e2 * Mathf.Clamp01(Vector2.Dot(v2, e2) / Vector2.Dot(e2, e2));

            var s = Mathf.Sign(e0.x * e2.y - e0.y * e2.x);
            var d = Vector2.Min(Vector2.Min(
                    new Vector2(Vector2.Dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
                    new Vector2(Vector2.Dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))),
                new Vector2(Vector2.Dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));

            return -Mathf.Sqrt(d.x) * Mathf.Sign(d.y);
        }

        /// <summary>Distance to a regular n-gon, rotated by <paramref name="rotation"/> radians.</summary>
        public static float Polygon(Vector2 p, Vector2 centre, float radius, int sides, float rotation = 0f)
        {
            var local = p - centre;
            var angle = Mathf.Atan2(local.y, local.x) - rotation;
            var segment = Mathf.PI * 2f / sides;
            // Fold the plane into one wedge, then it is a single half-plane distance.
            var folded = Mathf.Repeat(angle + segment * 0.5f, segment) - segment * 0.5f;
            var apothem = radius * Mathf.Cos(segment * 0.5f);
            return local.magnitude * Mathf.Cos(folded) - apothem;
        }

        /// <summary>Distance to an annulus (ring) of the given radius and thickness.</summary>
        public static float Ring(Vector2 p, Vector2 centre, float radius, float thickness)
        {
            return Mathf.Abs((p - centre).magnitude - radius) - thickness * 0.5f;
        }

        /// <summary>Union of two shapes (nearest surface wins).</summary>
        public static float Union(float a, float b) => Mathf.Min(a, b);

        /// <summary>Intersection of two shapes.</summary>
        public static float Intersect(float a, float b) => Mathf.Max(a, b);

        /// <summary>Subtracts <paramref name="b"/> from <paramref name="a"/>.</summary>
        public static float Subtract(float a, float b) => Mathf.Max(a, -b);

        /// <summary>Smooth union — the two shapes blend into one blob over <paramref name="k"/> pixels.</summary>
        public static float SmoothUnion(float a, float b, float k)
        {
            var h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / Mathf.Max(0.0001f, k));
            return Mathf.Lerp(b, a, h) - k * h * (1f - h);
        }

        /// <summary>Rotates <paramref name="p"/> around <paramref name="centre"/> by radians.</summary>
        public static Vector2 Rotate(Vector2 p, Vector2 centre, float radians)
        {
            var d = p - centre;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);
            return centre + new Vector2(d.x * cos - d.y * sin, d.x * sin + d.y * cos);
        }

        /// <summary>Mirrors the x axis about <paramref name="centre"/> so a shape only has to be authored once.</summary>
        public static Vector2 MirrorX(Vector2 p, float centreX)
        {
            return new Vector2(centreX + Mathf.Abs(p.x - centreX), p.y);
        }
    }
}
