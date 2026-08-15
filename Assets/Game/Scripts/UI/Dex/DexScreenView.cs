using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The dex: a grid of every species with its seen and caught state, and a detail panel
    /// reusing <see cref="CreatureDetailView"/>.
    ///
    /// Unseen species are rendered as silhouettes rather than hidden. A dex that only lists
    /// what you already have is a receipt; showing the gaps is what makes it a collection.
    /// Seen-but-uncaught sits between the two — name and types revealed, stats withheld.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DexScreenView : MonoBehaviour
    {
        [Header("Parts")]
        [SerializeField] private RectTransform _grid;
        [SerializeField] private CreatureDetailView _detail;
        [SerializeField] private TextMeshProUGUI _title;
        [SerializeField] private TextMeshProUGUI _progress;
        [SerializeField] private AnimatedBar _progressBar;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private bool _buildOnAwake = true;

        private readonly List<DexEntryTile> _tiles = new List<DexEntryTile>(64);
        private int _selectedSpeciesId = -1;
        private bool _built;

        /// <summary>Raised when the player backs out.</summary>
        public Action Closed;

        /// <summary>True while the screen is open.</summary>
        public bool IsOpen { get; private set; }

        private void Awake()
        {
            if (_buildOnAwake && !_built) BuildRuntime();
            SetOpen(false, true);
        }

        /// <summary>Opens the dex and rebuilds from the species registry.</summary>
        public void Open()
        {
            Refresh();
            SetOpen(true);
        }

        /// <summary>Closes the dex.</summary>
        public void Close()
        {
            SetOpen(false);
            Closed?.Invoke();
        }

        /// <summary>Rebuilds every tile from the registry and the player's records.</summary>
        public void Refresh()
        {
            var registry = UiServices.Species;
            var all = registry?.All;
            var count = all?.Count ?? 0;

            EnsureTiles(count);

            var seenCount = 0;
            var caughtCount = 0;

            for (var i = 0; i < _tiles.Count; i++)
            {
                var active = i < count;
                _tiles[i].gameObject.SetActive(active);
                if (!active) continue;

                var species = all[i];
                var seen = UiServices.HasSeen(species.Id);
                var caught = UiServices.HasCaught(species.Id);
                if (seen) seenCount++;
                if (caught) caughtCount++;
                _tiles[i].Bind(species, seen, caught);
            }

            if (_progress != null) _progress.SetText($"{caughtCount} caught · {seenCount} seen · {count} total");
            if (_progressBar != null)
            {
                _progressBar.SetValue(count > 0 ? caughtCount / (float)count : 0f, 0.7f);
                _progressBar.SetColorImmediate(UiPalette.ScannerCyan);
            }

            if (count > 0) Select(_selectedSpeciesId >= 0 ? _selectedSpeciesId : all[0].Id);
        }

        /// <summary>Selects a species and shows its detail.</summary>
        public void Select(int speciesId)
        {
            _selectedSpeciesId = speciesId;
            for (var i = 0; i < _tiles.Count; i++) _tiles[i].SetSelected(_tiles[i].SpeciesId == speciesId);

            var seen = UiServices.HasSeen(speciesId);
            if (!seen)
            {
                _detail?.SetVisible(false);
                return;
            }
            _detail?.BindSpecies(speciesId, UiServices.HasCaught(speciesId));
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

        private void EnsureTiles(int count)
        {
            while (_tiles.Count < count)
            {
                var tile = DexEntryTile.Build(_grid);
                tile.Selected += Select;
                _tiles.Add(tile);
            }
        }

        /// <summary>Builds the dex screen.</summary>
        public void BuildRuntime()
        {
            _built = true;
            var root = transform as RectTransform;
            if (root == null) return;

            UiBuilder.Stretch(root);
            _group = UiBuilder.Group(this, 0f, false, false);

            var scrim = UiBuilder.Image("Scrim", root, UiSprites.Solid(), UiPalette.Backdrop.WithAlpha(0.9f),
                Image.Type.Simple, true);
            UiBuilder.Stretch(scrim.rectTransform);

            var safe = UiBuilder.SafeArea(root, 72f, 48f);

            var header = UiBuilder.Rect("Header", safe, false);
            UiBuilder.Anchor(header, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                Vector2.zero, new Vector2(0f, 62f));
            UiBuilder.Vertical(header, 4f, null, TextAnchor.UpperLeft);

            _title = UiBuilder.Text("Title", header, "Dex", UiTextRole.Title, UiPalette.TextPrimary);
            UiBuilder.Size(_title.rectTransform, preferredHeight: 38f, flexibleWidth: 1f);

            var progressRow = UiBuilder.Rect("Progress", header);
            UiBuilder.Horizontal(progressRow, 12f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(progressRow, preferredHeight: 16f, minHeight: 16f, flexibleWidth: 1f);

            _progress = UiBuilder.Text("Text", progressRow, string.Empty, UiTextRole.Caption, UiPalette.TextMuted);
            _progress.characterSpacing = 0.5f;
            UiBuilder.Size(_progress.rectTransform, preferredWidth: 280f, minWidth: 220f);

            _progressBar = AnimatedBar.Build("Bar", progressRow, 6f, UiPalette.ScannerCyan);
            _progressBar.SetImmediate(0f);

            var body = UiBuilder.Rect("Body", safe);
            body.offsetMax = new Vector2(0f, -76f);
            UiBuilder.Horizontal(body, 18f, null, TextAnchor.UpperLeft, false, true);

            var gridColumn = UiBuilder.Rect("GridColumn", body);
            UiBuilder.Size(gridColumn, flexibleWidth: 1.6f, flexibleHeight: 1f, minWidth: 430f);
            var scroll = UiBuilder.ScrollList("Grid", gridColumn, out var content, 0f, new RectOffset(0, 8, 0, 0));
            UiBuilder.Stretch(scroll.GetComponent<RectTransform>());

            // The vertical layout the scroll helper installs is replaced by a grid: the dex
            // is the one list in the game that genuinely wants tiles rather than rows.
            var vertical = content.GetComponent<VerticalLayoutGroup>();
            if (vertical != null) Destroy(vertical);
            var grid = UiBuilder.Grid(content, new Vector2(138f, 132f), new Vector2(10f, 10f), 3);
            // Flexible rather than a fixed column count: the content rect is stretched to the
            // viewport, so at 21:9 the grid gains a column instead of leaving a dead margin.
            grid.constraint = GridLayoutGroup.Constraint.Flexible;
            _grid = content;

            var detailColumn = UiBuilder.Rect("DetailColumn", body);
            UiBuilder.Size(detailColumn, flexibleWidth: 1f, flexibleHeight: 1f, minWidth: 400f);
            _detail = CreatureDetailView.Build(detailColumn);
            UiBuilder.Stretch(_detail.GetComponent<RectTransform>());

            UiBuilder.EnsureEventSystem();
        }
    }
}
