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
        // Master at 0.5, on the user's instruction ("볼륨 크기 50%로 줄여줘").
        //
        // The cut is applied to MASTER alone and the per-bus balance below is left exactly as
        // it was. That balance is a mix — music sitting under effects, ambience under both —
        // and scaling every bus by half would have preserved nothing except the arithmetic
        // while making each slider mean something different from what it says. Halving the one
        // bus that everything passes through turns the whole game down and keeps the mix.
        //
        // These are DEFAULTS, not the live values: LoadVolume reads PlayerPrefs over the top
        // of them in Awake, so a player who has moved a slider is not reset by this.
        [SerializeField, Range(0f, 1f)] private float masterVolume = 0.5f;
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

        /// <summary>Depth of every scope that is still holding, one entry per live scope.</summary>
        private readonly List<float> _scopedDucks = new List<float>();
        /// <summary>Depth of the timed duck, 0 when none is holding.</summary>
        private float _timedDuckAmount;
        /// <summary>Deepest request currently held. 0 when nothing is ducking.</summary>
        private float _activeDuckAmount;
        /// <summary>The depth the gain is actually using; glides towards the one above.</summary>
        private float _amountCurrent;

        private Coroutine _timedDuck;
        private bool _warnedNoCatalog;
        private Action<string, Vector3> _cue;

        /// <summary>
        /// Linear gain the music loops should currently be multiplied by.
        /// <see cref="MusicDirector"/> applies this to its *loop* sources only, so a
        /// sting or fanfare playing on the music bus is not ducked by its own arrival.
        /// </summary>
        public float MusicDuckGain { get; private set; } = 1f;

        /// <summary>Raised whenever <see cref="MusicDuckGain"/> changes.</summary>
        public event Action<float> MusicDuckChanged;

        /// <summary>
        /// The clip catalogue, loaded on first use if nothing assigned one.
        ///
        /// Resolved through the property rather than only inside Resolve, because MusicDirector
        /// reads this directly to look a track up — so a lazy load buried in Resolve fixed the
        /// sound effects and left every piece of music reporting "missing from catalogue"
        /// against a catalogue that had simply never been fetched. One accessor, one place the
        /// loading happens.
        /// </summary>
        public AudioClipCatalog Catalog =>
            catalog != null ? catalog : (catalog = Resources.Load<AudioClipCatalog>(CatalogResourceName));

        /// <summary>Name the catalogue is loaded under. It lives in a Resources folder because
        /// no scene references it, and that is the only lookup a build has.</summary>
        public const string CatalogResourceName = "AudioClipCatalog";
        public AudioMixer Mixer => mixer;

        public AudioMixerGroup GroupFor(AudioBus bus) =>
            _groups.TryGetValue(bus, out var g) ? g : null;

        // ------------------------------------------------------------------------------

        private static AudioDirector _instance;

        /// <summary>
        /// Gives the scene ears if nobody else did.
        ///
        /// An <see cref="AudioListener"/> was never placed anywhere in the project — not in a
        /// scene, not on a prefab, not in code — so Unity warned once about having none and
        /// then played silence, which looks like every audio bug except the one it is. The
        /// camera is where it belongs: positional sound is mixed relative to the view, and the
        /// rig rebuild puts one there too, so this only has to cover scenes built before that.
        /// </summary>
        private static void EnsureListener()
        {
            if (FindFirstObjectByType<AudioListener>(FindObjectsInactive.Include) != null) return;

            var camera = Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[Audio] No AudioListener and no main camera to put one on, " +
                                 "so this scene plays silently.");
                return;
            }

            camera.gameObject.AddComponent<AudioListener>();
        }

        private void Awake()
        {
            // First one wins, and any later arrival removes itself.
            //
            // Every playable scene carries its own GameHosts, so a band streamed in
            // additively brings a second director whose Awake runs *during* the load. It
            // used to overwrite all three hub registrations and was then stripped with the
            // rest of the duplicate hosts, leaving the hub pointing at a destroyed director
            // whose voice pools no longer exist: every consumer that resolved audio after
            // that band load was silent for the remainder of the session.
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;

            EnsureListener();
            ResolveGroups();

            DropStaleOverrides();

            // The serialized fields are DEFAULTS; a value the player has actually chosen wins.
            // Without this the settings screen would move the mixer for one session and forget
            // everything the moment the game was reopened, which is worse than having no
            // settings screen at all — it looks like it worked.
            _busVolume[AudioBus.Master] = LoadVolume(AudioBus.Master, masterVolume);
            _busVolume[AudioBus.Music] = LoadVolume(AudioBus.Music, musicVolume);
            _busVolume[AudioBus.Sfx] = LoadVolume(AudioBus.Sfx, sfxVolume);
            _busVolume[AudioBus.Ambience] = LoadVolume(AudioBus.Ambience, ambienceVolume);
            _busVolume[AudioBus.Ui] = LoadVolume(AudioBus.Ui, uiVolume);

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
            //
            // Kept in a field rather than written inline so OnDestroy can hand back this
            // exact delegate; the hub only drops a registration that is still ours.
            _cue = (cueId, position) => { if (HasClip(cueId)) PlaySfx(cueId); };
            ServiceHub.Register(_cue);

            StartCoroutine(WarmCatalog());
        }

        /// <summary>
        /// Starts the background load of every one-shot clip in the catalogue, a few per
        /// frame. Nothing is engine-preloaded any more — on the web the engine's own
        /// scene-load preload asked every SFX clip its length before the browser had
        /// decoded it, one warning per clip before any script ran — so residency is this
        /// director's job now. Kicks are paced so boot never issues ninety decode
        /// requests in one frame; the set is resident within a couple of seconds, well
        /// before gameplay input exists. Music is left out: the decks load their own
        /// tracks on demand, and decoding whole songs up front is memory the web build
        /// does not have. Streaming clips are skipped for the same reason they are never
        /// load-gated off-web — they buffer for themselves.
        /// </summary>
        private IEnumerator WarmCatalog()
        {
            var cat = Catalog;
            if (cat == null) yield break;

            int kicked = 0;
            for (int i = 0; i < cat.Entries.Count; i++)
            {
                var e = cat.Entries[i];
                if (e.Clip == null || e.Bus == AudioBus.Music) continue;
                if (e.Clip.loadType == AudioClipLoadType.Streaming) continue;
                if (e.Clip.loadState != AudioDataLoadState.Unloaded) continue;
                e.Clip.LoadAudioData();
                if (++kicked % WarmClipsPerFrame == 0) yield return null;
            }
        }

        /// <summary>Decode kicks issued per frame by <see cref="WarmCatalog"/>.</summary>
        private const int WarmClipsPerFrame = 8;

        private void OnDestroy()
        {
            _sfxPool?.StopAll();
            _spatialPool?.StopAll();
            _uiPool?.StopAll();
            _loopPool?.StopAll();

            // Only the owning instance gives the services back, and only while they are
            // still its own: a duplicate standing down must not deregister the live
            // director, and neither must the outgoing scene's copy once a new one has
            // taken the slot.
            if (_instance != this) return;
            _instance = null;
            ServiceHub.Unregister<IGameAudio>(this);
            ServiceHub.Unregister(this);
            if (_cue != null) ServiceHub.Unregister(_cue);
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
            bool settled = Mathf.Approximately(_duckCurrent, _duckTarget) &&
                           Mathf.Approximately(_amountCurrent, _activeDuckAmount);
            if (settled) return;

            float step = _duckSpeed * Time.unscaledDeltaTime;
            _duckCurrent = Mathf.MoveTowards(_duckCurrent, _duckTarget, step);
            // The depth travels at the same rate as the dip itself, so a second duck
            // arriving on top of the first deepens the music rather than stepping it
            // down, and handing one back lets the music rise to whatever is still
            // holding instead of snapping up to it.
            _amountCurrent = Mathf.MoveTowards(_amountCurrent, _activeDuckAmount, step);
            UpdateDuckGain();
        }

        private void UpdateDuckGain()
        {
            float shaped = duckCurve.Evaluate(Mathf.Clamp01(_duckCurrent));
            float gain = Mathf.Lerp(1f, 1f - _amountCurrent, shaped);
            if (Mathf.Approximately(gain, MusicDuckGain)) return;
            MusicDuckGain = gain;
            MusicDuckChanged?.Invoke(gain);
        }

        // ---- volume ------------------------------------------------------------------

        public void SetBusVolume(AudioBus bus, float linear01)
        {
            linear01 = Mathf.Clamp01(linear01);
            _busVolume[bus] = linear01;
            ApplyBusVolume(bus, linear01);
            SaveVolume(bus, linear01);
        }

        /// <summary>Where one bus's chosen level is remembered between sessions.</summary>
        private static string PrefKeyFor(AudioBus bus) => "pokelab.audio.volume." + bus;

        /// <summary>
        /// Bumped whenever the shipped default levels change. See <see cref="DropStaleOverrides"/>.
        /// </summary>
        private const int DefaultsVersion = 2;

        private const string DefaultsVersionKey = "pokelab.audio.defaultsVersion";

        /// <summary>
        /// Forgets remembered levels that were remembered against DIFFERENT defaults.
        ///
        /// A stored preference always beating the serialized value is right until the shipped
        /// value moves, and then it is exactly wrong: master went to 0.5 because the user asked
        /// for 볼륨 크기 50%, and every player who had already opened the game once had 1.0
        /// sitting in PlayerPrefs from the first boot — so the change did nothing for the only
        /// people it was made for, and the game stayed loud. That is not a hypothetical; it is
        /// what the report 메인 메뉴 사운드가 너무 큼. 50% 줄인게 맞나 was describing, on a build
        /// where the new default was in the source and in five scenes.
        ///
        /// Clearing rather than rewriting: the point is to make the fields authoritative again,
        /// and LoadVolume already falls back to them. A player who has since dragged a slider
        /// loses that drag once, at the version bump, which is the cost of shipping a new
        /// default at all.
        /// </summary>
        private static void DropStaleOverrides()
        {
            if (PlayerPrefs.GetInt(DefaultsVersionKey, 0) == DefaultsVersion) return;

            foreach (AudioBus bus in System.Enum.GetValues(typeof(AudioBus)))
                PlayerPrefs.DeleteKey(PrefKeyFor(bus));

            PlayerPrefs.SetInt(DefaultsVersionKey, DefaultsVersion);
            PlayerPrefs.Save();
        }

        private static float LoadVolume(AudioBus bus, float fallback) =>
            Mathf.Clamp01(PlayerPrefs.GetFloat(PrefKeyFor(bus), fallback));

        /// <summary>
        /// Written on every change rather than on quit.
        ///
        /// A settings screen is dragged, not committed — there is no OK button to hang a save
        /// on — and the web build has no reliable quit at all: a browser tab can be closed
        /// without OnApplicationQuit ever running. Writing a float per drag is cheap and is the
        /// only version of this that survives the way the game is actually left.
        /// </summary>
        private static void SaveVolume(AudioBus bus, float linear01)
        {
            PlayerPrefs.SetFloat(PrefKeyFor(bus), linear01);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Puts every bus back to the value the build shipped with, and forgets the overrides.
        ///
        /// The settings screen needs this because a player who has dragged master to zero has
        /// no way to hear their way back — silence gives no feedback that anything is moving.
        /// </summary>
        public void ResetVolumesToDefaults()
        {
            SetBusVolume(AudioBus.Master, masterVolume);
            SetBusVolume(AudioBus.Music, musicVolume);
            SetBusVolume(AudioBus.Sfx, sfxVolume);
            SetBusVolume(AudioBus.Ambience, ambienceVolume);
            SetBusVolume(AudioBus.Ui, uiVolume);
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
            Catalog != null && Catalog.TryGet(clipName, out _);

        private bool Resolve(string clipName, out AudioClipCatalog.Entry entry)
        {
            entry = default;

            if (Catalog == null)
            {
                if (!_warnedNoCatalog)
                {
                    _warnedNoCatalog = true;
                    Debug.LogWarning("[AudioDirector] No AudioClipCatalog assigned; " +
                                     "all audio calls are no-ops until one is.", this);
                }
                return false;
            }
            if (!Catalog.TryGet(clipName, out entry) || entry.Clip == null)
            {
                Debug.LogWarning($"[AudioDirector] Clip '{clipName}' not in catalogue.", this);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Whether a clip may start right now. A one-shot whose data has not arrived is
        /// dropped — a missed click beats a logged warning — and the kick issued here
        /// means the next trigger finds it resident; loops defer instead (see
        /// <see cref="StartLoopWhenLoaded"/>). A clip the kick cannot touch (off-web
        /// streaming buffers for itself) counts as ready, and a Failed one never does.
        /// </summary>
        private static bool ClipReady(AudioClip clip)
        {
            if (clip.loadState == AudioDataLoadState.Loaded) return true;
            if (clip.loadState != AudioDataLoadState.Unloaded) return false;
            clip.LoadAudioData();
            return clip.loadState == AudioDataLoadState.Unloaded;
        }

        public void PlaySfx(string clipName, float volume = 1f, float pitch = 1f)
        {
            if (!Resolve(clipName, out var e)) return;
            if (!ClipReady(e.Clip)) return;
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
            if (!ClipReady(e.Clip)) return;
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
            if (!ClipReady(e.Clip)) return;
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
            // The caller gets its handle either way; a clip still decoding starts the
            // frame its data lands, the way the ambience beds retry.
            if (ClipReady(e.Clip)) src.Play();
            else StartCoroutine(StartLoopWhenLoaded(src, e.Clip));
            return src;
        }

        /// <summary>
        /// The loop counterpart of the one-shot drop: the source is already rented and
        /// configured — the caller holds it — so it waits silent instead. Release ends
        /// the wait: the pool's Reset clears the clip, and that (or a re-rent onto
        /// another clip) is the signal to stand down.
        /// </summary>
        private IEnumerator StartLoopWhenLoaded(AudioSource src, AudioClip clip)
        {
            while (src != null && ReferenceEquals(src.clip, clip) && !src.isPlaying)
            {
                if (clip.loadState == AudioDataLoadState.Loaded) { src.Play(); yield break; }
                if (clip.loadState == AudioDataLoadState.Failed) yield break;
                yield return null;
            }
        }

        public void StopLoop(AudioSource source, float fadeSeconds = 0.25f)
        {
            if (source == null) return;
            // A loop still waiting on its data has made no sound to fade; releasing it
            // now clears its clip, which is what stands the waiting start down.
            if (fadeSeconds <= 0f || !isActiveAndEnabled || !source.isPlaying)
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
            _timedDuckAmount = amount;
            RefreshDuckAmount();
            _duckSpeed = 1f / Mathf.Max(0.01f, attack);
            _duckTarget = 1f;
            yield return new WaitForSecondsRealtime(attack + Mathf.Max(0f, hold));

            _timedDuck = null;
            _timedDuckAmount = 0f;
            RefreshDuckAmount();

            // A scope still holding keeps the music down -- at its own depth, not at the
            // one this duck asked for.
            if (_scopedDucks.Count > 0) yield break;
            _duckSpeed = 1f / Mathf.Max(0.01f, release);
            _duckTarget = 0f;
        }

        public IDisposable DuckMusicScope(float amount01 = 0.55f, float attack = 0.25f,
                                          float release = 0.6f)
        {
            float amount = Mathf.Clamp01(amount01);
            _scopedDucks.Add(amount);
            RefreshDuckAmount();
            _duckSpeed = 1f / Mathf.Max(0.01f, attack);
            _duckTarget = 1f;
            return new DuckHandle(this, amount, release);
        }

        private void EndScope(float amount, float release)
        {
            _scopedDucks.Remove(amount);
            RefreshDuckAmount();
            if (_scopedDucks.Count > 0 || _timedDuck != null) return;
            _duckSpeed = 1f / Mathf.Max(0.01f, release);
            _duckTarget = 0f;
        }

        /// <summary>
        /// Recomputes the dip from the requests that are actually still held.
        ///
        /// This used to be a high-water mark that nothing ever lowered, which meant the
        /// first victory fanfare left every later duck in the session dipping by three
        /// quarters, and a duck shallower than the initial value -- the authored 0.35
        /// dialogue dip among them -- could never be heard at all.
        /// </summary>
        private void RefreshDuckAmount()
        {
            float deepest = _timedDuckAmount;
            for (int i = 0; i < _scopedDucks.Count; i++)
                deepest = Mathf.Max(deepest, _scopedDucks[i]);
            _activeDuckAmount = deepest;
        }

        private sealed class DuckHandle : IDisposable
        {
            private AudioDirector _owner;
            private readonly float _amount;
            private readonly float _release;

            public DuckHandle(AudioDirector owner, float amount, float release)
            {
                _owner = owner;
                _amount = amount;
                _release = release;
            }

            public void Dispose()
            {
                if (_owner == null) return;
                _owner.EndScope(_amount, _release);
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
