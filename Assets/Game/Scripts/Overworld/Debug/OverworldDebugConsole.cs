using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Development-only inspection and cheats for the overworld, driven entirely from the
    /// inspector's context menu rather than from keys or UI — this assembly ships no UI, and a
    /// hotkey would fight the UI worker's input map.
    ///
    /// Everything here is either read-only or explicitly invoked. Nothing in this component runs
    /// on its own, so leaving it on a scene object cannot change how the game plays.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldDebugConsole : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private PlayerProfileHost _profileHost;
        [SerializeField] private DayNightCycle _clock;
        [SerializeField] private WeatherDirector _weather;
        [SerializeField] private ZoneDirector _zones;
        [SerializeField] private EncounterDirector _encounters;
        [SerializeField] private RoamingCreatureSpawner _spawner;

        [Header("Forced encounter")]
        [SerializeField] private int _forceSpeciesId = SliceRoster.Pikachu;
        [Min(1)][SerializeField] private int _forceLevel = 5;

        [Header("Read-only status")]
        [SerializeField] private string _status = "(press Refresh)";

        [ContextMenu("Refresh status")]
        public void RefreshStatus()
        {
            var zone = _zones != null && _zones.ActiveZone != null ? _zones.ActiveZone.BiomeId : "-";
            var phase = _clock != null ? _clock.Phase.ToString() : "-";
            var hour = _clock != null ? _clock.Hour.ToString("0.0") : "-";
            var weather = _weather != null ? _weather.EffectiveWeather.ToString() : "-";
            var pressure = _encounters != null ? _encounters.Pressure.ToString("0.0") : "-";
            var roamers = _spawner != null ? _spawner.Active.Count.ToString() : "-";
            var party = ServiceHub.TryGet<IPlayerProfile>(out var profile) ? profile.Party.Count.ToString() : "-";

            _status = $"zone={zone} time={phase}@{hour}h weather={weather} "
                      + $"pressure={pressure}m roamers={roamers} party={party}";
            Debug.Log("[OverworldDebug] " + _status, this);
        }

        [ContextMenu("Force wild encounter")]
        public void ForceEncounter()
        {
            if (!ServiceHub.TryGet<IGameFlow>(out var flow))
            {
                Debug.LogWarning("[OverworldDebug] No IGameFlow registered.", this);
                return;
            }

            var request = new EncounterRequest
            {
                Kind = BattleKind.Wild,
                WildSpeciesId = _forceSpeciesId,
                WildLevel = _forceLevel,
                Weather = _weather != null ? _weather.EffectiveWeather : Weather.Clear,
                TimeOfDay = _clock != null ? _clock.Phase : TimeOfDay.Day,
                BiomeId = _zones != null && _zones.ActiveZone != null ? _zones.ActiveZone.BiomeId : "debug",
                WorldPosition = transform.position,
                PlayerRotation = transform.rotation,
                Seed = Random.Range(int.MinValue, int.MaxValue),
            };

            flow.RequestEncounter(request, result =>
                Debug.Log($"[OverworldDebug] Forced encounter resolved: {result?.Outcome}", this));
        }

        [ContextMenu("Heal party")]
        public void HealParty()
        {
            if (ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete)
                concrete.HealParty();
        }

        [ContextMenu("Give 20 Poké Balls and 10 Potions")]
        public void GiveItems()
        {
            if (!ServiceHub.TryGet<IPlayerProfile>(out var profile)) return;
            profile.AddItem(FieldItems.PokeBall, 20);
            profile.AddItem(FieldItems.Potion, 10);
        }

        [ContextMenu("Log save file path")]
        public void LogSavePath() => Debug.Log("[OverworldDebug] Save: " + SaveSystem.SavePath, this);

        [ContextMenu("Save now")]
        public void SaveNow()
        {
            if (_profileHost != null) Debug.Log("[OverworldDebug] Save " + (_profileHost.SaveGame() ? "ok" : "failed"), this);
        }

        [ContextMenu("Despawn all roamers")]
        public void DespawnRoamers()
        {
            if (_spawner != null) _spawner.DespawnAll();
        }

        [ContextMenu("Cycle weather")]
        public void CycleWeather()
        {
            if (_weather == null) return;
            var next = (Weather)(((int)_weather.GlobalWeather + 1) % 6);
            _weather.SetWeather(next);
        }
    }
}
