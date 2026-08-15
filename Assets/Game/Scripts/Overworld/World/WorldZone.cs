using System;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Data describing how a zone should feel. Deliberately *only* data: this assembly never
    /// touches a Light, a Volume or a fog setting. The VFX/rendering worker's LightingDirector
    /// subscribes to <see cref="OverworldEvents.AmbienceBlend"/> and interprets these values.
    /// </summary>
    [Serializable]
    public struct ZoneAmbience
    {
        [Tooltip("True for caves and building interiors. Drives the interior lighting handover.")]
        public bool IsInterior;

        [Tooltip("Suggested ambient tint. A hint for the lighting worker, not an applied colour.")]
        public Color AmbientTint;

        [Range(0f, 1f)][Tooltip("0 fully lit, 1 pitch dark. Caves sit near 0.8.")]
        public float Darkness;

        [Range(0f, 1f)][Tooltip("Relative fog density hint.")]
        public float FogDensity;

        [Tooltip("Audio ambience key for the audio worker, e.g. 'amb_route_day'.")]
        public string AmbienceKey;

        [Tooltip("Suggested exploration camera distance. Caves want a tighter rig.")]
        public float CameraRestDistance;

        /// <summary>Numeric cross-fade between two ambiences, so the handover is continuous.</summary>
        public static ZoneAmbience Lerp(ZoneAmbience a, ZoneAmbience b, float t)
        {
            return new ZoneAmbience
            {
                // Interior is a step, not a blend — a half-interior state is meaningless. Flip at
                // the midpoint so the lighting worker gets a clean boolean to key off.
                IsInterior = t < 0.5f ? a.IsInterior : b.IsInterior,
                AmbientTint = Color.Lerp(a.AmbientTint, b.AmbientTint, t),
                Darkness = Mathf.Lerp(a.Darkness, b.Darkness, t),
                FogDensity = Mathf.Lerp(a.FogDensity, b.FogDensity, t),
                AmbienceKey = t < 0.5f ? a.AmbienceKey : b.AmbienceKey,
                CameraRestDistance = Mathf.Lerp(a.CameraRestDistance, b.CameraRestDistance, t),
            };
        }

        public static ZoneAmbience Default => new ZoneAmbience
        {
            IsInterior = false,
            AmbientTint = new Color(0.72f, 0.78f, 0.85f, 1f),
            Darkness = 0f,
            FogDensity = 0.08f,
            AmbienceKey = "amb_default",
            CameraRestDistance = 5.5f,
        };
    }

    /// <summary>Relative likelihood of each weather state inside a zone. Weights, not probabilities.</summary>
    [Serializable]
    public struct WeatherBias
    {
        [Min(0f)] public float Clear;
        [Min(0f)] public float Rain;
        [Min(0f)] public float Sun;
        [Min(0f)] public float Sandstorm;
        [Min(0f)] public float Hail;
        [Min(0f)] public float Fog;

        public float WeightOf(Weather weather)
        {
            switch (weather)
            {
                case Weather.Clear: return Clear;
                case Weather.Rain: return Rain;
                case Weather.Sun: return Sun;
                case Weather.Sandstorm: return Sandstorm;
                case Weather.Hail: return Hail;
                case Weather.Fog: return Fog;
                default: return 0f;
            }
        }

        public float Total => Clear + Rain + Sun + Sandstorm + Hail + Fog;

        /// <summary>Sensible per-area defaults, applied when a zone leaves the bias untouched.</summary>
        public static WeatherBias For(ZoneKind kind)
        {
            switch (kind)
            {
                case ZoneKind.Route:
                    return new WeatherBias { Clear = 5f, Rain = 2f, Sun = 2f, Fog = 1f };
                case ZoneKind.Town:
                    return new WeatherBias { Clear = 6f, Rain = 2f, Sun = 2f };
                case ZoneKind.Cave:
                    // Underground: the sky does not reach you. Weather is pinned Clear.
                    return new WeatherBias { Clear = 1f };
                case ZoneKind.Lakeside:
                    return new WeatherBias { Clear = 3f, Rain = 4f, Fog = 3f, Sun = 1f };
                default:
                    return new WeatherBias { Clear = 1f };
            }
        }
    }

    /// <summary>
    /// One explorable area. Holds identity, its encounter table, its ambience and its weather
    /// bias; the trigger volumes that decide when you are inside it are separate
    /// (<see cref="ZoneVolume"/>) so a zone can be an irregular shape made of several boxes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldZone : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Stable id broadcast on GameEvents.BiomeEntered. Battle stages key off this.")]
        [SerializeField] private string _biomeId = "route_01";
        [SerializeField] private string _displayName = "Route 1";
        [SerializeField] private ZoneKind _kind = ZoneKind.Route;

        [Header("Encounters")]
        [Tooltip("Leave empty to use the built-in slice table for this zone kind.")]
        [SerializeField] private EncounterTable _encounterTable;
        [Tooltip("Table used while the player is on water in this zone. Falls back to the main table.")]
        [SerializeField] private EncounterTable _waterEncounterTable;
        [Tooltip("Scales all encounter pressure inside this zone. 0 disables wild encounters entirely.")]
        [Range(0f, 3f)][SerializeField] private float _encounterRateMultiplier = 1f;

        [Header("Ambience")]
        [SerializeField] private ZoneAmbience _ambience = ZoneAmbience.Default;

        [Header("Weather")]
        [Tooltip("Leave all weights at zero to use the default bias for this zone kind.")]
        [SerializeField] private WeatherBias _weatherBias;
        [Tooltip("Interiors ignore the global weather entirely and report Clear.")]
        [SerializeField] private bool _suppressWeather;

        [Header("Streaming")]
        [Tooltip("Content root toggled by the streamer. Leave null to never unload this zone.")]
        [SerializeField] private GameObject _contentRoot;
        [Tooltip("Zones kept loaded while this one is active, so a doorway never shows an empty room.")]
        [SerializeField] private List<WorldZone> _neighbours = new List<WorldZone>();

        [Header("Roaming population")]
        [Tooltip("Target number of visible wild creatures alive in this zone.")]
        [SerializeField] private int _roamerBudget = 6;
        [Tooltip("Points roamers may be spawned around. Empty falls back to the zone volumes.")]
        [SerializeField] private List<Transform> _roamerSpawnAnchors = new List<Transform>();

        public string BiomeId => _biomeId;
        public string DisplayName => _displayName;
        public ZoneKind Kind => _kind;
        public ZoneAmbience Ambience => _ambience;
        public float EncounterRateMultiplier => _encounterRateMultiplier;
        public bool SuppressWeather => _suppressWeather || _kind == ZoneKind.Cave;
        public GameObject ContentRoot => _contentRoot;
        public IReadOnlyList<WorldZone> Neighbours => _neighbours;
        public int RoamerBudget => _roamerBudget;
        public IReadOnlyList<Transform> RoamerSpawnAnchors => _roamerSpawnAnchors;

        /// <summary>Volumes registered against this zone, populated by <see cref="ZoneVolume"/> on enable.</summary>
        public List<ZoneVolume> Volumes { get; } = new List<ZoneVolume>();

        /// <summary>
        /// Encounter table for the requested traversal state, falling back through water to land
        /// and finally to the built-in slice defaults so a zone is playable before any asset exists.
        /// </summary>
        public EncounterTable TableFor(TraversalState traversal)
        {
            if (traversal == TraversalState.Water && _waterEncounterTable != null) return _waterEncounterTable;
            if (_encounterTable != null) return _encounterTable;
            return EncounterTable.BuiltInFor(_kind, traversal);
        }

        /// <summary>Effective weather weights, resolving the "left at defaults" case.</summary>
        public WeatherBias EffectiveWeatherBias =>
            _weatherBias.Total > 0f ? _weatherBias : WeatherBias.For(_kind);

        /// <summary>Weather as this zone reports it — interiors always read Clear.</summary>
        public Weather FilterWeather(Weather global) => SuppressWeather ? Weather.Clear : global;

        internal void RegisterVolume(ZoneVolume volume)
        {
            if (!Volumes.Contains(volume)) Volumes.Add(volume);
        }

        internal void UnregisterVolume(ZoneVolume volume) => Volumes.Remove(volume);

        /// <summary>A NavMesh-agnostic point inside the zone, used to seed roamer spawns.</summary>
        public bool TryGetSpawnPoint(ref DeterministicRandom rng, out Vector3 point)
        {
            if (_roamerSpawnAnchors.Count > 0)
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var anchor = _roamerSpawnAnchors[rng.Range(0, _roamerSpawnAnchors.Count)];
                    if (anchor == null) continue;
                    var offset = new Vector3(rng.Range(-6f, 6f), 0f, rng.Range(-6f, 6f));
                    point = anchor.position + offset;
                    return true;
                }
            }

            if (Volumes.Count > 0)
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var volume = Volumes[rng.Range(0, Volumes.Count)];
                    if (volume == null) continue;
                    if (volume.TryGetRandomPoint(ref rng, out point)) return true;
                }
            }

            point = transform.position;
            return false;
        }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(_biomeId)) _biomeId = _kind.ToString().ToLowerInvariant();
            if (_ambience.CameraRestDistance <= 0f) _ambience.CameraRestDistance = 5.5f;
        }
    }
}
