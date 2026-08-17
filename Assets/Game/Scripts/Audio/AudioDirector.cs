using System;
using System.Collections;
using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;
using UnityEngine.Audio;

namespace PokeLab.Audio
{

    /// <summary>
    /// Owns the mixer, the volume model, the ducking state and every pooled AudioSource.
    ///
    /// Degrading gracefully is a hard requirement here, because this system boots before
    /// most of the others exist: with no mixer assigned it falls back to per-source gain,
    /// with no catalogue assigned every call becomes a no-op and logs once, and every
    /// cross-system lookup goes through <see cref="ServiceHub.TryGet{T}"/>.
    /// </summary>
    [AddComponentMenu("Poke Lab/Audio/Audio Director")]
    [DefaultExecutionOrder(-500)]
    public sealed class AudioDirector : MonoBehaviour, IGameAudio
    {
        // ---- exposed mixer parameter names; these must match GameMixer.mixer ----------
        public const string ParamMaster = "MasterVolume";
        public const string ParamMusic = "MusicVolume";
        public const string ParamSfx = "SfxVolume";
        public const string ParamAmbience = "AmbienceVolume";
        public const string ParamUi = "UiVolume";

        public const string GroupMaster = "Master";
        public const string GroupMusic = "Music";
        public const string GroupSfx = "SFX";
        public const string GroupAmbience = "Ambience";
        public const string GroupUi = "UI";

        [Header("Assets")]
        [Tooltip("Assets/Game/Audio/GameMixer.mixer. Optional: without it, volumes are applied per source.")]
        [SerializeField] private AudioMixer mixer;
        [Tooltip("Built from audio_manifest.json via Tools > Poke Lab > Audio > Rebuild Catalogue.")]
        [SerializeField] private AudioClipCatalog catalog;

        [Header("Pool sizes")]
        [SerializeField, Range(4, 64)] private int sfxVoices = 24;
        [SerializeField, Range(2, 32)] private int spatialVoices = 12;
        [SerializeField, Range(2, 16)] private int uiVoices = 6;

        [Header("Default levels (linear 0-1)")]
        [SerializeField, Range(0f, 1f)] private float masterVolume = 1f;
        [SerializeField, Range(0f, 1f)] private float musicVolume = 0.72f;
        [SerializeField, Range(0f, 1f)] private float sfxVolume = 0.85f;
        [SerializeField, Range(0f, 1f)] private float ambienceVolume = 0.6f;
        [SerializeField, Range(0f, 1f)] private float uiVolume = 0.7f;

        [Header("Ducking")]
        [Tooltip("Shape of the dip and the recovery. Evaluated 0 (unducked) to 1 (fully ducked).")]
        [SerializeField]
        private AnimationCurve duckCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        private readonly Dictionary<AudioBus, float> _busVolume = new Dictionary<AudioBus, float>();
        private readonly Dictionary<AudioBus, AudioMixerGroup> _groups =
            new Dictionary<AudioBus, AudioMixerGroup>();

        private AudioSourcePool _sfxPool;
        private AudioSourcePool _spatialPool;
        private AudioSourcePool _uiPool;
        private AudioSourcePool _loopPool;

        private float _duckTarget;      // 0 = no duck, 1 = full duck
        private float _duckCurrent;
        private float _duckSpeed = 4f;
        private int _scopedDucks;
        private Coroutine _timedDuck;
        private bool _warnedNoCatalog;

        /// <summary>
        /// Linear gain the music loops should currently be multiplied by.
        /// <see cref="MusicDirector"/> applies this to its *loop* sources only, so a
        /// sting or fanfare playing on the music bus is not ducked by its own arrival.
        /// </summary>
        public float MusicDuckGain { get; private set; } = 1f;

        /// <summary>Raised whenever <see cref="MusicDuckGain"/> changes.</summary>
        public event Action<float> MusicDuckChanged;

        public AudioClipCatalog Catalog => catalog;
        public AudioMixer Mixer => mixer;

        public AudioMixerGroup GroupFor(AudioBus bus) =>
            _groups.TryGetValue(bus, out var g) ? g : null;

