using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Audio
{
    /// <summary>
    /// Menu and dialogue sounds.
    ///
    /// The only interesting logic is the typewriter throttle. A dialogue box revealing
    /// forty characters a second would fire forty blips a second, which turns into a
    /// buzz; real games play the blip on roughly every third character and skip
    /// whitespace and punctuation entirely. That is what <see cref="PlayTypewriter"/>
    /// does, so the UI worker can call it per character without thinking about it.
    /// </summary>
    [AddComponentMenu("Poke Lab/Audio/UI Audio")]
    public sealed class UiAudio : MonoBehaviour, IUiSoundBank
    {
        [SerializeField, Range(1, 8)] private int typewriterEveryNth = 3;
        [Tooltip("Hard floor between blips regardless of character rate.")]
        [SerializeField, Range(0.01f, 0.2f)] private float typewriterMinInterval = 0.045f;
        [SerializeField, Range(0f, 0.3f)] private float typewriterPitchJitter = 0.08f;
        [SerializeField, Range(0f, 1f)] private float typewriterVolume = 0.5f;

        [Tooltip("Drop repeated navigate blips fired inside this window, so holding a " +
                 "direction scrolls rather than rattles.")]
        [SerializeField, Range(0f, 0.2f)] private float navigateMinInterval = 0.05f;

        private AudioDirector _audio;
        private int _charCounter;
        private float _lastTypewriterAt = -99f;
        private float _lastNavigateAt = -99f;

        private static UiAudio _instance;

        private void Awake()
        {
            // First one wins, and any later arrival removes itself. Every playable scene
            // carries its own GameHosts, so a band streamed in additively brings a second
            // copy whose Awake runs during the load; it took the hub slot and was then
            // stripped with the duplicate hosts, and every menu blip after that band load
            // was routed at a destroyed component.
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;
            ServiceHub.Register(this);
            // The Core-facing name. The UI assembly cannot reference this one, so every
            // menu and typewriter speaks through IUiSoundBank and finds this instance on
            // the hub; the concrete registration above stays for audio-side callers.
            ServiceHub.Register<IUiSoundBank>(this);
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            _instance = null;
            ServiceHub.Unregister(this);
            ServiceHub.Unregister<IUiSoundBank>(this);
        }

        private bool Ready()
        {
            if (_audio == null) AudioDirector.TryResolve(out _audio);
            return _audio != null;
        }

        public void PlayNavigate()
        {
            if (!Ready()) return;
            if (Time.unscaledTime - _lastNavigateAt < navigateMinInterval) return;
            _lastNavigateAt = Time.unscaledTime;
            _audio.PlayUi(AudioIds.UiNavigate, 0.8f, 1f + Random.Range(-0.03f, 0.03f));
        }

        public void PlayConfirm() { if (Ready()) _audio.PlayUi(AudioIds.UiConfirm); }
        public void PlayCancel() { if (Ready()) _audio.PlayUi(AudioIds.UiCancel); }
        public void PlayError() { if (Ready()) _audio.PlayUi(AudioIds.UiError, 0.9f); }
        public void PlayMenuOpen() { if (Ready()) _audio.PlayUi(AudioIds.UiMenuOpen); }
        public void PlayMenuClose() { if (Ready()) _audio.PlayUi(AudioIds.UiMenuClose); }

        /// <summary>
        /// Call once per revealed character. Whitespace and punctuation are silent, and
        /// only every nth remaining character sounds.
        /// </summary>
        public void PlayTypewriter(char revealed = 'a')
        {
            if (!Ready()) return;
            if (char.IsWhiteSpace(revealed) || char.IsPunctuation(revealed)) return;
            if (++_charCounter % Mathf.Max(1, typewriterEveryNth) != 0) return;
            if (Time.unscaledTime - _lastTypewriterAt < typewriterMinInterval) return;
            _lastTypewriterAt = Time.unscaledTime;
            _audio.PlayUi(AudioIds.UiTypewriter, typewriterVolume,
                          1f + Random.Range(-typewriterPitchJitter, typewriterPitchJitter));
        }

        /// <summary>Resets the character counter so each dialogue line starts on a blip.</summary>
        public void BeginDialogueLine() => _charCounter = 0;

        // ---- IUiSoundBank ------------------------------------------------------------
        // The Core contract's vocabulary, mapped one-to-one onto the methods above. Thin
        // on purpose: throttling and jitter stay in one place, and a UI caller pushing a
        // character per frame gets exactly the same discipline an audio-side caller does.

        void IUiSoundBank.Navigate() => PlayNavigate();
        void IUiSoundBank.Confirm() => PlayConfirm();
        void IUiSoundBank.Cancel() => PlayCancel();
        void IUiSoundBank.Error() => PlayError();
        void IUiSoundBank.MenuOpen() => PlayMenuOpen();
        void IUiSoundBank.MenuClose() => PlayMenuClose();
        void IUiSoundBank.TypewriterTick(char revealed) => PlayTypewriter(revealed);
        void IUiSoundBank.BeginTypewriterLine() => BeginDialogueLine();
    }
}
