using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Battle;
using PokeLab.Core;
using Simulation = PokeLab.Battle.BattleStage;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The choreographer: turns the engine's ordered <see cref="BattleEvent"/> stream into a
    /// performance.
    ///
    /// The contract this class keeps, in priority order:
    ///
    /// 1. <b>Nothing is dropped.</b> Every event that arrives is queued and eventually
    ///    played. Presentation is advisory to game state, but a skipped beat is a visible
    ///    hole in the performance.
    /// 2. <b>Nothing overlaps.</b> One event is performed at a time, and the next does not
    ///    begin until the current one has run for at least its
    ///    <see cref="BattleEvent.SuggestedDuration"/>. Overlapping beats are how a battle
    ///    ends up with two creatures attacking simultaneously and neither reading.
    /// 3. <b>Beats may stretch, never shorten.</b> <c>SuggestedDuration</c> is a floor. Where
    ///    the authored choreography is longer, it wins.
    ///
    /// One deliberate liberty is taken with the stream: the queue is read ahead of the
    /// playhead. That is what makes a dodge possible — a miss is reported after the move
    /// that missed, so without lookahead the target could only start evading once the attack
    /// had already landed on it.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class BattlePresenter : MonoBehaviour, IBattleEventListener
    {
        [Header("Scene wiring")]
        [Tooltip("The arena layout. Found on this object or in the scene when empty.")]
        [SerializeField] private BattleStage stage;
        [Tooltip("The Cinemachine shot set. Found on this object or in the scene when empty.")]
        [SerializeField] private BattleCameraRig rig;

        [Header("Assets")]
        [Tooltip("Optional ball prefab for throws and captures. A stand-in is used when empty.")]
        [SerializeField] private GameObject ballPrefab;

        [Header("Timing")]
        [SerializeField] private BattleChoreography timing = new BattleChoreography();

        [Header("Simulation")]
        [Tooltip("Claim the registered battle stage and drive it a turn at a time. Turn this off " +
                 "only for a presenter fed by the synthetic stream, never for one in a playable scene.")]
        [SerializeField] private bool claimStagedBattles = true;
        [Tooltip("Seconds a turn's beats may take to perform before the driver stops waiting on them " +
                 "and asks for the next turn anyway.")]
        [SerializeField] private float turnPerformanceTimeout = 45f;
        [Tooltip("Unscaled seconds a claimed battle may run before it is aborted. A last resort: the " +
                 "player is frozen until the stage resolves, so a driver that stalls has to give up.")]
        [SerializeField] private float claimedBattleTimeout = 300f;

        [Header("Diagnostics")]
        [Tooltip("Log every event as it is performed, with its measured beat length.")]
        [SerializeField] private bool logBeats;

        // The queue is a List rather than a Queue because the choreography reads ahead of the
        // playhead to anticipate misses and damage; a Queue cannot be indexed.
        private readonly List<BattleEvent> _pending = new List<BattleEvent>();
        private readonly Dictionary<BattleSide, CreatureInstance> _active = new Dictionary<BattleSide, CreatureInstance>();

        private Coroutine _pump;
        private MoveDeclaredEvent _declaredMove;
        private BattleOutcome _outcome = BattleOutcome.InProgress;

        private Simulation _simulation;
        private Coroutine _driver;
        private float _holdUntil;

        /// <summary>True when the queue is empty and no beat is playing. Gate turn input on this.</summary>
        public bool IsIdle => _pump == null && _pending.Count == 0;

        /// <summary>Number of events still waiting to be performed. Diagnostic only.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Raised on the frame the presenter finishes the last queued event.</summary>
        public event Action Drained;

        /// <summary>The arena this presenter is staging into.</summary>
        public BattleStage Stage { get { EnsureWired(); return stage; } }

        /// <summary>The camera rig this presenter is directing.</summary>
        public BattleCameraRig Rig { get { EnsureWired(); return rig; } }

        /// <summary>Timing values, exposed so a debug harness can slow the whole performance down.</summary>
        public BattleChoreography Timing => timing;

        private void Awake()
        {
            EnsureWired();
        }

        private void OnEnable()
        {
            if (claimStagedBattles) ClaimSimulation(false);
        }

        /// <summary>
        /// Second attempt at the claim, and the one allowed to complain.
        ///
        /// <c>OnEnable</c> can run before the stage host has registered — the arena is enabled
        /// from inside a transition, and nothing orders that against another component's Awake.
        /// Start runs after every Awake in the frame, so a claim that failed once has had its
        /// chance by here and a claim that fails now is a real missing dependency.
        /// </summary>
        private void Start()
        {
            if (claimStagedBattles) ClaimSimulation(true);
        }

        private void OnDisable()
        {
            ReleaseSimulation();
        }

        private void EnsureWired()
        {
            if (stage == null) stage = GetComponent<BattleStage>();
            if (stage == null) stage = FindAnyObjectByType<BattleStage>();
            if (stage == null) stage = gameObject.AddComponent<BattleStage>();

            if (rig == null) rig = GetComponent<BattleCameraRig>();
            if (rig == null) rig = FindAnyObjectByType<BattleCameraRig>();
            if (rig == null) rig = gameObject.AddComponent<BattleCameraRig>();

            timing ??= new BattleChoreography();
        }

        // --- Claiming the simulation --------------------------------------------------------

        /// <summary>
        /// Subscribes to the registered battle stage, which is what claims the battle.
        ///
        /// The stage checks whether anything is listening to <c>BattleStaged</c> at the moment
        /// an encounter begins: with a listener it waits to be driven a turn at a time, and
        /// without one it plays both sides out itself in a single synchronous loop and reports
        /// the result. That second path is the one every encounter took — correct simulation,
        /// no performance, a frozen screen for the duration. So this has to be subscribed
        /// <i>before</i> the flow calls <c>BeginEncounter</c>, which it is: the arena is
        /// activated behind the transition cover a frame earlier, and the stage host registers
        /// itself several hundred execution-order steps ahead of this component.
        ///
        /// The event stream is subscribed here too, and it must be: the stage publishes the
        /// opening events before it raises <c>BattleStaged</c>, so a presenter that only
        /// subscribed on being claimed would miss the battle start and both send-outs.
        /// </summary>
        private void ClaimSimulation(bool complainWhenMissing)
        {
            if (_simulation != null) return;

            if (!ServiceHub.TryGet<IBattleStage>(out var registered) || !(registered is Simulation concrete))
            {
                if (complainWhenMissing)
                {
                    Debug.LogWarning("[BattlePresenter] No PokeLab.Battle.BattleStage is registered, so this " +
                                     "presenter cannot claim encounters and every battle will resolve as an " +
                                     "unattended simulation. Add a BattleStageHost to the scene.", this);
                }
                return;
            }

            _simulation = concrete;
            _simulation.EventsProduced += OnBattleEvents;
            _simulation.BattleStaged += OnBattleStaged;
        }

        private void ReleaseSimulation()
        {
            if (_driver != null) { CinematicRunner.Halt(_driver); _driver = null; }

            if (_simulation == null) return;
            _simulation.EventsProduced -= OnBattleEvents;
            _simulation.BattleStaged -= OnBattleStaged;

            // The driver has just been stopped, so nothing is left to submit turns and the flow
            // controller would wait out its whole battle timeout. Aborting reports a flee, which
            // is the only honest result available once the arena has gone away mid-battle.
            if (_simulation.IsBattleActive)
                _simulation.Abort("The battle arena was deactivated while a battle was running.");

            _simulation = null;
        }

        private void OnBattleStaged(Simulation staged)
        {
            if (_driver != null) CinematicRunner.Halt(_driver);
            _driver = CinematicRunner.Run(DriveBattle(staged));
        }

        /// <summary>
        /// Asks for one turn, performs everything it produces, and asks for the next.
        ///
        /// The pacing is the whole reason for claiming: the stage resolves a turn synchronously
        /// and hands back the entire event stream, so left to itself it resolves the whole
        /// battle in one frame. Waiting for the queue to drain between turns is what turns that
        /// stream back into a battle happening at a watchable speed.
        ///
        /// Runs on <see cref="CinematicRunner"/> rather than on this component, so a beat that
        /// disables the arena cannot kill the loop that has to keep the flow released. Both
        /// exits — the deadline and the empty stream — end in <c>Abort</c> rather than in a
        /// return, because the flow controller is holding the player frozen until the stage
        /// resolves and there is no other way to release it.
        /// </summary>
        private IEnumerator DriveBattle(Simulation battle)
        {
            float deadline = Time.unscaledTime + Mathf.Max(10f, claimedBattleTimeout);

            while (battle.IsBattleActive && Time.unscaledTime < deadline)
            {
                yield return WaitUntilIdle(Mathf.Max(1f, turnPerformanceTimeout));
                if (!battle.IsBattleActive) break;

                if (battle.Engine == null)
                {
                    battle.Abort("The stage reported a live battle with no engine behind it.");
                    break;
                }

                var stream = battle.SubmitAction(ChooseAction(battle));
                if (stream.Count == 0 && battle.IsBattleActive)
                {
                    // A live battle that produces no events for a turn cannot be advanced by
                    // asking again — the loop would spin at frame rate until the deadline.
                    battle.Abort("A turn resolved to an empty event stream.");
                    break;
                }
            }

            if (battle.IsBattleActive)
            {
                Debug.LogError($"[BattlePresenter] The battle ran past {claimedBattleTimeout:0}s without " +
                               "resolving; aborting so the player is released.", this);
                battle.Abort("The performance exceeded its time budget.");
            }

            _driver = null;
        }

        /// <summary>
        /// The player's action for a turn.
        ///
        /// Claiming the battle takes the player's side away from the stage's auto-play, and no
        /// command UI exists to fill the gap yet — so the same policy the stage would have used
        /// is asked for the choice, one turn at a time, paced by the performance. That is the
        /// honest interim: the alternative is not claiming, and not claiming is the invisible
        /// battle. <see cref="ActionSource"/> is the seam the battle UI replaces this through,
        /// and nothing else here has to change when it does.
        /// </summary>
        private BattleAction ChooseAction(Simulation battle)
        {
            var source = ActionSource;
            if (source != null) return source(battle.Engine);

            var policy = battle.AutoPlayPolicy ?? new BattleAi(AiDifficulty.Standard);
            return policy.ChooseAction(battle.Engine, BattleSide.Player);
        }

        /// <summary>
        /// Supplies the player's action for each turn. Set by the battle UI once it can ask the
        /// player; until then the stage's own policy answers.
        /// </summary>
        public Func<BattleEngine, BattleAction> ActionSource { get; set; }

        // --- Queue -----------------------------------------------------------------------

        /// <inheritdoc />
        public void OnBattleEvent(BattleEvent evt)
        {
            if (evt == null) return;

            // Enqueue and return immediately. The engine calls this synchronously while
            // resolving a turn, and the contract says handlers must not block.
            _pending.Add(evt);
            if (_pump == null && isActiveAndEnabled) _pump = CinematicRunner.Run(Pump());
        }

        /// <summary>Queues an entire turn's worth of events at once.</summary>
        public void OnBattleEvents(IReadOnlyList<BattleEvent> events)
        {
            if (events == null) return;
            for (int i = 0; i < events.Count; i++) OnBattleEvent(events[i]);
        }

        /// <summary>Blocks until every queued event has been performed.</summary>
        public IEnumerator WaitUntilIdle(float timeout = 120f)
        {
            float elapsed = 0f;
            while (!IsIdle && elapsed < timeout)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        /// <summary>
        /// Drops everything still queued. Only for tearing a battle down early — losing
        /// beats is a visual failure, so nothing in the normal flow calls this.
        /// </summary>
        public void FlushQueue()
        {
            _pending.Clear();
            if (_pump != null) { CinematicRunner.Halt(_pump); _pump = null; }
            ReleasePerformance();
        }

        /// <summary>
        /// Stops beats being performed for up to <paramref name="maxSeconds"/>, without
        /// dropping any.
        ///
        /// The transition director holds the performance while the screen is covered. Without
        /// it the stage begins publishing the moment the flow calls it, and the send-out — the
        /// ball throw, the burst, the landing — plays out in full behind an opaque wipe; the
        /// first thing the player ever sees is two creatures already standing there.
        ///
        /// The hold expires on its own rather than waiting to be released, because the only
        /// caller is a transition sequence that can be stopped mid-run, and a hold that
        /// outlived its holder would freeze the battle and with it the player.
        /// </summary>
        public void HoldPerformance(float maxSeconds)
            => _holdUntil = Time.unscaledTime + Mathf.Max(0f, maxSeconds);

        /// <summary>Ends a hold early. Safe when nothing is held.</summary>
        public void ReleasePerformance() => _holdUntil = 0f;

        /// <summary>True while beats are queued but deliberately not being performed.</summary>
        public bool IsHeld => Time.unscaledTime < _holdUntil;

        private IEnumerator Pump()
        {
            while (_pending.Count > 0)
            {
                while (IsHeld) yield return null;

                BattleEvent evt = _pending[0];
                _pending.RemoveAt(0);

                float startedAt = Time.unscaledTime;
                yield return Perform(evt);

                // The floor. Overshooting it is fine and normal; undershooting is not.
                float elapsed = Time.unscaledTime - startedAt;
                float remaining = evt.SuggestedDuration - elapsed;
                if (remaining > 0f) yield return CinematicRunner.Wait(remaining);

                if (logBeats)
                {
                    Debug.Log($"[BattlePresenter] {evt.GetType().Name} turn={evt.Turn} " +
                              $"beat={Time.unscaledTime - startedAt:0.00}s floor={evt.SuggestedDuration:0.00}s", this);
                }
            }

            _pump = null;
            Drained?.Invoke();
        }

        // --- Lookahead --------------------------------------------------------------------

        /// <summary>
        /// Finds the next queued event of a type, without consuming it.
        ///
        /// Used to know what is about to happen so the current beat can be staged for it:
        /// a move that is going to miss is performed differently from one that will land,
        /// and the difference has to be visible from the wind-up onward.
        /// </summary>
        private bool PeekNext<T>(out T found, int maxLookahead = 3) where T : BattleEvent
        {
            int limit = Mathf.Min(_pending.Count, Mathf.Max(1, maxLookahead));
            for (int i = 0; i < limit; i++)
            {
                if (_pending[i] is T typed) { found = typed; return true; }
            }
            found = null;
            return false;
        }

        /// <summary>Consumes a specific queued event that this beat has already performed.</summary>
        private void ConsumePeeked(BattleEvent evt)
        {
            if (evt == null) return;
            _pending.Remove(evt);
        }

        // --- Dispatch ---------------------------------------------------------------------

        private IEnumerator Perform(BattleEvent evt)
        {
            switch (evt)
            {
                case BattleStartedEvent e: return PlayBattleStarted(e);
                case CreatureSentOutEvent e: return PlaySentOut(e);
                case CreatureWithdrawnEvent e: return PlayWithdrawn(e);
                case MoveDeclaredEvent e: return PlayMoveDeclared(e);
                case MoveExecutedEvent e: return PlayMoveExecuted(e);
                case MoveMissedEvent e: return PlayMoveMissed(e);
                case DamageDealtEvent e: return PlayDamage(e);
                case HealedEvent e: return PlayHealed(e);
                case StatusChangedEvent e: return PlayStatusChanged(e);
                case VolatileChangedEvent e: return PlayVolatileChanged(e);
                case StatStageChangedEvent e: return PlayStatStage(e);
                case AbilityTriggeredEvent e: return PlayAbility(e);
                case ItemUsedEvent e: return PlayItemUsed(e);
                case WeatherChangedEvent e: return PlayWeather(e);
                case CreatureFaintedEvent e: return PlayFainted(e);
                case CaptureAttemptEvent e: return PlayCapture(e);
                case ExperienceGainedEvent e: return PlayExperience(e);
                case BattleEndedEvent e: return PlayBattleEnded(e);
                case MessageEvent e: return PlayMessage(e);
                // An unrecognised event still gets its floor honoured by the pump; holding
                // the current shot is the correct thing to do with a beat we cannot stage.
                default: return HoldBeat();
            }
        }

        private IEnumerator HoldBeat()
        {
            yield break;
        }

        // --- Battle opening ----------------------------------------------------------------

        private IEnumerator PlayBattleStarted(BattleStartedEvent e)
        {
            _outcome = BattleOutcome.InProgress;
            Stage.FaceOff(0.5f);
            Rig.SetRestingShot(BattleShot.Field);
            Rig.Show(BattleShot.Field);

            CinematicHooks.HudBeat("battle_start", 1f);
            CinematicHooks.Audio(CinematicAudioCues.EncounterSting, Stage.Center.position);

            // A beat held on the field shot while nothing else is happening. It reads as the
            // camera taking in the arena, and it covers the frame where the HUD arrives.
            yield return CinematicRunner.Wait(0.6f);
            Rig.Release();
        }

        // --- Send-out ----------------------------------------------------------------------

        private IEnumerator PlaySentOut(CreatureSentOutEvent e)
        {
            BattleSide side = e.Side;
            _active[side] = e.Creature;

            // A replacement is a faster, less ceremonial version of the same beat: the player
            // has already seen the throw once this battle and a full-length repeat drags.
            float scale = e.IsReplacement ? timing.ReplacementSpeedUp : 1f;

            CreatureView view = Stage.Occupy(side, e.Creature);
            view.SetModelVisible(false);

            // Refit the arena before retargeting: the marks move, and the camera solve reads
            // their positions. Doing it the other way round frames the previous layout.
            RefitStage();
            Rig.SetSubject(side, view, view.DisplayHeight);
            Rig.SetRoles(side, BattleStage.Opposite(side));
            Rig.Show(BattleShot.SendOut);

            Transform trainer = Stage.TrainerMarkOf(side);
            Vector3 burstPoint = Stage.BurstPointOf(side);

            // Who, if anyone, throws for this side. A wild creature has nobody standing behind
            // it, and that is the whole difference between the two kinds of encounter: the
            // player's creature is thrown out of a ball, the wild one is already standing there.
            // A wild creature that burst out of a ball would read as somebody else's.
            TrainerView thrower = Stage.TrainerViewOf(side);
            bool thrown = thrower != null && thrower.HasArt;

            if (thrown)
            {
                yield return thrower.Enter(timing.TrainerEnter * scale);

                // Wind-up. Nothing moves yet — this is what makes the throw read as a throw, and
                // it only reads at all now that there is a person to wind up.
                yield return CinematicRunner.Wait(timing.ThrowWindUp * scale);

                var ball = BallActor.Spawn(ballPrefab, trainer.position + Vector3.up * 1.25f);
                yield return ball.Throw(burstPoint, timing.BallFlight * scale, 1.3f);
                yield return ball.Open(timing.BallOpen * scale);

                view.SetModelVisible(true);
                view.Play(CreatureAnimation.SentOut, 0f);
                ball.Dismiss();

                CinematicHooks.Vfx(CinematicVfxKeys.BallBurst, burstPoint, Quaternion.identity);

                // Fall and land with weight. The compression on contact is the beat that sells
                // the creature as a physical object rather than a spawned prop.
                float fallHeight = Mathf.Max(0.8f, burstPoint.y - Stage.MarkOf(side).position.y);
                yield return view.PlayEntrance(fallHeight, timing.LandFall * scale, timing.LandSettle * scale);

                Rig.Shake?.Bump(view.transform.position, Mathf.Clamp(view.DisplayHeight * 0.35f, 0.12f, 0.6f));
                CinematicHooks.Vfx(CinematicVfxKeys.LandingDust, view.transform.position, Quaternion.identity);
                CinematicHooks.Audio(CinematicAudioCues.CreatureLand, view.transform.position);
            }
            else
            {
                // No ball, no fall, no landing dust. The creature is revealed where it stands,
                // which is what the player walked into.
                view.SetModelVisible(true);
                view.Play(CreatureAnimation.SentOut, 0f);
                yield return CinematicRunner.Wait(timing.LandSettle * scale);
            }

            view.PlayAuthored(CreatureAnimation.IdleBattle);
            CinematicHooks.Cry(e.Creature?.SpeciesId ?? 0, view.transform.position);
            yield return CinematicRunner.Wait(timing.CryHold * scale);

            // The trainer leaves alongside the face-off rather than before it, so the shot is
            // never held on an empty mark waiting for somebody to finish walking.
            Coroutine leaving = thrown ? CinematicRunner.Run(thrower.Exit(timing.TrainerExit * scale)) : null;

            // Turn to face the opponent last, as a deliberate act, not as part of landing.
            Vector3 facing = Stage.MarkOf(BattleStage.Opposite(side)).position;
            yield return view.FaceTowardsAndWait(facing, timing.FaceOffTurn * scale);
            if (leaving != null) yield return leaving;

            Rig.Release();
        }

        private IEnumerator PlayWithdrawn(CreatureWithdrawnEvent e)
        {
            CreatureView view = Stage.ViewOf(e.Side);
            Rig.SetRoles(e.Side, BattleStage.Opposite(e.Side));
            Rig.Show(BattleCameraRig.FocusOn(e.Side));

            // Turn back toward its trainer before being recalled — a creature recalled while
            // still facing the enemy looks like it was deleted rather than called back.
            yield return view.FaceTowardsAndWait(Stage.TrainerMarkOf(e.Side).position, 0.26f);
            view.PlayAuthored(CreatureAnimation.Recalled);

            var ball = BallActor.Spawn(ballPrefab, Stage.TrainerMarkOf(e.Side).position + Vector3.up * 1.2f);
            yield return ball.Recall(view, timing.RecallBeam);
            ball.Dismiss(0.6f);

            yield return CinematicRunner.Wait(timing.RecallHold);
            Rig.Release();
        }

        // --- Moves ---------------------------------------------------------------------------

        private IEnumerator PlayMoveDeclared(MoveDeclaredEvent e)
        {
            _declaredMove = e;
            BattleSide attacker = e.Side;
            BattleSide target = BattleStage.Opposite(attacker);

            Rig.SetRoles(attacker, target);

            // Anticipation pans toward the attacker rather than pushing in on it. Starting
            // tight leaves nowhere to go when the attack actually fires, and after the pivot
            // "tight" is a 1.33× push that has to be spent on the impact, not on the wind-up.
            Rig.Show(BattleCameraRig.FocusOn(attacker));

            CreatureView view = Stage.ViewOf(attacker);
            view.FaceTowards(Stage.MarkOf(target).position, 0.22f);
            CinematicHooks.HudBeat("move_declared", 1f);

            yield return CinematicRunner.Wait(timing.MoveAnticipation);
        }

        private IEnumerator PlayMoveExecuted(MoveExecutedEvent e)
        {
            // The declared move is only used for its display name, and only on the first hit
            // of a multi-hit sequence: naming the move once per hit turns a flurry into a
            // stutter in the log.
            if (e.HitIndex <= 0 && _declaredMove != null && _declaredMove.Side == e.Attacker)
            {
                CinematicHooks.HudBeat("move_" + (_declaredMove.MoveId ?? e.MoveId), 1f);
            }

            CreatureView attacker = Stage.ViewOf(e.Attacker);
            CreatureView target = Stage.ViewOf(e.Target);
            Rig.SetRoles(e.Attacker, e.Target);

            bool firstHit = e.HitIndex <= 0;
            bool lastHit = e.HitIndex >= e.HitCount - 1;

            // Read ahead: if this move is going to miss, the target has to start moving
            // before the attack arrives or the dodge looks like a delayed reaction.
            bool willMiss = PeekNext<MoveMissedEvent>(out var miss, 2);

            if (firstHit)
            {
                Rig.Show(BattleCameraRig.FocusOn(e.Attacker));
                attacker.FaceTowards(Stage.MarkOf(e.Target).position, 0.18f);
            }
            else
            {
                // Multi-hit: no fresh anticipation, no fresh camera move. The hits should
                // feel like one continuous flurry, not like N separate attacks.
                yield return CinematicRunner.Wait(timing.MultiHitAnticipation);
            }

            Coroutine dodge = null;
            if (willMiss)
            {
                Vector3 evade = Vector3.Cross(Vector3.up, Rig.Axis).normalized;
                if (e.Target == BattleSide.Opponent) evade = -evade;
                // Started, not awaited: the evasion and the attack must overlap in time or
                // the attack is not something the target is dodging.
                dodge = CinematicRunner.Run(DodgeRoutine(target, evade));
            }

            switch (e.Category)
            {
                case MoveCategory.Physical:
                    yield return PlayPhysical(e, attacker, target, willMiss);
                    break;
                case MoveCategory.Special:
                    yield return PlaySpecial(e, attacker, target, willMiss);
                    break;
                default:
                    yield return PlayStatusMove(e, attacker, target);
                    break;
            }

            if (dodge != null) yield return dodge;

            if (willMiss)
            {
                // Performed here rather than as its own beat, because a dodge that plays after
                // the attack has already finished is not a dodge. The event is consumed so
                // the pump does not stage it a second time; its floor has been more than
                // covered by the beat just played.
                ConsumePeeked(miss);
                yield return PlayMissBeat(miss);
            }

            if (lastHit)
            {
                attacker.PlayAuthored(CreatureAnimation.IdleBattle);
                yield return CinematicRunner.Wait(timing.MoveRecovery);
            }
        }

        /// <summary>
        /// Physical: travel into contact. The camera cuts to the reaction shot on the frame
        /// of contact rather than at the start of the lunge, so the impact is what changes
        /// the shot.
        /// </summary>
        private IEnumerator PlayPhysical(MoveExecutedEvent e, CreatureView attacker, CreatureView target, bool willMiss)
        {
            attacker.Play(CreatureAnimation.AttackPhysical, CreatureView.DefaultCrossfade(CreatureAnimation.AttackPhysical));

            Vector3 toTarget = target.transform.position - attacker.transform.position;
            toTarget.y = 0f;
            float gap = toTarget.magnitude;
            // Stop short by the combined body radii so the models do not interpenetrate.
            float standoff = (attacker.DisplayHeight + target.DisplayHeight) * 0.32f;
            float travel = Mathf.Max(0.1f, gap - standoff);
            if (!e.MakesContact) travel *= 0.35f; // a non-contact physical move stays back

            float contactAt = timing.PhysicalContactAt;
            CinematicRunner.ForkDelayed(timing.PhysicalLunge * contactAt, ContactMoment(e, target, willMiss));

            yield return attacker.Motion.Lunge(toTarget, travel, timing.PhysicalLunge, contactAt);
        }

        /// <summary>
        /// Special: plant, charge, release. The attacker never leaves its mark; the energy
        /// travels instead, which is the whole visual difference from a physical move.
        /// </summary>
        private IEnumerator PlaySpecial(MoveExecutedEvent e, CreatureView attacker, CreatureView target, bool willMiss)
        {
            attacker.Play(CreatureAnimation.AttackSpecial, CreatureView.DefaultCrossfade(CreatureAnimation.AttackSpecial));

            float release = timing.SpecialReleaseAt;
            Coroutine plant = CinematicRunner.Run(attacker.Motion.Plant(timing.SpecialPlant, release));

            yield return CinematicRunner.Wait(timing.SpecialPlant * release);

            if (e.IsProjectile)
            {
                yield return FireProjectile(e, attacker, target, willMiss);
            }
            else
            {
                // A non-projectile special resolves at the target immediately: the effect is
                // conjured on them rather than thrown at them.
                yield return ContactMoment(e, target, willMiss);
            }

            yield return plant;
        }

        private IEnumerator PlayStatusMove(MoveExecutedEvent e, CreatureView attacker, CreatureView target)
        {
            attacker.Play(CreatureAnimation.AttackStatus, CreatureView.DefaultCrossfade(CreatureAnimation.AttackStatus));
            Rig.Show(BattleCameraRig.FocusOn(e.Attacker));

            yield return CinematicRunner.Parallel(
                attacker.Motion.Swell(timing.StatusGesture),
                EmitMoveVfx(e, attacker, target));
        }

        private IEnumerator EmitMoveVfx(MoveExecutedEvent e, CreatureView attacker, CreatureView target)
        {
            string key = string.IsNullOrEmpty(e.VfxKey) ? CinematicVfxKeys.AbilityFlare : e.VfxKey;
            Transform muzzle = attacker.MuzzleAnchor != null ? attacker.MuzzleAnchor : attacker.transform;
            CinematicHooks.Vfx(key, muzzle.position, attacker.transform.forward);
            yield break;
        }

        /// <summary>
        /// A projectile that actually travels. It leaves <c>Anchor_Muzzle</c>, flies to the
        /// target's <c>Anchor_Body</c>, and the contact moment is raised on arrival — not on
        /// a timer that happens to be close.
        /// </summary>
        private IEnumerator FireProjectile(MoveExecutedEvent e, CreatureView attacker, CreatureView target, bool willMiss)
        {
            Transform muzzle = attacker.MuzzleAnchor != null ? attacker.MuzzleAnchor : attacker.transform;
            Transform body = target.BodyAnchor != null ? target.BodyAnchor : target.transform;

            string key = string.IsNullOrEmpty(e.VfxKey) ? CinematicVfxKeys.ImpactGeneric : e.VfxKey;
            var projectile = ProjectileActor.Spawn(key, muzzle.position);
            CinematicHooks.Audio(CinematicAudioCues.Whoosh, muzzle.position);

            // Flight scales with the actual gap, so a projectile does not crawl across a
            // short stage or teleport across a long one.
            float distance = Vector3.Distance(muzzle.position, body.position);
            float flight = Mathf.Clamp(timing.ProjectileFlight * (distance / 5f), 0.18f, 1.1f);

            if (willMiss)
            {
                // Aim where the target was. It will not be there, and the shot sails through.
                yield return projectile.FlyPast(body.position, flight * 1.25f, 3.5f, 0.35f);
                projectile.Finish();
                yield break;
            }

            yield return projectile.FlyTo(body, flight, 0.45f, 480f);
            projectile.Finish();
            yield return ContactMoment(e, target, false);
        }

        /// <summary>
        /// The frame the attack lands: impact VFX, audio, and the push-in. Damage numbers and
        /// recoil are not applied here — those belong to the <see cref="DamageDealtEvent"/>
        /// that the engine emits next, and doing them twice would double the reaction.
        /// </summary>
        private IEnumerator ContactMoment(MoveExecutedEvent e, CreatureView target, bool willMiss)
        {
            if (willMiss) yield break;

            Vector3 point = target.BodyAnchor != null ? target.BodyAnchor.position : target.transform.position;
            string key = string.IsNullOrEmpty(e.VfxKey) ? CinematicVfxKeys.ImpactGeneric : e.VfxKey;
            CinematicHooks.Vfx(key, point, -Rig.Axis);

            // Look ahead at the damage this is about to cause so the shot is chosen correctly
            // on the frame of contact rather than one event later. A critical takes the push;
            // anything else stays on a pan, because spending the rig's whole zoom range on
            // every hit leaves nothing to escalate with.
            bool crit = PeekNext<DamageDealtEvent>(out var incoming, 2) && incoming.Critical;

            Rig.SetRoles(e.Attacker, e.Target);
            Rig.Show(crit ? BattleShot.FieldPush : BattleCameraRig.FocusOn(e.Target));
            yield break;
        }

        private IEnumerator DodgeRoutine(CreatureView target, Vector3 evadeDirection)
        {
            yield return CinematicRunner.Wait(timing.DodgePrepare);
            target.Play(CreatureAnimation.Dodge, CreatureView.DefaultCrossfade(CreatureAnimation.Dodge));
            CinematicHooks.Vfx(CinematicVfxKeys.DodgeStreak, target.transform.position, evadeDirection);
            yield return target.Motion.Dodge(evadeDirection, timing.DodgeMove);
            target.PlayAuthored(CreatureAnimation.IdleBattle);
        }

        private IEnumerator PlayMoveMissed(MoveMissedEvent e)
        {
            // Reached only when the miss was not already consumed inside the executed beat —
            // an immunity reported without a preceding MoveExecutedEvent, for instance.
            CreatureView target = Stage.ViewOf(e.Target);
            Rig.SetRoles(e.Attacker, e.Target);
            Rig.Show(BattleCameraRig.FocusOn(e.Target));

            if (!e.WasImmune)
            {
                Vector3 evade = Vector3.Cross(Vector3.up, Rig.Axis).normalized;
                if (e.Target == BattleSide.Opponent) evade = -evade;
                yield return DodgeRoutine(target, evade);
            }

            yield return PlayMissBeat(e);
        }

        private IEnumerator PlayMissBeat(MoveMissedEvent e)
        {
            if (e != null && e.WasImmune)
            {
                // An immunity is a different statement from a dodge: the target does not move
                // at all, which is what makes it read as "that did nothing" rather than "that
                // was avoided".
                CinematicHooks.HudBeat("immune", 1f);
                CinematicHooks.Vfx(CinematicVfxKeys.AbilityFlare, Rig.ImpactPointOf(e.Target), Quaternion.identity);
            }
            else
            {
                CinematicHooks.HudBeat("miss", 1f);
            }

            yield return CinematicRunner.Wait(timing.MissBeat);
            Rig.Release();
        }

        // --- Damage ----------------------------------------------------------------------------

        private IEnumerator PlayDamage(DamageDealtEvent e)
        {
            CreatureView target = Stage.ViewOf(e.Target);
            float fraction = e.MaxHp > 0 ? Mathf.Clamp01(e.Amount / (float)e.MaxHp) : 0.2f;

            Rig.SetRoles(BattleStage.Opposite(e.Target), e.Target);

            // Indirect damage — burn, weather, recoil — gets a quieter treatment. Using the
            // full impact language for a poison tick makes every real hit feel smaller.
            bool indirect = !string.IsNullOrEmpty(e.IndirectSourceId);

            if (indirect)
            {
                Rig.Show(BattleCameraRig.FocusOn(e.Target));
                CinematicHooks.Vfx(CinematicVfxKeys.ImpactGeneric, Rig.ImpactPointOf(e.Target), Quaternion.identity);
                yield return target.Motion.Flinch(0.5f, 0.4f);
                Rig.Release();
                yield break;
            }

            Rig.Show(BattleShot.FieldPush);
            Rig.PunchForDamage(e.Target, fraction, e.Critical, e.Effectiveness);

            CinematicHooks.Audio(EffectivenessCue(e.Effectiveness), target.transform.position);
            if (e.Critical)
            {
                CinematicHooks.Audio(CinematicAudioCues.Critical, target.transform.position);
                CinematicHooks.Vfx(CinematicVfxKeys.ImpactCritical, Rig.ImpactPointOf(e.Target), Quaternion.identity);
                CinematicHooks.HudBeat("critical", 1f);
            }
            if (e.Effectiveness == Effectiveness.SuperEffective)
            {
                CinematicHooks.Vfx(CinematicVfxKeys.ImpactSuperEffective, Rig.ImpactPointOf(e.Target), Quaternion.identity);
                CinematicHooks.HudBeat("supereffective", 1f);
            }

            target.Play(CreatureAnimation.Hit, CreatureView.DefaultCrossfade(CreatureAnimation.Hit));

            Vector3 knockDirection = e.Target == BattleSide.Player ? -Rig.Axis : Rig.Axis;

            // The hitch runs alongside the recoil, not before it: freezing first and then
            // recoiling separates cause from effect and both halves read as glitches.
            IEnumerator recoil = target.Motion.Recoil(knockDirection, fraction, timing.RecoilFor(fraction));
            if (e.Critical && Rig.Shake != null)
            {
                yield return CinematicRunner.Parallel(recoil, Rig.Shake.Hitch(Mathf.Lerp(0.6f, 1f, fraction)));
            }
            else
            {
                yield return recoil;
            }

            float hold = e.Critical ? timing.PunchInHoldCritical : timing.PunchInHold;
            yield return CinematicRunner.Wait(hold);

            if (e.Effectiveness == Effectiveness.SuperEffective)
                yield return CinematicRunner.Wait(timing.SuperEffectiveBeat);

            // Do not return to idle if the creature is about to faint — playing a battle idle
            // for one frame before a collapse is the single most obvious animation pop in a
            // battle, and the fainted event is always the next one when it happens.
            bool faintNext = PeekNext<CreatureFaintedEvent>(out var faint, 1) && faint.Side == e.Target;
            if (!faintNext) target.PlayAuthored(CreatureAnimation.IdleBattle);

            Rig.Release();
        }

        private static string EffectivenessCue(Effectiveness effectiveness)
        {
            switch (effectiveness)
            {
                case Effectiveness.SuperEffective: return CinematicAudioCues.HitSuper;
                case Effectiveness.NotVeryEffective: return CinematicAudioCues.HitWeak;
                default: return CinematicAudioCues.HitNeutral;
            }
        }

        private IEnumerator PlayHealed(HealedEvent e)
        {
            CreatureView view = Stage.ViewOf(e.Target);
            Rig.SetRoles(e.Target, BattleStage.Opposite(e.Target));
            Rig.Show(BattleCameraRig.FocusOn(e.Target));

            CinematicHooks.Vfx(CinematicVfxKeys.StatUp, view.transform.position, Quaternion.identity);
            CinematicHooks.HudBeat("heal", 1f);
            yield return view.Motion.Swell(0.55f);
            Rig.Release();
        }

        // --- Status, stats, abilities, weather -----------------------------------------------

        private IEnumerator PlayStatusChanged(StatusChangedEvent e)
        {
            CreatureView view = Stage.ViewOf(e.Target);
            Rig.SetRoles(e.Target, BattleStage.Opposite(e.Target));
            Rig.Show(BattleCameraRig.FocusOn(e.Target));

            bool cured = e.Current == StatusCondition.None;
            CinematicHooks.HudBeat(cured ? "status_cured" : "status_" + e.Current.ToString().ToLowerInvariant(), 1f);
            CinematicHooks.Vfx(cured ? CinematicVfxKeys.StatUp : CinematicVfxKeys.StatDown,
                view.transform.position, Quaternion.identity);

            if (cured)
            {
                yield return view.Motion.Swell(timing.StatusOnset);
            }
            else if (e.Current == StatusCondition.Sleep)
            {
                // Sleep is the one status with its own clip; play it and let it hold.
                view.PlayAuthored(CreatureAnimation.Sleep);
                yield return view.Motion.Flinch(0.25f, timing.StatusOnset);
            }
            else
            {
                yield return view.Motion.Flinch(0.7f, timing.StatusOnset);
                view.PlayAuthored(CreatureAnimation.IdleBattle);
            }

            Rig.Release();
        }

        private IEnumerator PlayVolatileChanged(VolatileChangedEvent e)
        {
            CreatureView view = Stage.ViewOf(e.Target);
            if (e.Added != VolatileFlags.None)
            {
                CinematicHooks.HudBeat("volatile_" + e.Added.ToString().ToLowerInvariant(), 1f);
                yield return view.Motion.Flinch(0.35f, 0.3f);
            }
        }

        private IEnumerator PlayStatStage(StatStageChangedEvent e)
        {
            CreatureView view = Stage.ViewOf(e.Target);
            bool up = e.Delta > 0;

            Rig.SetRoles(e.Target, BattleStage.Opposite(e.Target));
            Rig.Show(BattleCameraRig.FocusOn(e.Target));

            CinematicHooks.Vfx(up ? CinematicVfxKeys.StatUp : CinematicVfxKeys.StatDown,
                view.transform.position, Quaternion.identity);
            CinematicHooks.HudBeat(up ? "stat_up" : "stat_down", Mathf.Abs(e.Delta));

            // A two-stage change is visibly bigger than a one-stage change.
            float weight = Mathf.Clamp01(Mathf.Abs(e.Delta) / 2f);
            yield return up
                ? view.Motion.Swell(timing.StatStageBeat * (0.8f + weight * 0.4f))
                : view.Motion.Flinch(0.35f + weight * 0.35f, timing.StatStageBeat);

            Rig.Release();
        }

        private IEnumerator PlayAbility(AbilityTriggeredEvent e)
        {
            CreatureView view = Stage.ViewOf(e.Side);
            Rig.SetRoles(e.Side, BattleStage.Opposite(e.Side));
            Rig.Show(BattleCameraRig.FocusOn(e.Side));

            CinematicHooks.Vfx(CinematicVfxKeys.AbilityFlare, view.transform.position, Quaternion.identity);
            CinematicHooks.HudBeat("ability", 1f);
            CinematicHooks.Audio(CinematicAudioCues.Whoosh, view.transform.position);

            yield return view.Motion.Swell(timing.AbilityFlare);
            Rig.Release();
        }

        private IEnumerator PlayItemUsed(ItemUsedEvent e)
        {
            Rig.SetRoles(e.Side, BattleStage.Opposite(e.Side));
            Rig.Show(BattleCameraRig.FocusOn(e.Side));
            CinematicHooks.HudBeat("item", 1f);
            yield return CinematicRunner.Wait(0.4f);
            Rig.Release();
        }

        private IEnumerator PlayWeather(WeatherChangedEvent e)
        {
            // Weather is a change to the whole field, so it is staged on the field, not on a
            // combatant. The field shot is the widest the rig has and the only one framed on
            // the arena rather than on somebody in it.
            Rig.Show(BattleShot.Field);
            CinematicHooks.HudBeat("weather_" + e.Current.ToString().ToLowerInvariant(), 1f);
            CinematicHooks.Vfx("weather_" + e.Current.ToString().ToLowerInvariant(), Stage.Center.position, Quaternion.identity);
            yield return CinematicRunner.Wait(timing.WeatherTurn);
            Rig.Release();
        }

        // --- Faint ------------------------------------------------------------------------------

        private IEnumerator PlayFainted(CreatureFaintedEvent e)
        {
            CreatureView view = Stage.ViewOf(e.Side);
            Rig.SetRoles(BattleStage.Opposite(e.Side), e.Side);
            Rig.Show(BattleShot.Faint);

            view.Play(CreatureAnimation.Faint, CreatureView.DefaultCrossfade(CreatureAnimation.Faint));
            CinematicHooks.Audio(CinematicAudioCues.Faint, view.transform.position);
            CinematicHooks.HudBeat("faint", 1f);

            yield return view.Motion.Collapse(timing.FaintCollapse);

            CinematicHooks.Vfx(CinematicVfxKeys.FaintCollapse, view.transform.position, Quaternion.identity);
            Rig.Shake?.Bump(view.transform.position, Mathf.Clamp(view.DisplayHeight * 0.5f, 0.15f, 0.8f));

            // The held beat. Nothing moves, nothing cuts. Shortening this is the difference
            // between a knockout and a despawn.
            yield return CinematicRunner.Wait(timing.FaintHold);

            var ball = BallActor.Spawn(ballPrefab, Stage.TrainerMarkOf(e.Side).position + Vector3.up * 1.2f);
            yield return ball.Recall(view, timing.FaintRecall);
            ball.Dismiss(0.6f);

            view.Motion.ResetLayer();
            Rig.Release();
        }

        // --- Capture ------------------------------------------------------------------------------

        /// <summary>
        /// The capture set piece.
        ///
        /// Structured as throw, absorb, fall, settle, then exactly
        /// <see cref="CaptureAttemptEvent.Shakes"/> shakes with rising tension, then either
        /// the click or the break-out. The shake count is taken literally from the event —
        /// the player counts them, and a presenter that plays four when the engine said two
        /// is lying about the outcome.
        /// </summary>
        private IEnumerator PlayCapture(CaptureAttemptEvent e)
        {
            // CORE GAP: CaptureAttemptEvent carries no BattleSide, so the target is assumed to
            // be the opponent. That is correct for every capture the vertical slice contains
            // (the player throws, the wild creature is caught) and wrong the moment anything
            // can be captured on the player's side. Reported to the integrator; adding a
            // Side field to the event is the fix, not inferring it here.
            BattleSide targetSide = BattleSide.Opponent;
            CreatureView target = Stage.ViewOf(targetSide);

            Rig.SetRoles(BattleSide.Player, targetSide);
            Rig.Show(BattleShot.Capture);

            Transform trainer = Stage.TrainerMarkOf(BattleSide.Player);
            Vector3 groundPoint = Stage.MarkOf(targetSide).position;

            // The player steps back into frame for the throw and leaves once the ball is down.
            // Always the player's side: a capture is the one throw that is never the opponent's.
            TrainerView thrower = Stage.TrainerViewOf(BattleSide.Player);
            yield return thrower.Enter(timing.TrainerEnter);

            yield return CinematicRunner.Wait(timing.CaptureThrowWindUp);

            var ball = BallActor.Spawn(ballPrefab, trainer.position + Vector3.up * 1.3f);
            yield return ball.Throw(target.BodyAnchor != null ? target.BodyAnchor.position : groundPoint + Vector3.up,
                timing.CaptureFlight, 1.7f);

            yield return ball.Open(0.16f);
            yield return ball.Absorb(target, timing.CaptureAbsorb);
            yield return ball.FallAndSettle(groundPoint, timing.CaptureFall);

            Rig.Shake?.Bump(ball.Position, 0.14f);

            // The trainer goes before the shakes, not after. The shakes are the tense beat of the
            // whole battle and the frame has to be the ball and nothing else.
            yield return thrower.Exit(timing.TrainerExit);

            // The loaded silence before the first shake.
            yield return CinematicRunner.Wait(timing.CaptureSettleHold);

            int shakes = Mathf.Max(0, e.Shakes);
            for (int i = 0; i < shakes; i++)
            {
                // Tension climbs across the sequence, so shake four is visibly a harder fight
                // than shake one even though it is the same routine.
                float tension = shakes <= 1 ? 0.5f : i / (float)(shakes - 1);
                yield return ball.Shake(tension);
                CinematicHooks.HudBeat("capture_shake", i + 1);
            }

            if (e.Succeeded)
            {
                yield return ball.Click(timing.CaptureClickHold);
                CinematicHooks.HudBeat("capture_success", 1f);
                target.SetModelVisible(false);
                yield return CinematicRunner.Wait(timing.CaptureCelebrate);
                ball.Dismiss(2f);
            }
            else
            {
                yield return ball.BurstOut();
                ball.Dismiss(0.8f);

                // The creature comes back out, and it comes back out angry: visible, upright,
                // facing the player. A break-out that just re-enables the renderer wastes the
                // tension the shakes just built.
                target.SetModelVisible(true);
                target.Motion.RestoreScale();
                target.PlayAuthored(CreatureAnimation.SentOut);
                CinematicHooks.Vfx(CinematicVfxKeys.BallBurst, target.transform.position, Quaternion.identity);
                CinematicHooks.Cry(_active.TryGetValue(targetSide, out var c) ? c.SpeciesId : 0, target.transform.position);

                yield return target.PlayEntrance(target.DisplayHeight * 1.2f, 0.22f, 0.36f);
                yield return target.FaceTowardsAndWait(Stage.MarkOf(BattleSide.Player).position, 0.3f);
                target.PlayAuthored(CreatureAnimation.IdleBattle);
                CinematicHooks.HudBeat("capture_failed", 1f);
            }

            Rig.Release();
        }

        // --- Outro ------------------------------------------------------------------------------

        private IEnumerator PlayExperience(ExperienceGainedEvent e)
        {
            CinematicHooks.HudBeat("experience", e.Amount);
            yield return CinematicRunner.Wait(timing.ExperienceBeat);

            if (!e.LeveledUp) yield break;

            CreatureView view = Stage.ViewOf(BattleSide.Player);
            Rig.SetRoles(BattleSide.Player, BattleSide.Opponent);
            Rig.Show(BattleShot.Victory);

            CinematicHooks.HudBeat("levelup", e.NewLevel);
            CinematicHooks.Vfx(CinematicVfxKeys.StatUp, view.transform.position, Quaternion.identity);

            yield return view.Motion.Swell(timing.LevelUpBeat);
            Rig.Release();
        }

        private IEnumerator PlayBattleEnded(BattleEndedEvent e)
        {
            _outcome = e.Outcome;

            if (e.Outcome == BattleOutcome.PlayerVictory || e.Outcome == BattleOutcome.Captured)
            {
                CreatureView winner = Stage.ViewOf(BattleSide.Player);
                Rig.SetRoles(BattleSide.Player, BattleSide.Opponent);
                Rig.SetRestingShot(BattleShot.Victory);
                Rig.Show(BattleShot.Victory);

                // Turn out toward the camera before celebrating — a creature celebrating at
                // the empty space where its opponent used to be reads as a bug.
                // Turn out toward the lens, which for a sprite is a real reveal: the player's
                // creature has shown its back sprite for the whole battle and this is the one
                // beat that turns it around. Aimed along the camera's own view direction rather
                // than at an arbitrary offset, so the front view lands square.
                // Releases the pinned back view for this one beat, so the turn actually shows the
                // front sprite. Pinned, the creature would rotate and go on showing its back,
                // which reads as it turning away from its own victory.
                winner.ForceFacing(null);
                Vector3 outward = winner.transform.position - Rig.ViewDirection * 3f;
                yield return winner.FaceTowardsAndWait(outward, 0.5f);

                winner.PlayAuthored(CreatureAnimation.Celebrate);
                CinematicHooks.Audio(CinematicAudioCues.Victory, winner.transform.position);
                CinematicHooks.Vfx(CinematicVfxKeys.CelebrateSparkle, winner.transform.position, Quaternion.identity);
                CinematicHooks.HudBeat("victory", 1f);

                yield return winner.Motion.Celebrate(3, 0.4f);
                yield return CinematicRunner.Wait(timing.VictoryPose);

                // Return to the trainer: walk back, then face them. The battle is over, so the
                // creature stops being a combatant before the camera leaves.
                yield return ReturnToTrainer(winner, BattleSide.Player);
            }
            else
            {
                Rig.SetRestingShot(BattleShot.Field);
                Rig.Show(BattleShot.Field);
                CinematicHooks.HudBeat(e.Outcome == BattleOutcome.Fled ? "fled" : "defeat", 1f);
                yield return CinematicRunner.Wait(timing.BattleEndHold);
            }

            CinematicHooks.HudVisible(false, 0.45f);
        }

        /// <summary>
        /// Walks a creature back to its trainer's mark and turns it to face them.
        /// Called out explicitly in the brief, and it is also the beat that makes the
        /// hand-off into the overworld transition feel like a consequence rather than a cut.
        /// </summary>
        private IEnumerator ReturnToTrainer(CreatureView view, BattleSide side)
        {
            Transform trainer = Stage.TrainerMarkOf(side);
            Vector3 destination = trainer.position + (view.transform.position - trainer.position).normalized
                                  * Mathf.Max(0.9f, view.DisplayHeight * 1.2f);

            yield return view.FaceTowardsAndWait(destination, 0.3f);
            view.PlayAuthored(CreatureAnimation.Walk);

            Vector3 from = view.transform.position;
            float distance = Vector3.Distance(from, destination);
            float duration = Mathf.Clamp(distance / 2.2f, 0.4f, 1.6f);

            yield return CinematicRunner.Tween(duration, CinematicEase.InOutCubic,
                p => view.transform.position = Vector3.Lerp(from, destination, p));

            yield return view.FaceTowardsAndWait(trainer.position, 0.3f);
            view.PlayAuthored(CreatureAnimation.Idle);
        }

        private IEnumerator PlayMessage(MessageEvent e)
        {
            // The UI worker renders the text. The camera holds its shot rather than moving,
            // because a camera move under a log line makes the line hard to read.
            CinematicHooks.HudBeat("message", 1f);
            yield break;
        }

        /// <summary>
        /// Rescales the arena to the two creatures currently standing in it.
        ///
        /// Only ever called from inside a send-out beat. Moving the marks slides whatever is
        /// standing on them, which is invisible while a creature is still in its ball and
        /// jarring at any other moment.
        /// </summary>
        private void RefitStage()
        {
            Stage.AdaptToCreatureSizes(
                Stage.ViewOf(BattleSide.Player).DisplayHeight,
                Stage.ViewOf(BattleSide.Opponent).DisplayHeight);
        }

        // --- State queries ------------------------------------------------------------------------

        /// <summary>The creature currently staged on a side, or null.</summary>
        public CreatureInstance ActiveOn(BattleSide side)
            => _active.TryGetValue(side, out var c) ? c : null;

        /// <summary>The outcome reported by the last <see cref="BattleEndedEvent"/>.</summary>
        public BattleOutcome Outcome => _outcome;

        /// <summary>Resets per-battle state. Call before staging a new encounter.</summary>
        public void ResetForNewBattle()
        {
            FlushQueue();
            _active.Clear();
            _declaredMove = null;
            _outcome = BattleOutcome.InProgress;
            // A flushed queue can strand a trainer mid-exit, and the arena is reused: the next
            // battle would open on somebody standing off to one side of the field.
            Stage.HideTrainers();
            Rig.SetRestingShot(BattleShot.Field);
            Rig.Release();
        }
    }
}