        // ------------------------------------------------------------------------------

        private void Awake()
        {
            ResolveGroups();

            _busVolume[AudioBus.Master] = masterVolume;
            _busVolume[AudioBus.Music] = musicVolume;
            _busVolume[AudioBus.Sfx] = sfxVolume;
            _busVolume[AudioBus.Ambience] = ambienceVolume;
            _busVolume[AudioBus.Ui] = uiVolume;

            _sfxPool = new AudioSourcePool(transform, "SfxVoices", sfxVoices, GroupFor(AudioBus.Sfx));
            _spatialPool = new AudioSourcePool(transform, "SpatialVoices", spatialVoices,
                                               GroupFor(AudioBus.Sfx), spatial: true);
            _uiPool = new AudioSourcePool(transform, "UiVoices", uiVoices, GroupFor(AudioBus.Ui));
            _loopPool = new AudioSourcePool(transform, "LoopVoices", 12, GroupFor(AudioBus.Sfx));

            foreach (var kv in _busVolume) ApplyBusVolume(kv.Key, kv.Value);

            ServiceHub.Register<IGameAudio>(this);
            ServiceHub.Register(this);

            // Zero-coupling escape hatch for the cinematics layer. CinematicHooks probes
            // ServiceHub for exactly Action<string, Vector3> when no ICinematicAudioHook is
            // registered, so the delegate must have that shape -- the Action<string> this used
            // to register was dead code the probe could never find. The position is accepted
            // and ignored: battle cues read fine flat, and the 3D pool is for the overworld.
            // Gated on HasClip because the hook vocabulary ("sfx_hit_neutral"...) is not the
            // catalogue vocabulary; an unmapped cue must degrade to silence here, not to a
            // warning per swing of every battle. Belt-and-braces only -- the real mapping
            // lives in CinematicAudioHookHost, and this fires solely when that host is absent.
            ServiceHub.Register<Action<string, Vector3>>((cueId, position) =>
            {
                if (HasClip(cueId)) PlaySfx(cueId);
            });
        }

        private void OnDestroy()
        {
            _sfxPool?.StopAll();
            _spatialPool?.StopAll();
            _uiPool?.StopAll();
            _loopPool?.StopAll();
        }

        private void ResolveGroups()
        {
            _groups.Clear();
            if (mixer == null) return;
            TryBind(AudioBus.Master, GroupMaster);
            TryBind(AudioBus.Music, GroupMusic);
            TryBind(AudioBus.Sfx, GroupSfx);
            TryBind(AudioBus.Ambience, GroupAmbience);
            TryBind(AudioBus.Ui, GroupUi);
        }

        private void TryBind(AudioBus bus, string groupName)
        {
            var found = mixer.FindMatchingGroups(groupName);
            if (found != null && found.Length > 0) _groups[bus] = found[0];
            else Debug.LogWarning($"[AudioDirector] Mixer group '{groupName}' not found; " +
                                  $"{bus} will play unrouted at source gain.", this);
        }

        private void Update()
        {
            if (!Mathf.Approximately(_duckCurrent, _duckTarget))
            {
                _duckCurrent = Mathf.MoveTowards(_duckCurrent, _duckTarget,
                                                 _duckSpeed * Time.unscaledDeltaTime);
                UpdateDuckGain();
            }
        }

        private void UpdateDuckGain()
        {
            float shaped = duckCurve.Evaluate(Mathf.Clamp01(_duckCurrent));
            float gain = Mathf.Lerp(1f, 1f - _activeDuckAmount, shaped);
            if (Mathf.Approximately(gain, MusicDuckGain)) return;
            MusicDuckGain = gain;
            MusicDuckChanged?.Invoke(gain);
        }

        private float _activeDuckAmount = 0.55f;

        // ---- volume ------------------------------------------------------------------

        public void SetBusVolume(AudioBus bus, float linear01)
        {
            linear01 = Mathf.Clamp01(linear01);
            _busVolume[bus] = linear01;
            ApplyBusVolume(bus, linear01);
        }

