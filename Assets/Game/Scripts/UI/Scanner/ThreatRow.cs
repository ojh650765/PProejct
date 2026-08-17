using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// One identified danger on the field.
    ///
    /// The incoming KO chance is the number that actually changes a player's decision, so it
    /// gets a dedicated radial dial and the largest type on the row — the headline explains
    /// it, but the dial is what is read first. Rows animate in from the right and animate out
    /// rather than vanishing, because a threat clearing is information too.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ThreatRow : MonoBehaviour
    {
        [SerializeField] private Image _levelPip;
        [SerializeField] private Image _background;
        [SerializeField] private TextMeshProUGUI _levelLabel;
        [SerializeField] private TextMeshProUGUI _headline;
        [SerializeField] private TextMeshProUGUI _detail;
        [SerializeField] private Image _koDial;
        [SerializeField] private AnimatedNumber _koNumber;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _rect;

        private TweenHandle _dialTween;
        private TweenHandle _lifecycleTween;
        private float _koFraction;
        private bool _bound;

        /// <summary>Stable identity for diffing between readouts: source move plus headline.</summary>
        public string Key { get; private set; }

        /// <summary>Human name for a threat level. One place, so log and scanner agree.</summary>
        public static string LevelName(ThreatLevel level) => level switch
        {
            ThreatLevel.Lethal => "Lethal",
            ThreatLevel.Dangerous => "Dangerous",
            ThreatLevel.Manageable => "Manageable",
            _ => "Trivial",
        };

        /// <summary>Binds a threat. <paramref name="isNew"/> plays the entry animation.</summary>
        public void Bind(ThreatAssessment threat, int index, bool isNew)
        {
            Key = MakeKey(threat);
            var color = UiPalette.Threat(threat.Level);

            if (_levelPip != null) _levelPip.color = color;
            if (_background != null) _background.color = Color.Lerp(UiPalette.SurfaceRaised, color, 0.10f).WithAlpha(0.9f);
            if (_levelLabel != null)
            {
                _levelLabel.SetText(LevelName(threat.Level));
                _levelLabel.color = color;
            }
            if (_headline != null) _headline.SetText(threat.Headline ?? "Unidentified threat");
            if (_detail != null)
            {
                var hasDetail = !string.IsNullOrWhiteSpace(threat.Detail);
                _detail.gameObject.SetActive(hasDetail);
                if (hasDetail) _detail.SetText(threat.Detail);
            }

            if (_koDial != null) _koDial.color = color;
            if (_koNumber != null)
            {
                _koNumber.SetColor(color, 0.25f);
                if (_bound) _koNumber.SetValue(threat.IncomingKoChance);
                else _koNumber.SetImmediate(threat.IncomingKoChance);
            }

            var target = Mathf.Clamp01(threat.IncomingKoChance);
            UiTween.Kill(ref _dialTween);
            if (_bound)
            {
                var from = _koFraction;
                _dialTween = UiTween.Run(0.55f, t =>
                {
                    _koFraction = Mathf.LerpUnclamped(from, target, t);
                    if (_koDial != null) _koDial.fillAmount = _koFraction;
                }, Ease.OutCubic);
            }
            else
            {
                _koFraction = target;
                if (_koDial != null) _koDial.fillAmount = target;
            }

            // A rebind supersedes whatever the row was doing, including a PlayExit fade whose
            // completion would otherwise deactivate a row now carrying live data — a threat
            // that cleared and came straight back vanished from the list until the next
            // readout rebuilt it.
            UiTween.Kill(ref _lifecycleTween);
            gameObject.SetActive(true);

            if (isNew)
            {
                PlayEnter(index, threat.Level);
            }
            else if (_group != null)
            {
                _group.alpha = 1f;
            }

            _bound = true;
        }

        private void PlayEnter(int index, ThreatLevel level)
        {
            UiTween.Kill(ref _lifecycleTween);
            if (_group != null) _group.alpha = 0f;

            var delay = 0.05f + index * 0.06f;
            UiTween.Fade(_group, 1f, 0.26f, Ease.OutCubic, delay);

            if (_rect != null)
            {
                var target = _rect.anchoredPosition;
                _rect.anchoredPosition = target + new Vector2(26f, 0f);
                _lifecycleTween = UiTween.AnchoredMove(_rect, target, 0.34f, Ease.OutCubic, delay);
            }

            // A lethal threat arriving gets a punch on top of the slide — this is the single
            // most important thing the scanner can tell the player.
            if (level == ThreatLevel.Lethal) UiTween.Punch(transform, 0.10f, 0.4f);
        }

        /// <summary>Animates the row out, then deactivates it for reuse.</summary>
        public void PlayExit()
        {
            if (!gameObject.activeSelf) return;
            UiTween.Kill(ref _lifecycleTween);
            _lifecycleTween = UiTween.Fade(_group, 0f, 0.2f, Ease.InCubic, 0f, () =>
            {
                if (this != null) gameObject.SetActive(false);
            });
        }

        /// <summary>Hides immediately, skipping the exit animation. For panel close.</summary>
        public void HideImmediate()
        {
            UiTween.Kill(ref _lifecycleTween);
            if (_group != null) _group.alpha = 0f;
            gameObject.SetActive(false);
            _bound = false;
        }

        /// <summary>Diff key. Threats without a source move fall back to their headline text.</summary>
        public static string MakeKey(ThreatAssessment threat)
        {
            return (threat.SourceMoveId ?? "?") + "|" + (threat.Headline ?? string.Empty);
        }

        /// <summary>Builds a threat row.</summary>
        public static ThreatRow Build(Transform parent)
        {
            // The layout group is on the row root with the padding built in, and the panel
            // background is excluded from layout. That way the row's height comes from its
            // own text — a threat with a long detail line grows the row instead of spilling
            // out of it — without a fitter fighting the list's vertical group.
            var root = UiBuilder.Rect("ThreatRow", parent);
            UiBuilder.Horizontal(root, 10f, new RectOffset(10, 12, 8, 8), TextAnchor.MiddleLeft);
            UiBuilder.Size(root, minHeight: 54f, flexibleWidth: 1f);

            var row = root.gameObject.AddComponent<ThreatRow>();
            row._rect = root;
            row._group = UiBuilder.Group(root);

            row._background = UiBuilder.Backdrop("Bg", root, UiSprites.Panel(10),
                UiPalette.SurfaceRaised.WithAlpha(0.9f));

            // Severity pip: a full-height coloured bar. Colour alone carries the ranking, so
            // the list is scannable before any text is read.
            row._levelPip = UiBuilder.Image("Pip", root, UiSprites.Pill(8), UiPalette.Critical);
            UiBuilder.Size(row._levelPip.rectTransform, preferredWidth: 4f, minWidth: 4f, flexibleHeight: 1f, minHeight: 38f);

            var textStack = UiBuilder.Rect("Text", root);
            UiBuilder.Vertical(textStack, 2f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(textStack, flexibleWidth: 1f, minWidth: 120f);

            row._levelLabel = UiBuilder.Text("Level", textStack, string.Empty, UiTextRole.Overline, UiPalette.Critical);
            row._levelLabel.fontSize = 10f;
            row._levelLabel.characterSpacing = 6f;
            UiBuilder.Size(row._levelLabel.rectTransform, preferredHeight: 13f, flexibleWidth: 1f);

            row._headline = UiBuilder.Text("Headline", textStack, string.Empty, UiTextRole.Body, UiPalette.TextPrimary);
            row._headline.fontSize = 16f;
            UiBuilder.Size(row._headline.rectTransform, flexibleWidth: 1f);

            row._detail = UiBuilder.Text("Detail", textStack, string.Empty, UiTextRole.Caption, UiPalette.TextMuted);
            row._detail.characterSpacing = 0f;
            row._detail.fontSize = 12f;
            UiBuilder.Size(row._detail.rectTransform, flexibleWidth: 1f);

            // --- KO dial
            var dialHolder = UiBuilder.Rect("KoDial", root, false);
            UiBuilder.Size(dialHolder, preferredWidth: 46f, preferredHeight: 46f, minWidth: 46f, minHeight: 46f);

            var dialTrack = UiBuilder.Image("Track", dialHolder, UiSprites.RingSprite(96, 0.14f),
                UiPalette.SurfaceSunken, Image.Type.Simple);
            UiBuilder.Stretch(dialTrack.rectTransform);

            row._koDial = UiBuilder.Image("Fill", dialHolder, UiSprites.RingSprite(96, 0.14f),
                UiPalette.Critical, Image.Type.Filled);
            UiBuilder.Stretch(row._koDial.rectTransform);
            row._koDial.type = Image.Type.Filled;
            row._koDial.fillMethod = Image.FillMethod.Radial360;
            row._koDial.fillOrigin = (int)Image.Origin360.Top;
            row._koDial.fillClockwise = true;
            row._koDial.fillAmount = 0f;

            var koStack = UiBuilder.Rect("KoText", dialHolder);
            UiBuilder.Vertical(koStack, -2f, null, TextAnchor.MiddleCenter);
            UiBuilder.Stretch(koStack, 8f);

            var koValue = UiBuilder.Text("Value", koStack, "0%", UiTextRole.Numeric, UiPalette.Critical,
                TextAlignmentOptions.Center);
            koValue.fontSize = 15f;
            UiBuilder.Size(koValue.rectTransform, preferredHeight: 16f, flexibleWidth: 1f);
            row._koNumber = AnimatedNumber.Attach(koValue, v => UiType.Percent(v), 0.55f);

            var koCaption = UiBuilder.Text("Caption", koStack, "KO", UiTextRole.Caption, UiPalette.TextMuted,
                TextAlignmentOptions.Center);
            koCaption.fontSize = 9f;
            koCaption.characterSpacing = 3f;
            UiBuilder.Size(koCaption.rectTransform, preferredHeight: 11f, flexibleWidth: 1f);

            root.gameObject.SetActive(false);
            return row;
        }
    }
}
