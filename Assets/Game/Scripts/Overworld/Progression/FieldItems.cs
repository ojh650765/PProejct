using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The slice's item ids and their out-of-battle effects.
    ///
    /// Item *ids* are strings across the whole project because <see cref="IPlayerProfile"/> and
    /// <see cref="BattleAction.UseItem"/> both key on them; this type is the overworld's opinion
    /// about what those ids do when used from the party menu. The battle engine has its own
    /// opinion for in-battle use, which is correct — a Potion used mid-battle costs a turn.
    ///
    /// Effects are intentionally table-driven and dumb. Anything with a real interaction belongs
    /// in the battle engine, not here.
    /// </summary>
    public static class FieldItems
    {
        // Hyphenated, to match ItemCatalog. These two tables key the same dictionary --
        // IPlayerProfile.Inventory is written here and read by the battle engine -- and
        // they disagreed on every multi-word id: the overworld handed out "poke_ball"
        // while the engine spent "poke-ball", so a ball picked up in the field could
        // never be thrown in a battle, and the same held for every potion and status
        // heal. ItemCatalog is the registry the engine resolves against, so it wins.
        //
        // Any save written before this carries the old keys and will read as an empty
        // bag. That is the right trade during development; a migration would be worth
        // more than the bag it recovers only after the game ships.
        public const string PokeBall = "poke-ball";
        public const string GreatBall = "great-ball";
        public const string Potion = "potion";
        public const string SuperPotion = "super-potion";
        public const string Antidote = "antidote";
        public const string ParalyzeHeal = "paralyze-heal";
        public const string Awakening = "awakening";
        public const string BurnHeal = "burn-heal";
        public const string IceHeal = "ice-heal";
        public const string FullHeal = "full-heal";
        public const string Revive = "revive";
        public const string Repel = "repel";

        /// <summary>True for items that are thrown at a wild creature and so are battle-only.</summary>
        public static bool IsBall(string itemId) => itemId == PokeBall || itemId == GreatBall;

        /// <summary>
        /// Applies an item to a creature outside battle. Returns false when the item would do
        /// nothing — a full-HP creature and a Potion — so the caller can refuse the use rather
        /// than silently consuming it.
        /// </summary>
        public static bool TryApply(string itemId, CreatureInstance creature)
        {
            if (creature == null || string.IsNullOrEmpty(itemId)) return false;

            switch (itemId)
            {
                case Potion: return Heal(creature, 20);
                case SuperPotion: return Heal(creature, 60);

                case Antidote: return CureStatus(creature, StatusCondition.Poison, StatusCondition.BadPoison);
                case ParalyzeHeal: return CureStatus(creature, StatusCondition.Paralysis);
                case Awakening: return CureStatus(creature, StatusCondition.Sleep);
                case BurnHeal: return CureStatus(creature, StatusCondition.Burn);
                case IceHeal: return CureStatus(creature, StatusCondition.Freeze);

                case FullHeal:
                    if (creature.IsFainted || creature.Status == StatusCondition.None) return false;
                    creature.Status = StatusCondition.None;
                    creature.StatusCounter = 0;
                    return true;

                case Revive:
                    // Only meaningful on a fainted creature, and it comes back at half health.
                    if (!creature.IsFainted) return false;
                    creature.CurrentHp = Mathf.Max(1, creature.MaxHp / 2);
                    creature.Status = StatusCondition.None;
                    creature.StatusCounter = 0;
                    return true;

                default:
                    return false;
            }
        }

        private static bool Heal(CreatureInstance creature, int amount)
        {
            // A fainted creature needs a Revive; healing it would quietly break the faint rules.
            if (creature.IsFainted) return false;
            if (creature.CurrentHp >= creature.MaxHp) return false;
            creature.CurrentHp = Mathf.Min(creature.MaxHp, creature.CurrentHp + amount);
            return true;
        }

        private static bool CureStatus(CreatureInstance creature, params StatusCondition[] cures)
        {
            if (creature.IsFainted) return false;
            for (var i = 0; i < cures.Length; i++)
            {
                if (creature.Status != cures[i]) continue;
                creature.Status = StatusCondition.None;
                creature.StatusCounter = 0;
                return true;
            }
            return false;
        }

        /// <summary>Fallback display name, used only until the UI worker ships its own item table.</summary>
        public static string DisplayName(string itemId)
        {
            switch (itemId)
            {
                case PokeBall: return "Poké Ball";
                case GreatBall: return "Great Ball";
                case Potion: return "Potion";
                case SuperPotion: return "Super Potion";
                case Antidote: return "Antidote";
                case ParalyzeHeal: return "Paralyze Heal";
                case Awakening: return "Awakening";
                case BurnHeal: return "Burn Heal";
                case IceHeal: return "Ice Heal";
                case FullHeal: return "Full Heal";
                case Revive: return "Revive";
                case Repel: return "Repel";
                default: return itemId;
            }
        }
    }
}
