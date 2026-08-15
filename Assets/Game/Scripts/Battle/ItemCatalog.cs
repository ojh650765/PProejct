using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>What a bag item does when used in battle.</summary>
    public enum BagItemKind
    {
        None = 0,
        /// <summary>Restores HP by <see cref="BagItemData.Amount"/>, or fully when the amount is 0.</summary>
        Heal = 1,
        /// <summary>Cures the statuses listed in <see cref="BagItemData.Cures"/>.</summary>
        StatusCure = 2,
        /// <summary>Revives a fainted party member to <see cref="BagItemData.Amount"/> percent of max HP.</summary>
        Revive = 3,
        /// <summary>A capture device; routes to the catch formula instead of a normal item use.</summary>
        Ball = 4,
    }

    /// <summary>An item the player can select from the bag during battle.</summary>
    public sealed class BagItemData
    {
        public string Id;
        public string DisplayName;
        public BagItemKind Kind;
        /// <summary>HP restored, or revive percentage. 0 with <see cref="BagItemKind.Heal"/> means "full".</summary>
        public int Amount;
        public StatusCondition[] Cures = Array.Empty<StatusCondition>();
        /// <summary>Ball catch multiplier. 0 marks a guaranteed catch (master ball).</summary>
        public float BallMultiplier = 1f;

        public bool CuresStatus(StatusCondition status)
        {
            if (status == StatusCondition.None) return false;
            for (var i = 0; i < Cures.Length; i++)
                if (Cures[i] == status) return true;
            return false;
        }
    }

    /// <summary>When a held item fires.</summary>
    public enum HeldItemTrigger
    {
        None = 0,
        /// <summary>Checked after any HP loss, once the holder drops to or below its threshold.</summary>
        LowHp = 1,
        /// <summary>Checked during end-of-turn upkeep.</summary>
        EndOfTurn = 2,
        /// <summary>Checked immediately after a status is inflicted.</summary>
        OnStatus = 3,
    }

    /// <summary>A held item. Berries are consumed on use; passive items like Leftovers are not.</summary>
    public sealed class HeldItemData
    {
        public string Id;
        public string DisplayName;
        public HeldItemTrigger Trigger;

        /// <summary>HP fraction at or below which a <see cref="HeldItemTrigger.LowHp"/> item fires.</summary>
        public float HpThreshold = 0.5f;
        /// <summary>Flat HP restored. Ignored when <see cref="HealFraction"/> is non-zero.</summary>
        public int HealAmount;
        /// <summary>HP restored as a fraction of max HP.</summary>
        public float HealFraction;

        public StatusCondition[] Cures = Array.Empty<StatusCondition>();
        /// <summary>True to remove confusion as well as the listed statuses.</summary>
        public bool CuresConfusion;
        /// <summary>True when using it removes it from the holder.</summary>
        public bool Consumed = true;

        public bool CuresStatus(StatusCondition status)
        {
            if (status == StatusCondition.None) return false;
            for (var i = 0; i < Cures.Length; i++)
                if (Cures[i] == status) return true;
            return false;
        }
    }

    /// <summary>
    /// The battle item table. Small and code-authored on purpose: unlike moves and species
    /// there is no exported dataset for items, and the slice's list is a dozen entries. It
    /// still goes through a registry so the eventual data-driven version is a drop-in.
    /// </summary>
    public static class ItemCatalog
    {
        /// <summary>Ball whose multiplier means "never fails".</summary>
        public const string MasterBallId = "master-ball";
        public const string PokeBallId = "poke-ball";

        private static readonly Dictionary<string, BagItemData> Bag =
            new Dictionary<string, BagItemData>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HeldItemData> Held =
            new Dictionary<string, HeldItemData>(StringComparer.OrdinalIgnoreCase);

        private static readonly StatusCondition[] AllStatuses =
        {
            StatusCondition.Burn, StatusCondition.Freeze, StatusCondition.Paralysis,
            StatusCondition.Poison, StatusCondition.BadPoison, StatusCondition.Sleep,
        };

        static ItemCatalog()
        {
            RegisterBag(new BagItemData { Id = "potion", DisplayName = "Potion", Kind = BagItemKind.Heal, Amount = 20 });
            RegisterBag(new BagItemData { Id = "super-potion", DisplayName = "Super Potion", Kind = BagItemKind.Heal, Amount = 60 });
            RegisterBag(new BagItemData { Id = "hyper-potion", DisplayName = "Hyper Potion", Kind = BagItemKind.Heal, Amount = 120 });
            RegisterBag(new BagItemData { Id = "max-potion", DisplayName = "Max Potion", Kind = BagItemKind.Heal, Amount = 0 });

            RegisterBag(new BagItemData { Id = "antidote", DisplayName = "Antidote", Kind = BagItemKind.StatusCure, Cures = new[] { StatusCondition.Poison, StatusCondition.BadPoison } });
            RegisterBag(new BagItemData { Id = "burn-heal", DisplayName = "Burn Heal", Kind = BagItemKind.StatusCure, Cures = new[] { StatusCondition.Burn } });
            RegisterBag(new BagItemData { Id = "ice-heal", DisplayName = "Ice Heal", Kind = BagItemKind.StatusCure, Cures = new[] { StatusCondition.Freeze } });
            RegisterBag(new BagItemData { Id = "awakening", DisplayName = "Awakening", Kind = BagItemKind.StatusCure, Cures = new[] { StatusCondition.Sleep } });
            RegisterBag(new BagItemData { Id = "paralyze-heal", DisplayName = "Paralyze Heal", Kind = BagItemKind.StatusCure, Cures = new[] { StatusCondition.Paralysis } });
            RegisterBag(new BagItemData { Id = "full-heal", DisplayName = "Full Heal", Kind = BagItemKind.StatusCure, Cures = AllStatuses });
            RegisterBag(new BagItemData { Id = "revive", DisplayName = "Revive", Kind = BagItemKind.Revive, Amount = 50 });
            RegisterBag(new BagItemData { Id = "max-revive", DisplayName = "Max Revive", Kind = BagItemKind.Revive, Amount = 100 });

            RegisterBag(new BagItemData { Id = PokeBallId, DisplayName = "Poké Ball", Kind = BagItemKind.Ball, BallMultiplier = 1f });
            RegisterBag(new BagItemData { Id = "great-ball", DisplayName = "Great Ball", Kind = BagItemKind.Ball, BallMultiplier = 1.5f });
            RegisterBag(new BagItemData { Id = "ultra-ball", DisplayName = "Ultra Ball", Kind = BagItemKind.Ball, BallMultiplier = 2f });
            RegisterBag(new BagItemData { Id = "net-ball", DisplayName = "Net Ball", Kind = BagItemKind.Ball, BallMultiplier = 3.5f });
            RegisterBag(new BagItemData { Id = "dusk-ball", DisplayName = "Dusk Ball", Kind = BagItemKind.Ball, BallMultiplier = 3f });
            RegisterBag(new BagItemData { Id = "quick-ball", DisplayName = "Quick Ball", Kind = BagItemKind.Ball, BallMultiplier = 5f });
            RegisterBag(new BagItemData { Id = "timer-ball", DisplayName = "Timer Ball", Kind = BagItemKind.Ball, BallMultiplier = 1f });
            RegisterBag(new BagItemData { Id = MasterBallId, DisplayName = "Master Ball", Kind = BagItemKind.Ball, BallMultiplier = 0f });

            RegisterHeld(new HeldItemData { Id = "oran-berry", DisplayName = "Oran Berry", Trigger = HeldItemTrigger.LowHp, HpThreshold = 0.5f, HealAmount = 10 });
            RegisterHeld(new HeldItemData { Id = "sitrus-berry", DisplayName = "Sitrus Berry", Trigger = HeldItemTrigger.LowHp, HpThreshold = 0.5f, HealFraction = 0.25f });
            RegisterHeld(new HeldItemData { Id = "leftovers", DisplayName = "Leftovers", Trigger = HeldItemTrigger.EndOfTurn, HealFraction = 1f / 16f, Consumed = false });
            RegisterHeld(new HeldItemData { Id = "lum-berry", DisplayName = "Lum Berry", Trigger = HeldItemTrigger.OnStatus, Cures = AllStatuses, CuresConfusion = true });
            RegisterHeld(new HeldItemData { Id = "cheri-berry", DisplayName = "Cheri Berry", Trigger = HeldItemTrigger.OnStatus, Cures = new[] { StatusCondition.Paralysis } });
            RegisterHeld(new HeldItemData { Id = "rawst-berry", DisplayName = "Rawst Berry", Trigger = HeldItemTrigger.OnStatus, Cures = new[] { StatusCondition.Burn } });
            RegisterHeld(new HeldItemData { Id = "pecha-berry", DisplayName = "Pecha Berry", Trigger = HeldItemTrigger.OnStatus, Cures = new[] { StatusCondition.Poison, StatusCondition.BadPoison } });
            RegisterHeld(new HeldItemData { Id = "chesto-berry", DisplayName = "Chesto Berry", Trigger = HeldItemTrigger.OnStatus, Cures = new[] { StatusCondition.Sleep } });
            RegisterHeld(new HeldItemData { Id = "aspear-berry", DisplayName = "Aspear Berry", Trigger = HeldItemTrigger.OnStatus, Cures = new[] { StatusCondition.Freeze } });
        }

        /// <summary>Adds or replaces a bag item.</summary>
        public static void RegisterBag(BagItemData item)
        {
            if (item != null && !string.IsNullOrEmpty(item.Id)) Bag[item.Id] = item;
        }

        /// <summary>Adds or replaces a held item.</summary>
        public static void RegisterHeld(HeldItemData item)
        {
            if (item != null && !string.IsNullOrEmpty(item.Id)) Held[item.Id] = item;
        }

        public static bool TryGetBag(string itemId, out BagItemData item)
        {
            item = null;
            return !string.IsNullOrEmpty(itemId) && Bag.TryGetValue(itemId, out item);
        }

        public static bool TryGetHeld(string itemId, out HeldItemData item)
        {
            item = null;
            return !string.IsNullOrEmpty(itemId) && Held.TryGetValue(itemId, out item);
        }

        /// <summary>True when the id names a ball, i.e. the action should route to capture.</summary>
        public static bool IsBall(string itemId) =>
            TryGetBag(itemId, out var item) && item.Kind == BagItemKind.Ball;

        /// <summary>
        /// Ball multiplier for this catch attempt. Situational balls resolve here so the
        /// catch formula stays a single expression.
        /// </summary>
        public static float BallMultiplier(string ballId, IBattleStateView state, SpeciesData target)
        {
            if (!TryGetBag(ballId, out var item) || item.Kind != BagItemKind.Ball) return 1f;

            switch (ballId.ToLowerInvariant())
            {
                case "net-ball":
                    // Only against Water and Bug; a plain ball otherwise.
                    var netApplies = target != null &&
                                     (target.HasType(ElementType.Water) || target.HasType(ElementType.Bug));
                    return netApplies ? item.BallMultiplier : 1f;
                case "quick-ball":
                    // The surprise only works on the opening turn.
                    return state != null && state.TurnNumber <= 1 ? item.BallMultiplier : 1f;
                case "timer-ball":
                    // Grows by 0.3 per turn, capping at 4x.
                    var turns = state?.TurnNumber ?? 1;
                    return Math.Min(4f, 1f + 0.3f * Math.Max(0, turns - 1));
                default:
                    return item.BallMultiplier;
            }
        }

        /// <summary>Every registered bag item, for inventory UI.</summary>
        public static IEnumerable<BagItemData> AllBagItems => Bag.Values;
    }
}
