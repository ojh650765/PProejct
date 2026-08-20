using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// A yes/no the player has to answer before anything else happens.
    ///
    /// Built for one caller and written to be reusable by the next: erasing a save is the first
    /// thing in this game with no undo, and a menu that erases one on a second press of Enter
    /// is a menu that will eventually cost somebody an afternoon.
    ///
    /// <b>The cancel is the default.</b> The cursor opens on the safe answer, Escape takes it,
    /// and the destructive button is the one that has to be moved to — every one of those is a
    /// separate chance for a mistyped press to land harmlessly.
    ///
    /// <b>The selection is a whole state, not an outline.</b> The chosen button inverts to
    /// near-white with dark text and grows a lime cursor beside it, exactly as a menu row does,
    /// and the destructive one is the only red thing on the card. On a dialog with two buttons
    /// and no undo, "which one is selected" has to be legible from across the room.
    ///
    /// It scrims the whole screen and raycast-blocks it, so the menu underneath cannot be
    /// clicked while the question is up; the caller's own keyboard handling stands down on a
    /// flag rather than being disabled, because a caller that forgets to re-enable itself is a
    /// menu that stops responding for the rest of the session.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConfirmDialog : MonoBehaviour
    {
        /// <summary>The parts of one button the selection state has to reach.</summary>
        private sealed class Choice
        {
            public RectTransform Lift;
            public UiPane Pane;
            public RectTransform Cursor;
            public TextMeshProUGUI Caption;
            public Color Accent;
            public bool Destructive;
        }

        private Action<bool> _answer;
        private bool _confirmHighlighted;
        private Choice _confirm;
        private Choice _cancel;
        private bool _answered;

        /// <summary>Puts the question on screen. The callback fires exactly once.</summary>
        public static ConfirmDialog Show(Transform parent, string title, string body,
                                         string confirmLabel, string cancelLabel,
                                         Action<bool> answer)
        {
            var host = new GameObject("ConfirmDialog", typeof(RectTransform));
            host.transform.SetParent(parent, false);

            var dialog = host.AddComponent<ConfirmDialog>();
            dialog._answer = answer;
            dialog.Build(title, body, confirmLabel, cancelLabel);
            return dialog;
        }

        private void Build(string title, string body, string confirmLabel, string cancelLabel)
        {
            var root = (RectTransform)transform;
            UiBuilder.Stretch(root);

            // Raycast true: this is what stops the menu underneath being clicked through. It
            // fades in rather than appearing, because the card behind it stays visible and a
            // hard cut to 70% black reads as a rendering glitch.
            var scrim = UiBuilder.Backdrop("Scrim", root, null, UiPalette.AceNight.WithAlpha(0f), true);
            UiBuilder.Stretch(scrim.rectTransform);
            UiTween.Color(scrim.color, UiPalette.AceNight.WithAlpha(0.78f), 0.2f,
                c => { if (scrim != null) scrim.color = c; });

            var card = UiBuilder.Rect("Card", root, false);
            UiBuilder.Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(780f, 380f));

            UiJuice.Pane("Glass", card, UiPalette.AceGlass.WithAlpha(0.94f), 24, true, true, true,
                UiPalette.AceRim, 180);

            var stripe = UiBuilder.Image("Stripe", card, UiSprites.Pill(8), UiPalette.AceRed);
            UiBuilder.Anchor(stripe.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(-460f, 8f));

            // 76 and not 52: UiType gives every label overflowMode Ellipsis, and TMP draws
            // nothing at all when a line is taller than its rect, so a 50pt Title needs real
            // headroom rather than an exact fit.
            var heading = UiBuilder.Text("Title", card, title, UiTextRole.Title,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(heading.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(44f, -36f), new Vector2(-88f, 76f));

            var text = UiBuilder.Text("Body", card, body, UiTextRole.Secondary,
                UiPalette.AceTextDim, TextAlignmentOptions.TopLeft);
            UiBuilder.Anchor(text.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(44f, -120f), new Vector2(-88f, 110f));

            _cancel = BuildButton(card, cancelLabel, UiPalette.AceGlassLift,
                new Vector2(-44f, 40f), new Vector2(1f, 0f), false, () => Answer(false));
            _confirm = BuildButton(card, confirmLabel, UiPalette.AceRed,
                new Vector2(-296f, 40f), new Vector2(1f, 0f), true, () => Answer(true));

            Highlight(false);

            // Lands rather than appears. A modal that fades in is a page; one that drops in and
            // settles is an object that just arrived on top of what you were doing.
            UiJuice.PopScale(card, 0f, 0.8f, 0.36f);
        }

        private Choice BuildButton(Transform card, string label, Color accent, Vector2 offset,
                                   Vector2 anchor, bool destructive, Action onClick)
        {
            var button = UiBuilder.Rect("Button_" + label, card, false);
            UiBuilder.Anchor(button, anchor, anchor, anchor, offset, new Vector2(236f, 74f));

            var lift = UiBuilder.Rect("Lift", button);

            var pane = UiJuice.Pane("Pane", lift, accent.WithAlpha(destructive ? 0.92f : 0.7f),
                16, false, true, true, UiPalette.AceRim, 74);

            var caption = UiBuilder.Text("Label", lift, label, UiTextRole.Body, UiPalette.AceText,
                TextAlignmentOptions.Center);
            UiBuilder.Anchor(caption.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), new Vector2(14f, 0f), new Vector2(-52f, -12f));

            // Inside the button, not in the margin beside it. Two buttons sixteen pixels apart
            // leave no margin to put it in: the first capture of this dialog had the lime mark
            // sitting on top of the destructive button next door, which reads as that one being
            // selected — on the one screen in the game where being wrong about that costs a save.
            var cursor = UiJuice.Cursor(lift, 34f);
            cursor.anchoredPosition = new Vector2(28f, 0f);
            cursor.gameObject.SetActive(false);

            UiBuilder.Button("Take", button, pane.Fill, onClick);

            return new Choice
            {
                Lift = lift,
                Pane = pane,
                Cursor = cursor,
                Caption = caption,
                Accent = accent,
                Destructive = destructive,
            };
        }

        private void Highlight(bool confirm)
        {
            _confirmHighlighted = confirm;
            Apply(_confirm, confirm);
            Apply(_cancel, !confirm);
        }

        private static void Apply(Choice choice, bool selected)
        {
            if (choice == null) return;

            // Selected inverts to near-white, exactly as a menu row does. The destructive
            // button keeps its red rim even when it is not the one selected, so the answer that
            // cannot be undone is never the quiet one on the card.
            UiJuice.Recolour(choice.Pane,
                selected ? UiPalette.AceSelect : choice.Accent.WithAlpha(choice.Destructive ? 0.92f : 0.7f),
                selected ? UiPalette.AceLime.WithAlpha(0.9f)
                    : choice.Destructive ? UiPalette.AceRed.WithAlpha(0.8f) : UiPalette.AceRim);

            if (choice.Caption != null)
            {
                var caption = choice.Caption;
                var to = selected ? UiPalette.AceInk : UiPalette.AceText;
                UiTween.Color(caption.color, to, 0.16f, c => { if (caption != null) caption.color = c; });
            }

            if (choice.Lift != null)
                UiTween.Scale(choice.Lift, Vector3.one * (selected ? 1.05f : 1f), 0.2f, Ease.OutBack);

            if (choice.Cursor != null && choice.Cursor.gameObject.activeSelf != selected)
                choice.Cursor.gameObject.SetActive(selected);
        }

        private void Answer(bool confirmed)
        {
            // Guarded, because a click and a keypress in the same frame would otherwise answer
            // twice — and the destructive branch is not one to run a second time.
            if (_answered) return;
            _answered = true;

            var callback = _answer;
            _answer = null;
            UiSound.Confirm();

            // Squashed before it goes, so the answer is seen to be taken. The dialog is torn
            // down out of the squash rather than on the frame the key went down; the guard
            // above is what stops anything else being answered in that window.
            var chosen = confirmed ? _confirm : _cancel;
            UiJuice.Squash(chosen != null ? chosen.Lift : null, () =>
            {
                if (this != null) Destroy(gameObject);
                callback?.Invoke(confirmed);
            });
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame
                || keyboard.tabKey.wasPressedThisFrame)
            {
                Highlight(!_confirmHighlighted);
                UiSound.Navigate();
            }

            if (keyboard.escapeKey.wasPressedThisFrame) { Answer(false); return; }

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                Answer(_confirmHighlighted);
        }
    }
}