        public float GetBusVolume(AudioBus bus) =>
            _busVolume.TryGetValue(bus, out var v) ? v : 1f;

        private void ApplyBusVolume(AudioBus bus, float linear01)
        {
            if (mixer == null) return;
            mixer.SetFloat(ParamFor(bus), LinearToDb(linear01));
        }

        public static string ParamFor(AudioBus bus)
        {
            switch (bus)
            {
                case AudioBus.Music: return ParamMusic;
                case AudioBus.Sfx: return ParamSfx;
                case AudioBus.Ambience: return ParamAmbience;
                case AudioBus.Ui: return ParamUi;
                default: return ParamMaster;
            }
        }

        /// <summary>
        /// Slider position to mixer decibels. A linear slider mapped straight to dB feels
        /// wrong at both ends, so this is the usual logarithmic law with a hard -80 dB
        /// floor that reads as true silence.
        /// </summary>
        public static float LinearToDb(float linear01)
        {
            if (linear01 <= 0.0001f) return -80f;
            return Mathf.Log10(Mathf.Clamp01(linear01)) * 20f;
        }

        // ---- playback ----------------------------------------------------------------

        public bool HasClip(string clipName) =>
            catalog != null && catalog.TryGet(clipName, out _);

        private bool Resolve(string clipName, out AudioClipCatalog.Entry entry)
        {
            entry = default;
            if (catalog == null)
            {
                if (!_warnedNoCatalog)
                {
                    _warnedNoCatalog = true;
                    Debug.LogWarning("[AudioDirector] No AudioClipCatalog assigned; " +
                                     "all audio calls are no-ops until one is.", this);
                }
                return false;
            }
            if (!catalog.TryGet(clipName, out entry) || entry.Clip == null)
            {
                Debug.LogWarning($"[AudioDirector] Clip '{clipName}' not in catalogue.", this);
                return false;
            }
            return true;
        }

        public void PlaySfx(string clipName, float volume = 1f, float pitch = 1f)
        {
            if (!Resolve(clipName, out var e)) return;
            var src = _sfxPool.Rent();
            if (src == null) return;
            src.outputAudioMixerGroup = GroupFor(e.Bus == AudioBus.Master ? AudioBus.Sfx : e.Bus);
            src.spatialBlend = 0f;
            src.pitch = pitch;
            src.volume = volume * SafeGain(e) * SourceFallbackGain(e.Bus);
            src.PlayOneShot(e.Clip);
        }

        public void PlaySfxAt(string clipName, Vector3 worldPosition, float volume = 1f,
                              float pitch = 1f)
        {
            if (!Resolve(clipName, out var e)) return;
            var src = _spatialPool.Rent();
            if (src == null) return;
            src.transform.position = worldPosition;
            src.outputAudioMixerGroup = GroupFor(AudioBus.Sfx);
            src.spatialBlend = 1f;
            src.pitch = pitch;
            src.volume = volume * SafeGain(e) * SourceFallbackGain(AudioBus.Sfx);
            src.PlayOneShot(e.Clip);
        }

        public void PlayUi(string clipName, float volume = 1f, float pitch = 1f)
        {
            if (!Resolve(clipName, out var e)) return;
            var src = _uiPool.Rent();
            if (src == null) return;
            src.outputAudioMixerGroup = GroupFor(AudioBus.Ui);
            src.spatialBlend = 0f;
            src.pitch = pitch;
            src.volume = volume * SafeGain(e) * SourceFallbackGain(AudioBus.Ui);
            src.PlayOneShot(e.Clip);
        }

        public AudioSource PlayLoop(string clipName, AudioBus bus, float volume = 1f,
                                    bool spatial = false)
        {
            if (!Resolve(clipName, out var e)) return null;
            var src = _loopPool.RentReserved();
            if (src == null)
            {
                Debug.LogWarning("[AudioDirector] Loop pool exhausted; ignoring " + clipName, this);
                return null;
            }
            src.outputAudioMixerGroup = GroupFor(bus);
            src.clip = e.Clip;
            src.loop = true;
            src.spatialBlend = spatial ? 1f : 0f;
            src.volume = volume * SafeGain(e) * SourceFallbackGain(bus);
            src.Play();
            return src;
        }

