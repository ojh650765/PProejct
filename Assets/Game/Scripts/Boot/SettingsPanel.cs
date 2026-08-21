using System;
using PokeLab.Audio;
using PokeLab.Core;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.Boot
{
    /// <summary>
    /// Volume, and the two other switches worth exposing.
    ///
    /// <b>Why this had to exist the moment the master volume moved.</b> The build now ships at
    /// 50% master — the user's instruction — and until this screen there was no way for a
    /// player to change it back. Shipping a quieter game with no volume control is not a
    /// setting, it is a decision made on the player's behalf and hidden from them.
    ///
    /// <b>Every slider takes effect while it is being dragged.</b> Audio is the one settings
    /// category where a preview is not a nicety: a number between 0 and 1 means nothing until
    /// you hear it, and a screen that applies on close asks the player to guess. Each change
    /// also plays a short cue through the bus being moved, so dragging the SFX slider makes SFX
    /// noise and dragging the music slider does not — you hear exactly the thing you are
    /// setting.
    ///
    /// <b>Reset exists for one specific failure.</b> Drag master to zero and the game goes
    /// silent, and silence gives no feedback that anything is still responding — the player has
    /// no way to hear their way back out. The reset button is the way out.
    ///
    /// Persistence is <see cref="AudioDirector"/>'s: it writes each change to PlayerPrefs
    /// immediately, because there is no OK button here to commit on and a browser tab can be
    /// closed without any quit callback ever running.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SettingsPanel : MonoBehaviour
    {
        /// <summary>Raised when the panel closes, so the screen behind it can redraw.</summary>
        public Action Closed;

        public bool IsOpen { get; private set; }

        private AudioDirector _audio;
        private TextMeshProUGUI _status;

        private void Awake() => gameObject.SetActive(false);

        public void Open()
        {
            gameObject.SetActive(true);
            IsOpen = true;
            _audio = ResolveAudio();
            Build();
        }

        public void Close()
        {
            IsOpen = false;
            gameObject.SetActive(false);
            Closed?.Invoke();
        }

        private static AudioDirector ResolveAudio() =>
            ServiceHub.TryGet<AudioDirector>(out var director)
                ? director
                : FindAnyObjectByType<AudioDirector>();

        // --- Layout ---------------------------------------------------------------------

        private void Build()
        {
            var root = (RectTransform)transform;
            UiBuilder.ClearChildren(root);
            UiBuilder.Stretch(root);

            var scrim = UiBuilder.Backdrop("Scrim", root, null,
                new Color(UiPalette.Scrim.r, UiPalette.Scrim.g, UiPalette.Scrim.b, 0.9f), true);
            UiBuilder.Stretch(scrim.rectTransform);

            var card = UiBuilder.Rect("Card", root, false);
            UiBuilder.Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(840f, 640f));

            var backing = UiBuilder.Panel("Backing", card, UiPalette.Surface, 24);
            UiBuilder.Stretch(backing.rectTransform);

            var title = UiBuilder.Text("Title", card, Loc.Pick("Settings", "설정"), UiTextRole.Title,
                UiPalette.TextPrimary, TextAlignmentOptions.Left);
            UiBuilder.Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(40f, -30f), new Vector2(760f, 64f));

            var y = -110f;

            if (_audio != null)
            {
                y = Slider(card, y, Loc.Pick("MASTER", "전체"), AudioBus.Master);
                y = Slider(card, y, Loc.Pick("MUSIC", "음악"), AudioBus.Music);
                y = Slider(card, y, Loc.Pick("EFFECTS", "효과음"), AudioBus.Sfx);
                y = Slider(card, y, Loc.Pick("AMBIENCE", "환경음"), AudioBus.Ambience);
            }
            else
            {
                var none = UiBuilder.Text("NoAudio", card,
                    Loc.Pick("No audio director in this scene.", "이 씬에는 오디오 디렉터가 없어요."),
                    UiTextRole.Secondary, UiPalette.TextMuted, TextAlignmentOptions.Left);
                UiBuilder.Anchor(none.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(0f, 1f), new Vector2(40f, y), new Vector2(760f, 40f));
                y -= 56f;
            }

            y -= 10f;
            y = Toggle(card, y, Loc.Pick("MOTION", "화면 효과"),
                Loc.Pick("Menu and reveal animation.", "메뉴와 연출 애니메이션."),
                UiTween.MotionEnabled, on => UiTween.MotionEnabled = on);

            _status = UiBuilder.Text("Status", card, "", UiTextRole.Caption,
                UiPalette.TextMuted, TextAlignmentOptions.Left);
            UiBuilder.Anchor(_status.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(40f, 110f), new Vector2(760f, 34f));

            Button(card, Loc.Pick("Reset", "기본값"), UiPalette.SurfaceRaised, UiPalette.TextPrimary,
                new Vector2(40f, 34f), new Vector2(0f, 0f), () =>
                {
                    _audio?.ResetVolumesToDefaults();
                    Build();
                    Say(Loc.Pick("Restored to defaults.", "기본값으로 되돌렸어요."));
                });

            Button(card, Loc.Pick("Close", "닫기"), UiPalette.Info, UiPalette.TextOnAccent,
                new Vector2(-40f, 34f), new Vector2(1f, 0f), Close);
        }

        /// <summary>
        /// One labelled slider, wired straight through to the mixer.
        ///
        /// The percentage is drawn as its own label rather than left implicit: a bare handle on
        /// a track tells the player where it is relative to itself and nothing else, and "50%"
        /// is what they will want to say when they ask somebody why the game is quiet.
        /// </summary>
        private float Slider(Transform card, float y, string label, AudioBus bus)
        {
            var caption = UiBuilder.Text("Label_" + bus, card, label, UiTextRole.Overline,
                UiPalette.ScannerCyan, TextAlignmentOptions.Left);
            UiBuilder.Anchor(caption.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(40f, y), new Vector2(400f, 28f));

            var readout = UiBuilder.Text("Value_" + bus, card, "", UiTextRole.Body,
                UiPalette.TextPrimary, TextAlignmentOptions.Right);
            UiBuilder.Anchor(readout.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(40f, y - 2f), new Vector2(760f, 34f));

            var track = UiBuilder.Rect("Track_" + bus, card, false);
            UiBuilder.Anchor(track, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(40f, y - 36f), new Vector2(760f, 26f));

            var groove = UiBuilder.Panel("Groove", track, UiPalette.SurfaceSunken, 12);
            UiBuilder.Stretch(groove.rectTransform);

            var fill = UiBuilder.Panel("Fill", track, UiPalette.ScannerCyan, 12);
            UiBuilder.Anchor(fill.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), Vector2.zero, new Vector2(0f, 0f));

            var handle = UiBuilder.Rect("Handle", track, false);
            var knob = UiBuilder.Panel("Knob", handle, UiPalette.TextPrimary, 10);
            UiBuilder.Stretch(knob.rectTransform);

            var slider = track.gameObject.AddComponent<UnityEngine.UI.Slider>();
            slider.transition = Selectable.Transition.None;
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle;
            slider.targetGraphic = groove;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = _audio != null ? _audio.GetBusVolume(bus) : 1f;

            readout.text = Percent(slider.value);

            slider.onValueChanged.AddListener(value =>
            {
                _audio?.SetBusVolume(bus, value);
                readout.text = Percent(value);
                Preview(bus);
            });

            return y - 82f;
        }

        private static string Percent(float value01) =>
            Mathf.RoundToInt(Mathf.Clamp01(value01) * 100f) + "%";

        /// <summary>
        /// A short cue on the bus being changed, rate-limited so a drag is not a machine gun.
        ///
        /// Deliberately silent for Master: every other bus routes through it, so the cue from
        /// whichever slider was touched last already demonstrates it, and adding another would
        /// only stack two sounds on one drag.
        /// </summary>
        private void Preview(AudioBus bus)
        {
            if (_audio == null || bus == AudioBus.Master) return;
            if (Time.unscaledTime < _nextPreview) return;
            _nextPreview = Time.unscaledTime + 0.12f;

            switch (bus)
            {
                case AudioBus.Sfx:
                    _audio.PlaySfx(AudioIds.BattleHpTick);
                    break;
                case AudioBus.Ui:
                    _audio.PlayUi(AudioIds.UiNavigate);
                    break;
                // Music and ambience are continuous and already audible while being dragged;
                // a one-shot on top of them would be a third sound, not a preview.
            }
        }

        private float _nextPreview;

        private float Toggle(Transform card, float y, string label, string detail, bool value,
                             Action<bool> onChanged)
        {
            var caption = UiBuilder.Text("Label_" + label, card, label, UiTextRole.Overline,
                UiPalette.ScannerCyan, TextAlignmentOptions.Left);
            UiBuilder.Anchor(caption.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(40f, y), new Vector2(400f, 28f));

            var hint = UiBuilder.Text("Hint_" + label, card, detail, UiTextRole.Caption,
                UiPalette.TextMuted, TextAlignmentOptions.Left);
            UiBuilder.Anchor(hint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(40f, y - 30f), new Vector2(500f, 28f));

            var state = value;
            var button = UiBuilder.Rect("Toggle_" + label, card, false);
            UiBuilder.Anchor(button, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-40f, y - 4f), new Vector2(160f, 56f));

            var slab = UiBuilder.Panel("Slab", button,
                state ? UiPalette.Positive : UiPalette.SurfaceRaised, 14);
            UiBuilder.Stretch(slab.rectTransform);

            var text = UiBuilder.Text("State", button,
                state ? Loc.Pick("ON", "켜짐") : Loc.Pick("OFF", "꺼짐"), UiTextRole.Body,
                state ? UiPalette.TextOnAccent : UiPalette.TextPrimary, TextAlignmentOptions.Center);
            UiBuilder.Stretch(text.rectTransform);

            UiBuilder.Button("Take", button, slab, () =>
            {
                state = !state;
                onChanged?.Invoke(state);
                slab.color = state ? UiPalette.Positive : UiPalette.SurfaceRaised;
                text.text = state ? Loc.Pick("ON", "켜짐") : Loc.Pick("OFF", "꺼짐");
                text.color = state ? UiPalette.TextOnAccent : UiPalette.TextPrimary;
            });

            return y - 84f;
        }

        private static void Button(Transform parent, string label, Color accent, Color ink,
                                   Vector2 offset, Vector2 anchor, Action onClick)
        {
            var button = UiBuilder.Rect("Button_" + label, parent, false);
            UiBuilder.Anchor(button, anchor, anchor, anchor, offset, new Vector2(200f, 60f));

            var slab = UiBuilder.Panel("Slab", button, accent, 14);
            UiBuilder.Stretch(slab.rectTransform);

            var caption = UiBuilder.Text("Label", button, label, UiTextRole.Body, ink,
                TextAlignmentOptions.Center);
            UiBuilder.Stretch(caption.rectTransform);

            UiBuilder.Button("Take", button, slab, onClick);
        }

        private void Say(string message)
        {
            if (_status != null) _status.text = message ?? "";
        }

        private void Update()
        {
            if (!IsOpen) return;
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) Close();
        }
    }
}
