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
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleHudView : MonoBehaviour, IBattleEventListener, IPokeLabListener
    {
        [Header("Parts")]
        [SerializeField] private CreatureStatusPanel _playerPlate;
        [SerializeField] private CreatureStatusPanel _opponentPlate;
        [SerializeField] private PokeLabScannerView _scanner;
        [SerializeField] private BattleCommandPanel _commands;
        [SerializeField] private BattleLogView _log;
        [SerializeField] private OverlayDirector _overlays;
        [SerializeField] private CanvasGroup _group;

        [Header("Layout")]
        [Tooltip("Clear space between the command column and anything to its left.")]
        [SerializeField] private float _columnGutter = 40f;
        [SerializeField] private bool _buildOnAwake = true;

        private CreatureInstance _playerActive;
        private CreatureInstance _opponentActive;
        private bool _built;
        private TweenHandle _visibilityFade;

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

        /// <summary>Locks the command panel while a turn resolves.</summary>
        public void LockCommands() => _commands?.Lock();

        /// <summary>Back navigation for an input handler to route a cancel press into.</summary>
        public bool GoBack() => _commands != null && _commands.GoBack();

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
                    _log?.Append(started.Kind == BattleKind.Trainer
                        ? Loc.Pick("A trainer challenges you!", "트레이너가 승부를 걸어왔다!")
                        : Loc.Pick("A wild creature appeared!", "앗! 야생 포켓몬이 나타났다!"));
                    _overlays?.PlayBattleIntro(started.Kind, started.OpponentTrainerId);
                    break;

                case CreatureSentOutEvent sentOut:
                    BindSide(sentOut.Side, sentOut.Creature, true);
                    // The wild side gets "나타났다", the trainer side "내보냈다": one walked
                    // out of the grass on its own, the other was sent. DP keeps them apart and
                    // so does this — a wild encounter that says the grass "sent out" a Rattata
                    // is the tell that a template wrote the line.
                    _log?.Append(sentOut.Side == BattleSide.Player
                        ? Loc.Pick($"Go, {UiServices.NameOf(sentOut.Creature)}!",
                                   $"가랏! {UiServices.NameOf(sentOut.Creature)}!")
                        : Loc.Pick($"{UiServices.NameOf(sentOut.Creature)} entered the field.",
                                   $"{Josa.WithSubject(UiServices.NameOf(sentOut.Creature))} 나타났다!"));
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
                    RefreshSide(damage.Target);
                    AppendEffectiveness(damage);
                    break;

                case HealedEvent healed:
                    RefreshSide(healed.Target);
                    _log?.Append(Loc.Pick($"{UiServices.NameOf(ActiveOf(healed.Target))} recovered health.",
                        $"{Josa.WithTopic(UiServices.NameOf(ActiveOf(healed.Target)))} 체력을 회복했다!"));
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
                    _overlays?.PlayCapture(capture.Shakes, capture.Succeeded, capture.CatchProbability);
                    _log?.Append(capture.Succeeded
                        ? Loc.Pick("Gotcha! It was caught!", "신난다! 포켓몬을 잡았다!")
                        : Loc.Pick("It broke free!", "앗! 포켓몬이 볼에서 나와버렸다!"));
                    break;

                case ExperienceGainedEvent experience:
                    _log?.Append(Loc.Pick($"Gained {experience.Amount} EXP.",
                        $"경험치를 {experience.Amount} 얻었다!"));
                    if (experience.LeveledUp)
                    {
                        _overlays?.PlayLevelUp(_playerActive, experience.NewLevel);
                        if (_playerActive != null) _playerPlate?.Bind(_playerActive);
                    }
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

            // --- overlays sit above everything, full-bleed, ignoring the safe area.
            var overlayRoot = UiBuilder.Rect("Overlays", rootRect);
            _overlays = overlayRoot.gameObject.AddComponent<OverlayDirector>();
            _overlays.BuildRuntime();

            UiBuilder.EnsureEventSystem();
        }
    }
}
