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
        [Tooltip("Width reserved for the docked scanner column.")]
        [SerializeField] private float _scannerWidth = 430f;
        [SerializeField] private bool _buildOnAwake = true;

        private CreatureInstance _playerActive;
        private CreatureInstance _opponentActive;
        private bool _built;

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
                UiTween.Fade(_group, 1f, 0.3f);
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
            UiTween.Fade(_group, 0f, 0.25f, Ease.InCubic, 0f, () =>
            {
                if (this != null) gameObject.SetActive(false);
            });
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
                        ? "A trainer challenges you!"
                        : "A wild creature appeared!");
                    _overlays?.PlayBattleIntro(started.Kind, started.OpponentTrainerId);
                    break;

                case CreatureSentOutEvent sentOut:
                    BindSide(sentOut.Side, sentOut.Creature, true);
                    _log?.Append(sentOut.Side == BattleSide.Player
                        ? $"Go, {UiServices.NameOf(sentOut.Creature)}!"
                        : $"{UiServices.NameOf(sentOut.Creature)} entered the field.");
                    break;

                case CreatureWithdrawnEvent withdrawn:
                    PlateFor(withdrawn.Side)?.SetVisible(false);
                    _log?.Append($"{UiServices.NameOf(withdrawn.Creature)} was withdrawn.");
                    break;

                case MoveDeclaredEvent declared:
                    _log?.Append($"{UiServices.NameOf(ActiveOf(declared.Side))} used " +
                                 $"<b>{declared.MoveDisplayName ?? UiServices.MoveName(declared.MoveId)}</b>!");
                    break;

                case MoveMissedEvent missed:
                    _log?.Append(missed.WasImmune
                        ? $"It had no effect{(string.IsNullOrEmpty(missed.BlockingAbilityId) ? "" : " — " + UiServices.Titleise(missed.BlockingAbilityId.Replace('_', ' ')))}."
                        : "The attack missed!");
                    break;

                case DamageDealtEvent damage:
                    RefreshSide(damage.Target);
                    AppendEffectiveness(damage);
                    break;

                case HealedEvent healed:
                    RefreshSide(healed.Target);
                    _log?.Append($"{UiServices.NameOf(ActiveOf(healed.Target))} recovered health.");
                    break;

                case StatusChangedEvent status:
                    RefreshSide(status.Target);
                    if (status.Current != StatusCondition.None)
                    {
                        _log?.Append($"{UiServices.NameOf(ActiveOf(status.Target))} is " +
                                     $"<b>{StatusBadge.FullName(status.Current).ToLowerInvariant()}</b>!");
                    }
                    break;

                case StatStageChangedEvent stage:
                    _log?.Append(BuildStatLine(stage));
                    break;

                case AbilityTriggeredEvent ability:
                    _log?.Append($"{UiServices.NameOf(ActiveOf(ability.Side))}'s " +
                                 $"<b>{ability.AbilityDisplayName ?? UiServices.Titleise(ability.AbilityId ?? "ability")}</b> activated.");
                    break;

                case ItemUsedEvent item:
                    _log?.Append($"Used {item.ItemDisplayName ?? UiServices.Titleise(item.ItemId ?? "an item")}.");
                    RefreshSide(item.Side);
                    break;

                case WeatherChangedEvent weather:
                    _log?.Append(WeatherLine(weather.Current));
                    break;

                case VolatileChangedEvent volatiles:
                    if (volatiles.Added != VolatileFlags.None)
                    {
                        _log?.Append($"{UiServices.NameOf(ActiveOf(volatiles.Target))} is {volatiles.Added.ToString().ToLowerInvariant()}.");
                    }
                    break;

                case CreatureFaintedEvent fainted:
                    PlateFor(fainted.Side)?.SetVisible(false);
                    _log?.Append($"{UiServices.NameOf(fainted.Creature)} fainted!");
                    break;

                case CaptureAttemptEvent capture:
                    _overlays?.PlayCapture(capture.Shakes, capture.Succeeded, capture.CatchProbability);
                    _log?.Append(capture.Succeeded ? "Gotcha! It was caught!" : "It broke free!");
                    break;

                case ExperienceGainedEvent experience:
                    _log?.Append($"Gained {experience.Amount} EXP.");
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
            if (!string.IsNullOrEmpty(damage.IndirectSourceId))
            {
                _log?.Append($"{UiServices.NameOf(ActiveOf(damage.Target))} was hurt by " +
                             $"{UiServices.Titleise(damage.IndirectSourceId.Replace('_', ' '))}.");
                return;
            }

            if (damage.Critical) _log?.Append("<b>A critical hit!</b>");

            switch (damage.Effectiveness)
            {
                case Effectiveness.SuperEffective:
                    _log?.Append("It's super effective!");
                    break;
                case Effectiveness.NotVeryEffective:
                    _log?.Append("It's not very effective…");
                    break;
                case Effectiveness.Immune:
                    _log?.Append("It had no effect.");
                    break;
            }
        }

        private string BuildStatLine(StatStageChangedEvent stage)
        {
            var name = UiServices.NameOf(ActiveOf(stage.Target));
            var stat = stage.Stat.ToString();
            if (stage.Delta > 0) return $"{name}'s {stat} rose{(stage.Delta > 1 ? " sharply" : string.Empty)}!";
            if (stage.Delta < 0) return $"{name}'s {stat} fell{(stage.Delta < -1 ? " harshly" : string.Empty)}!";
            return $"{name}'s {stat} will not change.";
        }

        private static string WeatherLine(Weather weather) => weather switch
        {
            Weather.Rain => "Rain began to fall.",
            Weather.Sun => "The sunlight turned harsh.",
            Weather.Sandstorm => "A sandstorm kicked up.",
            Weather.Hail => "It started to hail.",
            Weather.Fog => "Fog rolled across the field.",
            _ => "The weather cleared.",
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
        /// The layout reserves a fixed-width column on the right for the scanner and gives
        /// everything else the remaining width, so at 21:9 the extra pixels widen the command
        /// panel and the log rather than stretching the scanner into a billboard. Nothing is
        /// centred on the screen midpoint, which is the usual cause of ultrawide overlap.
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
            var scannerColumn = UiBuilder.Rect("ScannerColumn", safe, false);
            UiBuilder.Anchor(scannerColumn, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
                Vector2.zero, new Vector2(_scannerWidth, 0f));
            var scannerGo = UiBuilder.Rect("Scanner", scannerColumn);
            _scanner = scannerGo.gameObject.AddComponent<PokeLabScannerView>();
            _scanner.BuildRuntime();
            _scanner.SwitchRequested += index => SwitchRequested?.Invoke(index);

            // --- main column, everything left of the scanner.
            var main = UiBuilder.Rect("Main", safe);
            main.offsetMax = new Vector2(-(_scannerWidth + 18f), 0f);

            _opponentPlate = CreatureStatusPanel.Build(main, BattleSide.Opponent);
            UiBuilder.Anchor(_opponentPlate.GetComponent<RectTransform>(),
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                Vector2.zero, new Vector2(340f, 78f));

            // 272 = 16px body inset top and bottom + 26 for the prompt line + the 208 the
            // 2×2 move grid needs. Shrinking this clips the bottom row of move cards.
            _commands = BattleCommandPanel.Build(main);
            UiBuilder.Anchor(_commands.GetComponent<RectTransform>(),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                Vector2.zero, new Vector2(0f, 272f));
            _commands.MoveChosen += index => MoveChosen?.Invoke(index);
            _commands.BagRequested += () => BagRequested?.Invoke();
            _commands.PartyRequested += () => PartyRequested?.Invoke();
            _commands.RunRequested += () => RunRequested?.Invoke();

            // Stacked upward from the command panel with a 12px gutter each time, so no two
            // elements can overlap regardless of aspect ratio — heights are fixed and only
            // the widths flex.
            _log = BattleLogView.Build(main);
            UiBuilder.Anchor(_log.GetComponent<RectTransform>(),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 284f), new Vector2(0f, 104f));

            _playerPlate = CreatureStatusPanel.Build(main, BattleSide.Player);
            UiBuilder.Anchor(_playerPlate.GetComponent<RectTransform>(),
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 400f), new Vector2(340f, 96f));

            // --- overlays sit above everything, full-bleed, ignoring the safe area.
            var overlayRoot = UiBuilder.Rect("Overlays", rootRect);
            _overlays = overlayRoot.gameObject.AddComponent<OverlayDirector>();
            _overlays.BuildRuntime();

            UiBuilder.EnsureEventSystem();
        }
    }
}
