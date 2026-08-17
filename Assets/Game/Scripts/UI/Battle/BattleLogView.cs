using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// The battle log.
    ///
    /// Lines are queued and revealed one at a time rather than dumped, because the log is the
    /// narration of the fight and a wall of six lines appearing at once is unreadable. The
    /// newest line is bright and typed out; older lines fade back and dim. When the queue
    /// runs long — a multi-hit move that generates eight events in half a second — reveal
    /// speeds up rather than falling behind, so the log never narrates a moment the battle
    /// has already moved past.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleLogView : MonoBehaviour
    {
        [SerializeField] private RectTransform _lineParent;
        [SerializeField] private CanvasGroup _group;

        [Header("Tuning")]
        [Tooltip("Lines kept on screen. Older ones are recycled.")]
        [SerializeField] private int _maxLines = 4;
        [Tooltip("Seconds per character at a normal pace.")]
        [SerializeField] private float _secondsPerCharacter = 0.018f;
        [Tooltip("Queue depth beyond which reveal accelerates to catch up.")]
        [SerializeField] private int _catchUpThreshold = 3;

        private readonly List<TextMeshProUGUI> _lines = new List<TextMeshProUGUI>(6);
        private readonly Queue<string> _pending = new Queue<string>(8);

        private TweenHandle _typing;
        private bool _revealing;

        /// <summary>Queues a line. Safe to call several times in one frame.</summary>
        public void Append(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _pending.Enqueue(text);
            if (!_revealing) RevealNext();
        }

        /// <summary>Drops everything, queued and displayed. Called between battles.</summary>
        public void Clear()
        {
            UiTween.Kill(ref _typing);
            _pending.Clear();
            _revealing = false;
            for (var i = 0; i < _lines.Count; i++)
            {
                _lines[i].SetText(string.Empty);
                _lines[i].gameObject.SetActive(false);
            }
        }

        /// <summary>Finishes the current reveal instantly and flushes the queue. For a skip input.</summary>
        public void FlushImmediate()
        {
            UiTween.Kill(ref _typing);
            _revealing = false;
            while (_pending.Count > 0)
            {
                var text = _pending.Dequeue();
                var line = PushLine();
                line.SetText(text);
                line.maxVisibleCharacters = int.MaxValue;
            }
        }

        private void RevealNext()
        {
            if (_pending.Count == 0)
            {
                _revealing = false;
                return;
            }

            _revealing = true;
            var text = _pending.Dequeue();
            var line = PushLine();
            line.SetText(text);
            line.maxVisibleCharacters = 0;

            // Strip rich-text tags from the count so a coloured line does not appear to
            // stall while the typewriter walks through markup.
            //
            // Reparsed first: GetParsedText reads the mesh TMP last built, so straight after
            // SetText it measures the line this slot was showing before rather than the one
            // being revealed — and this slot is recycled, so that is some line from three
            // events ago.
            line.ForceMeshUpdate(true);
            var visibleCount = Mathf.Max(1, line.GetParsedText().Length);
            var pace = _pending.Count >= _catchUpThreshold ? _secondsPerCharacter * 0.4f : _secondsPerCharacter;
            var duration = Mathf.Clamp(visibleCount * pace, 0.12f, 1.4f);

            UiTween.Kill(ref _typing);
            _typing = UiTween.Run(duration, t =>
            {
                if (line == null) return;
                line.maxVisibleCharacters = Mathf.CeilToInt(t * visibleCount);
            }, Ease.Linear, 0f, true, () =>
            {
                if (line != null) line.maxVisibleCharacters = int.MaxValue;
                // A short beat after each line so consecutive events do not run together.
                UiTween.Delay(0.12f, RevealNext);
            });
        }

        /// <summary>
        /// Rotates the line stack: existing lines dim and shift up, and the newest slot is
        /// returned bright. Recycles rather than instantiating, so a long battle does not
        /// leak a hundred TMP objects.
        /// </summary>
        private TextMeshProUGUI PushLine()
        {
            EnsureLines();

            // Move the oldest to the bottom of the sibling order and reuse it as the newest.
            var line = _lines[0];
            _lines.RemoveAt(0);
            _lines.Add(line);
            line.rectTransform.SetAsLastSibling();
            line.gameObject.SetActive(true);

            for (var i = 0; i < _lines.Count; i++)
            {
                // Newest is fully bright; each older line steps down in value.
                var age = _lines.Count - 1 - i;
                var alpha = Mathf.Lerp(1f, 0.28f, age / Mathf.Max(1f, _lines.Count - 1f));
                var target = _lines[i];
                var from = target.color;
                var to = (age == 0 ? UiPalette.TextPrimary : UiPalette.TextSecondary).WithAlpha(alpha);
                UiTween.Color(from, to, 0.25f, c => { if (target != null) target.color = c; });
            }

            return line;
        }

        private void EnsureLines()
        {
            while (_lines.Count < _maxLines)
            {
                // Body, and no hardcoded size over the top of it.
                //
                // This is the battle's narration — "앗! 야생 포켓몬이 나타났다!" — not a caption,
                // and it was pinned to 16pt, which overrode the type scale and left the one
                // thing the player has to read the smallest text on the screen.
                var text = UiBuilder.Text("Line", _lineParent, string.Empty, UiTextRole.Body,
                    UiPalette.TextPrimary);
                UiBuilder.Size(text.rectTransform, flexibleWidth: 1f, minHeight: 34f);
                text.gameObject.SetActive(false);
                _lines.Add(text);
            }
        }

        /// <summary>Builds the log panel.</summary>
        public static BattleLogView Build(Transform parent, int maxLines = 4)
        {
            var root = UiBuilder.Rect("BattleLog", parent);
            var view = root.gameObject.AddComponent<BattleLogView>();
            view._maxLines = maxLines;
            view._group = UiBuilder.Group(root);

            var shell = UiBuilder.Panel("Shell", root, UiPalette.Backdrop.WithAlpha(0.82f), 14, true, 18, 0.4f);
            var rim = UiBuilder.Image("Rim", shell.rectTransform, UiSprites.Frame(14, 1), Color.white.WithAlpha(0.05f));
            UiBuilder.Stretch(rim.rectTransform);

            var lines = UiBuilder.Rect("Lines", root);
            UiBuilder.Stretch(lines, 14f);
            UiBuilder.Vertical(lines, 3f, null, TextAnchor.LowerLeft);
            view._lineParent = lines;

            return view;
        }
    }
}