        public void StopLoop(AudioSource source, float fadeSeconds = 0.25f)
        {
            if (source == null) return;
            if (fadeSeconds <= 0f || !isActiveAndEnabled)
            {
                _loopPool.Release(source);
                return;
            }
            StartCoroutine(FadeOutAndRelease(source, fadeSeconds));
        }

        private IEnumerator FadeOutAndRelease(AudioSource src, float seconds)
        {
            float start = src.volume;
            float t = 0f;
            while (t < seconds && src != null)
            {
                t += Time.unscaledDeltaTime;
                src.volume = Mathf.Lerp(start, 0f, t / seconds);
                yield return null;
            }
            _loopPool.Release(src);
        }

        private static float SafeGain(AudioClipCatalog.Entry e) => e.Gain <= 0f ? 1f : e.Gain;

        /// <summary>
        /// When no mixer is assigned there is nothing applying the bus volume, so fold it
        /// into the source gain instead. With a mixer this returns 1 and the mixer owns it.
        /// </summary>
        private float SourceFallbackGain(AudioBus bus) =>
            mixer == null ? GetBusVolume(bus) * GetBusVolume(AudioBus.Master) : 1f;

        // ---- ducking -----------------------------------------------------------------

        public void DuckMusic(float amount01, float attack, float hold, float release)
        {
            if (_timedDuck != null) StopCoroutine(_timedDuck);
            _timedDuck = StartCoroutine(TimedDuck(Mathf.Clamp01(amount01), attack, hold, release));
        }

        private IEnumerator TimedDuck(float amount, float attack, float hold, float release)
        {
            _activeDuckAmount = Mathf.Max(_activeDuckAmount, amount);
            _duckSpeed = 1f / Mathf.Max(0.01f, attack);
            _duckTarget = 1f;
            yield return new WaitForSecondsRealtime(attack + Mathf.Max(0f, hold));

            if (_scopedDucks == 0)
            {
                _duckSpeed = 1f / Mathf.Max(0.01f, release);
                _duckTarget = 0f;
            }
            _timedDuck = null;
        }

        public IDisposable DuckMusicScope(float amount01 = 0.55f, float attack = 0.25f,
                                          float release = 0.6f)
        {
            _scopedDucks++;
            _activeDuckAmount = Mathf.Max(_activeDuckAmount, Mathf.Clamp01(amount01));
            _duckSpeed = 1f / Mathf.Max(0.01f, attack);
            _duckTarget = 1f;
            return new DuckHandle(this, release);
        }

        private void EndScope(float release)
        {
            _scopedDucks = Mathf.Max(0, _scopedDucks - 1);
            if (_scopedDucks > 0 || _timedDuck != null) return;
            _duckSpeed = 1f / Mathf.Max(0.01f, release);
            _duckTarget = 0f;
        }

        private sealed class DuckHandle : IDisposable
        {
            private AudioDirector _owner;
            private readonly float _release;

            public DuckHandle(AudioDirector owner, float release)
            {
                _owner = owner;
                _release = release;
            }

            public void Dispose()
            {
                if (_owner == null) return;
                _owner.EndScope(_release);
                _owner = null;
            }
        }

        // ---- convenience for other systems in this assembly --------------------------

        /// <summary>Resolves the director without throwing during partial integration.</summary>
        public static bool TryResolve(out AudioDirector director) =>
            ServiceHub.TryGet(out director);

        public string DebugVoiceReport() =>
            $"sfx {_sfxPool?.ActiveCount()}/{_sfxPool?.Capacity}  " +
            $"3d {_spatialPool?.ActiveCount()}/{_spatialPool?.Capacity}  " +
            $"ui {_uiPool?.ActiveCount()}/{_uiPool?.Capacity}  " +
            $"loop {_loopPool?.ActiveCount()}/{_loopPool?.Capacity}  duck {MusicDuckGain:0.00}";
    }
}
