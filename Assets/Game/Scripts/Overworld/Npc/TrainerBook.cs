using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Builds a <see cref="TrainerDefinition"/> from the authored trainer table.
    ///
    /// <c>TrainerController</c> takes its definition as a serialized ScriptableObject,
    /// which is right for one hand-placed boss and wrong for this level: the trainers are
    /// generated from <c>slice_layout.json</c>, so anything dragged onto them is thrown
    /// away by the next rebuild. Nothing was dragging one either, so <c>_definition</c> was
    /// null on every placed trainer — and <c>CanInteract</c> tests it, which meant not one
    /// of them could be challenged at all.
    ///
    /// The data was already complete and already shaped for this. The field names in
    /// <c>trainers.json</c> match the private serialized names on the definition
    /// (<c>_trainerId</c>, <c>_party</c>, <c>_prizeMoney</c>), which is what lets
    /// <c>JsonUtility.FromJsonOverwrite</c> fill an instance directly rather than needing a
    /// parallel DTO and a copy for every field.
    ///
    /// Dialogue is the one thing JSON cannot carry, because the definition holds object
    /// references. The table stores sequence ids — <c>"riv_pre"</c>, <c>"riv_lost"</c> —
    /// and they are resolved through <see cref="DialogueBook"/>, which is where every other
    /// line in the game already comes from.
    /// </summary>
    public sealed class TrainerBook
    {
        public const string BookPath = "Assets/Game/Data/Story/trainers.json";
        public const string BookResourceName = "trainers";

        private static TrainerBook _shared;

        private readonly Dictionary<string, TrainerRow> _rows =
            new Dictionary<string, TrainerRow>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static TrainerBook Shared => _shared ??= Load();

        /// <summary>Drops the cache so an edited table is picked up without a domain reload.</summary>
        public static void Reset() => _shared = null;

        public int Count => _rows.Count;

        private static TrainerBook Load()
        {
            var book = new TrainerBook();

            var asset = Resources.Load<TextAsset>(BookResourceName);
            var json = asset != null ? asset.text : ReadFromDisk();
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning($"[Trainers] No trainer table found at {BookPath}, so every " +
                                 "trainer in the world is a person who cannot be fought.");
                return book;
            }

            TrainerFile file;
            try { file = JsonUtility.FromJson<TrainerFile>(json); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Trainers] The trainer table would not parse: {e.Message}");
                return book;
            }

            foreach (var row in file?.trainers ?? Array.Empty<TrainerRow>())
            {
                if (row == null || string.IsNullOrEmpty(row._trainerId)) continue;
                book._rows[row._trainerId] = row;
                // Indexed by the scene object too, because the level names trainers by where
                // they stand — Trainer_Route_Youngster — and the table names them by who
                // they are. Either handle finds the same row.
                if (!string.IsNullOrEmpty(row.sceneObject)) book._rows[row.sceneObject] = row;
                if (!string.IsNullOrEmpty(row.npcId)) book._rows[row.npcId] = row;
            }
            return book;
        }

        private static string ReadFromDisk()
        {
#if UNITY_EDITOR
            var path = Path.Combine(Directory.GetCurrentDirectory(), BookPath);
            return File.Exists(path) ? File.ReadAllText(path) : null;
#else
            return null;
#endif
        }

        /// <summary>
        /// A definition for this trainer, or null when the table does not describe them.
        ///
        /// Created fresh per call and owned by the caller. These are runtime instances that
        /// must never be saved into a scene, which is what <c>HideAndDontSave</c> is for —
        /// a definition that ends up serialized is a second, stale copy of a row that is
        /// still being edited in the table.
        /// </summary>
        public TrainerDefinition Build(string trainerIdOrObjectName)
        {
            if (string.IsNullOrEmpty(trainerIdOrObjectName)) return null;
            if (!_rows.TryGetValue(trainerIdOrObjectName, out var row))
            {
                if (_warned.Add(trainerIdOrObjectName))
                    Debug.LogWarning($"[Trainers] '{trainerIdOrObjectName}' is not in the " +
                                     "trainer table, so they cannot be challenged.");
                return null;
            }

            var definition = ScriptableObject.CreateInstance<TrainerDefinition>();
            definition.name = "~Trainer_" + row._trainerId;
            definition.hideFlags = HideFlags.HideAndDontSave;

            // Fills every field whose JSON name matches, which is all of them except the
            // dialogue references — those are ids in the table and objects on the type.
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(row), definition);

            definition.BindDialogue(
                Sequence(row._preBattle),
                Sequence(row._onDefeat),
                Sequence(row._afterDefeat),
                Sequence(row._onPlayerDefeat),
                Sequence(row._rematchIntro));

            return definition;
        }

        private static DialogueSequence Sequence(string id) =>
            string.IsNullOrEmpty(id) ? null : DialogueBook.Shared?.Build(id);

        [Serializable] private sealed class TrainerFile { public TrainerRow[] trainers; }

        /// <summary>
        /// One row, named field-for-field after TrainerDefinition's own serialized fields so
        /// FromJsonOverwrite can do the work. The dialogue members are strings here and
        /// object references there, which is the one place the shapes diverge.
        /// </summary>
        [Serializable]
        private sealed class TrainerRow
        {
            public string _trainerId;
            public string _displayName;
            public string _trainerClass;
            public string npcId;
            public string sceneObject;
            public int _prizeMoney;
            public int _partySeed;
            public string _preBattle;
            public string _onDefeat;
            public string _onPlayerDefeat;
            public string _afterDefeat;
            public string _rematchIntro;
            public bool _allowsRematch;
            public float _rematchCooldownHours;
            public int _rematchLevelBonus;
        }
    }
}
