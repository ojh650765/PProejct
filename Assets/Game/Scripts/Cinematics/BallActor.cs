using System.Collections;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// A thrown ball, for both send-outs and capture attempts.
    ///
    /// The capture sequence is the one set piece in the battle that the player is asked to
    /// watch without doing anything, so it is written here as an explicit series of beats
    /// with rising tension rather than as a single animation: throw, absorb, fall, land,
    /// then exactly the number of shakes the engine reported, each slower and heavier than
    /// the last, then either the click or the break-out.
    ///
    /// Ships no art. A ball prefab supplied by the VFX or art worker is used when present;
    /// otherwise a small sphere stands in so the timing is reviewable.
    /// </summary>
    public sealed class BallActor : MonoBehaviour
    {
        private Transform _visual;
        private GameObject _attachedVfx;
        private float _radius = 0.11f;

        /// <summary>World position of the ball, for VFX and camera targeting.</summary>
        public Vector3 Position => transform.position;

        /// <summary>Transform the camera should frame during the capture sequence.</summary>
        public Transform FocusTarget => transform;

        /// <summary>
        /// Creates a ball. <paramref name="prefab"/> may be null, in which case a stand-in is
        /// built — the capture choreography must be reviewable before the art lands.
        /// </summary>
        public static BallActor Spawn(GameObject prefab, Vector3 origin, float radius = 0.11f)
        {
            var go = new GameObject("~Ball");
            go.transform.position = origin;
            var actor = go.AddComponent<BallActor>();
            actor._radius = Mathf.Max(0.03f, radius);
            actor.Build(prefab);
            return actor;
        }

        private void Build(GameObject prefab)
        {
            if (prefab != null)
            {
                var instance = Instantiate(prefab, transform);
                instance.transform.localPosition = Vector3.zero;
                _visual = instance.transform;
            }
            else
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "BallStandIn";
                sphere.transform.SetParent(transform, false);
                sphere.transform.localScale = Vector3.one * (_radius * 2f);
                var collider = sphere.GetComponent<Collider>();
                if (collider != null) Destroy(collider);
                _visual = sphere.transform;
            }

            _attachedVfx = CinematicHooks.AttachVfx(CinematicVfxKeys.BallThrowTrail, transform);
        }

        // --- Beats ----------------------------------------------------------------------------

        /// <summary>
        /// The throw. A real arc with a real apex, tumbling end over end.
        /// </summary>
        public IEnumerator Throw(Vector3 destination, float duration = 0.55f, float apex = 1.4f)
        {
            CinematicHooks.Audio(CinematicAudioCues.BallThrow, transform.position);

            Vector3 origin = transform.position;
            yield return CinematicRunner.Progress(duration, p =>
            {
                Vector3 pos = Vector3.Lerp(origin, destination, p);
                pos.y += 4f * apex * p * (1f - p);
                transform.position = pos;
                // Tumble is deliberately fast and about a tilted axis: a ball spinning about
                // one clean axis reads as a prop being rotated, not as a thrown object.
                if (_visual != null) _visual.localRotation = Quaternion.Euler(p * 1080f, p * 260f, p * 140f);
            });
            transform.position = destination;
        }

        /// <summary>The ball springs open. The moment the creature emerges or is absorbed.</summary>
        public IEnumerator Open(float duration = 0.18f)
        {
            CinematicHooks.Audio(CinematicAudioCues.BallOpen, transform.position);
            CinematicHooks.Vfx(CinematicVfxKeys.BallBurst, transform.position, Quaternion.identity);

            Vector3 baseScale = _visual != null ? _visual.localScale : Vector3.one;
            yield return CinematicRunner.Progress(duration, p =>
            {
                if (_visual == null) return;
                // Snap open, then relax. Peak at ~35% keeps the pop on the audio transient.
                float k = CinematicEase.Pulse(p, 0.35f);
                _visual.localScale = baseScale * (1f + 0.55f * k);
            });
            if (_visual != null) _visual.localScale = baseScale;
        }

        /// <summary>
        /// Draws a creature into the ball: the view shrinks toward the ball while the ball
        /// holds still and glows. Returns when the creature has fully disappeared.
        /// </summary>
        public IEnumerator Absorb(CreatureView view, float duration = 0.65f)
        {
            CinematicHooks.Vfx(CinematicVfxKeys.BallAbsorb, transform.position, Quaternion.identity);
            if (view == null) yield break;

            Vector3 from = view.transform.position;
            yield return CinematicRunner.Parallel(
                view.Motion.RecallShrink(duration),
                CinematicRunner.Progress(duration, p =>
                {
                    // Pull the whole view toward the ball as it shrinks. Shrinking in place
                    // reads as the creature evaporating; travelling reads as capture.
                    view.transform.position = Vector3.Lerp(from, transform.position, CinematicEase.InCubic(p));
                }));

            view.SetModelVisible(false);
            view.transform.position = from;
            view.Motion.RestoreScale();
        }

        /// <summary>
        /// The ball drops and settles. Bounces decay, and the last one is barely there —
        /// that decay is what makes the pause before the first shake feel loaded.
        /// </summary>
        public IEnumerator FallAndSettle(Vector3 groundPoint, float duration = 0.7f)
        {
            Vector3 origin = transform.position;
            Vector3 rest = groundPoint + Vector3.up * _radius;

            yield return CinematicRunner.Progress(duration, p =>
            {
                float k = CinematicEase.OutBounce(p);
                var pos = Vector3.Lerp(origin, rest, CinematicEase.OutQuad(p));
                pos.y = Mathf.Lerp(origin.y, rest.y, k);
                transform.position = pos;
                if (_visual != null) _visual.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(p * 14f) * 12f * (1f - p));
            });

            transform.position = rest;
            if (_visual != null) _visual.localRotation = Quaternion.identity;
            CinematicHooks.Audio(CinematicAudioCues.CreatureLand, transform.position);
        }

        /// <summary>
        /// One shake. <paramref name="tension"/> from 0 to 1 across the sequence makes each
        /// shake wider, slower and later than the one before it, so four shakes build instead
        /// of repeating. The pause before the shake is where the tension actually lives.
        /// </summary>
        public IEnumerator Shake(float tension)
        {
            tension = Mathf.Clamp01(tension);

            // The wait grows with tension. This is the single most important number in the
            // capture sequence: an evenly paced set of shakes has no drama at all.
            float pause = Mathf.Lerp(0.32f, 0.85f, tension);
            yield return CinematicRunner.Wait(pause);

            CinematicHooks.Audio(CinematicAudioCues.BallShake, transform.position);

            float amplitude = Mathf.Lerp(13f, 30f, tension);
            float duration = Mathf.Lerp(0.34f, 0.5f, tension);
            Vector3 rest = transform.position;

            yield return CinematicRunner.Progress(duration, p =>
            {
                // A decaying oscillation rather than a sine: the ball is being fought from
                // the inside and should lose energy over each shake.
                float wobble = CinematicEase.DampedOscillation(p, 2.2f, 3.4f);
                if (_visual != null) _visual.localRotation = Quaternion.Euler(0f, 0f, wobble * amplitude);
                transform.position = rest + Vector3.right * (wobble * 0.035f * (0.5f + tension));
            });

            transform.position = rest;
            if (_visual != null) _visual.localRotation = Quaternion.identity;
        }

        /// <summary>The catch. A held beat, then the click and the seal flash.</summary>
        public IEnumerator Click(float preHold = 0.55f)
        {
            // The silence before the click is longer than any of the shake pauses. That
            // asymmetry is what makes the click land as a resolution.
            yield return CinematicRunner.Wait(preHold);

            CinematicHooks.Audio(CinematicAudioCues.BallClick, transform.position);
            CinematicHooks.Vfx(CinematicVfxKeys.BallClick, transform.position, Quaternion.identity);

            Vector3 baseScale = _visual != null ? _visual.localScale : Vector3.one;
            yield return CinematicRunner.Progress(0.28f, p =>
            {
                if (_visual == null) return;
                float k = CinematicEase.Pulse(p, 0.2f);
                _visual.localScale = baseScale * (1f + 0.18f * k);
            });
            if (_visual != null) _visual.localScale = baseScale;
        }

        /// <summary>
        /// The break-out. Violent, immediate, and the opposite shape to <see cref="Click"/>:
        /// no pause, all energy at the front.
        /// </summary>
        public IEnumerator BurstOut(float duration = 0.34f)
        {
            CinematicHooks.Audio(CinematicAudioCues.BallBreakOut, transform.position);
            CinematicHooks.Vfx(CinematicVfxKeys.BallBreakOut, transform.position, Quaternion.identity);

            Vector3 baseScale = _visual != null ? _visual.localScale : Vector3.one;
            Vector3 rest = transform.position;
            yield return CinematicRunner.Progress(duration, p =>
            {
                if (_visual != null) _visual.localScale = baseScale * Mathf.Lerp(1.5f, 0.01f, CinematicEase.InCubic(p));
                transform.position = rest + Vector3.up * (Mathf.Sin(p * Mathf.PI) * 0.35f);
            });
        }

        /// <summary>Recall beam variant of <see cref="Absorb"/>, used for a voluntary switch-out.</summary>
        public IEnumerator Recall(CreatureView view, float duration = 0.5f)
        {
            CinematicHooks.Vfx(CinematicVfxKeys.BallRecall, view != null ? view.transform.position : transform.position, Quaternion.identity);
            yield return Absorb(view, duration);
        }

        /// <summary>Removes the ball, letting any attached effect finish first.</summary>
        public void Dismiss(float vfxLinger = 1.2f)
        {
            if (_attachedVfx != null)
            {
                _attachedVfx.transform.SetParent(null, true);
                Destroy(_attachedVfx, Mathf.Max(0.05f, vfxLinger));
                _attachedVfx = null;
            }
            Destroy(gameObject);
        }
    }
}
