using System.Collections;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// Procedural motion applied on top of whatever frames are playing.
    ///
    /// This layer existed before the sprite pivot for two reasons, and the pivot promoted it
    /// from a useful supplement to <b>the primary animation system</b>:
    ///
    /// <list type="number">
    /// <item><b>Clips cannot know the geometry.</b> A lunge has to travel exactly as far as the
    /// gap between two creatures, which depends on their sizes and their marks. Baking that
    /// into a clip means one distance for every matchup.</item>
    /// <item><b>There are no clips.</b> The official Gen 5 sprite set gives a looping idle,
    /// front and back, and nothing else — because Gen 1-5 drew nothing else. Every attack,
    /// reaction, faint and celebration in those games is a runtime transform of the idle
    /// sprite. That is what this file now is, and matching it is what makes the result read as
    /// Pokémon rather than as a 3D game wearing sprites.</item>
    /// </list>
    ///
    /// <b>What the pivot changed.</b> A camera-facing billboard discards its transform's
    /// rotation, so every rotation-based beat here would silently do nothing. Rather than
    /// delete the rotation vocabulary — it is what makes a dodge read as a dodge — the layer
    /// now <i>publishes</i> its roll and lean (<see cref="Roll"/>, <see cref="Lean"/>) for
    /// <see cref="CreatureBillboard"/> to apply in the view plane, and publishes its squash
    /// (<see cref="Squash"/>) for the billboard to apply in its own local space, where a
    /// non-uniform scale cannot shear against a facing rotation. Set
    /// <see cref="SpriteMode"/> and the layer stops writing rotation and scale to its own
    /// transform; leave it off and a rigged 3D actor behaves exactly as it did before.
    ///
    /// Everything is written to a dedicated child transform, never to the view root. The root
    /// belongs to <see cref="CreatureView.FaceTowards"/> and to the battle stage, and two
    /// systems writing one transform is how creatures end up sliding off their marks.
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

        [Tooltip("Publish rotation and scale for a billboard to apply instead of writing them to this " +
                 "transform, which a camera-facing quad would ignore.")]
        [SerializeField] private bool spriteMode = true;

        // Offsets are accumulated separately so overlapping beats compose instead of
        // overwriting: a creature can be recoiling from hit one while hit two lands.
        private Vector3 _beatOffset;
        private Vector3 _beatEuler;
        private Vector3 _squash = Vector3.one;
        private Vector3 _breathOffset;
        private float _breathPhase;
        private float _fade = 1f;

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

        /// <summary>
        /// When true, rotation and scale are published rather than written to this transform.
        /// The default, because creatures are billboards; turn it off for a rigged 3D actor.
        /// </summary>
        public bool SpriteMode
        {
            get => spriteMode;
            set => spriteMode = value;
        }

        /// <summary>True while a one-shot beat owns this layer.</summary>
        public bool IsPlayingBeat => _beat != null;

        /// <summary>
        /// Screen-plane roll in degrees, for a billboard to apply about the view axis. This is
        /// the Z component of the beat rotation, which on a flat subject is the only component
        /// that ever meant anything.
        /// </summary>
        public float Roll => _beatEuler.z;

        /// <summary>
        /// Forward/backward lean in degrees. A billboard has no depth to lean into, so this is
        /// offered for a caller that wants to read it as a vertical foreshortening; the shipped
        /// billboard ignores it rather than faking a rotation the art cannot support.
        /// </summary>
        public float Lean => _beatEuler.x;

        /// <summary>Non-uniform scale the current beat wants, for a billboard to apply locally.</summary>
        public Vector3 Squash => _squash;

        /// <summary>
        /// Opacity the current beat wants, 0-1. Faint and recall drive this; everything else
        /// leaves it at 1. Published rather than applied because opacity is the renderer's
        /// business and this layer owns transforms.
        /// </summary>
        public float Fade => _fade;

        private void LateUpdate()
        {
            // LateUpdate, so this composes on top of whatever is driving the frames rather
            // than being overwritten by it.
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

            if (spriteMode)
            {
                // A billboard ignores this transform's rotation and would shear under a
                // non-uniform scale combined with its own facing rotation, so both are left at
                // identity here and published instead.
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one;
            }
            else
            {
                transform.localRotation = Quaternion.Euler(_beatEuler);
                transform.localScale = _squash;
            }
        }

        /// <summary>Clears all procedural offset immediately. Used when re-binding a view.</summary>
        public void ResetLayer()
        {
            if (_beat != null) { CinematicRunner.Halt(_beat); _beat = null; }
            _beatOffset = Vector3.zero;
            _beatEuler = Vector3.zero;
            _breathOffset = Vector3.zero;
            _squash = Vector3.one;
            _fade = 1f;
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
        ///
        /// The stretch along the direction of travel is doing more work than it used to: with
        /// no attack frame to cut to, the squash is most of what says "this is an attack".
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
                // Lean into the blow, then straighten. Reads as effort on a rigged actor; on a
                // billboard the roll below carries it instead.
                _beatEuler = new Vector3(-9f * k, 0f, -4f * k);
                float s = 1f + 0.14f * k;
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
                // The Z term is the one a billboard can show, so it carries most of the tilt.
                _beatEuler = new Vector3(-tilt * impulse * 0.4f, 0f,
                    Mathf.Sin(p * 18f) * tilt * 0.7f * impulse);
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
        /// The collapse. Dispatches by <see cref="SpriteMode"/>, because the two art forms
        /// need genuinely different beats rather than one beat with different numbers.
        /// </summary>
        public IEnumerator Collapse(float duration = 1.1f)
            => spriteMode ? Sink(duration) : Own(TipOverRoutine(duration));

        /// <summary>
        /// The faint, for a sprite: stagger, then slide down through the ground plane with a
        /// vertical squash while the alpha goes.
        ///
        /// This is the Gen 1-5 faint and it is the right one here, not a compromise. Tipping a
        /// billboard onto its side shows the player a rotating rectangle, because a quad has no
        /// underside and no side view; sinking it uses only the axis the art supports. The old
        /// tip-over survives for rigged actors as <see cref="TipOverRoutine"/>.
        /// </summary>
        public IEnumerator Sink(float duration = 1.1f)
        {
            return Own(SinkRoutine(duration));
        }

        private IEnumerator SinkRoutine(float duration)
        {
            idleMotion = false;

            // A stagger first: the legs go before the body does. Two quick lurches, because a
            // single smooth wobble reads as the creature swaying rather than losing its footing.
            yield return CinematicRunner.Progress(duration * 0.24f, p =>
            {
                float k = CinematicEase.OutQuad(p);
                _beatEuler = new Vector3(0f, 0f, Mathf.Sin(p * Mathf.PI * 2.5f) * 9f * k);
                _beatOffset = new Vector3(0f, -0.05f * displayHeight * k, 0f);
                _squash = new Vector3(1f + 0.05f * k, 1f - 0.07f * k, 1f + 0.05f * k);
            });

            // Then the sink, accelerating. The squash keeps the crown descending faster than
            // the feet, so the creature reads as folding rather than as an elevator going down.
            yield return CinematicRunner.Progress(duration * 0.52f, p =>
            {
                float k = CinematicEase.InCubic(p);
                _beatOffset = new Vector3(0f, -0.62f * displayHeight * k, 0f);
                _beatEuler = new Vector3(0f, 0f, Mathf.Lerp(6f, 14f, k));
                float s = Mathf.Lerp(1f, 0.72f, k);
                _squash = new Vector3(Mathf.Lerp(1f, 1.12f, k), s, Mathf.Lerp(1f, 1.12f, k));
                // The fade starts late and finishes with the slide. Fading from the first frame
                // makes the collapse look like a despawn.
                _fade = 1f - CinematicEase.InQuad(Mathf.Clamp01((p - 0.35f) / 0.65f));
            });

            // Gone, and held gone. Nothing bounces back.
            yield return CinematicRunner.Progress(duration * 0.24f, p =>
            {
                _fade = 0f;
                _beatOffset = new Vector3(0f, -0.62f * displayHeight, 0f);
            });
        }

        /// <summary>
        /// The rigged-actor collapse: tips over about a foot rather than sinking straight
        /// down, holds at the bottom, and never fades. Retained for any 3D actor still in the
        /// project; a billboard uses <see cref="Sink"/> instead.
        /// </summary>
        private IEnumerator TipOverRoutine(float duration)
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
            float fromFade = _fade;
            return Own(RiseRoutine(fromEuler, fromOffset, fromFade, duration));
        }

        private IEnumerator RiseRoutine(Vector3 fromEuler, Vector3 fromOffset, float fromFade, float duration)
        {
            Vector3 fromSquash = _squash;
            yield return CinematicRunner.Progress(duration, p =>
            {
                float k = CinematicEase.OutBack(p, 1.1f);
                _beatEuler = Vector3.Lerp(fromEuler, Vector3.zero, k);
                _beatOffset = Vector3.Lerp(fromOffset, Vector3.zero, k);
                _squash = Vector3.Lerp(fromSquash, Vector3.one, k);
                _fade = Mathf.Lerp(fromFade, 1f, CinematicEase.OutQuad(p));
            });
            _beatEuler = Vector3.zero;
            _beatOffset = Vector3.zero;
            _squash = Vector3.one;
            _fade = 1f;
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
                    // Yaw is invisible on a billboard, so the wobble goes into roll instead.
                    _beatEuler = new Vector3(0f, 0f, Mathf.Sin(p * Mathf.PI * 2f) * 10f * scale);
                    float s = p < 0.15f ? 1f - 0.12f * (p / 0.15f) : 1f + 0.06f * k;
                    _squash = new Vector3(1f / Mathf.Sqrt(s), s, 1f / Mathf.Sqrt(s));
                });
            }
            _beatOffset = Vector3.zero;
            _beatEuler = Vector3.zero;
            _squash = Vector3.one;
        }

        /// <summary>
        /// The send-out: scale in from near nothing at the burst point, overshoot, settle.
        ///
        /// Traditional Pokémon does exactly this and nothing more — there is no emergence
        /// animation because there is no emergence frame. The overshoot is what stops it
        /// reading as a fade-in, and the alpha ramp is front-loaded so the smallest, worst
        /// frames of a scaled-up sprite are also the least visible.
        /// </summary>
        public IEnumerator Pop(float duration = 0.42f)
        {
            PrepareEntrance();
            return Own(CinematicRunner.Progress(duration, p =>
            {
                float e = CinematicEase.OutBack(p, 2.6f);
                float s = Mathf.Lerp(0.05f, 1f, e);
                _squash = new Vector3(s, s, s);
                // Drop the last of the way onto the mark rather than arriving in mid-air.
                _beatOffset = new Vector3(0f, 0.18f * displayHeight * (1f - CinematicEase.OutQuad(p)), 0f);
                _fade = Mathf.Clamp01(p / 0.25f);
            }));
        }

        /// <summary>
        /// Collapses the creature to nothing <i>immediately</i>, before the entrance beat is
        /// yielded on.
        ///
        /// A coroutine's first line does not run until the caller yields on it, so without
        /// this the sprite is drawn once at full size and full opacity in the frame between
        /// being made visible and the pop starting — a single-frame flash of the finished
        /// creature, which is exactly the artefact the pop exists to avoid.
        /// </summary>
        public void PrepareEntrance()
        {
            _squash = Vector3.one * 0.05f;
            _fade = 0f;
            _beatOffset = new Vector3(0f, 0.18f * displayHeight, 0f);
            _beatEuler = Vector3.zero;
            _grounded = true;
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
                _fade = 1f - CinematicEase.InQuad(p);
            }));
        }

        /// <summary>Restores full scale and opacity after a recall, before a re-send.</summary>
        public void RestoreScale()
        {
            _squash = Vector3.one;
            _beatOffset = Vector3.zero;
            _beatEuler = Vector3.zero;
            _fade = 1f;
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
