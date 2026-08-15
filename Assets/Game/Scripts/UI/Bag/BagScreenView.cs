using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>Bag tabs. Ordered by how often a player reaches for them mid-battle.</summary>
    public enum BagCategory
    {
        Balls = 0,
        Medicine = 1,
        Battle = 2,
        Key = 3,
    }

    /// <summary>
    /// The item bag: category tabs, a list of held items, and the use flow.
    ///
    /// Core carries no item definition — <see cref="Core.IPlayerProfile.Inventory"/> is a
    /// string-to-count map and nothing else — so categorisation and display names are
    /// derived from the item id here, with a serialized override list for anything the
    /// heuristic gets wrong. That is a deliberate stopgap, not a design: an <c>ItemData</c>
    /// contract in Core would replace it, and this file is where that change would land.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BagScreenView : MonoBehaviour
    {
        /// <summary>Explicit id-to-category mapping for items the heuristic misreads.</summary>
        [Serializable]
        public struct CategoryOverride
        {
            public string ItemId;
            public BagCategory Category;
        }

        [Header("Parts")]
        [SerializeField] private RectTransform _tabRow;
        [SerializeField] private RectTransform _itemParent;
        [SerializeField] private TextMeshProUGUI _emptyLabel;
        [SerializeField] private TextMeshProUGUI _title;
        [SerializeField] private TextMeshProUGUI _detailName;
        [SerializeField] private TextMeshProUGUI _detailBody;
        [SerializeField] private Button _useButton;
        [SerializeField] private TextMeshProUGUI _useLabel;
        [SerializeField] private CanvasGroup _group;

        [Header("Data")]
        [SerializeField] private CategoryOverride[] _overrides = Array.Empty<CategoryOverride>();
        [SerializeField] private bool _buildOnAwake = true;

        private readonly List<BagItemRow> _rows = new List<BagItemRow>(12);
        private readonly List<Image> _tabBackgrounds = new List<Image>(4);
        private readonly List<TextMeshProUGUI> _tabLabels = new List<TextMeshProUGUI>(4);
        private readonly List<KeyValuePair<string, int>> _filtered = new List<KeyValuePair<string, int>>(16);

        private BagCategory _category = BagCategory.Balls;
        private string _selectedItemId;
        private bool _built;

        /// <summary>Raised with the item id when the player confirms a use.</summary>
        public Action<string> ItemUsed;

        /// <summary>Raised when the player backs out.</summary>
        public Action Closed;

        /// <summary>True while the screen is open.</summary>
        public bool IsOpen { get; private set; }

        private void Awake()
        {
            if (_buildOnAwake && !_built) BuildRuntime();
            SetOpen(false, true);
        }

        /// <summary>Opens the bag on the last category viewed.</summary>
        public void Open()
        {
            Refresh();
            SetOpen(true);
        }

        /// <summary>Closes the bag.</summary>
        public void Close()
        {
            SetOpen(false);
            Closed?.Invoke();
        }

        /// <summary>Switches tab and rebuilds the list.</summary>
        public void SetCategory(BagCategory category)
        {
            _category = category;
            for (var i = 0; i < _tabBackgrounds.Count; i++)
            {
                var active = i == (int)category;
                var background = _tabBackgrounds[i];
                if (background != null)
                {
                    var target = active
                        ? UiPalette.ScannerCyan.WithAlpha(0.18f)
                        : UiPalette.SurfaceSunken.WithAlpha(0.6f);
                    UiTween.Color(background.color, target, 0.18f,
                        c => { if (background != null) background.color = c; });
                }

                var label = _tabLabels[i];
                if (label != null)
                {
                    var target = active ? UiPalette.ScannerCyan : UiPalette.TextMuted;
                    UiTween.Color(label.color, target, 0.18f, c => { if (label != null) label.color = c; });
                }
            }
            Refresh();
        }

        /// <summary>Rebuilds the item list from the profile's inventory.</summary>
        public void Refresh()
        {
            _filtered.Clear();
            var inventory = UiServices.Profile?.Inventory;
            if (inventory != null)
            {
                foreach (var entry in inventory)
                {
                    if (entry.Value <= 0) continue;
                    if (CategoryOf(entry.Key) != _category) continue;
                    _filtered.Add(entry);
                }
            }
            _filtered.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            EnsureRows(_filtered.Count);
            for (var i = 0; i < _rows.Count; i++)
            {
                var active = i < _filtered.Count;
                _rows[i].gameObject.SetActive(active);
                if (active) _rows[i].Bind(_filtered[i].Key, _filtered[i].Value);
            }

            if (_emptyLabel != null)
            {
                _emptyLabel.gameObject.SetActive(_filtered.Count == 0);
                _emptyLabel.SetText("Nothing in this pocket.");
            }

            SelectItem(_filtered.Count > 0 ? _filtered[0].Key : null);
        }

        /// <summary>Selects an item and shows its detail.</summary>
        public void SelectItem(string itemId)
        {
            _selectedItemId = itemId;
            for (var i = 0; i < _rows.Count; i++) _rows[i].SetSelected(_rows[i].ItemId == itemId);

            var has = !string.IsNullOrEmpty(itemId);
            if (_detailName != null) _detailName.SetText(has ? DisplayName(itemId) : "—");
            if (_detailBody != null)
            {
                _detailBody.SetText(has ? Description(itemId) : "Select an item.");
            }
            if (_useButton != null) _useButton.interactable = has;
            if (_useLabel != null) _useLabel.color = has ? UiPalette.TextPrimary : UiPalette.TextMuted;
        }

        private void Use()
        {
            if (string.IsNullOrEmpty(_selectedItemId)) return;
            ItemUsed?.Invoke(_selectedItemId);
            Refresh();
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

        private void EnsureRows(int count)
        {
            while (_rows.Count < count)
            {
                var row = BagItemRow.Build(_itemParent);
                row.Selected += SelectItem;
                row.Activated += id => { SelectItem(id); Use(); };
                _rows.Add(row);
            }
        }

        // ------------------------------------------------------------- item metadata

        /// <summary>
        /// Category for an item id. The overrides win; otherwise the id is matched against
        /// the naming conventions the data export uses.
        /// </summary>
        public BagCategory CategoryOf(string itemId)
        {
            for (var i = 0; i < _overrides.Length; i++)
            {
                if (string.Equals(_overrides[i].ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                {
                    return _overrides[i].Category;
                }
            }

            if (string.IsNullOrEmpty(itemId)) return BagCategory.Key;
            var id = itemId.ToLowerInvariant();

            if (id.Contains("ball")) return BagCategory.Balls;
            if (id.Contains("potion") || id.Contains("heal") || id.Contains("revive") ||
                id.Contains("antidote") || id.Contains("elixir") || id.Contains("ether")) return BagCategory.Medicine;
            if (id.Contains("x_") || id.Contains("guard") || id.Contains("berry") ||
                id.Contains("boost")) return BagCategory.Battle;
            return BagCategory.Key;
        }

        /// <summary>Display name derived from the id until an item data contract exists.</summary>
        public static string DisplayName(string itemId) =>
            string.IsNullOrEmpty(itemId) ? "—" : UiServices.Titleise(itemId.Replace('_', ' '));

        /// <summary>
        /// Placeholder description. Written to be honest rather than invented — the UI does
        /// not know what an arbitrary item does, and making something up would be worse than
        /// saying so.
        /// </summary>
        public static string Description(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return string.Empty;
            var id = itemId.ToLowerInvariant();
            if (id.Contains("ball")) return "Thrown at a wild creature to attempt a capture.";
            if (id.Contains("potion")) return "Restores health to one party member.";
            if (id.Contains("revive")) return "Revives a fainted party member.";
            if (id.Contains("antidote")) return "Cures poison.";
            return "No description recorded yet.";
        }

        // -------------------------------------------------------------------- build

        /// <summary>Builds the bag screen.</summary>
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

            var safe = UiBuilder.SafeArea(root, 96f, 56f);

            _title = UiBuilder.Text("Title", safe, "Bag", UiTextRole.Title, UiPalette.TextPrimary);
            UiBuilder.Anchor(_title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 44f));

            var tabs = UiBuilder.Rect("Tabs", safe, false);
            UiBuilder.Anchor(tabs, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -52f), new Vector2(0f, 38f));
            UiBuilder.Horizontal(tabs, 8f, null, TextAnchor.MiddleLeft);
            _tabRow = tabs;

            foreach (BagCategory category in Enum.GetValues(typeof(BagCategory)))
            {
                BuildTab(tabs, category);
            }

            var body = UiBuilder.Rect("Body", safe);
            body.offsetMax = new Vector2(0f, -100f);
            UiBuilder.Horizontal(body, 18f, null, TextAnchor.UpperLeft, false, true);

            var listColumn = UiBuilder.Rect("ListColumn", body);
            UiBuilder.Size(listColumn, preferredWidth: 520f, minWidth: 400f, flexibleWidth: 1.2f, flexibleHeight: 1f);
            var scroll = UiBuilder.ScrollList("Items", listColumn, out var items, 6f, new RectOffset(0, 6, 0, 0));
            UiBuilder.Stretch(scroll.GetComponent<RectTransform>());
            _itemParent = items;

            _emptyLabel = UiBuilder.Text("Empty", listColumn, string.Empty, UiTextRole.Secondary, UiPalette.TextMuted,
                TextAlignmentOptions.Top);
            UiBuilder.Anchor(_emptyLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(0f, 24f));
            _emptyLabel.gameObject.SetActive(false);

            BuildDetailColumn(body);

            UiBuilder.EnsureEventSystem();
            SetCategory(BagCategory.Balls);
        }

        private void BuildTab(Transform parent, BagCategory category)
        {
            var root = UiBuilder.Rect("Tab_" + category, parent, false);
            UiBuilder.Size(root, preferredWidth: 128f, preferredHeight: 34f, minWidth: 110f, minHeight: 34f);

            var background = UiBuilder.Image("Bg", root, UiSprites.Pill(34), UiPalette.SurfaceSunken.WithAlpha(0.6f),
                Image.Type.Sliced, true);
            UiBuilder.Stretch(background.rectTransform);
            _tabBackgrounds.Add(background);

            var label = UiBuilder.Text("Label", root, category.ToString().ToUpperInvariant(), UiTextRole.Overline,
                UiPalette.TextMuted, TextAlignmentOptions.Center);
            label.fontSize = 11f;
            UiBuilder.Stretch(label.rectTransform);
            _tabLabels.Add(label);

            UiButtonMotion.Attach(root, 17);
            var captured = category;
            UiBuilder.Button("Click", root, background, () => SetCategory(captured));
        }

        private void BuildDetailColumn(Transform parent)
        {
            var column = UiBuilder.Rect("DetailColumn", parent);
            UiBuilder.Size(column, preferredWidth: 420f, minWidth: 340f, flexibleWidth: 1f, flexibleHeight: 1f);

            UiBuilder.Panel("Shell", column, UiPalette.Surface, 16, true, 22, 0.45f);

            var stack = UiBuilder.Rect("Stack", column);
            UiBuilder.Stretch(stack, 20f);
            UiBuilder.Vertical(stack, 10f, null, TextAnchor.UpperLeft);

            _detailName = UiBuilder.Text("Name", stack, "—", UiTextRole.Heading, UiPalette.TextPrimary);
            UiBuilder.Size(_detailName.rectTransform, preferredHeight: 28f, flexibleWidth: 1f);

            _detailBody = UiBuilder.Text("Body", stack, "Select an item.", UiTextRole.Secondary,
                UiPalette.TextSecondary);
            UiBuilder.Size(_detailBody.rectTransform, flexibleWidth: 1f);

            UiBuilder.Spacer(stack);

            var useRoot = UiBuilder.Rect("Use", stack);
            UiBuilder.Size(useRoot, preferredHeight: 48f, minHeight: 48f, flexibleWidth: 1f);
            var useBg = UiBuilder.Image("Bg", useRoot, UiSprites.Panel(12),
                UiPalette.ScannerCyan.WithAlpha(0.18f), Image.Type.Sliced, true);
            UiBuilder.Stretch(useBg.rectTransform);
            _useLabel = UiBuilder.Text("Label", useRoot, "USE", UiTextRole.Overline, UiPalette.TextPrimary,
                TextAlignmentOptions.Center);
            _useLabel.fontSize = 13f;
            UiBuilder.Stretch(_useLabel.rectTransform);
            UiButtonMotion.Attach(useRoot, 12);
            _useButton = UiBuilder.Button("Click", useRoot, useBg, Use);
        }
    }
}
