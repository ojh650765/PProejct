using System.Collections;
using PokeLab.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Audio
{
    /// <summary>
    /// Sole music transport. Themes and fanfares share one source: fade down, replace,
    /// fade up. New requests cancel pending loads and returns from older cues.
    /// </summary>
    [AddComponentMenu("Poke Lab/Audio/Music Director")]
    [DefaultExecutionOrder(-400)]
    public sealed class MusicDirector : MonoBehaviour
    {
        [SerializeField] private float biomeFade=1f;
        [SerializeField] private float timeOfDayFade=1.5f;
        [SerializeField] private float battleFade=.35f;
        [SerializeField] private float outroFade=.65f;
        [SerializeField] private bool duskUsesNightTheme=true;
        [SerializeField,Range(-12f,0f)] private float heavyWeatherTrimDb=-2.5f;
        [SerializeField,Range(0f,1f)] private float musicLevel=1f;

        private static MusicDirector _instance;
        private AudioDirector _director;
        private AudioSource _source;
        private GameObject _sourceHost;
        private Coroutine _transition;
        private int _request;
        private string _currentTrack,_pendingTrack;
        private string _biome=AudioIds.BiomeNone;
        private TimeOfDay _timeOfDay=TimeOfDay.Day;
        private GameMode _mode=GameMode.Boot;
        private BattleKind _battleKind=BattleKind.Wild;
        private bool _inWorld,_cinematicHold,_battleLive,_oneShot;
        private float _duckGain=1,_weatherTrim=1,_weatherTrimTarget=1,_envelope,_cueGain=1;

        public string CurrentTrack=>_currentTrack;
        public string PendingTrack=>_pendingTrack;
        public bool IsOneShot=>_oneShot;
        public int PlayingSourceCount=>_source!=null && _source.isPlaying ? 1 : 0;
        public bool IsNight=>_timeOfDay==TimeOfDay.Night || (duskUsesNightTheme && _timeOfDay==TimeOfDay.Dusk);

        private void Awake()
        {
            if(_instance!=null && _instance!=this){enabled=false;Destroy(this);return;}
            _instance=this;
            AudioDirector.TryResolve(out _director);
            _sourceHost=new GameObject("MusicTransport");
            _sourceHost.transform.SetParent(transform,false);
            _source=_sourceHost.AddComponent<AudioSource>();
            _source.playOnAwake=false;_source.spatialBlend=0;_source.dopplerLevel=0;
            if(_director!=null)_source.outputAudioMixerGroup=_director.GroupFor(AudioBus.Music);
            ServiceHub.Register(this);
        }
        private void OnEnable()
        {
            if(_instance!=this)return;
            GameEvents.BiomeEntered+=OnBiomeEntered;
            GameEvents.TimeOfDayChanged+=OnTimeOfDayChanged;
            GameEvents.WeatherChanged+=OnWeatherChanged;
            GameEvents.ModeChanged+=OnModeChanged;
            SceneManager.sceneLoaded+=OnSceneLoaded;
            if(_director!=null)
            {
                _director.MusicDuckChanged+=OnDuckChanged;
                _duckGain=_director.MusicDuckGain;
            }
        }
        private void OnDisable()
        {
            GameEvents.BiomeEntered-=OnBiomeEntered;
            GameEvents.TimeOfDayChanged-=OnTimeOfDayChanged;
            GameEvents.WeatherChanged-=OnWeatherChanged;
            GameEvents.ModeChanged-=OnModeChanged;
            SceneManager.sceneLoaded-=OnSceneLoaded;
            if(_director!=null)_director.MusicDuckChanged-=OnDuckChanged;
            CancelPending();
            if(_source!=null){_source.Stop();_source.clip=null;}
            _currentTrack=null;_pendingTrack=null;_oneShot=false;
        }
        private void OnDestroy()
        {
            if(_sourceHost!=null)Destroy(_sourceHost);
            if(_instance!=this)return;
            _instance=null;ServiceHub.Unregister(this);
        }
        private void OnSceneLoaded(Scene scene,LoadSceneMode mode)
        {
            if(mode!=LoadSceneMode.Single)return;
            // A delayed victory/encounter cue belongs to the old scene.
            CancelPending();
            if(_oneShot && _source!=null){_source.Stop();_source.clip=null;_currentTrack=null;}
            _pendingTrack=null;_oneShot=false;_battleLive=false;_cinematicHold=false;
        }
        private void OnDuckChanged(float gain)=>_duckGain=gain;
        private void Update()
        {
            _weatherTrim=Mathf.MoveTowards(_weatherTrim,_weatherTrimTarget,Time.unscaledDeltaTime*.5f);
            ApplyLevel();
        }
        private void ApplyLevel()
        {
            if(_source!=null)_source.volume=musicLevel*_envelope*_cueGain*_weatherTrim*(_oneShot ? 1 : _duckGain);
        }
        private bool CanStartWorldMusic()=>_inWorld && !_cinematicHold && !_battleLive &&
            (_mode==GameMode.Exploring || _mode==GameMode.Menu || _mode==GameMode.Dialogue || _mode==GameMode.Boot);
        private void OnBiomeEntered(string biome)
        {
            if(string.IsNullOrEmpty(biome))return;
            _inWorld=true;_biome=biome.Trim().ToLowerInvariant();
            if(CanStartWorldMusic())PlayTrack(SelectExplorationTrack(),biomeFade);
        }
        private void OnTimeOfDayChanged(TimeOfDay from,TimeOfDay to)
        { _timeOfDay=to;if(CanStartWorldMusic())PlayTrack(SelectExplorationTrack(),timeOfDayFade); }
        private void OnWeatherChanged(Weather from,Weather to)
        {
            _weatherTrimTarget=to==Weather.Rain || to==Weather.Sandstorm || to==Weather.Hail
                ? Mathf.Pow(10,heavyWeatherTrimDb/20) : 1;
            if(CanStartWorldMusic())PlayTrack(SelectExplorationTrack(),biomeFade);
        }
        private void OnModeChanged(GameMode from,GameMode to)
        {
            _mode=to;
            switch(to)
            {
                case GameMode.Exploring:
                    _battleLive=false;
                    if(CanStartWorldMusic())PlayTrack(SelectExplorationTrack(),outroFade);
                    else if(from==GameMode.BattleOutro)FadeOutAll(.2f);
                    break;
                case GameMode.EncounterIntro: PlayEncounterSting();break;
                case GameMode.Battle: _battleLive=true;PlayTrack(BattleTrack(),battleFade);break;
                case GameMode.BattleOutro:
                    _battleLive=false;
                    // Presentation still owns capture/victory cues until it drains.
                    if(!_oneShot)FadeOutAll(outroFade);
                    break;
            }
        }
        public void LeaveWorld()
        {
            _inWorld=false;_biome=AudioIds.BiomeNone;_cinematicHold=false;_battleLive=false;
            FadeOutAll(.15f);
        }
        [UnityEngine.Scripting.Preserve]
        public void SetCinematicHold(bool held)
        {
            if(_cinematicHold==held)return;
            _cinematicHold=held;
            if(CanStartWorldMusic())PlayTrack(SelectExplorationTrack(),outroFade);
        }
        public void SetBattleKind(BattleKind kind)
        {
            _battleKind=kind;
            if(_mode==GameMode.Battle || !_inWorld)
            { _battleLive=true;PlayTrack(BattleTrack(),battleFade); }
        }
        public void EndBattle(BattleOutcome outcome)
        {
            _battleLive=false;
            if(outcome==BattleOutcome.PlayerVictory)PlaySting(AudioIds.MusicVictoryFanfare);
            else if(outcome!=BattleOutcome.Captured)FadeOutAll(outroFade);
        }
        private string BattleTrack()=>_battleKind==BattleKind.Trainer ? AudioIds.MusicBattleTrainer : AudioIds.MusicBattleWild;
        public string SelectExplorationTrack()
        {
            if(_biome==AudioIds.BiomeTown || _biome.StartsWith("interior_"))return IsNight ? AudioIds.MusicTownNight : AudioIds.MusicTownDay;
            if(_biome==AudioIds.BiomeCave || _biome.Contains("cave"))return AudioIds.MusicCave;
            if(_biome==AudioIds.BiomeLakeside)return AudioIds.MusicLakeside;
            return IsNight ? AudioIds.MusicRouteNight : AudioIds.MusicRouteDay;
        }
        public void PlayTrack(string trackName,float fadeSeconds)=>Request(trackName,true,fadeSeconds,1);
        public void PlayEncounterSting()=>Request(AudioIds.MusicEncounterSting,false,.2f,1);
        public void PlaySting(string trackName,float volume=1)=>Request(trackName,false,.18f,volume);

        private void Request(string trackName,bool loop,float seconds,float volume)
        {
            if(_instance!=this || !isActiveAndEnabled || _source==null || string.IsNullOrEmpty(trackName))return;
            if(trackName==_pendingTrack)return;
            if(trackName==_currentTrack && _transition==null && _source.isPlaying)
            { _envelope=1;ApplyLevel();return; }
            if(_director==null && !AudioDirector.TryResolve(out _director))return;
            if(_director.Catalog!=null && _director.Catalog.TryGet(trackName,out var entry) && entry.Disabled)
            {
                // An intentionally silent region must also stop the previous scene's song.
                FadeOutAll(.2f);
                return;
            }
            var clip=_director.Catalog!=null ? _director.Catalog.GetClip(trackName) : null;
            if(clip==null){Debug.LogWarning("[MusicDirector] Missing track: "+trackName,this);return;}
            CancelPending();
            _pendingTrack=trackName;
            _transition=StartCoroutine(Replace(clip,trackName,loop,Mathf.Clamp(seconds,.05f,1.5f),Mathf.Clamp01(volume),_request));
        }
        private void CancelPending()
        {
            ++_request;
            if(_transition!=null){StopCoroutine(_transition);_transition=null;}
        }
        private static bool NeedsLoadGate(AudioClip clip)=>clip!=null && clip.loadState!=AudioDataLoadState.Loaded &&
            (clip.loadType!=AudioClipLoadType.Streaming || Application.platform==RuntimePlatform.WebGLPlayer);
        private IEnumerator Replace(AudioClip clip,string name,bool loop,float seconds,float volume,int request)
        {
            // Start loading while the old theme fades. Only this source can play music.
            if(NeedsLoadGate(clip) && clip.loadState==AudioDataLoadState.Unloaded)clip.LoadAudioData();
            yield return FadeEnvelope(0,seconds*.45f);
            if(request!=_request)yield break;
            _source.Stop();_source.clip=null;_currentTrack=null;
            float waited=0;
            while(NeedsLoadGate(clip) && clip.loadState==AudioDataLoadState.Loading && waited<5)
            {waited+=Time.unscaledDeltaTime;yield return null;}
            if(request!=_request)yield break;
            if(clip.loadState==AudioDataLoadState.Failed)
            {_pendingTrack=null;_transition=null;yield break;}
            _source.clip=clip;_source.loop=loop;_source.outputAudioMixerGroup=_director.GroupFor(AudioBus.Music);
            _oneShot=!loop;_cueGain=volume;_source.Play();
            _currentTrack=name;_pendingTrack=null;
            yield return FadeEnvelope(1,seconds*.55f);
            if(request!=_request)yield break;
            if(loop){_transition=null;yield break;}
            while(_source.isPlaying && request==_request)yield return null;
            if(request!=_request)yield break;
            _source.clip=null;_currentTrack=null;_oneShot=false;_transition=null;
            if(_battleLive && _mode!=GameMode.BattleOutro)PlayTrack(BattleTrack(),battleFade);
            else if(CanStartWorldMusic())PlayTrack(SelectExplorationTrack(),outroFade);
        }
        private IEnumerator FadeEnvelope(float target,float seconds)
        {
            float start=_envelope,elapsed=0;
            while(elapsed<seconds)
            {
                elapsed+=Time.unscaledDeltaTime;
                _envelope=Mathf.Lerp(start,target,Mathf.SmoothStep(0,1,Mathf.Clamp01(elapsed/seconds)));
                ApplyLevel();yield return null;
            }
            _envelope=target;ApplyLevel();
        }
        public void FadeOutAll(float seconds)
        {
            CancelPending();_pendingTrack=null;
            if(_source==null)return;
            _transition=StartCoroutine(StopAfterFade(Mathf.Clamp(seconds,.05f,1.5f),_request));
        }
        private IEnumerator StopAfterFade(float seconds,int request)
        {
            yield return FadeEnvelope(0,seconds);
            if(request!=_request)yield break;
            _source.Stop();_source.clip=null;_currentTrack=null;_oneShot=false;_transition=null;
        }
        public void SetMusicLevel(float linear01)=>musicLevel=Mathf.Clamp01(linear01);
    }
}
