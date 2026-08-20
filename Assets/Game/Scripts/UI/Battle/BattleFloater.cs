using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The numbers that jump off a plate when something happens to it: damage taken, health
    /// recovered, experience gained, and the shout that a level-up is.
    ///
    /// Built and thrown away per hit rather than pooled. A battle produces on the order of
    /// twenty of these across a whole fight, and a pool would need an owner, a reset path and
    /// a story for what happens when the HUD is destroyed mid-flight — all of it to save
    /// twenty allocations. The object is parented into the plate it belongs to, so the HUD
    /// tearing down takes every floater with it.
    ///
    /// Two details are load-bearing.
    ///
    /// <b>Size.</b> TMP draws nothing at all when a line is taller than its rect — it does not
    /// clip — so every rect built here is given far more height than its point size and the
    /// text is set to overflow rather than ellipsise. A floater that silently fails to draw
    /// looks exactly like one that was never spawned.
    ///
    /// <b>Direction.</b> Each floater travels <i>away</i> from the bar it describes: the
    /// player's plate sits in the bottom-left so its numbers rise off the top of it, and the
    /// opponent's sits in the top-right so its numbers fall off the bottom. Both drift toward
    /// the empty middle of the frame, and neither ever covers the health bar the player is
    /// watching drain.
    /// </summary>
    public static class BattleFloater
    {
        /// <summary>
        /// A damage figure, scaled by how much of the bar it took.
        ///
        /// The scale is the whole point. A four-point chip and a hit that takes half the bar
        /// are the same event to the log and must not be the same event to the eye, so
        /// <paramref name="share"/> drives size, distance, colour weight and the little tilt
        /// that makes a big number feel thrown rather than placed.
        /// </summary>
        public static void Damage(RectTransform layer, int amount, float share, bool critical,
            Effectiveness effectiveness, bool toRight, bool upward)
        {
            if (layer == null || amount <= 0) return;

            share = Mathf.Clamp01(share);
            // Perceptual rather than linear: a 10% chip should still be legible, and the top
            // of the range is reserved for hits that genuinely threaten.
            var weight = Mathf.Sqrt(share);

            var size = Mathf.Lerp(38f, 78f, weight);
            var tint = UiPalette.TextPrimary;
            switch (effectiveness)
            {
                case Effectiveness.SuperEffective:
                    tint = UiPalette.ScannerAmber;
                    size *= 1.12f;
                    break;
                case Effectiveness.NotVeryEffective:
                    tint = UiPalette.TextSecondary;
                    size *= 0.86f;
                    break;
            }
            if (critical)
            {
                tint = UiPalette.Critical;
                size *= 1.22f;
            }

            var content = critical
                ? $"<size=56%>{Loc.Pick("CRIT", "급소")}</size> {amount}"
                : amount.ToString();

            Throw(layer, content, tint, size, weight, toRight, upward, critical);
        }

        /// <summary>Health coming back. Quieter than damage on purpose: a heal is relief, not impact.</summary>
        public static void Heal(RectTransform layer, int amount, float share, bool toRight, bool upward)
        {
            if (layer == null || amount <= 0) return;
            var weight = Mathf.Clamp01(Mathf.Sqrt(Mathf.Clamp01(share))) * 0.7f;
            Throw(layer, "+" + amount, UiPalette.Positive, Mathf.Lerp(36f, 58f, weight), weight,
                toRight, upward, false);
        }

        /// <summary>A plain gain line — "+128 EXP" — drifting off the plate.</summary>
        public static void Gain(RectTransform layer, string label, Color tint, float size = 40f,
            bool toRight = true, bool upward = true)
        {
            if (layer == null || string.IsNullOrEmpty(label)) return;
            Throw(layer, label, tint, size, 0.3f, toRight, upward, false);
        }

        /// <summary>
        /// The level-up shout. Not a floater in the same sense: it plants itself over the
        /// plate, holds long enough to be read, and leaves — because this is the one the
        /// player is meant to stop and notice.
        /// </summary>
        public static void Shout(RectTransform layer, string label, Color tint, float size = 52f,
            bool upward = true)
        {
            if (layer == null || string.IsNullOrEmpty(label)) return;

            var edge = upward ? 1f : 0f;
            var rect = UiBuilder.Rect("Shout", layer, false);
            UiBuilder.Anchor(rect, new Vector2(0.5f, edge), new Vector2(0.5f, edge), new Vector2(0.5f, 0.5f),
                new Vector2(0f, upward ? 40f : -40f), new Vector2(460f, size * 2.6f));

            var glow = UiBuilder.Image("Glow", rect, UiSprites.Glow(192), tint.WithAlpha(0.40f), Image.Type.Simple);
            UiBuilder.Stretch(glow.rectTransform, -48f);

            var text = UiBuilder.Text("Label", rect, label, UiTextRole.Title, tint, TextAlignmentOptions.Center);
            text.fontSize = size;
            text.fontStyle = FontStyles.Bold;
            text.characterSpacing = 4f;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            UiBuilder.Stretch(text.rectTransform);
            UiType.ApplyShadow(text);

            var group = UiBuilder.Group(rect, 0f, false, false);

            // Slams in squashed and springs square. The stretch on entry is what separates
            // this from every other label in the game that simply fades up.
            var squashed = new Vector3(1.45f, 0.55f, 1f);
            rect.localScale = squashed;
            UiTween.Run(0.34f, t =>
            {
                if (rect != null) rect.localScale = Vector3.LerpUnclamped(squashed, Vector3.one, t);
            }, Ease.OutBack);
            UiTween.Fade(group, 1f, 0.18f);

            UiTween.Delay(0.44f, () => { if (rect != null) UiTween.Punch(rect, 0.09f, 0.42f); });

            UiTween.Delay(1.5f, () =>
            {
                if (rect == null) return;
                UiTween.AnchoredMove(rect, rect.anchoredPosition + new Vector2(0f, upward ? 26f : -26f),
                    0.34f, Ease.InCubic);
                UiTween.Fade(group, 0f, 0.34f, Ease.InCubic, 0f, () =>
                {
                    if (rect != null) Object.Destroy(rect.gameObject);
                });
            });

            UiTween.Delay(2.4f, () => { if (rect != null) Object.Destroy(rect.gameObject); });
        }

        // -------------------------------------------------------------------------------

        /// <summary>
        /// The shared throw: pop in oversized, arc away, hold, fade, delete.
        ///
        /// The schedule runs on <see cref="UiTween.Delay"/> rather than off tween completions,
        /// because reduced motion collapses a tween to its final value on the spot but keeps a
        /// delay as pacing — so with motion off the figure still appears, still holds long
        /// enough to be read, and still leaves, instead of being created and destroyed inside
        /// one frame.
        /// </summary>
        private static void Throw(RectTransform layer, string content, Color tint, float size,
            float weight, bool toRight, bool upward, bool loud)
        {
            var height = Mathf.Max(size * 1.9f, 58f);
            var xEdge = toRight ? 1f : 0f;
            var yEdge = upward ? 1f : 0f;

            var rect = UiBuilder.Rect("Floater", layer, false);
            UiBuilder.Anchor(rect,
                new Vector2(xEdge, yEdge), new Vector2(xEdge, yEdge),
                new Vector2(xEdge, 0.5f),
                new Vector2(toRight ? -46f : 46f, upward ? 14f : -14f),
                new Vector2(420f, height));

            if (loud)
            {
                var burst = UiBuilder.Image("Burst", rect, UiSprites.Starburst(192, 10, 0.34f),
                    tint.WithAlpha(0.55f), Image.Type.Simple);
                UiBuilder.Stretch(burst.rectTransform, -30f);
                UiTween.Run(0.5f, t =>
                {
                    if (burst == null) return;
                    burst.rectTransform.localScale = Vector3.one * Mathf.LerpUnclamped(0.4f, 1.6f, t);
                    burst.color = tint.WithAlpha(0.55f * (1f - t));
                }, Ease.OutCubic);
            }

            var text = UiBuilder.Text("Value", rect, content, UiTextRole.Numeric, tint,
                toRight ? TextAlignmentOptions.Right : TextAlignmentOptions.Left);
            text.fontSize = size;
            text.fontStyle = FontStyles.Bold;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            // Overflow, never Ellipsis: the rect is deliberately larger than the glyphs, and a
            // floater that ellipsised its own number would be worse than useless.
            text.overflowMode = TextOverflowModes.Overflow;
            UiBuilder.Stretch(text.rectTransform);
            UiType.ApplyShadow(text);

            var group = UiBuilder.Group(rect, 1f, false, false);

            var from = rect.anchoredPosition;
            var travel = Mathf.Lerp(52f, 104f, weight) * (upward ? 1f : -1f);
            var drift = (toRight ? -1f : 1f) * Mathf.Lerp(6f, 26f, weight);
            var to = from + new Vector2(drift, travel);

            // A heavy hit is thrown at an angle; a chip lands flat. Small enough to read as
            // force rather than as a broken layout.
            rect.localEulerAngles = new Vector3(0f, 0f, (toRight ? -1f : 1f) * Mathf.Lerp(0f, 7f, weight));

            var overshoot = Mathf.Lerp(1.1f, 1.35f, weight);
            rect.localScale = Vector3.one * 0.45f;
            UiTween.Run(0.16f, t =>
            {
                if (rect != null) rect.localScale = Vector3.one * Mathf.LerpUnclamped(0.45f, overshoot, t);
            }, Ease.OutCubic, 0f, true, () =>
            {
                if (rect == null) return;
                UiTween.Run(0.2f, s =>
                {
                    if (rect != null) rect.localScale = Vector3.one * Mathf.LerpUnclamped(overshoot, 1f, s);
                }, Ease.OutCubic);
            });

            // Decelerating, like something thrown rather than something animated.
            UiTween.Run(0.82f, t =>
            {
                if (rect != null) rect.anchoredPosition = Vector2.LerpUnclamped(from, to, t);
            }, Ease.OutCubic);

            var hold = loud ? 0.62f : 0.44f;
            UiTween.Delay(hold, () =>
            {
                if (group == null) return;
                UiTween.Fade(group, 0f, 0.36f, Ease.InCubic, 0f, () =>
                {
                    if (rect != null) Object.Destroy(rect.gameObject);
                });
            });

            // The reaper. Reduced motion completes the fade on the spot, and a callback that
            // fires while the plate is being torn down would leave the object behind; this
            // guarantees the floater goes away on every path.
            UiTween.Delay(hold + 1.2f, () => { if (rect != null) Object.Destroy(rect.gameObject); });
        }
    }
}
