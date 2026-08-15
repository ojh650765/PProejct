using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// Hover / press / select motion for any interactive element.
    ///
    /// Unity's built-in <see cref="Selectable"/> colour tint snaps between states, which is
    /// exactly what the brief forbids. This replaces it: scale and tint are tweened, and a
    /// separate highlight graphic fades in so the base colour stays authored by the view.
    /// Works for mouse (pointer events) and gamepad (select events) alike.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UiButtonMotion : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler,
        IPointerDownHandler, IPointerUpHandler,
        ISelectHandler, IDeselectHandler
    {
        [Header("Targets")]
        [Tooltip("Transform that scales. Defaults to this object.")]
        [SerializeField] private RectTransform _motionTarget;
        [Tooltip("Optional graphic faded in on hover/select. Created at runtime when absent.")]
        [SerializeField] private Graphic _highlight;
        [Tooltip("Optional graphic shown only while this element is the selected one.")]
        [SerializeField] private Graphic _selectionRing;

        [Header("Motion")]
        [SerializeField] private float _hoverScale = 1.025f;
        [SerializeField] private float _pressScale = 0.975f;
        [SerializeField] private float _duration = 0.14f;
        [SerializeField] private float _highlightAlpha = 0.10f;

        private TweenHandle _scaleTween;
        private TweenHandle _highlightTween;
        private bool _hovered;
        private bool _pressed;
        private bool _selected;

        /// <summary>Raised on click-equivalent activation, for views that do not use Button.</summary>
        public System.Action Activated;

        private void Awake()
        {
            if (_motionTarget == null) _motionTarget = transform as RectTransform;
        }

        private void OnDisable()
        {
            // A disabled element must not be left mid-tween at 1.025 scale.
            UiTween.Kill(ref _scaleTween);
            UiTween.Kill(ref _highlightTween);
            _hovered = _pressed = _selected = false;
            if (_motionTarget != null) _motionTarget.localScale = Vector3.one;
            if (_highlight != null) SetGraphicAlpha(_highlight, 0f);
            if (_selectionRing != null) SetGraphicAlpha(_selectionRing, 0f);
        }

        /// <summary>Wires the graphics after a runtime build.</summary>
        public void Bind(RectTransform motionTarget, Graphic highlight, Graphic selectionRing = null)
        {
            _motionTarget = motionTarget != null ? motionTarget : transform as RectTransform;
            _highlight = highlight;
            _selectionRing = selectionRing;
            if (_highlight != null) SetGraphicAlpha(_highlight, 0f);
            if (_selectionRing != null) SetGraphicAlpha(_selectionRing, 0f);
        }

        public void OnPointerEnter(PointerEventData eventData) { _hovered = true; Refresh(); }
        public void OnPointerExit(PointerEventData eventData) { _hovered = false; _pressed = false; Refresh(); }
        public void OnPointerDown(PointerEventData eventData) { _pressed = true; Refresh(); }
        public void OnPointerUp(PointerEventData eventData) { _pressed = false; Refresh(); }
        public void OnSelect(BaseEventData eventData) { _selected = true; Refresh(); }
        public void OnDeselect(BaseEventData eventData) { _selected = false; Refresh(); }

        private void Refresh()
        {
            var target = _pressed ? _pressScale : ((_hovered || _selected) ? _hoverScale : 1f);
            UiTween.Kill(ref _scaleTween);
            if (_motionTarget != null)
            {
                _scaleTween = UiTween.Scale(_motionTarget, Vector3.one * target, _duration, Ease.OutCubic);
            }

            var highlightTarget = _pressed ? _highlightAlpha * 1.8f : ((_hovered || _selected) ? _highlightAlpha : 0f);
            UiTween.Kill(ref _highlightTween);
            if (_highlight != null)
            {
                var from = _highlight.color.a;
                _highlightTween = UiTween.Run(_duration,
                    t => { if (_highlight != null) SetGraphicAlpha(_highlight, Mathf.LerpUnclamped(from, highlightTarget, t)); });
            }

            if (_selectionRing != null) SetGraphicAlpha(_selectionRing, _selected ? 1f : 0f);
        }

        private static void SetGraphicAlpha(Graphic graphic, float alpha)
        {
            var color = graphic.color;
            color.a = alpha;
            graphic.color = color;
        }

        /// <summary>
        /// Attaches motion to a freshly built element, generating the highlight and selection
        /// ring so callers do not repeat six lines at every button site.
        /// </summary>
        public static UiButtonMotion Attach(RectTransform target, int cornerRadius = 14, Color? ringColor = null)
        {
            var motion = target.gameObject.GetComponent<UiButtonMotion>();
            if (motion == null) motion = target.gameObject.AddComponent<UiButtonMotion>();

            // Both overlays are excluded from layout. Several elements that use this — switch
            // rows, option toggles — carry a layout group on the very root these are parented
            // to, and without the exclusion the highlight would be laid out as a flow child
            // and shove the real content sideways.
            var highlight = UiBuilder.Image("Highlight", target, UiSprites.Panel(cornerRadius), Color.white.WithAlpha(0f));
            highlight.rectTransform.SetAsLastSibling();
            UiBuilder.Stretch(highlight.rectTransform);
            UiBuilder.IgnoreLayout(highlight.rectTransform);

            var ring = UiBuilder.Image("SelectionRing", target, UiSprites.Frame(cornerRadius, 2),
                (ringColor ?? UiPalette.ScannerCyan).WithAlpha(0f));
            ring.rectTransform.SetAsLastSibling();
            UiBuilder.Stretch(ring.rectTransform);
            UiBuilder.IgnoreLayout(ring.rectTransform);
            // The ring sits a couple of pixels proud so it does not fight the panel edge.
            ring.rectTransform.offsetMin = new Vector2(-2f, -2f);
            ring.rectTransform.offsetMax = new Vector2(2f, 2f);

            motion.Bind(target, highlight, ring);
            return motion;
        }
    }
}
