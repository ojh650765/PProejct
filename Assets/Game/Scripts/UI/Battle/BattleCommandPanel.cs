using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
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
        /// <summary>Fight / Throw Ball / Party / Bag / Run.</summary>
        Root = 1,
        /// <summary>The four move cards with their forecasts.</summary>
        Moves = 2,
        /// <summary>Choosing which combatant a move hits.</summary>
        Targets = 3,
    }

    /// <summary>
    /// The player's command surface during battle: the root menu, the move list with live
    /// damage forecasts, and target selection.
    ///
    /// A single column of pills pinned to the bottom-right corner, not a grid across the
    /// bottom of the frame. The grid it replaces spanned the full width and put the player's
    /// own creature behind it — a battle UI that hides a combatant is worse than no UI, and
    /// a column leaves the whole centre band of the shot to the two creatures.
    ///
    /// Input is a selected-index model driven from here, not the EventSystem's navigation.
    /// Unity's navigation needs a currently-selected GameObject to move from, which means the
    /// first arrow press after a mouse click behaves differently from the first arrow press
    /// after a keyboard-only turn. Owning the cursor makes the keyboard and the mouse agree at
    /// every moment: hovering a pill moves the cursor, arrows move the cursor, and Enter takes
    /// whatever the cursor is on.
    ///
    /// Navigation between pages is a small explicit stack rather than four booleans. Back
    /// always means "one page up", the panel locks itself the instant a decision is committed
    /// so a double press cannot submit two actions, and every page transition is a cross-fade.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleCommandPanel : MonoBehaviour
    {
        /// <summary>Column width, including the gutter the selection chevron hangs in.</summary>
        public const float PanelWidth = MoveButtonView.SelectedWidth + ChevronGutter + 8f;

        /// <summary>
        /// Column height: the caption band plus the tallest of the three stacks. Since the
        /// ball command joined the root menu that is the five-pill root page, not the four
        /// move cards — five pills at 84px outgrow four rows at 94px.
        /// </summary>
        public const float PanelHeight = CaptionHeight + StackGap + 5f * PillHeight + 4f * PillSpacing;

        private const float ChevronGutter = 38f;
        private const float CaptionHeight = 44f;
        private const float StackGap = 12f;
        private const float PillSpacing = 10f;
        private const float PillHeight = 84f;
        private const float PillWidth = 400f;
        private const float PillSelectedWidth = 452f;
        private const float BackWidth = 138f;
        private const int PillRadius = 22;

        [Header("Pages")]
        [SerializeField] private CanvasGroup _rootPage;
        [SerializeField] private CanvasGroup _movePage;
        [SerializeField] private CanvasGroup _targetPage;

        [Header("Chrome")]
        [SerializeField] private RectTransform _caption;
        [SerializeField] private TextMeshProUGUI _promptLabel;
        [SerializeField] private RectTransform _backButton;

        [Header("Lists")]
        [SerializeField] private RectTransform _moveList;
        [SerializeField] private RectTransform _targetList;

        private readonly List<MoveButtonView> _moveButtons = new List<MoveButtonView>(4);
        private readonly List<CommandPill> _rootPills = new List<CommandPill>(5);
        private readonly List<CommandPill> _targetPills = new List<CommandPill>(2);
        private readonly List<Entry> _entries = new List<Entry>(5);
        private readonly Stack<BattleCommandPage> _history = new Stack<BattleCommandPage>(4);

        private BattleCommandPage _page = BattleCommandPage.Locked;
        private CreatureInstance _active;
        private DamageForecast[] _forecasts;
        private CommandPill _capturePill;
        private BattleKind _kind = BattleKind.Wild;
        private int _pendingMoveIndex = -1;
        private int _selected;
        private bool _committed;
        // One per page, so a cross-fade is superseded by the next change to that same page
        // rather than left to finish against the state it captured.
        private TweenHandle _rootFade;
        private TweenHandle _moveFade;
        private TweenHandle _targetFade;

        /// <summary>Raised when the player commits a move, with its slot index.</summary>
        public Action<int> MoveChosen;
        /// <summary>Raised when the player opens the bag.</summary>
        public Action BagRequested;
        /// <summary>Raised when the player opens the party screen.</summary>
        public Action PartyRequested;
        /// <summary>Raised when the player tries to flee.</summary>
        public Action RunRequested;
        /// <summary>Raised when the player throws a ball at a wild creature.</summary>
        public Action CaptureRequested;
        /// <summary>Raised when the ball command is pressed in a battle where it is illegal.</summary>
        public Action CaptureBlocked;

        /// <summary>The page currently visible.</summary>
        public BattleCommandPage Page => _page;

        /// <summary>Index of the entry the cursor is on, within the current page.</summary>
        public int SelectedIndex => _selected;

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
            UiSound.MenuOpen();
        }

        /// <summary>
        /// Tells the panel what kind of battle it is commanding. The HUD calls this from
        /// <see cref="BattleStartedEvent"/>, which is the one moment the kind is stated.
        ///
        /// The ball command stays visible in a trainer battle rather than disappearing —
        /// a menu whose entries come and go between fights reads as the layout being broken,
        /// and pressing it is how the player learns the rule. It is drawn refused and answers
        /// with the refusal sound and the engine's own line, mirroring what the engine would
        /// say had the action reached it.
        /// </summary>
        public void SetBattleKind(BattleKind kind)
        {
            _kind = kind;
            if (_capturePill == null) return;
            _capturePill.Enabled = kind == BattleKind.Wild;
            // Redraw with the cursor preserved: the resting/refused face is applied by
            // SetSelected, and the kind can arrive while the root page is already showing.
            if (_page == BattleCommandPage.Root) ApplySelection();
            else _capturePill.SetSelected(false);
        }

        /// <summary>
        /// Locks the panel while the engine resolves. No input is accepted.
        ///
        /// The lock is not a page the player navigated to, so it is neither remembered nor
        /// left with somewhere to go back to. Pushing it on the stack activated the cancel
        /// control, and the caption band carries no CanvasGroup — so Back stayed clickable
        /// through the whole of the enemy's turn and walked the player straight back into a
        /// live move list.
        /// </summary>
        public void Lock()
        {
            _history.Clear();
            ShowPage(BattleCommandPage.Locked, false);
        }

        /// <summary>
        /// Pushes new forecasts without disturbing navigation. The readout updates every time
        /// battle conditions change, and the move pills must follow it live — a player
        /// deliberating over the move list should watch the verdicts move.
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
            // Rebinding re-applies each pill's resting look, so the cursor has to be redrawn or
            // it silently vanishes mid-deliberation — exactly when the player is looking at it.
            if (_page == BattleCommandPage.Moves) ApplySelection();
        }

        /// <summary>Back navigation. Returns false when already at the root page.</summary>
        public bool GoBack()
        {
            // Refused once a decision is in, and while the panel is locked. This is reachable
            // from the cancel control and from BattleHudView.GoBack, neither of which can see
            // that the turn has already been committed.
            if (_committed || _page == BattleCommandPage.Locked) return false;
            if (_history.Count == 0) return false;
            ShowPage(_history.Pop(), false);
            UiSound.Cancel();
            return true;
        }

        // ------------------------------------------------------------------- input

        /// <summary>Moves the cursor by <paramref name="delta"/>, wrapping at both ends.</summary>
        public void Move(int delta)
        {
            if (_entries.Count == 0) return;
            SelectIndex(((_selected + delta) % _entries.Count + _entries.Count) % _entries.Count);
        }

        /// <summary>Puts the cursor on a specific entry. Out-of-range indices are ignored.</summary>
        public void SelectIndex(int index)
        {
            if (index < 0 || index >= _entries.Count || index == _selected) return;
            _selected = index;
            ApplySelection();
            // Ticked here rather than in Move(): this is the one funnel every cursor change
            // passes through — arrows, WASD, pad and pointer hover — and the guard above has
            // already dropped the no-op calls that would otherwise click every frame a
            // pointer rests on the selected pill.
            UiSound.Navigate();
        }

        /// <summary>Takes the entry the cursor is on.</summary>
        public void Take()
        {
            if (_committed || _selected < 0 || _selected >= _entries.Count) return;
            _entries[_selected].Take?.Invoke();
        }

        /// <summary>
        /// Polls the keyboard directly.
        ///
        /// Polled here rather than routed in from the battle presenter because the presenter
        /// lives in another assembly and already owns the turn loop; giving it a second job
        /// would mean the panel could not be tested or driven on its own. Mouse input arrives
        /// through the pills' own Buttons, so both devices reach the same selected-index model.
        /// </summary>
        private void Update()
        {
            if (_page == BattleCommandPage.Locked || _committed) return;

            // Only while nothing is stacked on top of this panel. Choosing Party or Bag opens
            // a screen over the battle and the root page stays behind it, so without this the
            // arrows drove a cursor the player could not see and Enter re-fired the very entry
            // that opened the screen they were standing in. The clearing of the EventSystem's
            // selection below is inside the guard for the same reason: it would otherwise
            // deselect that screen's own controls, and the name field, every frame.
            if (!UiFocus.Owns(this)) return;

            // Unity's own UI navigation moves whatever GameObject the EventSystem has selected,
            // and clicking a pill selects it. Left alone, one mouse click would leave arrow
            // presses driving both cursors at once and landing on different pills. Clearing the
            // EventSystem's selection every frame the panel is open makes this the only cursor.
            var events = EventSystem.current;
            if (events != null && events.currentSelectedGameObject != null)
                events.SetSelectedGameObject(null);

            // Both devices, because a menu the pad cannot reach is a menu half the players
            // cannot use — and because the play probe drives a synthetic pad, so a
            // keyboard-only panel is also a panel no capture can ever photograph past its
            // first page.
            var keyboard = Keyboard.current;
            var pad = Gamepad.current;
            if (keyboard == null && pad == null) return;

            var down = (keyboard != null && (keyboard.downArrowKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame))
                       || (pad != null && pad.dpad.down.wasPressedThisFrame);
            var up = (keyboard != null && (keyboard.upArrowKey.wasPressedThisFrame || keyboard.wKey.wasPressedThisFrame))
                     || (pad != null && pad.dpad.up.wasPressedThisFrame);
            var confirm = (keyboard != null && (keyboard.enterKey.wasPressedThisFrame
                                                || keyboard.numpadEnterKey.wasPressedThisFrame
                                                || keyboard.zKey.wasPressedThisFrame
                                                || keyboard.spaceKey.wasPressedThisFrame))
                          || (pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.buttonNorth.wasPressedThisFrame));
            var back = (keyboard != null && (keyboard.escapeKey.wasPressedThisFrame
                                             || keyboard.xKey.wasPressedThisFrame
                                             || keyboard.backspaceKey.wasPressedThisFrame))
                       || (pad != null && pad.buttonEast.wasPressedThisFrame);

            if (down) Move(1);
            if (up) Move(-1);
            if (confirm) Take();
            if (back) GoBack();
        }

        // ------------------------------------------------------------------- moves

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

        /// <summary>
        /// The forecast for a move slot, or null when there is none.
        ///
        /// The analyser hands back a slot-aligned array and marks a slot it skipped with an
        /// empty placeholder rather than shortening the array, so entry i is always move i.
        /// An empty entry has to come back as null: a default forecast carries a zero type
        /// multiplier, which the pill would print as "no effect" — a confident wrong verdict
        /// on a move the model never evaluated. A real forecast always names a KO horizon,
        /// even if that is <see cref="int.MaxValue"/> for a move that cannot get there.
        /// </summary>
        private DamageForecast? ForecastFor(int index)
        {
            if (_forecasts == null || index < 0 || index >= _forecasts.Length) return null;
            var forecast = _forecasts[index];
            if (forecast.TurnsToKo == 0 && forecast.HitChance <= 0f &&
                forecast.TypeMultiplier <= 0f && forecast.MaxFraction <= 0f) return null;
            return forecast;
        }

        private void EnsureMoveButtons(int count)
        {
            while (_moveButtons.Count < count)
            {
                var button = MoveButtonView.Build(_moveList);
                button.Chosen += OnMoveChosen;
                var index = _moveButtons.Count;
                Hover(button.gameObject, () => SelectEntryFor(index, BattleCommandPage.Moves));
                _moveButtons.Add(button);
            }
        }

        /// <summary>
        /// Runs a root-menu action, but only while the root menu is genuinely the player's.
        ///
        /// The move pills have always been gated on <c>_committed</c>; the root pills were not,
        /// so Run could be fired in the gap between committing a move and the lock's cross-fade
        /// dropping raycasts — or after BattleEndedEvent had already locked the panel, on a
        /// pill still fading out. The page test is the same guard the hover handler uses: a
        /// pill belonging to a page that is no longer showing must not act.
        /// </summary>
        private void TakeRootAction(Action action)
        {
            if (_committed || _page != BattleCommandPage.Root) return;
            action?.Invoke();
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
            BuildTargetPills(candidates);
            ShowPage(BattleCommandPage.Targets);
            UiSound.Confirm();
        }

        private void Commit(int moveIndex)
        {
            // Second entry point after OnMoveChosen: a target pill can be clicked twice before
            // the lock's cross-fade has finished dropping raycasts.
            if (_committed) return;
            _committed = true;
// Through Lock rather than straight to the page, so committing also clears the back
            // stack. Remembering the move list here left the cancel control lit through the
            // resolution — refused, now, but a control that is visible and does nothing is its
            // own small lie.
            Lock();
            UiSound.Confirm();
            MoveChosen?.Invoke(moveIndex);
        }

        /// <summary>
        /// The ball command. Legality is decided here rather than greying the pill into
        /// unnavigability because pressing is how the rule is taught — see
        /// <see cref="SetBattleKind"/>. The commit itself is not latched here: like RUN, the
        /// press only raises the request and the presenter's commit is what locks the panel,
        /// so a capture the binding drops leaves the menu alive rather than soft-locked.
        /// </summary>
        private void OnCapturePressed()
        {
            if (_committed) return;
            if (_kind != BattleKind.Wild)
            {
                UiSound.Error();
                CaptureBlocked?.Invoke();
                return;
            }
            UiSound.Confirm();
            CaptureRequested?.Invoke();
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

        private void BuildTargetPills(List<CreatureInstance> candidates)
        {
            UiBuilder.ClearChildren(_targetList);
            _targetPills.Clear();

            var moveIndex = _pendingMoveIndex;
            for (var i = 0; i < candidates.Count; i++)
            {
                var creature = candidates[i];
                var fraction = creature.HpFraction;
                var pill = BuildPill(_targetList, UiServices.NameOf(creature), UiSprites.Dot(28),
                    UiPalette.Health(fraction), Mathf.CeilToInt(fraction * 100f) + "%",
                    () => Commit(moveIndex));
                _targetPills.Add(pill);

                var index = i;
                Hover(pill.Root.gameObject, () => SelectEntryFor(index, BattleCommandPage.Targets));
            }
        }

        // --------------------------------------------------------------- selection

        /// <summary>One navigable line on the current page.</summary>
        private struct Entry
        {
            public Action<bool> Select;
            public Action Take;
        }

        /// <summary>
        /// Rebuilds the cursor's list for the page now showing, and puts the cursor on the
        /// first line. On the move page, moves with no PP are skipped rather than merely
        /// greyed — a cursor that stops on a pill Enter will not take is a dead end the player
        /// has to discover by pressing.
        /// </summary>
        private void RebuildEntries()
        {
            _entries.Clear();

            switch (_page)
            {
                case BattleCommandPage.Root:
                    for (var i = 0; i < _rootPills.Count; i++)
                    {
                        var pill = _rootPills[i];
                        _entries.Add(new Entry { Select = pill.SetSelected, Take = pill.Take });
                    }
                    break;

                case BattleCommandPage.Moves:
                {
                    var anyUsable = false;
                    for (var i = 0; i < _moveButtons.Count; i++)
                    {
                        if (_moveButtons[i].gameObject.activeSelf && _moveButtons[i].IsUsable) anyUsable = true;
                        _moveButtons[i].SetSelected(false);
                    }
                    for (var i = 0; i < _moveButtons.Count; i++)
                    {
                        var button = _moveButtons[i];
                        if (!button.gameObject.activeSelf) continue;
                        // When nothing has PP left every pill becomes navigable again, so the
                        // player can still see the list instead of facing a menu with no cursor.
                        if (anyUsable && !button.IsUsable) continue;
                        _entries.Add(new Entry { Select = button.SetSelected, Take = button.Take });
                    }
                    break;
                }

                case BattleCommandPage.Targets:
                    for (var i = 0; i < _targetPills.Count; i++)
                    {
                        var pill = _targetPills[i];
                        _entries.Add(new Entry { Select = pill.SetSelected, Take = pill.Take });
                    }
                    break;
            }

            _selected = 0;
            ApplySelection();
        }

        private void ApplySelection()
        {
            for (var i = 0; i < _entries.Count; i++) _entries[i].Select?.Invoke(i == _selected);
        }

        /// <summary>
        /// Moves the cursor to the entry a pointer entered, ignoring hovers that arrive while
        /// another page is showing. Pills are only deactivated a frame after a page change, so
        /// a pointer resting over the move list can otherwise drag the root page's cursor.
        /// </summary>
        private void SelectEntryFor(int listIndex, BattleCommandPage page)
        {
            if (_page != page) return;
            SelectIndex(page == BattleCommandPage.Moves ? EntryForMove(listIndex) : listIndex);
        }

        /// <summary>Maps a move slot index onto its position in the cursor's list, or -1.</summary>
        private int EntryForMove(int moveIndex)
        {
            if (moveIndex < 0 || moveIndex >= _moveButtons.Count) return -1;
            var target = _moveButtons[moveIndex];
            if (!target.gameObject.activeSelf) return -1;

            var shown = 0;
            for (var i = 0; i < _moveButtons.Count; i++)
                if (_moveButtons[i].gameObject.activeSelf) shown++;
            // RebuildEntries drops the unusable pills unless none is usable, in which case it
            // keeps them all. Both cases have to be mirrored here or hovering a spent move
            // would move the cursor onto its neighbour.
            var allNavigable = _entries.Count == shown;

            var cursor = 0;
            for (var i = 0; i < _moveButtons.Count; i++)
            {
                var button = _moveButtons[i];
                if (!button.gameObject.activeSelf) continue;
                if (!allNavigable && !button.IsUsable) continue;
                if (button == target) return cursor;
                cursor++;
            }
            return -1;
        }

        // ------------------------------------------------------------------- pages

        /// <summary>Cross-fades to a page, optionally pushing the current one onto the back stack.</summary>
        private void ShowPage(BattleCommandPage page, bool remember = true)
        {
            if (remember && _page != BattleCommandPage.Locked && _page != page) _history.Push(_page);
            _page = page;

            Fade(ref _rootFade, _rootPage, page == BattleCommandPage.Root);
            Fade(ref _moveFade, _movePage, page == BattleCommandPage.Moves);
            Fade(ref _targetFade, _targetPage, page == BattleCommandPage.Targets);

            RebuildEntries();

            if (_caption != null && page != BattleCommandPage.Locked)
            {
                _caption.anchoredPosition = new Vector2(0f, StackHeight(page) + StackGap);
            }

            // Shown only where there is somewhere to go back to. A cancel control on the root
            // page would suggest the player can decline to take a turn, which they cannot.
            if (_backButton != null) _backButton.gameObject.SetActive(_history.Count > 0);

            if (_promptLabel == null) return;
            _promptLabel.SetText(page switch
            {
                BattleCommandPage.Root => _active != null
                    ? Loc.Pick($"What will {UiServices.NameOf(_active)} do?",
                               $"{Josa.WithTopic(UiServices.NameOf(_active))} 무엇을 할까?")
                    : Loc.Pick("Choose an action.", "무엇을 할까?"),
                BattleCommandPage.Moves => Loc.Pick("Choose a move.", "어떤 기술을 쓸까?"),
                BattleCommandPage.Targets => Loc.Pick("Choose a target.", "누구에게 쓸까?"),
                _ => string.Empty,
            });
        }

        /// <summary>
        /// How tall the stack on a page is. Computed rather than measured off the layout group
        /// because the group has not run yet on the frame a page is shown, and reading
        /// <c>rect.height</c> here would place the caption using the previous page's size.
        /// </summary>
        private float StackHeight(BattleCommandPage page)
        {
            switch (page)
            {
                case BattleCommandPage.Moves:
                {
                    var shown = 0;
                    for (var i = 0; i < _moveButtons.Count; i++)
                        if (_moveButtons[i].gameObject.activeSelf) shown++;
                    return Rows(shown, MoveButtonView.RowHeight);
                }
                case BattleCommandPage.Targets:
                    return Rows(_targetPills.Count, PillHeight);
                default:
                    return Rows(_rootPills.Count, PillHeight);
            }
        }

        private static float Rows(int count, float rowHeight) =>
            count <= 0 ? 0f : count * rowHeight + (count - 1) * PillSpacing;

        /// <summary>
        /// Cross-fades one page. The handle is that page's own, so Fight → Back → Fight inside
        /// the 0.12s hide kills it rather than letting it switch the move list off underneath a
        /// panel that has already navigated back to it.
        /// </summary>
        private static void Fade(ref TweenHandle fade, CanvasGroup group, bool visible)
        {
            if (group == null) return;
            group.interactable = visible;
            group.blocksRaycasts = visible;
            UiTween.FadeActive(ref fade, group, visible, visible ? 0.18f : 0.12f,
                visible ? Ease.OutCubic : Ease.InCubic);
        }

        // -------------------------------------------------------------------- build

        /// <summary>
        /// Builds the whole command surface as one right-hand column. The caller anchors it to
        /// the bottom-right corner at <see cref="PanelWidth"/> × <see cref="PanelHeight"/>.
        /// </summary>
        public static BattleCommandPanel Build(Transform parent)
        {
            var root = UiBuilder.Rect("CommandPanel", parent);
            var panel = root.gameObject.AddComponent<BattleCommandPanel>();

            // No shell behind the column. The pills are already opaque slabs with their own
            // shadows, and a second panel behind them would be one more rectangle covering the
            // diorama for no information — the thing that made the old menu read as debug UI.

            // The caption is anchored to the bottom and lifted by whatever the current stack is
            // tall, rather than pinned to the top of the column. The three pages are different
            // heights, and a fixed caption would float forty pixels clear of the root menu and
            // sit tight against the move list — a caption has to belong to the stack it labels.
            var caption = UiBuilder.Rect("Caption", root, false);
            UiBuilder.Anchor(caption, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                Vector2.zero, new Vector2(0f, CaptionHeight));
            panel._caption = caption;

            panel._backButton = BuildBackButton(caption, () => panel.GoBack());
            panel._backButton.gameObject.SetActive(false);

            panel._promptLabel = UiBuilder.Text("Prompt", caption, string.Empty, UiTextRole.Secondary,
                UiPalette.TextPrimary, TextAlignmentOptions.Right);
            panel._promptLabel.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Stretch(panel._promptLabel.rectTransform);
            // Stops short of the right edge by the cancel control's column, whether or not the
            // control is showing: a caption that shifted sideways between the root page and the
            // move page would read as the layout jumping every time the player pressed a key.
            panel._promptLabel.rectTransform.offsetMax = new Vector2(-(BackWidth + 14f), 0f);
            // The caption floats over the scene rather than over a panel, so it needs the glyph
            // shadow to survive a bright tile behind it.
            UiType.ApplyShadow(panel._promptLabel);

            // Fills the column. Every stack inside is bottom-aligned, so the caption floating
            // above them needs no reserved band of its own.
            var pages = UiBuilder.Rect("Pages", root);

            panel._rootPage = BuildRootPage(pages, panel);
            panel._movePage = BuildMovePage(pages, panel);
            panel._targetPage = BuildTargetPage(pages, panel);

            panel.ShowPage(BattleCommandPage.Locked, false);
            return panel;
        }

        /// <summary>
        /// A page's stack. Bottom-aligned and right-aligned so a list of two targets and a list
        /// of four moves both grow upward from the same corner instead of floating.
        /// </summary>
        private static RectTransform BuildStack(string name, Transform parent)
        {
            var stack = UiBuilder.Rect(name, parent);
            UiBuilder.Vertical(stack, PillSpacing, null, TextAnchor.LowerRight,
                expandWidth: false, expandHeight: false);
            return stack;
        }

        private static CanvasGroup BuildRootPage(Transform parent, BattleCommandPanel panel)
        {
            var page = UiBuilder.Rect("RootPage", parent);
            var group = UiBuilder.Group(page, 0f, false, false);
            var stack = BuildStack("Stack", page);

            // Icons are borrowed from the procedural type-glyph family rather than drawn fresh:
            // a fist for the attack, a plain disc for the thrown ball, the ring-and-dot that
            // reads as a ball for the party, a bracket for the bag, a wing for the retreat.
            // One authored stroke weight across all five, which is what a purpose-made set
            // would have had to match anyway.
            panel._rootPills.Add(BuildPill(stack, Loc.Pick("Fight", "싸운다"),
                UiTypeIcons.Get(ElementType.Fighting, 48), UiPalette.ScannerAmber, null,
                () => panel.TakeRootAction(() =>
                {
                    UiSound.Confirm();
                    panel.ShowPage(BattleCommandPage.Moves);
                })));

            // Second slot, directly under FIGHT: in a wild battle the throw is the other
            // decision the player is actually weighing, and burying it under the bag — where
            // the games keep it — would cost a submenu this slice does not have. Routed
            // through TakeRootAction like every root pill, so it obeys the same "only while
            // the root menu is genuinely the player's" guard; the trainer-battle refusal
            // still plays, because the guard tests the page, not the battle kind.
            panel._capturePill = BuildPill(stack, Loc.Pick("Throw Ball", "볼 던지기"),
                UiSprites.Dot(48), UiPalette.Negative, null,
                () => panel.TakeRootAction(() => panel.OnCapturePressed()));
            panel._rootPills.Add(panel._capturePill);

            panel._rootPills.Add(BuildPill(stack, Loc.Pick("Party", "포켓몬"),
                UiTypeIcons.Get(ElementType.Normal, 48), UiPalette.Positive, null,
                () => panel.TakeRootAction(() =>
                {
                    UiSound.Confirm();
                    panel.PartyRequested?.Invoke();
                })));

            panel._rootPills.Add(BuildPill(stack, Loc.Pick("Bag", "가방"),
                UiSprites.Bracket(48, 6), UiPalette.ScannerCyan, null,
                () => panel.TakeRootAction(() =>
                {
                    UiSound.Confirm();
                    panel.BagRequested?.Invoke();
                })));

            panel._rootPills.Add(BuildPill(stack, Loc.Pick("Run", "도망친다"),
                UiTypeIcons.Get(ElementType.Flying, 48), UiPalette.TextMuted, null,
                () => panel.TakeRootAction(() =>
                {
                    UiSound.Confirm();
                    panel.RunRequested?.Invoke();
                })));

            for (var i = 0; i < panel._rootPills.Count; i++)
            {
                var index = i;
                Hover(panel._rootPills[i].Root.gameObject,
                    () => panel.SelectEntryFor(index, BattleCommandPage.Root));
            }

            return group;
        }

        private static CanvasGroup BuildMovePage(Transform parent, BattleCommandPanel panel)
        {
            var page = UiBuilder.Rect("MovePage", parent);
            var group = UiBuilder.Group(page, 0f, false, false);
            panel._moveList = BuildStack("List", page);
            return group;
        }

        private static CanvasGroup BuildTargetPage(Transform parent, BattleCommandPanel panel)
        {
            var page = UiBuilder.Rect("TargetPage", parent);
            var group = UiBuilder.Group(page, 0f, false, false);
            panel._targetList = BuildStack("List", page);
            return group;
        }

        /// <summary>
        /// Moves the cursor when the pointer enters an element.
        ///
        /// Reuses <see cref="StartMenuRowHover"/>, which does exactly this one job. It sits
        /// under UI/Overworld only because that is where it was first needed and renaming it
        /// is not this file's to do; duplicating it here would be worse.
        /// </summary>
        private static void Hover(GameObject target, Action onEnter)
        {
            var hover = target.GetComponent<StartMenuRowHover>();
            if (hover == null) hover = target.AddComponent<StartMenuRowHover>();
            hover.Bind(onEnter);
        }

        /// <summary>
        /// One command pill: icon square, label, optional trailing figure, and a chevron that
        /// appears outside its left edge when the cursor is on it.
        /// </summary>
        private static CommandPill BuildPill(Transform parent, string label, Sprite icon, Color accent,
            string trailing, Action onTake)
        {
            var root = UiBuilder.Rect("Pill_" + label, parent, false);
            var pill = new CommandPill { Root = root, Accent = accent, OnTake = onTake };
            pill.Sizing = UiBuilder.Size(root, preferredWidth: PillWidth, preferredHeight: PillHeight,
                minHeight: PillHeight);

            pill.Panel = UiBuilder.Image("Panel", root, UiSprites.Panel(PillRadius),
                UiPalette.Backdrop, Image.Type.Sliced, true);
            UiBuilder.Stretch(pill.Panel.rectTransform);

            pill.Rim = UiBuilder.Image("Rim", root, UiSprites.Frame(PillRadius, 2), Color.white.WithAlpha(0.08f));
            UiBuilder.Stretch(pill.Rim.rectTransform);

            var chevron = UiBuilder.Image("Chevron", root, UiSprites.Chevron(30), UiPalette.ScannerAmber,
                Image.Type.Simple);
            chevron.preserveAspect = true;
            // Centre pivot, not an edge pivot: a RectTransform rotates about its pivot, and a
            // right-edge pivot would swing the glyph up above the pill instead of leaving it
            // beside it. Centred at -25 puts its 26px box in the span [-38, -12].
            UiBuilder.Anchor(chevron.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-25f, 0f), new Vector2(26f, 26f));
            // The one chevron sprite is drawn pointing up; -90° turns it to point right.
            chevron.rectTransform.localEulerAngles = new Vector3(0f, 0f, -90f);
            pill.Chevron = chevron.rectTransform;
            chevron.gameObject.SetActive(false);

            var row = UiBuilder.Rect("Row", root);
            UiBuilder.Horizontal(row, 16f, new RectOffset(18, 24, 0, 0), TextAnchor.MiddleLeft);

            var square = UiBuilder.Rect("Icon", row, false);
            UiBuilder.Size(square, preferredWidth: 52f, preferredHeight: 52f, minWidth: 52f, minHeight: 52f);
            pill.IconBackground = UiBuilder.Image("Bg", square, UiSprites.Panel(13), accent.WithAlpha(0.20f));
            UiBuilder.Stretch(pill.IconBackground.rectTransform);
            pill.IconGlyph = UiBuilder.Image("Glyph", square, icon, accent, Image.Type.Simple);
            pill.IconGlyph.preserveAspect = true;
            UiBuilder.Stretch(pill.IconGlyph.rectTransform, 9f);

            pill.Label = UiBuilder.Text("Label", row, label, UiTextRole.Heading, UiPalette.TextPrimary);
            pill.Label.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(pill.Label.rectTransform, flexibleWidth: 1f, minWidth: 120f);

            if (!string.IsNullOrEmpty(trailing))
            {
                pill.Trailing = UiBuilder.Text("Trailing", row, trailing, UiTextRole.Numeric,
                    UiPalette.TextSecondary, TextAlignmentOptions.Right);
                UiBuilder.Size(pill.Trailing.rectTransform, preferredWidth: 90f, minWidth: 90f);
            }

            UiButtonMotion.Attach(root, PillRadius);
            UiBuilder.Button("Take", root, pill.Panel, pill.Take);
            pill.SetSelected(false);
            return pill;
        }

        /// <summary>
        /// The cancel control, flush with the right edge of the caption band so it lines up
        /// with the column of pills underneath it rather than floating off to their left.
        /// </summary>
        private static RectTransform BuildBackButton(Transform parent, Action onClick)
        {
            var root = UiBuilder.Rect("Back", parent, false);
            UiBuilder.Anchor(root, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0f, 0f), new Vector2(BackWidth, 40f));

            var background = UiBuilder.Image("Bg", root, UiSprites.Pill(40), UiPalette.Backdrop.WithAlpha(0.86f),
                Image.Type.Sliced, true);
            UiBuilder.Stretch(background.rectTransform);

            var chevron = UiBuilder.Image("Chevron", root, UiSprites.Chevron(24, false, 4), UiPalette.TextSecondary,
                Image.Type.Simple);
            chevron.preserveAspect = true;
            UiBuilder.Anchor(chevron.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(23f, 0f), new Vector2(18f, 18f));
            chevron.rectTransform.localEulerAngles = new Vector3(0f, 0f, 90f);

            // Names the key as well as the action: this panel takes the keyboard before it
            // takes the mouse, and a cancel the player has to guess at is a cancel they will
            // look for with Escape and then give up on.
            var label = UiBuilder.Text("Label", root, Loc.Pick("Back  X", "취소  X"), UiTextRole.Caption,
                UiPalette.TextSecondary, TextAlignmentOptions.Center);
            UiBuilder.Stretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(36f, 0f);

            UiButtonMotion.Attach(root, 20);
            UiBuilder.Button("Click", root, background, onClick);
            return root;
        }

        /// <summary>
        /// A root-menu or target pill. A plain class rather than a MonoBehaviour because it
        /// carries no lifecycle of its own — it is a handle onto graphics the panel built.
        /// </summary>
        private sealed class CommandPill
        {
            public RectTransform Root;
            public Image Panel;
            public Image Rim;
            public Image IconBackground;
            public Image IconGlyph;
            public TextMeshProUGUI Label;
            public TextMeshProUGUI Trailing;
            public RectTransform Chevron;
            public LayoutElement Sizing;
            public Color Accent;
            public Action OnTake;

            /// <summary>
            /// False for a command the current battle refuses — today only the ball in a
            /// trainer fight. A refused pill stays navigable so pressing it can explain
            /// itself; it merely never earns the amber fill that promises Enter will work.
            /// </summary>
            public bool Enabled = true;

            public void Take() => OnTake?.Invoke();

            /// <summary>
            /// The selected pill is filled with the accent, is wider, and grows a chevron.
            /// Three cues, because the player's eyes are on the creatures and a single change
            /// of shade at the edge of vision is not a cue at all.
            ///
            /// A refused pill under the cursor keeps its grey face and gets only a muted
            /// chevron and the width: the cursor must still be visible there, but the amber
            /// fill is the promise that Enter will take, and breaking that promise once is
            /// how a player learns to stop trusting the highlight everywhere.
            /// </summary>
            public void SetSelected(bool selected)
            {
                var lit = selected && Enabled;
                if (Panel != null)
                    Panel.color = lit ? UiPalette.ScannerAmber : UiPalette.Backdrop.WithAlpha(Enabled ? 0.88f : 0.66f);
                if (Rim != null)
                    Rim.color = lit ? Color.white.WithAlpha(0.35f)
                        : Color.white.WithAlpha(selected ? 0.28f : Enabled ? 0.08f : 0.04f);
                if (Label != null)
                    Label.color = lit ? UiPalette.TextOnAccent : Enabled ? UiPalette.TextPrimary : UiPalette.TextMuted;
                if (Trailing != null)
                    Trailing.color = lit ? UiPalette.TextOnAccent.WithAlpha(0.75f)
                        : Enabled ? UiPalette.TextSecondary : UiPalette.TextMuted;
                // On the amber fill the icon tile has to invert too, or it reads as a hole.
                if (IconBackground != null)
                    IconBackground.color = lit
                        ? UiPalette.TextOnAccent.WithAlpha(0.16f)
                        : Accent.WithAlpha(Enabled ? 0.20f : 0.10f);
                if (IconGlyph != null)
                    IconGlyph.color = lit ? UiPalette.TextOnAccent : Accent.WithAlpha(Enabled ? 1f : 0.45f);
                if (Chevron != null)
                {
                    Chevron.gameObject.SetActive(selected);
                    var chevronImage = Chevron.GetComponent<Image>();
                    if (chevronImage != null)
                        chevronImage.color = Enabled ? UiPalette.ScannerAmber : UiPalette.TextMuted;
                }
                if (Sizing != null)
                    Sizing.preferredWidth = selected ? PillSelectedWidth : PillWidth;
            }
        }
    }
}
