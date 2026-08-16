using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// The menu the Start button opens: 도감, 포켓몬, 가방, the trainer's own card, 리포트,
    /// 설정, 닫는다.
    ///
    /// Modelled on the games' own: a column pinned to the right edge, tall rows of an icon and
    /// a word, and one row outlined to show where the cursor is. It is pinned right rather
    /// than centred because it opens over a world the player is still looking at — the whole
    /// point of that layout is that it does not cover what is in front of you.
    ///
    /// The view owns no game state. It is given a list of entries and reports which one was
    /// taken; what any of them mean is the presenter's business.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StartMenuView : MonoBehaviour
    {
        /// <summary>An entry: what it says, and the colour of its icon chip.</summary>
        public readonly struct Entry
        {
            public readonly string Label;
            public readonly Color Accent;

            public Entry(string label, Color accent)
            {
                Label = label;
                Accent = accent;
            }
        }

        private const float PanelWidth = 360f;
        private const float RowHeight = 78f;

        /// <summary>Raised with the index of the entry the player took.</summary>
        public Action<int> Chosen;

        /// <summary>Raised when the player backs out without choosing — Escape, or 닫는다.</summary>
        public Action Cancelled;

        private readonly List<RectTransform> _rows = new List<RectTransform>();
        private readonly List<Image> _outlines = new List<Image>();

        private RectTransform _panel;
        private CanvasGroup _group;
        private int _highlighted;
        private int _count;

        /// <summary>Which row the cursor is on.</summary>
        public int Highlighted => _highlighted;

        /// <summary>True while the menu is on screen and taking input.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>
        /// Builds the column for these entries and shows it.
        ///
        /// Rebuilt on every open rather than built once and toggled, because the trainer's own
        /// row is labelled with their name — which does not exist until they have been asked
        /// for it, and can be different the next time the menu opens.
        /// </summary>
        public void Open(IReadOnlyList<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            Build(entries);
            _count = entries.Count;

            IsOpen = true;
            _group.alpha = 1f;
            _group.blocksRaycasts = true;
            _group.interactable = true;
            gameObject.SetActive(true);

            Highlight(0);
        }

        /// <summary>Takes the menu off the screen. Safe to call when it is already closed.</summary>
        public void Close()
        {
            IsOpen = false;
            if (_group != null)
            {
                _group.alpha = 0f;
                _group.blocksRaycasts = false;
                _group.interactable = false;
            }
        }

        /// <summary>Moves the cursor by <paramref name="delta"/>, wrapping at both ends.</summary>
        public void Move(int delta)
        {
            if (_count <= 0) return;
            Highlight(((_highlighted + delta) % _count + _count) % _count);
        }

        /// <summary>Takes the highlighted entry.</summary>
        public void Take() => Chosen?.Invoke(_highlighted);

        private void Highlight(int index)
        {
            _highlighted = Mathf.Clamp(index, 0, Mathf.Max(0, _count - 1));
            for (int i = 0; i < _outlines.Count; i++)
            {
                if (_outlines[i] == null) continue;
                _outlines[i].enabled = i == _highlighted;
            }
        }

        private void Build(IReadOnlyList<Entry> entries)
        {
            var root = transform as RectTransform;
            if (root == null)
            {
                Debug.LogError("StartMenuView must live on a RectTransform under a Canvas.", this);
                return;
            }

            UiBuilder.ClearChildren(root);
            _rows.Clear();
            _outlines.Clear();

            UiBuilder.Stretch(root);
            _group = UiBuilder.Group(this, 1f, true, true);

            _panel = UiBuilder.Rect("Column", root, false);
            UiBuilder.Anchor(_panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-34f, -34f), new Vector2(PanelWidth, entries.Count * (RowHeight + 10f) + 24f));

            var backing = UiBuilder.Panel("Backing", _panel, UiPalette.Surface, 22);
            UiBuilder.Stretch(backing.rectTransform);

            var list = UiBuilder.Rect("Rows", _panel, false);
            UiBuilder.Stretch(list, 12f);
            UiBuilder.Vertical(list, 10f);

            for (int i = 0; i < entries.Count; i++)
            {
                var index = i;
                var entry = entries[i];

                var row = UiBuilder.Rect($"Row_{i}", list, false);
                UiBuilder.Size(row, PanelWidth - 24f, RowHeight, flexibleWidth: 1f);

                var slab = UiBuilder.Panel("Slab", row, UiPalette.SurfaceRaised, 16);
                UiBuilder.Stretch(slab.rectTransform);

                // Drawn as a separate always-present image toggled by Highlight, rather than
                // recoloured on the slab: an outline that appears and disappears reads as a
                // cursor, while a slab that changes shade reads as a disabled row.
                var outline = UiBuilder.OutlinedPanel("Cursor", row, new Color(0f, 0f, 0f, 0f), 16, 4);
                UiBuilder.Stretch(outline.rectTransform, -4f);
                outline.color = UiPalette.ScannerAmber;
                outline.enabled = false;
                _outlines.Add(outline);

                var chip = UiBuilder.Panel("Chip", row, entry.Accent, 12);
                UiBuilder.Anchor(chip.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(44f, 44f));

                var label = UiBuilder.Text("Label", row, entry.Label, UiTextRole.Heading,
                    UiPalette.TextPrimary, TextAlignmentOptions.Left);
                UiBuilder.Anchor(label.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f),
                    new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero);
                label.rectTransform.offsetMin = new Vector2(74f, 0f);
                label.rectTransform.offsetMax = new Vector2(-16f, 0f);

                // Hovering moves the cursor as well as clicking, so the keyboard and the mouse
                // never disagree about which row is selected.
                var button = UiBuilder.Button($"Take_{i}", row, slab, () =>
                {
                    Highlight(index);
                    Chosen?.Invoke(index);
                });
                var hover = button.gameObject.AddComponent<StartMenuRowHover>();
                hover.Bind(() => Highlight(index));

                _rows.Add(row);
            }
        }
    }

    /// <summary>
    /// Moves the cursor when the pointer enters a row.
    ///
    /// Its own component because <c>EventTrigger</c> allocates a list of delegate wrappers per
    /// entry and this needs exactly one callback.
    /// </summary>
    public sealed class StartMenuRowHover : MonoBehaviour,
        UnityEngine.EventSystems.IPointerEnterHandler
    {
        private Action _onEnter;

        public void Bind(Action onEnter) => _onEnter = onEnter;

        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData) => _onEnter?.Invoke();
    }
}
