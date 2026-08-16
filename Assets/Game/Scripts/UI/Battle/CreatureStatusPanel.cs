using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The name-plate for one combatant, built to the Sword/Shield shape: a sheared dark slab
    /// carrying name, level, a full-width health bar and the exact HP figures, with the
    /// experience bar added on the player's side.
    ///
    /// The slab leans rather than sitting square because the two plates are the only UI in the
    /// shot that touches the corners of the frame, and a square slab in the corner of a
    /// perspective HD-2D scene reads as a debug overlay pasted on top. The lean is small — 14px
    /// over the plate height — which is enough to tie it to the diorama and not enough to make
    /// the text look crooked.
    ///
    /// All state changes route through <see cref="Bind"/>, which diffs against the last
    /// snapshot so a health tween is never restarted by an unrelated status change.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CreatureStatusPanel : MonoBehaviour
    {
        /// <summary>Plate width. Public so the HUD anchors the plate at the size its slab sprite was drawn for.</summary>
        public const float PlateWidth = 496f;

        /// <summary>Opponent plate height: name row, health bar, HP figures.</summary>
        public const float OpponentHeight = 132f;

        /// <summary>Player plate height. The extra band is the experience line.</summary>
        public const float PlayerHeight = 148f;

        /// <summary>Horizontal shear of the slab, in pixels across its full height.</summary>
        private const int Shear = 14;

        [Header("Identity")]
        [SerializeField] private TextMeshProUGUI _name;
        [SerializeField] private TextMeshProUGUI _gender;
        [SerializeField] private TextMeshProUGUI _level;
        [SerializeField] private StatusBadge _status;

        [Header("Health")]
        [SerializeField] private AnimatedBar _healthBar;
        [SerializeField] private AnimatedNumber _healthNumber;

        [Header("Experience")]
        [SerializeField] private AnimatedBar _experienceBar;

        [Header("Shell")]
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _rect;

        private BattleSide _side = BattleSide.Player;
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
            if (_level != null) _level.SetText("Lv. " + creature.Level);

            BindHealth(creature, immediate || isNewCreature);
            BindStatus(creature, immediate || isNewCreature);
            BindExperience(creature, immediate || isNewCreature);

            if (isNewCreature && !immediate) PlayEnter();
        }

        /// <summary>
        /// Shows or clears the gender mark beside the name. Pass null to hide it.
        ///
        /// Separate from <see cref="Bind"/> because <see cref="CreatureInstance"/> carries no
        /// gender yet. The plate reserves the space and draws nothing rather than deriving a
        /// symbol from the instance id: a made-up mark here would contradict whatever the
        /// party and summary screens later show for the same creature, and a wrong ♂ is worse
        /// than no ♂.
        /// </summary>
        public void SetGenderMark(string mark, Color? color = null)
        {
            if (_gender == null) return;
            var visible = !string.IsNullOrEmpty(mark);
            _gender.gameObject.SetActive(visible);
            if (!visible) return;
            _gender.SetText(mark);
            _gender.color = color ?? UiPalette.TextSecondary;
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

            if (_healthNumber != null)
            {
                var max = creature.MaxHp;
                _healthNumber.WithFormat(v => $"{Mathf.RoundToInt(v)}<size=78%><color=#FFFFFF70>/{max}</color></size>");
                if (immediate) _healthNumber.SetImmediate(creature.CurrentHp);
                else _healthNumber.SetValue(creature.CurrentHp);
                _healthNumber.SetColor(color, 0.3f);
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
            // Enters from the outside edge the plate is pinned to, so it reads as sliding in
            // from off-screen rather than drifting across the creature it describes.
            var from = target + new Vector2(_side == BattleSide.Player ? -90f : 90f, 0f);
            _rect.anchoredPosition = from;
            UiTween.AnchoredMove(_rect, target, 0.45f, Ease.OutCubic);
            UiTween.Fade(_group, 1f, 0.3f);
        }

        /// <summary>
        /// Builds a plate. Both sides share the construction; the player's carries the
        /// experience band underneath the health bar.
        /// </summary>
        public static CreatureStatusPanel Build(Transform parent, BattleSide side)
        {
            var isPlayer = side == BattleSide.Player;
            var height = isPlayer ? PlayerHeight : OpponentHeight;

            var root = UiBuilder.Rect("StatusPanel_" + side, parent, false);
            root.sizeDelta = new Vector2(PlateWidth, height);

            var panel = root.gameObject.AddComponent<CreatureStatusPanel>();
            panel._rect = root;
            panel._side = side;
            panel._group = UiBuilder.Group(root);
            BuildSlab(root, Mathf.RoundToInt(height));

            // Side padding is wider than the top/bottom because the slab's edges lean: at the
            // top of the plate the left edge has already travelled Shear pixels inward, and
            // text laid out to a square inset would touch it.
            var stack = UiBuilder.Rect("Stack", root);
            UiBuilder.Vertical(stack, 7f, new RectOffset(30, 30, 14, 14), TextAnchor.MiddleLeft);

            // --- name row: name, gender mark, status pill, level right-aligned
            var nameRow = UiBuilder.Rect("NameRow", stack);
            UiBuilder.Horizontal(nameRow, 10f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(nameRow, preferredHeight: 38f, minHeight: 38f, flexibleWidth: 1f);

            panel._name = UiBuilder.Text("Name", nameRow, "—", UiTextRole.Body, UiPalette.TextPrimary);
            panel._name.fontStyle = FontStyles.Bold;
            panel._name.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuilder.Size(panel._name.rectTransform, minWidth: 90f, flexibleWidth: 1f);

            panel._gender = UiBuilder.Text("Gender", nameRow, string.Empty, UiTextRole.Body,
                UiPalette.TextSecondary, TextAlignmentOptions.Center);
            UiBuilder.Size(panel._gender.rectTransform, preferredWidth: 22f, minWidth: 22f);
            panel._gender.gameObject.SetActive(false);

            // Carried on both plates, not just the player's. A status the opponent is under
            // changes what the player should do next as much as one of their own, and the badge
            // hides itself when there is nothing to say, so it costs no space when idle.
            panel._status = StatusBadge.Build(nameRow, 30f);

            panel._level = UiBuilder.Text("Level", nameRow, "Lv. 1", UiTextRole.Numeric,
                UiPalette.TextSecondary, TextAlignmentOptions.Right);
            // Wide enough for "Lv. 100" at the Numeric size without ellipsing. Level is the one
            // figure on the plate that silently grows a digit late in the game.
            UiBuilder.Size(panel._level.rectTransform, preferredWidth: 132f, minWidth: 132f);

            // --- health bar, spanning the plate
            panel._healthBar = AnimatedBar.Build("HealthBar", stack, 12f, UiPalette.Health(1f), true);

            // --- experience band, player only, immediately under the health bar
            if (isPlayer)
            {
                panel._experienceBar = AnimatedBar.Build("ExpBar", stack, 6f, UiPalette.Info, false,
                    UiPalette.SurfaceSunken.WithAlpha(0.85f));
                panel._experienceBar.SetImmediate(0f);
            }

            // --- exact HP figures, left-aligned under the bars
            //
            // Shown for the opponent as well as the player. That reverses the asymmetry this
            // plate used to enforce — the genre hides the opponent's raw HP so "can I survive
            // this" stays a judgement call — because the reference layout being matched puts
            // the figures on both plates. Flip the opponent back to a percentage by giving that
            // side a plain label instead of an AnimatedNumber here; nothing else depends on it.
            var hpText = UiBuilder.Text("HpValue", stack, "0/0", UiTextRole.Numeric,
                UiPalette.TextPrimary, TextAlignmentOptions.Left);
            UiBuilder.Size(hpText.rectTransform, preferredHeight: 30f, minHeight: 30f, flexibleWidth: 1f);
            panel._healthNumber = AnimatedNumber.Attach(hpText, v => Mathf.RoundToInt(v).ToString(), 0.6f);

            return panel;
        }

        /// <summary>The sheared slab, its rim and the shadow beneath it.</summary>
        private static void BuildSlab(RectTransform root, int height)
        {
            // Drawn first so it lands behind the body. Rounded rather than sheared: at this
            // spread and opacity the lean is invisible, and a sheared shadow would need a
            // second generated texture per plate height for no visible gain.
            var shadow = UiBuilder.Image("Shadow", root, UiSprites.Shadow(16, 22),
                UiPalette.Shadow.WithAlpha(0.55f));
            shadow.rectTransform.offsetMin = new Vector2(-14f, -18f);
            shadow.rectTransform.offsetMax = new Vector2(14f, 10f);

            // Backdrop rather than Surface: these plates sit over a lit 3D diorama, and the
            // lighter Surface value leaves white text at barely 3:1 against a sunlit tile.
            var body = UiBuilder.Image("Body", root, UiSprites.Slant(height, Shear),
                UiPalette.Backdrop.WithAlpha(0.88f));
            UiBuilder.Stretch(body.rectTransform);

            var rim = UiBuilder.Image("Rim", root, UiSprites.SlantFrame(height, Shear, 2),
                Color.white.WithAlpha(0.11f));
            UiBuilder.Stretch(rim.rectTransform);
        }
    }
}
