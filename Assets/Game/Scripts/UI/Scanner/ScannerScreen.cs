using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>
    /// The physical screen of the Poké Lab device: glass, backlight, hex substrate,
    /// scrolling scanlines, edge vignette and the sweep line that crosses it on every
    /// recalculation.
    ///
    /// This is what separates "a trainer's field device" from "a panel with numbers on it".
    /// None of it is a custom shader — the VFX worker owns shaders and this must not depend
    /// on their assembly — so the whole effect stack is layered uGUI graphics driven by
    /// tweens. The layer order below is the order the real object would have: backlight
    /// behind the substrate, content above it, then the physical artefacts of the glass
    /// (scanlines, vignette, glare) on top of everything including the readout.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScannerScreen : MonoBehaviour
    {
        [Header("Layers")]
        [SerializeField] private Image _glass;
        [SerializeField] private Image _backlight;
        [SerializeField] private RawImage _substrate;
        [SerializeField] private RectTransform _contentRoot;
        [SerializeField] private RawImage _scanlines;
        [SerializeField] private Image _vignette;
        [SerializeField] private Image _sweep;
        [SerializeField] private Image _alertWash;
        [SerializeField] private CanvasGroup _contentGroup;

        [Header("Tuning")]
        [Tooltip("Scanline scroll speed in texture repeats per second.")]
        [SerializeField] private float _scanlineSpeed = 0.35f;
        [Tooltip("Peak-to-peak backlight breathing, as a fraction of base alpha.")]
        [SerializeField] private float _breatheAmount = 0.14f;
        [SerializeField] private float _breathePeriod = 3.7f;

        private float _backlightBase = 0.10f;
        private float _time;
        private TweenHandle _sweepTween;
        private TweenHandle _alertTween;
        private TweenHandle _flickerTween;

        /// <summary>Parent for everything the readout draws. Never touch the effect layers directly.</summary>
        public RectTransform ContentRoot => _contentRoot;

        /// <summary>Fades the readout content without touching the glass or backlight.</summary>
        public CanvasGroup ContentGroup => _contentGroup;

        private void Update()
        {
            _time += Time.unscaledDeltaTime;

            if (_scanlines != null)
            {
                // Scroll downward: CRT-style drift reads as "live" rather than "paused".
                var uv = _scanlines.uvRect;
                uv.y -= _scanlineSpeed * Time.unscaledDeltaTime;
                if (uv.y < -1f) uv.y += 1f;
                _scanlines.uvRect = uv;
            }

            if (_backlight != null)
            {
                // Slow asymmetric breathing so the idle device never looks frozen.
                var breathe = Mathf.Sin(_time * Mathf.PI * 2f / _breathePeriod) * 0.5f + 0.5f;
                var color = _backlight.color;
                color.a = _backlightBase * (1f + (breathe - 0.5f) * 2f * _breatheAmount);
                _backlight.color = color;
            }
        }

        /// <summary>
        /// Runs the power-on sequence: glass lights, substrate resolves, sweep crosses,
        /// content fades in. <paramref name="onComplete"/> fires when the readout may render.
        /// </summary>
        public void PlayBoot(System.Action onComplete = null)
        {
            if (_contentGroup != null) _contentGroup.alpha = 0f;

            if (_glass != null)
            {
                var glassTarget = _glass.color;
                var dark = glassTarget.WithAlpha(0f);
                _glass.color = dark;
                UiTween.Color(dark, glassTarget, 0.28f, c => { if (_glass != null) _glass.color = c; }, Ease.OutExpo);
            }

            if (_substrate != null)
            {
                var target = _substrate.color;
                _substrate.color = target.WithAlpha(0f);
                UiTween.Color(target.WithAlpha(0f), target, 0.5f,
                    c => { if (_substrate != null) _substrate.color = c; }, Ease.OutCubic, 0.20f);
            }

            _backlightBase = 0f;
            UiTween.Float(0f, 0.10f, 0.6f, v => _backlightBase = v, Ease.OutCubic, 0.12f);

            // Two sweeps: the first is the calibration pass, the second lands with the content.
            PlaySweep(0.55f, 0.18f);
            PlaySweep(0.40f, 0.62f);

            UiTween.Fade(_contentGroup, 1f, 0.35f, Ease.OutCubic, 0.72f, onComplete);
            PlayFlicker(0.9f);
        }

        /// <summary>Powers the screen down. Used when the player lowers the handheld.</summary>
        public void PlayShutdown(System.Action onComplete = null)
        {
            UiTween.Fade(_contentGroup, 0f, 0.16f, Ease.InCubic);
            UiTween.Float(_backlightBase, 0f, 0.24f, v => _backlightBase = v, Ease.InCubic, 0.08f);
            if (_glass != null)
            {
                var from = _glass.color;
                UiTween.Color(from, from.WithAlpha(0f), 0.28f,
                    c => { if (_glass != null) _glass.color = c; }, Ease.InCubic, 0.1f, true, onComplete);
            }
            else
            {
                UiTween.Delay(0.3f, onComplete);
            }
        }

        /// <summary>
        /// The recalculation tell: a single sweep line crosses the glass. Called every time a
        /// new readout arrives, so the player learns that a sweep means "these numbers just
        /// changed" before they consciously read anything.
        /// </summary>
        public void PlaySweep(float duration = 0.45f, float delay = 0f)
        {
            if (_sweep == null) return;
            UiTween.Kill(ref _sweepTween);

            var rect = _sweep.rectTransform;
            _sweep.enabled = true;

            _sweepTween = UiTween.Run(duration, t =>
            {
                if (_sweep == null) return;
                // Travel top to bottom in normalised parent space.
                rect.anchorMin = new Vector2(0f, 1f - t);
                rect.anchorMax = new Vector2(1f, 1f - t);
                // Brightest in the middle of the pass; no hard on/off at either edge.
                var alpha = Mathf.Sin(t * Mathf.PI);
                _sweep.color = UiPalette.ScannerCyan.WithAlpha(alpha * 0.55f);
            }, Ease.InOutQuad, delay, true, () =>
            {
                if (_sweep != null) _sweep.color = UiPalette.ScannerCyan.WithAlpha(0f);
            });
        }

        /// <summary>
        /// Washes the glass in a colour for a moment — red when a lethal threat appears,
        /// cyan when the probability jumps the player's way. Peripheral, not modal.
        /// </summary>
        public void PlayAlert(Color color, float strength = 0.22f, float duration = 0.65f)
        {
            if (_alertWash == null) return;
            UiTween.Kill(ref _alertTween);
            _alertTween = UiTween.Run(duration, t =>
            {
                if (_alertWash == null) return;
                // Fast attack, slow release: an alert should arrive suddenly and linger.
                var envelope = t < 0.15f ? t / 0.15f : 1f - (t - 0.15f) / 0.85f;
                _alertWash.color = color.WithAlpha(envelope * strength);
            }, Ease.Linear, 0f, true, () =>
            {
                if (_alertWash != null) _alertWash.color = color.WithAlpha(0f);
            });
        }

        /// <summary>Brief instability in the content layer. Sells "signal reacquired" on a big change.</summary>
        public void PlayFlicker(float duration = 0.4f)
        {
            if (_contentGroup == null) return;
            UiTween.Kill(ref _flickerTween);
            var baseAlpha = 1f;
            _flickerTween = UiTween.Run(duration, t =>
            {
                if (_contentGroup == null) return;
                // Decaying square-ish noise. Value-noise on t keeps it deterministic per run.
                var decay = 1f - t;
                var noise = Mathf.PerlinNoise(t * 34f, 0.5f);
                _contentGroup.alpha = baseAlpha - decay * decay * (noise > 0.55f ? 0.35f : 0.04f);
            }, Ease.Linear, 0f, true, () =>
            {
                if (_contentGroup != null) _contentGroup.alpha = baseAlpha;
            });
        }

        /// <summary>
        /// Builds the full screen stack under <paramref name="parent"/>, including the
        /// housing bezel if <paramref name="withHousing"/> — the overworld handheld draws its
        /// own bezel, the docked battle version borrows the HUD's.
        /// </summary>
        public static ScannerScreen Build(Transform parent, bool withHousing, int cornerRadius = 20)
        {
            var root = UiBuilder.Rect("ScannerScreen", parent);
            var screen = root.gameObject.AddComponent<ScannerScreen>();

            Transform glassParent = root;
            if (withHousing)
            {
                // Bezel: a slightly larger, near-black rounded shell with a rim highlight,
                // so the screen reads as recessed into a physical object.
                var housing = UiBuilder.Panel("Housing", root, UiPalette.ScannerHousing, cornerRadius + 8, true, 26, 0.6f);
                var rim = UiBuilder.Image("Rim", housing.rectTransform, UiSprites.Frame(cornerRadius + 8, 2),
                    Color.white.WithAlpha(0.07f));
                UiBuilder.Stretch(rim.rectTransform);

                var inner = UiBuilder.Rect("Recess", housing.rectTransform);
                UiBuilder.Stretch(inner, 10f);
                glassParent = inner;
            }

            screen._glass = UiBuilder.Image("Glass", glassParent, UiSprites.Panel(cornerRadius), UiPalette.ScannerGlass);

            screen._backlight = UiBuilder.Image("Backlight", screen._glass.rectTransform,
                UiSprites.Panel(cornerRadius), UiPalette.ScannerCyan.WithAlpha(0.10f));

            var substrate = new GameObject("Substrate", typeof(RectTransform)).GetComponent<RectTransform>();
            substrate.SetParent(screen._glass.rectTransform, false);
            UiBuilder.Stretch(substrate);
            screen._substrate = substrate.gameObject.AddComponent<RawImage>();
            screen._substrate.texture = UiSprites.HexGrid(26).texture;
            screen._substrate.color = UiPalette.ScannerCyan.WithAlpha(0.055f);
            screen._substrate.raycastTarget = false;
            screen._substrate.uvRect = new Rect(0f, 0f, 14f, 8f);

            screen._contentRoot = UiBuilder.Rect("Content", screen._glass.rectTransform);
            UiBuilder.Stretch(screen._contentRoot, 18f);
            screen._contentGroup = UiBuilder.Group(screen._contentRoot);

            var sweep = UiBuilder.Image("Sweep", screen._glass.rectTransform, UiSprites.Solid(),
                UiPalette.ScannerCyan.WithAlpha(0f), Image.Type.Simple);
            sweep.rectTransform.anchorMin = new Vector2(0f, 1f);
            sweep.rectTransform.anchorMax = new Vector2(1f, 1f);
            sweep.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            sweep.rectTransform.sizeDelta = new Vector2(0f, 3f);
            screen._sweep = sweep;

            var scanlines = new GameObject("Scanlines", typeof(RectTransform)).GetComponent<RectTransform>();
            scanlines.SetParent(screen._glass.rectTransform, false);
            UiBuilder.Stretch(scanlines);
            screen._scanlines = scanlines.gameObject.AddComponent<RawImage>();
            screen._scanlines.texture = UiSprites.Scanlines(4).texture;
            screen._scanlines.color = Color.white.WithAlpha(0.55f);
            screen._scanlines.raycastTarget = false;
            screen._scanlines.uvRect = new Rect(0f, 0f, 1f, 120f);

            screen._vignette = UiBuilder.Image("Vignette", screen._glass.rectTransform,
                UiSprites.Vignette(128, 0.8f), Color.white, Image.Type.Simple);

            screen._alertWash = UiBuilder.Image("AlertWash", screen._glass.rectTransform,
                UiSprites.Panel(cornerRadius), Color.clear);

            return screen;
        }
    }
}
