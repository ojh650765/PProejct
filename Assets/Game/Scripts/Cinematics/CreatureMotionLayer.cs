using System.Collections;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// Procedural motion applied on top of whatever the Animator is playing.
    ///
    /// This layer exists for two reasons that both matter to the finished product:
    ///
    /// 1. <b>Clips cannot know the geometry.</b> A lunge has to travel exactly as far as the
    ///    gap between two creatures, which depends on their sizes and their marks. Baking
    ///    that into a clip means one distance for every matchup. Driving it here means the
    ///    attack always arrives at contact range.
    /// 2. <b>It degrades.</b> When the Blender rigs are still stubs, this layer alone still
    ///    produces a readable performance — the creature visibly winds up, strikes, gets
    ///    knocked back and collapses — so choreography can be reviewed before the art lands.
    ///
    /// Everything is written to a dedicated child transform, never to the view root. The
    /// root belongs to <see cref="CreatureView.FaceTowards"/> and to the battle stage, and
    /// two systems writing one transform is how creatures end up sliding off their marks.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class CreatureMotionLayer : MonoBehaviour
    {
        [Tooltip("Creature height in metres. Every amplitude here is expressed relative to it.")]
        [SerializeField] private float displayHeight = 1f;

        [Tooltip("Idle breathing amplitude as a fraction of display height.")]
        [Range(0f, 0.1f)]
        [SerializeField] private float breathAmplitude = 0.018f;

        [Tooltip("Idle breathing cycles per second.")]
        [SerializeField] private float breathRate = 0.55f;

        [Tooltip("When true the breathing bob runs. Turned off while a one-shot beat owns the layer.")]
        [SerializeField] private bool idleMotion = true;

        // Offsets are accumulated separately so overlapping beats compose instead of
        // overwriting: a creature can be recoiling from hit one while hit two lands.
        private Vector3 _beatOffset;
        private Vector3 _beatEuler;
        private Vector3 _squash = Vector3.one;
        private Vector3 _breathOffset;
        private float _breathPhase;

        private Coroutine _beat;
        private bool _grounded = true;

        /// <summary>Creature height used to scale every amplitude. Set on bind.</summary>
        public float DisplayHeight
        {
            get => displayHeight;
            set => displayHeight = Mathf.Max(0.05f, value);
        }

        /// <summary>Whether the idle breathing bob is running.</summary>
        public bool IdleMotion
        {
            get => idleMotion;
            set => idleMotion = value;
        }

        /// <summary>True while a one-shot beat owns this layer.</summary>
        public bool IsPlayingBeat => _beat != null;

        private void LateUpdate()
        {
            // LateUpdate, so this composes on top of the Animator rather than being
            // overwritten by it. The Animator writes the model's bones; this writes the
            // container those bones hang from.
            // Breathing is accumulated separately from beat offsets and summed at the end.
            // Folding it into _beatOffset would make every beat start from wherever the bob
            // happened to be, and the creature would drift off its mark over a long battle.
            float target = 0f;
            if (idleMotion && _grounded)
            {
                _breathPhase += Time.deltaTime * breathRate;
                // Two incommensurate frequencies so the idle never visibly loops.
                float bob = Mathf.Sin(_breathPhase * Mathf.PI * 2f) * 0.7f
                          + Mathf.Sin(_breathPhase * Mathf.PI * 2f * 0.37f) * 0.3f;
                target = bob * breathAmplitude * displayHeight;
            }
            _breathOffset.y = Mathf.Lerp(_breathOffset.y, target, 1f - Mathf.Exp(-8f * Time.deltaTime));

            transform.localPosition = _beatOffset + _breathOffset;
            transform.localRotation = Quaternion.Euler(_beatEuler);
            transform.localScale = _squash;
        }

        /// <summary>Clears all procedural offset immediately. Used when re-binding a view.</summary>
        public void ResetLayer()
        {
            if (_beat != null) { CinematicRunner.Halt(_beat); _beat = null; }
            _beatOffset = Vector3.zero;
            _beatEuler = Vector3.zero;
            _breathOffset = Vector3.zero;
            _squash = Vector3.one;
            _grounded = true;
            idleMotion = true;
        }

        private IEnumerator Own(IEnumerator routine)
        {
            // One beat at a time: a second lunge starting while the first is mid-flight would
            // leave the creature permanently displaced, because both would be interpolating
            // from their own captured start offsets.
            if (_beat != null) CinematicRunner.Halt(_beat);
            _beat = CinematicRunner.Run(Wrapped(routine));
            yield return _beat;
        }

        private IEnumerator Wrapped(IEnumerator routine)
        {
            yield return routine;
            _beat = null;
        }

        // --- Beats --------------------------------------------------------------------------

        /// <summary>
        /// Drives into contact and returns. The strike is fast and the recovery is slow, so
        /// the creature reads as committing to the blow rather than bouncing off it.
        /// </summary>
        /// <param name="direction">World direction to travel. Converted to local space.</param>
        /// <param name="distance">Metres to travel at the peak.</param>
        /// <param name="duration">Total beat length. Contact lands at <paramref name="contactAt"/>.</param>
        /// <param name="contactAt">Normalised time of peak extension, 0-1.</param>
        public IEnumerator Lunge(Vector3 direction, float distance, float duration, float contactAt = 0.38f)
        {
            Vector3 local = ToLocal(direction) * distance;
            return Own(CinematicRunner.Progress(duration, p =>
            {
                float k = CinematicEase.Pulse(p, contactAt);
                _beatOffset = local * k;
                // Lean into the blow, then straighten. Reads as effort.
                _beatEuler = new Vector3(-9f * k, 0f, 0f);
                float s = 1f + 0.08f * k;
                _squash = new Vector3(1f / Mathf.Sqrt(s), s, 1f / Mathf.Sqrt(s));
            }));
        }

        /// <summary>
        /// Plants and releases. Used for special moves: the creature does not travel, it
        /// compresses, holds, and then throws its whole body into the release.
        /// </summary>
        public IEnumerator Plant(float duration, float releaseAt = 0.55f)
        {
            return Own(CinematicRunner.Progress(duration, p =>
            {
                if (p < releaseAt)
                {
                    // Compress and sink.
                    float k = CinematicEase.InQuad(p / releaseAt);
                    _beatOffset = new Vector3(0f, -0.09f * displayHeight * k, 0f);
                    _squash = new Vector3(1f + 0.10f * k, 1f - 0.13f * k, 1f + 0.10f * k);
                    _beatEuler = new Vector3(6f * k, 0f, 0f);
                }
                else
                {
                    // Extend past neutral on release, then settle.
                    float k = (p - releaseAt) / (1f - releaseAt);
                    float e = CinematicEase.OutBack(k, 2.2f);
                    _beatOffset = new Vector3(0f, Mathf.Lerp(-0.09f * displayHeight, 0.04f * displayHeight, e), 0f);
                    _squash = Vector3.Lerp(new Vector3(1.10f, 0.87f, 1.10f), Vector3.one, e);
                    _beatEuler = new Vector3(Mathf.Lerp(6f, -4f, e) * (1f - CinematicEase.OutQuad(k)), 0f, 0f);
                }
            }));
        }

        /// <summary>
        /// Knocked back and wobbling. Amplitude scales with how much of the health bar the
        /// hit took, which is what makes a chip read differently from a near-KO.
        /// </summary>
        /// <param name="direction">World direction the impact travelled.</param>
        /// <param name="severity">0-1. Damage as a fraction of max HP.</param>
        public IEnumerator Recoil(Vector3 direction, float severity, float duration = 0.55f)
        {
            Vector3 local = ToLocal(direction);
            float push = Mathf.Lerp(0.10f, 0.65f, Mathf.Clamp01(severity)) * Mathf.Max(0.4f, displayHeight);
            float tilt = Mathf.Lerp(4f, 22f, Mathf.Clamp01(severity));

            return Own(CinematicRunner.Progress(duration, p =>
            {
                // Snap out, then oscillate back down to nothing.
                float impulse = p < 0.12f
                    ? CinematicEase.OutExpo(p / 0.12f)
                    : Mathf.Abs(CinematicEase.DampedOscillation((p - 0.12f) / 0.88f, 2.2f, 4.5f));
                _beatOffset = local * push * impulse;
                _beatEuler = new Vector3(-tilt * impulse, 0f, Mathf.Sin(p * 18f) * tilt * 0.25f * impulse);
                float s = 1f - 0.12f * impulse;
                _squash = new Vector3(1f / s, s, 1f / s);
            }));
        }

        /// <summary>A real dodge: the body clears the line of the attack and returns.</summary>
        /// <param name="sideways">World direction to evade along.</param>
        public IEnumerator Dodge(Vector3 sideways, float duration = 0.45f)
        {
            Vector3 local = ToLocal(sideways) * (0.55f + 0.55f * displayHeight);
            return Own(CinematicRunner.Progress(duration, p =>
            {
                float k = CinematicEase.Pulse(p, 0.42f);
                // Hop as well as slide — a purely lateral slide reads as a glitch.
                _beatOffset = local * k + Vector3.up * (0.16f * displayHeight * Mathf.Sin(k * Mathf.PI));
                _beatEuler = new Vector3(0f, 0f, -16f * k);
                _squash = new Vector3(1f - 0.05f * k, 1f + 0.07f * k, 1f - 0.05f * k);
            }));
        }

        /// <summary>
        /// Arriving from above with weight: fall, compress on contact, rebound, settle.
        /// The compression is the whole trick — a creature that stops dead at ground level
        /// looks weightless no matter how fast it fell.
        /// </summary>
        public IEnumerator Land(float fromHeight, float fallDuration = 0.32f, float settleDuration = 0.42f)
        {
            float h = Mathf.Max(fromHeight, displayHeight * 1.5f);
            return Own(LandRoutine(h, fallDuration, settleDuration));
        }

        private IEnumerator LandRoutine(float h, float fallDuration, float settleDuration)
        {
            _grounded = false;
            yield return CinematicRunner.Progress(fallDuration, p =>
            {
                // Gravity is quadratic; a linear fall reads as floating.
                float k = CinematicEase.InQuad(p);
                _beatOffset = new Vector3(0f, Mathf.Lerp(h, 0f, k), 0f);
                _squash = new Vector3(1f - 0.06f * k, 1f + 0.10f * k, 1f - 0.06f * k);
            });

            yield return CinematicRunner.Progress(settleDuration, p =>
            {
                // Compress hard on the first fifth, then spring back with a decaying overshoot.
                float compress = p < 0.2f
                    ? CinematicEase.OutQuad(p / 0.2f)
                    : 1f - CinematicEase.OutBack((p - 0.2f) / 0.8f, 1.4f);
                compress = Mathf.Clamp01(compress);
                _beatOffset = new Vector3(0f, -0.13f * displayHeight * compress, 0f);
                float s = 1f - 0.22f * compress;
                _squash = new Vector3(1f / Mathf.Sqrt(s), s, 1f / Mathf.Sqrt(s));
                _beatEuler = Vector3.zero;
            });

            _beatOffset = Vector3.zero;
            _squash = Vector3.one;
            _grounded = true;
        }

        /// <summary>
        /// The collapse. Tips over about a foot rather than sinking straight down, holds at
        /// the bottom, and never fades — the brief calls out fades explicitly and a fade is
        /// what a faint looks like when nobody authored one.
        /// </summary>
        public IEnumerator Collapse(float duration = 1.1f)
        {
            return Own(CollapseRoutine(duration));
        }

        private IEnumerator CollapseRoutine(float duration)
        {
            idleMotion = false;

            // A stagger first: the legs go before the body does.
            yield return CinematicRunner.Progress(duration * 0.22f, p =>
            {
                float k = CinematicEase.OutQuad(p);
                _beatEuler = new Vector3(0f, 0f, 7f * k);
                _beatOffset = new Vector3(0f, -0.05f * displayHeight * k, 0f);
                _squash = new Vector3(1f + 0.04f * k, 1f - 0.06f * k, 1f + 0.04f * k);
            });

            // Then the fall, accelerating.
            yield return CinematicRunner.Progress(duration * 0.45f, p =>
            {
                float k = CinematicEase.InCubic(p);
                _beatEuler = new Vector3(0f, 0f, Mathf.Lerp(7f, 88f, k));
                // Pivoting about the base means the centre of mass drops by roughly the
                // half-height as it goes horizontal; without this the model floats.
                _beatOffset = new Vector3(0f, -0.42f * displayHeight * k, 0f);
                _squash = Vector3.one;
            });

            // A single dead bounce, then stillness.
            yield return CinematicRunner.Progress(duration * 0.33f, p =>
            {
                float settle = Mathf.Abs(CinematicEase.DampedOscillation(p, 1.6f, 7f));
                _beatEuler = new Vector3(0f, 0f, 88f + 4f * settle);
                _beatOffset = new Vector3(0f, -0.42f * displayHeight - 0.02f * displayHeight * settle, 0f);
            });
        }

        /// <summary>Gets back up from a collapse. Used when a capture attempt fails.</summary>
        public IEnumerator Rise(float duration = 0.7f)
        {
            Vector3 fromEuler = _beatEuler;
            Vector3 fromOffset = _beatOffset;
            return Own(RiseRoutine(fromEuler, fromOffset, duration));
        }

        private IEnumerator RiseRoutine(Vector3 fromEuler, Vector3 fromOffset, float duration)
        {
            yield return CinematicRunner.Progress(duration, p =>
            {
                float k = CinematicEase.OutBack(p, 1.1f);
                _beatEuler = Vector3.Lerp(fromEuler, Vector3.zero, k);
                _beatOffset = Vector3.Lerp(fromOffset, Vector3.zero, k);
            });
            _beatEuler = Vector3.zero;
            _beatOffset = Vector3.zero;
            _squash = Vector3.one;
            idleMotion = true;
        }

        /// <summary>A celebratory bounce, repeated. Used for victory and level-up.</summary>
        public IEnumerator Celebrate(int hops = 3, float hopDuration = 0.4f)
        {
            return Own(CelebrateRoutine(hops, hopDuration));
        }

        private IEnumerator CelebrateRoutine(int hops, float hopDuration)
        {
            for (int i = 0; i < Mathf.Max(1, hops); i++)
            {
                // Each hop is a little lower than the last so it decays instead of looping.
                float scale = Mathf.Lerp(1f, 0.6f, i / Mathf.Max(1f, hops - 1f));
                yield return CinematicRunner.Progress(hopDuration, p =>
                {
                    float k = Mathf.Sin(p * Mathf.PI);
                    _beatOffset = new Vector3(0f, 0.30f * displayHeight * k * scale, 0f);
                    _beatEuler = new Vector3(0f, Mathf.Sin(p * Mathf.PI * 2f) * 14f * scale, 0f);
                    float s = p < 0.15f ? 1f - 0.12f * (p / 0.15f) : 1f + 0.06f * k;
                    _squash = new Vector3(1f / Mathf.Sqrt(s), s, 1f / Mathf.Sqrt(s));
                });
            }
            _beatOffset = Vector3.zero;
            _beatEuler = Vector3.zero;
            _squash = Vector3.one;
        }

        /// <summary>Shrinks into the recall beam and disappears. Reverse of a send-out burst.</summary>
        public IEnumerator RecallShrink(float duration = 0.45f)
        {
            return Own(CinematicRunner.Progress(duration, p =>
            {
                float k = CinematicEase.InCubic(p);
                float s = Mathf.Lerp(1f, 0.02f, k);
                _squash = new Vector3(s, s, s);
                // Drawn toward the ball, not just scaled: a pure scale-down reads as a bug.
                _beatOffset = new Vector3(0f, 0.15f * displayHeight * k, 0f);
            }));
        }

        /// <summary>Restores full scale after a recall, before a re-send.</summary>
        public void RestoreScale()
        {
            _squash = Vector3.one;
            _beatOffset = Vector3.zero;
            _beatEuler = Vector3.zero;
        }

        /// <summary>
        /// A quiet flinch: no travel, just a compression. Used for status ticks and stat
        /// drops where a full recoil would over-sell a minor event.
        /// </summary>
        public IEnumerator Flinch(float strength = 0.4f, float duration = 0.35f)
        {
            return Own(CinematicRunner.Progress(duration, p =>
            {
                float k = CinematicEase.Pulse(p, 0.25f) * Mathf.Clamp01(strength);
                _beatOffset = new Vector3(0f, -0.06f * displayHeight * k, 0f);
                float s = 1f - 0.10f * k;
                _squash = new Vector3(1f / s, s, 1f / s);
                _beatEuler = new Vector3(0f, 0f, Mathf.Sin(p * 26f) * 3f * k);
            }));
        }

        /// <summary>A rising swell, for a stat boost or an ability trigger.</summary>
        public IEnumerator Swell(float duration = 0.5f)
        {
            return Own(CinematicRunner.Progress(duration, p =>
            {
                float k = Mathf.Sin(Mathf.Clamp01(p) * Mathf.PI);
                _beatOffset = new Vector3(0f, 0.10f * displayHeight * k, 0f);
                float s = 1f + 0.09f * k;
                _squash = new Vector3(s, s, s);
            }));
        }

        private Vector3 ToLocal(Vector3 worldDirection)
        {
            if (worldDirection.sqrMagnitude < 1e-6f) return Vector3.forward;
            Transform parent = transform.parent;
            Vector3 local = parent != null ? parent.InverseTransformDirection(worldDirection) : worldDirection;
            local.y = 0f;
            return local.sqrMagnitude < 1e-6f ? Vector3.forward : local.normalized;
        }
    }
}
