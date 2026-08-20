using System;
using UnityEngine;
using UnityEngine.UI;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The battle screen: both name-plates, the docked Poké Lab scanner, the command panel
    /// and the log, assembled and driven from the event stream.
    ///
    /// It is a presenter, never an authority. Every event is treated as advisory exactly as
    /// the contract requires — dropping one leaves the HUD stale for a frame and correct
    /// again on the next rebind, and nothing here ever mutates a <see cref="CreatureInstance"/>.
    /// Creature references are cached from send-out events so the plates keep working when
    /// the battle engine has not been registered yet, which is the normal state until
    /// integration.
    ///
    /// Turn gating is explicit: the HUD does not guess when the player may act. Whoever runs
    /// the battle calls <see cref="BeginPlayerTurn"/> and <see cref="LockCommands"/>, which
    /// keeps the command panel in step with the presentation queue rather than with the
    /// engine's internal state.
    ///
    /// It is also the UI side of <see cref="ICinematicHudHook"/>. The choreography cannot
    /// name this assembly, so its frame-accurate HUD beats — a crit landing, the ball
    /// clicking — arrive through that Core interface, and this view is the natural owner
    /// because it already holds the plates and the fade group the beats act on. Every beat
    /// is cheap and non-blocking, and an unrecognised beat id is ignored silently: the
    /// choreography grows vocabulary faster than the HUD does, and a warning per unknown
    /// beat would turn every new set piece into log spam on this side of the seam.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleHudView : MonoBehaviour, IBattleEventListener, IPokeLabListener, ICinematicHudHook
    {
        [Header("Parts")]
        [SerializeField] private CreatureStatusPanel _playerPlate;
        [SerializeField] private CreatureStatusPanel _opponentPlate;
        [SerializeField] private PokeLabScannerView _scanner;
        [SerializeField] private BattleCommandPanel _commands;
        [SerializeField] private BattlePartyPicker _partyPicker;
        [SerializeField] private BattleLogView _log;
        [SerializeField] private OverlayDirector _overlays;
        [SerializeField] private Image _beatFlash;
        [SerializeField] private CanvasGroup _group;

        [Header("Layout")]
        [Tooltip("Clear space between the command column and anything to its left.")]
        [SerializeField] private float _columnGutter = 40f;
        [SerializeField] private bool _buildOnAwake = true;

        private CreatureInstance _playerActive;
        private CreatureInstance _opponentActive;
        private bool _built;
        private TweenHandle _visibilityFade;
        private TweenHandle _beatFlashTween;

        // Which plate the last blow landed on. The choreography's emphasis beats — "critical",
        // "supereffective" — name the beat but not the side, and they arrive on the same frame
        // as the damage event that set this, so it is always the plate they mean.
        private BattleSide _lastHitSide = BattleSide.Opponent;

        // What kind of battle is running. Set from the opening event, and used for the one
        // line whose wording depends on it: whether the creature opposite walked out of the
        // grass or was sent out by somebody.
        private BattleKind _battleKind = BattleKind.Wild;

        /// <summary>Raised when the player commits a move, with its slot index.</summary>
        public Action<int> MoveChosen;
        /// <summary>Raised when the player asks to switch, with a party index.</summary>
        public Action<int> SwitchRequested;
        /// <summary>Raised when the player opens the bag.</summary>
        public Action BagRequested;
        /// <summary>Raised when the player opens the party screen.</summary>
        public Action PartyRequested;
        /// <summary>Raised when the player tries to flee.</summary>
        public Action RunRequested;
        /// <summary>Raised when the player throws a ball at the wild creature.</summary>
        public Action CaptureRequested;

        /// <summary>The docked scanner, for callers that want to drive it directly.</summary>
        public PokeLabScannerView Scanner => _scanner;

        /// <summary>The overlay director, exposed so the cinematics worker can drive it.</summary>
        public OverlayDirector Overlays => _overlays;

        /// <summary>The battle log, for systems that want to narrate outside the event stream.</summary>
        public BattleLogView Log => _log;

        private void Awake()
        {
            if (_buildOnAwake && !_built) BuildRuntime();
            if (_group == null) _group = UiBuilder.Group(this, 0f, false, false);
        }

        // ---------------------------------------------------------------- lifecycle

        /// <summary>Fades the HUD in and docks the scanner. Called when the battle stage is ready.</summary>
        public void Show()
        {
            gameObject.SetActive(true);
            if (_group != null)
            {
                _group.interactable = true;
                _group.blocksRaycasts = true;
                // Through the tracked fade, because the outgoing one has to die here: a Hide
                // still running when the next battle stages itself would otherwise switch the
                // whole HUD off a quarter of a second into the fight.
                UiTween.FadeActive(ref _visibilityFade, _group, true, 0.3f);
            }
            _scanner?.SetMode(ScannerMode.Docked);
        }

        /// <summary>Fades the HUD out. The cinematics worker calls this before the outro blend.</summary>
        public void Hide()
        {
            _scanner?.SetMode(ScannerMode.Hidden);
            _commands?.Lock();
            // A battle can end with the picker open — a capture resolving while the player
            // deliberates a switch — and a modal that survives its battle would still be
            // polling for input over the overworld.
            _partyPicker?.Close();
            if (_group == null) return;
            _group.interactable = false;
            _group.blocksRaycasts = false;
            UiTween.FadeActive(ref _visibilityFade, _group, false, 0.25f, Ease.InCubic);
        }

        /// <summary>
        /// Opens the command panel for the player's turn. Forecasts come from the most recent
        /// readout and may be null before the oracle lands.
        /// </summary>
        public void BeginPlayerTurn(CreatureInstance active, DamageForecast[] forecasts = null)
        {
            _playerActive = active ?? _playerActive;
            if (_playerActive != null) _playerPlate?.Bind(_playerActive);
            _commands?.BeginTurn(_playerActive, forecasts ?? _scanner?.LastReadout?.MoveForecasts);
        }

        /// <summary>
        /// Locks the command panel while a turn resolves. The picker closes with it: the
        /// decision timeout can fire while the picker is open, and a modal that outlives its
        /// turn would poll the keyboard against next turn's menu.
        /// </summary>
        public void LockCommands()
        {
            _commands?.Lock();
            _partyPicker?.Close();
        }

        /// <summary>Back navigation for an input handler to route a cancel press into.</summary>
        public bool GoBack() => _commands != null && _commands.GoBack();

        /// <summary>
        /// Opens the party picker over the command column, either as the PARTY command's
        /// answer or — <paramref name="forced"/> — as the replacement prompt after a faint.
        ///
        /// The command panel is locked first, not merely covered: both surfaces poll the
        /// keyboard themselves, and two open menus in one corner would take every arrow
        /// press twice. A pick surfaces through <see cref="SwitchRequested"/>, exactly as a
        /// switch from any other surface does, so the presenter needs no second wire; a
        /// cancel reopens the command menu on the same turn, because backing out of a
        /// voluntary switch must never cost the turn itself.
        /// </summary>
        public void OpenPartyPicker(System.Collections.Generic.IReadOnlyList<CreatureInstance> party,
            int activeIndex, bool forced)
        {
            if (_partyPicker == null) return;
            _commands?.Lock();
            _partyPicker.Open(party, activeIndex, forced);
            // A picker that declined to open — an empty or all-null party list — must not
            // leave the corner with both menus shut, or the turn dies to the decision
            // timeout with nothing on screen to press.
            if (!_partyPicker.IsOpen) BeginPlayerTurn(_playerActive);
        }

        // ------------------------------------------------------------------ readout

        /// <summary>
        /// Forwards the readout to the scanner and pushes the per-move forecasts into the
        /// command panel so the move cards stay live while the player deliberates.
        /// </summary>
        public void OnReadoutUpdated(TacticalReadout readout)
        {
            _scanner?.OnReadoutUpdated(readout);
            if (readout?.MoveForecasts != null) _commands?.UpdateForecasts(readout.MoveForecasts);
        }

        // ------------------------------------------------------------------- events

        /// <summary>
        /// Renders one battle event. Every case is presentation only; a case that cannot be
        /// rendered (missing plate, unknown side) falls through silently rather than throwing,
        /// because a presenter that throws mid-stream would strand the battle.
        /// </summary>
        public void OnBattleEvent(BattleEvent evt)
        {
            _scanner?.OnBattleEvent(evt);

            switch (evt)
            {
                case BattleStartedEvent started:
                    _log?.Clear();
                    // The one moment the battle's kind is stated, and the ball command's
                    // legality follows from it for the rest of the fight.
                    _battleKind = started.Kind;
                    _commands?.SetBattleKind(started.Kind);
                    _log?.Append(started.Kind == BattleKind.Trainer
                        ? Loc.Pick("A trainer challenges you!", "트레이너가 승부를 걸어왔다!")
                        : Loc.Pick("A wild creature appeared!", "앗! 야생 포켓몬이 나타났다!"));
                    _overlays?.PlayBattleIntro(started.Kind, started.OpponentTrainerId);
                    break;

                case CreatureSentOutEvent sentOut:
                    BindSide(sentOut.Side, sentOut.Creature, true);
                {
                    // "나타났다" against "내보냈다": one walked out of the grass on its own, the
                    // other was sent by somebody. DP keeps them apart and so does this — a wild
                    // encounter that says the grass "sent out" a Rattata is the tell that a
                    // template wrote the line.
                    //
                    // The split used to be by SIDE, which got the wild case right by accident
                    // and the trainer case wrong every time: an opposing trainer's creature was
                    // narrated as having "appeared", as though it had wandered in during their
                    // own battle. It is the battle's KIND that decides, not which half of the
                    // field the creature is standing on.
                    var who = UiServices.NameOf(sentOut.Creature);
                    if (sentOut.Side == BattleSide.Player)
                        _log?.Append(Loc.Pick($"Go, {who}!", $"가랏! {who}!"));
                    else if (_battleKind == BattleKind.Trainer)
                        _log?.Append(Loc.Pick($"The opponent sent out {who}!",
                                              $"상대는 {Josa.WithObject(who)} 내보냈다!"));
                    else
                        _log?.Append(Loc.Pick($"A wild {who} appeared!",
                                              $"{Josa.WithSubject(who)} 나타났다!"));
                }
                    break;

                case CreatureWithdrawnEvent withdrawn:
                    PlateFor(withdrawn.Side)?.SetVisible(false);
                    _log?.Append(Loc.Pick($"{UiServices.NameOf(withdrawn.Creature)} was withdrawn.",
                        $"돌아와! {UiServices.NameOf(withdrawn.Creature)}!"));
                    break;

                case MoveDeclaredEvent declared:
                {
                    var actor = UiServices.NameOf(ActiveOf(declared.Side));
                    var move = declared.MoveDisplayName ?? UiServices.MoveName(declared.MoveId);
                    // "피카츄의 전기쇼크!" — the games name the attacker and the move and stop
                    // there. "피카츄가 전기쇼크를 사용했다" is the translated version of the
                    // same sentence and reads like a manual.
                    _log?.Append(Loc.Pick($"{actor} used <b>{move}</b>!",
                                          $"{actor}의 <b>{move}</b>!"));
                }
                    break;

                case MoveMissedEvent missed:
                    _log?.Append(missed.WasImmune
                        ? Loc.Pick("It had no effect.", "효과가 없는 것 같다…")
                        : Loc.Pick("The attack missed!", "하지만 빗나갔다!"));
                    break;

                case DamageDealtEvent damage:
                    // Driven from the event's own RemainingHp, never from the instance. The
                    // engine has already resolved the whole turn by the time the first blow of
                    // it is staged, so the creature is holding its post-turn HP: a second hit
                    // in the same turn would find the bar already where it was going.
                    _lastHitSide = damage.Target;
                    PlateFor(damage.Target)?.PlayDamage(damage);
                    AppendEffectiveness(damage);
                    break;

                case HealedEvent healed:
                {
                    var plate = PlateFor(healed.Target);
                    if (plate != null && healed.MaxHp > 0) plate.PlayHeal(healed.Amount, healed.RemainingHp, healed.MaxHp);
                    else RefreshSide(healed.Target);
                    _log?.Append(Loc.Pick($"{UiServices.NameOf(ActiveOf(healed.Target))} recovered health.",
                        $"{Josa.WithTopic(UiServices.NameOf(ActiveOf(healed.Target)))} 체력을 회복했다!"));
                }
                    break;

                case StatusChangedEvent status:
                    RefreshSide(status.Target);
                    if (status.Current != StatusCondition.None)
                    {
                        var whom = UiServices.NameOf(ActiveOf(status.Target));
                        _log?.Append(Loc.Pick(
                            $"{whom} is <b>{StatusBadge.FullName(status.Current).ToLowerInvariant()}</b>!",
                            $"{Josa.WithTopic(whom)} <b>{StatusKorean(status.Current)}</b> 상태가 되었다!"));
                    }
                    break;

                case StatStageChangedEvent stage:
                    _log?.Append(BuildStatLine(stage));
                    break;

                case AbilityTriggeredEvent ability:
                {
                    var owner = UiServices.NameOf(ActiveOf(ability.Side));
                    var what = ability.AbilityDisplayName ?? UiServices.Titleise(ability.AbilityId ?? "ability");
                    _log?.Append(Loc.Pick($"{owner}'s <b>{what}</b> activated.",
                                          $"{owner}의 <b>{what}</b>!"));
                }
                    break;

                case ItemUsedEvent item:
                {
                    var what = item.ItemDisplayName ?? UiServices.Titleise(item.ItemId ?? "an item");
                    _log?.Append(Loc.Pick($"Used {what}.", $"{Josa.WithObject(what)} 사용했다!"));
                }
                    RefreshSide(item.Side);
                    break;

                case WeatherChangedEvent weather:
                    _log?.Append(WeatherLine(weather.Current));
                    break;

                case VolatileChangedEvent volatiles:
                    if (volatiles.Added != VolatileFlags.None)
                    {
                        var who = UiServices.NameOf(ActiveOf(volatiles.Target));
                        _log?.Append(Loc.Pick($"{who} is {volatiles.Added.ToString().ToLowerInvariant()}.",
                            $"{Josa.WithTopic(who)} {volatiles.Added.ToString().ToLowerInvariant()} 상태다!"));
                    }
                    break;

                case CreatureFaintedEvent fainted:
                    PlateFor(fainted.Side)?.SetVisible(false);
                    _log?.Append(Loc.Pick($"{UiServices.NameOf(fainted.Creature)} fainted!",
                        $"{Josa.WithTopic(UiServices.NameOf(fainted.Creature))} 쓰러졌다!"));
                    break;

                case CaptureAttemptEvent capture:
                    // No overlay. The choreography throws a real ball, absorbs the creature
                    // into it and shakes it exactly Shakes times; a flat ball on this canvas
                    // wobbling the same count under a catch-rate readout was the same event
                    // dramatised twice at once. Same reasoning as the result beats below — see
                    // PlayHudBeat — only applied to the beat it had never been applied to. The
                    // log line stays, because narration is this view's job and the arena has
                    // no words.
                    _log?.Append(capture.Succeeded
                        ? Loc.Pick("Gotcha! It was caught!", "신난다! 포켓몬을 잡았다!")
                        : Loc.Pick("It broke free!", "앗! 포켓몬이 볼에서 나와버렸다!"));
                    break;

                case ExperienceGainedEvent experience:
                    PlayExperienceGain(experience);
                    break;

                case BattleEndedEvent ended:
                    _commands?.Lock();
                    _overlays?.PlayResult(ended.Outcome, ended.MoneyEarned);
                    break;

                case MessageEvent message:
                    _log?.Append(message.Text);
                    break;
            }
        }

        /// <summary>
        /// The experience beat, on the plate rather than on a card.
        ///
        /// The event carries the new running total, the new level and how much was gained, but
        /// not where it started — so the start is reconstructed: the old total is the new one
        /// minus the gain, and the old level is what that total implies on the curve. That is
        /// exact rather than approximate, because the curve is a pure function of the total
        /// (see <see cref="ExperienceCurve"/>, which mirrors the engine's n³).
        ///
        /// Only the creature standing on the field gets the animation. Experience is shared
        /// with every participant, and a benched member's gain has no bar on screen to fill —
        /// it gets its line in the log and nothing else, rather than silently animating the
        /// wrong creature's plate, which is what binding by side alone would have done.
        /// </summary>
        private void PlayExperienceGain(ExperienceGainedEvent experience)
        {
            if (experience == null) return;

            var active = _playerActive;
            var isActive = active != null &&
                           (string.IsNullOrEmpty(experience.InstanceId) ||
                            experience.InstanceId == active.InstanceId);

            var name = isActive ? UiServices.NameOf(active) : null;
            _log?.Append(name != null
                ? Loc.Pick($"{name} gained {experience.Amount} EXP.",
                           $"{Josa.WithTopic(name)} 경험치를 {experience.Amount} 얻었다!")
                : Loc.Pick($"Gained {experience.Amount} EXP.",
                           $"경험치를 {experience.Amount} 얻었다!"));

            if (!isActive || _playerPlate == null) return;

            var newTotal = Mathf.Max(0, experience.NewTotal);
            var newLevel = Mathf.Max(1, experience.NewLevel);
            var oldTotal = Mathf.Clamp(newTotal - Mathf.Max(0, experience.Amount), 0, newTotal);
            var oldLevel = Mathf.Min(newLevel, ExperienceCurve.LevelFor(oldTotal));

            BattleFloater.Gain(_playerPlate.FloaterLayer, "+" + experience.Amount + " EXP",
                BattleSkin.Cyan, 38f, _playerPlate.FloatToRight, _playerPlate.FloatUpward);

            var plate = _playerPlate;
            plate.PlayExperience(oldTotal, oldLevel, newTotal, newLevel, 1.5f,
                onLevelUp: () =>
                {
                    if (plate == null) return;
                    BattleFloater.Shout(plate.FloaterLayer, Loc.Pick("LEVEL UP!", "레벨 업!"),
                        UiPalette.ScannerAmber, 52f, plate.FloatUpward);
                    // The frame's quiet agreement with the plate — a wash in the corner the
                    // plate lives in, not a card over the middle of the screen.
                    _overlays?.PlayLevelUp(active, newLevel);
                    PulseBeatFlash(UiPalette.ScannerAmber, 0.16f, 0.4f);
                },
                onComplete: () =>
                {
                    // Levelling recomputes stats, so max HP may have moved under the bar. The
                    // rebind is deliberately last: doing it up front would overwrite the roll
                    // with the creature's already-final fraction and there would be nothing
                    // left to animate.
                    if (plate != null && active != null && newLevel > oldLevel) plate.Bind(active);
                });
        }

        private void AppendEffectiveness(DamageDealtEvent damage)
        {
            var hurt = UiServices.NameOf(ActiveOf(damage.Target));

            if (!string.IsNullOrEmpty(damage.IndirectSourceId))
            {
                var source = UiServices.Titleise(damage.IndirectSourceId.Replace('_', ' '));
                _log?.Append(Loc.Pick($"{hurt} was hurt by {source}.",
                                      $"{Josa.WithTopic(hurt)} {source}(으)로 데미지를 입었다!"));
                return;
            }

            if (damage.Critical)
                _log?.Append(Loc.Pick("<b>A critical hit!</b>", "<b>급소에 맞았다!</b>"));

            switch (damage.Effectiveness)
            {
                case Effectiveness.SuperEffective:
                    _log?.Append(Loc.Pick("It's super effective!", "효과가 굉장했다!"));
                    break;
                case Effectiveness.NotVeryEffective:
                    _log?.Append(Loc.Pick("It's not very effective…", "효과가 별로인 것 같다…"));
                    break;
                case Effectiveness.Immune:
                    _log?.Append(Loc.Pick("It had no effect.", "효과가 없는 것 같다…"));
                    break;
            }

            // How much, in points and as a share of the bar.
            //
            // The games do not print a number, but they show a health bar draining over a
            // second and a half; here the bar and the log are the only feedback there is, and
            // "how badly did that hurt" was the question the fight could not answer.
            if (damage.Amount > 0 && damage.MaxHp > 0)
            {
                var share = Mathf.RoundToInt(100f * damage.Amount / damage.MaxHp);
                _log?.Append(Loc.Pick($"{hurt} took {damage.Amount} damage ({share}%).",
                                      $"{Josa.WithTopic(hurt)} {damage.Amount}의 데미지를 받았다! ({share}%)"));
            }
        }

        private string BuildStatLine(StatStageChangedEvent stage)
        {
            var name = UiServices.NameOf(ActiveOf(stage.Target));
            var stat = stage.Stat.ToString();
            var statKo = StatKorean(stage.Stat);

            if (stage.Delta > 0)
                return Loc.Pick($"{name}'s {stat} rose{(stage.Delta > 1 ? " sharply" : string.Empty)}!",
                    $"{name}의 {statKo}{(stage.Delta > 1 ? "이(가) 크게" : "이(가)")} 올라갔다!");
            if (stage.Delta < 0)
                return Loc.Pick($"{name}'s {stat} fell{(stage.Delta < -1 ? " harshly" : string.Empty)}!",
                    $"{name}의 {statKo}{(stage.Delta < -1 ? "이(가) 크게" : "이(가)")} 떨어졌다!");
            return Loc.Pick($"{name}'s {stat} will not change.", $"{name}의 {statKo}은(는) 더 이상 변하지 않는다!");
        }

        /// <summary>The Korean name of a status, as the games print it.</summary>
        private static string StatusKorean(StatusCondition status) => status switch
        {
            StatusCondition.Burn => "화상",
            StatusCondition.Freeze => "얼음",
            StatusCondition.Paralysis => "마비",
            StatusCondition.Poison => "독",
            StatusCondition.BadPoison => "맹독",
            StatusCondition.Sleep => "잠듦",
            _ => StatusBadge.FullName(status),
        };

        /// <summary>The Korean name of a stat.</summary>
        private static string StatKorean(StatKind stat) => stat switch
        {
            StatKind.Attack => "공격",
            StatKind.Defense => "방어",
            StatKind.SpAttack => "특수공격",
            StatKind.SpDefense => "특수방어",
            StatKind.Speed => "스피드",
            StatKind.Accuracy => "명중률",
            StatKind.Evasion => "회피율",
            _ => stat.ToString(),
        };

        private static string WeatherLine(Weather weather) => weather switch
        {
            Weather.Rain => Loc.Pick("Rain began to fall.", "비가 내리기 시작했다!"),
            Weather.Sun => Loc.Pick("The sunlight turned harsh.", "햇살이 강해졌다!"),
            Weather.Sandstorm => Loc.Pick("A sandstorm kicked up.", "모래바람이 불기 시작했다!"),
            Weather.Hail => Loc.Pick("It started to hail.", "싸라기눈이 내리기 시작했다!"),
            Weather.Fog => Loc.Pick("Fog rolled across the field.", "안개가 자욱해졌다!"),
            _ => Loc.Pick("The weather cleared.", "날씨가 원래대로 돌아왔다!"),
        };

        private void BindSide(BattleSide side, CreatureInstance creature, bool immediate)
        {
            if (side == BattleSide.Player) _playerActive = creature;
            else _opponentActive = creature;
            PlateFor(side)?.Bind(creature, immediate);
        }

        private void RefreshSide(BattleSide side)
        {
            var creature = ActiveOf(side);
            if (creature != null) PlateFor(side)?.Bind(creature);
        }

        /// <summary>
        /// The live creature for a side. The engine is preferred because it is authoritative;
        /// the cached send-out reference is the fallback so the HUD works standalone.
        /// </summary>
        private CreatureInstance ActiveOf(BattleSide side)
        {
            var state = UiServices.Engine?.State;
            if (state != null)
            {
                var fromEngine = state.ActiveOf(side);
                if (fromEngine != null) return fromEngine;
            }
            return side == BattleSide.Player ? _playerActive : _opponentActive;
        }

        private CreatureStatusPanel PlateFor(BattleSide side) =>
            side == BattleSide.Player ? _playerPlate : _opponentPlate;

        // -------------------------------------------------------------------- build

        /// <summary>
        /// Assembles the whole battle screen.
        ///
        /// The frame is read as four corners with an empty middle, because the middle is where
        /// the two creatures stand. The opponent's plate takes the top right, the player's the
        /// bottom left, the command column the bottom right, and the log the bottom-left band
        /// above the plate. Nothing is centred on the screen midpoint and nothing spans the
        /// full width — the previous layout put a full-width command panel and a full-width log
        /// across the bottom 390px and the player's own creature stood behind both of them.
        ///
        /// Every element is pinned to a corner at a fixed size rather than stretched, so the
        /// extra pixels at 21:9 widen the empty middle instead of any panel. The one exception
        /// is the log, which stretches from the left up to the command column's gutter.
        /// </summary>
        public void BuildRuntime()
        {
            _built = true;

            var rootRect = transform as RectTransform;
            if (rootRect == null)
            {
                Debug.LogError("BattleHudView must live on a RectTransform under a Canvas.");
                return;
            }

            UiBuilder.Stretch(rootRect);
            _group = UiBuilder.Group(this, 0f, false, false);

            var safe = UiBuilder.SafeArea(rootRect, 44f, 30f);

            // --- scanner column, pinned to the right edge.
            // The Poké Lab readout is built but not shown.
            //
            // It is the model's working surface, not the player's: win probability, threat
            // list, recommended switches and a paragraph explaining the estimate. On screen it
            // took a third of the width, was the loudest thing in the shot, and told the player
            // the answer before they had chosen — a solved battle is not a battle. The
            // intelligence still runs and still feeds the move forecasts the command menu
            // colours its buttons with; only the panel is gone.
            //
            // Built rather than skipped because OnReadoutUpdated and OnBattleEvent both feed it
            // and BeginPlayerTurn reads LastReadout for those forecasts. A null scanner would
            // quietly cost the forecasts as well as the panel.
            // Parked off-screen inside a holder it does not know about.
            //
            // Two gentler attempts failed. Deactivating the object is undone by Show(), which
            // calls SetMode(Docked) on every battle and animates the panel back in. A
            // CanvasGroup at zero alpha is ignored, because BuildRuntime adds a group of its
            // own and Unity honours only the first one on an object.
            //
            // A holder solves it because nothing inside the scanner has a reference to it:
            // SetMode can do whatever it likes to the panel's own alpha, position and mode,
            // and the whole thing is still ten thousand units to the left of the screen. The
            // component keeps running and keeps producing the move forecasts the command menu
            // reads off LastReadout; only the picture is gone.
            var scannerHolder = UiBuilder.Rect("ScannerHolder", safe, false);
            UiBuilder.Anchor(scannerHolder, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), new Vector2(-10000f, 0f), new Vector2(448f, 0f));

            var scannerGo = UiBuilder.Rect("Scanner", scannerHolder);
            _scanner = scannerGo.gameObject.AddComponent<PokeLabScannerView>();
            _scanner.BuildRuntime();
            _scanner.SwitchRequested += index => SwitchRequested?.Invoke(index);

            // --- main area: the full safe rect. The scanner column that used to be carved out
            // of the right-hand 448px is dead space now that the panel is built-but-hidden, and
            // reserving it pushed the command column a quarter of the screen inboard, straight
            // over the opponent's half of the diorama.
            var main = UiBuilder.Rect("Main", safe);

            // --- opponent plate, top right.
            _opponentPlate = CreatureStatusPanel.Build(main, BattleSide.Opponent);
            UiBuilder.Anchor(_opponentPlate.GetComponent<RectTransform>(),
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                Vector2.zero, new Vector2(CreatureStatusPanel.PlateWidth, CreatureStatusPanel.OpponentHeight));

            // --- command column, bottom right. Sized by the panel itself: the stack, the pill
            // heights and the gutter the selection chevron hangs in are all its business, and a
            // height guessed here would clip the bottom pill the moment any of them changed.
            _commands = BattleCommandPanel.Build(main);
            UiBuilder.Anchor(_commands.GetComponent<RectTransform>(),
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                Vector2.zero, new Vector2(BattleCommandPanel.PanelWidth, BattleCommandPanel.PanelHeight));
            _commands.MoveChosen += index => MoveChosen?.Invoke(index);
            _commands.BagRequested += () => BagRequested?.Invoke();
            _commands.PartyRequested += () => PartyRequested?.Invoke();
            _commands.RunRequested += () => RunRequested?.Invoke();
            _commands.CaptureRequested += () => CaptureRequested?.Invoke();
            // The refused throw is narrated here rather than in the panel because the log is
            // this view's to write. The line is the engine's own — the message it emits when
            // an illegal capture reaches it — so the answer is the same whether the rule is
            // enforced at the menu or in the engine.
            _commands.CaptureBlocked += () => _log?.Append(Loc.Pick(
                "You can't catch another trainer's creature!",
                "다른 트레이너의 포켓몬은 잡을 수 없다!"));

            // --- party picker, standing in the command column's corner while it is open.
            // Sized for a full party of six; with fewer members the rows bottom-align into
            // the same corner and the caption follows them down, like every stack here.
            _partyPicker = BattlePartyPicker.Build(main);
            UiBuilder.Anchor(_partyPicker.GetComponent<RectTransform>(),
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                Vector2.zero, new Vector2(BattlePartyPicker.PanelWidth, BattlePartyPicker.PanelHeight));
            _partyPicker.Picked += index => SwitchRequested?.Invoke(index);
            _partyPicker.Cancelled += () => BeginPlayerTurn(_playerActive);

            // --- player plate, bottom left.
            _playerPlate = CreatureStatusPanel.Build(main, BattleSide.Player);
            UiBuilder.Anchor(_playerPlate.GetComponent<RectTransform>(),
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                Vector2.zero, new Vector2(CreatureStatusPanel.PlateWidth, CreatureStatusPanel.PlayerHeight));

            // --- log, immediately above the player's plate, stopping at the command column's
            // gutter. It is the only element that flexes with width, and it flexes leftward,
            // so a wider screen buys the narration more room and costs the creatures nothing.
            var logInset = BattleCommandPanel.PanelWidth + _columnGutter;
            _log = BattleLogView.Build(main);
            UiBuilder.Anchor(_log.GetComponent<RectTransform>(),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(-logInset * 0.5f, CreatureStatusPanel.PlayerHeight + 14f),
                // Tall enough for three lines at the type scale's Body size. The log used to
                // hardcode 16pt and this box was cut to fit that; with the narration back on
                // the real scale, a box sized for the old font clips the line the fight is
                // actually telling you about.
                new Vector2(-logInset, 168f));

            // --- beat flash: a full-bleed tint the cinematic hook pulses for a crit or a
            // super-effective hit. Solid rather than a vignette because the vignette sprite
            // bakes black pixels and cannot be tinted; at the alphas this uses it reads as a
            // colour washing over the frame, not a cover. It sits below the overlays so a
            // result card is never flashed over.
            _beatFlash = UiBuilder.Image("BeatFlash", rootRect, UiSprites.Solid(),
                Color.white.WithAlpha(0f), Image.Type.Simple);
            _beatFlash.enabled = false;

            // --- overlays sit above everything, full-bleed, ignoring the safe area.
            var overlayRoot = UiBuilder.Rect("Overlays", rootRect);
            _overlays = overlayRoot.gameObject.AddComponent<OverlayDirector>();
            _overlays.BuildRuntime();

            UiBuilder.EnsureEventSystem();

            // The choreography's side of the seam. Registered here because this is the
            // moment the objects the beats act on exist; there is only ever one battle HUD,
            // so a re-registration after a domain reload simply replaces a dead reference.
            ServiceHub.Register<ICinematicHudHook>(this);
        }

        // ------------------------------------------------------------ cinematic hook

        /// <summary>
        /// Flies the HUD in or out for the choreography. The duration is honoured directly
        /// on the fade group rather than routed through <see cref="Show"/>/<see cref="Hide"/>,
        /// whose house timings would silently override a set piece that asked for a slow
        /// reveal — but the end states are identical to theirs, so the two paths can be
        /// interleaved without the HUD ending up half-interactive.
        /// </summary>
        public void SetHudVisible(bool visible, float duration)
        {
            // ServiceHub cannot unregister, so after a teardown a cached hook can point at a
            // destroyed view. Unity's lifetime-aware == makes this the honest check.
            if (this == null) return;
            duration = Mathf.Max(0.05f, duration);
            if (visible)
            {
                gameObject.SetActive(true);
                if (_group != null)
                {
                    _group.interactable = true;
                    _group.blocksRaycasts = true;
                    UiTween.Fade(_group, 1f, duration);
                }
                _scanner?.SetMode(ScannerMode.Docked);
                return;
            }

            _scanner?.SetMode(ScannerMode.Hidden);
            _commands?.Lock();
            _partyPicker?.Close();
            if (_group == null) { gameObject.SetActive(false); return; }
            _group.interactable = false;
            _group.blocksRaycasts = false;
            UiTween.Fade(_group, 0f, duration, Ease.InCubic, 0f, () =>
            {
                if (this != null) gameObject.SetActive(false);
            });
        }

        /// <summary>
        /// A named emphasis beat from the choreography, played cheap and non-blocking: a
        /// tint pulse and a plate punch, never a coroutine, never a wait. Unknown ids are
        /// ignored without logging — the beat vocabulary belongs to the cinematics worker
        /// and grows on their side of the seam, so an id this HUD has no answer for yet is
        /// the expected case, not an error. The result beats are deliberate no-ops here:
        /// <see cref="OverlayDirector.PlayResult"/> already stages those moments, and a
        /// flash under a VICTORY card would be the two systems shouting over each other.
        /// </summary>
        public void PlayHudBeat(string beatId, float intensity)
        {
            if (string.IsNullOrEmpty(beatId)) return;
            var strength = Mathf.Clamp(intensity <= 0f ? 1f : intensity, 0.2f, 2f);

            switch (beatId.ToLowerInvariant())
            {
                // The hit beats carry the share of the bar the blow took as their intensity.
                // The plate's own shake, flash and damage figure are already driven from the
                // event itself, so all the frame adds is a wash — and only for a hit big
                // enough to deserve one, because a tint on every chip is a strobe.
                case "hit_player":
                case "hit_opponent":
                    if (intensity >= 0.28f)
                    {
                        PulseBeatFlash(beatId == "hit_player" ? UiPalette.Negative : Color.white,
                            Mathf.Lerp(0.05f, 0.13f, Mathf.Clamp01(intensity)), 0.26f);
                    }
                    break;

                case "critical":
                    PulseBeatFlash(UiPalette.Critical, 0.20f * strength, 0.32f);
                    // The plate that was actually struck. This used to be hardcoded to the
                    // opponent's, so a critical landing on the player knocked the wrong plate.
                    ShakePlate(PlateFor(_lastHitSide), 22f * strength);
                    break;

                case "supereffective":
                    PulseBeatFlash(UiPalette.ScannerAmber, 0.14f * strength, 0.28f);
                    ShakePlate(PlateFor(_lastHitSide), 15f * strength);
                    break;

                case "capture_shake":
                    // No flash: the ball is the show and the OverlayDirector is already
                    // staging it. A whisper of motion on the plate is all the HUD adds.
                    ShakePlate(_opponentPlate, 7f * strength);
                    break;

                case "victory":
                case "defeat":
                case "fled":
                    // The result card is armed by BattleEndedEvent with a lead, and this is
                    // the frame the choreography says the moment has actually arrived — the
                    // winner has turned to the lens and started celebrating. Cueing it here
                    // is what makes the card read as caused by the celebration rather than
                    // as something that happened to appear at the same time.
                    _overlays?.CueResult();
                    break;
            }
        }

        /// <summary>One pulse of the beat tint: near-instant attack, exponential decay.</summary>
        private void PulseBeatFlash(Color tint, float peakAlpha, float duration)
        {
            if (_beatFlash == null) return;
            UiTween.Kill(ref _beatFlashTween);
            _beatFlash.enabled = true;
            _beatFlashTween = UiTween.Run(duration, t =>
            {
                if (_beatFlash == null) return;
                var alpha = t < 0.1f ? t / 0.1f : Mathf.Pow(1f - (t - 0.1f) / 0.9f, 2f);
                _beatFlash.color = tint.WithAlpha(alpha * peakAlpha);
            }, Ease.Linear, 0f, true, () =>
            {
                if (_beatFlash == null) return;
                _beatFlash.color = tint.WithAlpha(0f);
                _beatFlash.enabled = false;
            });
        }

        /// <summary>
        /// A knock on a plate, in pixels.
        ///
        /// Pixels rather than the scale punch this used to do: a plate that grows on impact
        /// reads as a button being pressed, and a plate shoved off its anchor reads as
        /// something hitting the creature it names.
        /// </summary>
        private static void ShakePlate(CreatureStatusPanel plate, float pixels)
        {
            if (plate == null || !plate.gameObject.activeInHierarchy) return;
            plate.Shake(pixels, 0.3f);
        }
    }
}
