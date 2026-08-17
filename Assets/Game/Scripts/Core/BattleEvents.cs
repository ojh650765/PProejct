using System;

namespace PokeLab.Core
{
    /// <summary>
    /// The single decoupling seam of the project.
    ///
    /// The battle engine is pure C# and never touches the scene. It emits an ordered
    /// stream of these records; the cinematics, VFX, UI and audio layers each subscribe
    /// and play their own response. That is why those systems can be built in parallel
    /// against this file without ever referencing each other.
    ///
    /// Presentation layers must treat every event as advisory: skipping an event may
    /// never desync game state, because state already changed inside the engine.
    /// </summary>
    public abstract class BattleEvent
    {
        /// <summary>Turn this event belongs to. 0 for pre-battle intro events.</summary>
        public int Turn;

        /// <summary>
        /// Suggested seconds the presenter should dwell before the next event.
        /// A presenter may stretch this but should not shorten it below zero.
        /// </summary>
        public virtual float SuggestedDuration => 0.35f;
    }

    public sealed class BattleStartedEvent : BattleEvent
    {
        public BattleKind Kind;
        public Weather Weather;
        public string OpponentTrainerId;
        public override float SuggestedDuration => 1.2f;
    }

    /// <summary>A creature is sent out — the presenter plays the throw, land and cry.</summary>
    public sealed class CreatureSentOutEvent : BattleEvent
    {
        public BattleSide Side;
        public CreatureInstance Creature;
        /// <summary>True when this replaces a fainted creature rather than opening the battle.</summary>
        public bool IsReplacement;
        public override float SuggestedDuration => 1.6f;
    }

    public sealed class CreatureWithdrawnEvent : BattleEvent
    {
        public BattleSide Side;
        public CreatureInstance Creature;
        public override float SuggestedDuration => 0.9f;
    }

    public sealed class MoveDeclaredEvent : BattleEvent
    {
        public BattleSide Side;
        public string MoveId;
        public string MoveDisplayName;
        public override float SuggestedDuration => 0.8f;
    }

    /// <summary>The attack animation beat. Damage is reported separately by <see cref="DamageDealtEvent"/>.</summary>
    public sealed class MoveExecutedEvent : BattleEvent
    {
        public BattleSide Attacker;
        public BattleSide Target;
        public string MoveId;
        public MoveCategory Category;
        public ElementType MoveType;
        public string VfxKey;
        public string AnimationKey;
        public bool IsProjectile;
        public bool MakesContact;
        /// <summary>Index within a multi-hit sequence, 0-based.</summary>
        public int HitIndex;
        public int HitCount = 1;
        public override float SuggestedDuration => 0.9f;
    }

    public sealed class MoveMissedEvent : BattleEvent
    {
        public BattleSide Attacker;
        public BattleSide Target;
        public string MoveId;
        /// <summary>True when an ability or immunity blocked it rather than an accuracy roll.</summary>
        public bool WasImmune;
        public string BlockingAbilityId;
        public override float SuggestedDuration => 0.8f;
    }

    public sealed class DamageDealtEvent : BattleEvent
    {
        public BattleSide Target;
        public int Amount;
        public int RemainingHp;
        public int MaxHp;
        public bool Critical;
        public float TypeMultiplier = 1f;
        public Effectiveness Effectiveness = Effectiveness.Neutral;
        /// <summary>Set when the damage came from a status, weather or recoil source rather than a move.</summary>
        public string IndirectSourceId;
        public override float SuggestedDuration => 0.7f;
    }

    public sealed class HealedEvent : BattleEvent
    {
        public BattleSide Target;
        public int Amount;
        public int RemainingHp;
        public int MaxHp;
        public string SourceId;

        /// <summary>
        /// Instance id of the creature that was healed. Empty means the one on the field.
        ///
        /// A side alone cannot tell a benched party member from the creature standing on it,
        /// so a presenter that trusts <see cref="Target"/> animates the wrong HP bar — for a
        /// revive, a bar that fills for a creature still lying down.
        /// </summary>
        public string InstanceId;
    }

    public sealed class StatusChangedEvent : BattleEvent
    {
        public BattleSide Target;
        public StatusCondition Previous;
        public StatusCondition Current;
        public override float SuggestedDuration => 0.8f;

        /// <summary>Instance id of the creature whose status changed. Empty means the one on the field.</summary>
        public string InstanceId;
    }

    public sealed class VolatileChangedEvent : BattleEvent
    {
        public BattleSide Target;
        public VolatileFlags Added;
        public VolatileFlags Removed;
    }

    public sealed class StatStageChangedEvent : BattleEvent
    {
        public BattleSide Target;
        public StatKind Stat;
        public int Delta;
        public int NewStage;
        public override float SuggestedDuration => 0.6f;
    }

    public sealed class AbilityTriggeredEvent : BattleEvent
    {
        public BattleSide Side;
        public string AbilityId;
        public string AbilityDisplayName;
        public override float SuggestedDuration => 0.9f;
    }

    public sealed class ItemUsedEvent : BattleEvent
    {
        public BattleSide Side;
        public string ItemId;
        public string ItemDisplayName;
    }

    public sealed class WeatherChangedEvent : BattleEvent
    {
        public Weather Previous;
        public Weather Current;
        public override float SuggestedDuration => 1.1f;
    }

    public sealed class CreatureFaintedEvent : BattleEvent
    {
        public BattleSide Side;
        public CreatureInstance Creature;
        public override float SuggestedDuration => 1.8f;
    }

    /// <summary>One shake of a thrown ball. Four in a row with <see cref="Succeeded"/> means a catch.</summary>
    public sealed class CaptureAttemptEvent : BattleEvent
    {
        /// <summary>
        /// Which side is being captured. Always <see cref="BattleSide.Opponent"/> in the current
        /// slice, but stated explicitly: the choreography has to know where to throw the ball,
        /// and inferring it silently breaks the moment anything on the player's side is catchable.
        /// </summary>
        public BattleSide Target = BattleSide.Opponent;

        public string BallId;
        public int Shakes;
        public bool Succeeded;
        public float CatchProbability;
        public override float SuggestedDuration => 3.4f;
    }

    public sealed class ExperienceGainedEvent : BattleEvent
    {
        public string InstanceId;
        public int Amount;
        public int NewTotal;
        public int NewLevel;
        public bool LeveledUp;
        public override float SuggestedDuration => 1.4f;
    }

    public sealed class BattleEndedEvent : BattleEvent
    {
        public BattleOutcome Outcome;
        public int MoneyEarned;
        public override float SuggestedDuration => 2.0f;
    }

    /// <summary>Free-form battle log line, already localised by the engine.</summary>
    public sealed class MessageEvent : BattleEvent
    {
        public string Text;
        public override float SuggestedDuration => 1.0f;
    }

    /// <summary>
    /// Presentation layers implement this and register with the battle presenter.
    /// Handlers are invoked in registration order and must not block.
    /// </summary>
    public interface IBattleEventListener
    {
        void OnBattleEvent(BattleEvent evt);
    }
}
