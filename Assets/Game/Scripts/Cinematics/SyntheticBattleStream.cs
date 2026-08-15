using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// A scripted <see cref="BattleEvent"/> stream, for developing and reviewing the
    /// choreography before the battle engine exists.
    ///
    /// It is not a simulation and makes no attempt to be one: the numbers are chosen to
    /// exercise the presentation, not to be legal. What it guarantees is coverage — the
    /// default script walks through an opening send-out, a physical move, a special
    /// projectile move, a critical hit, a super-effective hit, a status, a stat change, an
    /// ability, a miss with a real dodge, a multi-hit flurry, a switch, a faint, a
    /// replacement, a capture attempt that fails, a capture attempt that succeeds, weather,
    /// experience with a level-up, and victory.
    ///
    /// If any beat in that list looks wrong, it looks wrong here first, without waiting on
    /// another worker.
    ///
    /// Compiled out of player builds by default. Set <c>POKELAB_CINEMATIC_DEBUG</c> in
    /// Scripting Define Symbols to keep it in a build for QA.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class SyntheticBattleStream : MonoBehaviour
    {
        [Header("Enable")]
        [Tooltip("Master switch. The component does nothing at all when this is off.")]
        [SerializeField] private bool enableSyntheticStream;
        [Tooltip("Run the script automatically on Start.")]
        [SerializeField] private bool playOnStart;
        [Tooltip("Loop the script forever, for leaving a review build running.")]
        [SerializeField] private bool loop;

        [Header("Target")]
        [Tooltip("Presenter to feed. Found in the scene when empty.")]
        [SerializeField] private BattlePresenter presenter;

        [Header("Cast")]
        [Tooltip("Species id for the player's lead. Should exist in the art registry to see real art.")]
        [SerializeField] private int playerSpeciesId = 25;
        [Tooltip("Species id for the player's second party member, used by the switch beat.")]
        [SerializeField] private int playerSecondSpeciesId = 6;
        [Tooltip("Species id for the wild creature.")]
        [SerializeField] private int opponentSpeciesId = 16;
        [Tooltip("Species id for the opponent's replacement after the first faints.")]
        [SerializeField] private int opponentSecondSpeciesId = 92;

        [Header("Pacing")]
        [Tooltip("Extra seconds inserted between scripted turns, so beats are separable by eye.")]
        [SerializeField] private float turnGap = 0.6f;

        private Coroutine _running;

        /// <summary>True while the scripted stream is feeding the presenter.</summary>
        public bool IsRunning => _running != null;

        private void Start()
        {
            if (!enableSyntheticStream || !playOnStart) return;
            Play();
        }

        /// <summary>Starts the scripted stream. Safe to call twice; the second call is ignored.</summary>
        public void Play()
        {
#if !UNITY_EDITOR && !POKELAB_CINEMATIC_DEBUG
            // Inert in player builds. The class itself still compiles so a scene reference to
            // it does not break the build, but nothing it does can reach a shipped executable
            // unless POKELAB_CINEMATIC_DEBUG is defined for a QA build.
            return;
#else
            if (!enableSyntheticStream || _running != null) return;
            if (presenter == null) presenter = FindAnyObjectByType<BattlePresenter>();
            if (presenter == null)
            {
                Debug.LogWarning("[SyntheticBattleStream] No BattlePresenter found; nothing to drive.", this);
                return;
            }
            _running = CinematicRunner.Run(Run());
#endif
        }

        /// <summary>Stops the stream and drains anything still queued.</summary>
        public void Stop()
        {
            if (_running != null) { CinematicRunner.Halt(_running); _running = null; }
            presenter?.FlushQueue();
        }

        private IEnumerator Run()
        {
            do
            {
                presenter.ResetForNewBattle();
                foreach (var batch in Script())
                {
                    presenter.OnBattleEvents(batch);
                    // Wait for the presenter to finish this batch before queuing the next.
                    // The real engine hands over a turn at a time and then waits for input,
                    // so feeding it that way is what actually exercises the queue's pacing.
                    yield return presenter.WaitUntilIdle();
                    yield return CinematicRunner.Wait(turnGap);
                }
            }
            while (loop);

            _running = null;
        }

        // --- The script ------------------------------------------------------------------

        /// <summary>
        /// The scripted turns, each yielded as one batch. Exposed so a test or an editor
        /// tool can drive the same sequence without this component.
        /// </summary>
        public IEnumerable<List<BattleEvent>> Script()
        {
            CreatureInstance playerLead = MakeCreature(playerSpeciesId, "Lead", 14, 46);
            CreatureInstance playerSecond = MakeCreature(playerSecondSpeciesId, "Second", 15, 52);
            CreatureInstance wild = MakeCreature(opponentSpeciesId, null, 13, 41);
            CreatureInstance wildSecond = MakeCreature(opponentSecondSpeciesId, null, 14, 44);

            // --- Turn 0: the opening -------------------------------------------------------
            yield return new List<BattleEvent>
            {
                new BattleStartedEvent { Turn = 0, Kind = BattleKind.Wild, Weather = Weather.Clear },
                new CreatureSentOutEvent { Turn = 0, Side = BattleSide.Opponent, Creature = wild, IsReplacement = false },
                new CreatureSentOutEvent { Turn = 0, Side = BattleSide.Player, Creature = playerLead, IsReplacement = false },
                new MessageEvent { Turn = 0, Text = "A wild creature appeared!" },
            };

            // --- Turn 1: a plain physical exchange -----------------------------------------
            yield return new List<BattleEvent>
            {
                new MoveDeclaredEvent { Turn = 1, Side = BattleSide.Player, MoveId = "tackle", MoveDisplayName = "Tackle" },
                new MoveExecutedEvent
                {
                    Turn = 1, Attacker = BattleSide.Player, Target = BattleSide.Opponent,
                    MoveId = "tackle", Category = MoveCategory.Physical, MoveType = ElementType.Normal,
                    MakesContact = true, IsProjectile = false, VfxKey = "impact_generic",
                },
                Damage(1, BattleSide.Opponent, 9, 32, 41),

                new MoveDeclaredEvent { Turn = 1, Side = BattleSide.Opponent, MoveId = "gust", MoveDisplayName = "Gust" },
                new MoveExecutedEvent
                {
                    Turn = 1, Attacker = BattleSide.Opponent, Target = BattleSide.Player,
                    MoveId = "gust", Category = MoveCategory.Special, MoveType = ElementType.Flying,
                    IsProjectile = true, VfxKey = "gust",
                },
                Damage(1, BattleSide.Player, 7, 39, 46),
            };

            // --- Turn 2: a critical, super-effective projectile ----------------------------
            // Camera should punch in hard, the time hitch should fire, and the reaction beat
            // should be visibly longer than the plain hit in turn 1.
            yield return new List<BattleEvent>
            {
                new MoveDeclaredEvent { Turn = 2, Side = BattleSide.Player, MoveId = "thunderbolt", MoveDisplayName = "Thunderbolt" },
                new MoveExecutedEvent
                {
                    Turn = 2, Attacker = BattleSide.Player, Target = BattleSide.Opponent,
                    MoveId = "thunderbolt", Category = MoveCategory.Special, MoveType = ElementType.Electric,
                    IsProjectile = true, VfxKey = "thunderbolt",
                },
                new DamageDealtEvent
                {
                    Turn = 2, Target = BattleSide.Opponent, Amount = 19, RemainingHp = 13, MaxHp = 41,
                    Critical = true, TypeMultiplier = 2f, Effectiveness = Effectiveness.SuperEffective,
                },
                new StatusChangedEvent
                {
                    Turn = 2, Target = BattleSide.Opponent,
                    Previous = StatusCondition.None, Current = StatusCondition.Paralysis,
                },
            };

            // --- Turn 3: a miss with a real dodge, then an ability and a stat drop ---------
            // The opponent must be visibly moving before the projectile arrives, and the
            // projectile must continue through the space it vacated.
            yield return new List<BattleEvent>
            {
                new MoveDeclaredEvent { Turn = 3, Side = BattleSide.Opponent, MoveId = "quick_attack", MoveDisplayName = "Quick Attack" },
                new MoveExecutedEvent
                {
                    Turn = 3, Attacker = BattleSide.Opponent, Target = BattleSide.Player,
                    MoveId = "quick_attack", Category = MoveCategory.Physical, MoveType = ElementType.Normal,
                    MakesContact = true,
                },
                new MoveMissedEvent { Turn = 3, Attacker = BattleSide.Opponent, Target = BattleSide.Player, MoveId = "quick_attack" },

                new AbilityTriggeredEvent { Turn = 3, Side = BattleSide.Player, AbilityId = "static", AbilityDisplayName = "Static" },
                new StatStageChangedEvent { Turn = 3, Target = BattleSide.Opponent, Stat = StatKind.Speed, Delta = -1, NewStage = -1 },
            };

            // --- Turn 4: weather, then a three-hit flurry ---------------------------------
            // The three hits must read as one continuous attack, not as three attacks.
            var flurry = new List<BattleEvent>
            {
                new WeatherChangedEvent { Turn = 4, Previous = Weather.Clear, Current = Weather.Rain },
                new MoveDeclaredEvent { Turn = 4, Side = BattleSide.Player, MoveId = "fury_swipes", MoveDisplayName = "Fury Swipes" },
            };
            for (int i = 0; i < 3; i++)
            {
                flurry.Add(new MoveExecutedEvent
                {
                    Turn = 4, Attacker = BattleSide.Player, Target = BattleSide.Opponent,
                    MoveId = "fury_swipes", Category = MoveCategory.Physical, MoveType = ElementType.Normal,
                    MakesContact = true, HitIndex = i, HitCount = 3,
                });
                flurry.Add(Damage(4, BattleSide.Opponent, 4, Mathf.Max(1, 13 - 4 * (i + 1)), 41));
            }
            yield return flurry;

            // --- Turn 5: a voluntary switch ------------------------------------------------
            // Withdraw and replacement send-out should differ visibly from the opening.
            yield return new List<BattleEvent>
            {
                new CreatureWithdrawnEvent { Turn = 5, Side = BattleSide.Player, Creature = playerLead },
                new CreatureSentOutEvent { Turn = 5, Side = BattleSide.Player, Creature = playerSecond, IsReplacement = true },
            };

            // --- Turn 6: the faint and the opponent's replacement -------------------------
            yield return new List<BattleEvent>
            {
                new MoveDeclaredEvent { Turn = 6, Side = BattleSide.Player, MoveId = "ember", MoveDisplayName = "Ember" },
                new MoveExecutedEvent
                {
                    Turn = 6, Attacker = BattleSide.Player, Target = BattleSide.Opponent,
                    MoveId = "ember", Category = MoveCategory.Special, MoveType = ElementType.Fire,
                    IsProjectile = true, VfxKey = "ember",
                },
                new DamageDealtEvent
                {
                    Turn = 6, Target = BattleSide.Opponent, Amount = 12, RemainingHp = 0, MaxHp = 41,
                    Critical = false, TypeMultiplier = 2f, Effectiveness = Effectiveness.SuperEffective,
                },
                new CreatureFaintedEvent { Turn = 6, Side = BattleSide.Opponent, Creature = wild },
                new CreatureSentOutEvent { Turn = 6, Side = BattleSide.Opponent, Creature = wildSecond, IsReplacement = true },
            };

            // --- Turn 7: a capture attempt that fails --------------------------------------
            // Three shakes then a break-out. The shakes must accelerate in tension and the
            // creature must come back out, not simply re-appear.
            yield return new List<BattleEvent>
            {
                new MessageEvent { Turn = 7, Text = "You throw a ball!" },
                new CaptureAttemptEvent { Turn = 7, BallId = "poke_ball", Shakes = 3, Succeeded = false, CatchProbability = 0.42f },
            };

            // --- Turn 8: soften it up, then a capture that succeeds ------------------------
            yield return new List<BattleEvent>
            {
                new MoveDeclaredEvent { Turn = 8, Side = BattleSide.Player, MoveId = "ember", MoveDisplayName = "Ember" },
                new MoveExecutedEvent
                {
                    Turn = 8, Attacker = BattleSide.Player, Target = BattleSide.Opponent,
                    MoveId = "ember", Category = MoveCategory.Special, MoveType = ElementType.Fire,
                    IsProjectile = true, VfxKey = "ember",
                },
                Damage(8, BattleSide.Opponent, 26, 4, 44),
                new StatusChangedEvent
                {
                    Turn = 8, Target = BattleSide.Opponent,
                    Previous = StatusCondition.None, Current = StatusCondition.Sleep,
                },
            };

            yield return new List<BattleEvent>
            {
                new CaptureAttemptEvent { Turn = 9, BallId = "great_ball", Shakes = 4, Succeeded = true, CatchProbability = 0.86f },
            };

            // --- Turn 10: progression and the outro ----------------------------------------
            yield return new List<BattleEvent>
            {
                new ExperienceGainedEvent
                {
                    Turn = 10, InstanceId = playerSecond.InstanceId, Amount = 148,
                    NewTotal = 1204, NewLevel = 16, LeveledUp = true,
                },
                new BattleEndedEvent { Turn = 10, Outcome = BattleOutcome.Captured, MoneyEarned = 0 },
            };
        }

        private static DamageDealtEvent Damage(int turn, BattleSide target, int amount, int remaining, int max)
            => new DamageDealtEvent
            {
                Turn = turn, Target = target, Amount = amount,
                RemainingHp = remaining, MaxHp = max,
                Critical = false, TypeMultiplier = 1f, Effectiveness = Effectiveness.Neutral,
            };

        private static CreatureInstance MakeCreature(int speciesId, string nickname, int level, int maxHp)
        {
            var c = new CreatureInstance
            {
                SpeciesId = speciesId,
                Nickname = nickname,
                Level = level,
                MaxHp = maxHp,
                CurrentHp = maxHp,
                InstanceId = Guid.NewGuid().ToString("N"),
            };
            for (int i = 0; i < c.Stats.Length; i++) c.Stats[i] = 20 + level;
            c.Stats[(int)StatKind.Hp] = maxHp;
            return c;
        }
    }
}
