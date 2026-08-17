using System;
using UnityEngine;
using UnityEngine.Events;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The four areas of the vertical slice. Kept as an enum rather than free strings so
    /// gameplay code can branch on it, while <see cref="WorldZone.BiomeId"/> stays the
    /// string identity that crosses the <see cref="GameEvents.BiomeEntered"/> seam.
    /// </summary>
    public enum ZoneKind
    {
        Route = 0,
        Town = 1,
        Cave = 2,
        Lakeside = 3,
    }

    /// <summary>Where the player's body currently is, which gates which encounter source may fire.</summary>
    public enum TraversalState
    {
        Ground = 0,
        TallGrass = 1,
        Water = 2,
        Frozen = 3,
    }

    /// <summary>
    /// Anything the player can aim the interaction ray at. Implemented by signs, healing
    /// machines, NPCs and item balls. The prompt string is data only — the UI worker
    /// renders it, this assembly never draws.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>Short verb phrase, e.g. "Talk", "Heal party". Shown by the UI layer.</summary>
        string InteractionPrompt { get; }

        /// <summary>False hides the prompt entirely — used for one-shot pickups and beaten trainers.</summary>
        bool CanInteract(GameObject instigator);

        void Interact(GameObject instigator);
    }

    /// <summary>Serializable payload describing an interaction target for the UI prompt.</summary>
    [Serializable]
    public struct InteractionPromptData
    {
        public string Prompt;
        public Vector3 WorldPosition;
        public bool Visible;
    }

    /// <summary>UnityEvent aliases, so the integrator can wire UI/VFX/audio in the inspector
    /// without any assembly ever referencing <c>PokeLab.Overworld</c>. This is deliberate:
    /// inspector wiring is the only zero-coupling hand-off we have across eight workers.</summary>
    [Serializable] public sealed class InteractionPromptEvent : UnityEvent<InteractionPromptData> { }
    [Serializable] public sealed class StringEvent : UnityEvent<string> { }
    [Serializable] public sealed class FloatEvent : UnityEvent<float> { }
    [Serializable] public sealed class BoolEvent : UnityEvent<bool> { }
    [Serializable] public sealed class TransformEvent : UnityEvent<Transform> { }
    [Serializable] public sealed class Vector3Event : UnityEvent<Vector3> { }

    /// <summary>
    /// Overworld-scoped broadcast. Core's <see cref="GameEvents"/> stays small by contract,
    /// so signals that only matter inside exploration live here. Anything that must cross
    /// into battle, lighting or audio also goes through <see cref="GameEvents"/>.
    /// </summary>
    public static class OverworldEvents
    {
        /// <summary>Raised the moment an encounter is telegraphed, before the battle transition.
        /// Carries the world position of the source so VFX can play the rustle there.</summary>
        public static event Action<Vector3> EncounterTelegraphed;

        /// <summary>Raised with the fully built request just before it is handed to <see cref="IGameFlow"/>.</summary>
        public static event Action<EncounterRequest> EncounterCommitted;

        /// <summary>Raised after an <see cref="EncounterResult"/> has been applied to the profile.</summary>
        public static event Action<EncounterResult> EncounterResolved;

        /// <summary>Zone ambience cross-fade. <c>t</c> runs 0 to 1 across the handover so the
        /// lighting worker can blend interior and exterior looks instead of popping.</summary>
        public static event Action<ZoneAmbience, ZoneAmbience, float> AmbienceBlend;

        /// <summary>Weather cross-fade progress, 0 to 1, for the same reason.</summary>
        public static event Action<Weather, Weather, float> WeatherBlend;

        /// <summary>Raised when the party composition or any member's HP changes.</summary>
        public static event Action PartyChanged;

        /// <summary>Raised when the player's traversal state changes (ground, grass, water).</summary>
        public static event Action<TraversalState> TraversalChanged;

        /// <summary>Current interaction prompt, or a hidden payload when nothing is targeted.</summary>
        public static event Action<InteractionPromptData> InteractionPromptChanged;

        /// <summary>A trainer's sight cone caught the player. Fires before the approach walk.</summary>
        public static event Action<string> TrainerSpotted;

        /// <summary>
        /// A named signal fired from dialogue — a line's EventId or a choice's ResultEventId.
        /// This is how a conversation opens a door or hands over an item without the dialogue
        /// system needing to know what doors and items are.
        /// </summary>
        public static event Action<string> DialogueSignal;

        /// <summary>
        /// An item was taken off the ground: the item id, and how many.
        ///
        /// <see cref="ItemPickup"/> already carried a per-instance UnityEvent for this and not
        /// one instance in any scene had a listener wired to it, so a ball picked up went into
        /// the bag and vanished off the ground with nothing on screen either way. The series
        /// always says what you found, and without that line the whole feature reads as absent
        /// rather than as silent. A static event because the announcement belongs to the UI,
        /// which is an assembly the world cannot see.
        /// </summary>
        public static event Action<string, int> ItemCollected;

        public static void RaiseEncounterTelegraphed(Vector3 source) => EncounterTelegraphed?.Invoke(source);
        public static void RaiseEncounterCommitted(EncounterRequest request) => EncounterCommitted?.Invoke(request);
        public static void RaiseEncounterResolved(EncounterResult result) => EncounterResolved?.Invoke(result);
        public static void RaiseAmbienceBlend(ZoneAmbience from, ZoneAmbience to, float t) => AmbienceBlend?.Invoke(from, to, t);
        public static void RaiseWeatherBlend(Weather from, Weather to, float t) => WeatherBlend?.Invoke(from, to, t);
        public static void RaisePartyChanged() => PartyChanged?.Invoke();
        public static void RaiseTraversalChanged(TraversalState state) => TraversalChanged?.Invoke(state);
        public static void RaiseInteractionPromptChanged(InteractionPromptData data) => InteractionPromptChanged?.Invoke(data);
        public static void RaiseTrainerSpotted(string trainerId) => TrainerSpotted?.Invoke(trainerId);
        public static void RaiseDialogueSignal(string signalId) => DialogueSignal?.Invoke(signalId);
        public static void RaiseItemCollected(string itemId, int count) => ItemCollected?.Invoke(itemId, count);

        /// <summary>Detaches every subscriber. Call alongside <see cref="GameEvents.Reset"/> on play mode exit.</summary>
        public static void Reset()
        {
            EncounterTelegraphed = null;
            EncounterCommitted = null;
            EncounterResolved = null;
            AmbienceBlend = null;
            WeatherBlend = null;
            PartyChanged = null;
            TraversalChanged = null;
            InteractionPromptChanged = null;
            TrainerSpotted = null;
            DialogueSignal = null;
            ItemCollected = null;
        }
    }

    /// <summary>
    /// Layer and tag names this assembly expects. Centralised because the integrator has to
    /// create them in ProjectSettings and a typo here is a silent runtime failure, not a
    /// compile error.
    /// </summary>
    public static class OverworldNames
    {
        public const string PlayerTag = "Player";

        public const string LayerGround = "Ground";
        public const string LayerEnvironment = "Environment";
        public const string LayerInteractable = "Interactable";
        public const string LayerCreature = "Creature";
        public const string LayerWater = "Water";
        public const string LayerTrigger = "ZoneTrigger";
    }
}
