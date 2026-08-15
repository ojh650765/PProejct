using System;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>Result of a party operation, so callers can tell the player why something failed.</summary>
    public enum PartyOperationResult
    {
        Success = 0,
        PartyFull,
        StorageFull,
        InvalidIndex,
        LastHealthyMember,
        ItemUnavailable,
        NoEffect,
    }

    /// <summary>
    /// The concrete <see cref="IPlayerProfile"/>: party, storage, money, bag, dex flags and the
    /// world flags that remember which trainers you have beaten.
    ///
    /// It is a plain C# class rather than a MonoBehaviour so it can be constructed, serialized and
    /// unit-tested without a scene; <see cref="PlayerProfileHost"/> owns its lifetime and registers
    /// it with <see cref="ServiceHub"/>.
    ///
    /// The interface exposes read-only views. Mutation goes through the named operations here, so
    /// every change has exactly one place to fire <see cref="OverworldEvents.PartyChanged"/> from
    /// and the UI never has to poll.
    /// </summary>
    public sealed class PlayerProfile : IPlayerProfile
    {
        /// <summary>Party size. Fixed by the genre, not a tuning value.</summary>
        public const int MaxPartySize = 6;

        /// <summary>Storage cap. Generous, but bounded so a save file cannot grow without limit.</summary>
        public const int MaxStorageSize = 120;

        private readonly List<CreatureInstance> _party = new List<CreatureInstance>(MaxPartySize);
        private readonly List<CreatureInstance> _storage = new List<CreatureInstance>();
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly HashSet<int> _caught = new HashSet<int>();
        private readonly Dictionary<string, int> _inventory = new Dictionary<string, int>();
        private readonly Dictionary<string, string> _flags = new Dictionary<string, string>();

        public string TrainerName { get; private set; } = "Trainer";
        public int Money { get; private set; } = 3000;

        public IReadOnlyList<CreatureInstance> Party => _party;
        public IReadOnlyList<CreatureInstance> Storage => _storage;
        public IReadOnlyCollection<int> SeenSpecies => _seen;
        public IReadOnlyCollection<int> CaughtSpecies => _caught;
        public IReadOnlyDictionary<string, int> Inventory => _inventory;

        /// <summary>World flags: trainer defeats, one-shot events, rematch counters.</summary>
        public IReadOnlyDictionary<string, string> Flags => _flags;

        /// <summary>Where the player was standing at the last save, for restoring position.</summary>
        public Vector3 LastPosition { get; set; }
        public Quaternion LastRotation { get; set; } = Quaternion.identity;
        public string LastBiomeId { get; set; }

        /// <summary>Total play seconds, accumulated by <see cref="PlayerProfileHost"/>.</summary>
        public float PlayTimeSeconds { get; set; }

        /// <summary>Raised on any mutation, so the UI can rebind without polling.</summary>
        public event Action Changed;

        public void SetTrainerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            TrainerName = name.Trim();
            RaiseChanged();
        }

        // ---- Party -----------------------------------------------------------------------

        public bool TryAddToParty(CreatureInstance creature)
        {
            if (creature == null) return false;
            if (_party.Count >= MaxPartySize) return false;
            _party.Add(creature);
            MarkCaught(creature.SpeciesId);
            RaiseChanged();
            return true;
        }

        /// <summary>Deposits a creature the party could not hold. Returns false only if storage is full.</summary>
        public bool SendToStorage(CreatureInstance creature)
        {
            if (creature == null) return false;
            if (_storage.Count >= MaxStorageSize) return false;
            _storage.Add(creature);
            MarkCaught(creature.SpeciesId);
            RaiseChanged();
            return true;
        }

        /// <summary>Moves a party member to a different slot, shifting the rest.</summary>
        public PartyOperationResult ReorderParty(int fromIndex, int toIndex)
        {
            if (!IsValidPartyIndex(fromIndex) || toIndex < 0 || toIndex >= _party.Count)
                return PartyOperationResult.InvalidIndex;
            if (fromIndex == toIndex) return PartyOperationResult.NoEffect;

            var creature = _party[fromIndex];
            _party.RemoveAt(fromIndex);
            _party.Insert(toIndex, creature);
            RaiseChanged();
            return PartyOperationResult.Success;
        }

        /// <summary>Swaps two party slots outright. Used by drag-and-drop in the party UI.</summary>
        public PartyOperationResult SwapPartySlots(int a, int b)
        {
            if (!IsValidPartyIndex(a) || !IsValidPartyIndex(b)) return PartyOperationResult.InvalidIndex;
            if (a == b) return PartyOperationResult.NoEffect;
            (_party[a], _party[b]) = (_party[b], _party[a]);
            RaiseChanged();
            return PartyOperationResult.Success;
        }

        /// <summary>Deposits a party member into storage.</summary>
        public PartyOperationResult DepositToStorage(int partyIndex)
        {
            if (!IsValidPartyIndex(partyIndex)) return PartyOperationResult.InvalidIndex;
            if (_storage.Count >= MaxStorageSize) return PartyOperationResult.StorageFull;

            // Never leave the player with an empty or entirely fainted party — they would be stuck
            // in the overworld unable to win the next encounter and unable to heal past it.
            if (CountHealthy(exceptPartyIndex: partyIndex) == 0) return PartyOperationResult.LastHealthyMember;

            var creature = _party[partyIndex];
            _party.RemoveAt(partyIndex);
            _storage.Add(creature);
            RaiseChanged();
            return PartyOperationResult.Success;
        }

        /// <summary>Withdraws a stored creature into the party.</summary>
        public PartyOperationResult WithdrawFromStorage(int storageIndex)
        {
            if (storageIndex < 0 || storageIndex >= _storage.Count) return PartyOperationResult.InvalidIndex;
            if (_party.Count >= MaxPartySize) return PartyOperationResult.PartyFull;

            var creature = _storage[storageIndex];
            _storage.RemoveAt(storageIndex);
            _party.Add(creature);
            RaiseChanged();
            return PartyOperationResult.Success;
        }

        /// <summary>Swaps a party member for a stored one in a single operation.</summary>
        public PartyOperationResult SwapWithStorage(int partyIndex, int storageIndex)
        {
            if (!IsValidPartyIndex(partyIndex)) return PartyOperationResult.InvalidIndex;
            if (storageIndex < 0 || storageIndex >= _storage.Count) return PartyOperationResult.InvalidIndex;

            var incoming = _storage[storageIndex];
            if (incoming.IsFainted && CountHealthy(exceptPartyIndex: partyIndex) == 0)
                return PartyOperationResult.LastHealthyMember;

            _storage[storageIndex] = _party[partyIndex];
            _party[partyIndex] = incoming;
            RaiseChanged();
            return PartyOperationResult.Success;
        }

        /// <summary>Restores the whole party. The healing machine's effect.</summary>
        public void HealParty()
        {
            for (var i = 0; i < _party.Count; i++) CreatureFactory.FullHeal(_party[i]);
            RaiseChanged();
        }

        /// <summary>First party member that can still fight, or null when the player has whited out.</summary>
        public CreatureInstance FirstHealthy()
        {
            for (var i = 0; i < _party.Count; i++)
                if (_party[i] != null && !_party[i].IsFainted) return _party[i];
            return null;
        }

        public bool HasUsableParty() => FirstHealthy() != null;

        private int CountHealthy(int exceptPartyIndex = -1)
        {
            var count = 0;
            for (var i = 0; i < _party.Count; i++)
            {
                if (i == exceptPartyIndex) continue;
                if (_party[i] != null && !_party[i].IsFainted) count++;
            }
            return count;
        }

        private bool IsValidPartyIndex(int index) => index >= 0 && index < _party.Count;

        // ---- Items -----------------------------------------------------------------------

        public void AddItem(string itemId, int count)
        {
            if (string.IsNullOrEmpty(itemId) || count == 0) return;
            _inventory.TryGetValue(itemId, out var existing);
            var total = existing + count;
            if (total <= 0) _inventory.Remove(itemId);
            else _inventory[itemId] = total;
            RaiseChanged();
        }

        public bool ConsumeItem(string itemId, int count = 1)
        {
            if (string.IsNullOrEmpty(itemId) || count <= 0) return false;
            if (!_inventory.TryGetValue(itemId, out var existing) || existing < count) return false;

            var remaining = existing - count;
            if (remaining <= 0) _inventory.Remove(itemId);
            else _inventory[itemId] = remaining;
            RaiseChanged();
            return true;
        }

        public int ItemCount(string itemId) =>
            !string.IsNullOrEmpty(itemId) && _inventory.TryGetValue(itemId, out var count) ? count : 0;

        /// <summary>
        /// Field item use — potions and status cures outside battle. In-battle item use belongs to
        /// the battle engine via <see cref="BattleAction.UseItem"/>; this path is the menu only.
        /// </summary>
        public PartyOperationResult UseItemOnPartyMember(string itemId, int partyIndex)
        {
            if (!IsValidPartyIndex(partyIndex)) return PartyOperationResult.InvalidIndex;
            if (ItemCount(itemId) <= 0) return PartyOperationResult.ItemUnavailable;

            var creature = _party[partyIndex];
            if (creature == null) return PartyOperationResult.InvalidIndex;
            if (!FieldItems.TryApply(itemId, creature)) return PartyOperationResult.NoEffect;

            ConsumeItem(itemId);
            RaiseChanged();
            return PartyOperationResult.Success;
        }

        public void AddMoney(int delta)
        {
            if (delta == 0) return;
            Money = Mathf.Max(0, Money + delta);
            RaiseChanged();
        }

        public bool TrySpend(int amount)
        {
            if (amount <= 0 || Money < amount) return false;
            Money -= amount;
            RaiseChanged();
            return true;
        }

        // ---- Dex -------------------------------------------------------------------------

        public void MarkSeen(int speciesId)
        {
            if (speciesId <= 0) return;
            if (_seen.Add(speciesId)) RaiseChanged();
        }

        public void MarkCaught(int speciesId)
        {
            if (speciesId <= 0) return;
            var changed = _caught.Add(speciesId);
            changed |= _seen.Add(speciesId); // catching implies seeing
            if (changed) RaiseChanged();
        }

        // ---- Flags -----------------------------------------------------------------------

        /// <summary>Sets a world flag. Trainer defeats and one-shot events live here.</summary>
        public void SetFlag(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _flags[key] = value ?? string.Empty;
            RaiseChanged();
        }

        public string GetFlag(string key, string fallback = null) =>
            !string.IsNullOrEmpty(key) && _flags.TryGetValue(key, out var value) ? value : fallback;

        public bool GetFlagBool(string key) => GetFlag(key) == "1";
        public void SetFlagBool(string key, bool value) => SetFlag(key, value ? "1" : "0");

        public int GetFlagInt(string key, int fallback = 0) =>
            int.TryParse(GetFlag(key), out var parsed) ? parsed : fallback;

        public void SetFlagInt(string key, int value) => SetFlag(key, value.ToString());

        // ---- Bulk state (save/load) -------------------------------------------------------

        /// <summary>Replaces every field from a loaded save. Only <see cref="SaveSystem"/> calls this.</summary>
        internal void RestoreFrom(string trainerName, int money,
                                  List<CreatureInstance> party, List<CreatureInstance> storage,
                                  IEnumerable<int> seen, IEnumerable<int> caught,
                                  IEnumerable<KeyValuePair<string, int>> inventory,
                                  IEnumerable<KeyValuePair<string, string>> flags)
        {
            TrainerName = string.IsNullOrWhiteSpace(trainerName) ? "Trainer" : trainerName;
            Money = Mathf.Max(0, money);

            _party.Clear();
            if (party != null)
                for (var i = 0; i < party.Count && _party.Count < MaxPartySize; i++)
                    if (party[i] != null) _party.Add(party[i]);

            _storage.Clear();
            if (storage != null)
                for (var i = 0; i < storage.Count && _storage.Count < MaxStorageSize; i++)
                    if (storage[i] != null) _storage.Add(storage[i]);

            _seen.Clear();
            if (seen != null) foreach (var id in seen) _seen.Add(id);

            _caught.Clear();
            if (caught != null) foreach (var id in caught) _caught.Add(id);

            _inventory.Clear();
            if (inventory != null) foreach (var pair in inventory) _inventory[pair.Key] = pair.Value;

            _flags.Clear();
            if (flags != null) foreach (var pair in flags) _flags[pair.Key] = pair.Value;

            RaiseChanged();
        }

        /// <summary>Populates a brand new save with the opening kit.</summary>
        public void InitialiseNewGame(string trainerName, int starterSpeciesId, int starterLevel, int seed)
        {
            TrainerName = string.IsNullOrWhiteSpace(trainerName) ? "Trainer" : trainerName;
            Money = 3000;
            _party.Clear();
            _storage.Clear();
            _seen.Clear();
            _caught.Clear();
            _inventory.Clear();
            _flags.Clear();

            if (starterSpeciesId > 0)
            {
                var starter = CreatureFactory.Create(starterSpeciesId, Mathf.Max(1, starterLevel), seed);
                _party.Add(starter);
                MarkCaught(starterSpeciesId);
            }

            AddItem(FieldItems.PokeBall, 10);
            AddItem(FieldItems.Potion, 5);
            AddItem(FieldItems.Antidote, 2);

            RaiseChanged();
        }

        private void RaiseChanged()
        {
            Changed?.Invoke();
            OverworldEvents.RaisePartyChanged();
        }
    }
}
