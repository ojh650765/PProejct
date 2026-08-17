using System;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The serialized shape of a save file.
    ///
    /// Everything here is a <c>[Serializable]</c> class of arrays and primitives because
    /// <see cref="JsonUtility"/> — which is the only allocation-free JSON path available without
    /// pulling in a dependency the other seven workers would also have to take — cannot serialize
    /// dictionaries, sets, interfaces or polymorphic types. Dictionaries are flattened into
    /// key/value arrays on save and rebuilt on load.
    ///
    /// <see cref="Version"/> is checked on load. Files from a future version are refused rather
    /// than partially parsed, and older versions are migrated forward in
    /// <see cref="SaveSystem.Migrate"/>.
    /// </summary>
    [Serializable]
    public sealed class SaveGame
    {
        /// <summary>Bump on every breaking shape change and add a migration step.</summary>
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public string SavedAtUtc;
        public float PlayTimeSeconds;

        public string TrainerName = "Trainer";
        public int Money;

        public CreatureSave[] Party = Array.Empty<CreatureSave>();
        public CreatureSave[] Storage = Array.Empty<CreatureSave>();

        public int[] SeenSpecies = Array.Empty<int>();
        public int[] CaughtSpecies = Array.Empty<int>();

        public ItemStack[] Inventory = Array.Empty<ItemStack>();
        public FlagEntry[] Flags = Array.Empty<FlagEntry>();

        public WorldSave World = new WorldSave();
    }

    /// <summary>World state that must survive a reload for the world to feel continuous.</summary>
    [Serializable]
    public sealed class WorldSave
    {
        public string BiomeId;

        /// <summary>
        /// The scene the save was written in.
        ///
        /// A position on its own is not enough to restore anybody. Caves and interiors are separate
        /// scenes with their own coordinate spaces — that is not a shortcut, the overworld's height
        /// grid describes the outside of the massif — so a point saved at a grotto's floor names
        /// somewhere on the summit above it in the overworld, or the inside of a wall in a house.
        /// </summary>
        public string SceneName;

        public Vector3 PlayerPosition;
        public Quaternion PlayerRotation = Quaternion.identity;

        /// <summary>
        /// Whether <see cref="PlayerPosition"/> was actually written.
        ///
        /// The old test was <c>PlayerPosition != Vector3.zero</c>, which quietly makes one legitimate
        /// spot in the world unsaveable: the origin is walkable in the town, and a save taken there
        /// was thrown away on every load.
        /// </summary>
        public bool HasPlayerPosition;

        /// <summary>Normalised clock position, so you load back into the same evening.</summary>
        public float TimeOfDayNormalised = 0.34f;

        public int Weather;

        /// <summary>Encounter world seed and check counter, so the encounter stream resumes.</summary>
        public int EncounterSeed;
        public int EncounterCheckCounter;

        /// <summary>
        /// Whether <see cref="EncounterSeed"/> was actually written. Zero is a perfectly good seed
        /// and the old <c>!= 0</c> guard refused to restore it, so a session seeded at zero resumed
        /// on a different encounter stream from the one it had saved — which is the one thing the
        /// deterministic model is for.
        /// </summary>
        public bool HasEncounterSeed;

        /// <summary>
        /// True for a file written before the identity fields above existed.
        ///
        /// Every file this build writes carries a scene name, because the active scene always has
        /// one — so an empty name can only mean an older shape, whose flags are all default-false.
        /// Reading those as "nothing was saved" would strand every existing save at its spawn
        /// marker, so they fall back to the sentinels the old code used. Bumping
        /// <see cref="SaveGame.CurrentVersion"/> would be the tidier home for this, and
        /// <see cref="SaveSystem.Migrate"/> refuses any version it has no step for — so it would
        /// reject every file on disk instead.
        /// </summary>
        public bool IsLegacyShape => string.IsNullOrEmpty(SceneName);

        /// <summary>Whether there is a saved position worth restoring at all.</summary>
        public bool HasSavedPosition => IsLegacyShape ? PlayerPosition != Vector3.zero : HasPlayerPosition;

        /// <summary>Whether there is a saved encounter seed worth restoring.</summary>
        public bool HasSavedEncounterSeed => IsLegacyShape ? EncounterSeed != 0 : HasEncounterSeed;

        /// <summary>
        /// Whether the saved position means anything in <paramref name="sceneName"/>. A legacy file
        /// names no scene, so it is trusted the way it always was.
        /// </summary>
        public bool PositionAppliesTo(string sceneName) =>
            IsLegacyShape || string.Equals(SceneName, sceneName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One creature, flattened. <see cref="CreatureInstance"/> is itself <c>[Serializable]</c>, but
    /// serializing it directly would couple the save format to a Core type that the battle worker
    /// may extend — a field added there would silently change every existing save file's shape.
    /// The explicit copy makes that a compile error instead.
    /// </summary>
    [Serializable]
    public sealed class CreatureSave
    {
        public int SpeciesId;
        public string Nickname;
        public int Level;
        public int Experience;
        public int CurrentHp;
        public int[] Ivs = Array.Empty<int>();
        public string AbilityId;
        public string HeldItemId;
        public int Status;
        public int StatusCounter;
        public string InstanceId;
        public MoveSave[] Moves = Array.Empty<MoveSave>();

        public static CreatureSave From(CreatureInstance creature)
        {
            var save = new CreatureSave
            {
                SpeciesId = creature.SpeciesId,
                Nickname = creature.Nickname,
                Level = creature.Level,
                Experience = creature.Experience,
                CurrentHp = creature.CurrentHp,
                AbilityId = creature.AbilityId,
                HeldItemId = creature.HeldItemId,
                Status = (int)creature.Status,
                StatusCounter = creature.StatusCounter,
                InstanceId = creature.InstanceId,
                Ivs = creature.Ivs != null ? (int[])creature.Ivs.Clone() : new int[StatKinds.BaseCount],
            };

            if (creature.Moves != null)
            {
                save.Moves = new MoveSave[creature.Moves.Count];
                for (var i = 0; i < creature.Moves.Count; i++)
                {
                    save.Moves[i] = new MoveSave
                    {
                        MoveId = creature.Moves[i].MoveId,
                        CurrentPp = creature.Moves[i].CurrentPp,
                        MaxPp = creature.Moves[i].MaxPp,
                    };
                }
            }
            return save;
        }

        /// <summary>
        /// Rebuilds a live instance. Derived stats are recomputed rather than stored, so a save
        /// made before the species dex loaded is corrected the moment the real data is available.
        /// </summary>
        public CreatureInstance ToInstance()
        {
            var creature = new CreatureInstance
            {
                SpeciesId = SpeciesId,
                Nickname = Nickname,
                Level = Mathf.Max(1, Level),
                Experience = Experience,
                AbilityId = AbilityId,
                HeldItemId = HeldItemId,
                Status = (StatusCondition)Status,
                StatusCounter = StatusCounter,
                // A restored save keeps its stored id; only a save written before ids existed
                // needs one, and it must be derived rather than random so replays stay stable.
                InstanceId = string.IsNullOrEmpty(InstanceId)
                    ? CreatureIds.Derive(SpeciesId, Mathf.Max(1, Level), Experience, 0)
                    : InstanceId,
                Ivs = Ivs != null && Ivs.Length >= StatKinds.BaseCount ? (int[])Ivs.Clone() : new int[StatKinds.BaseCount],
                Moves = new List<MoveSlot>(4),
            };

            if (Moves != null)
            {
                for (var i = 0; i < Moves.Length; i++)
                {
                    creature.Moves.Add(new MoveSlot
                    {
                        MoveId = Moves[i].MoveId,
                        CurrentPp = Moves[i].CurrentPp,
                        MaxPp = Moves[i].MaxPp,
                    });
                }
            }

            CreatureFactory.ApplyStats(creature);
            // Applied after the stat rebuild so a max-HP change does not resurrect a fainted member.
            creature.CurrentHp = Mathf.Clamp(CurrentHp, 0, creature.MaxHp);
            return creature;
        }
    }

    [Serializable]
    public struct MoveSave
    {
        public string MoveId;
        public int CurrentPp;
        public int MaxPp;
    }

    [Serializable]
    public struct ItemStack
    {
        public string ItemId;
        public int Count;
    }

    [Serializable]
    public struct FlagEntry
    {
        public string Key;
        public string Value;
    }
}
