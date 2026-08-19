using System;
using System.Collections.Generic;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>
    /// The seam between the overworld and the simulation.
    ///
    /// <see cref="IGameFlow"/> hands over an <see cref="EncounterRequest"/> and freezes the
    /// player until a result comes back, so the single rule this class exists to guarantee
    /// is that <c>onResolved</c> fires exactly once on every path — a battle that finishes,
    /// a battle that cannot be built, and a battle that is torn down mid-turn.
    ///
    /// It is still pure C#: it builds parties, drives <see cref="BattleEngine"/> and
    /// publishes the event stream. A presenter subscribes to <see cref="BattleStaged"/> to
    /// claim the battle and then drives it a turn at a time through
    /// <see cref="SubmitAction"/>. When nothing claims it — a half-integrated build with no
    /// battle UI yet — the stage plays both sides out itself rather than stranding the
    /// player behind a transition, so the overworld loop stays complete either way.
    /// </summary>
    public sealed class BattleStage : IBattleStage
    {
        /// <summary>Hard ceiling on an unattended battle, so a pathological state cannot hang the flow.</summary>
        private const int AutoPlayTurnCap = 500;

        /// <summary>Species handed to a player who has no party at all, so a battle can still happen.</summary>
        private const int FallbackStarterSpeciesId = 5;   // Charmander
        private const int FallbackWildSpeciesId = 21;     // Pidgey
        private const int FallbackLevel = 5;

        private readonly List<CreatureInstance> _playerParty = new List<CreatureInstance>(6);
        private readonly List<CreatureInstance> _opponentParty = new List<CreatureInstance>(6);

        private Action<EncounterResult> _onResolved;
        private TrainerProfile _trainerProfile;
        private int _prizeMoney;

        /// <inheritdoc />
        public bool IsBattleActive { get; private set; }

        /// <summary>The engine running the current battle, or the last one that ran.</summary>
        public BattleEngine Engine { get; private set; }

        /// <summary>The request that staged the current battle.</summary>
        public EncounterRequest CurrentRequest { get; private set; }

        /// <summary>Trainer profile for the current battle, or null in a wild encounter.</summary>
        public TrainerProfile Trainer => _trainerProfile;

        /// <summary>
        /// Why the last encounter aborted, or null when it ran normally. The stage cannot
        /// log — it is pure C# — so the host component surfaces this instead of the failure
        /// disappearing into a silent flee.
        /// </summary>
        public string LastFailureReason { get; private set; }

        /// <summary>
        /// Raised once the field is staged and the intro events have been published.
        /// Subscribing claims the battle: the stage will then wait for
        /// <see cref="SubmitAction"/> rather than playing it out itself.
        /// </summary>
        public event Action<BattleStage> BattleStaged;

        /// <summary>Every event the engine produces, in order, including the pre-battle intro.</summary>
        public event Action<IReadOnlyList<BattleEvent>> EventsProduced;

        /// <summary>Difficulty used for trainer battles. Wild encounters always use the erratic policy.</summary>
        public AiDifficulty TrainerDifficulty { get; set; } = AiDifficulty.Standard;

        /// <summary>Levels added to a trainer's authored party, for scaling a rematch.</summary>
        public int TrainerLevelOffset { get; set; }

        /// <summary>Policy that plays the player's side when no presenter has claimed the battle.</summary>
        public BattleAi AutoPlayPolicy { get; set; } = new BattleAi(AiDifficulty.Standard);

        /// <summary>
        /// Diagnostic sink handed to every engine this stage builds — see
        /// <see cref="BattleEngine.Trace"/>. Set by the scene host; the stage itself is pure
        /// C# and cannot log.
        /// </summary>
        public Action<string> EngineTrace { get; set; }

        /// <summary>Registers this stage so <see cref="IGameFlow"/> can find it.</summary>
        public void Register() => ServiceHub.Register<IBattleStage>(this);

        // ---- Staging ----------------------------------------------------------------

        /// <inheritdoc />
        public void BeginEncounter(EncounterRequest request, Action<EncounterResult> onResolved)
        {
            if (request == null)
            {
                LastFailureReason = "No encounter request.";
                Resolve(onResolved, null);
                return;
            }

            if (IsBattleActive)
            {
                // Two battles sharing one stage would fight over the engine and over the
                // player's party. Refuse the second cleanly instead of interleaving.
                LastFailureReason = "A battle is already running.";
                Resolve(onResolved, null);
                return;
            }

            _onResolved = onResolved;
            CurrentRequest = request;
            _trainerProfile = null;
            _prizeMoney = 0;
            LastFailureReason = null;

            if (!TryStage(request, out var reason))
            {
                // The flow is holding the player frozen behind a covered screen. Reporting a
                // flee is the only honest way out; going silent strands them there.
                DiscardFailedStaging();
                Finish(BattleOutcome.Fled, reason);
                return;
            }

            IsBattleActive = true;

            Publish(Engine.DrainPendingEvents());

            var claimed = BattleStaged != null;
            BattleStaged?.Invoke(this);

            if (!claimed) AutoPlay();
        }

        /// <summary>
        /// Advances the battle by one turn with the supplied player action and publishes the
        /// resulting events. Returns the same stream so a caller that prefers polling to
        /// subscribing can use it directly. Returns an empty stream when no battle is live.
        /// </summary>
        public IReadOnlyList<BattleEvent> SubmitAction(BattleAction action)
        {
            if (!IsBattleActive || Engine == null) return Array.Empty<BattleEvent>();

            var stream = Engine.ResolveTurn(action);
            Publish(stream);

            if (Engine.State.Outcome != BattleOutcome.InProgress)
                Finish(Engine.State.Outcome, null);

            return stream;
        }

        /// <summary>
        /// Ends the battle early — a scene teardown, a quit to menu — and reports a flee so
        /// the waiting flow is released. Safe to call when no battle is running.
        /// </summary>
        public void Abort(string reason = null)
        {
            if (!IsBattleActive) return;
            Finish(BattleOutcome.Fled, reason);
        }

        /// <summary>
        /// Drops everything a failed staging may have left behind before the flee result is
        /// assembled.
        ///
        /// <see cref="Finish"/> builds its result from <see cref="Engine"/> and
        /// <see cref="_opponentParty"/>, and a staging that aborted leaves both holding the
        /// previous battle's contents — so an encounter that never started could hand the
        /// overworld the last battle's captured creature and its species list. Only reached
        /// on the failure path: a refused concurrent request returns before this, so a live
        /// battle is never torn out from under itself.
        /// </summary>
        private void DiscardFailedStaging()
        {
            Engine = null;
            _trainerProfile = null;
            _prizeMoney = 0;
            _playerParty.Clear();
            _opponentParty.Clear();
        }

        // ---- Party construction -----------------------------------------------------

        private bool TryStage(EncounterRequest request, out string reason)
        {
            reason = null;

            if (!ServiceHub.Has<ISpeciesRegistry>() || !ServiceHub.Has<IMoveRegistry>() || !ServiceHub.Has<ITypeChart>())
            {
                reason = "The dex, move pool or type chart is not registered yet.";
                return false;
            }

            try
            {
                BuildPlayerParty(request);
                BuildOpponentParty(request);
            }
            catch (Exception ex)
            {
                // Anything the data layer throws has to become a flee rather than an
                // exception unwinding into the flow coroutine that is holding the player.
                reason = $"Could not build the parties: {ex.Message}";
                return false;
            }

            if (_playerParty.Count == 0 || _opponentParty.Count == 0)
            {
                reason = "One of the parties came out empty.";
                return false;
            }

            // A wiped party would stage a battle that is already over, so the encounter is
            // declined instead. The overworld's response to a flee is to let the player go.
            if (!HasAnyHealthy(_playerParty))
            {
                reason = "The player has no creature able to battle.";
                return false;
            }

            if (!HasAnyHealthy(_opponentParty))
            {
                reason = "The opposing party has no creature able to battle.";
                return false;
            }

            Engine = new BattleEngine(ai: new BattleAi(
                request.Kind == BattleKind.Wild ? AiDifficulty.Wild : TrainerDifficulty));

            Engine.Trace = EngineTrace;
            Engine.SetOpponentTrainer(request.TrainerId);
            Engine.Begin(request.Kind, _playerParty, _opponentParty, request.Weather, request.Seed);
            return true;
        }

        private static bool HasAnyHealthy(List<CreatureInstance> party)
        {
            for (var i = 0; i < party.Count; i++)
                if (party[i] != null && !party[i].IsFainted) return true;
            return false;
        }

        private void BuildPlayerParty(EncounterRequest request)
        {
            _playerParty.Clear();

            if (ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile.Party != null)
            {
                var party = profile.Party;
                for (var i = 0; i < party.Count; i++)
                    if (party[i] != null) _playerParty.Add(party[i]);
            }

            if (_playerParty.Count > 0) return;

            // A player with nothing to fight with still has to be given a battle, or the
            // encounter cannot resolve and the loop dead-ends.
            _playerParty.Add(CreatureFactory.Create(
                FallbackStarterSpeciesId, FallbackLevel, request.Seed ^ 0x5EED));
        }

        private void BuildOpponentParty(EncounterRequest request)
        {
            _opponentParty.Clear();

            if (request.Kind == BattleKind.Trainer)
            {
                BuildTrainerParty(request);
                if (_opponentParty.Count > 0) return;
            }

            var speciesId = request.WildSpeciesId > 0 ? request.WildSpeciesId : FallbackWildSpeciesId;
            var level = Math.Max(1, request.WildLevel);

            _opponentParty.Add(CreatureFactory.Create(speciesId, level, request.Seed));
        }

        private void BuildTrainerParty(EncounterRequest request)
        {
            if (!ServiceHub.TryGet<ITrainerRegistry>(out var trainers) || string.IsNullOrEmpty(request.TrainerId))
            {
                BuildDefaultTrainerParty(request);
                return;
            }

            if (trainers.TryGetProfile(request.TrainerId, out var profile))
            {
                _trainerProfile = profile;
                _prizeMoney = profile?.Reward ?? 0;
            }

            // The registry contracts to return a fresh list every call, which matters
            // because the engine mutates what it is handed.
            var party = trainers.BuildParty(request.TrainerId, TrainerLevelOffset);
            if (party != null)
                for (var i = 0; i < party.Count; i++)
                    if (party[i] != null) _opponentParty.Add(party[i]);

            if (_opponentParty.Count == 0) BuildDefaultTrainerParty(request);
        }

        /// <summary>
        /// A two-creature roster for a trainer id the registry does not know. Keeps trainer
        /// battles runnable while the overworld's trainer table is still being authored.
        /// </summary>
        private void BuildDefaultTrainerParty(EncounterRequest request)
        {
            var level = Math.Max(1, request.WildLevel > 0 ? request.WildLevel : FallbackLevel);
            var lead = request.WildSpeciesId > 0 ? request.WildSpeciesId : FallbackWildSpeciesId;

            // Distinct ordinals, so two same-species members of a fallback party do not end
            // up sharing an instance id and confusing the presenters that key views on it.
            _opponentParty.Add(CreatureFactory.Create(lead, level, request.Seed, ordinal: 0));
            _opponentParty.Add(CreatureFactory.Create(FallbackWildSpeciesId, level, request.Seed, ordinal: 1));
        }

        // ---- Resolution --------------------------------------------------------------

        /// <summary>Plays both sides with the AI. Used when no presenter claimed the battle.</summary>
        private void AutoPlay()
        {
            var policy = AutoPlayPolicy ?? new BattleAi(AiDifficulty.Standard);
            var turns = 0;

            while (IsBattleActive && Engine.State.Outcome == BattleOutcome.InProgress && turns < AutoPlayTurnCap)
            {
                SubmitAction(policy.ChooseAction(Engine, BattleSide.Player));
                turns++;
            }

            // The cap is a safety net, not an expected path: report it rather than hiding it.
            if (IsBattleActive) Finish(BattleOutcome.Fled, "The battle exceeded its turn cap.");
        }

        private void Publish(IReadOnlyList<BattleEvent> stream)
        {
            if (stream == null || stream.Count == 0) return;
            EventsProduced?.Invoke(stream);
        }

        private void Finish(BattleOutcome outcome, string reason)
        {
            IsBattleActive = false;
            if (!string.IsNullOrEmpty(reason)) LastFailureReason = reason;

            var result = new EncounterResult
            {
                Outcome = outcome,
                CapturedCreature = Engine?.CapturedCreature,
                MoneyDelta = MoneyFor(outcome),
            };

            CollectSpeciesSeen(result.SpeciesSeen);

            // Cleared before the callback: a handler that immediately stages the next
            // encounter must not find itself holding this battle's callback.
            var callback = _onResolved;
            _onResolved = null;
            Resolve(callback, result);
        }

        private int MoneyFor(BattleOutcome outcome)
        {
            if (outcome != BattleOutcome.PlayerVictory) return 0;

            // A trainer's authored reward wins over the engine's level-derived estimate,
            // because the overworld balanced its economy against those numbers.
            if (_prizeMoney > 0) return _prizeMoney;
            return Engine != null && Engine.State.Kind == BattleKind.Trainer ? EngineAwardedMoney() : 0;
        }

        private int EngineAwardedMoney()
        {
            var highest = 0;
            for (var i = 0; i < _opponentParty.Count; i++)
                if (_opponentParty[i] != null && _opponentParty[i].Level > highest) highest = _opponentParty[i].Level;
            return highest * 24;
        }

        /// <summary>
        /// The opponent species the player actually met, so the overworld can mark the dex.
        ///
        /// Filtered through the engine's scouted set rather than dumping the whole party:
        /// this used to report every opposing party member, including trainers' reserves
        /// that never entered the field, which let a battle abandoned on the first turn
        /// fill in dex entries for creatures the player never laid eyes on. The engine
        /// already keeps the honest record — <see cref="BattleState.MarkScouted"/> fires
        /// when a creature shows a move, faints or is caught — so the stage asks it via
        /// <see cref="IBattleStateView.HasScouted"/> per party member instead of keeping a
        /// second, disagreeing notion of "seen". Iterating the party and testing
        /// membership (rather than exposing the raw set) also keeps the player's own
        /// species out of this list, exactly as before: their captured/seen bookkeeping
        /// belongs to the profile, not to the encounter result.
        /// </summary>
        private void CollectSpeciesSeen(List<int> destination)
        {
            if (Engine == null) return;

            for (var i = 0; i < _opponentParty.Count; i++)
            {
                var member = _opponentParty[i];
                if (member == null || !Engine.State.HasScouted(member.SpeciesId)) continue;
                if (!destination.Contains(member.SpeciesId)) destination.Add(member.SpeciesId);
            }
        }

        /// <summary>
        /// The single place the callback is invoked, so the "exactly once" rule has one
        /// place to be wrong rather than six.
        /// </summary>
        private static void Resolve(Action<EncounterResult> callback, EncounterResult result) =>
            callback?.Invoke(result ?? new EncounterResult { Outcome = BattleOutcome.Fled });
    }
}
