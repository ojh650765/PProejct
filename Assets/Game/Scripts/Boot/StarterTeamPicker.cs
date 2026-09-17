using System;
using PokeLab.Core;
using PokeLab.Online;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.Boot
{
    public sealed class StarterTeamPicker : MonoBehaviour
    {
        private bool _busy;
        private TextMeshProUGUI _note;
        private Action _chosen;
        public static StarterTeamPicker Show(Transform parent, GachaGroup[] groups, Action chosen)
        {
            var root = UiBuilder.Rect("FreeTeamPicker", parent, false);
            UiBuilder.Stretch(root);
            var picker = root.gameObject.AddComponent<StarterTeamPicker>();
            picker._chosen = chosen;
            var shade = UiBuilder.Image("Shade", root, UiSprites.Panel(0), UiPalette.AceInk.WithAlpha(0.97f));
            UiBuilder.Stretch(shade.rectTransform);
            shade.raycastTarget = true;
            var card = UiBuilder.Rect("Card", root, false);
            UiBuilder.Anchor(card, new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(.5f,.5f), Vector2.zero, new Vector2(1460, 930));
            UiBuilder.Vertical(card, 14, new RectOffset(30,30,24,24));
            var title = UiBuilder.Text("Title", card, Loc.Pick("Choose your first team", "함께 시작할 팀을 고르세요"), UiTextRole.Title, UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Size(title.rectTransform, preferredHeight: 72, flexibleWidth: 1);
            picker._note = UiBuilder.Text("Note", card, Loc.Pick("Five free groups · six Pokémon each · choose one", "무료 5개 조합 · 조합마다 6마리 · 한 팀 선택"), UiTextRole.Body, UiPalette.AceTextDim, TextAlignmentOptions.Center);
            UiBuilder.Size(picker._note.rectTransform, preferredHeight: 50, flexibleWidth: 1);
            for (int group = 0; group < groups.Length; group++)
            {
                int index = group;
                var row = UiBuilder.Rect("Group_" + group, card, false);
                UiBuilder.Size(row, preferredHeight: 132, flexibleWidth: 1);
                var pane = UiJuice.Pane("Pane", row, UiPalette.AceGlassLift, 16, false, true, true, UiPalette.AceRim, 132);
                var number = UiBuilder.Text("Number", row, (group + 1).ToString(), UiTextRole.Heading, UiPalette.AceCyan, TextAlignmentOptions.Center);
                UiBuilder.Anchor(number.rectTransform, new Vector2(0,.5f),new Vector2(0,.5f), new Vector2(0,.5f), new Vector2(8,0),new Vector2(60,80));
                for (int slot = 0; slot < groups[group].pulls.Length; slot++)
                {
                    var pull = groups[group].pulls[slot];
                    var sprite = UiBuilder.Image("Pokemon_" + slot, row, CreatureThumbnail.Front(pull.speciesId) ?? UiSprites.BallGlyph(96,8), Color.white, Image.Type.Simple);
                    sprite.preserveAspect = true;
                    UiBuilder.Anchor(sprite.rectTransform, new Vector2(0,.5f),new Vector2(0,.5f),new Vector2(0,.5f),new Vector2(80+slot*176,15),new Vector2(130,88));
                    string name = UiServices.SpeciesName(pull.speciesId);
                    var label = UiBuilder.Text("Name_"+slot,row,name,UiTextRole.Caption,UiPalette.AceText,TextAlignmentOptions.Center);
                    UiBuilder.Anchor(label.rectTransform,new Vector2(0,0),new Vector2(0,0),Vector2.zero,new Vector2(70+slot*176,7),new Vector2(154,30));
                    label.enableAutoSizing = true; label.fontSizeMin = 14; label.fontSizeMax = 24;
                }
                var pick = UiBuilder.Text("Choose",row,Loc.Pick("Choose", "선택"),UiTextRole.Body,UiPalette.AceLime,TextAlignmentOptions.Center);
                UiBuilder.Anchor(pick.rectTransform,new Vector2(1,.5f),new Vector2(1,.5f),new Vector2(1,.5f),new Vector2(-12,0),new Vector2(160,65));
                UiBuilder.Button("Choose",row,pane.Fill,()=>picker.Choose(index));
            }
            return picker;
        }

        private void Choose(int index)
        {
            if (_busy) return;
            _busy = true;
            foreach (var button in GetComponentsInChildren<Button>()) button.interactable = false;
            _note.text = Loc.Pick("Saving your team…", "팀을 저장하는 중…");
            var session = OnlineSession.Instance;
            // The session owns the request, so closing this panel cannot abort a committed choice.
            session.StartCoroutine(session.StarterGacha(index, response =>
            {
                if (this == null) return;
                if (response == null)
                {
                    _busy = false;
                    foreach (var button in GetComponentsInChildren<Button>()) button.interactable = true;
                    _note.text = OnlineClient.Explain(session.LastError);
                    return;
                }
                _chosen?.Invoke();
                Destroy(gameObject);
            }));
        }

        private void Update() { if (!_busy) UiKeyboardCursor.Update(transform); }
    }
}
