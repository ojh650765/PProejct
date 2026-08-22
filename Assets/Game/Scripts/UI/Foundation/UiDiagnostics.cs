using PokeLab.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// Switches parts of a built canvas off before its first frame, so the cost of DRAWING it
    /// can be separated from the cost of building it, and then attributed to a subsystem.
    ///
    /// <b>Why prevention, and why after construction.</b> A WebAssembly heap never shrinks, so
    /// an experiment that frees memory and re-reads the figure returns zero no matter what is
    /// true — two earlier A/Bs were worthless for exactly that reason. The only valid test is to
    /// not allocate, and compare two runs. The earlier <c>pl_noui</c> did that by skipping
    /// construction, which conflates the objects with their rendering. This applies the switches
    /// to a canvas that was built normally, so only the draw differs.
    ///
    /// <b>Read the result with the allocator, not the heap alone.</b> The heap figure is a
    /// high-water mark of the address space, not a measure of live memory and not a residency
    /// total; whoever reads these runs should have <c>Profiler.GetTotalAllocatedMemoryLong</c>
    /// and <c>GetTotalReservedMemoryLong</c> beside it, which the player logs on the same frame.
    ///
    /// Every switch here breaks the screen on purpose and is off unless the page URL asks.
    /// </summary>
    public static class UiDiagnostics
    {
        /// <summary>
        /// Applies whichever switches the URL carries to everything under <paramref name="root"/>.
        /// Call after the canvas is built and before the first frame is drawn.
        /// </summary>
        public static void Apply(Transform root)
        {
            if (root == null) return;

            var flags = Diag.Summary;
            if (string.IsNullOrEmpty(flags)) return;

            var disabled = 0;

            if (Diag.NoDraw)
                foreach (var c in root.GetComponentsInChildren<Canvas>(true))
                    { c.enabled = false; disabled++; }

            if (Diag.NoText)
                foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
                    { t.enabled = false; disabled++; }

            if (Diag.NoImages)
            {
                foreach (var i in root.GetComponentsInChildren<Image>(true))
                    { i.enabled = false; disabled++; }
                foreach (var i in root.GetComponentsInChildren<RawImage>(true))
                    { i.enabled = false; disabled++; }
            }

            if (Diag.NoLayout)
            {
                foreach (var l in root.GetComponentsInChildren<LayoutGroup>(true))
                    { l.enabled = false; disabled++; }
                foreach (var f in root.GetComponentsInChildren<ContentSizeFitter>(true))
                    { f.enabled = false; disabled++; }
            }

            if (Diag.NoEffects)
            {
                foreach (var e in root.GetComponentsInChildren<BaseMeshEffect>(true))
                    { e.enabled = false; disabled++; }
                foreach (var m in root.GetComponentsInChildren<Mask>(true))
                    { m.enabled = false; disabled++; }
                foreach (var m in root.GetComponentsInChildren<RectMask2D>(true))
                    { m.enabled = false; disabled++; }
            }

            if (Diag.UiKeep < 1f)
            {
                // Whole GameObjects, not components: a disabled Graphic keeps its
                // CanvasRenderer, and the CanvasRenderer is what this is trying to count.
                var renderers = root.GetComponentsInChildren<CanvasRenderer>(true);
                var keep = Mathf.CeilToInt(renderers.Length * Diag.UiKeep);
                for (var i = keep; i < renderers.Length; i++)
                {
                    renderers[i].gameObject.SetActive(false);
                    disabled++;
                }
                Debug.Log($"[Diag] kept {keep} of {renderers.Length} CanvasRenderers");
            }

            Debug.Log($"[Diag] {flags.Trim()}: disabled {disabled} components under {root.name}");
        }
    }
}
