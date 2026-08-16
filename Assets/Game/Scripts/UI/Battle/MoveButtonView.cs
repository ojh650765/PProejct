using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// One move in the selection list: a wide pill carrying the move's type mark, its name,
    /// a one-line verdict on how well it will land, and the PP remaining.
    ///
    /// Four facts, always in the same four places, so the player compares four moves at a
    /// glance instead of reading four sentences. The pill is tinted with the move's type
    /// colour because type is the thing a player scans for first and the only one that can be
    /// recognised without reading.
    ///
    /// A move with no forecast (the oracle has not landed, or the engine cannot simulate it)
    /// leaves the verdict line blank rather than printing "neutral". A confident label on an
    /// absent estimate is a lie, and "neutral" on three of four pills is noise either way —
    /// the geometry stays fixed so the four pills never go ragged.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MoveButtonView : MonoBehaviour
    {
        /// <summary>Height of one pill. The panel sizes its stack from this.</summary>
        public const float RowHeight = 94f;

        /// <summary>Resting width. The selected pill grows to <see cref="SelectedWidth"/>.</summary>
        public const float Width = 604f;

        /// <summary>Width of the selected pill. Wider is the loudest selection cue there is.</summary>
        public const float SelectedWidth = 648f;

        private const int Radius = 20;

        [Header("Identity")]
        [SerializeField] private Image _panel;
        [SerializeField] private Image _rim;
        [SerializeField] private Image _typeChip;
        [SerializeField] private Image _typeGlyph;
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _pp;

        [Header("Forecast")]
        [SerializeField] private Image _verdictDot;
        [SerializeField] private TextMeshProUGUI _verdict;

        [Header("State")]
        [SerializeField] private RectTransform _chevron;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private LayoutElement _sizing;
        [SerializeField] private Button _button;

        private int _index = -1;
        private ElementType _type = ElementType.None;
        private bool _usable = true;
        private bool _selected;

        /// <summary>Move slot index this card represents.</summary>
        public int Index => _index;

        /// <summary>True when the move can legally be selected.</summary>
        public bool IsUsable => _usable;

        /// <summary>True while this pill is the one the cursor is on.</summary>
        public bool IsSelected => _selected;

        /// <summary>Raised with the slot index when the player picks this move.</summary>
        public Action<int> Chosen;

        /// <summary>Binds a move slot. Pass a null forecast when none is available.</summary>
        public void Bind(int index, MoveSlot slot, DamageForecast? forecast, bool immediate = false)
        {
            _index = index;
            var move = UiServices.MoveOf(slot.MoveId);
            _type = move?.Type ?? ElementType.None;

            gameObject.SetActive(!string.IsNullOrEmpty(slot.MoveId));
            if (string.IsNullOrEmpty(slot.MoveId)) return;

            if (_name != null) _name.SetText(UiServices.MoveName(slot.MoveId));

            if (_pp != null)
            {
                // mspace pins every digit to the same advance, so a move dropping from 10 to 9
                // does not shuffle the whole column sideways — the one place in this UI where
                // proportional figures are actually noticeable, because four of them stack.
                _pp.SetText($"<mspace=0.60em>{slot.CurrentPp}/{slot.MaxPp}</mspace>");
                // PP turns amber below a third and red at zero: the player should feel a move
                // running out before it actually does.
                var ratio = slot.MaxPp > 0 ? slot.CurrentPp / (float)slot.MaxPp : 0f;
                _pp.color = slot.CurrentPp == 0 ? UiPalette.Negative
                    : (ratio <= 0.34f ? UiPalette.Caution : UiPalette.TextSecondary);
            }

            if (_typeChip != null) _typeChip.color = UiPalette.TypeDeep(_type);
            if (_typeGlyph != null)
            {
                _typeGlyph.sprite = UiTypeIcons.Get(_type, 56);
                _typeGlyph.color = UiPalette.Type(_type);
            }

            BindForecast(forecast);
            SetUsable(slot.Usable);
            ApplySelection();
        }

        private void BindForecast(DamageForecast? forecast)
        {
            var label = forecast.HasValue
                ? VerdictLabel(forecast.Value.Effectiveness, forecast.Value.TypeMultiplier)
                : string.Empty;
            var hasVerdict = !string.IsNullOrEmpty(label);

            if (_verdict != null)
            {
                _verdict.SetText(label);
                _verdict.color = forecast.HasValue
                    ? UiPalette.Effectiveness(forecast.Value.Effectiveness)
                    : UiPalette.TextMuted;
            }
            if (_verdictDot != null)
            {
                _verdictDot.enabled = hasVerdict;
                if (hasVerdict) _verdictDot.color = UiPalette.Effectiveness(forecast.Value.Effectiveness);
            }
        }

        /// <summary>
        /// Marks this pill as the cursor's. Drives fill, rim, chevron and width together —
        /// four cues rather than one, because a colour change alone is invisible to a player
        /// whose eyes are on the creatures rather than the menu.
        /// </summary>
        public void SetSelected(bool selected)
        {
            if (_selected == selected) return;
            _selected = selected;
            ApplySelection();
        }

        private void ApplySelection()
        {
            if (_panel != null)
            {
                // Lerped toward the raised surface rather than to the raw type colour: the
                // eighteen type colours span a huge luminance range, and filling the pill with
                // Electric yellow at full strength would leave the white move name unreadable
                // on exactly one of them.
                _panel.color = _selected
                    ? Color.Lerp(UiPalette.SurfaceRaised, UiPalette.Type(_type), 0.40f).WithAlpha(0.98f)
                    : Color.Lerp(UiPalette.Backdrop, UiPalette.TypeDeep(_type), 0.90f).WithAlpha(0.90f);
            }
            if (_rim != null)
            {
                _rim.color = _selected ? UiPalette.Type(_type).WithAlpha(0.95f) : Color.white.WithAlpha(0.08f);
            }
            if (_chevron != null) _chevron.gameObject.SetActive(_selected);
            if (_sizing != null) _sizing.preferredWidth = _selected ? SelectedWidth : Width;
            if (_name != null) _name.color = _selected ? UiPalette.TextPrimary : UiPalette.TextSecondary;
        }

        /// <summary>Greys the card out when the move cannot be used (no PP, disabled, locked).</summary>
        public void SetUsable(bool usable)
        {
            _usable = usable;
            if (_group != null)
            {
                _group.alpha = usable ? 1f : 0.42f;
                _group.interactable = usable;
                _group.blocksRaycasts = usable;
            }
            if (_button != null) _button.interactable = usable;
        }

        /// <summary>Plays the confirm flourish before the caller closes the panel.</summary>
        public void PlayConfirm()
        {
            UiTween.Punch(transform, 0.06f, 0.22f);
        }

        /// <summary>Raises <see cref="Chosen"/> as if the pill had been clicked. For keyboard confirm.</summary>
        public void Take()
        {
            if (!_usable) return;
            PlayConfirm();
            Chosen?.Invoke(_index);
        }

        /// <summary>
        /// The one-line verdict, in the register a Korean player expects to read it in.
        ///
        /// Three positive-to-negative rungs rather than the games' two, because the forecast
        /// already distinguishes a double weakness from a single one and that is the difference
        /// between "this ends the fight" and "this is a good pick". Neutral returns empty; see
        /// the class remarks.
        /// </summary>
        private static string VerdictLabel(Effectiveness effectiveness, float multiplier) => effectiveness switch
        {
            Effectiveness.SuperEffective when multiplier >= 3.9f =>
                Core.Loc.Pick("Devastating", "효과가 굉장하다"),
            Effectiveness.SuperEffective =>
                Core.Loc.Pick("Super effective", "효과가 좋다"),
            Effectiveness.NotVeryEffective =>
                Core.Loc.Pick("Not very effective", "효과가 별로다"),
            Effectiveness.Immune =>
                Core.Loc.Pick("No effect", "효과가 없다"),
            _ => string.Empty,
        };

        /// <summary>Builds one move pill, sized for the vertical stack in the command panel.</summary>
        public static MoveButtonView Build(Transform parent)
        {
            var root = UiBuilder.Rect("MoveButton", parent, false);
            var view = root.gameObject.AddComponent<MoveButtonView>();
            view._group = UiBuilder.Group(root);
            view._sizing = UiBuilder.Size(root, preferredWidth: Width, preferredHeight: RowHeight,
                minHeight: RowHeight);

            view._panel = UiBuilder.Image("Panel", root, UiSprites.Panel(Radius), UiPalette.SurfaceRaised,
                Image.Type.Sliced, true);
            UiBuilder.Stretch(view._panel.rectTransform);

            view._rim = UiBuilder.Image("Rim", root, UiSprites.Frame(Radius, 2), Color.white.WithAlpha(0.08f));
            UiBuilder.Stretch(view._rim.rectTransform);

            // Sits outside the pill's left edge and points into it. Outside rather than inside
            // because the selected pill is also the widest one, and a cursor drawn within the
            // pill would move with the width change instead of holding a fixed column.
            var chevron = UiBuilder.Image("Chevron", root, UiSprites.Chevron(30), UiPalette.ScannerAmber,
                Image.Type.Simple);
            chevron.preserveAspect = true;
            // Centre pivot, not an edge pivot: a RectTransform rotates about its pivot, and a
            // right-edge pivot would swing the glyph up above the pill instead of leaving it
            // beside it. Centred at -25 puts its 26px box in the span [-38, -12].
            UiBuilder.Anchor(chevron.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-25f, 0f), new Vector2(26f, 26f));
            // The one chevron sprite is drawn pointing up; -90° turns it to point right.
            chevron.rectTransform.localEulerAngles = new Vector3(0f, 0f, -90f);
            view._chevron = chevron.rectTransform;
            chevron.gameObject.SetActive(false);

            var row = UiBuilder.Rect("Row", root);
            UiBuilder.Horizontal(row, 18f, new RectOffset(18, 24, 0, 0), TextAnchor.MiddleLeft);

            // --- type mark
            var chip = UiBuilder.Rect("TypeChip", row, false);
            UiBuilder.Size(chip, preferredWidth: 60f, preferredHeight: 60f, minWidth: 60f, minHeight: 60f);
            view._typeChip = UiBuilder.Image("Bg", chip, UiSprites.Panel(14), UiPalette.SurfaceSunken);
            UiBuilder.Stretch(view._typeChip.rectTransform);
            view._typeGlyph = UiBuilder.Image("Glyph", chip, UiTypeIcons.Get(ElementType.None, 56),
                UiPalette.TextMuted, Image.Type.Simple);
            view._typeGlyph.preserveAspect = true;
            UiBuilder.Stretch(view._typeGlyph.rectTransform, 9f);

            // --- name over verdict
            // Height as well as width.
            //
            // The row's HorizontalLayoutGroup has childControlHeight on and
            // childForceExpandHeight off, so a child with no height of its own collapses to
            // zero — and a zero-height column gives its name and verdict zero-height rects.
            // The pill drew its type mark and its PP, which carry their own sizes, and nothing
            // in between: every move in the game was a coloured icon and a number with no name.
            const float nameHeight = 40f;
            const float verdictHeight = 30f;

            var column = UiBuilder.Rect("Text", row);
            UiBuilder.Vertical(column, 2f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(column, flexibleWidth: 1f, minWidth: 180f,
                preferredHeight: nameHeight + verdictHeight + 2f,
                minHeight: nameHeight + verdictHeight + 2f);

            view._name = UiBuilder.Text("Name", column, "—", UiTextRole.Body, UiPalette.TextSecondary);
            view._name.fontStyle = FontStyles.Bold;
            view._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(view._name.rectTransform, preferredHeight: nameHeight, minHeight: nameHeight, flexibleWidth: 1f);

            var verdictRow = UiBuilder.Rect("Verdict", column);
            UiBuilder.Horizontal(verdictRow, 9f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(verdictRow, preferredHeight: verdictHeight, minHeight: verdictHeight, flexibleWidth: 1f);

            var dot = UiBuilder.Rect("Dot", verdictRow, false);
            UiBuilder.Size(dot, preferredWidth: 14f, preferredHeight: 14f, minWidth: 14f, minHeight: 14f);
            view._verdictDot = UiBuilder.Image("Fill", dot, UiSprites.Dot(16), UiPalette.TextMuted,
                Image.Type.Simple);
            UiBuilder.Stretch(view._verdictDot.rectTransform);
            view._verdictDot.enabled = false;

            view._verdict = UiBuilder.Text("Label", verdictRow, string.Empty, UiTextRole.Secondary,
                UiPalette.TextSecondary);
            view._verdict.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(view._verdict.rectTransform, flexibleWidth: 1f, minWidth: 120f);

            // --- PP
            view._pp = UiBuilder.Text("Pp", row, "0/0", UiTextRole.Numeric, UiPalette.TextSecondary,
                TextAlignmentOptions.Right);
            UiBuilder.Size(view._pp.rectTransform, preferredWidth: 132f, minWidth: 132f);

            UiButtonMotion.Attach(root, Radius);
            view._button = UiBuilder.Button("Choose", root, view._panel, view.Take);
            view.ApplySelection();
            return view;
        }
    }
}
