using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>One creature's share of a battle's experience, as the summary needs it.</summary>
    public struct ExperienceSummaryEntry
    {
        /// <summary>Species, for the portrait. Zero draws the placeholder disc.</summary>
        public int SpeciesId;

        /// <summary>What to call it. Falls back to the species name when empty.</summary>
        public string DisplayName;

        /// <summary>Experience gained in this battle.</summary>
        public int Gained;

        /// <summary>Running total <b>after</b> the gain.</summary>
        public int NewTotal;

        /// <summary>Level <b>after</b> the gain.</summary>
        public int NewLevel;

        /// <summary>Levels crossed by this gain. Zero is the common case.</summary>
        public int LevelsGained;
    }

    /// <summary>
    /// The battle-mode result screen: what happened, and then what every creature got for it.
    ///
    /// It replaces a single line of text. Battle mode used to end by writing
    /// "경험치 +240, 레벨 2회 상승." into a centred label and waiting for a keypress — the entire
    /// reward for a six-on-six fight, stated as a fact rather than shown as an event.
    ///
    /// The sequence is one creature at a time, deliberately. Six bars filling at once is a
    /// progress screen; six bars filling one after another, each with its own number counting
    /// and its own rollover, is a reward. The cost is time, which is why the whole thing is
    /// skippable at any point — a press finalises everything still pending and drops straight
    /// to the prompt, which is the behaviour every game in the genre has.
    ///
    /// The curve is the engine's, mirrored in <see cref="ExperienceCurve"/>: the server sends
    /// a running total and a level, and the fraction within the level follows from those two
    /// alone. Nothing here invents a band.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleExpSummary : MonoBehaviour
    {
        private const float PanelWidth = 940f;
        private const float PanelHeight = 940f;

        // Six of these plus the header have to fit inside PanelHeight at the 1080 reference,
        // and every row's internals have to fit inside this: 10px padding top and bottom, then
        // a 34px name line, the bar, and the figure — with each text rect comfortably taller
        // than its point size, because TMP renders nothing at all when it is not.
        private const float RowHeight = 100f;

        private CanvasGroup _group;
        private RectTransform _rect;
        private RectTransform _rows;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _subtitle;
        private TextMeshProUGUI _prompt;
        private Image _titleRule;
        private Image _headerBall;

        private readonly List<Row> _built = new List<Row>();
        private bool _skipRequested;

        /// <summary>True once the player has asked to skip the remaining rolls.</summary>
        public bool Skipped => _skipRequested;

        private void Update()
        {
            if (_skipRequested) return;
            if (!PressedThisFrame()) return;
            _skipRequested = true;
        }

        /// <summary>Any key, any button. The summary is a reward, not a menu.</summary>
        private static bool PressedThisFrame()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.anyKey.wasPressedThisFrame) return true;
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) return true;
            var touch = Touchscreen.current;
            return touch != null && touch.primaryTouch.press.wasPressedThisFrame;
        }

        /// <summary>
        /// Plays the whole sequence and returns when the player has dismissed it.
        ///
        /// A coroutine rather than a callback chain because the caller — the battle-mode
        /// session — is already a coroutine that has to tear the arena down afterwards, and
        /// the one thing that must never happen is the teardown starting while the summary is
        /// still on screen.
        /// </summary>
        public IEnumerator Play(bool won, IReadOnlyList<ExperienceSummaryEntry> entries, string failureNote = null)
        {
            _skipRequested = false;
            gameObject.SetActive(true);

            // Winning takes the inverted header — solid near-white bar, dark navy word. Losing
            // does not: it stays the navy panel with light text, which is the reference's
            // un-selected state, and the value relationship says which happened before the
            // word is read.
            var accent = won ? BattleSkin.Lime : UiPalette.Negative;
            if (_titleRule != null) _titleRule.color = won ? BattleSkin.Light : BattleSkin.PlateBody;
            if (_headerBall != null)
                _headerBall.color = won ? BattleSkin.Ink.WithAlpha(0.85f) : accent.WithAlpha(0.7f);
            if (_title != null)
            {
                _title.SetText(won ? Loc.Pick("VICTORY", "승리!") : Loc.Pick("DEFEAT", "패배…"));
                _title.color = won ? BattleSkin.Ink : accent;
            }
            if (_subtitle != null)
            {
                _subtitle.SetText(string.IsNullOrEmpty(failureNote)
                    ? (won ? Loc.Pick("The field is yours.", "상대 팀을 모두 쓰러뜨렸다!")
                           : Loc.Pick("Your team was beaten.", "우리 팀이 모두 쓰러졌다…"))
                    : failureNote);
                _subtitle.color = string.IsNullOrEmpty(failureNote) ? UiPalette.TextSecondary : UiPalette.Caution;
            }
            if (_prompt != null) _prompt.gameObject.SetActive(false);

            BuildRows(entries);

            // The headline lands first and alone. Rows sliding in under a title that is still
            // arriving reads as a list loading rather than as a result being announced.
            if (_group != null) { _group.alpha = 0f; UiTween.Fade(_group, 1f, 0.28f); }
            if (_rect != null)
            {
                _rect.localScale = new Vector3(1f, 0.94f, 1f);
                UiTween.Run(0.42f, t =>
                {
                    if (_rect != null)
                        _rect.localScale = Vector3.LerpUnclamped(new Vector3(1f, 0.94f, 1f), Vector3.one, t);
                }, won ? Ease.OutBack : Ease.OutCubic);
            }
            if (_title != null && won) UiTween.Delay(0.2f, () =>
            {
                if (_title != null) UiTween.Punch(_title.transform, 0.10f, 0.4f);
            });

            yield return Wait(0.45f);

            for (var i = 0; i < _built.Count; i++)
            {
                var row = _built[i];
                row.Reveal();
                yield return Wait(_skipRequested ? 0f : 0.16f);

                row.Roll();
                while (row.IsRolling && !_skipRequested) yield return null;
                if (_skipRequested) row.Finish();
            }

            // Everything still mid-flight when the skip landed.
            if (_skipRequested)
            {
                for (var i = 0; i < _built.Count; i++)
                {
                    _built[i].Reveal(true);
                    _built[i].Finish();
                }
            }

            if (_prompt != null)
            {
                _prompt.gameObject.SetActive(true);
                _prompt.SetText(Loc.Pick("Press any key to return.", "아무 키나 누르면 돌아가요."));
                UiTween.Punch(_prompt.transform, 0.05f, 0.35f);
            }

            // A short deaf window, so the press that skipped the rolls does not also dismiss
            // the screen it just revealed.
            yield return Wait(0.45f);
            _skipRequested = false;

            var waited = 0f;
            while (waited < 30f && !_skipRequested)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (_group != null)
            {
                var done = false;
                UiTween.Fade(_group, 0f, 0.28f, Ease.InCubic, 0f, () => done = true);
                while (!done) yield return null;
            }
            gameObject.SetActive(false);
        }

        private static IEnumerator Wait(float seconds)
        {
            var elapsed = 0f;
            while (elapsed < seconds) { elapsed += Time.unscaledDeltaTime; yield return null; }
        }

        // ------------------------------------------------------------------- rows

        private void BuildRows(IReadOnlyList<ExperienceSummaryEntry> entries)
        {
            _built.Clear();
            if (_rows == null) return;
            UiBuilder.ClearChildren(_rows);
            if (entries == null) return;

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                // A creature that earned nothing is not a row. Six rows of "+0 EXP" is the
                // fastest way to make a reward screen feel like a receipt.
                if (entry.Gained <= 0 && entry.LevelsGained <= 0) continue;
                _built.Add(Row.Build(_rows, entry, _built.Count));
            }
        }

        /// <summary>One creature's line: portrait, name, level, bar, and the figure counting up.</summary>
        private sealed class Row
        {
            private RectTransform _rect;
            private CanvasGroup _group;
            private AnimatedBar _bar;
            private AnimatedNumber _gain;
            private TextMeshProUGUI _level;
            private TextMeshProUGUI _badge;
            private RectTransform _badgeRoot;
            private Image _flash;
            private ExperienceRoll _roll;
            private ExperienceSummaryEntry _entry;
            private int _fromLevel;
            private int _fromTotal;
            private bool _revealed;

            public bool IsRolling => _roll != null && _roll.IsRunning;

            public static Row Build(Transform parent, ExperienceSummaryEntry entry, int index)
            {
                var row = new Row { _entry = entry };

                row._fromTotal = Mathf.Clamp(entry.NewTotal - Mathf.Max(0, entry.Gained), 0, Mathf.Max(0, entry.NewTotal));
                // The server states how many levels were crossed, so the starting level is
                // arithmetic rather than a second guess at the curve.
                row._fromLevel = Mathf.Max(1, entry.NewLevel - Mathf.Max(0, entry.LevelsGained));

                var root = UiBuilder.Rect("Row", parent);
                UiBuilder.Size(root, preferredHeight: RowHeight, minHeight: RowHeight, flexibleWidth: 1f);
                row._rect = root;
                row._group = UiBuilder.Group(root, 0f, false, false);

                // Alternating tint rather than dividers: the reference gives its lists a
                // rhythm by value, not by rules, and a hairline between every row of a
                // six-row list is five more lines than the eye needs.
                var shell = UiBuilder.Image("Shell", root, UiSprites.Panel(14),
                    index % 2 == 0 ? BattleSkin.Panel : BattleSkin.RowTint);
                UiBuilder.Stretch(shell.rectTransform);
                UiBuilder.IgnoreLayout(shell.rectTransform);

                row._flash = UiBuilder.Image("Flash", root, UiSprites.Panel(14), Color.white.WithAlpha(0f));
                UiBuilder.Stretch(row._flash.rectTransform);
                UiBuilder.IgnoreLayout(row._flash.rectTransform);
                row._flash.enabled = false;

                UiBuilder.Horizontal(root, 18f, new RectOffset(16, 22, 10, 10), TextAnchor.MiddleLeft);

                // --- portrait
                var portrait = UiServices.PortraitOf(entry.SpeciesId);
                var art = UiBuilder.Image("Portrait", root, portrait ?? UiSprites.Dot(96),
                    portrait != null ? Color.white : UiPalette.SurfaceSunken, Image.Type.Simple);
                art.preserveAspect = true;
                UiBuilder.Size(art.rectTransform, preferredWidth: 72f, minWidth: 72f,
                    preferredHeight: 72f, minHeight: 72f);

                // --- the stack of name / bar / figures
                var column = UiBuilder.Rect("Column", root);
                UiBuilder.Vertical(column, 5f, null, TextAnchor.MiddleLeft);
                UiBuilder.Size(column, flexibleWidth: 1f);

                var head = UiBuilder.Rect("Head", column);
                UiBuilder.Horizontal(head, 10f, null, TextAnchor.MiddleLeft);
                UiBuilder.Size(head, preferredHeight: 34f, minHeight: 34f, flexibleWidth: 1f);

                var name = string.IsNullOrEmpty(entry.DisplayName)
                    ? UiServices.SpeciesName(entry.SpeciesId)
                    : entry.DisplayName;
                var nameLabel = UiBuilder.Text("Name", head, name, UiTextRole.Body, UiPalette.TextPrimary);
                nameLabel.fontStyle = FontStyles.Bold;
                nameLabel.textWrappingMode = TextWrappingModes.NoWrap;
                UiBuilder.Size(nameLabel.rectTransform, minWidth: 120f, flexibleWidth: 1f);

                // The level-up tag: a solid lime pill with dark navy text, which is the
                // reference's inverted-selection treatment borrowed for the one thing on this
                // screen that is genuinely good news. Hidden until earned, so it costs nothing
                // on the rows that never level.
                var badgeHolder = UiBuilder.Rect("Badge", head, false);
                UiBuilder.Size(badgeHolder, preferredWidth: 176f, minWidth: 176f,
                    preferredHeight: 32f, minHeight: 32f);
                var badgePill = UiBuilder.Image("Pill", badgeHolder, UiSprites.Pill(32), BattleSkin.Lime);
                UiBuilder.Stretch(badgePill.rectTransform);

                row._badge = UiBuilder.Text("Label", badgeHolder, Loc.Pick("LEVEL UP!", "레벨 업!"),
                    UiTextRole.Overline, BattleSkin.Ink, TextAlignmentOptions.Center);
                row._badge.characterSpacing = 4f;
                row._badge.textWrappingMode = TextWrappingModes.NoWrap;
                row._badge.overflowMode = TextOverflowModes.Overflow;
                UiBuilder.Stretch(row._badge.rectTransform);
                row._badgeRoot = badgeHolder;
                badgeHolder.gameObject.SetActive(false);

                row._level = UiBuilder.Text("Level", head, "Lv. " + row._fromLevel, UiTextRole.Numeric,
                    UiPalette.TextSecondary, TextAlignmentOptions.Right);
                UiBuilder.Size(row._level.rectTransform, preferredWidth: 132f, minWidth: 132f,
                    preferredHeight: 32f, minHeight: 32f);

                row._bar = AnimatedBar.Build("ExpBar", column, 10f, BattleSkin.Cyan, false,
                    UiPalette.SurfaceSunken.WithAlpha(0.9f));
                row._bar.SetImmediate(ExperienceCurve.FractionWithin(row._fromTotal, row._fromLevel));

                var gainLabel = UiBuilder.Text("Gain", column, "+0 EXP", UiTextRole.Caption,
                    BattleSkin.Cyan, TextAlignmentOptions.Right);
                UiBuilder.Size(gainLabel.rectTransform, preferredHeight: 26f, minHeight: 26f, flexibleWidth: 1f);
                row._gain = AnimatedNumber.Attach(gainLabel, v => "+" + Mathf.RoundToInt(v) + " EXP", 0.9f);
                row._gain.SetImmediate(0f);

                return row;
            }

            /// <summary>
            /// Brings the row in. Idempotent, so a skip can force every row visible.
            ///
            /// Fade and scale, never position: the row is a child of a vertical layout group,
            /// which owns its anchored position and will put it back the moment anything else
            /// in the panel dirties the layout — a level-up badge switching on, for instance,
            /// which is exactly what happens two rows later.
            /// </summary>
            public void Reveal(bool immediate = false)
            {
                if (_revealed || _rect == null) return;
                _revealed = true;

                if (_group != null) _group.gameObject.SetActive(true);

                if (immediate)
                {
                    if (_group != null) _group.alpha = 1f;
                    _rect.localScale = Vector3.one;
                    return;
                }

                var from = new Vector3(0.97f, 0.7f, 1f);
                _rect.localScale = from;
                UiTween.Run(0.3f, t =>
                {
                    if (_rect != null) _rect.localScale = Vector3.LerpUnclamped(from, Vector3.one, t);
                }, Ease.OutBack);
                if (_group != null) UiTween.Fade(_group, 1f, 0.22f);
            }

            /// <summary>Starts this row's roll. The bar, the figure and the level move together.</summary>
            public void Roll()
            {
                if (_bar == null) return;

                _gain?.SetValue(_entry.Gained, 1.35f);

                _roll = ExperienceRoll.Play(_bar, _fromTotal, _fromLevel, _entry.NewTotal,
                    Mathf.Max(_fromLevel, _entry.NewLevel), 1.35f,
                    level =>
                    {
                        if (_level != null) _level.SetText("Lv. " + level);
                        if (_badgeRoot != null && !_badgeRoot.gameObject.activeSelf)
                        {
                            _badgeRoot.gameObject.SetActive(true);
                            _badgeRoot.localScale = Vector3.one * 0.4f;
                            UiTween.Scale(_badgeRoot, Vector3.one, 0.3f, Ease.OutBack);
                        }
                        else if (_badgeRoot != null) UiTween.Punch(_badgeRoot, 0.14f, 0.36f);
                        if (_rect != null) UiTween.Punch(_rect, 0.03f, 0.34f);
                        FlashRow();
                    },
                    null);
            }

            /// <summary>Drops everything to its final state. The skip path, and the safety net.</summary>
            public void Finish()
            {
                _roll?.Cancel();
                _roll = null;

                var level = Mathf.Max(_fromLevel, _entry.NewLevel);
                _bar?.SetImmediate(ExperienceCurve.FractionWithin(_entry.NewTotal, level));
                _bar?.SetColorImmediate(BattleSkin.Cyan);
                _gain?.SetImmediate(_entry.Gained);
                if (_level != null) _level.SetText("Lv. " + level);
                if (_badgeRoot != null && _entry.LevelsGained > 0)
                {
                    _badgeRoot.gameObject.SetActive(true);
                    _badgeRoot.localScale = Vector3.one;
                }
            }

            private void FlashRow()
            {
                if (_flash == null) return;
                _flash.enabled = true;
                UiTween.Run(0.42f, t =>
                {
                    if (_flash == null) return;
                    var alpha = t < 0.12f ? t / 0.12f : Mathf.Pow(1f - (t - 0.12f) / 0.88f, 2f);
                    _flash.color = UiPalette.ScannerAmber.WithAlpha(alpha * 0.3f);
                }, Ease.Linear, 0f, true, () =>
                {
                    if (_flash == null) return;
                    _flash.color = Color.white.WithAlpha(0f);
                    _flash.enabled = false;
                });
            }
        }

        // ------------------------------------------------------------------ build

        /// <summary>Builds the panel under <paramref name="parent"/>, hidden.</summary>
        public static BattleExpSummary Build(Transform parent)
        {
            var host = UiBuilder.Rect("BattleExpSummary", parent);
            var summary = host.gameObject.AddComponent<BattleExpSummary>();

            // Navy, never black: the reference's ground is a darkened scene in indigo, and a
            // flat black scrim under a navy card is the one thing that makes the card look
            // pasted on rather than layered into a screen.
            var scrim = UiBuilder.Backdrop("Scrim", host, UiSprites.Solid(),
                BattleSkin.SceneTop.WithAlpha(0.88f), true);
            scrim.type = Image.Type.Simple;

            var card = UiBuilder.Rect("Card", host, false);
            UiBuilder.Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(PanelWidth, PanelHeight));
            summary._rect = card;
            summary._group = UiBuilder.Group(host, 0f, false, false);

            var shell = UiBuilder.Image("Shell", card, UiSprites.Panel(18), BattleSkin.Panel);
            UiBuilder.Stretch(shell.rectTransform, -18f);
            UiBuilder.IgnoreLayout(shell.rectTransform);
            var shellRim = UiBuilder.Image("Rim", card, UiSprites.Frame(18, 2), BattleSkin.PanelRim);
            UiBuilder.Stretch(shellRim.rectTransform, -18f);
            UiBuilder.IgnoreLayout(shellRim.rectTransform);

            UiBuilder.Vertical(card, 12f, new RectOffset(0, 0, 0, 0), TextAnchor.UpperCenter);

            // --- the header, inverted.
            //
            // A solid near-white bar carrying dark navy text and a ball mark, which is how the
            // reference draws the header of every detail card. It is the same move as its
            // selected menu row: a full value inversion rather than a tint or an outline. On a
            // navy panel it is the loudest thing available and costs no colour to be loud with.
            var header = UiBuilder.Rect("Header", card, false);
            UiBuilder.Size(header, preferredHeight: 104f, minHeight: 104f, flexibleWidth: 1f);

            summary._titleRule = UiBuilder.Image("HeaderBar", header, UiSprites.Panel(14), BattleSkin.Light);
            UiBuilder.Stretch(summary._titleRule.rectTransform);

            summary._headerBall = UiBuilder.Image("Ball", header, UiSprites.BallGlyph(96, 8),
                BattleSkin.Ink.WithAlpha(0.85f), Image.Type.Simple);
            UiBuilder.Anchor(summary._headerBall.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(30f, 0f), new Vector2(56f, 56f));

            // 60pt inside an 84px row. TMP renders nothing at all when the line is taller than
            // its rect, and a result screen with no word on it is the worst way for this to
            // fail — so the rect is generous and the mode is Overflow, never Ellipsis.
            summary._title = UiBuilder.Text("Title", header, string.Empty, UiTextRole.Title,
                BattleSkin.Ink, TextAlignmentOptions.Center);
            summary._title.fontSize = 60f;
            summary._title.characterSpacing = 5f;
            summary._title.textWrappingMode = TextWrappingModes.NoWrap;
            summary._title.overflowMode = TextOverflowModes.Overflow;
            UiBuilder.Anchor(summary._title.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-120f, 84f));

            summary._subtitle = UiBuilder.Text("Subtitle", card, string.Empty, UiTextRole.Body,
                UiPalette.TextSecondary, TextAlignmentOptions.Center);
            UiBuilder.Size(summary._subtitle.rectTransform, preferredHeight: 42f, minHeight: 42f, flexibleWidth: 1f);

            var caption = UiBuilder.Text("Caption", card, Loc.Pick("EXPERIENCE", "획득 경험치"),
                UiTextRole.Overline, BattleSkin.Cyan.WithAlpha(0.85f), TextAlignmentOptions.Center);
            UiBuilder.Size(caption.rectTransform, preferredHeight: 30f, minHeight: 30f, flexibleWidth: 1f);

            // No fixed height: the group reports its own preferred size through ILayoutElement,
            // so a two-creature team gets a short panel rather than four rows of empty space.
            summary._rows = UiBuilder.Rect("Rows", card);
            UiBuilder.Vertical(summary._rows, 10f, new RectOffset(0, 0, 4, 4), TextAnchor.UpperCenter);
            UiBuilder.Size(summary._rows, flexibleWidth: 1f);

            summary._prompt = UiBuilder.Text("Prompt", card, string.Empty, UiTextRole.Secondary,
                UiPalette.TextMuted, TextAlignmentOptions.Center);
            UiBuilder.Size(summary._prompt.rectTransform, preferredHeight: 36f, minHeight: 36f, flexibleWidth: 1f);
            summary._prompt.gameObject.SetActive(false);

            host.gameObject.SetActive(false);
            return summary;
        }
    }
}
