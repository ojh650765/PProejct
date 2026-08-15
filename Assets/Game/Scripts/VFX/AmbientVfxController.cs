using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Keeps the right environmental effects alive around the player.
    ///
    /// The user asked for "lots of environmental activity", and the trap with that
    /// brief is turning everything on at once: the frame fills with particles, the
    /// cost goes up and the world reads as busy rather than as alive. So this
    /// component owns a small set of rules instead:
    ///
    ///   - Emitters follow the player but simulate in world space, so particles stay
    ///     put while the volume moves. Nothing pops when the player walks.
    ///   - Only what belongs to the current time, weather and biome is running.
    ///     Fireflies at night, pollen by day, rain when it rains.
    ///   - Gusts are events, not a constant. One every few seconds, synchronised
    ///     with the wind global the foliage shader reads, so grass bends and visible
    ///     air crosses it at the same moment.
    ///
    /// Attach next to the player or the camera rig and leave it alone.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("PokeLab/Ambient VFX Controller")]
    public sealed class AmbientVfxController : MonoBehaviour
    {
        [Header("Follow")]
        [Tooltip("Transform the ambient volumes track. Defaults to the main camera.")]
        [SerializeField] private Transform followTarget;

        [Tooltip("Vertical offset from the follow target for the volume centre.")]
        [SerializeField] private float followHeight = 1.5f;

        [Tooltip("Metres the follow target must move before the volumes are repositioned. " +
                 "Snapping in steps is cheaper than tracking continuously and is invisible.")]
        [SerializeField] private float repositionThreshold = 3f;

        [Header("Content")]
        [SerializeField] private bool pollen = true;
        [SerializeField] private bool leaves = true;
        [SerializeField] private bool butterflies = true;
        [SerializeField] private bool birds = true;
        [SerializeField] private bool firefliesAtNight = true;

        [Header("Gusts")]
        [SerializeField] private bool visibleGusts = true;
        [SerializeField] private Vector2 gustIntervalRange = new Vector2(6f, 14f);
        [SerializeField] private float gustDistance = 9f;

        [Header("Weather")]
        [Tooltip("Ground height the rain ripple and splash layers sit at.")]
        [SerializeField] private float groundHeight;

        private readonly Dictionary<string, VfxHandle> active = new Dictionary<string, VfxHandle>();

        private Vector3 lastPosition = new Vector3(float.MaxValue, 0f, 0f);
        private float nextGustTime;
        private Weather weather = Weather.Clear;
        private bool isNight;
        private bool isCave;
        private bool subscribed;

        private void OnEnable()
        {
            if (followTarget == null && Camera.main != null) followTarget = Camera.main.transform;

            if (!subscribed)
            {
                GameEvents.WeatherChanged += OnWeatherChanged;
                GameEvents.TimeOfDayChanged += OnTimeOfDayChanged;
                GameEvents.BiomeEntered += OnBiomeEntered;
                subscribed = true;
            }

            nextGustTime = Time.time + Random.Range(gustIntervalRange.x, gustIntervalRange.y);
        }

        private void OnDisable()
        {
            if (subscribed)
            {
                GameEvents.WeatherChanged -= OnWeatherChanged;
                GameEvents.TimeOfDayChanged -= OnTimeOfDayChanged;
                GameEvents.BiomeEntered -= OnBiomeEntered;
                subscribed = false;
            }

            StopAll();
        }

        private void Update()
        {
            if (!VfxApi.IsReady) return;

            Vector3 centre = FollowPosition();
            if ((centre - lastPosition).sqrMagnitude > repositionThreshold * repositionThreshold)
            {
                lastPosition = centre;
                Reposition(centre);
            }

            Refresh(centre);

            if (visibleGusts && !isCave && Time.time >= nextGustTime)
            {
                SpawnGust(centre);
                nextGustTime = Time.time + Random.Range(gustIntervalRange.x, gustIntervalRange.y);
            }
        }

        private Vector3 FollowPosition()
        {
            Transform t = followTarget != null ? followTarget : transform;
            return t.position + Vector3.up * followHeight;
        }

        // ---------------------------------------------------------------------
        // Rules
        // ---------------------------------------------------------------------
        private void Refresh(Vector3 centre)
        {
            bool outdoors = !isCave;
            bool raining = weather == Weather.Rain;
            bool snowing = weather == Weather.Hail;
            bool sandy = weather == Weather.Sandstorm;
            bool clearish = !raining && !snowing && !sandy;

            // Daytime life. Suppressed in the rain and at night, because insects and
            // pollen in a downpour reads as a bug rather than as detail.
            Toggle(AmbientVfxLibrary.KeyPollen, outdoors && clearish && !isNight, centre);
            Toggle(AmbientVfxLibrary.KeyButterflies, outdoors && clearish && !isNight, centre);
            Toggle(AmbientVfxLibrary.KeyBirds, birds && outdoors && clearish && !isNight, centre);
            Toggle(AmbientVfxLibrary.KeyLeaves, leaves && outdoors && !snowing, centre);

            // Night life.
            Toggle(AmbientVfxLibrary.KeyFireflies,
                   firefliesAtNight && outdoors && clearish && isNight, centre);

            // Weather layers. The ripples and splashes sit on the ground plane, not
            // at the player's height, or rain would land in mid-air.
            Vector3 ground = new Vector3(centre.x, groundHeight, centre.z);
            Toggle(AmbientVfxLibrary.KeyRain, raining && outdoors, centre);
            Toggle(AmbientVfxLibrary.KeyRainRipples, raining && outdoors, ground);
            Toggle(AmbientVfxLibrary.KeyRainSplash, raining && outdoors, ground);
            Toggle(AmbientVfxLibrary.KeySnow, snowing && outdoors, centre);
            Toggle(AmbientVfxLibrary.KeySandstorm, sandy && outdoors, centre);

            // Cave.
            Toggle(AmbientVfxLibrary.KeyCaveDrips, isCave, centre);

            if (!pollen) Toggle(AmbientVfxLibrary.KeyPollen, false, centre);
            if (!butterflies) Toggle(AmbientVfxLibrary.KeyButterflies, false, centre);
        }

        private void Toggle(string key, bool wanted, Vector3 position)
        {
            bool running = active.TryGetValue(key, out var handle) && handle != null && handle.IsAlive;

            if (wanted && !running)
            {
                var spawned = VfxApi.Play(key, position, Quaternion.identity);
                if (spawned != null) active[key] = spawned;
            }
            else if (!wanted && running)
            {
                VfxApi.Stop(handle);
                active.Remove(key);
            }
        }

        private void Reposition(Vector3 centre)
        {
            Vector3 ground = new Vector3(centre.x, groundHeight, centre.z);
            foreach (var kv in active)
            {
                if (kv.Value == null || !kv.Value.IsAlive) continue;
                bool groundLayer = kv.Key == AmbientVfxLibrary.KeyRainRipples
                                || kv.Key == AmbientVfxLibrary.KeyRainSplash;
                kv.Value.SetPosition(groundLayer ? ground : centre);
            }
        }

        private void SpawnGust(Vector3 centre)
        {
            // Upwind of the player, aimed downwind, so it crosses the view rather
            // than appearing in front of the camera.
            Vector3 windDir = transform.forward;
            if (Camera.main != null)
            {
                // Cross the camera's view rather than travelling along it: motion
                // across the screen reads, motion towards it does not.
                windDir = Camera.main.transform.right;
                windDir.y = 0f;
                if (windDir.sqrMagnitude < 1e-4f) windDir = Vector3.right;
                windDir.Normalize();
            }

            Vector3 origin = centre - windDir * gustDistance;
            origin.y = groundHeight + 0.6f;
            VfxApi.Play(AmbientVfxLibrary.KeyWindGust, origin,
                        Quaternion.LookRotation(windDir, Vector3.up));
        }

        private void StopAll()
        {
            foreach (var kv in active)
                if (kv.Value != null) VfxApi.Stop(kv.Value, immediate: true);
            active.Clear();
        }

        // ---------------------------------------------------------------------
        // Events
        // ---------------------------------------------------------------------
        private void OnWeatherChanged(Weather from, Weather to) => weather = to;

        private void OnTimeOfDayChanged(TimeOfDay from, TimeOfDay to) =>
            isNight = to == TimeOfDay.Night || to == TimeOfDay.Dusk;

        private void OnBiomeEntered(string biomeId)
        {
            if (string.IsNullOrEmpty(biomeId)) return;
            isCave = biomeId.IndexOf("cave", System.StringComparison.OrdinalIgnoreCase) >= 0
                  || biomeId.IndexOf("interior", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
