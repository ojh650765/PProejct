using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace PokeLab.UI
{
    /// <summary>
    /// Runtime construction helpers for uGUI hierarchies.
    ///
    /// This worker ships no prefabs (the integrator owns those), and a screen assembled
    /// from thirty hand-anchored RectTransforms is impossible to get right without an
    /// editor to look at. So every view can build itself: serialized references win when
    /// the integrator has wired a prefab, and <c>BuildRuntime</c> fills the gaps otherwise.
    /// That also means the fake-readout harness produces a complete, correct screen with a
    /// single component drop, which is how this UI gets verified before the oracle lands.
    ///
    /// Layout rule enforced throughout: nothing is positioned by absolute pixel offsets
    /// against the screen. Everything is a layout group, an anchored stretch, or a content
    /// size fitter, so 16:9 and 21:9 both resolve without overlap.
    /// </summary>
    public static class UiBuilder
    {
        /// <summary>Reference resolution every canvas scales from.</summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        // ------------------------------------------------------------------ objects

        /// <summary>Creates an empty RectTransform child, stretched to its parent by default.</summary>
        public static RectTransform Rect(string name, Transform parent, bool stretch = true)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            if (stretch) Stretch(rect);
            return rect;
        }

        /// <summary>Creates an Image. Pass <c>null</c> sprite for a plain colour fill.</summary>
        public static Image Image(string name, Transform parent, Sprite sprite, Color color,
            UnityEngine.UI.Image.Type type = UnityEngine.UI.Image.Type.Sliced, bool raycast = false)
        {
            var rect = Rect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite ?? UiSprites.Solid();
            image.color = color;
            image.type = sprite == null ? UnityEngine.UI.Image.Type.Simple : type;
            image.raycastTarget = raycast;
            // Sliced sprites must not shrink their border when the rect is smaller than the
            // source; without this a 40px-tall pill draws its corners overlapping.
            if (image.type == UnityEngine.UI.Image.Type.Sliced) image.pixelsPerUnitMultiplier = 1f;
            return image;
        }

        /// <summary>
        /// A rounded panel with its drop shadow already behind it. Returns the panel image;
        /// children go under <c>panel.transform</c>.
        /// </summary>
        public static Image Panel(string name, Transform parent, Color color, int radius = 18,
            bool shadow = true, int shadowSpread = 18, float shadowOpacity = 0.5f)
        {
            var holder = Rect(name, parent);

            if (shadow)
            {
                var shadowImage = Image("Shadow", holder, UiSprites.Shadow(radius, shadowSpread),
                    UiPalette.Shadow.WithAlpha(shadowOpacity));
                var shadowRect = shadowImage.rectTransform;
                // Slightly larger than the panel and pushed down: light comes from above.
                shadowRect.offsetMin = new Vector2(-shadowSpread * 0.6f, -shadowSpread * 0.6f - 4f);
                shadowRect.offsetMax = new Vector2(shadowSpread * 0.6f, shadowSpread * 0.6f - 4f);
            }

            var panel = Image("Body", holder, UiSprites.Panel(radius), color);
            Stretch(panel.rectTransform);
            return panel;
        }

        /// <summary>A panel with a visible rim, for cards that sit on a similar-value background.</summary>
        public static Image OutlinedPanel(string name, Transform parent, Color color, int radius = 18, int border = 2)
        {
            var rect = Rect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = UiSprites.PanelOutline(radius, border);
            image.color = color;
            image.type = UnityEngine.UI.Image.Type.Sliced;
            image.raycastTarget = false;
            image.pixelsPerUnitMultiplier = 1f;
            return image;
        }

        /// <summary>Creates a TMP label with the given role already applied.</summary>
        public static TextMeshProUGUI Text(string name, Transform parent, string content,
            UiTextRole role = UiTextRole.Body, Color? color = null,
            TextAlignmentOptions alignment = TextAlignmentOptions.Left)
        {
            var rect = Rect(name, parent);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = content ?? string.Empty;
            text.alignment = alignment;
            UiType.Apply(text, role, color);
            return text;
        }

        /// <summary>
        /// Adds a Button to <paramref name="host"/> — the element's root — using
        /// <paramref name="raycastGraphic"/> (normally a child background) as the hit target.
        ///
        /// The Button must live on the root, not on the graphic. Unity dispatches pointer
        /// down, up and click with <c>ExecuteHierarchy</c>, which stops at the first
        /// GameObject carrying a handler: with the Button on a child, the root's
        /// <see cref="UiButtonMotion"/> would never see a press and every button in the game
        /// would lose its press animation while still looking fine on hover, because hover
        /// alone is dispatched to the whole ancestor chain. That is a nasty bug to find late.
        ///
        /// The transition is None because motion is owned by <see cref="UiButtonMotion"/>;
        /// Unity's built-in colour tint snaps, which the brief forbids.
        /// </summary>
        public static Button Button(string name, Transform host, Graphic raycastGraphic, Action onClick)
        {
            if (host == null) return null;
            var go = host.gameObject;
            var button = go.GetComponent<Button>();
            if (button == null) button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = raycastGraphic;
            if (raycastGraphic != null) raycastGraphic.raycastTarget = true;
            if (onClick != null) button.onClick.AddListener(() => onClick());
            return button;
        }

        // ------------------------------------------------------------------ layout

        /// <summary>Full-parent stretch with optional uniform padding.</summary>
        public static RectTransform Stretch(RectTransform rect, float padding = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(padding, padding);
            rect.offsetMax = new Vector2(-padding, -padding);
            return rect;
        }

        /// <summary>Anchors to a corner or edge with an explicit size. Use only for overlays.</summary>
        public static RectTransform Anchor(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 pivot, Vector2 offset, Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = offset;
            rect.sizeDelta = size;
            return rect;
        }

        /// <summary>Adds a vertical layout group configured the way this UI always wants it.</summary>
        public static VerticalLayoutGroup Vertical(RectTransform rect, float spacing = 8f,
            RectOffset padding = null, TextAnchor alignment = TextAnchor.UpperLeft,
            bool expandWidth = true, bool expandHeight = false)
        {
            var group = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            group.spacing = spacing;
            group.padding = padding ?? new RectOffset(0, 0, 0, 0);
            group.childAlignment = alignment;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = expandWidth;
            group.childForceExpandHeight = expandHeight;
            return group;
        }

        /// <summary>Adds a horizontal layout group configured the way this UI always wants it.</summary>
        public static HorizontalLayoutGroup Horizontal(RectTransform rect, float spacing = 8f,
            RectOffset padding = null, TextAnchor alignment = TextAnchor.MiddleLeft,
            bool expandWidth = false, bool expandHeight = false)
        {
            var group = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.spacing = spacing;
            group.padding = padding ?? new RectOffset(0, 0, 0, 0);
            group.childAlignment = alignment;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = expandWidth;
            group.childForceExpandHeight = expandHeight;
            return group;
        }

        /// <summary>Adds a grid layout group with a fixed cell size.</summary>
        public static GridLayoutGroup Grid(RectTransform rect, Vector2 cell, Vector2 spacing,
            int columns, RectOffset padding = null)
        {
            var group = rect.gameObject.AddComponent<GridLayoutGroup>();
            group.cellSize = cell;
            group.spacing = spacing;
            group.padding = padding ?? new RectOffset(0, 0, 0, 0);
            group.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            group.constraintCount = Mathf.Max(1, columns);
            return group;
        }

        /// <summary>
        /// A background graphic that fills its parent and takes no part in layout.
        ///
        /// This is the counterpart to putting the layout group on the element root rather
        /// than on a padded child. Without <c>ignoreLayout</c> the backdrop would be treated
        /// as a sibling of the content and pushed into the flow, which is how panels end up
        /// with their own background stacked above their text.
        /// </summary>
        public static Image Backdrop(string name, Transform parent, Sprite sprite, Color color, bool raycast = false)
        {
            var image = Image(name, parent, sprite, color, UnityEngine.UI.Image.Type.Sliced, raycast);
            Stretch(image.rectTransform);
            IgnoreLayout(image.rectTransform);
            return image;
        }

        /// <summary>Excludes a rect from its parent layout group, keeping its own anchors.</summary>
        public static LayoutElement IgnoreLayout(RectTransform rect)
        {
            var element = rect.gameObject.GetComponent<LayoutElement>();
            if (element == null) element = rect.gameObject.AddComponent<LayoutElement>();
            element.ignoreLayout = true;
            return element;
        }

        /// <summary>
        /// Makes a rect size itself to its children. Vertical-only by default.
        ///
        /// Only valid on a rect that is <b>not</b> a child of a layout group. Inside one, the
        /// group already asks the child for its preferred size through ILayoutElement — which
        /// TMP text, Image and any LayoutGroup all implement — and a fitter fighting the group
        /// for the same rect is the usual cause of jittering or overlapping panels. Put the
        /// layout group on the element's own root instead and give its backdrop
        /// <see cref="IgnoreLayout"/>.
        /// </summary>
        public static ContentSizeFitter Fit(RectTransform rect,
            ContentSizeFitter.FitMode horizontal = ContentSizeFitter.FitMode.Unconstrained,
            ContentSizeFitter.FitMode vertical = ContentSizeFitter.FitMode.PreferredSize)
        {
            var fitter = rect.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = horizontal;
            fitter.verticalFit = vertical;
            return fitter;
        }

        /// <summary>Sets explicit layout sizing on an element inside a layout group.</summary>
        public static LayoutElement Size(RectTransform rect, float preferredWidth = -1f, float preferredHeight = -1f,
            float minWidth = -1f, float minHeight = -1f, float flexibleWidth = -1f, float flexibleHeight = -1f)
        {
            var element = rect.gameObject.GetComponent<LayoutElement>();
            if (element == null) element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = preferredWidth;
            element.preferredHeight = preferredHeight;
            element.minWidth = minWidth;
            element.minHeight = minHeight;
            element.flexibleWidth = flexibleWidth;
            element.flexibleHeight = flexibleHeight;
            return element;
        }

        /// <summary>An invisible flexible gap that pushes siblings apart inside a layout group.</summary>
        public static RectTransform Spacer(Transform parent, float flexible = 1f, string name = "Spacer")
        {
            var rect = Rect(name, parent);
            Size(rect, flexibleWidth: flexible, flexibleHeight: flexible);
            return rect;
        }

        /// <summary>A one-pixel hairline divider that spans its layout group.</summary>
        public static Image Divider(Transform parent, float thickness = 1f, Color? color = null)
        {
            var image = Image("Divider", parent, UiSprites.Solid(), color ?? UiPalette.Divider,
                UnityEngine.UI.Image.Type.Simple);
            Size(image.rectTransform, preferredHeight: thickness, minHeight: thickness, flexibleWidth: 1f);
            return image;
        }

        /// <summary>
        /// A vertical scroll view built the way every list in this UI needs it: no visible
        /// scrollbar, content sized by a fitter, viewport masked with a rounded rect so the
        /// clip matches the card corner radius.
        /// </summary>
        public static ScrollRect ScrollList(string name, Transform parent, out RectTransform content,
            float spacing = 8f, RectOffset padding = null, int maskRadius = 0)
        {
            var root = Rect(name, parent);
            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.elasticity = 0.08f;
            scroll.scrollSensitivity = 32f;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;

            var viewport = Rect("Viewport", root);
            var viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.sprite = maskRadius > 0 ? UiSprites.Panel(maskRadius) : UiSprites.Solid();
            viewportImage.type = maskRadius > 0 ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            viewportImage.color = Color.white;
            viewportImage.raycastTarget = true;
            var mask = viewport.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            content = Rect("Content", viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = new Vector2(0f, 0f);
            content.offsetMax = new Vector2(0f, 0f);
            Vertical(content, spacing, padding);
            Fit(content);

            scroll.viewport = viewport;
            scroll.content = content;
            return scroll;
        }

        // ------------------------------------------------------------------- canvas

        /// <summary>
        /// Creates (or configures) a screen-space canvas that scales correctly from 16:9 to
        /// 21:9. <c>matchWidthOrHeight = 1</c> matches height, which is the only setting that
        /// keeps a HUD readable on an ultrawide — matching width shrinks everything to
        /// nothing at 21:9.
        /// </summary>
        public static Canvas ConfigureCanvas(Canvas canvas, int sortingOrder)
        {
            if (canvas == null) return null;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;

            var scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            scaler.referencePixelsPerUnit = 100f;

            if (canvas.GetComponent<GraphicRaycaster>() == null) canvas.gameObject.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>
        /// A safe-area container. Ultrawide letterboxes and console title-safe margins both
        /// land here, so no HUD element is ever authored flush to the physical screen edge.
        /// </summary>
        public static RectTransform SafeArea(Transform parent, float horizontalMargin = 48f, float verticalMargin = 32f)
        {
            var rect = Rect("SafeArea", parent);
            rect.offsetMin = new Vector2(horizontalMargin, verticalMargin);
            rect.offsetMax = new Vector2(-horizontalMargin, -verticalMargin);
            return rect;
        }

        /// <summary>Adds a CanvasGroup, creating it only if missing.</summary>
        public static CanvasGroup Group(Component target, float alpha = 1f, bool interactable = true, bool blocksRaycasts = true)
        {
            var group = target.GetComponent<CanvasGroup>();
            if (group == null) group = target.gameObject.AddComponent<CanvasGroup>();
            group.alpha = alpha;
            group.interactable = interactable;
            group.blocksRaycasts = blocksRaycasts;
            return group;
        }

        /// <summary>Destroys every child of a transform. Used when a list rebuilds.</summary>
        public static void ClearChildren(Transform parent)
        {
            if (parent == null) return;
            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                if (Application.isPlaying) UnityEngine.Object.Destroy(child);
                else UnityEngine.Object.DestroyImmediate(child);
            }
        }

        /// <summary>
        /// Ensures an EventSystem exists so runtime-built buttons respond during isolated
        /// testing. The integrator's boot scene will normally already have one.
        /// </summary>
        public static void EnsureEventSystem()
        {
#if UNITY_2023_1_OR_NEWER
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;
#else
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;
#endif
            var go = new GameObject("EventSystem", typeof(EventSystem));
            go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }
    }
}
