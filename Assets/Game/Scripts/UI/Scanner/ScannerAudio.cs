using UnityEngine;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The scanner's sound design language.
    ///
    /// A device that boots, sweeps and alerts silently does not read as a device. The audio
    /// worker owns the game's mixed soundtrack and will supply authored clips through the
    /// serialized fields below; until then every cue is synthesised here so the scanner is
    /// complete and reviewable on its own. That is not a placeholder in the usual sense —
    /// the synthesis defines the language (a clean sine family for the device's own voice,
    /// a detuned pair for alerts, a downward third for the power-down) that the authored
    /// clips should then match.
    ///
    /// All cues are short, quiet and non-musical so they never fight the battle mix.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScannerAudio : MonoBehaviour
    {
        [Header("Authored clips (optional — synthesised when absent)")]
        [SerializeField] private AudioClip _bootClip;
        [SerializeField] private AudioClip _powerDownClip;
        [SerializeField] private AudioClip _refreshClip;
        [SerializeField] private AudioClip _threatClip;
        [SerializeField] private AudioClip _swingUpClip;
        [SerializeField] private AudioClip _swingDownClip;
        [SerializeField] private AudioClip _rerankClip;
        [SerializeField] private AudioClip _retargetClip;

        [Header("Mix")]
        [Range(0f, 1f)][SerializeField] private float _volume = 0.35f;
        [SerializeField] private AudioSource _source;
        [Tooltip("Set false when the audio worker takes over cueing from the event stream.")]
        [SerializeField] private bool _enabled = true;

        private const int SampleRate = 44100;

        private AudioClip _synthBoot;
        private AudioClip _synthPowerDown;
        private AudioClip _synthRefresh;
        private AudioClip _synthThreatLow;
        private AudioClip _synthThreatHigh;
        private AudioClip _synthSwingUp;
        private AudioClip _synthSwingDown;
        private AudioClip _synthRerank;
        private AudioClip _synthRetarget;

        private void Awake()
        {
            if (_source == null) _source = GetComponent<AudioSource>();
            if (_source == null)
            {
                _source = gameObject.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                // 2D: the docked scanner is a HUD element, not a world object. The overworld
                // handheld is close enough to the camera that panning it adds nothing.
                _source.spatialBlend = 0f;
                _source.bypassReverbZones = true;
            }
        }

        /// <summary>
        /// Mutes or unmutes the device's cues. This is a field toggle rather than
        /// <c>MonoBehaviour.enabled</c> because cues are played by direct calls from the
        /// scanner view — a disabled component still executes those, so disabling it would
        /// silence nothing.
        /// </summary>
        public void SetMuted(bool muted) => _enabled = !muted;

        /// <summary>Whether cues are currently audible.</summary>
        public bool IsMuted => !_enabled;

        /// <summary>Power-on: a rising two-tone with a soft noise wash under it.</summary>
        public void PlayBoot() => Play(_bootClip, ref _synthBoot, () => Sweep(0.55f, 320f, 880f, 0.22f, true));

        /// <summary>Power-off: the same interval, inverted and shorter.</summary>
        public void PlayPowerDown() => Play(_powerDownClip, ref _synthPowerDown, () => Sweep(0.3f, 720f, 240f, 0.18f, false));

        /// <summary>Recalculation tick. Deliberately tiny — it fires on every readout.</summary>
        public void PlayRefresh() => Play(_refreshClip, ref _synthRefresh, () => Blip(0.06f, 1480f, 0.10f));

        /// <summary>New severe threat. Two detuned tones, which the ear reads as an alarm.</summary>
        public void PlayThreat(ThreatLevel level)
        {
            var lethal = level == ThreatLevel.Lethal;
            if (lethal) Play(_threatClip, ref _synthThreatHigh, () => Alarm(0.34f, 620f, 0.30f));
            else Play(_threatClip, ref _synthThreatLow, () => Alarm(0.26f, 460f, 0.22f));
        }

        /// <summary>Large probability swing. Direction is audible: up rises, down falls.</summary>
        public void PlaySwing(float deltaPoints)
        {
            if (deltaPoints >= 0f) Play(_swingUpClip, ref _synthSwingUp, () => Sweep(0.22f, 660f, 1180f, 0.20f, false));
            else Play(_swingDownClip, ref _synthSwingDown, () => Sweep(0.24f, 700f, 380f, 0.20f, false));
        }

        /// <summary>Switch list re-ranked. A short double tick, quieter than the threat cue.</summary>
        public void PlayRerank() => Play(_rerankClip, ref _synthRerank, () => DoubleBlip(1180f, 1560f, 0.09f));

        /// <summary>New target acquired. The boot tone, shortened.</summary>
        public void PlayRetarget() => Play(_retargetClip, ref _synthRetarget, () => Sweep(0.3f, 420f, 980f, 0.18f, true));

        private void Play(AudioClip authored, ref AudioClip synthesised, System.Func<AudioClip> generate)
        {
            if (!_enabled || _source == null) return;
            var clip = authored;
            if (clip == null)
            {
                if (synthesised == null) synthesised = generate();
                clip = synthesised;
            }
            if (clip == null) return;
            _source.PlayOneShot(clip, _volume);
        }

        // ------------------------------------------------------------- synthesis

        /// <summary>A pitch sweep with an optional noise bed, for boot and swing cues.</summary>
        private static AudioClip Sweep(float seconds, float fromHz, float toHz, float amplitude, bool withNoise)
        {
            var count = Mathf.RoundToInt(seconds * SampleRate);
            var data = new float[count];
            var random = new System.Random(17);
            var phase = 0.0;

            for (var i = 0; i < count; i++)
            {
                var t = (float)i / count;
                // Exponential glide reads as a machine spinning up rather than a siren.
                var hz = Mathf.Lerp(fromHz, toHz, t * t);
                phase += 2.0 * Mathf.PI * hz / SampleRate;

                var envelope = Envelope(t, 0.06f, 0.45f);
                var value = Mathf.Sin((float)phase);
                // A quiet fifth above adds body without turning the cue into a note.
                value += 0.3f * Mathf.Sin((float)phase * 1.5f);
                if (withNoise) value += 0.12f * (float)(random.NextDouble() * 2.0 - 1.0) * (1f - t);

                data[i] = value * amplitude * envelope * 0.6f;
            }

            return Wrap("ScannerSweep", data);
        }

        /// <summary>A single short tone. The device's smallest utterance.</summary>
        private static AudioClip Blip(float seconds, float hz, float amplitude)
        {
            var count = Mathf.RoundToInt(seconds * SampleRate);
            var data = new float[count];
            for (var i = 0; i < count; i++)
            {
                var t = (float)i / count;
                var phase = 2f * Mathf.PI * hz * i / SampleRate;
                data[i] = Mathf.Sin(phase) * amplitude * Envelope(t, 0.02f, 0.7f);
            }
            return Wrap("ScannerBlip", data);
        }

        /// <summary>Two blips in sequence, for the re-rank cue.</summary>
        private static AudioClip DoubleBlip(float firstHz, float secondHz, float amplitude)
        {
            const float each = 0.055f;
            const float gap = 0.035f;
            var total = Mathf.RoundToInt((each * 2f + gap) * SampleRate);
            var firstCount = Mathf.RoundToInt(each * SampleRate);
            var secondStart = Mathf.RoundToInt((each + gap) * SampleRate);
            var data = new float[total];

            for (var i = 0; i < firstCount; i++)
            {
                var t = (float)i / firstCount;
                data[i] = Mathf.Sin(2f * Mathf.PI * firstHz * i / SampleRate) * amplitude * Envelope(t, 0.02f, 0.7f);
            }
            for (var i = 0; i < firstCount && secondStart + i < total; i++)
            {
                var t = (float)i / firstCount;
                data[secondStart + i] = Mathf.Sin(2f * Mathf.PI * secondHz * i / SampleRate) * amplitude * Envelope(t, 0.02f, 0.7f);
            }
            return Wrap("ScannerRerank", data);
        }

        /// <summary>
        /// Two tones a few hertz apart. The resulting beat frequency is what makes an alert
        /// sound urgent without being loud — the ear tracks the wobble, not the volume.
        /// </summary>
        private static AudioClip Alarm(float seconds, float hz, float amplitude)
        {
            var count = Mathf.RoundToInt(seconds * SampleRate);
            var data = new float[count];
            for (var i = 0; i < count; i++)
            {
                var t = (float)i / count;
                var a = Mathf.Sin(2f * Mathf.PI * hz * i / SampleRate);
                var b = Mathf.Sin(2f * Mathf.PI * (hz + 7f) * i / SampleRate);
                // Two pulses across the cue, so it reads as a signal rather than a tone.
                var gate = Mathf.Repeat(t * 2f, 1f) < 0.6f ? 1f : 0.25f;
                data[i] = (a + b) * 0.5f * amplitude * Envelope(t, 0.01f, 0.5f) * gate;
            }
            return Wrap("ScannerAlarm", data);
        }

        /// <summary>Attack-decay envelope in normalised time, with a soft tail.</summary>
        private static float Envelope(float t, float attack, float release)
        {
            if (t < attack) return t / attack;
            var releaseStart = 1f - release;
            if (t < releaseStart) return 1f;
            var k = (t - releaseStart) / release;
            return (1f - k) * (1f - k);
        }

        private static AudioClip Wrap(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            clip.hideFlags = HideFlags.HideAndDontSave;
            return clip;
        }
    }
}
