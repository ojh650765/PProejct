using System.Text;
using PokeLab.UI;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace PokeLab.UI.Editor
{
    /// <summary>
    /// Measures what TextMeshPro actually does with a line that does not fit its rect.
    ///
    /// <b>Why this exists.</b> Labels were disappearing rather than truncating -- the swap list's
    /// names in 대전모드, the action titles in 내 포켓몬 -- and the arithmetic said why: Pretendard's
    /// line height is 1.193x the point size, so a 28pt label needs 33.4px and those rows are 32.
    /// But "TMP culls a line that does not fit when overflow is Ellipsis" is a claim about a
    /// third-party layout engine, and the fix for it -- widening rows, or granting margin slack --
    /// changes every screen in the game. That is not a thing to change on a reading of the source.
    ///
    /// So this builds the exact case, renders it, and counts the vertices TMP actually emitted.
    /// Zero means the line was dropped. It also reports preferredHeight, because a fix that
    /// altered it would silently flatten every row that sizes itself to its text -- and that is
    /// the one thing a global change here must not do.
    /// </summary>
    public static class TextFitProbe
    {
        [MenuItem("Tools/Poké Lab/Diagnostics/Probe text fit", priority = 900)]
        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine("[TextFitProbe] arm / role / rect -> vertices, preferredHeight");

            // The three arms: no slack at all (what shipped), the flat slack, and a slack scaled
            // to the point size. Only the third can be proved to cover every rect, because the
            // shortfall a rect can impose grows with the line and a constant cannot chase it.
            foreach (var arm in new[] { 0f, -1f, -2f })
            {
                foreach (var role in new[] { UiTextRole.Body, UiTextRole.Numeric, UiTextRole.Caption, UiTextRole.Metric })
                {
                    var size = UiType.Size(role);
                    foreach (var factor in new[] { 0.7f, 0.96f, 1.15f })
                        Measure(report, arm, role, size * 1.1929f * factor);
                }
            }

            Debug.Log(report.ToString());
        }

        private static void Measure(StringBuilder report, float arm, UiTextRole role, float height)
        {
            var canvasGo = new GameObject("~probeCanvas", typeof(Canvas));
            try
            {
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;

                var labelGo = new GameObject("~probeLabel", typeof(RectTransform));
                labelGo.transform.SetParent(canvasGo.transform, false);
                var text = labelGo.AddComponent<TextMeshProUGUI>();

                text.text = "이상해씨";
                text.alignment = TextAlignmentOptions.Left;
                UiType.Apply(text, role);
                text.textWrappingMode = TextWrappingModes.NoWrap;

                var size = UiType.Size(role);
                string armName;
                if (arm == 0f) { text.margin = Vector4.zero; armName = "none"; }
                else if (arm == -1f) { text.margin = new Vector4(0f, -12f, 0f, -12f); armName = "flat12"; }
                else { text.margin = new Vector4(0f, -size * 0.6f, 0f, -size * 0.6f); armName = "prop.6"; }

                var rect = (RectTransform)labelGo.transform;
                rect.sizeDelta = new Vector2(400f, height);

                text.ForceMeshUpdate(true, true);

                var vertices = 0;
                var info = text.textInfo;
                if (info != null && info.meshInfo != null)
                    for (var i = 0; i < info.meshInfo.Length; i++)
                        vertices += info.meshInfo[i].vertexCount;

                report.AppendLine(
                    $"  {armName,-7} {role,-8} rect {height,6:0.0}  line {size * 1.1929f,6:0.0}  " +
                    $"vertices {vertices,4}  preferredHeight {text.preferredHeight,7:0.0}  " +
                    (vertices > 0 ? "DRAWN" : "DROPPED"));
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }
    }
}
