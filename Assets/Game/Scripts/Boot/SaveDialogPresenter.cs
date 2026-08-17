using System;
using System.Collections;
using PokeLab.Core;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.Boot
{
    /// <summary>
    /// The report dialog: the little card that appears when the player saves from the menu.
    ///
    /// Saving is a synchronous file write that finishes in a millisecond, and for a long time
    /// the only evidence it had happened was one line of text swapped into the menu's detail
    /// panel. That reads as nothing — the games make writing the report a *beat*: a box, a
    /// moment of visible work, and then the confirmation. So this dialog deliberately takes
    /// longer than the write it fronts. The throbber holds for a minimum spin even though the
    /// file is already on disk, because a save that appears to cost nothing also appears to
    /// mean nothing.
    ///
    /// Every phase is a bounded wait on unscaled time and the coroutine always reaches the
    /// close: guard refused, write failed, write threw — each path swaps in its own honest
    /// line, holds briefly, fades, and hands control back. A stuck save dialog would block
    /// the whole menu (the presenter swallows input while <see cref="IsRunning"/>), so there
    /// is no path that leaves it open.
    ///
    /// Lives in PokeLab.Boot for the usual reason: the save lives in Overworld, the widgets
    /// in UI, and those two assemblies cannot see each other.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SaveDialogPresenter : MonoBehaviour
    {
        // Above the start menu at 460: the dialog is modal over the menu that opened it.
        private const int SortingOrder = 470;

        /// <summary>Seconds the throbber spins even though the write is long since done.</summary>
        private const float MinimumSpinSeconds = 0.9f;

        /// <summary>Seconds the completion line stays up before the dialog lets go.</summary>
        private const float DoneHoldSeconds = 0.8f;

        /// <summary>Seconds a refusal or failure stays up — slightly longer, it has to be read.</summary>
        private const float ProblemHoldSeconds = 1.1f;

        private const float FadeSeconds = 0.18f;
        private const float SpinDegreesPerSecond = 300f;

        private GameObject _root;
        private CanvasGroup _group;
        private TextMeshProUGUI _line;
        private GameObject _throbberRoot;
        private RectTransform _arc;
        private Coroutine _running;

        /// <summary>True while the dialog owns the screen. The menu ignores input while it does.</summary>
        public bool IsRunning => _running != null;

        /// <summary>
        /// Runs the whole save beat and reports the verdict when the dialog has closed.
        ///
        /// The guard and the write arrive as separate delegates because they mean different
        /// things to the player: a refused guard is "not now" (mid-encounter, mid-cutscene)
        /// and gets the refusal line with no throbber at all, while a failed write is "the
        /// game tried and could not" and gets the failure line after the spin. Collapsing
        /// them into one bool would show a disk-error message for the crime of opening the
        /// menu at the wrong moment.
        /// </summary>
        public void Run(Func<bool> canSave, Func<bool> write, Action<bool> finished)
        {
            // One report at a time. The menu blocks its own input while IsRunning, so a second
            // request can only come from a caller bug — refuse it rather than double-run.
            if (_running != null) return;

            Build();
            _running = StartCoroutine(Flow(canSave, write, finished));
        }

        private IEnumerator Flow(Func<bool> canSave, Func<bool> write, Action<bool> finished)
        {
            _root.SetActive(true);
            _group.alpha = 1f;

            bool allowed;
            try { allowed = canSave != null && canSave(); }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveDialog] The save guard threw, so the save is refused: {ex}", this);
                allowed = false;
            }

            if (!allowed)
            {
                // No throbber for a refusal: nothing is being written and pretending
                // otherwise is exactly the dishonesty this dialog exists to avoid.
                UiSound.Error();
                _throbberRoot.SetActive(false);
                _line.text = Loc.Pick("You can't save right now!", "지금은 리포트를 기록할 수 없다!");
                yield return Hold(ProblemHoldSeconds);
                yield return FadeOut();
                Finish(finished, false);
                yield break;
            }

            UiSound.Confirm();
            _throbberRoot.SetActive(true);
            _line.text = Loc.Pick("Writing up the report…", "리포트를 기록하고 있다…");

            // One rendered frame before the write. The write is synchronous, and running it in
            // the same frame would mean the "writing" line was never actually on screen while
            // anything was being written.
            yield return null;

            var saved = false;
            try { saved = write != null && write(); }
            catch (Exception ex)
            {
                // SaveSystem catches its own I/O failures; this catches everything upstream of
                // it, because an exception here must cost an error line, never a stuck modal.
                Debug.LogError($"[SaveDialog] The save threw instead of failing politely: {ex}", this);
            }

            // The write is already over; the spin is the beat. See the class comment.
            yield return Hold(MinimumSpinSeconds);
            _throbberRoot.SetActive(false);

            if (saved)
            {
                UiSound.MenuClose();
                _line.text = Loc.Pick("The report is written!", "리포트를 기록했다!");
                yield return Hold(DoneHoldSeconds);
            }
            else
            {
                UiSound.Error();
                _line.text = Loc.Pick("The report could not be written…", "리포트를 기록하지 못했다…");
                yield return Hold(ProblemHoldSeconds);
            }

            yield return FadeOut();
            Finish(finished, saved);
        }

        /// <summary>
        /// Waits out a beat on unscaled time, spinning the throbber while it is visible.
        ///
        /// Unscaled because the menu convention pauses the game by flow mode, not by
        /// timescale — but if anything ever does stop the clock, a spinner frozen mid-save is
        /// indistinguishable from a hang, which is the one thing a throbber must never be.
        /// </summary>
        private IEnumerator Hold(float seconds)
        {
            var end = Time.unscaledTime + seconds;
            while (Time.unscaledTime < end)
            {
                if (_throbberRoot.activeSelf)
                    _arc.Rotate(0f, 0f, -SpinDegreesPerSecond * Time.unscaledDeltaTime);
                yield return null;
            }
        }

        private IEnumerator FadeOut()
        {
            var t = 0f;
            while (t < FadeSeconds)
            {
                t += Time.unscaledDeltaTime;
                _group.alpha = 1f - Mathf.Clamp01(t / FadeSeconds);
                yield return null;
            }
        }

        private void Finish(Action<bool> finished, bool saved)
        {
            _root.SetActive(false);
            _running = null;
            finished?.Invoke(saved);
        }

        /// <summary>
        /// Drops the running claim if this component dies mid-report.
        ///
        /// Unity kills the coroutine with the component, and a coroutine that dies between
        /// Run and Finish would leave <see cref="IsRunning"/> true forever — with the menu
        /// politely ignoring every key while it waits for a dialog that no longer exists.
        /// The finished callback is deliberately not invoked here: the presenter that gave it
        /// is a sibling under the same root and is being torn down with us.
        /// </summary>
        private void OnDisable()
        {
            if (_running == null) return;
            _running = null;
            if (_root != null) _root.SetActive(false);
        }

        /// <summary>
        /// Builds the card once, on first use — runtime-built like every other panel in the
        /// project, because the scenes are regenerated constantly and a serialized widget
        /// would have to survive every rebuild.
        /// </summary>
        private void Build()
        {
            if (_root != null) return;

            var canvasGo = new GameObject("SaveDialogCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(canvas, SortingOrder);

            _root = new GameObject("SaveDialog", typeof(RectTransform));
            _root.transform.SetParent(canvasGo.transform, false);
            var root = (RectTransform)_root.transform;
            UiBuilder.Stretch(root);
            _group = UiBuilder.Group(root);

            // Full-screen and a raycast target: the menu's rows are clickable buttons, and a
            // modal that only swallowed the keyboard would still let the mouse press them.
            var scrim = UiBuilder.Image("Scrim", root, null,
                new Color(0.02f, 0.03f, 0.06f, 0.35f), Image.Type.Simple, raycast: true);
            UiBuilder.Stretch(scrim.rectTransform);

            var card = UiBuilder.Rect("Card", root, false);
            UiBuilder.Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 150f));

            var fill = UiBuilder.Panel("Fill", card, UiPalette.Surface, 20);
            UiBuilder.Stretch(fill.rectTransform);

            var edge = UiBuilder.OutlinedPanel("Edge", card, new Color(0f, 0f, 0f, 0f), 20, 3);
            UiBuilder.Stretch(edge.rectTransform);
            edge.color = UiPalette.ScannerCyan;

            _line = UiBuilder.Text("Line", card, string.Empty, UiTextRole.Body,
                UiPalette.TextPrimary, TextAlignmentOptions.Left);
            UiBuilder.Stretch(_line.rectTransform);
            _line.rectTransform.offsetMin = new Vector2(32f, 20f);
            _line.rectTransform.offsetMax = new Vector2(-116f, -20f);

            // The throbber: a dim full ring as the track, and a bright arc — the ring sprite
            // in Filled/Radial360 mode, the same trick the scanner's confidence dial uses —
            // rotated by Hold while work is (nominally) happening.
            _throbberRoot = new GameObject("Throbber", typeof(RectTransform));
            _throbberRoot.transform.SetParent(card, false);
            var throbber = (RectTransform)_throbberRoot.transform;
            UiBuilder.Anchor(throbber, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f), new Vector2(-30f, 0f), new Vector2(54f, 54f));

            var track = UiBuilder.Image("Track", throbber, UiSprites.RingSprite(96, 0.14f),
                UiPalette.ScannerCyanDim, Image.Type.Simple);
            UiBuilder.Stretch(track.rectTransform);

            var arc = UiBuilder.Image("Arc", throbber, UiSprites.RingSprite(96, 0.14f),
                UiPalette.ScannerCyan, Image.Type.Filled);
            arc.fillMethod = Image.FillMethod.Radial360;
            arc.fillOrigin = (int)Image.Origin360.Top;
            arc.fillClockwise = true;
            arc.fillAmount = 0.28f;
            UiBuilder.Stretch(arc.rectTransform);
            _arc = arc.rectTransform;

            _root.SetActive(false);
        }
    }
}
