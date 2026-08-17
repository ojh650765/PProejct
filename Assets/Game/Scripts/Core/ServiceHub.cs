using System;
using System.Collections.Generic;

namespace PokeLab.Core
{
    /// <summary>
    /// Minimal service locator. It exists so eight independently developed systems can
    /// find each other without any of them referencing another's assembly — every
    /// dependency is expressed as a Core interface and resolved here at runtime.
    ///
    /// Register during boot only. Resolving an unregistered service throws rather than
    /// returning null, because a silent null here surfaces much later as an unrelated bug.
    /// </summary>
    public static class ServiceHub
    {
        private static readonly Dictionary<Type, object> Services = new Dictionary<Type, object>();

        public static void Register<T>(T service) where T : class
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            Services[typeof(T)] = service;
        }

        /// <summary>
        /// Drops the registration for <typeparamref name="T"/>.
        ///
        /// The hub had no way to give a service back, which mattered because it outlives every
        /// scene — the boot object is <c>DontDestroyOnLoad</c>. A scene-owned service registered
        /// itself once and then stayed on the hub as a destroyed object after the next load, and
        /// the first caller to resolve it threw from somewhere unrelated.
        /// </summary>
        public static void Unregister<T>() where T : class => Services.Remove(typeof(T));

        /// <summary>
        /// Drops the registration only while it is still <paramref name="instance"/>.
        ///
        /// This is the overload a component's <c>OnDestroy</c> wants. A duplicate arriving with an
        /// additively streamed scene and then stripping itself must not deregister the live
        /// service, and neither must the outgoing scene's copy once the incoming one has already
        /// claimed the slot.
        /// </summary>
        public static void Unregister<T>(T instance) where T : class
        {
            if (instance == null) return;
            if (Services.TryGetValue(typeof(T), out var stored) && ReferenceEquals(stored, instance))
                Services.Remove(typeof(T));
        }

        public static T Get<T>() where T : class
        {
            if (TryGet<T>(out var service)) return service;
            throw new InvalidOperationException(
                $"No {typeof(T).Name} registered. Boot order problem: register it before anything resolves it.");
        }

        public static bool TryGet<T>(out T service) where T : class
        {
            if (Services.TryGetValue(typeof(T), out var found) && IsAlive(found))
            {
                service = (T)found;
                return true;
            }
            service = null;
            return false;
        }

        public static bool Has<T>() where T : class => TryGet<T>(out _);

        /// <summary>
        /// Whether a stored service is still usable.
        ///
        /// A destroyed <see cref="UnityEngine.Object"/> is not a null reference — it is a live
        /// managed wrapper around a native object that has gone — so the dictionary happily hands
        /// it back and the caller gets a MissingReferenceException on first use, a long way from
        /// the scene load that caused it. Treating one as absent turns that into the ordinary
        /// "nothing registered" path every caller already handles.
        /// </summary>
        private static bool IsAlive(object service) =>
            service != null && !(service is UnityEngine.Object unityObject && unityObject == null);

        /// <summary>Clears everything. Called on play mode exit so domain reload stays clean.</summary>
        public static void Reset() => Services.Clear();
    }

    /// <summary>
    /// Global broadcast for cross-system moments that are not battle events.
    /// Keep this small: anything battle-shaped belongs in the BattleEvent stream instead.
    /// </summary>
    public static class GameEvents
    {
        public static event Action<Weather, Weather> WeatherChanged;
        public static event Action<TimeOfDay, TimeOfDay> TimeOfDayChanged;
        public static event Action<float> TimeOfDayProgress;
        public static event Action<string> BiomeEntered;
        public static event Action<int> SpeciesSeen;
        public static event Action<CreatureInstance> CreatureCaught;
        public static event Action<GameMode, GameMode> ModeChanged;

        public static void RaiseWeatherChanged(Weather from, Weather to) => WeatherChanged?.Invoke(from, to);
        public static void RaiseTimeOfDayChanged(TimeOfDay from, TimeOfDay to) => TimeOfDayChanged?.Invoke(from, to);
        /// <summary>Normalised 0-1 position through the full day cycle.</summary>
        public static void RaiseTimeOfDayProgress(float normalised) => TimeOfDayProgress?.Invoke(normalised);
        public static void RaiseBiomeEntered(string biomeId) => BiomeEntered?.Invoke(biomeId);
        public static void RaiseSpeciesSeen(int speciesId) => SpeciesSeen?.Invoke(speciesId);
        public static void RaiseCreatureCaught(CreatureInstance creature) => CreatureCaught?.Invoke(creature);
        public static void RaiseModeChanged(GameMode from, GameMode to) => ModeChanged?.Invoke(from, to);

        /// <summary>Detaches every subscriber. Called alongside <see cref="ServiceHub.Reset"/>.</summary>
        public static void Reset()
        {
            WeatherChanged = null;
            TimeOfDayChanged = null;
            TimeOfDayProgress = null;
            BiomeEntered = null;
            SpeciesSeen = null;
            CreatureCaught = null;
            ModeChanged = null;
        }
    }
}
