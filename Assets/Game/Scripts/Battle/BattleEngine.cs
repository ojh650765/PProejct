using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>
    /// The turn-based battle simulation.
    ///
    /// Pure C#: it holds no scene references, constructs no Unity objects and can be run
    /// headless in a test. It resolves a turn against its own seeded RNG and returns an
    /// ordered <see cref="BattleEvent"/> stream; every presentation layer subscribes to
    /// that stream and nothing else. State has already changed by the time an event is
    /// handed out, so a presenter that drops one can never desync the simulation.
    ///
    /// Determinism is a hard requirement: the same seed and the same action sequence must
    /// produce a byte-identical stream. That is why every random decision goes through
    /// <see cref="BattleRandom"/>, why capture always consumes four draws whether or not it
    /// short-circuits, and why <see cref="CreatureFactory"/> replaces the GUID instance id.
    /// </summary>
    public sealed class BattleEngine : IBattleEngine, IAbilityHost
    {
        private const int PreTurnPriority = 6;
        private const int ConfusionSelfHitPercent = 33;
        private const int ParalysisFullStopPercent = 25;
        private const int ThawPercent = 20;
        private const int WeatherChipDivisor = 16;
        private const int BurnDivisor = 16;
        private const int PoisonDivisor = 8;
        private const int LeechSeedDivisor = 8;
        private const int TrainerMoneyPerLevel = 24;
        private const int ConfusionMinTurns = 1;
        private const int ConfusionMaxTurns = 4;
        private const int SleepMinTurns = 1;
        private const int SleepMaxTurns = 3;

        private readonly BattleState _state = new BattleState();
        private readonly List<BattleEvent> _stream = new List<BattleEvent>(64);
        private readonly List<BattleEvent> _pending = new List<BattleEvent>(8);
        private readonly List<BattleAction> _legalScratch = new List<BattleAction>(12);
        private readonly Dictionary<int, SpeciesData> _speciesCache = new Dictionary<int, SpeciesData>();
        private readonly DamageContext _forecastContext = new DamageContext();
        private readonly DamageContext _resolveContext = new DamageContext();

        /// <summary>Indexed by <see cref="BattleSide"/>. Reset each turn; drives the flinch rule.</summary>
        private readonly bool[] _hasMovedThisTurn = new bool[2];
        private readonly BattleSide[] _upkeepOrder = new BattleSide[2];

        private ISpeciesRegistry _species;
        private IMoveRegistry _moves;
        private ITypeChart _typeChart;

        private BattleRandom _rng;
        private int _runAttempts;

        /// <summary>
        /// Typeless 50-power fallback so a creature that has run out of PP can still act
        /// and the battle is guaranteed to terminate.
        /// </summary>
        public static readonly MoveData Struggle = new MoveData
        {
            Id = "struggle",
            NameEn = "Struggle",
            Type = ElementType.Normal,
            Category = MoveCategory.Physical,
            Power = 50,
            Accuracy = 0,
            PowerPoints = 1,
            RecoilRatio = 0.25f,
            MakesContact = true,
            AnimationKey = "AttackPhysical",
        };

        public IBattleStateView State => _state;

        /// <summary>The opponent's decision policy. Swap or retune it without touching the engine.</summary>
        public BattleAi Ai { get; set; }

        /// <summary>Seeded generator for this battle. Null until <see cref="Begin"/> is called.</summary>
        public BattleRandom Random => _rng;

        BattleState IAbilityHost.State => _state;
        BattleRandom IAbilityHost.Random => _rng;

        /// <summary>
        /// Constructs the engine. Registries default to <see cref="ServiceHub"/> lookups
        /// performed in <see cref="Begin"/>, so the engine can be created before the data
        /// layer has finished loading; tests pass fakes in directly.
        /// </summary>
        public BattleEngine(
            ISpeciesRegistry species = null,
            IMoveRegistry moves = null,
            ITypeChart typeChart = null,
            BattleAi ai = null)
        {
            _species = species;
            _moves = moves;
            _typeChart = typeChart;

            // Remembering that nobody chose an AI lets Begin pick the policy that matches
            // the battle kind without ever overriding a caller's deliberate choice.
            _usingDefaultAi = ai == null;
            Ai = ai ?? new BattleAi();
        }

        private readonly bool _usingDefaultAi;

        // ---- Setup ------------------------------------------------------------------

        /// <inheritdoc />
        public void Begin(BattleKind kind, IList<CreatureInstance> playerParty, IList<CreatureInstance> opponentParty,
            Weather weather, int seed)
        {
            ResolveRegistries();

            _state.Clear();
            _speciesCache.Clear();
            _pending.Clear();
            _stream.Clear();
            _runAttempts = 0;

            _rng = new BattleRandom(seed);
            if (_usingDefaultAi)
                Ai.Difficulty = kind == BattleKind.Wild ? AiDifficulty.Wild : AiDifficulty.Standard;

            _state.Kind = kind;
            _state.Weather = weather;
            _state.TurnNumber = 0;

            Fill(_state.PlayerSide, playerParty);
            Fill(_state.OpponentSide, opponentParty);

            _state.PlayerSide.ActiveIndex = Math.Max(0, _state.PlayerSide.FirstHealthyIndex());
            _state.OpponentSide.ActiveIndex = Math.Max(0, _state.OpponentSide.FirstHealthyIndex());

            // Intro beats are buffered rather than returned, because IBattleEngine.Begin is
            // void. They are prepended to the first ResolveTurn stream, or a presenter can
            // pull them early with DrainPendingEvents to play the intro before turn one.
            Emit(new BattleStartedEvent
            {
                Kind = kind,
                Weather = weather,
                OpponentTrainerId = _state.OpponentTrainerId,
            });

            // The opponent leads, then the player — the order a battle actually reads.
            SendOut(BattleSide.Opponent, _state.OpponentSide.ActiveIndex, false);
            SendOut(BattleSide.Player, _state.PlayerSide.ActiveIndex, false);

            FireEntryAbility(BattleSide.Opponent);
            FireEntryAbility(BattleSide.Player);

            _pending.AddRange(_stream);
            _stream.Clear();
        }

        /// <summary>Sets the trainer id reported by <see cref="BattleStartedEvent"/>. Call before <see cref="Begin"/>.</summary>
        public void SetOpponentTrainer(string trainerId) => _state.OpponentTrainerId = trainerId;

        /// <summary>
        /// Changes the field weather mid-battle and announces it. No move in the slice sets
        /// weather, so this exists for the overworld to push a storm into an in-flight
        /// encounter; the event lands in the current turn's stream.
        /// </summary>
        public void SetWeather(Weather weather)
        {
            if (_state.Weather == weather) return;
            var previous = _state.Weather;
            _state.Weather = weather;
            Emit(new WeatherChangedEvent { Previous = previous, Current = weather });
        }

        /// <summary>
        /// Removes and returns the buffered pre-battle events. Draining clears them, so a
        /// presenter that calls this will not see them repeated on the first turn.
        /// </summary>
        public IReadOnlyList<BattleEvent> DrainPendingEvents()
        {
            var drained = _pending.ToArray();
            _pending.Clear();
            return drained;
        }

        /// <summary>The creature caught this battle, or null. Valid once the outcome is <see cref="BattleOutcome.Captured"/>.</summary>
        public CreatureInstance CapturedCreature => _state.CapturedCreature;

        private void Fill(BattleSideState side, IList<CreatureInstance> party)
        {
            if (party == null) return;
            for (var i = 0; i < party.Count; i++)
                if (party[i] != null) side.Party.Add(party[i]);
        }

        private void ResolveRegistries()
        {
            _species ??= ServiceHub.TryGet<ISpeciesRegistry>(out var s) ? s : null;
            _moves ??= ServiceHub.TryGet<IMoveRegistry>(out var m) ? m : null;
            _typeChart ??= ServiceHub.TryGet<ITypeChart>(out var t) ? t : null;

            if (_species == null || _moves == null || _typeChart == null)
                throw new InvalidOperationException(
                    "BattleEngine needs ISpeciesRegistry, IMoveRegistry and ITypeChart. " +
                    "Register them with ServiceHub during boot, or inject them into the constructor.");
        }

        // ---- Turn resolution --------------------------------------------------------

        /// <inheritdoc />
        public IReadOnlyList<BattleEvent> ResolveTurn(BattleAction playerAction)
        {
            _stream.Clear();
            if (_pending.Count > 0)
            {
                _stream.AddRange(_pending);
                _pending.Clear();
            }

            if (_state.Outcome != BattleOutcome.InProgress) return _stream.ToArray();

            _state.TurnNumber++;
            _hasMovedThisTurn[0] = false;
            _hasMovedThisTurn[1] = false;

            var opponentAction = Ai.ChooseAction(this, BattleSide.Opponent);

            var first = Plan(playerAction);
            var second = Plan(opponentAction);
            if (!GoesFirst(first, second)) (first, second) = (second, first);

            Execute(first);
            if (_state.Outcome == BattleOutcome.InProgress) Execute(second);

            if (_state.Outcome == BattleOutcome.InProgress) EndOfTurn();
            if (_state.Outcome == BattleOutcome.InProgress) ReplaceFainted();

            EvaluateOutcome();
            return _stream.ToArray();
        }

        private readonly struct PlannedAction
        {
            public readonly BattleAction Action;
            public readonly int Priority;
            public readonly float Speed;

            public PlannedAction(BattleAction action, int priority, float speed)
            {
                Action = action; Priority = priority; Speed = speed;
            }

            public BattleSide Side => Action.Side;
        }

        private PlannedAction Plan(BattleAction action)
        {
            var priority = PreTurnPriority;

            if (action.Type == BattleAction.Kind.Move)
            {
                var move = MoveAt(action.Side, action.MoveIndex);
                priority = move?.Priority ?? 0;
            }

            return new PlannedAction(action, priority, EffectiveSpeed(action.Side));
        }

        /// <summary>
        /// Priority first, then effective Speed, then a coin flip. The flip is drawn only
        /// on an exact tie so that adding a speed-changing effect cannot silently reshuffle
        /// every later roll in a replay.
        /// </summary>
        private bool GoesFirst(PlannedAction a, PlannedAction b)
        {
            if (a.Priority != b.Priority) return a.Priority > b.Priority;
            if (Math.Abs(a.Speed - b.Speed) > 0.001f) return a.Speed > b.Speed;
            return _rng.CoinFlip();
        }

        private void Execute(PlannedAction planned)
        {
            var side = planned.Side;
            var actor = _state.ActiveOf(side);
            if (actor == null || actor.IsFainted) return;

            // Marked before the action resolves: a flinch inflicted by this action can only
            // land on a side that has not had its turn yet.
            _hasMovedThisTurn[(int)side] = true;

            switch (planned.Action.Type)
            {
                case BattleAction.Kind.Move:
                    UseMove(side, planned.Action.MoveIndex);
                    break;
                case BattleAction.Kind.Switch:
                    SwitchTo(side, planned.Action.PartyIndex);
                    break;
                case BattleAction.Kind.Item:
                    UseItem(side, planned.Action.ItemId, planned.Action.PartyIndex);
                    break;
                case BattleAction.Kind.Capture:
                    AttemptCapture(planned.Action.ItemId ?? ItemCatalog.PokeBallId);
                    break;
                case BattleAction.Kind.Run:
                    AttemptRun(side);
                    break;
            }
        }

        // ---- Moves ------------------------------------------------------------------

        private void UseMove(BattleSide side, int moveIndex)
        {
            var attackerState = _state.Sides(side);
            var attacker = attackerState.Active;
            var defenderSide = BattleState.Other(side);
            var defender = _state.ActiveOf(defenderSide);

            if (!CanAct(side, attacker)) { ClearProtectStreakIfUnused(attackerState, null); return; }

            var move = ResolveMoveForUse(side, moveIndex, out var slotIndex);
            if (move == null) return;

            ClearProtectStreakIfUnused(attackerState, move);
            SpendPp(attacker, slotIndex);
            attackerState.LastMoveId = move.Id;

            // The opponent is only "scouted" once it has shown the player a move.
            if (side == BattleSide.Opponent && attacker != null) _state.MarkScouted(attacker.SpeciesId);

            Emit(new MoveDeclaredEvent
            {
                Side = side,
                MoveId = move.Id,
                MoveDisplayName = move.DisplayName,
            });

            var selfTargeting = move.TargetsSelf;

            if (defender == null || defender.IsFainted)
            {
                // Nothing left to hit. Self-targeting support moves still resolve.
                if (!selfTargeting) return;
            }

            // The attack animation plays whether or not it connects — a viewer sees the
            // lunge and then the dodge, so the execution beat comes before every failure
            // path rather than only before a landed hit.
            if (!selfTargeting && defender != null && (_state.Sides(defenderSide).Volatiles & VolatileFlags.Protected) != 0)
            {
                EmitExecuted(side, defenderSide, move, 0, 1);
                Emit(new MoveMissedEvent { Attacker = side, Target = defenderSide, MoveId = move.Id, WasImmune = true });
                Emit(new MessageEvent { Text = $"{Name(defender)} protected itself!" });
                return;
            }

            if (!selfTargeting && defender != null && !RollAccuracy(side, defenderSide, move))
            {
                EmitExecuted(side, defenderSide, move, 0, 1);
                Emit(new MoveMissedEvent { Attacker = side, Target = defenderSide, MoveId = move.Id });
                Emit(new MessageEvent { Text = $"{Name(attacker)}'s attack missed!" });
                return;
            }

            if (!selfTargeting && defender != null)
            {
                var defenderAbility = AbilityOf(defender);
                if (defenderAbility.AbsorbsMove(move, move.Type))
                {
                    EmitExecuted(side, defenderSide, move, 0, 1);
                    AnnounceAbility(defenderSide, defenderAbility.Id);
                    defenderAbility.OnAbsorbedMove(Context(defenderSide), move);
                    Emit(new MoveMissedEvent
                    {
                        Attacker = side,
                        Target = defenderSide,
                        MoveId = move.Id,
                        WasImmune = true,
                        BlockingAbilityId = defenderAbility.Id,
                    });
                    return;
                }
            }

            if (move.Category == MoveCategory.Status)
            {
                ResolveStatusMove(side, defenderSide, move);
                return;
            }

            ResolveDamagingMove(side, defenderSide, move);
        }

        private void ResolveDamagingMove(BattleSide side, BattleSide defenderSide, MoveData move)
        {
            var attacker = _state.ActiveOf(side);
            var defender = _state.ActiveOf(defenderSide);
            if (defender == null) return;

            var hits = 1;
            if (move.MaxHits > 1) hits = _rng.Range(Math.Max(1, move.MinHits), move.MaxHits);

            var totalDamage = 0;
            var landed = false;

            for (var hit = 0; hit < hits; hit++)
            {
                if (defender.IsFainted || attacker.IsFainted) break;

                EmitExecuted(side, defenderSide, move, hit, hits);

                var critical = _rng.OneIn(DamageCalculator.CritDenominator(move));
                var roll = _rng.Range(DamageCalculator.MinRoll, DamageCalculator.MaxRoll);

                var ctx = BuildContext(_resolveContext, side, defenderSide, move, critical, roll);
                var damage = DamageCalculator.Compute(ctx, _typeChart);

                if (ctx.TypeMultiplier <= 0f)
                {
                    Emit(new MoveMissedEvent { Attacker = side, Target = defenderSide, MoveId = move.Id, WasImmune = true });
                    Emit(new MessageEvent { Text = $"It doesn't affect {Name(defender)}." });
                    return;
                }

                landed = true;
                var applied = ApplyMoveDamage(defenderSide, damage, critical, ctx.TypeMultiplier);
                totalDamage += applied;

                if (defender.IsFainted) break;
            }

            if (!landed) return;

            // Contact reactions read the whole exchange, so they run once after the hits.
            var defenderAbility = AbilityOf(defender);
            defenderAbility.OnHitByMove(new HitContext(this, defenderSide, side, move, totalDamage, defender.IsFainted));

            if (move.DrainRatio > 0f && totalDamage > 0)
            {
                var drained = Math.Max(1, (int)(totalDamage * move.DrainRatio));
                ApplyHeal(side, drained, move.Id);
            }

            if (move.RecoilRatio > 0f && totalDamage > 0 && !AbilityOf(attacker).BlocksRecoil(Context(side)))
            {
                var recoil = Math.Max(1, (int)(totalDamage * move.RecoilRatio));
                ApplyIndirectDamage(side, recoil, move.Id);
            }

            CheckHeldItem(defenderSide, HeldItemTrigger.LowHp);
            CheckFaint(defenderSide);
            CheckFaint(side);

            if (_state.ActiveOf(defenderSide) != null && !_state.ActiveOf(defenderSide).IsFainted)
                ApplySecondaryEffects(side, defenderSide, move);
        }

        /// <summary>
        /// The attack beat, carrying every presentation hint the move data holds. Emitted
        /// once per hit of a multi-hit move, and once on every failure path.
        /// </summary>
        private void EmitExecuted(BattleSide attacker, BattleSide target, MoveData move, int hitIndex, int hitCount,
            bool? contact = null)
        {
            Emit(new MoveExecutedEvent
            {
                Attacker = attacker,
                Target = target,
                MoveId = move.Id,
                Category = move.Category,
                MoveType = move.Type,
                VfxKey = move.VfxKey,
                AnimationKey = move.AnimationKey,
                IsProjectile = move.IsProjectile,
                MakesContact = contact ?? move.MakesContact,
                HitIndex = hitIndex,
                HitCount = hitCount,
            });
        }

        /// <summary>
        /// Applies damage from a move, giving the target's ability a last chance to survive
        /// it, and emits the hit beat.
        /// </summary>
        private int ApplyMoveDamage(BattleSide targetSide, int damage, bool critical, float typeMultiplier)
        {
            var target = _state.ActiveOf(targetSide);
            if (target == null || damage <= 0) return 0;

            var before = target.CurrentHp;
            if (damage >= before)
            {
                var survivalHp = AbilityOf(target).SurviveLethalHit(Context(targetSide), damage, before);
                if (survivalHp > 0) damage = before - survivalHp;
            }

            damage = Math.Min(damage, before);
            target.CurrentHp = before - damage;

            Emit(new DamageDealtEvent
            {
                Target = targetSide,
                Amount = damage,
                RemainingHp = target.CurrentHp,
                MaxHp = target.MaxHp,
                Critical = critical,
                TypeMultiplier = typeMultiplier,
                Effectiveness = EffectivenessRules.Classify(typeMultiplier),
            });

            return damage;
        }

        private void ResolveStatusMove(BattleSide side, BattleSide defenderSide, MoveData move)
        {
            var attacker = _state.ActiveOf(side);
            var targetSide = move.TargetsSelf ? side : defenderSide;

            EmitExecuted(side, targetSide, move, 0, 1, contact: false);

            if (move.HealRatio > 0f && attacker != null)
                ApplyHeal(side, Math.Max(1, (int)(attacker.MaxHp * move.HealRatio)), move.Id);

            // Protect is the one volatile the user grants itself, and it decays with use.
            if ((move.InflictsVolatile & VolatileFlags.Protected) != 0)
            {
                ResolveProtect(side);
                return;
            }

            ApplySecondaryEffects(side, defenderSide, move);
        }

        /// <summary>Stat changes, status and volatile riders, shared by damaging and status moves.</summary>
        private void ApplySecondaryEffects(BattleSide side, BattleSide defenderSide, MoveData move)
        {
            // A zero chance in the data means "always" — data authors set an explicit
            // percentage for the genuinely chancy riders.
            var chance = move.EffectChance > 0 ? move.EffectChance : 100;
            var targetSide = move.TargetsSelf ? side : defenderSide;

            if (move.StatChanges != null && move.StatChanges.Length > 0 && _rng.Chance(chance))
            {
                for (var i = 0; i < move.StatChanges.Length; i++)
                {
                    var change = move.StatChanges[i];
                    TryChangeStage(targetSide, change.Stat, change.Stages, move.Id);
                }
            }

            if (move.InflictsStatus != StatusCondition.None && _rng.Chance(chance))
                TryApplyStatus(targetSide, move.InflictsStatus, move.Id);

            var volatiles = move.InflictsVolatile & ~VolatileFlags.Protected;
            if (volatiles != VolatileFlags.None && _rng.Chance(chance))
                ApplyVolatiles(side, targetSide, volatiles, move);
        }

        private void ApplyVolatiles(BattleSide sourceSide, BattleSide targetSide, VolatileFlags flags, MoveData move)
        {
            var state = _state.Sides(targetSide);
            var target = _state.ActiveOf(targetSide);
            if (target == null || target.IsFainted) return;

            var added = VolatileFlags.None;

            if ((flags & VolatileFlags.Confused) != 0 && (state.Volatiles & VolatileFlags.Confused) == 0)
            {
                state.Volatiles |= VolatileFlags.Confused;
                state.ConfusionTurns = _rng.Range(ConfusionMinTurns, ConfusionMaxTurns);
                added |= VolatileFlags.Confused;
                Emit(new MessageEvent { Text = $"{Name(target)} became confused!" });
            }

            if ((flags & VolatileFlags.Flinched) != 0)
            {
                // A flinch only lands when the flincher moved first — the target has to
                // still have its turn ahead of it for there to be anything to interrupt.
                if (targetSide != sourceSide && !_hasMovedThisTurn[(int)targetSide])
                {
                    if (AbilityOf(target).BlocksFlinch(Context(targetSide)))
                    {
                        AnnounceAbility(targetSide, AbilityOf(target).Id);
                    }
                    else if ((state.Volatiles & VolatileFlags.Flinched) == 0)
                    {
                        state.Volatiles |= VolatileFlags.Flinched;
                        added |= VolatileFlags.Flinched;
                    }
                }
            }

            if ((flags & VolatileFlags.LeechSeeded) != 0 && (state.Volatiles & VolatileFlags.LeechSeeded) == 0)
            {
                var species = SpeciesOf(targetSide);
                if (DamageCalculator.HasType(species, ElementType.Grass))
                {
                    Emit(new MessageEvent { Text = $"It doesn't affect {Name(target)}." });
                }
                else
                {
                    state.Volatiles |= VolatileFlags.LeechSeeded;
                    state.LeechSeedSource = sourceSide;
                    added |= VolatileFlags.LeechSeeded;
                    Emit(new MessageEvent { Text = $"{Name(target)} was seeded!" });
                }
            }

            var passthrough = flags & (VolatileFlags.Charging | VolatileFlags.Recharging | VolatileFlags.Substitute);
            if (passthrough != VolatileFlags.None)
            {
                state.Volatiles |= passthrough;
                added |= passthrough;
            }

            if (added != VolatileFlags.None)
                Emit(new VolatileChangedEvent { Target = targetSide, Added = added, Removed = VolatileFlags.None });
        }

        private void ResolveProtect(BattleSide side)
        {
            var state = _state.Sides(side);
            var creature = state.Active;

            // Each consecutive Protect is a third as likely to work as the last.
            var successPercent = 100;
            for (var i = 0; i < state.ProtectStreak && successPercent > 1; i++) successPercent /= 3;

            if (_rng.Chance(successPercent))
            {
                state.Volatiles |= VolatileFlags.Protected;
                state.ProtectStreak++;
                Emit(new VolatileChangedEvent { Target = side, Added = VolatileFlags.Protected, Removed = VolatileFlags.None });
                Emit(new MessageEvent { Text = $"{Name(creature)} protected itself!" });
            }
            else
            {
                state.ProtectStreak = 0;
                Emit(new MessageEvent { Text = "But it failed!" });
            }
        }

        private void ClearProtectStreakIfUnused(BattleSideState state, MoveData move)
        {
            if (move != null && (move.InflictsVolatile & VolatileFlags.Protected) != 0) return;
            state.ProtectStreak = 0;
        }

        // ---- Pre-move gating --------------------------------------------------------

        /// <summary>
        /// Runs the checks that can stop a creature before it moves, in the order a player
        /// experiences them: flinch, freeze, sleep, paralysis, then confusion.
        /// </summary>
        private bool CanAct(BattleSide side, CreatureInstance creature)
        {
            if (creature == null || creature.IsFainted) return false;
            var state = _state.Sides(side);

            if ((state.Volatiles & VolatileFlags.Flinched) != 0)
            {
                state.Volatiles &= ~VolatileFlags.Flinched;
                Emit(new VolatileChangedEvent { Target = side, Added = VolatileFlags.None, Removed = VolatileFlags.Flinched });
                Emit(new MessageEvent { Text = $"{Name(creature)} flinched!" });
                return false;
            }

            if (creature.Status == StatusCondition.Freeze)
            {
                if (_rng.Chance(ThawPercent))
                {
                    SetStatus(side, StatusCondition.None);
                    Emit(new MessageEvent { Text = $"{Name(creature)} thawed out!" });
                }
                else
                {
                    Emit(new MessageEvent { Text = $"{Name(creature)} is frozen solid!" });
                    return false;
                }
            }

            if (creature.Status == StatusCondition.Sleep)
            {
                // The counter is the number of turns still to be slept through, so it is
                // spent before the wake check: a roll of 1 costs exactly one turn.
                if (creature.StatusCounter > 0)
                {
                    creature.StatusCounter--;
                    Emit(new MessageEvent { Text = $"{Name(creature)} is fast asleep." });
                    return false;
                }

                SetStatus(side, StatusCondition.None);
                Emit(new MessageEvent { Text = $"{Name(creature)} woke up!" });
            }

            if (creature.Status == StatusCondition.Paralysis && _rng.Chance(ParalysisFullStopPercent))
            {
                Emit(new MessageEvent { Text = $"{Name(creature)} is paralysed and can't move!" });
                return false;
            }

            if ((state.Volatiles & VolatileFlags.Confused) != 0)
            {
                state.ConfusionTurns--;
                if (state.ConfusionTurns <= 0)
                {
                    state.Volatiles &= ~VolatileFlags.Confused;
                    Emit(new VolatileChangedEvent { Target = side, Added = VolatileFlags.None, Removed = VolatileFlags.Confused });
                    Emit(new MessageEvent { Text = $"{Name(creature)} snapped out of its confusion!" });
                }
                else if (_rng.Chance(ConfusionSelfHitPercent))
                {
                    Emit(new MessageEvent { Text = $"{Name(creature)} is confused!" });
                    HitSelfInConfusion(side, creature);
                    return false;
                }
            }

            return true;
        }

        /// <summary>A confusion self-hit is a 40-power typeless physical hit against the user's own defence.</summary>
        private void HitSelfInConfusion(BattleSide side, CreatureInstance creature)
        {
            const int ConfusionPower = 40;

            var attack = Math.Max(1, (int)(creature.Stats[(int)StatKind.Attack] *
                                           StatMath.StageMultiplier(_state.Sides(side).Stages[(int)StatKind.Attack])));
            var defense = Math.Max(1, (int)(creature.Stats[(int)StatKind.Defense] *
                                            StatMath.StageMultiplier(_state.Sides(side).Stages[(int)StatKind.Defense])));

            var damage = 2 * creature.Level / 5 + 2;
            damage = damage * ConfusionPower * attack / defense;
            damage = damage / 50 + 2;
            damage = damage * _rng.Range(DamageCalculator.MinRoll, DamageCalculator.MaxRoll) / 100;

            Emit(new MessageEvent { Text = "It hurt itself in its confusion!" });
            ApplyIndirectDamage(side, Math.Max(1, damage), "confusion");
            CheckHeldItem(side, HeldItemTrigger.LowHp);
            CheckFaint(side);
        }

        // ---- Accuracy ---------------------------------------------------------------

        /// <summary>
        /// Hit chance as a 0-1 fraction. RNG-free so <see cref="ForecastMove"/> can report
        /// the same number the roll will be measured against.
        /// </summary>
        public float HitChance(BattleSide attackerSide, BattleSide defenderSide, MoveData move)
        {
            if (move == null) return 0f;
            if (move.Accuracy <= 0) return 1f;

            var attacker = _state.ActiveOf(attackerSide);
            var defender = _state.ActiveOf(defenderSide);
            if (attacker == null || defender == null) return 1f;

            var attackerAbility = AbilityOf(attacker);
            var defenderAbility = AbilityOf(defender);
            if (attackerAbility.IgnoresAccuracy(Context(attackerSide)) ||
                defenderAbility.IgnoresAccuracy(Context(defenderSide)))
                return 1f;

            var accuracyStage = _state.Sides(attackerSide).Stages[(int)StatKind.Accuracy];
            var evasionStage = _state.Sides(defenderSide).Stages[(int)StatKind.Evasion];

            var chance = move.Accuracy / 100f;
            chance *= StatMath.AccuracyStageMultiplier(accuracyStage);
            chance /= StatMath.AccuracyStageMultiplier(evasionStage);

            chance = attackerAbility.ModifyAccuracy(Context(attackerSide), chance, true);
            chance = defenderAbility.ModifyAccuracy(Context(defenderSide), chance, false);

            // Fog is the one weather that blunts accuracy outright.
            if (_state.Weather == Weather.Fog) chance *= 0.6f;

            return Math.Clamp(chance, 0f, 1f);
        }

        private bool RollAccuracy(BattleSide attackerSide, BattleSide defenderSide, MoveData move)
        {
            var chance = HitChance(attackerSide, defenderSide, move);
            if (chance >= 1f) return true;
            // Ten-thousandths keep sub-percent differences (evasion stacking) meaningful.
            return _rng.Next(10000) < (int)(chance * 10000f);
        }

        // ---- Switching, items, running, capture -------------------------------------

        private void SwitchTo(BattleSide side, int partyIndex)
        {
            var state = _state.Sides(side);
            if (partyIndex < 0 || partyIndex >= state.Party.Count) return;
            if (partyIndex == state.ActiveIndex) return;

            var incoming = state.Party[partyIndex];
            if (incoming == null || incoming.IsFainted) return;

            var outgoing = state.Active;
            if (outgoing != null && !outgoing.IsFainted)
                Emit(new CreatureWithdrawnEvent { Side = side, Creature = outgoing });

            state.ActiveIndex = partyIndex;
            state.ResetOnSwitch();

            SendOut(side, partyIndex, false);
            FireEntryAbility(side);
        }

        private void UseItem(BattleSide side, string itemId, int partyIndex)
        {
            if (ItemCatalog.IsBall(itemId))
            {
                AttemptCapture(itemId);
                return;
            }

            if (!ItemCatalog.TryGetBag(itemId, out var item))
            {
                Emit(new MessageEvent { Text = "Nothing happened." });
                return;
            }

            var state = _state.Sides(side);
            var index = partyIndex >= 0 && partyIndex < state.Party.Count ? partyIndex : state.ActiveIndex;
            var target = state.Party[index];
            if (target == null) return;

            Emit(new ItemUsedEvent { Side = side, ItemId = item.Id, ItemDisplayName = item.DisplayName });

            switch (item.Kind)
            {
                case BagItemKind.Heal:
                    if (target.IsFainted) { Emit(new MessageEvent { Text = "It had no effect." }); return; }
                    var amount = item.Amount > 0 ? item.Amount : target.MaxHp;
                    HealCreature(side, index, amount, item.Id);
                    break;

                case BagItemKind.StatusCure:
                    if (target.Status == StatusCondition.None || !item.CuresStatus(target.Status))
                    {
                        Emit(new MessageEvent { Text = "It had no effect." });
                        return;
                    }
                    var previous = target.Status;
                    target.Status = StatusCondition.None;
                    target.StatusCounter = 0;
                    Emit(new StatusChangedEvent { Target = side, Previous = previous, Current = StatusCondition.None });
                    break;

                case BagItemKind.Revive:
                    if (!target.IsFainted) { Emit(new MessageEvent { Text = "It had no effect." }); return; }
                    target.Status = StatusCondition.None;
                    target.StatusCounter = 0;
                    target.CurrentHp = Math.Max(1, target.MaxHp * item.Amount / 100);
                    Emit(new HealedEvent
                    {
                        Target = side,
                        Amount = target.CurrentHp,
                        RemainingHp = target.CurrentHp,
                        MaxHp = target.MaxHp,
                        SourceId = item.Id,
                    });
                    break;
            }
        }

        private void AttemptRun(BattleSide side)
        {
            if (_state.Kind == BattleKind.Trainer)
            {
                Emit(new MessageEvent { Text = "There's no running from a trainer battle!" });
                return;
            }

            var runner = _state.ActiveOf(side);
            var blocker = _state.ActiveOf(BattleState.Other(side));
            _runAttempts++;

            if (runner != null && AbilityOf(runner).GuaranteesEscape(Context(side)))
            {
                AnnounceAbility(side, AbilityOf(runner).Id);
                FinishBattle(BattleOutcome.Fled, 0);
                return;
            }

            var ownSpeed = runner?.Stats[(int)StatKind.Speed] ?? 1;
            var foeSpeed = Math.Max(1, blocker?.Stats[(int)StatKind.Speed] ?? 1);
            var odds = (ownSpeed * 128 / foeSpeed + 30 * _runAttempts) % 256;

            if (_rng.Next(256) < odds)
            {
                Emit(new MessageEvent { Text = "Got away safely!" });
                FinishBattle(BattleOutcome.Fled, 0);
            }
            else
            {
                Emit(new MessageEvent { Text = "Couldn't get away!" });
            }
        }

        private void AttemptCapture(string ballId)
        {
            if (_state.Kind == BattleKind.Trainer)
            {
                Emit(new MessageEvent { Text = "You can't catch another trainer's creature!" });
                return;
            }

            var target = _state.ActiveOf(BattleSide.Opponent);
            if (target == null || target.IsFainted) return;

            var species = SpeciesOf(BattleSide.Opponent);
            var catchRate = CaptureMath.CatchRateOf(species);
            var ballMultiplier = ItemCatalog.BallMultiplier(ballId, _state, species);

            ItemCatalog.TryGetBag(ballId, out var ballData);
            Emit(new ItemUsedEvent
            {
                Side = BattleSide.Player,
                ItemId = ballId,
                ItemDisplayName = ballData?.DisplayName ?? ballId,
            });

            var result = CaptureMath.Roll(target, catchRate, ballMultiplier, _rng);

            Emit(new CaptureAttemptEvent
            {
                BallId = ballId,
                Shakes = result.Shakes,
                Succeeded = result.Succeeded,
                CatchProbability = result.Probability,
            });

            if (result.Succeeded)
            {
                _state.CapturedCreature = target;
                _state.MarkScouted(target.SpeciesId);
                Emit(new MessageEvent { Text = $"{Name(target)} was caught!" });
                FinishBattle(BattleOutcome.Captured, 0);
            }
            else
            {
                Emit(new MessageEvent { Text = "Oh no! It broke free!" });
            }
        }

        // ---- End of turn ------------------------------------------------------------

        /// <summary>
        /// Upkeep, in the order the reference games use: weather chip, then healing
        /// abilities, then held items, then leech seed, then burn and poison. Each side is
        /// processed fastest-first so a creature that dies to chip damage stops there.
        /// </summary>
        private void EndOfTurn()
        {
            var playerFirst = EffectiveSpeed(BattleSide.Player) >= EffectiveSpeed(BattleSide.Opponent);
            _upkeepOrder[0] = playerFirst ? BattleSide.Player : BattleSide.Opponent;
            _upkeepOrder[1] = playerFirst ? BattleSide.Opponent : BattleSide.Player;

            for (var i = 0; i < _upkeepOrder.Length; i++)
            {
                var side = _upkeepOrder[i];
                if (_state.Outcome != BattleOutcome.InProgress) return;

                ApplyWeatherChip(side);
                if (Dead(side)) continue;

                AbilityOf(_state.ActiveOf(side))?.OnEndOfTurn(Context(side));
                if (Dead(side)) continue;

                CheckHeldItem(side, HeldItemTrigger.EndOfTurn);
                ApplyLeechSeed(side);
                if (Dead(side)) continue;

                ApplyStatusDamage(side);
                CheckHeldItem(side, HeldItemTrigger.LowHp);
                CheckFaint(side);
            }

            ClearTurnVolatiles(BattleSide.Player);
            ClearTurnVolatiles(BattleSide.Opponent);
        }

        private bool Dead(BattleSide side)
        {
            var creature = _state.ActiveOf(side);
            if (creature == null) return true;
            if (!creature.IsFainted) return false;
            CheckFaint(side);
            return true;
        }

        private void ApplyWeatherChip(BattleSide side)
        {
            var creature = _state.ActiveOf(side);
            if (creature == null || creature.IsFainted) return;

            var species = SpeciesOf(side);
            var immune = false;

            switch (_state.Weather)
            {
                case Weather.Sandstorm:
                    immune = DamageCalculator.HasType(species, ElementType.Rock)
                             || DamageCalculator.HasType(species, ElementType.Ground)
                             || DamageCalculator.HasType(species, ElementType.Steel);
                    break;
                case Weather.Hail:
                    immune = DamageCalculator.HasType(species, ElementType.Ice);
                    break;
                default:
                    return;
            }

            if (immune || AbilityOf(creature).BlocksWeatherDamage(Context(side), _state.Weather)) return;

            var damage = Math.Max(1, creature.MaxHp / WeatherChipDivisor);
            ApplyIndirectDamage(side, damage, _state.Weather.ToString().ToLowerInvariant());
            CheckFaint(side);
        }

        private void ApplyStatusDamage(BattleSide side)
        {
            var creature = _state.ActiveOf(side);
            if (creature == null || creature.IsFainted) return;

            switch (creature.Status)
            {
                case StatusCondition.Burn:
                    ApplyIndirectDamage(side, Math.Max(1, creature.MaxHp / BurnDivisor), "burn");
                    break;
                case StatusCondition.Poison:
                    ApplyIndirectDamage(side, Math.Max(1, creature.MaxHp / PoisonDivisor), "poison");
                    break;
                case StatusCondition.BadPoison:
                    // Escalating: n/16 of max HP on the nth turn, uncapped as in the games.
                    creature.StatusCounter = Math.Max(1, creature.StatusCounter + 1);
                    var fraction = Math.Max(1, creature.MaxHp * creature.StatusCounter / 16);
                    ApplyIndirectDamage(side, fraction, "bad-poison");
                    break;
            }
        }

        private void ApplyLeechSeed(BattleSide side)
        {
            var state = _state.Sides(side);
            if ((state.Volatiles & VolatileFlags.LeechSeeded) == 0) return;

            var seeded = state.Active;
            if (seeded == null || seeded.IsFainted) return;

            var drain = Math.Max(1, seeded.MaxHp / LeechSeedDivisor);
            var taken = ApplyIndirectDamage(side, drain, "leech-seed");
            if (taken > 0) ApplyHeal(state.LeechSeedSource, taken, "leech-seed");
            CheckFaint(side);
        }

        private void ClearTurnVolatiles(BattleSide side)
        {
            var state = _state.Sides(side);
            var expiring = state.Volatiles & (VolatileFlags.Flinched | VolatileFlags.Protected);
            if (expiring == VolatileFlags.None) return;

            state.Volatiles &= ~expiring;
            Emit(new VolatileChangedEvent { Target = side, Added = VolatileFlags.None, Removed = expiring });
        }

        // ---- Fainting, replacement, outcome -----------------------------------------

        private void CheckFaint(BattleSide side)
        {
            var state = _state.Sides(side);
            var creature = state.Active;
            if (creature == null || creature.CurrentHp > 0) return;
            if (creature.Status == StatusCondition.Fainted) return;

            creature.CurrentHp = 0;
            creature.Status = StatusCondition.Fainted;
            creature.StatusCounter = 0;
            state.Volatiles = VolatileFlags.None;

            _state.MarkScouted(creature.SpeciesId);
            Emit(new CreatureFaintedEvent { Side = side, Creature = creature });

            // Only the player's team banks experience; a defeated player creature just goes.
            if (side == BattleSide.Opponent) AwardExperience(creature);
        }

        private void AwardExperience(CreatureInstance defeated)
        {
            if (!TryGetSpecies(defeated.SpeciesId, out var species)) return;

            var playerSide = _state.PlayerSide;
            playerSide.Participants.Add(playerSide.Active?.InstanceId ?? string.Empty);

            var participants = 0;
            for (var i = 0; i < playerSide.Party.Count; i++)
            {
                var member = playerSide.Party[i];
                if (member != null && !member.IsFainted && playerSide.Participants.Contains(member.InstanceId))
                    participants++;
            }

            var award = StatMath.ExperienceFromDefeat(
                species.BaseExperience, defeated.Level, participants, _state.Kind == BattleKind.Trainer);
            if (award <= 0) return;

            for (var i = 0; i < playerSide.Party.Count; i++)
            {
                var member = playerSide.Party[i];
                if (member == null || member.IsFainted) continue;
                if (!playerSide.Participants.Contains(member.InstanceId)) continue;
                GrantExperience(member, award);
            }

            // The slate is wiped for the next opponent, so a creature that never faced it
            // does not quietly keep collecting.
            playerSide.Participants.Clear();
            if (playerSide.Active != null) playerSide.Participants.Add(playerSide.Active.InstanceId);
        }

        private void GrantExperience(CreatureInstance member, int amount)
        {
            var startLevel = member.Level;
            member.Experience += amount;

            var leveled = false;
            while (member.Level < StatMath.MaxLevel &&
                   member.Experience >= StatMath.ExperienceForLevel(member.Level + 1))
            {
                member.Level++;
                leveled = true;
            }

            if (leveled && TryGetSpecies(member.SpeciesId, out var species))
                CreatureFactory.RecomputeStats(member, species);

            Emit(new ExperienceGainedEvent
            {
                InstanceId = member.InstanceId,
                Amount = amount,
                NewTotal = member.Experience,
                NewLevel = member.Level,
                LeveledUp = leveled,
            });

            if (leveled)
                Emit(new MessageEvent { Text = $"{Name(member)} grew to level {member.Level}!" });
            else if (startLevel != member.Level)
                Emit(new MessageEvent { Text = $"{Name(member)} changed level." });
        }

        private void ReplaceFainted()
        {
            ReplaceFainted(BattleSide.Opponent);
            ReplaceFainted(BattleSide.Player);
        }

        private void ReplaceFainted(BattleSide side)
        {
            var state = _state.Sides(side);
            var active = state.Active;
            if (active != null && !active.IsFainted) return;
            if (!state.HasHealthyMember) return;

            // The engine picks the replacement itself: IBattleEngine has no "choose your
            // next creature" callback, so a forced switch cannot be handed to the UI. The
            // AI still gets to choose for its own side.
            var index = side == BattleSide.Opponent
                ? Ai.ChooseReplacement(this, side)
                : state.FirstHealthyIndex();

            if (index < 0) return;

            state.ActiveIndex = index;
            state.ResetOnSwitch();
            SendOut(side, index, true);
            FireEntryAbility(side);
        }

        private void EvaluateOutcome()
        {
            if (_state.Outcome != BattleOutcome.InProgress) return;

            if (!_state.PlayerSide.HasHealthyMember)
            {
                FinishBattle(BattleOutcome.PlayerDefeat, 0);
                return;
            }

            if (!_state.OpponentSide.HasHealthyMember)
            {
                FinishBattle(BattleOutcome.PlayerVictory, PrizeMoney());
            }
        }

        private int PrizeMoney()
        {
            if (_state.Kind != BattleKind.Trainer) return 0;

            var highest = 0;
            var party = _state.OpponentSide.Party;
            for (var i = 0; i < party.Count; i++)
                if (party[i] != null && party[i].Level > highest) highest = party[i].Level;

            return highest * TrainerMoneyPerLevel;
        }

        private void FinishBattle(BattleOutcome outcome, int money)
        {
            _state.Outcome = outcome;
            Emit(new BattleEndedEvent { Outcome = outcome, MoneyEarned = money });
        }

        // ---- Shared mutation helpers (also the IAbilityHost surface) -----------------

        /// <inheritdoc />
        public void Emit(BattleEvent evt)
        {
            if (evt == null) return;
            evt.Turn = _state.TurnNumber;
            _stream.Add(evt);
        }

        /// <inheritdoc />
        public void AnnounceAbility(BattleSide side, string abilityId)
        {
            if (string.IsNullOrEmpty(abilityId)) return;
            Emit(new AbilityTriggeredEvent
            {
                Side = side,
                AbilityId = abilityId,
                AbilityDisplayName = AbilityRegistry.Resolve(abilityId).DisplayName,
            });
        }

        /// <inheritdoc />
        public int ApplyIndirectDamage(BattleSide side, int amount, string sourceId)
        {
            var creature = _state.ActiveOf(side);
            if (creature == null || creature.IsFainted || amount <= 0) return 0;

            var applied = Math.Min(amount, creature.CurrentHp);
            creature.CurrentHp -= applied;

            Emit(new DamageDealtEvent
            {
                Target = side,
                Amount = applied,
                RemainingHp = creature.CurrentHp,
                MaxHp = creature.MaxHp,
                Critical = false,
                TypeMultiplier = 1f,
                Effectiveness = Effectiveness.Neutral,
                IndirectSourceId = sourceId,
            });

            return applied;
        }

        /// <inheritdoc />
        public int ApplyHeal(BattleSide side, int amount, string sourceId) =>
            HealCreature(side, _state.Sides(side).ActiveIndex, amount, sourceId);

        private int HealCreature(BattleSide side, int partyIndex, int amount, string sourceId)
        {
            var state = _state.Sides(side);
            if (partyIndex < 0 || partyIndex >= state.Party.Count) return 0;

            var creature = state.Party[partyIndex];
            if (creature == null || creature.IsFainted || amount <= 0) return 0;

            var healed = Math.Min(amount, creature.MaxHp - creature.CurrentHp);
            if (healed <= 0) return 0;

            creature.CurrentHp += healed;

            Emit(new HealedEvent
            {
                Target = side,
                Amount = healed,
                RemainingHp = creature.CurrentHp,
                MaxHp = creature.MaxHp,
                SourceId = sourceId,
            });

            return healed;
        }

        /// <inheritdoc />
        public bool TryChangeStage(BattleSide target, StatKind stat, int delta, string sourceId)
        {
            if (delta == 0) return false;

            var state = _state.Sides(target);
            var creature = state.Active;
            if (creature == null || creature.IsFainted) return false;

            var index = (int)stat;
            if (index < 0 || index >= BattleSideState.StageCount) return false;

            if (delta < 0 && AbilityOf(creature).BlocksStatDrop(Context(target), stat))
            {
                AnnounceAbility(target, creature.AbilityId);
                return false;
            }

            var before = state.Stages[index];
            var after = StatMath.Clamp(before + delta);
            if (after == before)
            {
                Emit(new MessageEvent
                {
                    Text = delta > 0
                        ? $"{Name(creature)}'s {stat} won't go higher!"
                        : $"{Name(creature)}'s {stat} won't go lower!",
                });
                return false;
            }

            state.Stages[index] = after;
            Emit(new StatStageChangedEvent
            {
                Target = target,
                Stat = stat,
                Delta = after - before,
                NewStage = after,
            });
            return true;
        }

        /// <inheritdoc />
        public bool TryApplyStatus(BattleSide target, StatusCondition status, string sourceId)
        {
            if (status == StatusCondition.None || status == StatusCondition.Fainted) return false;

            var state = _state.Sides(target);
            var creature = state.Active;
            if (creature == null || creature.IsFainted) return false;

            // One non-volatile status at a time, always.
            if (creature.Status != StatusCondition.None) return false;

            var ability = AbilityOf(creature);
            if (ability.BlocksStatus(new StatusAttemptContext(this, target, creature, status, sourceId)))
            {
                AnnounceAbility(target, ability.Id);
                return false;
            }

            if (IsTypeImmuneToStatus(SpeciesOf(target), status))
            {
                Emit(new MessageEvent { Text = $"It doesn't affect {Name(creature)}." });
                return false;
            }

            SetStatus(target, status);
            CheckHeldItem(target, HeldItemTrigger.OnStatus);
            return true;
        }

        /// <inheritdoc />
        public SpeciesData SpeciesOf(BattleSide side)
        {
            var creature = _state.ActiveOf(side);
            return creature != null && TryGetSpecies(creature.SpeciesId, out var species) ? species : null;
        }

        private void SetStatus(BattleSide side, StatusCondition status)
        {
            var creature = _state.ActiveOf(side);
            if (creature == null) return;

            var previous = creature.Status;
            creature.Status = status;

            creature.StatusCounter = status switch
            {
                // Turns that will actually be missed: a roll of 1-3 costs 1-3 turns.
                StatusCondition.Sleep => _rng.Range(SleepMinTurns, SleepMaxTurns),
                StatusCondition.BadPoison => 0,
                _ => 0,
            };

            Emit(new StatusChangedEvent { Target = side, Previous = previous, Current = status });
        }

        /// <summary>Poison types shrug off poison, Fire shrugs off burn, Electric shrugs off paralysis, Ice off freeze.</summary>
        private static bool IsTypeImmuneToStatus(SpeciesData species, StatusCondition status)
        {
            if (species == null) return false;
            switch (status)
            {
                case StatusCondition.Poison:
                case StatusCondition.BadPoison:
                    return DamageCalculator.HasType(species, ElementType.Poison)
                           || DamageCalculator.HasType(species, ElementType.Steel);
                case StatusCondition.Burn:
                    return DamageCalculator.HasType(species, ElementType.Fire);
                case StatusCondition.Paralysis:
                    return DamageCalculator.HasType(species, ElementType.Electric);
                case StatusCondition.Freeze:
                    return DamageCalculator.HasType(species, ElementType.Ice);
                default:
                    return false;
            }
        }

        private void CheckHeldItem(BattleSide side, HeldItemTrigger trigger)
        {
            var state = _state.Sides(side);
            var creature = state.Active;
            if (creature == null || creature.IsFainted) return;
            if (!ItemCatalog.TryGetHeld(creature.HeldItemId, out var item) || item.Trigger != trigger) return;

            var used = false;

            switch (trigger)
            {
                case HeldItemTrigger.LowHp:
                    if (creature.HpFraction > item.HpThreshold) return;
                    var heal = item.HealFraction > 0f
                        ? Math.Max(1, (int)(creature.MaxHp * item.HealFraction))
                        : item.HealAmount;
                    if (creature.CurrentHp >= creature.MaxHp) return;
                    Emit(new ItemUsedEvent { Side = side, ItemId = item.Id, ItemDisplayName = item.DisplayName });
                    used = ApplyHeal(side, heal, item.Id) > 0;
                    break;

                case HeldItemTrigger.EndOfTurn:
                    if (creature.CurrentHp >= creature.MaxHp) return;
                    var upkeep = item.HealFraction > 0f
                        ? Math.Max(1, (int)(creature.MaxHp * item.HealFraction))
                        : item.HealAmount;
                    Emit(new ItemUsedEvent { Side = side, ItemId = item.Id, ItemDisplayName = item.DisplayName });
                    used = ApplyHeal(side, upkeep, item.Id) > 0;
                    break;

                case HeldItemTrigger.OnStatus:
                    var curesConfusion = item.CuresConfusion && (state.Volatiles & VolatileFlags.Confused) != 0;
                    if (!item.CuresStatus(creature.Status) && !curesConfusion) return;

                    Emit(new ItemUsedEvent { Side = side, ItemId = item.Id, ItemDisplayName = item.DisplayName });

                    if (item.CuresStatus(creature.Status))
                    {
                        var previous = creature.Status;
                        creature.Status = StatusCondition.None;
                        creature.StatusCounter = 0;
                        Emit(new StatusChangedEvent { Target = side, Previous = previous, Current = StatusCondition.None });
                    }

                    if (curesConfusion)
                    {
                        state.Volatiles &= ~VolatileFlags.Confused;
                        state.ConfusionTurns = 0;
                        Emit(new VolatileChangedEvent { Target = side, Added = VolatileFlags.None, Removed = VolatileFlags.Confused });
                    }

                    used = true;
                    break;
            }

            if (used && item.Consumed) creature.HeldItemId = null;
        }

        private void SendOut(BattleSide side, int partyIndex, bool isReplacement)
        {
            var state = _state.Sides(side);
            if (partyIndex < 0 || partyIndex >= state.Party.Count) return;

            var creature = state.Party[partyIndex];
            if (creature == null) return;

            // The player always knows its own team; the opponent must earn its entry.
            if (side == BattleSide.Player) _state.MarkScouted(creature.SpeciesId);

            if (side == BattleSide.Player)
            {
                state.Participants.Clear();
                state.Participants.Add(creature.InstanceId);
            }

            Emit(new CreatureSentOutEvent { Side = side, Creature = creature, IsReplacement = isReplacement });
        }

        private void FireEntryAbility(BattleSide side)
        {
            var creature = _state.ActiveOf(side);
            if (creature == null || creature.IsFainted) return;
            AbilityOf(creature).OnEntry(Context(side));
        }

        // ---- Legal actions ----------------------------------------------------------

        /// <inheritdoc />
        public IReadOnlyList<BattleAction> LegalActions(BattleSide side)
        {
            _legalScratch.Clear();

            var state = _state.Sides(side);
            var active = state.Active;
            if (active == null || _state.Outcome != BattleOutcome.InProgress) return _legalScratch.ToArray();

            if (!active.IsFainted)
            {
                var usable = 0;
                for (var i = 0; i < active.Moves.Count; i++)
                {
                    if (!active.Moves[i].Usable) continue;
                    _legalScratch.Add(BattleAction.UseMove(side, i));
                    usable++;
                }

                // With no PP anywhere, Struggle is the only legal move — index -1 selects it.
                if (usable == 0) _legalScratch.Add(BattleAction.UseMove(side, -1));
            }

            for (var i = 0; i < state.Party.Count; i++)
            {
                if (i == state.ActiveIndex) continue;
                var member = state.Party[i];
                if (member != null && !member.IsFainted) _legalScratch.Add(BattleAction.SwitchTo(side, i));
            }

            // Bag contents live in IPlayerProfile, which the engine deliberately does not
            // reference, so item actions are offered by the UI rather than enumerated here.
            if (side == BattleSide.Player && _state.Kind == BattleKind.Wild)
            {
                _legalScratch.Add(BattleAction.Capture(side, ItemCatalog.PokeBallId));
                _legalScratch.Add(BattleAction.Run(side));
            }

            return _legalScratch.ToArray();
        }

        // ---- Forecast ---------------------------------------------------------------

        /// <inheritdoc />
        public DamageForecast ForecastMove(BattleSide attacker, int moveIndex)
        {
            var defenderSide = BattleState.Other(attacker);
            var attackerCreature = _state.ActiveOf(attacker);
            var defenderCreature = _state.ActiveOf(defenderSide);
            var move = MoveAt(attacker, moveIndex);

            if (attackerCreature == null || defenderCreature == null || move == null || defenderCreature.MaxHp <= 0)
                return new DamageForecast(0f, 0f, 0f, 0f, 1f, int.MaxValue);

            var hitChance = HitChance(attacker, defenderSide, move);

            if (move.Category == MoveCategory.Status || move.Power <= 0)
                return new DamageForecast(0f, 0f, 0f, hitChance, 1f, int.MaxValue);

            // An ability that swallows the move outright makes every damage figure zero,
            // and the type multiplier must say so rather than reporting a neutral 1.
            if (AbilityOf(defenderCreature).AbsorbsMove(move, move.Type))
                return new DamageForecast(0f, 0f, 0f, 0f, 0f, int.MaxValue);

            var minDamage = Simulate(attacker, defenderSide, move, DamageCalculator.MinRoll, false, out var typeMultiplier);
            if (typeMultiplier <= 0f)
                return new DamageForecast(0f, 0f, 0f, hitChance, 0f, int.MaxValue);

            var maxDamage = Simulate(attacker, defenderSide, move, DamageCalculator.MaxRoll, true, out _);

            // The 16 rolls average 92.5%, which is not an integer percent, so the two
            // straddling rolls are averaged instead of rounding one way.
            var low = Simulate(attacker, defenderSide, move, 92, false, out _);
            var high = Simulate(attacker, defenderSide, move, 93, false, out _);
            var normal = (low + high) * 0.5f;

            var critLow = Simulate(attacker, defenderSide, move, 92, true, out _);
            var critHigh = Simulate(attacker, defenderSide, move, 93, true, out _);
            var crit = (critLow + critHigh) * 0.5f;

            var critChance = DamageCalculator.CritChance(move);
            var expected = normal * (1f - critChance) + crit * critChance;

            var hits = Math.Max(1, move.MaxHits > 1 ? (move.MinHits + move.MaxHits) / 2 : 1);
            if (hits > 1)
            {
                minDamage *= Math.Max(1, move.MinHits);
                maxDamage *= move.MaxHits;
                expected *= hits;
            }

            var maxHp = (float)defenderCreature.MaxHp;
            var turnsToKo = expected <= 0f
                ? int.MaxValue
                : (int)Math.Ceiling(defenderCreature.CurrentHp / expected);

            return new DamageForecast(
                minDamage / maxHp,
                maxDamage / maxHp,
                expected / maxHp,
                hitChance,
                typeMultiplier,
                turnsToKo);
        }

        /// <summary>
        /// One RNG-free damage evaluation against the live field. Nothing here writes to
        /// state — the context is a scratch object owned by the engine, reset per call.
        /// </summary>
        private int Simulate(BattleSide attacker, BattleSide defenderSide, MoveData move, int roll, bool critical,
            out float typeMultiplier)
        {
            var ctx = BuildContext(_forecastContext, attacker, defenderSide, move, critical, roll);
            var damage = DamageCalculator.Compute(ctx, _typeChart);
            typeMultiplier = ctx.TypeMultiplier;
            return damage;
        }

        private DamageContext BuildContext(DamageContext ctx, BattleSide attackerSide, BattleSide defenderSide,
            MoveData move, bool critical, int roll)
        {
            ctx.ResetModifiers();
            ctx.Move = move;
            ctx.MoveType = move.Type;
            ctx.Attacker = _state.ActiveOf(attackerSide);
            ctx.Defender = _state.ActiveOf(defenderSide);
            ctx.AttackerSpecies = SpeciesOf(attackerSide);
            ctx.DefenderSpecies = SpeciesOf(defenderSide);
            ctx.AttackerState = _state.Sides(attackerSide);
            ctx.DefenderState = _state.Sides(defenderSide);
            ctx.AttackerSide = attackerSide;
            ctx.Weather = _state.Weather;
            ctx.Critical = critical;
            ctx.RollPercent = roll;
            ctx.Typeless = ReferenceEquals(move, Struggle);

            AbilityOf(ctx.Attacker).OnModifyDamage(Context(attackerSide), ctx, true);
            AbilityOf(ctx.Defender).OnModifyDamage(Context(defenderSide), ctx, false);

            return ctx;
        }

        // ---- Lookups ----------------------------------------------------------------

        /// <summary>Move in a side's slot, or <see cref="Struggle"/> when the index is -1 or the slot is spent.</summary>
        public MoveData MoveAt(BattleSide side, int moveIndex)
        {
            var creature = _state.ActiveOf(side);
            if (creature == null) return null;
            if (moveIndex < 0 || moveIndex >= creature.Moves.Count) return Struggle;

            var slot = creature.Moves[moveIndex];
            return _moves != null && _moves.TryGet(slot.MoveId, out var move) ? move : Struggle;
        }

        private MoveData ResolveMoveForUse(BattleSide side, int moveIndex, out int slotIndex)
        {
            slotIndex = -1;
            var creature = _state.ActiveOf(side);
            if (creature == null) return null;

            if (moveIndex >= 0 && moveIndex < creature.Moves.Count)
            {
                var slot = creature.Moves[moveIndex];
                if (slot.Usable && _moves.TryGet(slot.MoveId, out var move))
                {
                    slotIndex = moveIndex;
                    return move;
                }
            }

            return Struggle;
        }

        private void SpendPp(CreatureInstance creature, int slotIndex)
        {
            if (creature == null || slotIndex < 0 || slotIndex >= creature.Moves.Count) return;
            var slot = creature.Moves[slotIndex];
            slot.CurrentPp = Math.Max(0, slot.CurrentPp - 1);
            creature.Moves[slotIndex] = slot;
        }

        /// <summary>Speed after stages, ability weather boosts and the paralysis cut.</summary>
        public float EffectiveSpeed(BattleSide side)
        {
            var creature = _state.ActiveOf(side);
            if (creature == null) return 0f;

            var speed = creature.Stats[(int)StatKind.Speed] *
                        StatMath.StageMultiplier(_state.Sides(side).Stages[(int)StatKind.Speed]);

            speed = AbilityOf(creature).ModifySpeed(Context(side), speed);

            // Paralysis is applied last so a Swift Swim boost is halved, not the other way.
            if (creature.Status == StatusCondition.Paralysis) speed *= 0.5f;

            return speed;
        }

        /// <summary>Type chart in use, exposed to the AI so it can weigh a switch without a live forecast.</summary>
        internal ITypeChart TypeChart => _typeChart;

        /// <summary>Move registry in use, exposed to the AI so it can read a benched creature's moves.</summary>
        internal IMoveRegistry MoveRegistry => _moves;

        internal bool TryGetSpecies(int speciesId, out SpeciesData species)
        {
            if (_speciesCache.TryGetValue(speciesId, out species)) return species != null;

            species = _species != null && _species.TryGet(speciesId, out var found) ? found : null;
            _speciesCache[speciesId] = species;
            return species != null;
        }

        private AbilityContext Context(BattleSide side) =>
            new AbilityContext(this, side, _state.ActiveOf(side));

        private static BattleAbility AbilityOf(CreatureInstance creature) =>
            AbilityRegistry.Resolve(creature?.AbilityId);

        private string Name(CreatureInstance creature)
        {
            if (creature == null) return "It";
            if (!string.IsNullOrEmpty(creature.Nickname)) return creature.Nickname;
            return TryGetSpecies(creature.SpeciesId, out var species) ? species.DisplayName : "It";
        }
    }
}
