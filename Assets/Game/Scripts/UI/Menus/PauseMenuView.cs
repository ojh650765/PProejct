using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>Which page of the pause menu is showing.</summary>
    public enum PauseMenuPage
    {
        Root = 0,
        Options = 1,
        Save = 2,
    }

    /// <summary>
    /// Pause, options and save, as three pages of one screen.
    ///
    /// They are one component rather than three because they share a shell, a back stack and
    /// a transition, and splitting them would mean three copies of that scaffolding kept in
    /// sync by hand. Each page is a vertical list of rows built from the same primitives, so
    /// adding a setting is one line.
    ///
    /// The one setting that reaches outside this file is reduced motion, which drives
    /// <see cref="UiTween.MotionEnabled"/> and therefore every animation in the game's UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PauseMenuView : MonoBehaviour
    {
        [Header("Parts")]
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _panel;
        [SerializeField] private TextMeshProUGUI _title;
        [SerializeField] private RectTransform _rootPage;
        [SerializeField] private RectTransform _optionsPage;
        [SerializeField] private RectTransform _savePage;
        [SerializeField] private TextMeshProUGUI _footer;
        [SerializeField] private bool _buildOnAwake = true;

        private readonly Stack<PauseMenuPage> _history = new Stack<PauseMenuPage>(4);
        private PauseMenuPage _page = PauseMenuPage.Root;
        private bool _built;

        /// <summary>Raised when the player resumes.</summary>
        public Action Resumed;
        /// <summary>Raised when the party screen is requested.</summary>
        public Action PartyRequested;
        /// <summary>Raised when the bag is requested.</summary>
        public Action BagRequested;
        /// <summary>Raised when the dex is requested.</summary>
        public Action DexRequested;
        /// <summary>Raised with a slot index when the player saves.</summary>
        public Action<int> SaveRequested;
        /// <summary>Raised when the player quits to the title.</summary>
        public Action QuitRequested;

        /// <summary>True while the menu is open.</summary>
        public bool IsOpen { get; private set; }

        private void Awake()
        {
            if (_buildOnAwake && !_built) BuildRuntime();
            SetOpen(false, true);
        }

        /// <summary>Opens the menu on its root page.</summary>
        public void Open()
        {
            _history.Clear();
            ShowPage(PauseMenuPage.Root, false);
            SetOpen(true);
        }

        /// <summary>Closes the menu and resumes.</summary>
        public void Close()
        {
            SetOpen(false);
            Resumed?.Invoke();
        }

        /// <summary>Back navigation. Closes the menu when already at the root page.</summary>
        public bool GoBack()
        {
            if (_history.Count == 0)
            {
                Close();
                return false;
            }
            ShowPage(_history.Pop(), false);
            return true;
        }

        private void ShowPage(PauseMenuPage page, bool remember = true)
        {
            if (remember && _page != page) _history.Push(_page);
            _page = page;

            SetPageVisible(_rootPage, page == PauseMenuPage.Root);
            SetPageVisible(_optionsPage, page == PauseMenuPage.Options);
            SetPageVisible(_savePage, page == PauseMenuPage.Save);

            if (_title != null)
            {
                _title.SetText(page switch
                {
                    PauseMenuPage.Options => "Options",
                    PauseMenuPage.Save => "Save",
                    _ => "Paused",
                });
            }
        }

        private static void SetPageVisible(RectTransform page, bool visible)
        {
            if (page == null) return;
            var group = UiBuilder.Group(page);
            if (visible) page.gameObject.SetActive(true);
            group.interactable = visible;
            group.blocksRaycasts = visible;
            UiTween.Fade(group, visible ? 1f : 0f, visible ? 0.18f : 0.12f, Ease.OutCubic, 0f,
                () => { if (!visible && page != null) page.gameObject.SetActive(false); });
        }

        private void SetOpen(bool open, bool immediate = false)
        {
            IsOpen = open;
            if (_group == null) return;
            if (open) gameObject.SetActive(true);
            _group.interactable = open;
            _group.blocksRaycasts = open;

            if (immediate)
            {
                _group.alpha = open ? 1f : 0f;
                gameObject.SetActive(open);
                return;
            }

            if (open && _panel != null)
            {
                _panel.localScale = Vector3.one * 0.94f;
                UiTween.Scale(_panel, Vector3.one, 0.3f, Ease.OutBack);
            }

            UiTween.Fade(_group, open ? 1f : 0f, open ? 0.2f : 0.15f, open ? Ease.OutCubic : Ease.InCubic, 0f,
                () => { if (!open && this != null) gameObject.SetActive(false); });
        }

        // -------------------------------------------------------------------- build

        /// <summary>Builds the menu, centred and modal.</summary>
        public void BuildRuntime()
        {
            _built = true;
            var root = transform as RectTransform;
            if (root == null) return;

            UiBuilder.Stretch(root);
            _group = UiBuilder.Group(this, 0f, false, false);

            var scrim = UiBuilder.Image("Scrim", root, UiSprites.Solid(), UiPalette.Backdrop.WithAlpha(0.78f),
                Image.Type.Simple, true);
            UiBuilder.Stretch(scrim.rectTransform);

            var panel = UiBuilder.Rect("Panel", root, false);
            UiBuilder.Anchor(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(480f, 560f));
            _panel = panel;

            UiBuilder.Panel("Shell", panel, UiPalette.Surface, 20, true, 30, 0.6f);
            var rim = UiBuilder.Image("Rim", panel, UiSprites.Frame(20, 1), Color.white.WithAlpha(0.07f));
            UiBuilder.Stretch(rim.rectTransform);

            var stack = UiBuilder.Rect("Stack", panel);
            UiBuilder.Stretch(stack, 26f);
            UiBuilder.Vertical(stack, 14f, null, TextAnchor.UpperLeft);

            _title = UiBuilder.Text("Title", stack, "Paused", UiTextRole.Title, UiPalette.TextPrimary);
            UiBuilder.Size(_title.rectTransform, preferredHeight: 40f, flexibleWidth: 1f);

            var pages = UiBuilder.Rect("Pages", stack);
            UiBuilder.Size(pages, flexibleHeight: 1f, flexibleWidth: 1f);

            _rootPage = BuildRootPage(pages);
            _optionsPage = BuildOptionsPage(pages);
            _savePage = BuildSavePage(pages);

            _footer = UiBuilder.Text("Footer", stack, "Esc closes · Backspace goes back", UiTextRole.Caption,
                UiPalette.TextMuted);
            UiBuilder.Size(_footer.rectTransform, preferredHeight: 16f, flexibleWidth: 1f);

            ShowPage(PauseMenuPage.Root, false);
            UiBuilder.EnsureEventSystem();
        }

        private RectTransform BuildRootPage(Transform parent)
        {
            var page = UiBuilder.Rect("RootPage", parent);
            UiBuilder.Group(page, 0f, false, false);
            UiBuilder.Vertical(page, 8f, null, TextAnchor.UpperLeft);

            BuildRow(page, "Resume", UiPalette.ScannerCyan, Close);
            BuildRow(page, "Party", UiPalette.Positive, () => PartyRequested?.Invoke());
            BuildRow(page, "Bag", UiPalette.ScannerAmber, () => BagRequested?.Invoke());
            BuildRow(page, "Dex", UiPalette.Info, () => DexRequested?.Invoke());
            BuildRow(page, "Options", UiPalette.TextSecondary, () => ShowPage(PauseMenuPage.Options));
            BuildRow(page, "Save", UiPalette.TextSecondary, () => ShowPage(PauseMenuPage.Save));
            BuildRow(page, "Quit to title", UiPalette.Negative, () => QuitRequested?.Invoke());

            return page;
        }

        private RectTransform BuildOptionsPage(Transform parent)
        {
            var page = UiBuilder.Rect("OptionsPage", parent);
            UiBuilder.Group(page, 0f, false, false);
            UiBuilder.Vertical(page, 8f, null, TextAnchor.UpperLeft);

            BuildToggle(page, "Reduced motion",
                "Completes every animation instantly. Numbers still update; they just stop moving.",
                !UiTween.MotionEnabled, on => UiTween.MotionEnabled = !on);

            BuildToggle(page, "Scanner sound", "Boot, sweep and alert cues from the Poké Lab device.",
                true, on =>
                {
                    // Muting is per-device rather than global, so the battle mix is untouched.
                    var devices = FindObjectsOfTypeCompat<ScannerAudio>();
                    for (var i = 0; i < devices.Length; i++) devices[i].SetMuted(!on);
                });

            UiBuilder.Spacer(page);
            BuildRow(page, "Back", UiPalette.TextSecondary, () => GoBack());

            return page;
        }

        private RectTransform BuildSavePage(Transform parent)
        {
            var page = UiBuilder.Rect("SavePage", parent);
            UiBuilder.Group(page, 0f, false, false);
            UiBuilder.Vertical(page, 8f, null, TextAnchor.UpperLeft);

            for (var i = 0; i < 3; i++)
            {
                var slot = i;
                BuildRow(page, "Slot " + (i + 1), UiPalette.ScannerCyan, () =>
                {
                    SaveRequested?.Invoke(slot);
                    if (_footer != null) _footer.SetText($"Saved to slot {slot + 1}.");
                });
            }

            UiBuilder.Spacer(page);
            BuildRow(page, "Back", UiPalette.TextSecondary, () => GoBack());

            return page;
        }

        private static Button BuildRow(Transform parent, string label, Color accent, Action onClick)
        {
            var root = UiBuilder.Rect("Row_" + label, parent);
            UiBuilder.Size(root, preferredHeight: 46f, minHeight: 46f, flexibleWidth: 1f);

            var background = UiBuilder.Image("Bg", root, UiSprites.Panel(11), UiPalette.SurfaceRaised,
                Image.Type.Sliced, true);
            UiBuilder.Stretch(background.rectTransform);

            var stripe = UiBuilder.Image("Stripe", root, UiSprites.Pill(6), accent);
            UiBuilder.Anchor(stripe.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(3f, -14f));

            var text = UiBuilder.Text("Label", root, label, UiTextRole.Body, UiPalette.TextPrimary);
            UiBuilder.Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(24f, 0f);

            UiButtonMotion.Attach(root, 11, accent);
            return UiBuilder.Button("Click", root, background, onClick);
        }

        private static void BuildToggle(Transform parent, string label, string description, bool initial,
            Action<bool> onChanged)
        {
            // Layout group on the root with the background excluded, so a long description
            // grows the row rather than overflowing a fixed-height card.
            var root = UiBuilder.Rect("Toggle_" + label, parent);
            UiBuilder.Horizontal(root, 12f, new RectOffset(16, 14, 8, 8), TextAnchor.MiddleLeft);
            UiBuilder.Size(root, minHeight: 58f, flexibleWidth: 1f);

            var background = UiBuilder.Backdrop("Bg", root, UiSprites.Panel(11), UiPalette.SurfaceRaised, true);

            var textStack = UiBuilder.Rect("Text", root);
            UiBuilder.Vertical(textStack, 2f, null, TextAnchor.MiddleLeft);
            UiBuilder.Size(textStack, flexibleWidth: 1f);

            var title = UiBuilder.Text("Label", textStack, label, UiTextRole.Body, UiPalette.TextPrimary);
            UiBuilder.Size(title.rectTransform, preferredHeight: 21f, flexibleWidth: 1f);

            var caption = UiBuilder.Text("Description", textStack, description, UiTextRole.Caption,
                UiPalette.TextMuted);
            caption.characterSpacing = 0f;
            UiBuilder.Size(caption.rectTransform, flexibleWidth: 1f);

            // A track-and-knob switch rather than a checkbox: the knob's position reads as
            // state from across the room, which a tick mark does not.
            var track = UiBuilder.Rect("Track", root, false);
            UiBuilder.Size(track, preferredWidth: 52f, preferredHeight: 28f, minWidth: 52f, minHeight: 28f);
            var trackImage = UiBuilder.Image("Bg", track, UiSprites.Pill(28), UiPalette.SurfaceSunken);
            UiBuilder.Stretch(trackImage.rectTransform);

            var knob = UiBuilder.Image("Knob", track, UiSprites.Dot(24), UiPalette.TextMuted, Image.Type.Simple);
            UiBuilder.Anchor(knob.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(15f, 0f), new Vector2(20f, 20f));

            var state = initial;

            void Apply(bool value, bool animate)
            {
                state = value;
                var x = value ? 37f : 15f;
                var color = value ? UiPalette.ScannerCyan : UiPalette.TextMuted;
                if (animate)
                {
                    UiTween.AnchoredMove(knob.rectTransform, new Vector2(x, 0f), 0.18f);
                    UiTween.Color(knob.color, color, 0.18f, c => { if (knob != null) knob.color = c; });
                }
                else
                {
                    knob.rectTransform.anchoredPosition = new Vector2(x, 0f);
                    knob.color = color;
                }
            }

            Apply(initial, false);

            UiButtonMotion.Attach(root, 11);
            UiBuilder.Button("Click", root, background, () =>
            {
                Apply(!state, true);
                onChanged?.Invoke(state);
            });
        }

        /// <summary>Version-tolerant object search, so the file compiles on either API.</summary>
        private static T[] FindObjectsOfTypeCompat<T>() where T : UnityEngine.Object
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            return UnityEngine.Object.FindObjectsOfType<T>(true);
#endif
        }
    }
}
