using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PokeLab.Cinematics
{
    /// <summary>Shapes the overlay can draw. Every one of them is a timed sequence, never a toggle.</summary>
    public enum WipeStyle
    {
        /// <summary>Uniform alpha ramp. The quiet option; used under menu pushes.</summary>
        Fade = 0,
        /// <summary>Angled bars sweep across with a staggered delay. The encounter signature.</summary>
        ShutterWipe = 1,
        /// <summary>Bars close from both edges toward the centre. Used on the return trip.</summary>
        SplitWipe = 2,
        /// <summary>A short bright flash that decays. Punctuation, not a cover.</summary>
        Flash = 3,
    }

    /// <summary>
    /// The full-screen wipe layer.
    ///
    /// Deliberately built from camera-space quads and a runtime unlit material rather than
    /// from uGUI or a custom shader. Two reasons: this assembly must not take a hard
    /// dependency on the UI worker's canvas or the VFX worker's shader library to be able
    /// to stage a transition during partial integration, and a mesh parented to the brain
    /// camera survives the camera being re-parented or blended mid-transition, which a
    /// screen-space canvas bound to a destroyed camera does not.
    ///
    /// Assign <see cref="overrideMaterial"/> to swap in the VFX worker's stylised wipe once
    /// it exists; the geometry and timing stay identical.
    /// </summary>
    [DefaultExecutionOrder(-400)]
    public sealed class ScreenTransitionOverlay : MonoBehaviour
    {
        [Header("Look")]
        [Tooltip("Colour of the wipe. Alpha is driven by the sequence, not read from here.")]
        [SerializeField] private Color wipeColor = new Color(0.04f, 0.05f, 0.09f, 1f);
        [Tooltip("Colour used by WipeStyle.Flash.")]
        [SerializeField] private Color flashColor = new Color(1f, 0.98f, 0.92f, 1f);
        [Tooltip("Number of bars in the shutter and split wipes. Odd numbers read better.")]
        [Range(1, 24)]
        [SerializeField] private int barCount = 9;
        [Tooltip("Bar tilt in degrees. Zero is a plain vertical wipe; 12-18 reads as stylised.")]
        [SerializeField] private float barTilt = 14f;
        [Tooltip("Fraction of the total duration spread across the bar stagger.")]
        [Range(0f, 0.9f)]
        [SerializeField] private float barStagger = 0.45f;

        [Header("Overrides")]
        [Tooltip("Optional replacement material. Must be unlit, transparent, and honour _BaseColor or _Color.")]
        [SerializeField] private Material overrideMaterial;
        [Tooltip("Distance in front of the near clip plane the overlay is drawn at.")]
        [SerializeField] private float nearPlaneMargin = 0.02f;

        private Camera _target;
        private Transform _rig;
        private readonly List<Transform> _bars = new List<Transform>();
        private readonly List<MeshRenderer> _barRenderers = new List<MeshRenderer>();
        private Vector2 _barBaseScale = Vector2.one;

        // The last applied state, so LateUpdate can re-layout for a changed aspect or FOV
        // and then re-apply the same visual state instead of resetting the wipe to full.
        private WipeStyle _lastStyle = WipeStyle.Fade;
        private float _lastProgress;
        private Color _lastColor = Color.black;

        private MaterialPropertyBlock _mpb;
        private Material _material;
        private Mesh _quad;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>Current opaque coverage, 0 clear to 1 fully covered. Read by the director to sync cuts.</summary>
        public float Coverage { get; private set; }

        /// <summary>True while the screen is fully covered — the only frame range where a hard cut is invisible.</summary>
        public bool IsCovered => Coverage >= 0.995f;

        private void Awake()
        {
            EnsureResources();
            EnsureRig();
            SetCoverage(0f, wipeColor);
        }

        /// <summary>
        /// Creates the mesh, material and property block.
        ///
        /// Split out of <c>Awake</c> and called from <see cref="EnsureRig"/> as well, because
        /// <see cref="TransitionDirector"/> resolves this component from another GameObject
        /// and may call <see cref="AttachTo"/> before this object's <c>Awake</c> has run. Built
        /// only in Awake, the bars would be created with a null mesh and a null material and
        /// the wipe would silently render nothing.
        /// </summary>
        private void EnsureResources()
        {
            _mpb ??= new MaterialPropertyBlock();
            if (_quad == null) _quad = BuildQuad();
            if (_material == null) _material = overrideMaterial != null ? overrideMaterial : BuildFallbackMaterial();
        }

        private void OnDestroy()
        {
            if (_quad != null) Destroy(_quad);
            if (_material != null && overrideMaterial == null) Destroy(_material);
        }

        /// <summary>
        /// Points the overlay at the camera that is actually rendering. The director calls
        /// this whenever the live brain changes so the wipe never ends up parented to a
        /// camera that has been blended out.
        /// </summary>
        public void AttachTo(Camera camera)
        {
            if (camera == null || _target == camera) return;
            _target = camera;
            EnsureRig();
            _rig.SetParent(camera.transform, false);
            LayoutBars();
        }

        private void EnsureRig()
        {
            if (_rig != null) return;
            EnsureResources();

            var rigGo = new GameObject("~ScreenWipe") { hideFlags = HideFlags.DontSave };
            _rig = rigGo.transform;
            _rig.SetParent(transform, false);

            for (int i = 0; i < Mathf.Max(1, barCount); i++)
            {
                var bar = new GameObject("Bar" + i) { hideFlags = HideFlags.DontSave };
                bar.transform.SetParent(_rig, false);

                var mf = bar.AddComponent<MeshFilter>();
                mf.sharedMesh = _quad;

                var mr = bar.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _material;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                mr.allowOcclusionWhenDynamic = false;

                _bars.Add(bar.transform);
                _barRenderers.Add(mr);
            }
        }

        /// <summary>
        /// Sizes the bars to exactly cover the frustum at the overlay plane, with a margin
        /// so a tilted bar cannot expose a corner. Recomputed on aspect or FOV change.
        /// </summary>
        private void LayoutBars()
        {
            if (_target == null || _rig == null) return;

            float z = _target.nearClipPlane + nearPlaneMargin;
            float halfHeight = _target.orthographic
                ? _target.orthographicSize
                : z * Mathf.Tan(_target.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfWidth = halfHeight * _target.aspect;

            // The tilt rotates each bar, so the swept rectangle must be oversized by the
            // projection of the bar's own height onto the horizontal axis, or the top and
            // bottom corners of a tilted bar leave triangular gaps.
            float tiltPad = Mathf.Abs(Mathf.Tan(barTilt * Mathf.Deg2Rad)) * halfHeight * 2f;
            float sweptWidth = halfWidth * 2f + tiltPad + 0.05f;
            float barWidth = sweptWidth / _bars.Count;
            float barHeight = halfHeight * 2f + 0.05f;

            // Extra 1.02 on width closes the seam between neighbouring tilted bars.
            _barBaseScale = new Vector2(barWidth * 1.02f, barHeight);

            for (int i = 0; i < _bars.Count; i++)
            {
                var t = _bars[i];
                t.localPosition = new Vector3(-sweptWidth * 0.5f + barWidth * (i + 0.5f), 0f, z);
                t.localRotation = Quaternion.Euler(0f, 0f, barTilt);
                t.localScale = new Vector3(_barBaseScale.x, _barBaseScale.y, 1f);
            }
        }

        private void LateUpdate()
        {
            // Aspect and FOV change with window resize and with every lens blend, so the
            // layout is refreshed whenever the overlay is actually visible — then the last
            // applied state is re-stamped on top, or the re-layout would erase the wipe.
            if (Coverage <= 0f) return;
            LayoutBars();
            ApplyStyle(_lastStyle, _lastProgress, _lastColor);
        }

        // --- Public sequences -------------------------------------------------------------

        /// <summary>Covers the screen over <paramref name="duration"/> seconds and leaves it covered.</summary>
        public IEnumerator CoverIn(float duration, WipeStyle style = WipeStyle.ShutterWipe)
        {
            EnsureRig();
            Color color = style == WipeStyle.Flash ? flashColor : wipeColor;

            // A flash arrives hard; a cover arrives smoothly. Assigned rather than written as
            // a ternary because a method group has no type until it is assigned to one.
            CinematicEase.Function ease = CinematicEase.InOutCubic;
            if (style == WipeStyle.Flash) ease = CinematicEase.OutExpo;

            yield return CinematicRunner.Progress(duration, p => ApplyStyle(style, ease(p), color));
            ApplyStyle(style, 1f, color);
        }

        /// <summary>Uncovers the screen over <paramref name="duration"/> seconds.</summary>
        public IEnumerator CoverOut(float duration, WipeStyle style = WipeStyle.ShutterWipe)
        {
            EnsureRig();
            Color color = style == WipeStyle.Flash ? flashColor : wipeColor;
            yield return CinematicRunner.Progress(duration, p => ApplyStyle(style, 1f - CinematicEase.InOutCubic(p), color));
            ApplyStyle(style, 0f, color);
        }

        /// <summary>
        /// Covers, invokes <paramref name="atBlack"/> on the covered frame, then uncovers.
        /// This is how a genuinely discontinuous change — swapping the whole battle stage in —
        /// is hidden without ever showing the player a cut.
        /// </summary>
        public IEnumerator CoverSwapReveal(float coverSeconds, float holdSeconds, float revealSeconds,
            System.Action atBlack, WipeStyle inStyle = WipeStyle.ShutterWipe, WipeStyle outStyle = WipeStyle.SplitWipe)
        {
            yield return CoverIn(coverSeconds, inStyle);
            atBlack?.Invoke();
            // A held covered frame gives the swap time to settle its first frame; without it
            // the reveal can catch a not-yet-culled object popping in.
            yield return CinematicRunner.Wait(Mathf.Max(holdSeconds, 0.05f));
            yield return CoverOut(revealSeconds, outStyle);
        }

        /// <summary>A single decaying flash. Used for crits and for the capture click.</summary>
        public IEnumerator Flash(float duration, float peakAlpha = 0.85f)
        {
            EnsureRig();
            yield return CinematicRunner.Progress(duration, p =>
            {
                float a = p < 0.15f
                    ? Mathf.Lerp(0f, peakAlpha, p / 0.15f)
                    : peakAlpha * (1f - CinematicEase.OutExpo((p - 0.15f) / 0.85f));
                ApplyStyle(WipeStyle.Fade, a, flashColor);
            });
            ApplyStyle(WipeStyle.Fade, 0f, flashColor);
        }

        /// <summary>
        /// Immediate state set, with no easing. Only for the partial dim under a menu push
        /// and for <see cref="TransitionDirector.AbortToMode"/> — every other caller uses one
        /// of the timed sequences above.
        /// </summary>
        public void SetCoverage(float coverage, Color color)
        {
            ApplyStyle(WipeStyle.Fade, coverage, color);
        }

        // --- Rendering --------------------------------------------------------------------

        private void ApplyStyle(WipeStyle style, float progress, Color color)
        {
            EnsureRig();
            progress = Mathf.Clamp01(progress);
            Coverage = progress;
            _lastStyle = style;
            _lastProgress = progress;
            _lastColor = color;

            bool isFade = style == WipeStyle.Fade || style == WipeStyle.Flash;
            int n = _bars.Count;

            for (int i = 0; i < n; i++)
            {
                float local = StyleProgress(style, i, n, progress);
                var mr = _barRenderers[i];
                var t = _bars[i];

                if (isFade)
                {
                    t.localScale = new Vector3(_barBaseScale.x, _barBaseScale.y, 1f);
                    SetBarAlpha(mr, color, local);
                }
                else
                {
                    // Bars grow vertically from their own centre and stay fully opaque.
                    // Scaling geometry rather than ramping alpha is what makes this read as
                    // an authored wipe instead of a crossfade — the difference is obvious
                    // in motion even though a still frame at 50% looks similar.
                    t.localScale = new Vector3(_barBaseScale.x, _barBaseScale.y * local, 1f);
                    SetBarAlpha(mr, color, 1f);
                }

                mr.enabled = local > 0.001f;
            }
        }

        private float StyleProgress(WipeStyle style, int index, int count, float progress)
        {
            switch (style)
            {
                case WipeStyle.Fade:
                case WipeStyle.Flash:
                    return progress;

                case WipeStyle.SplitWipe:
                {
                    // Distance from the centre bar drives the delay, so the wipe closes inward.
                    float centre = (count - 1) * 0.5f;
                    float d = count <= 1 ? 0f : Mathf.Abs(index - centre) / centre;
                    return StaggeredRamp(progress, 1f - d);
                }

                default: // ShutterWipe
                {
                    float order = count <= 1 ? 0f : index / (float)(count - 1);
                    return StaggeredRamp(progress, order);
                }
            }
        }

        /// <summary>
        /// Maps global progress to a bar's own 0-1 given its position in the stagger.
        /// The stagger is what makes this read as authored motion rather than a crossfade.
        /// </summary>
        private float StaggeredRamp(float progress, float order)
        {
            float span = Mathf.Clamp01(1f - barStagger);
            float start = barStagger * Mathf.Clamp01(order);
            if (span <= 1e-4f) return progress >= start ? 1f : 0f;
            return CinematicEase.OutQuad(Mathf.Clamp01((progress - start) / span));
        }

        private void SetBarAlpha(MeshRenderer mr, Color color, float alpha)
        {
            color.a = Mathf.Clamp01(alpha);
            mr.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, color);
            _mpb.SetColor(ColorId, color);
            mr.SetPropertyBlock(_mpb);
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "~WipeQuad", hideFlags = HideFlags.DontSave };
            m.SetVertices(new List<Vector3>
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),   new Vector3(-0.5f, 0.5f, 0f),
            });
            m.SetUVs(0, new List<Vector2>
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            });
            m.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            m.RecalculateNormals();
            // The overlay sits inside the near plane, so a real bounds test would cull it.
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            return m;
        }

        /// <summary>
        /// Builds a transparent unlit material from whichever shader this project actually
        /// has. URP is the target, but the sprite and built-in fallbacks keep the overlay
        /// working if a pipeline variant is missing, because a transition that silently
        /// stops covering the screen is worse than one that looks slightly different.
        /// </summary>
        private static Material BuildFallbackMaterial()
        {
            Shader shader =
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Unlit/Transparent") ??
                Shader.Find("Hidden/InternalErrorShader");

            var mat = new Material(shader) { name = "~ScreenWipe", hideFlags = HideFlags.DontSave };

            // URP/Unlit defaults to opaque; switch it to alpha-blended, depth-off, overlay queue.
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);       // Transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);           // Alpha
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)RenderQueue.Overlay;
            return mat;
        }
    }
}
