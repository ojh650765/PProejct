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
    /// <summary>
    /// The in-battle party list: one row per member, opened either because the player chose
    /// POKéMON from the command menu or because their active creature fainted and somebody
    /// has to replace it.
    ///
    /// It stands in the command column's corner rather than as a full-screen party screen,
    /// because mid-battle the question is only ever "who goes in" — moves, stats and items
    /// are the overworld party screen's job, and covering the two creatures with a summary
    /// screen to answer a one-line question would hide the fight the choice is about.
    ///
    /// The same surface serves both entrances, distinguished by one flag. In FORCED mode
    /// there is no cancel: the games do not let you decline to send the next Pokémon, and a
    /// back control that silently did nothing would read as broken, so the control is not
    /// drawn and the cancel key answers with the refusal sound instead of silence.
    ///
    /// Input follows <see cref="BattleCommandPanel"/>'s selected-index model exactly — the
    /// panel owns the cursor, hover moves it, arrows move it, Enter takes it — for the same
    /// reason the panel does: Unity's EventSystem navigation makes the first arrow press
    /// after a mouse click behave differently from one after a keyboard-only turn, and two
    /// menus in the same corner with two cursor behaviours would feel like two games.
    /// Fainted and already-fielded rows stay navigable rather than being skipped: unlike a
    /// spent move, a fainted partner is information the player needs to sit with for a
    /// moment, and pressing on it earns the refusal sound rather than a dead cursor.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattlePartyPicker : MonoBehaviour
    {
        /// <summary>Column width, including the gutter the selection chevron hangs in.</summary>
        public const float PanelWidth = RowSelectedWidth + ChevronGutter + 8f;

        /// <summary>Column height at a full party of six, plus the caption band.</summary>
        public const float PanelHeight = CaptionHeight + StackGap + 6f * RowHeight + 5f * RowSpacing;

        private const float ChevronGutter = 38f;
        private const float CaptionHeight = 44f;
        private const float StackGap = 12f;
        private const float RowHeight = 96f;
        private const float RowSpacing = 10f;
        private const float RowWidth = 520f;
        private const float RowSelectedWidth = 566f;
        private const float BackWidth = 138f;
        private const int RowRadius = 22;

        [Header("Chrome")]
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _caption;
        [SerializeField] private TextMeshProUGUI _promptLabel;
        [SerializeField] private RectTransform _backButton;
        [SerializeField] private RectTransform _list;

        private readonly List<PartyRow> _rows = new List<PartyRow>(6);

        private bool _open;
        private bool _forced;
        private bool _taken;
        private int _selected;

        /// <summary>Raised with the chosen party index. The picker has already closed itself.</summary>
        public Action<int> Picked;

        /// <summary>Raised when the player backs out. Never raised in forced mode.</summary>
        public Action Cancelled;

        /// <summary>True while the picker is showing and owning input.</summary>
        public bool IsOpen => _open;

        // ---------------------------------------------------------------- lifecycle

        /// <summary>
        /// Shows the picker over the party as it stands right now. Rows are rebuilt on every
        /// open rather than diffed — the list is at most six entries and an open is a player
        /// gesture, so correctness-by-reconstruction costs nothing perceptible and can never
        /// show last turn's HP.
        /// </summary>
        public void Open(IReadOnlyList<CreatureInstance> party, int activeIndex, bool forced)
        {
            _forced = forced;
            _taken = false;
            BuildRows(party, activeIndex);
            if (_rows.Count == 0) return;

            if (_promptLabel != null)
            {
                _promptLabel.SetText(forced
                    ? Loc.Pick("Choose your next Pokémon!", "다음 포켓몬을 내보내자!")
                    : Loc.Pick("Who will you switch in?", "누구와 교체할까?"));
            }
            if (_backButton != null) _backButton.gameObject.SetActive(!forced);
            if (_caption != null)
            {
                var stack = _rows.Count * RowHeight + (_rows.Count - 1) * RowSpacing;
                _caption.anchoredPosition = new Vector2(0f, stack + StackGap);
            }

            // Cursor starts on the first row that could actually be taken, so in the common
            // case — the lead just fainted — Enter-Enter sends the next healthy member
            // without a single arrow press.
            _selected = 0;
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Selectable) { _selected = i; break; }
            }
            ApplySelection();

            _open = true;
            gameObject.SetActive(true);
            if (_group != null)
            {
                _group.interactable = true;
                _group.blocksRaycasts = true;
                UiTween.Fade(_group, 1f, 0.18f);
            }
            UiSound.MenuOpen();
        }

        /// <summary>
        /// Hides the picker. Raises nothing — the callers decide what a close means — and is
        /// a no-op while already closed, so every "make sure this is gone" path can call it
        /// without double-playing the close sound.
        /// </summary>
        public void Close()
        {
            if (!_open) return;
            _open = false;
            if (_group == null) { gameObject.SetActive(false); return; }
            _group.interactable = false;
            _group.blocksRaycasts = false;
            UiTween.Fade(_group, 0f, 0.14f, Ease.InCubic, 0f, () =>
            {
                if (this != null) gameObject.SetActive(false);
            });
            UiSound.MenuClose();
        }

        // ------------------------------------------------------------------- input

        /// <summary>
        /// Polls the keyboard and pad directly, mirroring <see cref="BattleCommandPanel"/>'s
        /// bindings key for key — a player who has learnt the command menu has already
        /// learnt this one. Mouse input arrives through the rows' own Buttons.
        /// </summary>
        private void Update()
        {
            if (!_open || _taken) return;

            // Same EventSystem discipline as the command panel: clicking a row selects its
            // GameObject, and left alone Unity's own navigation would drive a second cursor.
            var events = EventSystem.current;
            if (events != null && events.currentSelectedGameObject != null)
                events.SetSelectedGameObject(null);

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

            if (down) MoveCursor(1);
            if (up) MoveCursor(-1);
            if (confirm) TakeRow(_selected);
            if (back) RequestCancel();
        }

        private void MoveCursor(int delta)
        {
            if (_rows.Count == 0) return;
            var next = ((_selected + delta) % _rows.Count + _rows.Count) % _rows.Count;
            SelectIndex(next);
        }

        private void SelectIndex(int index)
        {
            if (index < 0 || index >= _rows.Count || index == _selected) return;
            _selected = index;
            ApplySelection();
            UiSound.Navigate();
        }

        private void ApplySelection()
        {
            for (var i = 0; i < _rows.Count; i++) _rows[i].SetSelected(i == _selected);
        }

        private void TakeRow(int index)
        {
            if (_taken || index < 0 || index >= _rows.Count) return;
            var row = _rows[index];

            if (!row.Selectable)
            {
                // The refusal is audible rather than logged: the row already says why it
                // cannot go — greyed, marked fainted or fielded — and a log line repeating
                // what the player is looking at would be noise.
                UiSound.Error();
                return;
            }

            // Guarded before anything else fires, so a double press during the close fade
            // cannot commit two switches.
            _taken = true;
            UiSound.Confirm();
            Close();
            Picked?.Invoke(row.PartyIndex);
        }

        private void RequestCancel()
        {
            if (_taken) return;
            if (_forced)
            {
                // The battle cannot continue without a replacement, so the cancel key gets
                // the refusal sound rather than nothing — silence on a pressed key reads as
                // the game hanging, which after a faint is exactly the wrong moment.
                UiSound.Error();
                return;
            }
            _taken = true;
            UiSound.Cancel();
            Close();
            Cancelled?.Invoke();
        }

        // -------------------------------------------------------------------- rows

        private void BuildRows(IReadOnlyList<CreatureInstance> party, int activeIndex)
        {
            UiBuilder.ClearChildren(_list);
            _rows.Clear();
            if (party == null) return;

            for (var i = 0; i < party.Count; i++)
            {
                var creature = party[i];
                if (creature == null) continue;

                var row = BuildRow(_list, creature, i, i == activeIndex);
                var listIndex = _rows.Count;
                Hover(row.Root.gameObject, () => { if (_open && !_taken) SelectIndex(listIndex); });
                UiBuilder.Button("Take", row.Root, row.Panel, () => TakeRow(listIndex));
                _rows.Add(row);
            }
        }

        /// <summary>
        /// One member's row: portrait tile, name, level, a live-coloured health bar with the
        /// exact figures, and — where it applies — the one word explaining why the row cannot
        /// be taken. The pill chrome deliberately matches <see cref="BattleCommandPanel"/>'s
        /// so the picker reads as another page of the same menu, not a new device.
        /// </summary>
        private PartyRow BuildRow(Transform parent, CreatureInstance creature, int partyIndex, bool isActive)
        {
            var fainted = creature.IsFainted;
            var selectable = !fainted && !isActive;
            var name = UiServices.NameOf(creature);

            var root = UiBuilder.Rect("Row_" + partyIndex, parent, false);
            var row = new PartyRow { Root = root, PartyIndex = partyIndex, Selectable = selectable };
            row.Sizing = UiBuilder.Size(root, preferredWidth: RowWidth, preferredHeight: RowHeight,
                minHeight: RowHeight);

            row.Panel = UiBuilder.Image("Panel", root, UiSprites.Panel(RowRadius),
                UiPalette.Backdrop.WithAlpha(0.88f), Image.Type.Sliced, true);
            UiBuilder.Stretch(row.Panel.rectTransform);

            row.Rim = UiBuilder.Image("Rim", root, UiSprites.Frame(RowRadius, 2), Color.white.WithAlpha(0.08f));
            UiBuilder.Stretch(row.Rim.rectTransform);

            var chevron = UiBuilder.Image("Chevron", root, UiSprites.Chevron(30), UiPalette.ScannerAmber,
                Image.Type.Simple);
            chevron.preserveAspect = true;
            UiBuilder.Anchor(chevron.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-25f, 0f), new Vector2(26f, 26f));
            chevron.rectTransform.localEulerAngles = new Vector3(0f, 0f, -90f);
            row.Chevron = chevron;
            chevron.gameObject.SetActive(false);

            var body = UiBuilder.Rect("Body", root);
            UiBuilder.Horizontal(body, 14f, new RectOffset(16, 22, 12, 12), TextAnchor.MiddleLeft);

            // Portrait tile. The type glyph stands in when the art registry has not landed,
            // for the same reason MoveButtonView falls back to type marks: an empty square
            // looks like a bug, a glyph looks like a decision.
            var square = UiBuilder.Rect("Icon", body, false);
            UiBuilder.Size(square, preferredWidth: 64f, preferredHeight: 64f, minWidth: 64f, minHeight: 64f);
            UiServices.TypesOf(creature.SpeciesId, out var primaryType, out _);
            var accent = UiPalette.Type(primaryType);
            row.Accent = accent;
            row.IconBackground = UiBuilder.Image("Bg", square, UiSprites.Panel(14), accent.WithAlpha(0.20f));
            UiBuilder.Stretch(row.IconBackground.rectTransform);
            var portrait = UiServices.PortraitOf(creature.SpeciesId);
            row.IconGlyph = UiBuilder.Image("Glyph", square,
                portrait != null ? portrait : UiTypeIcons.Get(primaryType, 48),
                portrait != null ? Color.white : accent, Image.Type.Simple);
            row.IconGlyph.preserveAspect = true;
            UiBuilder.Stretch(row.IconGlyph.rectTransform, portrait != null ? 4f : 12f);

            // Two stacked lines beside the tile: identity above, health below.
            var lines = UiBuilder.Rect("Lines", body);
            UiBuilder.Vertical(lines, 6f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(lines, flexibleWidth: 1f);

            var nameRow = UiBuilder.Rect("NameRow", lines);
            UiBuilder.Horizontal(nameRow, 10f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(nameRow, preferredHeight: 32f, minHeight: 32f, flexibleWidth: 1f);

            row.Name = UiBuilder.Text("Name", nameRow, name, UiTextRole.Body, UiPalette.TextPrimary);
            row.Name.fontStyle = FontStyles.Bold;
            row.Name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(row.Name.rectTransform, flexibleWidth: 1f, minWidth: 90f);

            // The one word that explains an untakeable row, sitting where the eye lands
            // after the name. "빈사" is the games' own word for the fainted state — the log
            // says 쓰러졌다 in the moment, the party screen says 빈사 about the state.
            if (fainted || isActive)
            {
                row.Note = UiBuilder.Text("Note", nameRow,
                    fainted ? Loc.Pick("Fainted", "빈사") : Loc.Pick("In battle", "배틀 중"),
                    UiTextRole.Caption,
                    fainted ? UiPalette.Negative : UiPalette.ScannerCyan,
                    TextAlignmentOptions.Right);
                row.Note.textWrappingMode = TextWrappingModes.NoWrap;
                UiBuilder.Size(row.Note.rectTransform, preferredWidth: 84f, minWidth: 84f);
            }

            row.Level = UiBuilder.Text("Level", nameRow, "Lv. " + creature.Level, UiTextRole.Numeric,
                UiPalette.TextSecondary, TextAlignmentOptions.Right);
            UiBuilder.Size(row.Level.rectTransform, preferredWidth: 92f, minWidth: 92f);

            var healthRow = UiBuilder.Rect("HealthRow", lines);
            UiBuilder.Horizontal(healthRow, 12f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(healthRow, preferredHeight: 24f, minHeight: 24f, flexibleWidth: 1f);

            // Set immediately rather than animated: the picker shows standing state, and six
            // bars sweeping up from zero every time it opens would look like healing.
            var fraction = creature.HpFraction;
            row.HealthBar = AnimatedBar.Build("Health", healthRow, 10f, UiPalette.Health(fraction));
            row.HealthBar.SetImmediate(fraction);
            row.HealthBar.SetColorImmediate(UiPalette.Health(fraction));

            row.HealthText = UiBuilder.Text("Hp", healthRow,
                $"{creature.CurrentHp}<size=78%><color=#FFFFFF70>/{creature.MaxHp}</color></size>",
                UiTextRole.Numeric, UiPalette.TextPrimary, TextAlignmentOptions.Right);
            row.HealthText.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(row.HealthText.rectTransform, preferredWidth: 104f, minWidth: 104f);

            UiButtonMotion.Attach(root, RowRadius);
            row.ApplyResting();
            return row;
        }

        private static void Hover(GameObject target, Action onEnter)
        {
            var hover = target.GetComponent<StartMenuRowHover>();
            if (hover == null) hover = target.AddComponent<StartMenuRowHover>();
            hover.Bind(onEnter);
        }

        // -------------------------------------------------------------------- build

        /// <summary>
        /// Builds the picker as one right-hand column. The caller anchors it to the
        /// bottom-right corner at <see cref="PanelWidth"/> × <see cref="PanelHeight"/> —
        /// the same corner as the command panel it stands in for — and it starts hidden.
        /// </summary>
        public static BattlePartyPicker Build(Transform parent)
        {
            var root = UiBuilder.Rect("PartyPicker", parent);
            var picker = root.gameObject.AddComponent<BattlePartyPicker>();
            picker._group = UiBuilder.Group(root, 0f, false, false);

            // Caption band, lifted above the stack exactly as the command panel lifts its
            // prompt — see BattleCommandPanel.Build for why it is bottom-anchored.
            var caption = UiBuilder.Rect("Caption", root, false);
            UiBuilder.Anchor(caption, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                Vector2.zero, new Vector2(0f, CaptionHeight));
            picker._caption = caption;

            picker._backButton = BuildBackButton(caption, picker.RequestCancel);
            picker._backButton.gameObject.SetActive(false);

            picker._promptLabel = UiBuilder.Text("Prompt", caption, string.Empty, UiTextRole.Secondary,
                UiPalette.TextPrimary, TextAlignmentOptions.Right);
            picker._promptLabel.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Stretch(picker._promptLabel.rectTransform);
            picker._promptLabel.rectTransform.offsetMax = new Vector2(-(BackWidth + 14f), 0f);
            UiType.ApplyShadow(picker._promptLabel);

            var stack = UiBuilder.Rect("List", root);
            UiBuilder.Vertical(stack, RowSpacing, null, TextAnchor.LowerRight,
                expandWidth: false, expandHeight: false);
            picker._list = stack;

            root.gameObject.SetActive(false);
            return picker;
        }

        /// <summary>The cancel control, same shape and position as the command panel's.</summary>
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

            var label = UiBuilder.Text("Label", root, Loc.Pick("Back  X", "취소  X"), UiTextRole.Caption,
                UiPalette.TextSecondary, TextAlignmentOptions.Center);
            UiBuilder.Stretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(36f, 0f);

            UiButtonMotion.Attach(root, 20);
            UiBuilder.Button("Click", root, background, onClick);
            return root;
        }

        /// <summary>
        /// One row's handles. A plain class rather than a MonoBehaviour, exactly like the
        /// command panel's pills: it carries no lifecycle, only graphics the picker built.
        /// </summary>
        private sealed class PartyRow
        {
            public RectTransform Root;
            public Image Panel;
            public Image Rim;
            public Image IconBackground;
            public Image IconGlyph;
            public Image Chevron;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Level;
            public TextMeshProUGUI Note;
            public TextMeshProUGUI HealthText;
            public AnimatedBar HealthBar;
            public LayoutElement Sizing;
            public Color Accent;

            public int PartyIndex;
            public bool Selectable;

            /// <summary>
            /// Selection uses the command pills' three cues — fill, width, chevron — so the
            /// cursor feels identical across both menus. An untakeable row under the cursor
            /// keeps its grey face and gets a muted chevron: the cursor must still be
            /// visible there, but amber is the colour of "Enter will work" and lying about
            /// that with a full highlight is how a player learns to distrust the highlight.
            /// </summary>
            public void SetSelected(bool selected)
            {
                if (selected && Selectable)
                {
                    if (Panel != null) Panel.color = UiPalette.ScannerAmber;
                    if (Rim != null) Rim.color = Color.white.WithAlpha(0.35f);
                    if (Name != null) Name.color = UiPalette.TextOnAccent;
                    if (Level != null) Level.color = UiPalette.TextOnAccent.WithAlpha(0.75f);
                    if (HealthText != null) HealthText.color = UiPalette.TextOnAccent;
                    if (IconBackground != null) IconBackground.color = UiPalette.TextOnAccent.WithAlpha(0.16f);
                    if (Chevron != null)
                    {
                        Chevron.color = UiPalette.ScannerAmber;
                        Chevron.gameObject.SetActive(true);
                    }
                    if (Sizing != null) Sizing.preferredWidth = RowSelectedWidth;
                    return;
                }

                ApplyResting();
                if (selected)
                {
                    if (Rim != null) Rim.color = Color.white.WithAlpha(0.28f);
                    if (Chevron != null)
                    {
                        Chevron.color = UiPalette.TextMuted;
                        Chevron.gameObject.SetActive(true);
                    }
                    if (Sizing != null) Sizing.preferredWidth = RowSelectedWidth;
                }
            }

            /// <summary>The unselected face. Untakeable rows are dimmed as a whole.</summary>
            public void ApplyResting()
            {
                var muted = !Selectable;
                if (Panel != null) Panel.color = UiPalette.Backdrop.WithAlpha(muted ? 0.66f : 0.88f);
                if (Rim != null) Rim.color = Color.white.WithAlpha(muted ? 0.04f : 0.08f);
                if (Name != null) Name.color = muted ? UiPalette.TextMuted : UiPalette.TextPrimary;
                if (Level != null) Level.color = muted ? UiPalette.TextMuted : UiPalette.TextSecondary;
                if (HealthText != null) HealthText.color = muted ? UiPalette.TextMuted : UiPalette.TextPrimary;
                if (IconBackground != null) IconBackground.color = Accent.WithAlpha(muted ? 0.10f : 0.20f);
                if (IconGlyph != null) IconGlyph.color = IconGlyph.color.WithAlpha(muted ? 0.45f : 1f);
                if (Chevron != null) Chevron.gameObject.SetActive(false);
                if (Sizing != null) Sizing.preferredWidth = RowWidth;
            }
        }
    }
}
