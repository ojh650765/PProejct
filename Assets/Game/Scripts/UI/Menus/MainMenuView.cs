using System;
using System.Collections.Generic;
using PokeLab.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// The title screen: the first thing the game shows, and the only screen from which every
    /// mode is reachable.
    ///
    /// Until now the build started on Town.unity and the player was simply standing in the
    /// plaza — there was no way to choose anything, because there was only one thing to choose.
    /// That stops being true the moment there is a second mode, and this is the screen that
    /// admits it.
    ///
    /// <b>Two columns, not one.</b> The left column is the modes; the right panel is who you
    /// are (the trainer card and the online state). A single centred list is the obvious layout
    /// and it is wrong here, because half the screens behind this menu are about an account and
    /// a roster — the player has to be able to see, without opening anything, whether they are
    /// signed in and what they are signed in as.
    ///
    /// <b>It is drawn as a shop window, not as a device readout.</b> Every gameplay screen in
    /// this game is the scanner: near-black, slate panels, a hairline of cyan, which is right
    /// for a HUD read at a glance over a lit 3D scene. On the title screen it read as a
    /// diagnostic tool, which is a promise about the game that the game does not keep. So this
    /// one is a lit navy field with panes of blue glass layered over it, white bold type, and
    /// one bright accent per state. <see cref="UiJuice"/> owns the kit, and the gacha, account
    /// and confirmation screens draw from the same one.
    ///
    /// <b>Selection is an object that travels, not a state some row is in.</b> One near-white
    /// pill and one lime chevron exist for the whole list, and they move between rows with an
    /// overshoot and a settle; the row they arrive at drops its own fill so the pill becomes its
    /// surface and its text inverts to dark, and the rows either side are shoved away, tilted a
    /// degree and pushed back in scale. The version before this swapped a colour onto one row
    /// and switched a chevron off one and on at another, which is a teleport — nothing crossed
    /// the gap, so the eye had to re-find the cursor after every key press.
    ///
    /// <b>And the press is the payoff.</b> Taking a row punches the pill, throws a ring in the
    /// row's colour off it, and washes the screen to that colour before the action fires, so the
    /// thing that was pressed becomes the next screen rather than being cut away from. The
    /// entrance is seen once per visit; moving the cursor and pressing is what a player does
    /// dozens of times, and that is where the motion budget belongs.
    ///
    /// The view owns no game state and knows what no entry means. It is handed a list of rows,
    /// each of which may be disabled with a reason, and it reports which one was taken. Whether
    /// 이어하기 is available, whether the account exists, what a mode does — all of that is the
    /// presenter's business.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuView : MonoBehaviour
    {
        /// <summary>
        /// One place on the trainer card's team row: a party member, or an empty slot.
        ///
        /// A slot with an empty <see cref="Name"/> is drawn as an empty socket rather than
        /// skipped, because six sockets with two filled says "four to go" and two cards on
        /// their own say nothing at all.
        /// </summary>
        public readonly struct TeamSlot
        {
            public readonly string Name;
            public readonly int Level;
            /// <summary>Health as a 0-1 fraction.</summary>
            public readonly float Health;
            /// <summary>Progress toward the next level, 0-1.</summary>
            public readonly float Experience;
            /// <summary>Identity colour, normally the creature's primary type.</summary>
            public readonly Color Accent;

            public TeamSlot(string name, int level, float health, float experience, Color accent)
            {
                Name = name;
                Level = level;
                Health = health;
                Experience = experience;
                Accent = accent;
            }
        }

        /// <summary>A row: what it says, the line under it, its accent, and whether it is live.</summary>
        public readonly struct Entry
        {
            public readonly string Label;
            public readonly string Detail;
            public readonly Color Accent;
            public readonly bool Enabled;

            public Entry(string label, string detail, Color accent, bool enabled = true)
            {
                Label = label;
                Detail = detail;
                Accent = accent;
                Enabled = enabled;
            }
        }

        /// <summary>Everything one row needs to be restyled when the cursor moves onto or off it.</summary>
        private sealed class Row
        {
            public RectTransform Lift;      // scales and slides on selection
            public UiPane Pane;
            public TextMeshProUGUI Label;
            public TextMeshProUGUI Detail;
            public Image IconTile;
            public Image IconGlyph;
            public Color Accent;
            public bool Enabled;
        }

        private const float ColumnWidth = 720f;
        private const float ColumnPad = 40f;
        private const float RowWidth = ColumnWidth - ColumnPad * 2f;
        private const float RowHeight = 96f;
        private const float RowSpacing = 12f;
        private const float RowsTop = -232f;
        private const float CardWidth = 620f;
        private const float CardHeight = 640f;
        private const float CellWidth = 172f;
        private const float CellHeight = 100f;
        private const float CellGap = 12f;
        private const float CardPad = 40f;

        /// <summary>Raised with the index of the row the player took. Disabled rows never raise it.</summary>
        public Action<int> Chosen;

        private readonly List<Row> _rows = new List<Row>();

        /// <summary>Everything one team slot needs when the roster is handed over later.</summary>
        private sealed class Cell
        {
            public UiPane Pane;
            public Image Glyph;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Level;
            public Image Health;
            public Image Experience;
            public float BarWidth;
        }

        private TextMeshProUGUI _cardName;
        private TextMeshProUGUI _cardStatus;
        private RectTransform _hintHost;
        private RectTransform _pill;
        private RectTransform _cursor;
        private Image _pillFill;
        private Image _pillGlow;
        private readonly List<Cell> _cells = new List<Cell>();
        private int _highlighted;
        private int _count;
        private bool _taking;

        /// <summary>Which row the cursor is on.</summary>
        public int Highlighted => _highlighted;

        /// <summary>
        /// Builds the screen for these rows.
        ///
        /// Rebuilt on every call rather than built once and repopulated, for the same reason
        /// <see cref="StartMenuView"/> is: the rows change with the save and with the account —
        /// 이어하기 appears when there is a file, 온라인 대전 stops being greyed out when the
        /// player signs in — and a menu that is built once has to grow a second, separate code
        /// path to say so.
        /// </summary>
        public void Build(string title, string subtitle, IReadOnlyList<Entry> entries)
        {
            var root = transform as RectTransform;
            if (root == null)
            {
                Debug.LogError("MainMenuView must live on a RectTransform under a Canvas.", this);
                return;
            }

            UiBuilder.ClearChildren(root);
            _rows.Clear();
            _taking = false;
            UiBuilder.Stretch(root);

            UiJuice.Backdrop(root);

            var safe = UiBuilder.SafeArea(root, 64f, 44f);

            var column = UiBuilder.Rect("Column", safe, false);
            UiBuilder.Anchor(column, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                Vector2.zero, new Vector2(ColumnWidth, 0f));
            UiJuice.Pane("Glass", column, UiPalette.AceDepth.WithAlpha(0.78f), 26, true, true, true,
                UiPalette.AceRim, 220);

            BuildHeader(column, title, subtitle);
            BuildRows(column, entries);
            BuildCard(safe);
            BuildFooter(safe);

            _count = entries.Count;
            Highlight(FirstEnabled(), false);

            UiJuice.PopIn(column, 0f, new Vector2(-140f, 0f), 0.5f);
        }

        // ---------------------------------------------------------------- the header

        private void BuildHeader(Transform column, string title, string subtitle)
        {
            var block = UiBuilder.Rect("Header", column, false);
            UiBuilder.Anchor(block, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(ColumnPad, -34f), new Vector2(-ColumnPad * 2f, 176f));

            // The glowing mark beside the wordmark. Two images: the glyph, and a soft light
            // behind it, because a flat cyan shape on navy is a sticker and a lit one is a lamp.
            var lamp = UiBuilder.Image("Lamp", block, UiSprites.Glow(128, 1.7f),
                UiPalette.AceCyan.WithAlpha(0.5f), Image.Type.Simple);
            UiBuilder.Anchor(lamp.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(30f, -52f), new Vector2(150f, 150f));

            // The lamp breathes. Between key presses the title screen used to be a completely
            // still image, and a still image is the one thing the gacha reveal never is.
            UiIdle.Attach(lamp.rectTransform, UiIdleMode.Pulse, 0.07f, 3.4f);

            var bars = UiBuilder.Image("Mark", block, UiSprites.BarsGlyph(96, 3, 0.15f),
                UiPalette.AceText, Image.Type.Simple);
            UiBuilder.Anchor(bars.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(30f, -52f), new Vector2(58f, 58f));

            // Metric is 96pt and this box is 124: UiType gives every label overflowMode
            // Ellipsis, and TMP draws NOTHING AT ALL when the first line is taller than its
            // rect. A title in a box its own size is one bad font metric away from an empty
            // screen, so every text box on this screen carries real headroom.
            var name = UiBuilder.Text("Name", block, title, UiTextRole.Metric,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(76f, 6f), new Vector2(-76f, 124f));

            var rule = UiBuilder.Image("Rule", block, UiSprites.FadeRule(256, 0.55f),
                UiPalette.AceCyan.WithAlpha(0.85f), Image.Type.Simple);
            UiBuilder.Anchor(rule.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(4f, -122f), new Vector2(-4f, 4f));

            var under = UiBuilder.Text("Subtitle", block, subtitle, UiTextRole.Overline,
                UiPalette.AceCyan, TextAlignmentOptions.Left);
            UiBuilder.Anchor(under.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(6f, -136f), new Vector2(-6f, 30f));
        }

        // ------------------------------------------------------------------- the rows

        private void BuildRows(Transform column, IReadOnlyList<Entry> entries)
        {
            var height = entries.Count * (RowHeight + RowSpacing);

            var stack = UiBuilder.Rect("Modes", column, false);
            UiBuilder.Anchor(stack, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(ColumnPad, RowsTop), new Vector2(RowWidth, height));
            UiBuilder.Vertical(stack, RowSpacing);

            // The selection is ONE object that travels, built before the rows so it sits behind
            // them, and the cursor is one object built after them so it sits in front. See
            // BuildSelection for why that sandwich is the whole trick.
            BuildSelection(stack);

            for (var i = 0; i < entries.Count; i++)
            {
                var index = i;
                var entry = entries[i];

                var row = UiBuilder.Rect($"Row_{i}", stack, false);
                UiBuilder.Size(row, RowWidth, RowHeight, flexibleWidth: 1f);

                // Three nested rects, and each one owns exactly one kind of motion. The row
                // itself belongs to the layout group and must not be moved by hand; the body
                // carries the entrance; the lift carries selection and the press. Sharing one
                // rect between the entrance tween and the selection tween is how a menu ends up
                // with a row that flew in and then never landed.
                var body = UiBuilder.Rect("Body", row);
                var lift = UiBuilder.Rect("Lift", body);

                // NO per-row bloom. There used to be one here, and once the selection became a
                // pill travelling BEHIND the rows it became a lime-tinted sheet drawn in front
                // of that pill — which is why the white pill came back from the first capture
                // looking grey-green. The bloom belongs to the pill and travels with it.

                // A disabled row is a different material rather than merely a dimmer one.
                // Dimming alone reads as a style and the player tries it anyway; glass at a
                // third of the opacity reads as a door that is not open yet.
                var pane = UiJuice.Pane("Pane", lift,
                    entry.Enabled ? UiPalette.AceGlass.WithAlpha(0.72f) : UiPalette.AceGlass.WithAlpha(0.30f),
                    18, false, true, true,
                    entry.Enabled ? UiPalette.AceRim : UiPalette.AceRim.WithAlpha(0.07f), 96);

                var tile = UiBuilder.Image("IconTile", lift, UiSprites.Panel(14),
                    entry.Accent.WithAlpha(entry.Enabled ? 0.28f : 0.10f));
                UiBuilder.Anchor(tile.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(48f, 0f), new Vector2(58f, 58f));

                var glyph = UiBuilder.Image("IconGlyph", lift, UiSprites.BallGlyph(128, 10),
                    entry.Enabled ? entry.Accent : UiPalette.AceTextFaint, Image.Type.Simple);
                UiBuilder.Anchor(glyph.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(48f, 0f), new Vector2(38f, 38f));

                var label = UiBuilder.Text("Label", lift, entry.Label, UiTextRole.Heading,
                    entry.Enabled ? UiPalette.AceText : UiPalette.AceTextFaint,
                    TextAlignmentOptions.Left);
                UiBuilder.Anchor(label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                    new Vector2(0f, 1f), new Vector2(96f, -8f), new Vector2(-118f, 46f));

                var detail = UiBuilder.Text("Detail", lift, entry.Detail, UiTextRole.Caption,
                    entry.Enabled ? UiPalette.AceTextDim : UiPalette.AceTextFaint,
                    TextAlignmentOptions.Left);
                UiBuilder.Anchor(detail.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                    new Vector2(0f, 1f), new Vector2(96f, -56f), new Vector2(-118f, 28f));

                UiBuilder.Button($"Take_{i}", row, pane.Fill, () =>
                {
                    // A click on a disabled row must not drag the cursor onto it: a cursor
                    // parked somewhere Enter does nothing is a menu that looks broken.
                    if (!entry.Enabled) { UiSound.Error(); return; }
                    Highlight(index, true);
                    Take();
                });

                _rows.Add(new Row
                {
                    Lift = lift,
                    Pane = pane,
                    Label = label,
                    Detail = detail,
                    IconTile = tile,
                    IconGlyph = glyph,
                    Accent = entry.Accent,
                    Enabled = entry.Enabled,
                });

                UiJuice.PopIn(body, 0.12f + i * 0.05f, new Vector2(-180f, 0f));
            }

            // Built last so it draws over every row: while the pill slides behind the glass,
            // the chevron slides across the front of it, and the eye has one bright object to
            // follow the whole way.
            _cursor = UiJuice.Cursor(stack, 46f);
            UiBuilder.IgnoreLayout(_cursor);
            UiBuilder.Anchor(_cursor, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0.5f, 0.5f), CursorAt(0), new Vector2(46f, 46f));

            // Faded, not slid, for the same reason as the pill: Highlight owns this rect's
            // position from the first frame.
            var group = UiBuilder.Group(_cursor, 0f, false, false);
            UiTween.Fade(group, 1f, 0.36f, Ease.OutCubic, 0.3f);
            UiJuice.PopScale(_cursor, 0.3f, 0.5f, 0.44f);
        }

        /// <summary>
        /// The travelling selection: one near-white pill, its lime bloom, and the chevron.
        ///
        /// <b>Why one object instead of a highlight per row.</b> The selection used to be a
        /// colour swapped onto whichever row owned it, and a chevron switched off on one row and
        /// on again on another. That is a teleport: nothing crosses the gap, so the eye has to
        /// re-find the cursor after every press of an arrow key. Here there is a single pill and
        /// a single chevron and they physically travel, overshooting and settling, which is what
        /// makes the selection read as an object the player is pushing around rather than as a
        /// state some row is in.
        ///
        /// <b>The pill sits behind the rows on purpose.</b> Every row is a pane of translucent
        /// glass, so a bright shape passing underneath them is visible as a light moving behind
        /// frosted panels — and when it arrives, the row it lands on drops its own fill to
        /// nothing, so the pill becomes that row's surface with the label and the accent tile
        /// still drawn on top of it. One object, correct depth, no per-row copies.
        /// </summary>
        private void BuildSelection(Transform stack)
        {
            _pill = UiBuilder.Rect("Selection", stack, false);
            UiBuilder.IgnoreLayout(_pill);
            UiBuilder.Anchor(_pill, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0.5f, 0.5f), PillAt(0), new Vector2(RowWidth + 12f, RowHeight + 8f));

            _pillGlow = UiBuilder.Image("Glow", _pill, UiSprites.Shadow(20, 30),
                UiPalette.AceLime.WithAlpha(0.34f));
            UiBuilder.Stretch(_pillGlow.rectTransform, -18f);
            // The bloom breathes even when nothing is happening. It is the smallest possible
            // answer to "the screen is a still image between key presses".
            UiIdle.Attach(_pillGlow.rectTransform, UiIdleMode.Pulse, 0.035f, 2.1f);

            var pane = UiJuice.Pane("Pane", _pill, UiPalette.AceSelect, 18, false, true, true,
                UiPalette.AceLime.WithAlpha(0.9f), 96);
            _pillFill = pane.Fill;

            // Faded and scaled in, never SLID in. PopIn animates anchoredPosition, and this rect
            // is the one thing on the screen whose anchoredPosition means something the moment
            // the screen exists: Highlight tweens it to the first enabled row on the same frame,
            // and two tweens writing the same field would have landed the pill back on row zero
            // when the slower of them finished.
            var group = UiBuilder.Group(_pill, 0f, false, false);
            UiTween.Fade(group, 1f, 0.4f, Ease.OutCubic, 0.24f);
            UiJuice.PopScale(_pill, 0.24f, 0.74f, 0.46f);
        }

        private static Vector2 PillAt(int index) =>
            new Vector2(RowWidth * 0.5f + 14f, -(RowHeight * 0.5f + index * (RowHeight + RowSpacing)));

        private static Vector2 CursorAt(int index) =>
            new Vector2(-16f, -(RowHeight * 0.5f + index * (RowHeight + RowSpacing)));

        // ------------------------------------------------------------------ the card

        /// <summary>
        /// The trainer card: who the game thinks you are before you press anything, and what
        /// you have.
        ///
        /// <b>The team row is half the point of the pane.</b> Without it the card was a name, a
        /// two-line status and two hundred pixels of empty glass — a panel sized for content it
        /// did not have. The reference this screen is drawn against puts a party of six along
        /// the right pane, each member carrying a green health bar with a thinner cyan
        /// experience bar directly beneath it and its level in the corner, and that row is what
        /// makes a menu look like a game rather than like a launcher. Six sockets are drawn
        /// whether or not they are filled, because "four to go" is information and two lonely
        /// cards are not.
        /// </summary>
        private void BuildCard(Transform safe)
        {
            var holder = UiBuilder.Rect("Card", safe, false);
            UiBuilder.Anchor(holder, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f), new Vector2(-40f, 0f), new Vector2(CardWidth, CardHeight));

            UiJuice.Pane("Glass", holder, UiPalette.AceGlass.WithAlpha(0.66f), 24, true, true, true,
                UiPalette.AceRim, 160);

            // A cyan bar along the top edge, inset so it reads as part of the pane rather than
            // as a border on it. It is the same "this is live" accent the header rule uses.
            var stripe = UiBuilder.Image("Stripe", holder, UiSprites.Pill(8), UiPalette.AceCyan);
            UiBuilder.Anchor(stripe.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(-320f, 8f));

            // A slow rotor behind the ball. A ring would have been invisible — rotational
            // symmetry means a spinning ring does not appear to spin at all — so the mark under
            // it has spokes, and turns once every forty seconds at an alpha you notice only
            // because it never stops.
            var rotor = UiBuilder.Image("Rotor", holder, UiSprites.SpeedLines(256, 18, 0.30f),
                UiPalette.AceCyan.WithAlpha(0.085f), Image.Type.Simple);
            UiBuilder.Anchor(rotor.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(66f, -76f), new Vector2(132f, 132f));
            UiIdle.Attach(rotor.rectTransform, UiIdleMode.Spin, 360f, 40f);

            var pip = UiJuice.Ball(holder, 54f);
            UiBuilder.Anchor(pip, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 0.5f),
                new Vector2(66f, -76f), new Vector2(54f, 54f));
            UiIdle.Attach(pip, UiIdleMode.Bob, 4f, 2.6f, 0.35f);

            var overline = UiBuilder.Text("Overline", holder, "TRAINER", UiTextRole.Overline,
                UiPalette.AceCyan, TextAlignmentOptions.Left);
            UiBuilder.Anchor(overline.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(106f, -62f), new Vector2(-146f, 30f));

            _cardName = UiBuilder.Text("Name", holder, "—", UiTextRole.Title,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(_cardName.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(40f, -122f), new Vector2(-80f, 68f));

            var rule = UiBuilder.Image("Rule", holder, UiSprites.FadeRule(256, 0.6f),
                new Color(1f, 1f, 1f, 0.22f), Image.Type.Simple);
            UiBuilder.Anchor(rule.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(CardPad, -200f), new Vector2(-CardPad * 2f, 3f));

            _cardStatus = UiBuilder.Text("Status", holder, "", UiTextRole.Secondary,
                UiPalette.AceTextDim, TextAlignmentOptions.TopLeft);
            UiBuilder.Anchor(_cardStatus.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(CardPad, -216f), new Vector2(-CardPad * 2f, 108f));

            BuildTeam(holder);

            UiJuice.PopIn(holder, 0.24f, new Vector2(190f, 0f), 0.5f);
        }

        /// <summary>The party row: two ranks of three sockets, under their own label.</summary>
        private void BuildTeam(Transform holder)
        {
            _cells.Clear();

            var rule = UiBuilder.Image("TeamRule", holder, UiSprites.FadeRule(256, 0.6f),
                new Color(1f, 1f, 1f, 0.16f), Image.Type.Simple);
            UiBuilder.Anchor(rule.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(CardPad, -336f), new Vector2(-CardPad * 2f, 3f));

            var label = UiBuilder.Text("TeamLabel", holder, Loc.Pick("TEAM", "팀"),
                UiTextRole.Overline, UiPalette.AceCyan, TextAlignmentOptions.Left);
            UiBuilder.Anchor(label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(CardPad, -352f), new Vector2(-CardPad * 2f, 30f));

            for (var i = 0; i < 6; i++)
            {
                var column = i % 3;
                var rank = i / 3;
                var x = CardPad + column * (CellWidth + CellGap);
                var y = -392f - rank * (CellHeight + CellGap);
                _cells.Add(BuildCell(holder, i, new Vector2(x, y)));
            }

            SetTeam(null);
        }

        private static Cell BuildCell(Transform holder, int index, Vector2 at)
        {
            var root = UiBuilder.Rect($"Slot_{index}", holder, false);
            UiBuilder.Anchor(root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                at, new Vector2(CellWidth, CellHeight));

            var pane = UiJuice.Pane("Pane", root, UiPalette.AceGlass.WithAlpha(0.55f), 14,
                false, true, true, UiPalette.AceRim.WithAlpha(0.12f), 44);

            var glyph = UiBuilder.Image("Glyph", root, UiSprites.BallGlyph(128, 10),
                UiPalette.AceTextFaint, Image.Type.Simple);
            UiBuilder.Anchor(glyph.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(26f, -24f), new Vector2(28f, 28f));

            var name = UiBuilder.Text("Name", root, "—", UiTextRole.Caption,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(46f, -8f), new Vector2(-56f, 30f));
            name.characterSpacing = 0f;

            var barWidth = CellWidth - 24f;
            var health = UiJuice.Meter("Hp", root, new Vector2(12f, -44f), barWidth, 8f, 0f,
                UiPalette.Health(1f));
            var experience = UiJuice.Meter("Exp", root, new Vector2(12f, -56f), barWidth, 5f, 0f,
                UiPalette.AceCyan);

            var level = UiBuilder.Text("Level", root, "Lv. —", UiTextRole.Caption,
                UiPalette.AceTextDim, TextAlignmentOptions.Left);
            UiBuilder.Anchor(level.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(12f, -66f), new Vector2(110f, 30f));

            return new Cell
            {
                Pane = pane,
                Glyph = glyph,
                Name = name,
                Level = level,
                Health = health,
                Experience = experience,
                BarWidth = barWidth,
            };
        }

        /// <summary>
        /// The control hints along the bottom.
        ///
        /// It used to be one white sentence inside a bright glass capsule in the corner, which
        /// made a permanent, unchanging line the second-loudest object on the screen. The
        /// reference draws these as a muted strip of key-caps and labels — boxed glyphs the
        /// player recognises by shape rather than reads — so that is what
        /// <see cref="UiJuice.HintBar"/> builds, and the capsule is gone.
        /// </summary>
        private void BuildFooter(Transform safe)
        {
            _hintHost = UiBuilder.Rect("Hints", safe, false);
            UiBuilder.Anchor(_hintHost, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 0f), new Vector2(-40f, 14f), new Vector2(620f, 34f));

            UiJuice.PopIn(_hintHost, 0.4f, new Vector2(0f, -60f), 0.44f);
        }

        // --------------------------------------------------------------------- state

        /// <summary>Who the card says you are. Called again whenever the account state moves.</summary>
        public void SetCard(string trainerName, string status)
        {
            if (_cardName != null) _cardName.text = string.IsNullOrEmpty(trainerName) ? "—" : trainerName;
            if (_cardStatus != null) _cardStatus.text = status ?? "";
        }

        /// <summary>
        /// Who is on your team. Six slots; pass fewer and the rest are drawn empty, pass null
        /// and all six are.
        /// </summary>
        public void SetTeam(IReadOnlyList<TeamSlot> slots)
        {
            for (var i = 0; i < _cells.Count; i++)
            {
                var cell = _cells[i];
                var filled = slots != null && i < slots.Count && !string.IsNullOrEmpty(slots[i].Name);

                if (!filled)
                {
                    UiJuice.Recolour(cell.Pane, UiPalette.AceGlass.WithAlpha(0.24f),
                        UiPalette.AceRim.WithAlpha(0.06f));
                    if (cell.Glyph != null) cell.Glyph.color = UiPalette.AceTextFaint.WithAlpha(0.28f);
                    if (cell.Name != null)
                    {
                        cell.Name.text = "—";
                        cell.Name.color = UiPalette.AceTextFaint;
                    }
                    if (cell.Level != null)
                    {
                        cell.Level.text = "Lv. —";
                        cell.Level.color = UiPalette.AceTextFaint;
                    }
                    Fill(cell.Health, 0f, cell.BarWidth);
                    Fill(cell.Experience, 0f, cell.BarWidth);
                    continue;
                }

                var slot = slots[i];
                UiJuice.Recolour(cell.Pane, UiPalette.AceGlass.WithAlpha(0.62f),
                    slot.Accent.WithAlpha(0.42f));
                if (cell.Glyph != null) cell.Glyph.color = slot.Accent;
                if (cell.Name != null)
                {
                    cell.Name.text = slot.Name;
                    cell.Name.color = UiPalette.AceText;
                }
                if (cell.Level != null)
                {
                    cell.Level.text = "Lv. " + slot.Level;
                    cell.Level.color = UiPalette.AceTextDim;
                }
                if (cell.Health != null) cell.Health.color = UiPalette.Health(slot.Health);
                Fill(cell.Health, slot.Health, cell.BarWidth);
                Fill(cell.Experience, slot.Experience, cell.BarWidth);
            }
        }

        private static void Fill(Image bar, float fraction, float width)
        {
            if (bar == null) return;
            bar.rectTransform.sizeDelta =
                new Vector2(Mathf.Max(0f, width * Mathf.Clamp01(fraction)), 0f);
        }

        /// <summary>The control hints along the bottom, as key glyphs paired with what they do.</summary>
        public void SetHints(IReadOnlyList<UiJuice.Hint> hints)
        {
            if (_hintHost == null) return;
            UiBuilder.ClearChildren(_hintHost);

            var any = hints != null && hints.Count > 0;
            _hintHost.gameObject.SetActive(any);
            if (!any) return;

            var bar = UiJuice.HintBar(_hintHost, hints);
            UiBuilder.Stretch(bar);
        }

        /// <summary>
        /// The same hints, written as one line.
        ///
        /// Kept because the presenter has always called this and an empty string is how it
        /// hides the strip behind a modal. The line is split on the run of spaces that already
        /// separates the pairs, and each pair on its first space, so "Enter 선택" becomes a
        /// key-cap reading Enter next to the word 선택. Anything that does not split that way
        /// is drawn as a plain label with no cap, which is the honest fallback rather than a
        /// guess. <see cref="SetHints"/> is the direct route and wants no parsing at all.
        /// </summary>
        public void SetFooter(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { SetHints(null); return; }

            var hints = new List<UiJuice.Hint>();
            foreach (var chunk in text.Split(new[] { "   " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = chunk.Trim();
                if (segment.Length == 0) continue;

                var space = segment.IndexOf(' ');
                if (space <= 0) hints.Add(new UiJuice.Hint("", segment));
                else hints.Add(new UiJuice.Hint(segment.Substring(0, space),
                                                segment.Substring(space + 1).Trim()));
            }

            SetHints(hints);
        }

        /// <summary>Moves the cursor by <paramref name="delta"/>, skipping disabled rows and wrapping.</summary>
        public void Move(int delta)
        {
            if (_count <= 0 || delta == 0) return;

            // Steps one row at a time and keeps stepping over anything disabled, rather than
            // landing on it and refusing: a cursor that sits on a row that cannot be taken is
            // indistinguishable from a menu that has stopped responding.
            var step = delta > 0 ? 1 : -1;
            var index = _highlighted;
            for (var guard = 0; guard < _count; guard++)
            {
                index = ((index + step) % _count + _count) % _count;
                if (index < _rows.Count && _rows[index].Enabled) { Highlight(index, true); return; }
            }
        }

        /// <summary>
        /// Takes the highlighted row, if it is one that can be taken.
        ///
        /// <b>The press is a payoff, not a cut.</b> It used to be a squash and then the scene
        /// simply changed, which spends the animation budget on the entrance and none of it on
        /// the moment the player actually acts. Now the pill flares to white and punches, a ring
        /// in the row's own colour leaves it, and the screen washes to that colour before the
        /// action fires — so what the player pressed visibly becomes the next screen instead of
        /// being replaced by it. A menu cannot manufacture stakes the way the gacha can; it can
        /// at least make the last thing it does the loudest.
        ///
        /// <c>_taking</c> guards the gap: a second Enter inside that half second would otherwise
        /// start a scene load twice.
        /// </summary>
        public void Take()
        {
            if (_count <= 0 || _taking) return;
            if (_highlighted < 0 || _highlighted >= _rows.Count) return;
            if (!_rows[_highlighted].Enabled) { UiSound.Error(); return; }

            _taking = true;
            var index = _highlighted;
            var row = _rows[index];
            UiSound.Confirm();

            if (_pill != null)
            {
                UiTween.Punch(_pill, 0.13f, 0.4f);
                UiJuice.Shockwave(_pill, row.Accent.WithAlpha(0.6f), RowWidth * 0.86f, 1.7f, 0.55f,
                    0f, 10);
            }
            if (_pillGlow != null) Tint(_pillGlow, row.Accent.WithAlpha(0.75f), 0.12f);
            // The pill blows out to pure white for an instant. It is the frame that makes the
            // press feel like it hit something.
            if (_pillFill != null) Tint(_pillFill, Color.white, 0.08f);

            UiJuice.Squash(row.Lift);
            Wash(row.Accent, () =>
            {
                _taking = false;
                Chosen?.Invoke(index);
            });
        }

        /// <summary>
        /// Floods the screen with a colour, runs <paramref name="then"/> under it, and lifts.
        ///
        /// It has to lift rather than simply stay: three of these rows load a scene and take the
        /// whole canvas with them, but 대전 모드 only swaps the row list and 계정 opens a panel
        /// on top — a wash that never faded would be a coloured sheet parked over both of those
        /// forever. Fading back out turns it into a flash in those cases and costs nothing in
        /// the cases where the scene really did change.
        /// </summary>
        private void Wash(Color colour, Action then)
        {
            var root = transform as RectTransform;
            if (root == null || !UiTween.MotionEnabled) { then?.Invoke(); return; }

            var sheet = UiBuilder.Image("Wash", root, UiSprites.Solid(), colour, Image.Type.Simple);
            UiBuilder.Stretch(sheet.rectTransform);
            sheet.rectTransform.SetAsLastSibling();
            var group = UiBuilder.Group(sheet.rectTransform, 0f, false, false);

            UiTween.Fade(group, 0.92f, 0.24f, Ease.InCubic, 0f, () =>
            {
                then?.Invoke();
                UiTween.Fade(group, 0f, 0.34f, Ease.OutCubic, 0.02f, () =>
                {
                    if (sheet != null) Destroy(sheet.gameObject);
                });
            });
        }

        private int FirstEnabled()
        {
            for (var i = 0; i < _rows.Count; i++) if (_rows[i].Enabled) return i;
            return 0;
        }

        /// <summary>
        /// Moves the selection and restyles every row.
        ///
        /// <b>Three things happen, not one.</b> The pill and the chevron travel to the new row,
        /// overshooting and settling. The row they land on drops its own fill so the pill
        /// becomes its surface, and its text inverts to dark. And the rows either side are
        /// shoved away from it, tilted a degree and pushed back a little in scale — because
        /// juice is objects affecting each other, and a highlight that lands in a stack of rows
        /// that do not notice reads as a swapped colour rather than as a thing arriving.
        /// </summary>
        private void Highlight(int index, bool audible)
        {
            if (_rows.Count == 0) return;
            index = Mathf.Clamp(index, 0, _rows.Count - 1);
            var moved = index != _highlighted;
            _highlighted = index;

            if (_pill != null)
            {
                UiTween.AnchoredMove(_pill, PillAt(index), 0.3f, Ease.OutBack);
                // Only on an actual move: punching the pill every time the screen rebuilds
                // would make the menu twitch whenever the account state changed behind it.
                if (moved) UiTween.Punch(_pill, 0.055f, 0.32f);
            }
            if (_cursor != null) UiTween.AnchoredMove(_cursor, CursorAt(index), 0.34f, Ease.OutBack);

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var selected = i == _highlighted;
                var neighbour = Mathf.Abs(i - _highlighted) == 1;

                if (row.Pane.IsValid)
                {
                    UiJuice.Recolour(row.Pane,
                        !row.Enabled ? UiPalette.AceGlass.WithAlpha(0.30f)
                        // Transparent, not white: the travelling pill behind it is the white.
                        : selected ? UiPalette.AceSelect.WithAlpha(0f)
                        : UiPalette.AceGlass.WithAlpha(neighbour ? 0.62f : 0.72f),
                        !row.Enabled ? UiPalette.AceRim.WithAlpha(0.07f)
                        : selected ? UiPalette.AceRim.WithAlpha(0f)
                        : UiPalette.AceRim);
                }

                Tint(row.Label, !row.Enabled ? UiPalette.AceTextFaint
                    : selected ? UiPalette.AceInk : UiPalette.AceText);

                Tint(row.Detail, !row.Enabled ? UiPalette.AceTextFaint
                    : selected ? UiPalette.AceInk.WithAlpha(0.62f) : UiPalette.AceTextDim);

                Tint(row.IconTile, !row.Enabled ? row.Accent.WithAlpha(0.10f)
                    : selected ? row.Accent.WithAlpha(1f) : row.Accent.WithAlpha(0.28f));

                Tint(row.IconGlyph, !row.Enabled ? UiPalette.AceTextFaint
                    : selected ? UiPalette.AceInk.WithAlpha(0.8f) : row.Accent);

                if (row.Lift == null) continue;

                // Shoved away from the selected row, and tilted off it. Small numbers on
                // purpose — this is meant to be felt at the edge of vision, not read.
                var shove = i == _highlighted - 1 ? 8f : i == _highlighted + 1 ? -8f : 0f;
                var tilt = i == _highlighted - 1 ? 0.9f : i == _highlighted + 1 ? -0.9f : 0f;

                // The selected row does NOT scale. It used to, and the pill behind it did not,
                // so the label grew past its own highlight by four per cent. "Bigger" is
                // expressed by the pill overhanging the row instead, which is what the
                // reference does and which cannot drift out of register.
                UiTween.Scale(row.Lift, Vector3.one * (neighbour ? 0.985f : 1f), 0.24f, Ease.OutBack);
                UiTween.AnchoredMove(row.Lift, new Vector2(selected ? 14f : 0f, shove), 0.24f,
                    Ease.OutBack);
                Tilt(row.Lift, tilt);
            }

            if (audible && moved) UiSound.Navigate();
        }

        /// <summary>Tweens a rect's roll. UiTween has no rotation helper; this is that, locally.</summary>
        private static void Tilt(RectTransform rect, float degrees)
        {
            var from = rect.localEulerAngles.z;
            if (from > 180f) from -= 360f;
            if (Mathf.Approximately(from, degrees)) return;
            var target = rect;
            UiTween.Run(0.24f, t =>
            {
                if (target != null)
                    target.localRotation = Quaternion.Euler(0f, 0f, Mathf.LerpUnclamped(from, degrees, t));
            }, Ease.OutBack);
        }

        /// <summary>Tweens any graphic to a colour, guarding against the row having been torn down.</summary>
        private static void Tint(Graphic graphic, Color to, float seconds = 0.16f)
        {
            if (graphic == null) return;
            var target = graphic;
            UiTween.Color(target.color, to, seconds, c => { if (target != null) target.color = c; });
        }
    }
}
