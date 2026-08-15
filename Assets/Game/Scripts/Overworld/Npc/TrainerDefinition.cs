using System;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>One member of a trainer's party, as authored.</summary>
    [Serializable]
    public struct TrainerPartyMember
    {
        [Tooltip("KaggleID. Use the SliceRoster constants.")]
        public int SpeciesId;
        [Min(1)] public int Level;
        public string Nickname;
        [Tooltip("Optional held item id.")]
        public string HeldItemId;
    }

    /// <summary>
    /// A trainer's data: party, dialogue, reward and rematch behaviour.
    ///
    /// The battle engine builds the actual opposing party from
    /// <see cref="EncounterRequest.TrainerId"/>, because <c>Core</c> has no trainer-party
    /// contract. Until it does, <see cref="TrainerRegistry"/> exposes this data by id so the
    /// battle worker can resolve it without referencing this assembly's components, and
    /// <see cref="BuildParty"/> produces ready instances for whoever needs them.
    /// </summary>
    [CreateAssetMenu(menuName = "Poké Lab/Overworld/Trainer", fileName = "Trainer")]
    public sealed class TrainerDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable id written into EncounterRequest.TrainerId. Must be unique.")]
        [SerializeField] private string _trainerId = "trainer_youngster_joey";
        [SerializeField] private string _displayName = "Youngster";
        [Tooltip("Class shown in the battle intro, e.g. 'Bug Catcher'.")]
        [SerializeField] private string _trainerClass = "Youngster";

        [Header("Party")]
        [SerializeField] private List<TrainerPartyMember> _party = new List<TrainerPartyMember>();

        [Header("Reward")]
        [Tooltip("Money awarded on defeat. The battle engine reports this via EncounterResult.")]
        [Min(0)][SerializeField] private int _prizeMoney = 400;

        [Header("Dialogue")]
        [SerializeField] private DialogueSequence _preBattle;
        [SerializeField] private DialogueSequence _onDefeat;
        [SerializeField] private DialogueSequence _afterDefeat;
        [SerializeField] private DialogueSequence _onPlayerDefeat;
        [SerializeField] private DialogueSequence _rematchIntro;

        [Header("Rematch")]
        [Tooltip("Allows a second battle after the first defeat.")]
        [SerializeField] private bool _allowsRematch = true;
        [Tooltip("In-game hours before a rematch becomes available.")]
        [Min(0f)][SerializeField] private float _rematchCooldownHours = 24f;
        [Tooltip("Levels added to every party member per rematch, so it stays a fight.")]
        [Min(0)][SerializeField] private int _rematchLevelBonus = 2;

        [Header("Determinism")]
        [Tooltip("Seed for the party's IV roll, so this trainer's party is identical every time.")]
        [SerializeField] private int _partySeed = 8675309;

        public string TrainerId => _trainerId;
        public string DisplayName => _displayName;
        public string TrainerClass => _trainerClass;
        public IReadOnlyList<TrainerPartyMember> Party => _party;
        public int PrizeMoney => _prizeMoney;
        public bool AllowsRematch => _allowsRematch;
        public float RematchCooldownHours => _rematchCooldownHours;
        public int RematchLevelBonus => _rematchLevelBonus;

        public DialogueSequence PreBattle => _preBattle;
        public DialogueSequence OnDefeat => _onDefeat;
        public DialogueSequence AfterDefeat => _afterDefeat;
        public DialogueSequence OnPlayerDefeat => _onPlayerDefeat;
        public DialogueSequence RematchIntro => _rematchIntro;

        /// <summary>Flag key recording that this trainer has been beaten.</summary>
        public string DefeatFlag => "trainer_defeated_" + _trainerId;

        /// <summary>Flag key holding how many times the player has beaten them.</summary>
        public string WinCountFlag => "trainer_wins_" + _trainerId;

        /// <summary>Flag key holding the in-game hour of the last defeat, for the rematch cooldown.</summary>
        public string LastDefeatHourFlag => "trainer_last_" + _trainerId;

        /// <summary>
        /// Builds the opposing party. Levels are raised by <see cref="RematchLevelBonus"/> per
        /// prior win so a rematch is not a walkover, and the seed is fixed so the same trainer
        /// always fields the same creatures.
        /// </summary>
        public List<CreatureInstance> BuildParty(int priorWins = 0)
        {
            var result = new List<CreatureInstance>(_party.Count);
            for (var i = 0; i < _party.Count; i++)
            {
                var member = _party[i];
                if (member.SpeciesId <= 0) continue;

                var level = Mathf.Max(1, member.Level + priorWins * _rematchLevelBonus);
                // Offsetting by slot keeps party members from sharing an identical IV spread.
                var creature = CreatureFactory.Create(member.SpeciesId, level, _partySeed + i * 977);
                creature.Nickname = string.IsNullOrEmpty(member.Nickname) ? null : member.Nickname;
                creature.HeldItemId = member.HeldItemId;
                result.Add(creature);
            }
            return result;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(_trainerId))
                Debug.LogWarning($"[TrainerDefinition] '{name}' has no TrainerId; the battle side "
                                 + "cannot resolve its party.", this);
        }
#endif
    }

    /// <summary>
    /// Runtime lookup from trainer id to definition, so the battle worker can resolve a party from
    /// an <see cref="EncounterRequest.TrainerId"/> without referencing any scene component.
    ///
    /// Populated by <see cref="TrainerController"/> instances as they enable, and by explicit
    /// registration for trainers that are not in the world yet.
    /// </summary>
    public static class TrainerRegistry
    {
        private static readonly Dictionary<string, TrainerDefinition> Definitions =
            new Dictionary<string, TrainerDefinition>();

        public static void Register(TrainerDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.TrainerId)) return;
            Definitions[definition.TrainerId] = definition;
        }

        public static bool TryGet(string trainerId, out TrainerDefinition definition)
        {
            definition = null;
            return !string.IsNullOrEmpty(trainerId) && Definitions.TryGetValue(trainerId, out definition);
        }

        /// <summary>Convenience for the battle side: the ready-to-fight party for a trainer id.</summary>
        public static List<CreatureInstance> BuildParty(string trainerId)
        {
            if (!TryGet(trainerId, out var definition)) return new List<CreatureInstance>();

            var priorWins = 0;
            if (ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete)
                priorWins = concrete.GetFlagInt(definition.WinCountFlag);

            return definition.BuildParty(priorWins);
        }

        /// <summary>Clears the registry. Called alongside <see cref="ServiceHub.Reset"/>.</summary>
        public static void Reset() => Definitions.Clear();
    }
}
