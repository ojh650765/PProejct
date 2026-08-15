using System;
using System.Collections;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// A thing that travels from one point to another over real time.
    ///
    /// The reason this exists rather than the presenter simply playing an impact after a
    /// fixed delay: the brief requires projectiles to actually travel from
    /// <c>Anchor_Muzzle</c> to the target's <c>Anchor_Body</c>, with the impact landing when
    /// the projectile arrives. Those anchors move — the attacker may be mid-lunge and the
    /// target mid-dodge — so flight has to be resolved against live positions, and arrival
    /// has to be reported rather than assumed.
    ///
    /// It carries no art of its own. The VFX worker's effect is attached to the transform
    /// this drives, and when no VFX layer is registered a tiny unlit marker is used so the
    /// timing is still visible during development.
    /// </summary>
    public sealed class ProjectileActor : MonoBehaviour
    {
        [Tooltip("Show a debug marker when the VFX layer supplies no visual. Off for review builds.")]
        [SerializeField] private bool showDebugMarker = true;

        private Transform _visual;
        private GameObject _attachedVfx;

        /// <summary>Creates a pooled-free projectile instance. Destroyed when its flight ends.</summary>
        public static ProjectileActor Spawn(string vfxKey, Vector3 origin)
        {
            var go = new GameObject("~Projectile");
            go.transform.position = origin;
            var actor = go.AddComponent<ProjectileActor>();
            actor.Initialise(vfxKey);
            return actor;
        }

        private void Initialise(string vfxKey)
        {
            _attachedVfx = CinematicHooks.AttachVfx(vfxKey, transform);
            if (_attachedVfx != null || !showDebugMarker) return;

            // No VFX layer registered. A small emissive sphere keeps flight timing reviewable.
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "DebugMarker";
            marker.transform.SetParent(transform, false);
            marker.transform.localScale = Vector3.one * 0.22f;
            var collider = marker.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            _visual = marker.transform;
        }

        /// <summary>
        /// Flies to a moving target and completes on arrival.
        ///
        /// <paramref name="arcHeight"/> is added as a vertical bulge; the flight is otherwise
        /// a straight interpolation re-evaluated against the target's current position each
        /// frame, so a target that dodges is genuinely missed rather than magnetically hit.
        /// </summary>
        /// <param name="target">Live target transform. Its position is sampled every frame.</param>
        /// <param name="duration">Flight time in seconds.</param>
        /// <param name="arcHeight">Peak vertical bulge in metres. 0 for a flat beam.</param>
        /// <param name="spin">Degrees per second of spin about the flight axis.</param>
        public IEnumerator FlyTo(Transform target, float duration, float arcHeight = 0.6f, float spin = 0f)
        {
            if (target == null) { Finish(); yield break; }

            Vector3 origin = transform.position;
            float elapsed = 0f;
            duration = Mathf.Max(0.05f, duration);

            while (elapsed < duration)
            {
                float p = elapsed / duration;
                Vector3 destination = target.position;

                Vector3 flat = Vector3.Lerp(origin, destination, p);
                // sin gives zero bulge at both ends, so the projectile leaves and arrives
                // exactly on the anchors rather than near them.
                flat.y += Mathf.Sin(p * Mathf.PI) * arcHeight;

                Vector3 previous = transform.position;
                transform.position = flat;

                Vector3 velocity = flat - previous;
                if (velocity.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(velocity.normalized, Vector3.up);
                if (spin != 0f && _visual != null) _visual.Rotate(Vector3.forward, spin * Time.unscaledDeltaTime, Space.Self);

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            transform.position = target.position;
        }

        /// <summary>
        /// Flies past a fixed point and keeps going. This is what a missed attack does —
        /// the projectile continues through the space the target used to occupy, which is
        /// what sells the dodge as a dodge rather than as a cancelled attack.
        /// </summary>
        public IEnumerator FlyPast(Vector3 through, float duration, float overshoot = 3.5f, float arcHeight = 0.4f)
        {
            Vector3 origin = transform.position;
            Vector3 direction = (through - origin);
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-5f) direction = transform.forward;
            Vector3 destination = through + direction.normalized * overshoot;

            yield return CinematicRunner.Progress(Mathf.Max(0.05f, duration), p =>
            {
                Vector3 pos = Vector3.Lerp(origin, destination, p);
                pos.y += Mathf.Sin(Mathf.Clamp01(p) * Mathf.PI) * arcHeight;
                transform.position = pos;
            });
        }

        /// <summary>
        /// Ballistic arc to a fixed point with a real apex. Used for ball throws, where the
        /// height of the arc is the whole read on how hard the trainer threw.
        /// </summary>
        public IEnumerator Arc(Vector3 destination, float duration, float apexHeight, Action onLand = null)
        {
            Vector3 origin = transform.position;
            yield return CinematicRunner.Progress(Mathf.Max(0.05f, duration), p =>
            {
                Vector3 pos = Vector3.Lerp(origin, destination, p);
                // Parabola through the two endpoints with its peak at p = 0.5.
                pos.y += 4f * apexHeight * p * (1f - p);
                Vector3 previous = transform.position;
                transform.position = pos;

                Vector3 velocity = pos - previous;
                if (velocity.sqrMagnitude > 1e-7f) transform.rotation = Quaternion.LookRotation(velocity.normalized, Vector3.up);
            });
            transform.position = destination;
            onLand?.Invoke();
        }

        /// <summary>Detaches the VFX so it can finish its own dissolve, then destroys this actor.</summary>
        public void Finish(float vfxLinger = 1.5f)
        {
            if (_attachedVfx != null)
            {
                // Reparenting rather than destroying: an impact burst that is deleted on the
                // frame it spawns produces a single-frame flash and nothing else.
                _attachedVfx.transform.SetParent(null, true);
                Destroy(_attachedVfx, Mathf.Max(0.05f, vfxLinger));
                _attachedVfx = null;
            }
            Destroy(gameObject);
        }
    }
}
