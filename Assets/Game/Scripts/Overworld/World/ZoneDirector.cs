using System;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Decides which <see cref="WorldZone"/> the player is in and drives the handover between two.
    ///
    /// Two problems this solves that a naive OnTriggerEnter does not:
    ///
    /// 1. Overlapping volumes. Standing in a cave mouth you are inside both the cave and the route
    ///    volume; picking by priority (then by most-recently-entered) keeps the active zone stable
    ///    instead of flickering as you shuffle.
    /// 2. The interior handover. Entering a cave should be a transition, not a light popping. The
    ///    director runs a timed cross-fade and publishes the blended <see cref="ZoneAmbience"/>
    ///    every frame; the lighting worker consumes it. This assembly never touches a light.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-90)]
    public sealed class ZoneDirector : MonoBehaviour
    {
        [Header("Fallback")]
        [Tooltip("Used before the player enters any volume, and if they somehow leave every zone.")]
        [SerializeField] private WorldZone _defaultZone;

        [Header("Handover")]
        [Tooltip("Seconds the ambience cross-fades over. A cave mouth may override this per volume.")]
        [SerializeField] private float _transitionDuration = 1.1f;
        [Tooltip("Shape of the cross-fade. An ease-in-out reads as a doorway; linear reads as a wipe.")]
        [SerializeField] private AnimationCurve _transitionCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Camera")]
        [Tooltip("Optional. When set, each zone's suggested rest distance is applied to the rig.")]
        [SerializeField] private OverworldCameraRig _cameraRig;

        [Header("Ambience output")]
        [Tooltip("Wired to the VFX worker's LightingDirector. Fires every frame during a handover.")]
        [SerializeField] private StringEvent _biomeEntered = new StringEvent();

        private static ZoneDirector _instance;
        private static readonly List<ZoneVolume> ActiveVolumes = new List<ZoneVolume>();

        private WorldZone _activeZone;
        private ZoneAmbience _fromAmbience;
        private ZoneAmbience _toAmbience;
        private float _blendTimer;
        private float _blendDuration;
        private bool _blending;

        /// <summary>The single director in the scene. Null before boot, so callers must guard.</summary>
        public static ZoneDirector Instance => _instance;

        /// <summary>The zone the player is currently considered to be in. Never null after Start.</summary>
        public WorldZone ActiveZone => _activeZone;

        /// <summary>Ambience as of this frame, already blended mid-handover.</summary>
        public ZoneAmbience CurrentAmbience { get; private set; } = ZoneAmbience.Default;

        /// <summary>True while a handover is running. Encounters are suppressed during one.</summary>
        public bool IsTransitioning => _blending;

        /// <summary>Raised after the active zone has actually changed.</summary>
        public event Action<WorldZone, WorldZone> ZoneChanged;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[ZoneDirector] A second director was found; destroying the duplicate.", this);
                Destroy(this);
                return;
            }
            _instance = this;
            CurrentAmbience = _defaultZone != null ? _defaultZone.Ambience : ZoneAmbience.Default;
        }

        private void OnDestroy()
        {
            // Only the owning instance may touch the shared list.
            //
            // The duplicate above destroys itself in Awake, and its OnDestroy used to clear
            // ActiveVolumes on the way out — erasing the primary director's record of every volume
            // the player was standing in. The next Reevaluate then found nothing and fell back to
            // the default zone, so a player standing in a cave got the route's encounter table, the
            // route's ambience, and the route's biome id written into their save. A band streaming
            // in beside the town was enough to cause it.
            if (_instance != this) return;

            _instance = null;
            ActiveVolumes.Clear();
        }

        private void Start()
        {
            // Enter the default zone explicitly so BiomeEntered fires once at boot and the
            // lighting worker never starts from an undefined ambience.
            if (_activeZone == null && _defaultZone != null) ApplyZone(_defaultZone, 0f);
        }

        private void Update()
        {
            if (!_blending) return;

            _blendTimer += Time.deltaTime;
            var raw = _blendDuration <= 0f ? 1f : Mathf.Clamp01(_blendTimer / _blendDuration);
            var eased = _transitionCurve != null ? _transitionCurve.Evaluate(raw) : raw;

            CurrentAmbience = ZoneAmbience.Lerp(_fromAmbience, _toAmbience, eased);
            OverworldEvents.RaiseAmbienceBlend(_fromAmbience, _toAmbience, eased);

            if (_cameraRig != null) _cameraRig.SetRestDistance(CurrentAmbience.CameraRestDistance);

            if (raw >= 1f)
            {
                _blending = false;
                CurrentAmbience = _toAmbience;
            }
        }

        internal static void NotifyVolumeEnter(ZoneVolume volume)
        {
            if (volume == null) return;
            ActiveVolumes.Remove(volume);
            // Append so "most recently entered" is the natural tiebreak at equal priority.
            ActiveVolumes.Add(volume);
            if (_instance != null) _instance.Reevaluate(volume);
        }

        internal static void NotifyVolumeExit(ZoneVolume volume)
        {
            ActiveVolumes.Remove(volume);
            if (_instance != null) _instance.Reevaluate(null);
        }

        private void Reevaluate(ZoneVolume triggeringVolume)
        {
            ZoneVolume best = null;
            for (var i = 0; i < ActiveVolumes.Count; i++)
            {
                var candidate = ActiveVolumes[i];
                if (candidate == null || candidate.Zone == null) continue;
                if (best == null || candidate.Priority >= best.Priority) best = candidate;
            }

            var target = best != null ? best.Zone : _defaultZone;
            if (target == null || target == _activeZone) return;

            var duration = _transitionDuration;
            if (best != null && best.TransitionDurationOverride >= 0f) duration = best.TransitionDurationOverride;
            ApplyZone(target, duration);
        }

        private void ApplyZone(WorldZone zone, float duration)
        {
            var previous = _activeZone;
            _activeZone = zone;

            _fromAmbience = CurrentAmbience;
            _toAmbience = zone.Ambience;
            _blendDuration = Mathf.Max(0f, duration);
            _blendTimer = 0f;
            _blending = _blendDuration > 0f;

            if (!_blending)
            {
                CurrentAmbience = _toAmbience;
                OverworldEvents.RaiseAmbienceBlend(_fromAmbience, _toAmbience, 1f);
                if (_cameraRig != null) _cameraRig.SetRestDistance(_toAmbience.CameraRestDistance);
            }

            // Raised at the *start* of the handover, not the end: listeners need the whole
            // transition window to ramp, and the biome is authoritatively the new one already.
            GameEvents.RaiseBiomeEntered(zone.BiomeId);
            _biomeEntered.Invoke(zone.BiomeId);
            ZoneChanged?.Invoke(previous, zone);
        }

        /// <summary>Forces the active zone, for debug warps and for restoring a save.</summary>
        public void ForceZone(WorldZone zone, bool instant = true)
        {
            if (zone == null || zone == _activeZone) return;
            ApplyZone(zone, instant ? 0f : _transitionDuration);
        }

        /// <summary>Nearest zone containing a world point, for save restore and roamer placement.</summary>
        public WorldZone ZoneAt(Vector3 worldPosition)
        {
            WorldZone best = null;
            var bestPriority = int.MinValue;
            var zones = FindObjectsByType<WorldZone>(FindObjectsSortMode.None);
            for (var z = 0; z < zones.Length; z++)
            {
                var zone = zones[z];
                for (var v = 0; v < zone.Volumes.Count; v++)
                {
                    var volume = zone.Volumes[v];
                    if (volume == null) continue;
                    var collider = volume.GetComponent<Collider>();
                    if (collider == null || !collider.bounds.Contains(worldPosition)) continue;
                    if (volume.Priority <= bestPriority) continue;
                    bestPriority = volume.Priority;
                    best = zone;
                }
            }
            return best != null ? best : _defaultZone;
        }
    }
}
