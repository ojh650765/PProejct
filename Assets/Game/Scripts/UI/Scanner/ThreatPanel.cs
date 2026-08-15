using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// Ranked list of live threats.
    ///
    /// The panel diffs against the previous readout by threat key rather than rebuilding.
    /// That is the difference between "threats appear and clear" — the brief's words — and a
    /// list that silently swaps its contents every turn: a threat the player has already read
    /// must stay put, a new one must arrive visibly, and a resolved one must be seen leaving.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ThreatPanel : MonoBehaviour
    {
        [SerializeField] private RectTransform _rowParent;
        [SerializeField] private TextMeshProUGUI _emptyLabel;
        [SerializeField] private TextMeshProUGUI _countBadge;
        [SerializeField] private int _maxRows = 4;

        private readonly List<ThreatRow> _rows = new List<ThreatRow>(6);
        private readonly HashSet<string> _previousKeys = new HashSet<string>();
        private readonly HashSet<string> _currentKeys = new HashSet<string>();
        // Reused rather than allocated per bind: a readout arrives on every battle state
        // change, and a fresh List plus its sort comparer each time is pure garbage.
        private readonly List<ThreatAssessment> _ordered = new List<ThreatAssessment>(6);

        /// <summary>Highest threat level in the last bound readout. Drives the screen alert wash.</summary>
        public ThreatLevel HighestLevel { get; private set; } = ThreatLevel.Trivial;

        /// <summary>True when the last bind introduced a threat that was not present before.</summary>
        public bool HadNewThreat { get; private set; }

        /// <summary>Renders the threat list, sorted most severe first, then by KO chance.</summary>
        public void Bind(ThreatAssessment[] threats, bool animate = true)
        {
            HadNewThreat = false;
            HighestLevel = ThreatLevel.Trivial;

            if (threats == null || threats.Length == 0)
            {
                ClearAll(animate);
                if (_emptyLabel != null)
                {
                    _emptyLabel.gameObject.SetActive(true);
                    _emptyLabel.SetText("No active threats detected.");
                }
                if (_countBadge != null) _countBadge.SetText("0");
                return;
            }

            _ordered.Clear();
            _ordered.AddRange(threats);
            _ordered.Sort(CompareSeverity);

            var ordered = _ordered;
            var count = Mathf.Min(_maxRows, ordered.Count);
            EnsureRows(count);

            _currentKeys.Clear();
            for (var i = 0; i < count; i++)
            {
                var threat = ordered[i];
                var key = ThreatRow.MakeKey(threat);
                _currentKeys.Add(key);
                var isNew = animate && !_previousKeys.Contains(key);
                if (isNew) HadNewThreat = true;
                if ((int)threat.Level > (int)HighestLevel) HighestLevel = threat.Level;
                _rows[i].Bind(threat, i, isNew);
            }

            for (var i = count; i < _rows.Count; i++)
            {
                if (animate) _rows[i].PlayExit();
                else _rows[i].HideImmediate();
            }

            _previousKeys.Clear();
            foreach (var key in _currentKeys) _previousKeys.Add(key);

            if (_emptyLabel != null) _emptyLabel.gameObject.SetActive(false);
            if (_countBadge != null)
            {
                _countBadge.SetText(ordered.Count.ToString());
                _countBadge.color = UiPalette.Threat(HighestLevel);
            }
        }

        /// <summary>Drops every row and the diff history. Used when the scanner closes or retargets.</summary>
        public void ClearAll(bool animate = false)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (animate) _rows[i].PlayExit();
                else _rows[i].HideImmediate();
            }
            _previousKeys.Clear();
            HighestLevel = ThreatLevel.Trivial;
            HadNewThreat = false;
        }

        private void EnsureRows(int count)
        {
            while (_rows.Count < count) _rows.Add(ThreatRow.Build(_rowParent));
        }

        /// <summary>Most severe first; ties broken by the higher incoming KO chance.</summary>
        private static int CompareSeverity(ThreatAssessment a, ThreatAssessment b)
        {
            var byLevel = ((int)b.Level).CompareTo((int)a.Level);
            return byLevel != 0 ? byLevel : b.IncomingKoChance.CompareTo(a.IncomingKoChance);
        }

        /// <summary>Builds the panel with its section header.</summary>
        public static ThreatPanel Build(Transform parent, int maxRows = 4)
        {
            var root = UiBuilder.Rect("ThreatPanel", parent);
            UiBuilder.Vertical(root, 8f);
            UiBuilder.Size(root, flexibleWidth: 1f);

            var panel = root.gameObject.AddComponent<ThreatPanel>();
            panel._maxRows = maxRows;

            var header = UiBuilder.Rect("Header", root);
            UiBuilder.Horizontal(header, 8f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(header, preferredHeight: 16f, flexibleWidth: 1f);

            UiBuilder.Text("Title", header, "Threats", UiTextRole.Overline, UiPalette.Negative.WithAlpha(0.9f));

            var badge = UiBuilder.Text("Count", header, "0", UiTextRole.Numeric, UiPalette.TextMuted);
            badge.fontSize = 13f;
            UiBuilder.Size(badge.rectTransform, preferredWidth: 18f, minWidth: 18f);
            panel._countBadge = badge;

            var rule = UiBuilder.Image("Rule", header, UiSprites.Solid(), UiPalette.Divider, Image.Type.Simple);
            UiBuilder.Size(rule.rectTransform, preferredHeight: 1f, minHeight: 1f, flexibleWidth: 1f);

            panel._rowParent = UiBuilder.Rect("Rows", root);
            UiBuilder.Vertical(panel._rowParent, 6f);
            UiBuilder.Size(panel._rowParent, flexibleWidth: 1f);

            panel._emptyLabel = UiBuilder.Text("Empty", root, "No active threats detected.",
                UiTextRole.Secondary, UiPalette.TextMuted);
            UiBuilder.Size(panel._emptyLabel.rectTransform, flexibleWidth: 1f);

            return panel;
        }
    }
}
