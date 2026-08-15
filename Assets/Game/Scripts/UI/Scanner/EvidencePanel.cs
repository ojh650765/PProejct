using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The scanner "showing its reasoning": <see cref="PredictionEvidence"/> rendered as a
    /// ranked contribution breakdown.
    ///
    /// The framing matters as much as the data. This is not a feature-importance dump — it
    /// is the device explaining why it believes its own number, in the same voice as the
    /// rest of the readout. So the header reads as an explanation, the method footnote is
    /// written for a trainer rather than a data scientist, and rows carry player-facing
    /// labels with the raw model feature value demoted to a caption.
    ///
    /// Rows are pooled: a readout can arrive on every battle state change and rebuilding
    /// eleven rows from scratch each time would allocate through the whole battle.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EvidencePanel : MonoBehaviour
    {
        [Header("Parts")]
        [SerializeField] private RectTransform _rowParent;
        [SerializeField] private TextMeshProUGUI _emptyLabel;
        [SerializeField] private TextMeshProUGUI _headline;

        [Header("Tuning")]
        [Tooltip("Maximum rows shown. Beyond this the tail contributes almost nothing and only adds noise.")]
        [SerializeField] private int _maxRows = 6;

        private readonly List<EvidenceRow> _rows = new List<EvidenceRow>(8);
        private readonly List<PredictionEvidence> _sorted = new List<PredictionEvidence>(16);

        /// <summary>
        /// Renders a prediction's evidence. Safe with a null prediction — the panel then
        /// states that no reasoning is available rather than drawing an empty frame.
        /// </summary>
        public void Bind(MatchupPrediction prediction, bool animate = true)
        {
            var evidence = prediction?.Evidence;
            if (evidence == null || evidence.Length == 0)
            {
                ShowEmpty("No contributing factors resolved for this matchup.");
                return;
            }

            _sorted.Clear();
            _sorted.AddRange(evidence);
            // The contract says Evidence is already sorted by descending contribution, but a
            // fake or partial oracle may not honour that and the ranking is the whole point.
            _sorted.Sort((a, b) => Mathf.Abs(b.ProbabilityPoints).CompareTo(Mathf.Abs(a.ProbabilityPoints)));

            var count = Mathf.Min(_maxRows, _sorted.Count);
            var scale = Mathf.Abs(_sorted[0].ProbabilityPoints);

            EnsureRows(count);
            for (var i = 0; i < _rows.Count; i++)
            {
                var active = i < count;
                _rows[i].gameObject.SetActive(active);
                if (active) _rows[i].Bind(_sorted[i], scale, i, animate);
            }

            if (_emptyLabel != null) _emptyLabel.gameObject.SetActive(false);
            if (_headline != null)
            {
                var top = _sorted[0];
                var topLabel = string.IsNullOrWhiteSpace(top.Label) ? top.Feature : top.Label;
                var direction = top.ProbabilityPoints >= 0f ? "in your favour" : "against you";
                _headline.SetText($"<b>{topLabel}</b> is the deciding factor, {direction}.");
                _headline.gameObject.SetActive(true);
            }
        }

        /// <summary>Hides every row and states why there is nothing to show.</summary>
        public void ShowEmpty(string reason)
        {
            for (var i = 0; i < _rows.Count; i++) _rows[i].gameObject.SetActive(false);
            if (_headline != null) _headline.gameObject.SetActive(false);
            if (_emptyLabel == null) return;
            _emptyLabel.gameObject.SetActive(true);
            _emptyLabel.SetText(reason);
        }

        private void EnsureRows(int count)
        {
            while (_rows.Count < count)
            {
                _rows.Add(EvidenceRow.Build(_rowParent));
            }
        }

        /// <summary>Builds the panel including its header and method footnote.</summary>
        public static EvidencePanel Build(Transform parent, int maxRows = 6)
        {
            var root = UiBuilder.Rect("EvidencePanel", parent);
            UiBuilder.Vertical(root, 8f);
            UiBuilder.Size(root, flexibleWidth: 1f);

            var panel = root.gameObject.AddComponent<EvidencePanel>();
            panel._maxRows = maxRows;

            var header = UiBuilder.Rect("Header", root);
            UiBuilder.Horizontal(header, 8f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(header, preferredHeight: 16f, flexibleWidth: 1f);
            UiBuilder.Text("Title", header, "Why", UiTextRole.Overline, UiPalette.ScannerAmber.WithAlpha(0.85f));
            var rule = UiBuilder.Image("Rule", header, UiSprites.Solid(), UiPalette.Divider, Image.Type.Simple);
            UiBuilder.Size(rule.rectTransform, preferredHeight: 1f, minHeight: 1f, flexibleWidth: 1f);

            panel._headline = UiBuilder.Text("Headline", root, string.Empty, UiTextRole.Secondary, UiPalette.TextSecondary);
            UiBuilder.Size(panel._headline.rectTransform, flexibleWidth: 1f);

            panel._rowParent = UiBuilder.Rect("Rows", root);
            UiBuilder.Vertical(panel._rowParent, 4f);
            UiBuilder.Size(panel._rowParent, flexibleWidth: 1f);

            panel._emptyLabel = UiBuilder.Text("Empty", root, string.Empty, UiTextRole.Secondary, UiPalette.TextMuted);
            panel._emptyLabel.gameObject.SetActive(false);

            // The method note is what turns a bar chart into an explanation. It describes the
            // neutral-substitution algorithm in a trainer's terms, not the model's.
            var footnote = UiBuilder.Text("Method", root,
                "Each factor is neutralised in turn — the bar is how far the estimate moves without it.",
                UiTextRole.Caption, UiPalette.TextMuted.WithAlpha(0.55f));
            footnote.fontSize = 11f;
            footnote.characterSpacing = 0f;
            UiBuilder.Size(footnote.rectTransform, flexibleWidth: 1f);

            return panel;
        }
    }
}
