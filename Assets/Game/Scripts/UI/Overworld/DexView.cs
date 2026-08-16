using System.Collections.Generic;
using PokeLab.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// The Pokédex, laid out the way the games lay it out.
    ///
    /// A titled bar across the top, the selected creature drawn large on the left, a numbered
    /// list running down the right with the cursor on one row, and the two counts that are the
    /// whole point of a dex along the bottom: how many seen, how many caught.
    ///
    /// It replaced a wall of text. The counts and the entries were all there, printed as lines
    /// in a paragraph — which is the information and none of the thing. A dex is a collection
    /// you look at, so it has to show the creature.
    ///
    /// The list is virtual in the sense that matters here: rows are built once, up to
    /// <see cref="VisibleRows"/>, and re-bound as the cursor moves. A dex that instantiated a
    /// row per species would build several hundred objects to show eight of them.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DexView : MonoBehaviour
    {
        /// <summary>One line of the list.</summary>
        public readonly struct Entry
        {
            public readonly int Number;
            public readonly string Name;
            public readonly bool Seen;
            public readonly bool Caught;
            public readonly Sprite Art;

            public Entry(int number, string name, bool seen, bool caught, Sprite art)
            {
                Number = number;
                Name = name;
                Seen = seen;
                Caught = caught;
                Art = art;
            }
        }

        private const int VisibleRows = 8;
        private const float RowHeight = 62f;

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<RectTransform> _rows = new List<RectTransform>();
        private readonly List<TextMeshProUGUI> _numbers = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _names = new List<TextMeshProUGUI>();
        private readonly List<Image> _balls = new List<Image>();
        private readonly List<Image> _highlights = new List<Image>();

        private Image _art;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _seenCount;
        private TextMeshProUGUI _caughtCount;

        private int _cursor;
        private int _scroll;

        /// <summary>Which entry the cursor is on.</summary>
        public int Cursor => _cursor;

        /// <summary>Builds the screen and fills it. Safe to call repeatedly.</summary>
        public void Show(string title, IReadOnlyList<Entry> entries, int seen, int caught)
        {
            Build();

            _entries.Clear();
            if (entries != null) _entries.AddRange(entries);

            _title.text = title;
            _seenCount.text = seen.ToString();
            _caughtCount.text = caught.ToString();

            _cursor = 0;
            _scroll = 0;
            Refresh();
        }

        /// <summary>Moves the cursor, scrolling the window to keep it in view.</summary>
        public void Move(int delta)
        {
            if (_entries.Count == 0) return;

            _cursor = Mathf.Clamp(_cursor + delta, 0, _entries.Count - 1);

            // The window follows the cursor rather than centring on it, so a slow walk down the
            // list does not make the whole list slide under a stationary cursor.
            if (_cursor < _scroll) _scroll = _cursor;
            else if (_cursor >= _scroll + VisibleRows) _scroll = _cursor - VisibleRows + 1;

            Refresh();
        }

        private void Refresh()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var index = _scroll + i;
                var has = index >= 0 && index < _entries.Count;
                _rows[i].gameObject.SetActive(has);
                if (!has) continue;

                var entry = _entries[index];
                _numbers[i].text = entry.Number.ToString("000");

                // An unseen entry shows its number and nothing else. That blank is the dex's
                // only real mechanic — printing the name of something you have never met
                // would give the collection away.
                _names[i].text = entry.Seen ? entry.Name : "----------";
                _names[i].color = entry.Seen ? UiPalette.TextPrimary : UiPalette.TextMuted;

                _balls[i].enabled = entry.Caught;
                _highlights[i].enabled = index == _cursor;
            }

            var selected = _cursor >= 0 && _cursor < _entries.Count ? _entries[_cursor] : default;
            _art.sprite = selected.Seen ? selected.Art : null;
            _art.enabled = _art.sprite != null;
        }

        private void Build()
        {
            if (_art != null) return;

            var root = transform as RectTransform;
            if (root == null)
            {
                Debug.LogError("DexView must live on a RectTransform under a Canvas.", this);
                return;
            }

            UiBuilder.Stretch(root);

            var frame = UiBuilder.Rect("Frame", root, false);
            UiBuilder.Anchor(frame, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1120f, 720f));
            var backing = UiBuilder.Panel("Backing", frame, UiPalette.Surface, 26);
            UiBuilder.Stretch(backing.rectTransform);

            // Title bar.
            var bar = UiBuilder.Panel("TitleBar", frame, UiPalette.SurfaceRaised, 20);
            UiBuilder.Anchor(bar.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(-40f, 74f));
            _title = UiBuilder.Text("Title", bar.rectTransform, string.Empty, UiTextRole.Title,
                UiPalette.ScannerCyan, TextAlignmentOptions.Center);
            UiBuilder.Stretch(_title.rectTransform);

            // The creature, drawn large on the left.
            var artFrame = UiBuilder.Rect("ArtFrame", frame, false);
            UiBuilder.Anchor(artFrame, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(24f, -110f), new Vector2(430f, 430f));
            var artWell = UiBuilder.Panel("Well", artFrame, UiPalette.SurfaceSunken, 18);
            UiBuilder.Stretch(artWell.rectTransform);
            var artEdge = UiBuilder.OutlinedPanel("Edge", artFrame, new Color(0f, 0f, 0f, 0f), 18, 3);
            UiBuilder.Stretch(artEdge.rectTransform);
            artEdge.color = UiPalette.ScannerAmber;

            _art = UiBuilder.Image("Art", artFrame, null, Color.white, Image.Type.Simple);
            _art.preserveAspect = true;
            UiBuilder.Stretch(_art.rectTransform, 26f);

            // The list, down the right.
            var list = UiBuilder.Rect("List", frame, false);
            UiBuilder.Anchor(list, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-24f, -110f), new Vector2(600f, VisibleRows * RowHeight));
            UiBuilder.Vertical(list, 4f);

            for (int i = 0; i < VisibleRows; i++)
            {
                var row = UiBuilder.Rect($"Row_{i}", list, false);
                UiBuilder.Size(row, 600f, RowHeight - 4f, flexibleWidth: 1f);

                var slab = UiBuilder.Panel("Slab", row, UiPalette.SurfaceSunken, 12);
                UiBuilder.Stretch(slab.rectTransform);

                var highlight = UiBuilder.OutlinedPanel("Cursor", row, new Color(0f, 0f, 0f, 0f), 12, 3);
                UiBuilder.Stretch(highlight.rectTransform);
                highlight.color = UiPalette.ScannerAmber;
                highlight.enabled = false;
                _highlights.Add(highlight);

                var number = UiBuilder.Text("No", row, string.Empty, UiTextRole.Numeric,
                    UiPalette.TextSecondary, TextAlignmentOptions.Left);
                UiBuilder.Anchor(number.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f),
                    new Vector2(0f, 0.5f), new Vector2(18f, 0f), new Vector2(96f, 0f));
                _numbers.Add(number);

                // The ball marks a caught entry, which is the distinction the dex is counting.
                var ball = UiBuilder.Image("Ball", row, null, UiPalette.ScannerAmber, Image.Type.Simple);
                UiBuilder.Anchor(ball.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f), new Vector2(122f, 0f), new Vector2(26f, 26f));
                ball.enabled = false;
                _balls.Add(ball);

                var name = UiBuilder.Text("Name", row, string.Empty, UiTextRole.Body,
                    UiPalette.TextPrimary, TextAlignmentOptions.Left);
                UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f),
                    new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero);
                name.rectTransform.offsetMin = new Vector2(162f, 0f);
                name.rectTransform.offsetMax = new Vector2(-18f, 0f);
                _names.Add(name);

                _rows.Add(row);
            }

            // The two counts, along the bottom.
            var footer = UiBuilder.Panel("Footer", frame, UiPalette.SurfaceRaised, 18);
            UiBuilder.Anchor(footer.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 20f), new Vector2(-40f, 96f));

            BuildCount(footer.rectTransform, Loc.Pick("Seen", "발견한 수"), 0.25f, out _seenCount);
            BuildCount(footer.rectTransform, Loc.Pick("Caught", "잡은 수"), 0.75f, out _caughtCount);
        }

        private static void BuildCount(RectTransform parent, string label, float x,
            out TextMeshProUGUI value)
        {
            var caption = UiBuilder.Text("Label", parent, label, UiTextRole.Secondary,
                UiPalette.TextMuted, TextAlignmentOptions.Center);
            UiBuilder.Anchor(caption.rectTransform, new Vector2(x, 1f), new Vector2(x, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(300f, 32f));

            value = UiBuilder.Text("Value", parent, "0", UiTextRole.Title,
                UiPalette.TextPrimary, TextAlignmentOptions.Center);
            UiBuilder.Anchor(value.rectTransform, new Vector2(x, 0f), new Vector2(x, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(300f, 46f));
        }
    }
}
