using System.Collections;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Audio
{
    /// <summary>
    /// The Poké Lab scanner's voice.
    ///
    /// The scanner cues were synthesised as one family and this class is what keeps them
    /// sounding like one device in play: every cue is an FM sine on the same F# pentatonic
    /// over the same servo bed, nothing here plays a sound from outside that set, and the
    /// idle scan loop runs underneath the whole panel so even silence has the device's
    /// floor tone in it. That continuity is what separates "precision equipment" from a
    /// pile of UI beeps.
    ///
    /// It implements <see cref="IPokeLabListener"/> so it can be attached straight to the
    /// oracle's readout stream. If nothing pushes readouts to it, every method here is
    /// also callable directly by the UI worker.
    /// </summary>
    [AddComponentMenu("Poke Lab/Audio/Scanner Audio")]
    public sealed class ScannerAudio : MonoBehaviour, IPokeLabListener
    {
        [Header("Idle loop")]
        [SerializeField, Range(0f, 1f)] private float scanLoopVolume = 0.45f;
        [SerializeField, Range(0.05f, 3f)] private float scanLoopFade = 0.6f;

        [Header("Data blips")]
        [Tooltip("Minimum gap between resolve blips. Below this they are dropped rather " +
                 "than queued, so a panel populating in one frame does not machine-gun.")]
        [SerializeField, Range(0.01f, 0.5f)] private float blipMinInterval = 0.06f;

        [Header("Probability shift")]
        [Tooltip("Delta in percentage points that counts as a full-intensity swing.")]
        [SerializeField, Range(1f, 40f)] private float fullSwingPoints = 18f;
        [Tooltip("Deltas smaller than this are not worth a sound.")]
        [SerializeField, Range(0f, 5f)] private float deadZonePoints = 0.75f;
        [SerializeField, Range(0f, 1f)] private float shiftVolumeFloor = 0.35f;

        [Header("Threat alert")]
        [SerializeField, Range(0f, 30f)] private float threatRepeatCooldown = 6f;

        private AudioDirector _audio;
        private AudioSource _scanLoop;
        private float _lastBlipAt = -99f;
        private float _lastThreatAt = -99f;
        private int _blipIndex;
        private ThreatLevel _lastThreatLevel = ThreatLevel.Trivial;
        private bool _wasConfident;
        private bool _isOpen;

        public bool IsOpen => _isOpen;

        private void Awake() => ServiceHub.Register(this);

        private void OnDisable() => StopScanLoop();

        private bool Ready()
        {
            if (_audio == null) AudioDirector.TryResolve(out _audio);
            return _audio != null;
        }

        // ---- lifecycle ---------------------------------------------------------------

        /// <summary>Scanner raised. Boot sequence, then the idle loop fades in under it.</summary>
        public void Open()
        {
            if (_isOpen || !Ready()) return;
            _isOpen = true;
            _lastThreatLevel = ThreatLevel.Trivial;
            _wasConfident = false;
            _audio.PlaySfx(AudioIds.ScannerBoot);
            StartCoroutine(StartScanLoopAfter(0.55f));
        }

        /// <summary>Scanner lowered.</summary>
        public void Close()
        {
            if (!_isOpen) return;
            _isOpen = false;
            StopScanLoop();
            if (Ready()) _audio.PlaySfx(AudioIds.UiMenuClose, 0.7f, 0.92f);
        }

        private IEnumerator StartScanLoopAfter(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (!_isOpen || !Ready()) yield break;
            if (_scanLoop != null) yield break;
            _scanLoop = _audio.PlayLoop(AudioIds.ScannerScanLoop, AudioBus.Ui, 0f);
            if (_scanLoop == null) yield break;

            float t = 0f;
            while (t < scanLoopFade && _scanLoop != null)
            {
                t += Time.unscaledDeltaTime;
                _scanLoop.volume = Mathf.Lerp(0f, scanLoopVolume, t / scanLoopFade);
                yield return null;
            }
        }

        private void StopScanLoop()
        {
            if (_scanLoop == null || _audio == null) return;
            _audio.StopLoop(_scanLoop, scanLoopFade);
            _scanLoop = null;
        }

        // ---- cues --------------------------------------------------------------------

        /// <summary>One readout field has resolved. Call once per field as the panel fills.</summary>
        public void PlayDataBlip()
        {
            if (!Ready()) return;
            if (Time.unscaledTime - _lastBlipAt < blipMinInterval) return;
            _lastBlipAt = Time.unscaledTime;
            var clip = AudioIds.ScannerDataBlips[_blipIndex % AudioIds.ScannerDataBlips.Length];
            _blipIndex++;
            _audio.PlayUi(clip, 0.6f);
        }

        public void PlayThreatAlert(ThreatLevel level)
        {
            if (!Ready()) return;
            if (Time.unscaledTime - _lastThreatAt < threatRepeatCooldown) return;
            _lastThreatAt = Time.unscaledTime;
            // Lethal is a semitone up and louder than Dangerous; below Dangerous, silence.
            float volume = level == ThreatLevel.Lethal ? 1f : 0.78f;
            float pitch = level == ThreatLevel.Lethal ? 1.06f : 1f;
            _audio.PlayUi(AudioIds.ScannerThreatAlert, volume, pitch);
        }

        public void PlayRecommendation()
        {
            if (!Ready()) return;
            _audio.PlayUi(AudioIds.ScannerRecommendation, 0.85f);
        }

        /// <summary>
        /// The probability tone.
        ///
        /// One clip rises and one falls; the magnitude of the delta drives pitch and level
        /// rather than selecting between separate clips, so a 2-point drift and a 20-point
        /// swing are recognisably the same event at different intensities. Deltas inside
        /// the dead zone make no sound at all, because a scanner that chirps at every
        /// recalculation stops meaning anything.
        /// </summary>
        public void PlayProbabilityShift(float deltaPoints)
        {
            if (!Ready()) return;
            float magnitude = Mathf.Abs(deltaPoints);
            if (magnitude < deadZonePoints) return;

            float intensity = Mathf.Clamp01(magnitude / Mathf.Max(0.01f, fullSwingPoints));
            string clip = deltaPoints > 0f
                ? AudioIds.ScannerProbabilityUp
                : AudioIds.ScannerProbabilityDown;

            // Pitch tracks intensity in the direction of travel: a big gain sits higher,
            // a big loss sits lower, so the size of the move is audible as well as its sign.
            float pitch = 1f + Mathf.Sign(deltaPoints) * Mathf.Lerp(0f, 0.16f, intensity);
            float volume = Mathf.Lerp(shiftVolumeFloor, 1f, intensity);
            _audio.PlayUi(clip, volume, pitch);
        }

        // ---- IPokeLabListener --------------------------------------------------------

        /// <summary>
        /// Drives the whole scanner voice from one readout: the probability tone for the
        /// delta, an alert if the worst threat has escalated, and the recommendation chime
        /// the first time the readout becomes confident. Escalation rather than absolute
        /// level is what gates the alert, so a persistent Lethal threat alarms once.
        /// </summary>
        public void OnReadoutUpdated(TacticalReadout readout)
        {
            if (readout == null || !_isOpen) return;

            PlayProbabilityShift(readout.DeltaPoints);

            var worst = ThreatLevel.Trivial;
            if (readout.Threats != null)
            {
                for (int i = 0; i < readout.Threats.Length; i++)
                    if (readout.Threats[i].Level > worst) worst = readout.Threats[i].Level;
            }
            if (worst > _lastThreatLevel && worst >= ThreatLevel.Dangerous) PlayThreatAlert(worst);
            _lastThreatLevel = worst;

            if (readout.IsConfident && !_wasConfident && !string.IsNullOrEmpty(readout.RecommendedLine))
                PlayRecommendation();
            _wasConfident = readout.IsConfident;
        }
    }
}
