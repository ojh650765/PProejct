using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Versioned JSON persistence under <see cref="Application.persistentDataPath"/>.
    ///
    /// Two deliberate choices:
    ///
    /// - <b>Write to a temporary file, then replace.</b> A crash or a force-quit mid-write leaves
    ///   the previous save intact instead of a truncated one. Losing a session is annoying; losing
    ///   the file is a bug report.
    /// - <b>Refuse forward versions.</b> A file written by a newer build is not parsed on a best
    ///   effort basis, because a partially-understood save silently deletes whatever it did not
    ///   understand the next time it is written back.
    /// </summary>
    public static class SaveSystem
    {
        private const string FileName = "pokelab_save.json";
        private const string BackupName = "pokelab_save.backup.json";
        private const string TempName = "pokelab_save.tmp";

        public static string SavePath => Path.Combine(Application.persistentDataPath, FileName);
        public static string BackupPath => Path.Combine(Application.persistentDataPath, BackupName);

        public static bool SaveExists() => File.Exists(SavePath);

        /// <summary>Serializes the profile and the world snapshot. Returns false on an I/O failure.</summary>
        public static bool Save(PlayerProfile profile, WorldSave world)
        {
            if (profile == null) return false;

            try
            {
                var save = Capture(profile, world);
                var json = JsonUtility.ToJson(save, prettyPrint: true);

                var tempPath = Path.Combine(Application.persistentDataPath, TempName);
                File.WriteAllText(tempPath, json);

                // Keep the previous file as a backup before the swap, so a corrupt new write is
                // recoverable rather than terminal.
                if (File.Exists(SavePath))
                {
                    if (File.Exists(BackupPath)) File.Delete(BackupPath);
                    File.Move(SavePath, BackupPath);
                }
                File.Move(tempPath, SavePath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveSystem] Save failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Loads into the supplied profile. Returns the world snapshot on success and null on any
        /// failure, so the caller can distinguish "no save" from "loaded".
        /// </summary>
        public static WorldSave Load(PlayerProfile profile)
        {
            if (profile == null) return null;

            var save = ReadFile(SavePath);
            if (save == null)
            {
                save = ReadFile(BackupPath);
                if (save != null) Debug.LogWarning("[SaveSystem] Primary save unreadable; recovered from backup.");
            }
            if (save == null) return null;

            if (save.Version > SaveGame.CurrentVersion)
            {
                Debug.LogError($"[SaveSystem] Save is version {save.Version}, this build understands "
                               + $"{SaveGame.CurrentVersion}. Refusing to load rather than corrupting it.");
                return null;
            }

            if (save.Version < SaveGame.CurrentVersion && !Migrate(save)) return null;

            Apply(save, profile);
            return save.World ?? new WorldSave();
        }

        private static SaveGame ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                var save = JsonUtility.FromJson<SaveGame>(json);
                // JsonUtility returns a default-constructed object for malformed input rather than
                // throwing, so validate a field that can never legitimately be zero.
                return save != null && save.Version > 0 ? save : null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveSystem] Could not read '{path}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Brings an older file up to the current shape, in place. Each step handles exactly one
        /// version bump so the chain stays readable as versions accumulate.
        /// </summary>
        private static bool Migrate(SaveGame save)
        {
            // Version 1 is the first shipped format; there is nothing before it to migrate from.
            // When version 2 lands, add: if (save.Version == 1) { ...; save.Version = 2; }
            if (save.Version == SaveGame.CurrentVersion) return true;

            Debug.LogError($"[SaveSystem] No migration path from version {save.Version}.");
            return false;
        }

        private static SaveGame Capture(PlayerProfile profile, WorldSave world)
        {
            var save = new SaveGame
            {
                Version = SaveGame.CurrentVersion,
                SavedAtUtc = DateTime.UtcNow.ToString("o"),
                PlayTimeSeconds = profile.PlayTimeSeconds,
                TrainerName = profile.TrainerName,
                Money = profile.Money,
                World = world ?? new WorldSave(),
            };

            save.Party = ToSaves(profile.Party);
            save.Storage = ToSaves(profile.Storage);
            save.SeenSpecies = ToArray(profile.SeenSpecies);
            save.CaughtSpecies = ToArray(profile.CaughtSpecies);

            save.Inventory = new ItemStack[profile.Inventory.Count];
            var index = 0;
            foreach (var pair in profile.Inventory)
                save.Inventory[index++] = new ItemStack { ItemId = pair.Key, Count = pair.Value };

            save.Flags = new FlagEntry[profile.Flags.Count];
            index = 0;
            foreach (var pair in profile.Flags)
                save.Flags[index++] = new FlagEntry { Key = pair.Key, Value = pair.Value };

            return save;
        }

        private static void Apply(SaveGame save, PlayerProfile profile)
        {
            var party = ToInstances(save.Party);
            var storage = ToInstances(save.Storage);

            var inventory = new List<KeyValuePair<string, int>>();
            if (save.Inventory != null)
                for (var i = 0; i < save.Inventory.Length; i++)
                    if (!string.IsNullOrEmpty(save.Inventory[i].ItemId))
                        inventory.Add(new KeyValuePair<string, int>(save.Inventory[i].ItemId, save.Inventory[i].Count));

            var flags = new List<KeyValuePair<string, string>>();
            if (save.Flags != null)
                for (var i = 0; i < save.Flags.Length; i++)
                    if (!string.IsNullOrEmpty(save.Flags[i].Key))
                        flags.Add(new KeyValuePair<string, string>(save.Flags[i].Key, save.Flags[i].Value));

            profile.RestoreFrom(save.TrainerName, save.Money, party, storage,
                save.SeenSpecies, save.CaughtSpecies, inventory, flags);
            profile.PlayTimeSeconds = save.PlayTimeSeconds;

            if (save.World != null)
            {
                profile.LastPosition = save.World.PlayerPosition;
                profile.LastRotation = save.World.PlayerRotation;
                profile.LastBiomeId = save.World.BiomeId;
            }
        }

        private static CreatureSave[] ToSaves(IReadOnlyList<CreatureInstance> creatures)
        {
            if (creatures == null || creatures.Count == 0) return Array.Empty<CreatureSave>();
            var result = new CreatureSave[creatures.Count];
            for (var i = 0; i < creatures.Count; i++) result[i] = CreatureSave.From(creatures[i]);
            return result;
        }

        private static List<CreatureInstance> ToInstances(CreatureSave[] saves)
        {
            var result = new List<CreatureInstance>();
            if (saves == null) return result;
            for (var i = 0; i < saves.Length; i++)
            {
                if (saves[i] == null || saves[i].SpeciesId <= 0) continue;
                result.Add(saves[i].ToInstance());
            }
            return result;
        }

        private static int[] ToArray(IReadOnlyCollection<int> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<int>();
            var result = new int[source.Count];
            var index = 0;
            foreach (var value in source) result[index++] = value;
            return result;
        }

        /// <summary>Deletes both the save and its backup. Used by a "new game" that overwrites.</summary>
        public static void Delete()
        {
            try
            {
                if (File.Exists(SavePath)) File.Delete(SavePath);
                if (File.Exists(BackupPath)) File.Delete(BackupPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveSystem] Delete failed: {ex.Message}");
            }
        }
    }
}
