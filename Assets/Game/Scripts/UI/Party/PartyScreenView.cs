using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The party screen: a reorderable list on the left, the selected creature's detail on
    /// the right.
    ///
    /// Reordering is reported, not performed. <see cref="IPlayerProfile"/> exposes no
    /// reorder operation and this worker may not change Core, so the UI keeps a local view
    /// order, animates it correctly, and raises <see cref="OrderChanged"/> with the full new
    /// index sequence for whoever owns the party to apply. If the profile later gains a
    /// reorder method, this becomes a one-line change here rather than a redesign.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PartyScreenView : MonoBehaviour
    {
        [Header("Parts")]
        [SerializeField] private RectTransform _slotParent;
        [SerializeField] private RectTransform _dragLayer;
        [SerializeField] private CreatureDetailView _detail;
        [SerializeField] private TextMeshProUGUI _title;
        [SerializeField] private TextMeshProUGUI _hint;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private bool _buildOnAwake = true;

        private readonly List<PartySlotView> _slots = new List<PartySlotView>(6);
        private readonly List<CreatureInstance> _order = new List<CreatureInstance>(6);
        private int _selected = -1;
        private bool _built;

        /// <summary>Raised with the new party order after a drag. Indices refer to the original list.</summary>
        public Action<IReadOnlyList<int>> OrderChanged;

        /// <summary>Raised when a creature is chosen — the battle screen turns this into a switch.</summary>
        public Action<int> CreatureChosen;

        /// <summary>Raised when the player backs out.</summary>
        public Action Closed;

        /// <summary>True while the screen is open.</summary>
        public bool IsOpen { get; private set; }

        private void Awake()
        {
            if (_buildOnAwake && !_built) BuildRuntime();
            SetOpen(false, true);
        }

        /// <summary>Opens the screen and rebinds from the current profile.</summary>
        public void Open()
        {
            Refresh();
            SetOpen(true);
        }

        /// <summary>Closes the screen.</summary>
        public void Close()
        {
            SetOpen(false);
            Closed?.Invoke();
        }

        /// <summary>Rebuilds the list from <see cref="IPlayerProfile.Party"/>.</summary>
        public void Refresh()
        {
            _order.Clear();
            var party = UiServices.Profile?.Party;
            if (party != null)
            {
                for (var i = 0; i < party.Count; i++) _order.Add(party[i]);
            }

            EnsureSlots(Mathf.Max(_order.Count, 1));

            for (var i = 0; i < _slots.Count; i++)
            {
                var active = i < _order.Count;
                _slots[i].gameObject.SetActive(active);
                if (active) _slots[i].Bind(i, _order[i], true);
            }

            if (_title != null)
            {
                var healthy = 0;
                for (var i = 0; i < _order.Count; i++)
                {
                    if (_order[i] != null && !_order[i].IsFainted) healthy++;
                }
                _title.SetText($"Party <size=70%><color=#FFFFFF88>{healthy} / {_order.Count} able</color></size>");
            }

            Select(_order.Count > 0 ? Mathf.Clamp(_selected, 0, _order.Count - 1) : -1);
        }

        /// <summary>Selects a slot and shows its detail.</summary>
        public void Select(int index)
        {
            _selected = index;
            for (var i = 0; i < _slots.Count; i++) _slots[i].SetSelected(i == index);
            if (index >= 0 && index < _order.Count) _detail?.Bind(_order[index]);
            else _detail?.SetVisible(false);
        }

        private void SetOpen(bool open, bool immediate = false)
        {
            IsOpen = open;
            if (_group == null) return;
            if (open) gameObject.SetActive(true);
            _group.interactable = open;
            _group.blocksRaycasts = open;

            if (immediate)
            {
                _group.alpha = open ? 1f : 0f;
                gameObject.SetActive(open);
                return;
            }

            UiTween.Fade(_group, open ? 1f : 0f, open ? 0.22f : 0.16f, open ? Ease.OutCubic : Ease.InCubic, 0f,
                () => { if (!open && this != null) gameObject.SetActive(false); });
        }

        private void EnsureSlots(int count)
        {
            while (_slots.Count < count)
            {
                var slot = PartySlotView.Build(_slotParent);
                slot.SetDragLayer(_dragLayer);
                slot.Selected += OnSlotSelected;
                slot.Reordered += OnSlotReordered;
                _slots.Add(slot);
            }
        }

        private void OnSlotSelected(int index)
        {
            if (index == _selected)
            {
                CreatureChosen?.Invoke(index);
                return;
            }
            Select(index);
        }

        private void OnSlotReordered(int from, int to)
        {
            if (from < 0 || from >= _order.Count) return;
            to = Mathf.Clamp(to, 0, _order.Count - 1);
            if (from == to) return;

            var moved = _order[from];
            _order.RemoveAt(from);
            _order.Insert(to, moved);

            // Rebind every slot: indices shifted for everything between the two positions,
            // and the lead-slot highlight has to follow.
            for (var i = 0; i < _slots.Count; i++)
            {
                if (i < _order.Count)
                {
                    _slots[i].Bind(i, _order[i]);
                    _slots[i].transform.SetSiblingIndex(i);
                }
            }

            _selected = to;
            Select(to);

            // Report the permutation as source indices in their new order.
            var permutation = new int[_order.Count];
            var party = UiServices.Profile?.Party;
            for (var i = 0; i < _order.Count; i++)
            {
                permutation[i] = i;
                if (party == null) continue;
                for (var j = 0; j < party.Count; j++)
                {
                    if (ReferenceEquals(party[j], _order[i])) { permutation[i] = j; break; }
                }
            }
            OrderChanged?.Invoke(permutation);
        }

        /// <summary>Builds the screen: list column, detail column, drag layer on top.</summary>
        public void BuildRuntime()
        {
            _built = true;
            var root = transform as RectTransform;
            if (root == null) return;

            UiBuilder.Stretch(root);
            _group = UiBuilder.Group(this, 0f, false, false);

            var scrim = UiBuilder.Image("Scrim", root, UiSprites.Solid(), UiPalette.Backdrop.WithAlpha(0.86f),
                Image.Type.Simple, true);
            UiBuilder.Stretch(scrim.rectTransform);

            var safe = UiBuilder.SafeArea(root, 72f, 48f);

            // --- header
            var header = UiBuilder.Rect("Header", safe, false);
            UiBuilder.Anchor(header, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                Vector2.zero, new Vector2(0f, 52f));
            UiBuilder.Horizontal(header, 12f, null, TextAnchor.MiddleLeft);

            _title = UiBuilder.Text("Title", header, "Party", UiTextRole.Title, UiPalette.TextPrimary);
            UiBuilder.Size(_title.rectTransform, flexibleWidth: 1f);

            _hint = UiBuilder.Text("Hint", header, "Drag to reorder · click twice to send out",
                UiTextRole.Caption, UiPalette.TextMuted, TextAlignmentOptions.Right);
            UiBuilder.Size(_hint.rectTransform, preferredWidth: 340f, minWidth: 240f);

            // --- body: fixed-width list, detail takes the rest. At 21:9 the detail card
            // gains the extra width rather than the list becoming a runway.
            var body = UiBuilder.Rect("Body", safe);
            body.offsetMax = new Vector2(0f, -64f);
            UiBuilder.Horizontal(body, 18f, null, TextAnchor.UpperLeft, false, true);

            var listColumn = UiBuilder.Rect("ListColumn", body);
            UiBuilder.Size(listColumn, preferredWidth: 460f, minWidth: 380f, flexibleHeight: 1f);

            var scroll = UiBuilder.ScrollList("Slots", listColumn, out var slots, 8f, new RectOffset(0, 6, 0, 0));
            UiBuilder.Stretch(scroll.GetComponent<RectTransform>());
            _slotParent = slots;

            var detailColumn = UiBuilder.Rect("DetailColumn", body);
            UiBuilder.Size(detailColumn, flexibleWidth: 1f, flexibleHeight: 1f, minWidth: 420f);
            _detail = CreatureDetailView.Build(detailColumn);
            UiBuilder.Stretch(_detail.GetComponent<RectTransform>());

            // The drag layer must sit above both columns and ignore raycasts, otherwise the
            // lifted row would block the drop targets it is being dragged over.
            _dragLayer = UiBuilder.Rect("DragLayer", root);
            UiBuilder.Group(_dragLayer, 1f, false, false);

            UiBuilder.EnsureEventSystem();
        }
    }
}
