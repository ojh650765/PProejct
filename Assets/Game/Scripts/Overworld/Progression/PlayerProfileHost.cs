using UnityEngine;
using UnityEngine.SceneManagement;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Owns the <see cref="PlayerProfile"/> lifetime: creates it, registers it with
    /// <see cref="ServiceHub"/>, loads the save, and writes it back.
    ///
    /// Writing happens only when the player asks for it. This game used to save on a timer, on
    /// focus loss, on quit and after a heal; all of that is gone by request, in favour of the
    /// genre's report convention — the 리포트 entry in the menu is the game's one save, fronted
    /// by <see cref="TrySaveGame"/>. The accepted trade is explicit: an interrupted session now
    /// loses everything since the last report, and that is the classic deal rather than a bug.
    /// The only other writers are editor-only debug affordances (the context menu below, the
    /// debug console), which exist for developers and never fire on their own.
    ///
    /// Registration happens in <c>Awake</c> at a very early execution order because every other
    /// overworld system resolves <see cref="IPlayerProfile"/> lazily and would otherwise race it.
    /// If the integrator's boot scene registers a profile first, this defers to it rather than
    /// overwriting — the boot scene is the authority on service registration.
    ///
    /// There is more than one of these at runtime and there has to be. Every playable scene carries
    /// a host so it can be opened on its own in the editor, so walking into a house brings a second
    /// one and a streamed band brings a third. Only one of them may begin the session, and only one
    /// of them may write the file; the rest defer to the live profile and stand down. Both of those
    /// decisions are recorded in statics, because the session outlives every scene that carries a
    /// host.
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

        /// <summary>The host that currently owns the write side. See <see cref="OwnsWriteSide"/>.</summary>
        private static PlayerProfileHost _active;

        /// <summary>
        /// True once some host has loaded or newed the profile for this session.
        ///
        /// This is what stops every scene re-running the bootstrap. Start used to load or new
        /// unconditionally, so walking into a house rolled the live profile back to the last
        /// save on disk — or, with no file yet, reset it outright mid-session — and a band
        /// streamed in additively did the same a few frames after boot.
        /// </summary>
        private static bool _sessionBegun;

        private static bool _loadedFromSave;

        /// <summary>The live profile. Null only before Awake.</summary>
        public PlayerProfile Profile => _profile;

        /// <summary>
        /// True when this session began from a save file rather than a new game.
        ///
        /// Session-scoped rather than per-host, because it describes the session and not the
        /// component. A host that stood down loaded nothing, and answering "no" on its behalf has
        /// the opening episode read a continued session as a fresh one and replay itself in the
        /// house the player has just walked into.
        /// </summary>
        public bool LoadedFromSave => _loadedFromSave;

        /// <summary>True once the profile holds a real session rather than an empty default.</summary>
        public static bool SessionBegun => _sessionBegun;

        /// <summary>
        /// Whether this host owns the write side — the play clock and the save file.
        ///
        /// Claimed lazily rather than in Awake, because the slot has to be re-claimable. Every
        /// playable scene carries a host, so a door hands ownership from the outgoing scene's to the
        /// incoming one's, and the exact interleaving of teardown and Awake across a Single load is
        /// not something to bet a session's saves on. Unity's <c>==</c> reads a destroyed
        /// component as null, so the slot frees itself the moment its holder is gone.
        /// </summary>
        private bool OwnsWriteSide
        {
            get
            {
                if (_active == null) _active = this;
                return _active == this;
            }
        }

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

            // A profile that had to be built is the start of a session, so the markers start over
            // with it. Statics survive a domain reload the editor has switched off, and a marker
            // left standing from the previous play session would have the first host of the next
            // one stand down and never load the save at all.
            _sessionBegun = false;
            _loadedFromSave = false;
        }

        private void OnDestroy()
        {
            if (_active == this) _active = null;
            // _sessionBegun is deliberately left standing. The session continues across the load;
            // it is the profile's lifetime that ends it, and Awake handles that.
        }

        private void Start()
        {
            if (_zoneDirector == null) _zoneDirector = ZoneDirector.Instance;
            if (_clock == null) _clock = DayNightCycle.Instance;
            if (_weather == null) _weather = WeatherDirector.Instance;
            if (_encounters == null) _encounters = EncounterDirector.Instance;

            if (!OwnsWriteSide) return;

            if (_sessionBegun)
            {
                // The scene the player just walked into carries a host of its own, and it is not
                // this host's session to start over. The profile on the hub is the live one.
                Debug.Log("[Profile] The session had already begun, so this host kept the live "
                          + "profile instead of loading over it.", this);
                return;
            }

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
            // A duplicate host would double-count play time over the same profile. Only the
            // owner runs the clock.
            if (!OwnsWriteSide) return;

            _profile.PlayTimeSeconds += Time.deltaTime;

            // No autosave here, deliberately. The interval timer this loop used to run wrote
            // the file behind the player's back every two minutes; saving is now only ever the
            // menu's 리포트 action, so the player always knows exactly what their last record
            // holds. See the class comment for the trade that buys.
        }

        /// <summary>
        /// Whether a save right now would record the live session rather than something mid-flight.
        ///
        /// Three things have to hold. This host owns the write side; the session has actually begun,
        /// because before that the profile is an empty default and writing it out is not a save but
        /// a deletion; and nothing is mid-encounter, where the player's world position is
        /// mid-transition and the party is being mutated by the battle engine.
        ///
        /// The menu is allowed a save because it is the one place saving happens from, so the
        /// flow check reads "in the menu" as fine and refuses everything else — battle,
        /// cutscene, dialogue. Public because the menu asks this question before it even starts
        /// the save beat: a refusal should say "지금은 리포트를 기록할 수 없다!" to the
        /// player's face rather than dress up as a disk error.
        ///
        /// There is deliberately no OnApplicationQuit / OnApplicationPause save behind this
        /// guard any more. The quit and focus-loss writes are gone with the autosave timer:
        /// saving is only ever the player's explicit report, and an interruption losing the
        /// session since the last one is the accepted, classic trade.
        /// </summary>
        public bool CanSaveNow()
        {
            if (_profile == null) return false;
            if (!OwnsWriteSide) return false;
            if (!_sessionBegun) return false;
            if (_encounters != null && _encounters.SequenceRunning) return false;
            if (ServiceHub.TryGet<IGameFlow>(out var flow)
                && flow.Mode != GameMode.Exploring && flow.Mode != GameMode.Menu) return false;
            return true;
        }

        /// <summary>
        /// The menu's save: the guard, then the write. Returns false when the guard refuses or
        /// the write fails, so the caller can tell the player honestly instead of claiming a
        /// record that was never made. This is the game's only non-debug writer.
        /// </summary>
        public bool TrySaveGame()
        {
            if (!CanSaveNow()) return false;
            return SaveGame();
        }

        /// <summary>Starts a fresh profile with the opening kit.</summary>
        public void NewGame()
        {
            _loadedFromSave = false;
            // Marked here rather than at the call site, because these two are the session: whoever
            // reaches one of them — the boot path, or the context menu — has begun it, and every
            // host that arrives afterwards must see that and stand down.
            _sessionBegun = true;
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

            // Marked before the restore, not after: the position restore runs on its own coroutine
            // and a save the player requests mid-restore must already see a begun session.
            _loadedFromSave = true;
            _sessionBegun = true;

            RestoreWorld(world);
        }

        private void RestoreWorld(WorldSave world)
        {
            if (_clock != null) _clock.SetNormalisedTime(world.TimeOfDayNormalised);
            if (_weather != null) _weather.SetWeather((Weather)world.Weather, instant: true);

            if (_encounters != null)
            {
                // Asked as "was one written", not "is it non-zero". Zero is a usable seed and the
                // old test threw it away, so a session seeded there resumed on a different
                // encounter stream from the one it saved.
                if (world.HasSavedEncounterSeed) _encounters.WorldSeed = world.EncounterSeed;
                _encounters.CheckCounter = world.EncounterCheckCounter;
                _encounters.ResetPressure();
            }

            if (!_restorePosition || _player == null || !world.HasSavedPosition) return;

            var scene = SceneManager.GetActiveScene().name;
            if (!world.PositionAppliesTo(scene))
            {
                // The save was taken somewhere else, so its coordinates mean something else here.
                // The scene's own spawn marker is the right answer; a cave floor's position applied
                // to the overworld puts the player on the summit above it.
                Debug.Log($"[Profile] The save was written in '{world.SceneName}' and this is " +
                          $"'{scene}', so the scene's spawn marker stands and the saved position " +
                          "was left alone.", this);
                return;
            }

            StartCoroutine(RestorePosition(world));
        }

        /// <summary>
        /// Puts the player back at the saved position, once there is ground underneath it.
        ///
        /// The probe used to run on the first frame and discard the position the moment it missed.
        /// On the first frame it nearly always misses: the neighbouring bands are still being loaded
        /// additively, so the terrain the save refers to does not exist yet — and the player was
        /// left standing at the spawn marker every single time they loaded a save taken out in the
        /// field. So it waits for the ground instead of ruling on it once.
        ///
        /// Bounded, because the check itself is what stops a save from bricking a session and a wait
        /// with no end is its own way of never restoring anything.
        /// </summary>
        private System.Collections.IEnumerator RestorePosition(WorldSave world)
        {
            var deadline = Time.realtimeSinceStartup + StreamingGraceSeconds;

            while (_player != null && !HasStandableGround(world.PlayerPosition)
                   && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (_player == null) yield break;

            if (!HasStandableGround(world.PlayerPosition))
            {
                Debug.LogWarning($"[Profile] The save puts the player at {world.PlayerPosition}, " +
                                 $"where no ground had appeared within {StreamingGraceSeconds:F0}s. " +
                                 "Ignoring it and starting from the scene's spawn instead.", this);
                yield break;
            }

            _player.Warp(world.PlayerPosition, world.PlayerRotation);

            // Re-derive the active zone from the restored position: trigger volumes will not
            // fire for a warp that starts the player already inside them.
            if (_zoneDirector == null) _zoneDirector = ZoneDirector.Instance;
            if (_zoneDirector != null)
            {
                var zone = _zoneDirector.ZoneAt(world.PlayerPosition);
                if (zone != null) _zoneDirector.ForceZone(zone);
            }
        }

        /// <summary>Seconds the restore will wait for streamed ground before giving the position up.</summary>
        private const float StreamingGraceSeconds = 8f;

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
        /// So a restored position has to be checked before it is honoured. Whether a miss is
        /// terminal or merely early is the caller's to decide — see <see cref="RestorePosition"/>.
        /// </summary>
        private static bool HasStandableGround(Vector3 position)
        {
            const float lift = 2f;
            const float reach = 60f;
            return Physics.Raycast(position + Vector3.up * lift, Vector3.down, lift + reach,
                ~0, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Captures the world snapshot and writes the save file.</summary>
        public bool SaveGame()
        {
            if (_profile == null) return false;

            var world = new WorldSave
            {
                BiomeId = _zoneDirector != null && _zoneDirector.ActiveZone != null
                    ? _zoneDirector.ActiveZone.BiomeId : null,
                // Stamped so the position is only ever restored back into the scene it was taken in.
                SceneName = SceneManager.GetActiveScene().name,
                PlayerPosition = _player != null ? _player.transform.position : Vector3.zero,
                PlayerRotation = _player != null ? _player.transform.rotation : Quaternion.identity,
                // Recorded as a flag rather than inferred from the vector: the origin is a place the
                // player can legitimately stand, and it is not a way of saying "unknown".
                HasPlayerPosition = _player != null,
                TimeOfDayNormalised = _clock != null ? _clock.Normalised : 0.34f,
                Weather = _weather != null ? (int)_weather.GlobalWeather : 0,
                EncounterSeed = _encounters != null ? _encounters.WorldSeed : 0,
                HasEncounterSeed = _encounters != null,
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
