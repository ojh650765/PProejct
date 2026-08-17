using System.Collections;
using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Audio
{
    /// <summary>
    /// The frame-accurate half of the battle's soundtrack.
    ///
    /// Battle audio has two sources on purpose. <see cref="BattleAudioPresenter"/> scores the
    /// <see cref="BattleEvent"/> stream — one cue per event, fired when the presenter's beat
    /// opens. That is the right clock for everything the *event* is the moment of: HP ticks,
    /// the faint, the fanfare. But some sounds belong to a frame the event stream cannot
    /// express — the ball leaving the hand, the shake of a ball that is already on the ground,
    /// the instant a creature's feet hit the dirt. The cinematics layer knows those frames,
    /// and calls them in through <see cref="ICinematicAudioHook"/>; this component is that
    /// hook's implementation.
    ///
    /// The ownership audit, cue by cue, lives in <see cref="PlayCue"/>: every id the
    /// choreography can emit is either mapped onto the closest authored clip or deliberately
    /// silenced with the name of the system that owns that sound instead. A cue with two
    /// owners plays twice, and a doubled impact is the most audible integration bug there is.
    ///
    /// There are no recorded creature cries in the project at all, so <see cref="PlayCry"/>
    /// synthesises one: a short chirp whose pitch, contour and length are derived
    /// deterministically from the species id, so the same species always makes the same
    /// noise and no two species sound quite alike. Clips are cached per species and played
    /// through a voice routed to the SFX bus.
    /// </summary>
    [AddComponentMenu("Poke Lab/Audio/Cinematic Audio Hook Host")]
    public sealed class CinematicAudioHookHost : MonoBehaviour, ICinematicAudioHook
    {
        [Header("Cries")]
        [SerializeField, Range(0f, 1f)] private float cryVolume = 0.85f;
        [Tooltip("Sample rate for synthesised cries. 22 kHz is plenty for a chirp and half the memory.")]
        [SerializeField] private int crySampleRate = 22050;

        private AudioDirector _audio;
        private MusicDirector _music;
        private AudioSource _cryVoice;
        private int _shakeVariant;
        private Coroutine _captureSting;

        private readonly Dictionary<int, AudioClip> _cries = new Dictionary<int, AudioClip>();

        private void Awake()
        {
            // Registered under the Core interface, which is the only name the cinematics
            // assembly can see. CinematicHooks re-probes every half second, so registration
            // order against the presenter does not matter.
            ServiceHub.Register<ICinematicAudioHook>(this);

            // One dedicated voice for cries rather than the pooled one-shots, because a cry
            // is a runtime-generated clip the pool's catalogue knows nothing about.
            _cryVoice = gameObject.AddComponent<AudioSource>();
            _cryVoice.playOnAwake = false;
            _cryVoice.spatialBlend = 0f;
        }

        private void OnDestroy()
        {
            // Runtime-generated clips are not assets; nothing else will ever release them.
            foreach (var clip in _cries.Values)
            {
                if (clip != null) Destroy(clip);
            }
            _cries.Clear();
        }

        private bool Ready()
        {
            if (_audio == null) AudioDirector.TryResolve(out _audio);
            if (_music == null) ServiceHub.TryGet(out _music);
            return _audio != null;
        }

        // ---- cues --------------------------------------------------------------------

        /// <summary>
        /// One authored clip per choreography cue, or a documented silence.
        /// Positions are accepted and ignored: battle cues are mixed flat like the rest of
        /// the battle score, and a hard 3D pan on a cut-heavy camera reads as a glitch.
        /// </summary>
        public void PlayCue(string cueId, Vector3 position)
        {
            if (string.IsNullOrEmpty(cueId) || !Ready()) return;

            switch (cueId)
            {
                // ---- ball, frame-accurate from BallActor -----------------------------
                // These fire at the exact animation frames — release, open, wobble, click,
                // burst — which is why BattleAudioPresenter's timed CaptureRoutine stands
                // down whenever this hook is registered (see its OnCaptureAttempt).
                case "sfx_ball_throw":
                    _audio.PlaySfx(AudioIds.CaptureThrow, 0.9f);
                    break;

                case "sfx_ball_open":
                    // The send-out burst clip: the ball opening *is* the send-out moment,
                    // and BattleAudioPresenter suppresses its own copy for thrown entrances
                    // so the burst plays exactly once, on the open frame.
                    _audio.PlaySfx(AudioIds.BattleSendOut, 0.9f);
                    break;

                case "sfx_ball_shake":
                    {
                        // Rotate the three authored ticks and nudge the pitch upward per
                        // shake, mirroring the tension ramp the audio presenter used.
                        string tick = AudioIds.CaptureShakeTicks[_shakeVariant % AudioIds.CaptureShakeTicks.Length];
                        _audio.PlaySfx(tick, 0.95f, 1f + 0.018f * (_shakeVariant % 4));
                        _shakeVariant++;
                        break;
                    }

                case "sfx_ball_click":
                    _audio.PlaySfx(AudioIds.CaptureSuccessClick);
                    // The click resolves into the capture sting, exactly as the audio
                    // presenter's routine did: the click was tuned to a G major triad so the
                    // sting, which resolves to G, reads as the same gesture continuing.
                    if (_captureSting != null) StopCoroutine(_captureSting);
                    _captureSting = StartCoroutine(CaptureStingAfterClick());
                    break;

                case "sfx_ball_break_out":
                    _audio.PlaySfx(AudioIds.CaptureBreakOut);
                    break;

                // ---- movement and contact -------------------------------------------
                case "sfx_creature_land":
                    // No dedicated landing thud was authored; the ground impact pitched down
                    // and played quietly is an earthy thump that carries the weight without
                    // reading as an attack.
                    _audio.PlaySfx(AudioIds.MoveImpact(ElementType.Ground), 0.5f, 0.8f);
                    break;

                case "sfx_whoosh":
                    // The flying cast is the project's one genuine air-movement sound.
                    _audio.PlaySfx(AudioIds.MoveCast(ElementType.Flying), 0.55f, 1.05f);
                    break;

                case "sfx_transition_whoosh":
                    // Same air, slower and lower: a screen wipe is a bigger object moving.
                    _audio.PlaySfx(AudioIds.MoveCast(ElementType.Flying), 0.5f, 0.85f);
                    break;

                // ---- deliberately silent: another system owns these ------------------
                case "sfx_encounter_sting":
                    // MusicDirector plays the composed encounter sting on the
                    // GameMode.EncounterIntro change, on its own deck, fading the
                    // exploration theme under it. Playing anything here doubles it.
                    break;

                case "sfx_hit_neutral":
                case "sfx_hit_super":
                case "sfx_hit_weak":
                case "sfx_critical":
                    // BattleAudioPresenter scores DamageDealtEvent with the *element-typed*
                    // impact plus effectiveness and crit layers, and — on the performed
                    // tap — that event is dispatched on the very frame PlayDamage's beat
                    // opens, i.e. the same frame these cues fire. The presenter's version
                    // is richer (it knows the move's type; this hook only knows a cue id),
                    // so it owns the landing and these stay silent.
                    break;

                case "sfx_faint":
                case "sfx_victory":
                    // Same clock, same reasoning: CreatureFaintedEvent and BattleEndedEvent
                    // are performed-tap events, so SFX_Battle_Faint and the victory fanfare
                    // already land on these exact beats from BattleAudioPresenter.
                    break;

                default:
                    // A cue this map has never heard of. If the catalogue happens to know it
                    // by name, honour it; otherwise degrade to silence — a missing sound is
                    // a cosmetic gap, a warning per swing is log spam.
                    if (_audio.HasClip(cueId)) _audio.PlaySfx(cueId, 0.8f);
                    break;
            }
        }

        private IEnumerator CaptureStingAfterClick()
        {
            yield return new WaitForSeconds(0.14f);
            if (!Ready()) yield break;
            _audio.DuckMusic(0.75f, 0.2f, 1.8f, 1.0f);
            if (_music != null) _music.PlaySting(AudioIds.MusicCaptureSuccess);
            else _audio.PlaySfx(AudioIds.MusicCaptureSuccess);
            _captureSting = null;
        }

        // ---- cries -------------------------------------------------------------------

        /// <summary>
        /// A creature's voice, made from nothing.
        ///
        /// The design constraints, in order: it must be deterministic (the same creature
        /// heard twice is the same creature), it must be short (it plays inside the cry-hold
        /// beat of the send-out), and it must be charming rather than harsh — so the wave is
        /// a rounded sine-plus-harmonic with vibrato, never a square, and every parameter is
        /// clamped to a range that stays musical.
        /// </summary>
        public void PlayCry(int speciesId, Vector3 position)
        {
            if (!Ready() || _cryVoice == null) return;

            if (!_cries.TryGetValue(speciesId, out var clip) || clip == null)
            {
                clip = SynthesiseCry(speciesId);
                _cries[speciesId] = clip;
            }
            if (clip == null) return;

            // Routed through the SFX group so mixer volume and ducking apply; when no mixer
            // exists the director folds bus volume into source gain, and we mirror that here.
            _cryVoice.outputAudioMixerGroup = _audio.GroupFor(AudioBus.Sfx);
            float fallbackGain = _audio.Mixer == null
                ? _audio.GetBusVolume(AudioBus.Sfx) * _audio.GetBusVolume(AudioBus.Master)
                : 1f;
            _cryVoice.PlayOneShot(clip, cryVolume * fallbackGain);
        }

        /// <summary>
        /// Builds the clip. Character comes from hashed species bits:
        ///   * base pitch — small species ids are not "small creatures", so this is pure
        ///     hash, spread across roughly an octave and a half (300–850 Hz);
        ///   * contour — rising cries read as bright/curious, falling ones as heavy/gruff;
        ///   * one or two syllables — the second, when present, is a shorter echo a minor
        ///     third up, which is what makes a cry feel like a call rather than a beep;
        ///   * duration 0.3–0.6 s, per the brief.
        /// </summary>
        private AudioClip SynthesiseCry(int speciesId)
        {
            uint h = Scramble((uint)speciesId);

            float baseFreq = Mathf.Lerp(300f, 850f, Byte0(h) / 255f);
            bool rising = (h & 0x100u) != 0u;
            float duration = Mathf.Lerp(0.3f, 0.6f, Byte1(h) / 255f);
            int syllables = 1 + (int)((h >> 20) & 1u);
            float vibratoRate = Mathf.Lerp(9f, 15f, Byte2(h) / 255f);
            float vibratoDepth = Mathf.Lerp(0.015f, 0.05f, Byte3(h) / 255f);
            float glide = rising ? 1.45f : 0.62f;   // end pitch as a multiple of the start

            int sampleRate = Mathf.Clamp(crySampleRate, 8000, 48000);
            int totalSamples = Mathf.CeilToInt(duration * sampleRate);
            var samples = new float[totalSamples];

            // Syllable layout: the first takes the greater share, the second is an echo
            // with a short silence between them.
            float gap = syllables > 1 ? 0.06f : 0f;
            float firstLength = syllables > 1 ? (duration - gap) * 0.62f : duration;
            float secondStart = firstLength + gap;

            double phase = 0.0;
            for (int i = 0; i < totalSamples; i++)
            {
                float t = i / (float)sampleRate;

                float local;        // 0-1 through the current syllable
                float inSyllable;   // seconds into the current syllable
                float freqScale;    // pitch multiplier for the current syllable
                float level;        // syllable gain

                if (t < firstLength)
                {
                    inSyllable = t;
                    local = t / firstLength;
                    freqScale = 1f;
                    level = 1f;
                }
                else if (syllables > 1 && t >= secondStart)
                {
                    inSyllable = t - secondStart;
                    local = Mathf.Clamp01(inSyllable / Mathf.Max(0.01f, duration - secondStart));
                    freqScale = 1.19f;   // roughly a minor third up: an answer, not a repeat
                    level = 0.7f;
                }
                else
                {
                    samples[i] = 0f;     // the breath between syllables
                    continue;
                }

                // Pitch contour: an eased glide from start to start*glide, plus vibrato.
                float eased = local * local * (3f - 2f * local);
                float freq = baseFreq * freqScale * Mathf.Lerp(1f, glide, eased);
                freq *= 1f + vibratoDepth * Mathf.Sin(2f * Mathf.PI * vibratoRate * t);

                // Envelope: 12 ms attack so the onset does not click, then an exponential
                // release that leaves a soft tail rather than a chopped end.
                float attack = Mathf.Clamp01(inSyllable / 0.012f);
                float release = Mathf.Pow(1f - local, 1.6f);
                float envelope = attack * release * level;

                // Rounded timbre: fundamental plus a quiet octave harmonic. tanh-style
                // soft clip keeps any accidental peak from turning digital-harsh.
                phase += 2.0 * Mathf.PI * freq / sampleRate;
                float wave = Mathf.Sin((float)phase) + 0.3f * Mathf.Sin((float)(phase * 2.0));
                float value = envelope * wave * 0.62f;
                samples[i] = value / (1f + Mathf.Abs(value) * 0.4f);

                if (phase > 2.0 * Mathf.PI * 1000.0) phase -= 2.0 * Mathf.PI * 1000.0;
            }

            var clip = AudioClip.Create($"Cry_{speciesId}", totalSamples, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        // A tiny integer scrambler (xorshift-style) so consecutive species ids do not get
        // near-identical voices — raw ids differ by one bit and would sound like siblings.
        private static uint Scramble(uint x)
        {
            x ^= 0x9E3779B9u;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            x *= 0x85EBCA6Bu;
            x ^= x >> 13;
            return x;
        }

        private static float Byte0(uint h) => h & 0xFFu;
        private static float Byte1(uint h) => (h >> 8) & 0xFFu;
        private static float Byte2(uint h) => (h >> 16) & 0xFFu;
        private static float Byte3(uint h) => (h >> 24) & 0xFFu;
    }
}
