using UnityEngine;

namespace PokeLab.Core
{
    /// <summary>
    /// Hand-off point for touch-driven camera look, in screen pixels.
    ///
    /// The touch look pad lives in the UI assembly and the input reader in the overworld's,
    /// and neither may reference the other — the same wall the footstep bridge sits on. The
    /// pad adds drag deltas as they arrive; the reader consumes them once per frame inside
    /// its own look path, so touch look obeys the reader's input gate and sensitivity
    /// tuning exactly as every other look device does.
    ///
    /// Frame-stamped so an unconsumed delta evaporates: while the reader is gated
    /// (dialogue, battle, transitions) nothing drains the route, and pixels hoarded across
    /// that pause must not arrive afterwards as one big jerk of the camera.
    /// </summary>
    public static class TouchLookRoute
    {
        private static Vector2 s_delta;
        private static int s_frame = -1;

        /// <summary>Adds a drag delta, in screen pixels, to this frame's total.</summary>
        public static void Add(Vector2 pixels)
        {
            var frame = Time.frameCount;
            if (frame != s_frame)
            {
                s_delta = Vector2.zero;
                s_frame = frame;
            }
            s_delta += pixels;
        }

        /// <summary>This frame's accumulated pixels. Consuming clears; a stale frame reads zero.</summary>
        public static Vector2 Consume()
        {
            if (Time.frameCount != s_frame) return Vector2.zero;
            var value = s_delta;
            s_delta = Vector2.zero;
            return value;
        }
    }
}
