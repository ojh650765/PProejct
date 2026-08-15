using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The name-plate above a combatant: name, level, types, status, health and — on the
    /// player's side — exact HP numbers and the experience bar.
    ///
    /// The asymmetry is intentional and matches the genre: the player knows their own
    /// creature's exact HP and experience, and only a fraction for the opponent. Showing the
    /// opponent's raw numbers would flatten every "can I survive this" decision into
    /// arithmetic.
    ///
    /// All state changes route through <see cref="Bind"/>, which diffs against the last
    /// snapshot so a health tween is never restarted by an unrelated status change.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CreatureStatusPanel : MonoBehaviour
    {
        [Header("Identity")]
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _level;
        [SerializeField] private RectTransform _typeRow;
        [SerializeField] private StatusBadge _status;

        [Header("Health")]
        [SerializeField] private AnimatedBar _healthBar;
        [SerializeField] private TextMeshProUGUI _healthText;
        [SerializeField] private AnimatedNumber _healthNumber;

        [Header("Experience")]
        [SerializeField] private AnimatedBar _experienceBar;

        [Header("Shell")]
        [SerializeField] private Image _panel;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _rect;

        private readonly TypeBadge[] _typeBadges = new TypeBadge[2];
        private BattleSide _side = BattleSide.Player;
        private bool _showExactHp;
        private string _boundInstanceId;
        private int _lastHp = -1;
        private StatusCondition _lastStatus = StatusCondition.None;

        /// <summary>Instance currently displayed, or null.</summary>
        public string BoundInstanceId => _boundInstanceId;

        /// <summary>
        /// Binds a creature. Call this on every state change; it is cheap and only animates
        /// what actually moved.
        /// </summary>
        public void Bind(CreatureInstance creature, bool immediate = false)
        {
            if (creature == null)
            {
                SetVisible(false);
                _boundInstanceId = null;
                return;
            }

            var isNewCreature = creature.InstanceId != _boundInstanceId;
            _boundInstanceId = creature.InstanceId;
            SetVisible(true);

            if (_name != null) _name.SetText(UiServices.NameOf(creature));
            if (_level != null) _level.SetText("Lv " + creature.Level);

            UiServices.TypesOf(creature.SpeciesId, out var primary, out var secondary);
            EnsureTypeBadges();
            _typeBadges[0]?.Bind(primary);
            _typeBadges[1]?.Bind(secondary);

            BindHealth(creature, immediate || isNewCreature);
            BindStatus(creature, immediate || isNewCreature);
            BindExperience(creature, immediate || isNewCreature);

            if (isNewCreature && !immediate) PlayEnter();
        }

        private void BindHealth(CreatureInstance creature, bool immediate)
        {
            var fraction = creature.HpFraction;
            var color = UiPalette.Health(fraction);

            if (_healthBar != null)
            {
                if (immediate)
                {
                    _healthBar.SetImmediate(fraction);
                    _healthBar.SetColorImmediate(color);
                }
                else
                {
                    _healthBar.SetValue(fraction);
                    // Colour tween is faster than the fill so the warning colour arrives
                    // slightly ahead of the bar reaching the danger zone.
                    _healthBar.SetColor(color, 0.35f);
                }
            }

            if (_showExactHp && _healthNumber != null)
            {
                var max = creature.MaxHp;
                _healthNumber.WithFormat(v => $"{Mathf.RoundToInt(v)}<size=70%><color=#FFFFFF66> / {max}</color></size>");
                if (immediate) _healthNumber.SetImmediate(creature.CurrentHp);
                else _healthNumber.SetValue(creature.CurrentHp);
                _healthNumber.SetColor(color, 0.3f);
            }
            else if (_healthText != null && !_showExactHp)
            {
                // Opponent side: a percentage is the honest amount of information.
                _healthText.SetText(Mathf.CeilToInt(fraction * 100f) + "%");
                _healthText.color = color;
            }

            // Low-health pulse on the player's own bar only — a nagging red flash on the
            // opponent's plate would read as encouragement rather than warning.
            if (_side == BattleSide.Player && _lastHp >= 0 && creature.CurrentHp < _lastHp && fraction <= 0.25f)
            {
                UiTween.Punch(_rect != null ? _rect : transform, 0.04f, 0.35f);
            }
            _lastHp = creature.CurrentHp;
        }

        private void BindStatus(CreatureInstance creature, bool immediate)
        {
            if (_status == null) return;
            var changed = creature.Status != _lastStatus;
            _status.Bind(creature.Status, immediate);
            _lastStatus = creature.Status;
            if (changed && !immediate && creature.Status != StatusCondition.None)
            {
                UiTween.Punch(_rect != null ? _rect : transform, 0.05f, 0.4f);
            }
        }

        private void BindExperience(CreatureInstance creature, bool immediate)
        {
            if (_experienceBar == null) return;

            // The contract carries a running total but no per-level curve, so the bar shows
            // progress through a conventional cubic band. When the progression system lands
            // it should push an explicit 0-1 fraction through SetExperienceFraction instead.
            var level = Mathf.Max(1, creature.Level);
            var floor = level * level * level;
            var ceiling = (level + 1) * (level + 1) * (level + 1);
            var fraction = ceiling > floor
                ? Mathf.Clamp01((creature.Experience - floor) / (float)(ceiling - floor))
                : 0f;

            if (immediate) _experienceBar.SetImmediate(fraction);
            else _experienceBar.SetValue(fraction, 0.8f);
        }

        /// <summary>
        /// Explicit experience fraction, for when the progression system owns the curve.
        /// Preferred over letting <see cref="Bind"/> guess.
        /// </summary>
        public void SetExperienceFraction(float fraction, bool immediate = false)
        {
            if (_experienceBar == null) return;
            if (immediate) _experienceBar.SetImmediate(fraction);
            else _experienceBar.SetValue(fraction, 0.8f);
        }

        /// <summary>Slides the plate in or out. Used on send-out and withdraw.</summary>
        public void SetVisible(bool visible, bool immediate = false)
        {
            if (_group == null) return;
            if (immediate)
            {
                _group.alpha = visible ? 1f : 0f;
                gameObject.SetActive(visible);
                return;
            }
            if (visible) gameObject.SetActive(true);
            UiTween.Fade(_group, visible ? 1f : 0f, 0.25f, visible ? Ease.OutCubic : Ease.InCubic, 0f,
                () => { if (!visible && this != null) gameObject.SetActive(false); });
        }

        private void PlayEnter()
        {
            if (_rect == null) return;
            var target = _rect.anchoredPosition;
            // Enters from the side of the screen the combatant is on.
            var from = target + new Vector2(_side == BattleSide.Player ? -70f : 70f, 0f);
            _rect.anchoredPosition = from;
            UiTween.AnchoredMove(_rect, target, 0.45f, Ease.OutCubic);
            UiTween.Fade(_group, 1f, 0.3f);
        }

        private void EnsureTypeBadges()
        {
            if (_typeRow == null) return;
            for (var i = 0; i < 2; i++)
            {
                if (_typeBadges[i] == null) _typeBadges[i] = TypeBadge.BuildCompact(_typeRow, ElementType.None, 20f);
            }
        }

        /// <summary>
        /// Builds a plate. The player's plate carries exact HP and an experience bar; the
        /// opponent's does not, and is mirrored so both read outward from the centre.
        /// </summary>
        public static CreatureStatusPanel Build(Transform parent, BattleSide side)
        {
            var isPlayer = side == BattleSide.Player;

            var root = UiBuilder.Rect("StatusPanel_" + side, parent, false);
            root.sizeDelta = new Vector2(340f, isPlayer ? 96f : 78f);

            var panel = root.gameObject.AddComponent<CreatureStatusPanel>();
            panel._rect = root;
            panel._side = side;
            panel._showExactHp = isPlayer;
            panel._group = UiBuilder.Group(root);

            panel._panel = UiBuilder.Panel("Plate", root, UiPalette.Surface.WithAlpha(0.92f), 14, true, 20, 0.45f);
            var rim = UiBuilder.Image("Rim", panel._panel.rectTransform, UiSprites.Frame(14, 1),
                Color.white.WithAlpha(0.07f));
            UiBuilder.Stretch(rim.rectTransform);

            var stack = UiBuilder.Rect("Stack", root);
            UiBuilder.Vertical(stack, 5f, new RectOffset(14, 14, 9, 9), TextAnchor.MiddleLeft);

            // --- name row
            var nameRow = UiBuilder.Rect("NameRow", stack);
            UiBuilder.Horizontal(nameRow, 7f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(nameRow, preferredHeight: 22f, minHeight: 22f, flexibleWidth: 1f);

            panel._name = UiBuilder.Text("Name", nameRow, "—", UiTextRole.Body, UiPalette.TextPrimary);
            panel._name.fontStyle = FontStyles.Bold;
            panel._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(panel._name.rectTransform, preferredWidth: 130f, minWidth: 70f, flexibleWidth: 1f);

            panel._status = StatusBadge.Build(nameRow, 18f);

            var typeRow = UiBuilder.Rect("Types", nameRow, false);
            UiBuilder.Horizontal(typeRow, 4f, null, TextAnchor.MiddleRight);
            UiBuilder.Size(typeRow, preferredWidth: 44f, minWidth: 44f);
            panel._typeRow = typeRow;

            panel._level = UiBuilder.Text("Level", nameRow, "Lv 1", UiTextRole.Numeric, UiPalette.TextSecondary,
                TextAlignmentOptions.Right);
            panel._level.fontSize = 15f;
            UiBuilder.Size(panel._level.rectTransform, preferredWidth: 48f, minWidth: 48f);

            // --- health row
            var healthRow = UiBuilder.Rect("HealthRow", stack);
            UiBuilder.Horizontal(healthRow, 8f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(healthRow, preferredHeight: 16f, minHeight: 16f, flexibleWidth: 1f);

            var hpLabel = UiBuilder.Text("HpLabel", healthRow, "HP", UiTextRole.Overline, UiPalette.TextMuted);
            hpLabel.fontSize = 9f;
            hpLabel.characterSpacing = 3f;
            UiBuilder.Size(hpLabel.rectTransform, preferredWidth: 22f, minWidth: 22f);

            panel._healthBar = AnimatedBar.Build("HealthBar", healthRow, 10f, UiPalette.Health(1f), true);

            if (isPlayer)
            {
                var hpText = UiBuilder.Text("HpValue", healthRow, "0 / 0", UiTextRole.Numeric,
                    UiPalette.TextPrimary, TextAlignmentOptions.Right);
                hpText.fontSize = 14f;
                UiBuilder.Size(hpText.rectTransform, preferredWidth: 84f, minWidth: 84f);
                panel._healthText = hpText;
                panel._healthNumber = AnimatedNumber.Attach(hpText, v => Mathf.RoundToInt(v).ToString(), 0.6f);
            }
            else
            {
                var hpText = UiBuilder.Text("HpValue", healthRow, "100%", UiTextRole.Numeric,
                    UiPalette.TextPrimary, TextAlignmentOptions.Right);
                hpText.fontSize = 14f;
                UiBuilder.Size(hpText.rectTransform, preferredWidth: 44f, minWidth: 44f);
                panel._healthText = hpText;
            }

            // --- experience row, player only
            if (isPlayer)
            {
                var expRow = UiBuilder.Rect("ExpRow", stack);
                UiBuilder.Horizontal(expRow, 8f, null, TextAnchor.MiddleLeft);
                UiBuilder.Size(expRow, preferredHeight: 8f, minHeight: 8f, flexibleWidth: 1f);

                var expLabel = UiBuilder.Text("ExpLabel", expRow, "EXP", UiTextRole.Overline, UiPalette.TextMuted);
                expLabel.fontSize = 8f;
                expLabel.characterSpacing = 2f;
                UiBuilder.Size(expLabel.rectTransform, preferredWidth: 22f, minWidth: 22f);

                panel._experienceBar = AnimatedBar.Build("ExpBar", expRow, 5f, UiPalette.Info, false);
                panel._experienceBar.SetImmediate(0f);
            }

            return panel;
        }
    }
}
