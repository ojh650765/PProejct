using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// Ranked switch recommendations.
    ///
    /// Only positive-gain recommendations are shown by default: a list that includes
    /// "switching here loses you 12 points" is technically complete and practically noise,
    /// and the scanner is an advisor, not a table. The previous ranking is retained so each
    /// row can show whether it moved up or down since the last readout.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SwitchPanel : MonoBehaviour
    {
        [SerializeField] private RectTransform _rowParent;
        [SerializeField] private TextMeshProUGUI _emptyLabel;
        [SerializeField] private int _maxRows = 3;
        [Tooltip("Recommendations below this gain in points are suppressed as noise.")]
        [SerializeField] private float _minimumGainPoints = 0.5f;
        [SerializeField] private bool _interactive;

        private readonly List<SwitchRow> _rows = new List<SwitchRow>(4);
        private readonly Dictionary<string, int> _previousRanks = new Dictionary<string, int>(8);
        private readonly List<SwitchRecommendation> _filtered = new List<SwitchRecommendation>(8);

        /// <summary>Raised with a party index when the player picks a recommendation directly.</summary>
        public Action<int> SwitchRequested;

        /// <summary>True when the top recommendation changed identity in the last bind.</summary>
        public bool TopChanged { get; private set; }

        /// <summary>Renders the recommendations, highest gain first.</summary>
        public void Bind(SwitchRecommendation[] switches, bool animate = true)
        {
            TopChanged = false;

            _filtered.Clear();
            if (switches != null)
            {
                for (var i = 0; i < switches.Length; i++)
                {
                    if (switches[i].GainPoints >= _minimumGainPoints) _filtered.Add(switches[i]);
                }
            }
            _filtered.Sort((a, b) => b.GainPoints.CompareTo(a.GainPoints));

            if (_filtered.Count == 0)
            {
                for (var i = 0; i < _rows.Count; i++) _rows[i].HideImmediate();
                if (_emptyLabel != null)
                {
                    _emptyLabel.gameObject.SetActive(true);
                    // Positive framing: no switch beats staying in, which is itself advice.
                    _emptyLabel.SetText(switches == null || switches.Length == 0
                        ? "Party not scanned."
                        : "Your active creature is the best matchup on the team.");
                }
                _previousRanks.Clear();
                return;
            }

            var count = Mathf.Min(_maxRows, _filtered.Count);
            EnsureRows(count);

            var previousTop = _previousRanks.Count > 0 ? TopOf(_previousRanks) : null;

            for (var i = 0; i < count; i++)
            {
                var recommendation = _filtered[i];
                var key = KeyOf(recommendation);
                var previousRank = _previousRanks.TryGetValue(key, out var rank) ? rank : -1;
                _rows[i].Chosen = OnRowChosen;
                _rows[i].Bind(recommendation, i, previousRank, animate);
            }
            for (var i = count; i < _rows.Count; i++) _rows[i].HideImmediate();

            var newTop = KeyOf(_filtered[0]);
            TopChanged = previousTop != null && previousTop != newTop;

            _previousRanks.Clear();
            for (var i = 0; i < count; i++) _previousRanks[KeyOf(_filtered[i])] = i;

            if (_emptyLabel != null) _emptyLabel.gameObject.SetActive(false);
        }

        /// <summary>Clears the list and the rank history.</summary>
        public void ClearAll()
        {
            for (var i = 0; i < _rows.Count; i++) _rows[i].HideImmediate();
            _previousRanks.Clear();
            TopChanged = false;
        }

        private void OnRowChosen(int partyIndex) => SwitchRequested?.Invoke(partyIndex);

        private void EnsureRows(int count)
        {
            while (_rows.Count < count) _rows.Add(SwitchRow.Build(_rowParent, _interactive));
        }

        /// <summary>Identity for rank diffing: instance id when present, party index otherwise.</summary>
        private static string KeyOf(SwitchRecommendation recommendation)
        {
            return string.IsNullOrEmpty(recommendation.InstanceId)
                ? "slot:" + recommendation.PartyIndex
                : recommendation.InstanceId;
        }

        private static string TopOf(Dictionary<string, int> ranks)
        {
            foreach (var kvp in ranks)
            {
                if (kvp.Value == 0) return kvp.Key;
            }
            return null;
        }

        /// <summary>Builds the panel. Interactive rows raise <see cref="SwitchRequested"/> on click.</summary>
        public static SwitchPanel Build(Transform parent, int maxRows = 3, bool interactive = false)
        {
            var root = UiBuilder.Rect("SwitchPanel", parent);
            UiBuilder.Vertical(root, 8f);
            UiBuilder.Size(root, flexibleWidth: 1f);

            var panel = root.gameObject.AddComponent<SwitchPanel>();
            panel._maxRows = maxRows;
            panel._interactive = interactive;

            var header = UiBuilder.Rect("Header", root);
            UiBuilder.Horizontal(header, 8f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(header, preferredHeight: 16f, flexibleWidth: 1f);
            UiBuilder.Text("Title", header, "Recommended switches", UiTextRole.Overline,
                UiPalette.ScannerCyan.WithAlpha(0.75f));
            var rule = UiBuilder.Image("Rule", header, UiSprites.Solid(), UiPalette.Divider, Image.Type.Simple);
            UiBuilder.Size(rule.rectTransform, preferredHeight: 1f, minHeight: 1f, flexibleWidth: 1f);

            panel._rowParent = UiBuilder.Rect("Rows", root);
            UiBuilder.Vertical(panel._rowParent, 6f);
            UiBuilder.Size(panel._rowParent, flexibleWidth: 1f);

            panel._emptyLabel = UiBuilder.Text("Empty", root, string.Empty, UiTextRole.Secondary, UiPalette.TextMuted);
            UiBuilder.Size(panel._emptyLabel.rectTransform, flexibleWidth: 1f);
            panel._emptyLabel.gameObject.SetActive(false);

            return panel;
        }
    }
}
