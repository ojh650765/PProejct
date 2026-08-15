using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>Which page of the command panel is showing.</summary>
    public enum BattleCommandPage
    {
        /// <summary>Nothing — the engine is resolving a turn.</summary>
        Locked = 0,
        /// <summary>Fight / Bag / Party / Run.</summary>
        Root = 1,
        /// <summary>The four move cards with their forecasts.</summary>
        Moves = 2,
        /// <summary>Choosing which combatant a move hits.</summary>
        Targets = 3,
    }

    /// <summary>
    /// The player's command surface during battle: the root menu, the move grid with live
    /// damage forecasts, and target selection.
    ///
    /// Navigation is a small explicit stack rather than four booleans. Back always means
    /// "one page up", the panel locks itself the instant a decision is committed so a double
    /// press cannot submit two actions, and every page transition is a cross-fade — a
    /// command surface that pops between states makes a turn-based battle feel like a form.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleCommandPanel : MonoBehaviour
    {
        [Header("Pages")]
        [SerializeField] private CanvasGroup _rootPage;
        [SerializeField] private CanvasGroup _movePage;
        [SerializeField] private CanvasGroup _targetPage;

        [Header("Root menu")]
        [SerializeField] private Button _fightButton;
        [SerializeField] private Button _bagButton;
        [SerializeField] private Button _partyButton;
        [SerializeField] private Button _runButton;
        [SerializeField] private TextMeshProUGUI _promptLabel;

        [Header("Moves")]
        [SerializeField] private RectTransform _moveGrid;
        [SerializeField] private Button _moveBackButton;

        [Header("Targets")]
        [SerializeField] private RectTransform _targetList;
        [SerializeField] private Button _targetBackButton;

        private readonly List<MoveButtonView> _moveButtons = new List<MoveButtonView>(4);
        private readonly List<Button> _targetButtons = new List<Button>(2);
        private readonly Stack<BattleCommandPage> _history = new Stack<BattleCommandPage>(4);

        private BattleCommandPage _page = BattleCommandPage.Locked;
        private CreatureInstance _active;
        private DamageForecast[] _forecasts;
        private int _pendingMoveIndex = -1;
        private bool _committed;

        /// <summary>Raised when the player commits a move, with its slot index.</summary>
        public Action<int> MoveChosen;
        /// <summary>Raised when the player opens the bag.</summary>
        public Action BagRequested;
        /// <summary>Raised when the player opens the party screen.</summary>
        public Action PartyRequested;
        /// <summary>Raised when the player tries to flee.</summary>
        public Action RunRequested;

        /// <summary>The page currently visible.</summary>
        public BattleCommandPage Page => _page;

        /// <summary>
        /// Enables the panel for a new turn. Call once per turn, after the engine has
        /// finished presenting the previous one.
        /// </summary>
        public void BeginTurn(CreatureInstance active, DamageForecast[] forecasts)
        {
            _active = active;
            _forecasts = forecasts;
            _committed = false;
            _pendingMoveIndex = -1;
            _history.Clear();
            RefreshMoves();
            ShowPage(BattleCommandPage.Root);
        }

        /// <summary>Locks the panel while the engine resolves. No input is accepted.</summary>
        public void Lock() => ShowPage(BattleCommandPage.Locked);

        /// <summary>
        /// Pushes new forecasts without disturbing navigation. The readout updates every time
        /// battle conditions change, and the move cards must follow it live — a player
        /// deliberating over the move grid should watch the numbers move.
        /// </summary>
        public void UpdateForecasts(DamageForecast[] forecasts)
        {
            _forecasts = forecasts;
            if (_active == null) return;
            for (var i = 0; i < _moveButtons.Count; i++)
            {
                if (i >= _active.Moves.Count) continue;
                _moveButtons[i].Bind(i, _active.Moves[i], ForecastFor(i));
            }
        }

        /// <summary>Back navigation. Returns false when already at the root page.</summary>
        public bool GoBack()
        {
            if (_history.Count == 0) return false;
            ShowPage(_history.Pop(), false);
            return true;
        }

        private void RefreshMoves()
        {
            if (_active == null) return;
            EnsureMoveButtons(_active.Moves.Count);

            for (var i = 0; i < _moveButtons.Count; i++)
            {
                if (i < _active.Moves.Count)
                {
                    _moveButtons[i].Bind(i, _active.Moves[i], ForecastFor(i), true);
                }
                else
                {
                    _moveButtons[i].gameObject.SetActive(false);
                }
            }
        }

        private DamageForecast? ForecastFor(int index)
        {
            if (_forecasts == null || index < 0 || index >= _forecasts.Length) return null;
            return _forecasts[index];
        }

        private void EnsureMoveButtons(int count)
        {
            while (_moveButtons.Count < count)
            {
                var button = MoveButtonView.Build(_moveGrid);
                button.Chosen += OnMoveChosen;
                _moveButtons.Add(button);
            }
        }

        private void OnMoveChosen(int index)
        {
            if (_committed) return;

            var move = UiServices.MoveOf(_active != null && index < _active.Moves.Count
                ? _active.Moves[index].MoveId
                : null);

            // In a one-on-one battle there is exactly one legal target, so asking would be
            // busywork. The target page exists for the doubles case and for moves that can
            // hit the user's own side.
            var candidates = BuildTargetCandidates(move);
            if (candidates.Count <= 1)
            {
                Commit(index);
                return;
            }

            _pendingMoveIndex = index;
            BuildTargetButtons(candidates);
            ShowPage(BattleCommandPage.Targets);
        }

        private void Commit(int moveIndex)
        {
            _committed = true;
            ShowPage(BattleCommandPage.Locked);
            MoveChosen?.Invoke(moveIndex);
        }

        private List<CreatureInstance> BuildTargetCandidates(MoveData move)
        {
            var candidates = new List<CreatureInstance>(2);
            var state = UiServices.Engine?.State;
            if (state == null) return candidates;

            var opponent = state.ActiveOf(BattleSide.Opponent);
            if (opponent != null && !opponent.IsFainted) candidates.Add(opponent);

            // Self-targeting status moves add the user as a second candidate.
            if (move != null && move.TargetsSelf)
            {
                var self = state.ActiveOf(BattleSide.Player);
                if (self != null) candidates.Add(self);
            }
            return candidates;
        }

        private void BuildTargetButtons(List<CreatureInstance> candidates)
        {
            UiBuilder.ClearChildren(_targetList);
            _targetButtons.Clear();

            for (var i = 0; i < candidates.Count; i++)
            {
                var creature = candidates[i];
                var row = UiBuilder.Rect("Target", _targetList);
                UiBuilder.Size(row, preferredHeight: 46f, minHeight: 46f, flexibleWidth: 1f);

                var background = UiBuilder.Image("Bg", row, UiSprites.Panel(10), UiPalette.SurfaceRaised,
                    Image.Type.Sliced, true);
                UiBuilder.Stretch(background.rectTransform);

                var inner = UiBuilder.Rect("Inner", row);
                UiBuilder.Horizontal(inner, 10f, new RectOffset(12, 12, 0, 0), TextAnchor.MiddleLeft);

                var label = UiBuilder.Text("Name", inner, UiServices.NameOf(creature), UiTextRole.Body);
                UiBuilder.Size(label.rectTransform, flexibleWidth: 1f);

                var hp = UiBuilder.Text("Hp", inner, Mathf.CeilToInt(creature.HpFraction * 100f) + "%",
                    UiTextRole.Numeric, UiPalette.Health(creature.HpFraction), TextAlignmentOptions.Right);
                UiBuilder.Size(hp.rectTransform, preferredWidth: 54f, minWidth: 54f);

                UiButtonMotion.Attach(row, 10);
                var index = _pendingMoveIndex;
                _targetButtons.Add(UiBuilder.Button("Pick", row, background, () => Commit(index)));
            }
        }

        /// <summary>Cross-fades to a page, optionally pushing the current one onto the back stack.</summary>
        private void ShowPage(BattleCommandPage page, bool remember = true)
        {
            if (remember && _page != BattleCommandPage.Locked && _page != page) _history.Push(_page);
            _page = page;

            Fade(_rootPage, page == BattleCommandPage.Root);
            Fade(_movePage, page == BattleCommandPage.Moves);
            Fade(_targetPage, page == BattleCommandPage.Targets);

            if (_promptLabel == null) return;
            _promptLabel.SetText(page switch
            {
                BattleCommandPage.Root => _active != null
                    ? $"What will {UiServices.NameOf(_active)} do?"
                    : "Choose an action.",
                BattleCommandPage.Moves => "Choose a move.",
                BattleCommandPage.Targets => "Choose a target.",
                _ => string.Empty,
            });
        }

        private static void Fade(CanvasGroup group, bool visible)
        {
            if (group == null) return;
            if (visible) group.gameObject.SetActive(true);
            group.interactable = visible;
            group.blocksRaycasts = visible;
            UiTween.Fade(group, visible ? 1f : 0f, visible ? 0.18f : 0.12f, visible ? Ease.OutCubic : Ease.InCubic,
                0f, () => { if (!visible && group != null) group.gameObject.SetActive(false); });
        }

        /// <summary>Builds the whole command surface. Sized to sit across the bottom of the HUD.</summary>
        public static BattleCommandPanel Build(Transform parent)
        {
            var root = UiBuilder.Rect("CommandPanel", parent);
            var panel = root.gameObject.AddComponent<BattleCommandPanel>();

            var shell = UiBuilder.Panel("Shell", root, UiPalette.Surface.WithAlpha(0.96f), 18, true, 24, 0.55f);
            var rim = UiBuilder.Image("Rim", shell.rectTransform, UiSprites.Frame(18, 1), Color.white.WithAlpha(0.06f));
            UiBuilder.Stretch(rim.rectTransform);

            var body = UiBuilder.Rect("Body", root);
            UiBuilder.Stretch(body, 16f);

            panel._promptLabel = UiBuilder.Text("Prompt", body, string.Empty, UiTextRole.Secondary,
                UiPalette.TextSecondary);
            UiBuilder.Anchor(panel._promptLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 20f));

            var pageArea = UiBuilder.Rect("Pages", body);
            pageArea.offsetMax = new Vector2(0f, -26f);

            panel._rootPage = BuildRootPage(pageArea, panel);
            panel._movePage = BuildMovePage(pageArea, panel);
            panel._targetPage = BuildTargetPage(pageArea, panel);

            panel.ShowPage(BattleCommandPage.Locked, false);
            return panel;
        }

        private static CanvasGroup BuildRootPage(Transform parent, BattleCommandPanel panel)
        {
            var page = UiBuilder.Rect("RootPage", parent);
            var group = UiBuilder.Group(page, 0f, false, false);
            UiBuilder.Grid(page, new Vector2(228f, 62f), new Vector2(12f, 10f), 2);

            panel._fightButton = BuildMenuButton(page, "Fight", UiPalette.ScannerCyan, () =>
                panel.ShowPage(BattleCommandPage.Moves));
            panel._bagButton = BuildMenuButton(page, "Bag", UiPalette.ScannerAmber, () =>
                panel.BagRequested?.Invoke());
            panel._partyButton = BuildMenuButton(page, "Party", UiPalette.Positive, () =>
                panel.PartyRequested?.Invoke());
            panel._runButton = BuildMenuButton(page, "Run", UiPalette.TextMuted, () =>
                panel.RunRequested?.Invoke());

            return group;
        }

        private static CanvasGroup BuildMovePage(Transform parent, BattleCommandPanel panel)
        {
            var page = UiBuilder.Rect("MovePage", parent);
            var group = UiBuilder.Group(page, 0f, false, false);

            var grid = UiBuilder.Rect("Grid", page);
            grid.offsetMax = new Vector2(-96f, 0f);
            // 2 rows of 100 plus 8 spacing = 208, which the panel's page area is sized to
            // clear. The 96px inset on the right is the back button's column.
            UiBuilder.Grid(grid, new Vector2(440f, 100f), new Vector2(10f, 8f), 2);
            panel._moveGrid = grid;

            panel._moveBackButton = BuildBackButton(page, () => panel.GoBack());
            return group;
        }

        private static CanvasGroup BuildTargetPage(Transform parent, BattleCommandPanel panel)
        {
            var page = UiBuilder.Rect("TargetPage", parent);
            var group = UiBuilder.Group(page, 0f, false, false);

            var list = UiBuilder.Rect("List", page);
            list.offsetMax = new Vector2(-96f, 0f);
            UiBuilder.Vertical(list, 8f, null, TextAnchor.UpperLeft);
            panel._targetList = list;

            panel._targetBackButton = BuildBackButton(page, () => panel.GoBack());
            return group;
        }

        private static Button BuildMenuButton(Transform parent, string label, Color accent, Action onClick)
        {
            var root = UiBuilder.Rect("Menu_" + label, parent, false);

            var background = UiBuilder.Image("Bg", root, UiSprites.Panel(12),
                Color.Lerp(UiPalette.SurfaceRaised, accent, 0.12f), Image.Type.Sliced, true);
            UiBuilder.Stretch(background.rectTransform);

            var stripe = UiBuilder.Image("Stripe", root, UiSprites.Pill(8), accent);
            UiBuilder.Anchor(stripe.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(3f, -18f));

            var text = UiBuilder.Text("Label", root, label, UiTextRole.Heading, UiPalette.TextPrimary,
                TextAlignmentOptions.Left);
            UiBuilder.Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(24f, 0f);

            UiButtonMotion.Attach(root, 12, accent);
            return UiBuilder.Button("Click", root, background, onClick);
        }

        private static Button BuildBackButton(Transform parent, Action onClick)
        {
            var root = UiBuilder.Rect("Back", parent, false);
            UiBuilder.Anchor(root, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
                new Vector2(0f, 0f), new Vector2(84f, 0f));

            var background = UiBuilder.Image("Bg", root, UiSprites.Panel(12), UiPalette.SurfaceSunken,
                Image.Type.Sliced, true);
            UiBuilder.Stretch(background.rectTransform);

            var chevron = UiBuilder.Image("Chevron", root, UiSprites.Chevron(28, false, 5), UiPalette.TextSecondary,
                Image.Type.Simple);
            chevron.preserveAspect = true;
            chevron.rectTransform.anchorMin = chevron.rectTransform.anchorMax = new Vector2(0.5f, 0.62f);
            chevron.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            chevron.rectTransform.sizeDelta = new Vector2(20f, 20f);
            // The one chevron sprite, rotated to point left.
            chevron.rectTransform.localEulerAngles = new Vector3(0f, 0f, 90f);

            var label = UiBuilder.Text("Label", root, "BACK", UiTextRole.Overline, UiPalette.TextMuted,
                TextAlignmentOptions.Center);
            UiBuilder.Anchor(label.rectTransform, new Vector2(0f, 0.28f), new Vector2(1f, 0.28f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0f, 14f));

            UiButtonMotion.Attach(root, 12);
            return UiBuilder.Button("Click", root, background, onClick);
        }
    }
}
