using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Owns the <see cref="PlayerProfile"/> lifetime: creates it, registers it with
    /// <see cref="ServiceHub"/>, loads the save, and writes it back.
    ///
    /// Registration happens in <c>Awake</c> at a very early execution order because every other
    /// overworld system resolves <see cref="IPlayerProfile"/> lazily and would otherwise race it.
    /// If the integrator's boot scene registers a profile first, this defers to it rather than
    /// overwriting — the boot scene is the authority on service registration.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-500)]
    public sealed class PlayerProfileHost : MonoBehaviour
    {
        [Header("New game")]
        [SerializeField] private string _defaultTrainerName = "Trainer";
        [Tooltip("Species handed to the player when there is no save. Use a SliceRoster starter.")]
        [SerializeField] private int _starterSpeciesId = SliceRoster.Charmander;
        [Min(1)][SerializeField] private int _starterLevel = 5;
        [SerializeField] private int _starterSeed = 424242;

        [Header("Persistence")]
        [Tooltip("Load the save file at Awake. Turn off to always start a fresh session.")]
        [SerializeField] private bool _loadOnStart = true;
        [Tooltip("Seconds between autosaves. 0 disables autosaving.")]
        [SerializeField] private float _autosaveInterval = 120f;
        [Tooltip("Save when the application loses focus or quits. Strongly recommended.")]
        [SerializeField] private bool _saveOnQuit = true;

        [Header("World state")]
        [SerializeField] private PlayerLocomotion _player;
        [SerializeField] private ZoneDirector _zoneDirector;
        [SerializeField] private DayNightCycle _clock;
        [SerializeField] private WeatherDirector _weather;
        [SerializeField] private EncounterDirector _encounters;
        [Tooltip("Restore the player's saved position at load. Turn off while authoring, so you "
                 + "always start where the scene puts you.")]
        [SerializeField] private bool _restorePosition = true;

        private PlayerProfile _profile;
        private float _autosaveTimer;

        /// <summary>The live profile. Null only before Awake.</summary>
        public PlayerProfile Profile => _profile;

        /// <summary>True when this session began from a save file rather than a new game.</summary>
        public bool LoadedFromSave { get; private set; }

        private void Awake()
        {
            // Defer to a profile the boot scene already registered; two profiles would silently
            // diverge and the loop would apply results to whichever one it happened to resolve.
            if (ServiceHub.TryGet<IPlayerProfile>(out var existing) && existing is PlayerProfile registered)
            {
                _profile = registered;
                return;
            }

            _profile = new PlayerProfile();
            ServiceHub.Register<IPlayerProfile>(_profile);
        }

        private void Start()
        {
            if (_zoneDirector == null) _zoneDirector = ZoneDirector.Instance;
            if (_clock == null) _clock = DayNightCycle.Instance;
            if (_weather == null) _weather = WeatherDirector.Instance;
            if (_encounters == null) _encounters = EncounterDirector.Instance;

            StartCoroutine(BeginSession());
        }

        /// <summary>
        /// Starts the session once the dex is loadable.
        ///
        /// A new game builds the starter immediately, and CreatureFactory reads its base stats
        /// from ISpeciesRegistry — which on the web is still being fetched over HTTP when this
        /// runs. The creature was built from estimates and kept them for the rest of the
        /// session; the only sign was one line saying so, in a console the player never sees.
        ///
        /// Bounded, because a registry that never arrives must not cost the player the game.
        /// Waiting a second and starting on estimates is worse than waiting; never starting is
        /// worse than both.
        /// </summary>
        private System.Collections.IEnumerator BeginSession()
        {
            var deadline = Time.realtimeSinceStartup + 20f;
            while (!ServiceHub.Has<ISpeciesRegistry>() && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (!ServiceHub.Has<ISpeciesRegistry>())
                Debug.LogWarning("[Profile] The species registry never arrived, so the starter " +
                                 "is built from estimated base stats and will not match the dex.",
                                 this);

            if (_loadOnStart && SaveSystem.SaveExists()) LoadGame();
            else NewGame();
        }

        private void Update()
        {
            if (_profile == null) return;
            _profile.PlayTimeSeconds += Time.deltaTime;

            if (_autosaveInterval <= 0f) return;
            _autosaveTimer += Time.deltaTime;
            if (_autosaveTimer < _autosaveInterval) return;
            _autosaveTimer = 0f;

            // Never autosave mid-encounter: the player's world position is mid-transition and the
            // party is being mutated by the battle engine.
            if (_encounters != null && _encounters.SequenceRunning) return;
            if (ServiceHub.TryGet<IGameFlow>(out var flow) && flow.Mode != GameMode.Exploring) return;

            SaveGame();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && _saveOnQuit) SaveGame();
        }

        private void OnApplicationQuit()
        {
            if (_saveOnQuit) SaveGame();
        }

        /// <summary>Starts a fresh profile with the opening kit.</summary>
        public void NewGame()
        {
            LoadedFromSave = false;
            _profile.InitialiseNewGame(_defaultTrainerName, _starterSpeciesId, _starterLevel, _starterSeed);
        }

        /// <summary>Loads the save file and restores the world state that came with it.</summary>
        public void LoadGame()
        {
            var world = SaveSystem.Load(_profile);
            if (world == null)
            {
                Debug.LogWarning("[PlayerProfileHost] No usable save; starting a new game.", this);
                NewGame();
                return;
            }

            LoadedFromSave = true;
            RestoreWorld(world);
        }

        private void RestoreWorld(WorldSave world)
        {
            if (_clock != null) _clock.SetNormalisedTime(world.TimeOfDayNormalised);
            if (_weather != null) _weather.SetWeather((Weather)world.Weather, instant: true);

            if (_encounters != null)
            {
                _encounters.WorldSeed = world.EncounterSeed != 0 ? world.EncounterSeed : _encounters.WorldSeed;
                _encounters.CheckCounter = world.EncounterCheckCounter;
                _encounters.ResetPressure();
            }

            if (_restorePosition && _player != null && world.PlayerPosition != Vector3.zero
                && IsStandable(world.PlayerPosition))
            {
                _player.Warp(world.PlayerPosition, world.PlayerRotation);
                // Re-derive the active zone from the restored position: trigger volumes will not
                // fire for a warp that starts the player already inside them.
                if (_zoneDirector != null)
                {
                    var zone = _zoneDirector.ZoneAt(world.PlayerPosition);
                    if (zone != null) _zoneDirector.ForceZone(zone);
                }
            }
        }

        /// <summary>
        /// True when there is ground to stand on at a saved position.
        ///
        /// A save is a record of where the player was, and it is trusted absolutely — which
        /// makes it a way to brick a game permanently. That happened: the player walked off
        /// the edge of the height field, the fall was saved, and every session afterwards
        /// restored them mid-fall and saved a lower position still, down past y = -1300. The
        /// scene on disk was correct the whole time and reloading it changed nothing, because
        /// the save overrode it after load.
        ///
        /// So a restored position has to be checked before it is honoured. Failing this test
        /// leaves the player wherever the scene put them, which is the spawn marker.
        /// </summary>
        private static bool IsStandable(Vector3 position)
        {
            const float lift = 2f;
            const float reach = 60f;
            if (Physics.Raycast(position + Vector3.up * lift, Vector3.down, lift + reach,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                return true;
            }

            Debug.LogWarning($"[Profile] The save puts the player at {position}, where there " +
                             "is no ground within 60 m below. Ignoring it and starting from " +
                             "the scene's spawn instead.");
            return false;
        }

        /// <summary>Captures the world snapshot and writes the save file.</summary>
        public bool SaveGame()
        {
            if (_profile == null) return false;

            var world = new WorldSave
            {
                BiomeId = _zoneDirector != null && _zoneDirector.ActiveZone != null
                    ? _zoneDirector.ActiveZone.BiomeId : null,
                PlayerPosition = _player != null ? _player.transform.position : Vector3.zero,
                PlayerRotation = _player != null ? _player.transform.rotation : Quaternion.identity,
                TimeOfDayNormalised = _clock != null ? _clock.Normalised : 0.34f,
                Weather = _weather != null ? (int)_weather.GlobalWeather : 0,
                EncounterSeed = _encounters != null ? _encounters.WorldSeed : 0,
                EncounterCheckCounter = _encounters != null ? _encounters.CheckCounter : 0,
            };

            return SaveSystem.Save(_profile, world);
        }

        [ContextMenu("Save now")] private void ContextSave() => SaveGame();
        [ContextMenu("Load now")] private void ContextLoad() => LoadGame();

        [ContextMenu("Delete save and start new game")]
        private void ContextReset()
        {
            SaveSystem.Delete();
            NewGame();
        }
    }
}
