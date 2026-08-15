using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The three-letter status chip (BRN, PSN, PAR…) shown beside a creature's name.
    ///
    /// It animates in and out rather than toggling, because a status landing is one of the
    /// two or three moments per battle the player most needs to notice, and a chip that
    /// simply appears between frames is routinely missed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StatusBadge : MonoBehaviour
    {
        [SerializeField] private Image _background;
        [SerializeField] private TextMeshProUGUI _label;
        [SerializeField] private CanvasGroup _group;

        private StatusCondition _status = StatusCondition.None;
        private TweenHandle _tween;

        public StatusCondition Status => _status;

        /// <summary>Short label for a status. Kept in one place so log and badge never disagree.</summary>
        public static string Abbreviation(StatusCondition status) => status switch
        {
            StatusCondition.Burn => "BRN",
            StatusCondition.Freeze => "FRZ",
            StatusCondition.Paralysis => "PAR",
            StatusCondition.Poison => "PSN",
            StatusCondition.BadPoison => "TOX",
            StatusCondition.Sleep => "SLP",
            StatusCondition.Fainted => "FNT",
            _ => string.Empty,
        };

        /// <summary>Full name, for the scanner's threat detail and the battle log.</summary>
        public static string FullName(StatusCondition status) => status switch
        {
            StatusCondition.Burn => "Burned",
            StatusCondition.Freeze => "Frozen",
            StatusCondition.Paralysis => "Paralysed",
            StatusCondition.Poison => "Poisoned",
            StatusCondition.BadPoison => "Badly poisoned",
            StatusCondition.Sleep => "Asleep",
            StatusCondition.Fainted => "Fainted",
            _ => "Healthy",
        };

        /// <summary>Sets the status, animating the transition unless <paramref name="immediate"/>.</summary>
        public void Bind(StatusCondition status, bool immediate = false)
        {
            if (status == _status && !immediate) return;
            var previous = _status;
            _status = status;

            if (status == StatusCondition.None)
            {
                UiTween.Kill(ref _tween);
                if (immediate)
                {
                    gameObject.SetActive(false);
                    return;
                }
                _tween = UiTween.Fade(_group, 0f, 0.2f, Ease.InCubic, 0f, () => gameObject.SetActive(false));
                return;
            }

            gameObject.SetActive(true);
            if (_label != null)
            {
                _label.SetText(Abbreviation(status));
                _label.color = Color.white;
            }
            if (_background != null) _background.color = UiPalette.Status(status);

            UiTween.Kill(ref _tween);
            if (immediate)
            {
                if (_group != null) _group.alpha = 1f;
                transform.localScale = Vector3.one;
                return;
            }

            if (_group != null) _group.alpha = 0f;
            _tween = UiTween.Fade(_group, 1f, 0.22f);
            // A fresh status pops; a status swapping to another status only fades.
            if (previous == StatusCondition.None) UiTween.Scale(transform, Vector3.one, 0.34f, Ease.OutBack);
        }

        /// <summary>Builds a badge sized for the battle HUD name row.</summary>
        public static StatusBadge Build(Transform parent, float height = 20f)
        {
            var root = UiBuilder.Rect("StatusBadge", parent, false);
            UiBuilder.Size(root, preferredWidth: height * 2.2f, preferredHeight: height,
                minWidth: height * 2.2f, minHeight: height);

            var background = UiBuilder.Image("Bg", root, UiSprites.Pill(Mathf.RoundToInt(height)), UiPalette.TextMuted);
            UiBuilder.Stretch(background.rectTransform);

            var label = UiBuilder.Text("Label", root, string.Empty, UiTextRole.Caption, Color.white, TextAlignmentOptions.Center);
            label.fontSize = height * 0.55f;
            label.fontStyle = FontStyles.Bold;
            label.characterSpacing = 1f;
            UiBuilder.Stretch(label.rectTransform);

            var badge = root.gameObject.AddComponent<StatusBadge>();
            badge._background = background;
            badge._label = label;
            badge._group = UiBuilder.Group(badge, 0f, false, false);
            badge.gameObject.SetActive(false);
            return badge;
        }
    }
}
