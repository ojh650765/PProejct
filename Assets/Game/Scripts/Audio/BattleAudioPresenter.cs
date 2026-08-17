using System;
using System.Collections;
using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Audio
{
    /// <summary>
    /// Scores the battle event stream.
    ///
    /// Contract notes that shape this class:
    ///   * events are advisory, so nothing here may block, throw, or assume it saw the
    ///     previous event -- every handler is independently survivable;
    ///   * <c>SuggestedDuration</c> is a floor, so the capture sequence below lays its
    ///     beats out over roughly that long and is allowed to run past it;
    ///   * state has already changed inside the engine, so this class reads
    ///     <see cref="CreatureInstance"/> and never writes to it.
    ///
    /// Three properties of the live engine that this class is explicitly built around:
    ///   * <b>Intro events are stamped Turn = 0 and arrive inside turn 1's stream.</b>
    ///     <c>IBattleEngine.Begin</c> returns void, so battle-start, both send-outs and
    ///     entry abilities are buffered and drained into the first stream. Nothing here
    ///     reads <c>BattleEvent.Turn</c> or assumes a stream opens with a move; each
    ///     handler stands alone, so the send-out bursts and the battle-theme hand-off fire
    ///     correctly wherever in the stream they land.
    ///   * <b><c>CaptureAttemptEvent.Shakes</c> is a real computed number</b> from the catch
    ///     formula, so <see cref="CaptureRoutine"/> is bounded by exactly that count.
    ///   * <b>A faint is not immediately followed by a send-out.</b> With an
    ///     <c>IReplacementChooser</c> registered the player picks from a menu in between, and
    ///     that gap is unbounded. No cue here is scheduled relative to another event's
    ///     arrival; the only timed sequence is the capture, which is self-contained.
    ///
    /// The integrator adds this to the battle presenter's listener list. It also registers
    /// itself on <see cref="ServiceHub"/> under its concrete type -- not under
    /// <see cref="IBattleEventListener"/>, because VFX, UI and cinematics all implement
    /// that interface too and the hub holds one instance per type.
    /// </summary>
    [AddComponentMenu("Poke Lab/Audio/Battle Audio Presenter")]
    public sealed class BattleAudioPresenter : MonoBehaviour, IBattleEventListener
    {
        [Header("Health bar")]
        [Tooltip("Pitch of the drain tick at full health and at zero. The rise as HP falls " +
                 "is the whole point of the cue, so these must stay apart.")]
        [SerializeField] private float hpTickPitchFull = 0.88f;
        [SerializeField] private float hpTickPitchEmpty = 1.85f;
        [SerializeField, Range(1, 24)] private int maxHpTicks = 14;
        [SerializeField, Range(0.01f, 0.15f)] private float hpTickInterval = 0.045f;
        [SerializeField, Range(0f, 0.5f)] private float lowHpThreshold = 0.2f;

        [Header("Capture timing (seconds from the start of the attempt)")]
        [SerializeField] private float captureThrowAt = 0f;
        [SerializeField] private float captureBeamAt = 0.35f;
        [SerializeField] private float captureLandAt = 1.05f;
        [SerializeField] private float captureFirstShakeAt = 1.45f;
        [SerializeField] private float captureShakeSpacing = 0.62f;
        [SerializeField] private float captureResolveDelay = 0.55f;

        [Header("Ducking")]
        [SerializeField, Range(0f, 1f)] private float fanfareDuck = 0.75f;
        [SerializeField, Range(0f, 1f)] private float dialogueDuck = 0.35f;
        [SerializeField] private float dialogueDuckHold = 0.9f;

        private AudioDirector _audio;
        private MusicDirector _music;

        private ElementType _lastMoveType = ElementType.Normal;
        private bool _lastMoveMadeContact;
        private AudioSource _lowHpLoop;
        private AudioSource _expLoop;
        private int _shakeVariant;
        private Coroutine _capture;
        private readonly Dictionary<BattleSide, float> _lastHpFraction =
            new Dictionary<BattleSide, float>();

        // ------------------------------------------------------------------------------

        private static BattleAudioPresenter _instance;

        private void Awake()
        {
            // First one wins, and any later arrival removes itself. Every playable scene
            // carries its own GameHosts, so an additively streamed band brings a second
            // presenter whose Awake runs during the load; it took the hub slot and was then
            // stripped with the rest of the duplicate hosts, leaving the battle presenter
            // pushing its event stream at a destroyed listener.
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;
            ServiceHub.Register(this);
        }

        private void OnDisable()
        {
            StopLowHpWarning();
            StopExpLoop();
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            _instance = null;
            ServiceHub.Unregister(this);
        }

        private bool Ready()
        {
            if (_audio == null) AudioDirector.TryResolve(out _audio);
            if (_music == null) ServiceHub.TryGet(out _music);
            return _audio != null;
        }

        /// <summary>Single entry point. Never throws: a bad event must not stall the battle.</summary>
        public void OnBattleEvent(BattleEvent evt)
        {
            if (evt == null || !Ready()) return;
            try
            {
                Dispatch(evt);
            }
            catch (Exception e)
            {
                Debug.LogError($"[BattleAudioPresenter] {evt.GetType().Name} handler failed: {e}", this);
            }
        }

        private void Dispatch(BattleEvent evt)
        {
            switch (evt)
            {
                case BattleStartedEvent started: OnBattleStarted(started); break;
                case CreatureSentOutEvent sent: OnSentOut(sent); break;
                case CreatureWithdrawnEvent withdrawn: OnWithdrawn(withdrawn); break;
                case MoveExecutedEvent move: OnMoveExecuted(move); break;
                case MoveMissedEvent missed: OnMoveMissed(missed); break;
                case DamageDealtEvent damage: OnDamage(damage); break;
                case HealedEvent healed: OnHealed(healed); break;
                case StatusChangedEvent status: OnStatusChanged(status); break;
                case StatStageChangedEvent stat: OnStatStage(stat); break;
                case CreatureFaintedEvent fainted: OnFainted(fainted); break;
                case CaptureAttemptEvent capture: OnCaptureAttempt(capture); break;
                case ExperienceGainedEvent xp: OnExperience(xp); break;
                case BattleEndedEvent ended: OnBattleEnded(ended); break;
                case ItemUsedEvent _: _audio.PlaySfx(AudioIds.UiConfirm, 0.8f); break;
                case AbilityTriggeredEvent _: _audio.PlaySfx(AudioIds.BattleStatUp, 0.55f, 1.15f); break;
                case WeatherChangedEvent weather: OnWeatherChanged(weather); break;
                case MessageEvent message: OnMessage(message); break;
            }
        }

        // ---- battle lifecycle --------------------------------------------------------

        private void OnBattleStarted(BattleStartedEvent evt)
        {
            _lastHpFraction.Clear();
            _shakeVariant = 0;
            StopLowHpWarning();
            // The music director owns the transition; it only needs to know which of the
            // two battle themes to blend to, which the mode change alone cannot tell it.
            if (_music != null) _music.SetBattleKind(evt.Kind);
        }

        private void OnSentOut(CreatureSentOutEvent evt)
        {
            // A replacement after a faint arrives once the player has picked from a menu,
            // so it is a quieter, less ceremonial beat than opening the battle.
            float volume = evt.Side == BattleSide.Player ? 1f : 0.85f;
            if (evt.IsReplacement) volume *= 0.88f;
            _audio.PlaySfx(AudioIds.BattleSendOut, volume, evt.IsReplacement ? 1.04f : 1f);

            // Seed the health tracker from the incoming creature so the first hit it takes
            // drains from the right level even though this side's previous entry fainted.
            if (evt.Creature != null) _lastHpFraction[evt.Side] = evt.Creature.HpFraction;
            if (evt.Side == BattleSide.Player) EvaluateLowHp(evt.Creature);
        }

        private void OnWithdrawn(CreatureWithdrawnEvent evt)
        {
            _audio.PlaySfx(AudioIds.BattleRecall, 0.9f);
            if (evt.Side == BattleSide.Player) StopLowHpWarning();
        }

        private void OnBattleEnded(BattleEndedEvent evt)
        {
            StopLowHpWarning();
            StopExpLoop();

            if (evt.Outcome != BattleOutcome.PlayerVictory) return;

            // The fanfare plays on the music director's sting deck, which is not ducked --
            // only the looping theme underneath it dips, which is the effect asked for.
            _audio.DuckMusic(fanfareDuck, 0.25f, 2.4f, 1.2f);
            if (_music != null) _music.PlaySting(AudioIds.MusicVictoryFanfare);
            else _audio.PlaySfx(AudioIds.MusicVictoryFanfare);
        }

        private void OnMessage(MessageEvent evt)
        {
            // Battle dialogue: a light, short dip so the line reads over the theme.
            if (!string.IsNullOrEmpty(evt.Text))
                _audio.DuckMusic(dialogueDuck, 0.15f, dialogueDuckHold, 0.5f);
        }

        /// <summary>
        /// In-battle weather changes get an onset cue borrowed from the matching element,
        /// which keeps the battlefield's weather in the same sonic vocabulary as the moves
        /// that cause it. The persistent bed is the ambience director's job, not this one's.
        /// </summary>
        private void OnWeatherChanged(WeatherChangedEvent evt)
        {
            switch (evt.Current)
            {
                case Weather.Rain:
                    _audio.PlaySfx(AudioIds.MoveCast(ElementType.Water), 0.7f, 0.85f);
                    break;
                case Weather.Sandstorm:
                    _audio.PlaySfx(AudioIds.MoveCast(ElementType.Ground), 0.7f, 0.9f);
                    break;
                case Weather.Hail:
                    _audio.PlaySfx(AudioIds.StatusFreeze, 0.6f);
                    break;
                case Weather.Sun:
                    _audio.PlaySfx(AudioIds.MoveCast(ElementType.Fire), 0.55f, 0.8f);
                    break;
                case Weather.Fog:
                    _audio.PlaySfx(AudioIds.MoveCast(ElementType.Ghost), 0.5f, 0.9f);
                    break;
            }
        }

        // ---- moves -------------------------------------------------------------------

        private void OnMoveExecuted(MoveExecutedEvent evt)
        {
            _lastMoveType = evt.MoveType;
            _lastMoveMadeContact = evt.MakesContact;

            // Multi-hit sequences taper and detune slightly so five hits do not machine-gun.
            float volume = evt.HitCount > 1 ? Mathf.Lerp(1f, 0.72f, evt.HitIndex / (float)Mathf.Max(1, evt.HitCount - 1)) : 1f;
            float pitch = evt.HitCount > 1 ? 1f + 0.03f * evt.HitIndex : 1f;

            _audio.PlaySfx(AudioIds.MoveCast(evt.MoveType), volume, pitch);

            // Status moves get the stat-shift gesture as well: without it a status turn is
            // the only turn in a battle with no sound at all.
            if (evt.Category == MoveCategory.Status)
                _audio.PlaySfx(AudioIds.BattleStatUp, 0.35f, 0.95f);
        }

        private void OnMoveMissed(MoveMissedEvent evt)
        {
            if (evt.WasImmune)
                _audio.PlaySfx(AudioIds.MoveNotVeryEffective, 0.85f, 0.85f);
            else
                // a whiff of air: the normal cast whoosh, pitched up and quiet
                _audio.PlaySfx(AudioIds.MoveCast(ElementType.Normal), 0.6f, 1.25f);
        }

        private void OnDamage(DamageDealtEvent evt)
        {
            // Indirect damage (burn tick, weather, recoil) must not reuse the last move's
            // element -- it is not that move landing.
            bool indirect = !string.IsNullOrEmpty(evt.IndirectSourceId);

            if (evt.Effectiveness == Effectiveness.Immune)
            {
                _audio.PlaySfx(AudioIds.MoveNotVeryEffective, 0.8f, 0.8f);
                return;
            }

            if (indirect)
            {
                _audio.PlaySfx(AudioIds.MoveImpact(ElementType.Normal), 0.6f, 1.1f);
            }
            else
            {
                // Contact moves land heavier than projectiles: same cue, more of it.
                float volume = (evt.Critical ? 1f : 0.92f) * (_lastMoveMadeContact ? 1f : 0.9f);
                _audio.PlaySfx(AudioIds.MoveImpact(_lastMoveType), volume);
            }

            switch (evt.Effectiveness)
            {
                case Effectiveness.SuperEffective:
                    _audio.PlaySfx(AudioIds.MoveSuperEffective, 0.95f);
                    break;
                case Effectiveness.NotVeryEffective:
                    // layered *under* the impact, which is why it is quiet and dull
                    _audio.PlaySfx(AudioIds.MoveNotVeryEffective, 0.85f);
                    break;
            }

            // Crit rides on top of whatever landed rather than replacing it.
            if (evt.Critical) _audio.PlaySfx(AudioIds.MoveCritical, 1f);

            StartCoroutine(DrainHealthBar(evt));
        }

        private void OnHealed(HealedEvent evt)
        {
            _audio.PlaySfx(AudioIds.OverworldHeal, 0.75f);
            if (evt.MaxHp > 0)
            {
                _lastHpFraction[evt.Target] = evt.RemainingHp / (float)evt.MaxHp;
                if (evt.Target == BattleSide.Player &&
                    _lastHpFraction[evt.Target] > lowHpThreshold) StopLowHpWarning();
            }
        }

        /// <summary>
        /// The drain. One tick per bar step, pitch interpolating from the health the
        /// creature had to the health it has -- so a big hit sweeps upward through the
        /// ticks and a scratch is two flat ones.
        /// </summary>
        private IEnumerator DrainHealthBar(DamageDealtEvent evt)
        {
            if (evt.MaxHp <= 0) yield break;

            float after = Mathf.Clamp01(evt.RemainingHp / (float)evt.MaxHp);
            float before = _lastHpFraction.TryGetValue(evt.Target, out var prev)
                ? Mathf.Max(prev, after)
                : Mathf.Clamp01(after + evt.Amount / (float)evt.MaxHp);
            _lastHpFraction[evt.Target] = after;

            float drop = Mathf.Max(0f, before - after);
            int ticks = Mathf.Clamp(Mathf.RoundToInt(drop * maxHpTicks), 1, maxHpTicks);

            for (int i = 0; i < ticks; i++)
            {
                float f = ticks == 1 ? after : Mathf.Lerp(before, after, (i + 1) / (float)ticks);
                _audio.PlaySfx(AudioIds.BattleHpTick, 0.7f, HpTickPitch(f));
                yield return new WaitForSeconds(hpTickInterval);
            }

            if (evt.Target == BattleSide.Player)
            {
                if (after > 0f && after <= lowHpThreshold) StartLowHpWarning();
                else StopLowHpWarning();
            }
        }

        /// <summary>Health fraction to playback pitch. 1 = full health, 0 = empty.</summary>
        public float HpTickPitch(float hpFraction) =>
            Mathf.Lerp(hpTickPitchEmpty, hpTickPitchFull, Mathf.Clamp01(hpFraction));

        private void EvaluateLowHp(CreatureInstance creature)
        {
            if (creature == null) return;
            if (!creature.IsFainted && creature.HpFraction <= lowHpThreshold) StartLowHpWarning();
            else StopLowHpWarning();
        }

        private void StartLowHpWarning()
        {
            if (_lowHpLoop != null) return;
            _lowHpLoop = _audio.PlayLoop(AudioIds.BattleLowHpWarning, AudioBus.Sfx, 0.55f);
        }

        private void StopLowHpWarning()
        {
            if (_lowHpLoop == null || _audio == null) return;
            _audio.StopLoop(_lowHpLoop, 0.15f);
            _lowHpLoop = null;
        }

        // ---- status and stats --------------------------------------------------------

        private void OnStatusChanged(StatusChangedEvent evt)
        {
            string cue = CueForStatus(evt.Current);
            if (cue != null) _audio.PlaySfx(cue);
        }

        public static string CueForStatus(StatusCondition status)
        {
            switch (status)
            {
                case StatusCondition.Burn: return AudioIds.StatusBurn;
                case StatusCondition.Freeze: return AudioIds.StatusFreeze;
                case StatusCondition.Paralysis: return AudioIds.StatusParalysis;
                case StatusCondition.Poison:
                case StatusCondition.BadPoison: return AudioIds.StatusPoison;
                case StatusCondition.Sleep: return AudioIds.StatusSleep;
                default: return null;   // None and Fainted are covered elsewhere
            }
        }

        private void OnStatStage(StatStageChangedEvent evt)
        {
            if (evt.Delta == 0) return;
            // A two-stage change is pitched up a whole tone so "sharply rose" is audible.
            float pitch = 1f + 0.06f * (Mathf.Abs(evt.Delta) - 1) * Mathf.Sign(evt.Delta);
            _audio.PlaySfx(evt.Delta > 0 ? AudioIds.BattleStatUp : AudioIds.BattleStatDown,
                           0.9f, pitch);
        }

        private void OnFainted(CreatureFaintedEvent evt)
        {
            if (evt.Side == BattleSide.Player) StopLowHpWarning();
            _audio.PlaySfx(AudioIds.BattleFaint, evt.Side == BattleSide.Player ? 1f : 0.9f);
            _lastHpFraction[evt.Side] = 0f;
        }

        // ---- experience --------------------------------------------------------------

        private void OnExperience(ExperienceGainedEvent evt)
        {
            StartCoroutine(ExperienceRoutine(evt));
        }

        private IEnumerator ExperienceRoutine(ExperienceGainedEvent evt)
        {
            float fill = Mathf.Clamp(evt.SuggestedDuration * 0.6f, 0.3f, 2.5f);
            StopExpLoop();
            _expLoop = _audio.PlayLoop(AudioIds.BattleExpGain, AudioBus.Sfx, 0.6f);
            yield return new WaitForSeconds(fill);
            StopExpLoop();
            if (evt.LeveledUp) _audio.PlaySfx(AudioIds.BattleLevelUp);
        }

        private void StopExpLoop()
        {
            if (_expLoop == null || _audio == null) return;
            _audio.StopLoop(_expLoop, 0.08f);
            _expLoop = null;
        }

        // ---- capture -----------------------------------------------------------------

        private void OnCaptureAttempt(CaptureAttemptEvent evt)
        {
            if (_capture != null) StopCoroutine(_capture);
            _capture = StartCoroutine(CaptureRoutine(evt));
        }

        /// <summary>
        /// The set piece.
        ///
        /// Throw, beam, land, then <c>evt.Shakes</c> ticks and exactly that many -- the
        /// loop is bounded by the event's own count, so three shakes is three ticks and
        /// four is four, and a dropped or repeated event cannot desynchronise the count
        /// because nothing here accumulates state between attempts. Tick variants rotate
        /// and each is nudged very slightly in pitch so a run of four does not machine-gun.
        /// </summary>
        private IEnumerator CaptureRoutine(CaptureAttemptEvent evt)
        {
            StopLowHpWarning();

            // Absolute clock rather than a running sum: every beat is scheduled from the
            // start of the attempt, so a frame spike cannot let the shakes drift apart.
            float start = Time.time;

            yield return WaitTo(start, captureThrowAt);
            _audio.PlaySfx(AudioIds.CaptureThrow);

            yield return WaitTo(start, captureBeamAt);
            _audio.PlaySfx(AudioIds.CaptureAbsorbBeam, 0.9f);

            yield return WaitTo(start, captureLandAt);
            _audio.PlaySfx(AudioIds.CaptureBallLand, 0.95f);

            int shakes = Mathf.Max(0, evt.Shakes);
            for (int i = 0; i < shakes; i++)
            {
                yield return WaitTo(start, captureFirstShakeAt + i * captureShakeSpacing);

                string tick = AudioIds.CaptureShakeTicks[_shakeVariant % AudioIds.CaptureShakeTicks.Length];
                _shakeVariant++;

                // Tension: each successive shake is a touch louder and a touch higher.
                float volume = Mathf.Lerp(0.85f, 1f, shakes <= 1 ? 1f : i / (float)(shakes - 1));
                float pitch = 1f + 0.018f * i;
                _audio.PlaySfx(tick, volume, pitch);
            }

            float resolveAt = captureFirstShakeAt + Mathf.Max(0, shakes - 1) * captureShakeSpacing
                              + captureResolveDelay;
            yield return WaitTo(start, resolveAt);

            if (evt.Succeeded)
            {
                _audio.PlaySfx(AudioIds.CaptureSuccessClick);
                // The click was tuned to a G major triad specifically so this sting, which
                // resolves to G, reads as the same gesture continuing rather than a new one.
                yield return new WaitForSeconds(0.14f);
                _audio.DuckMusic(fanfareDuck, 0.2f, 1.8f, 1.0f);
                if (_music != null) _music.PlaySting(AudioIds.MusicCaptureSuccess);
                else _audio.PlaySfx(AudioIds.MusicCaptureSuccess);
            }
            else
            {
                _audio.PlaySfx(AudioIds.CaptureBreakOut);
            }

            _capture = null;
        }

        /// <summary>Waits until <paramref name="offset"/> seconds after <paramref name="start"/>.</summary>
        private static IEnumerator WaitTo(float start, float offset)
        {
            float wait = start + offset - Time.time;
            if (wait > 0f) yield return new WaitForSeconds(wait);
        }
    }
}
