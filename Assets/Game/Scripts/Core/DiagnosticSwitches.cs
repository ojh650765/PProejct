using UnityEngine;

namespace PokeLab.Core
{
    /// <summary>
    /// Runtime switches read from the page's own URL, so one build can answer several questions.
    ///
    /// <b>Why this exists.</b> The web player's heap grows by 802 MB during the first rendered
    /// frame of the main menu — measured to the frame, resolution-independent, and belonging to
    /// no loaded object. Narrowing that further means testing hypotheses by PREVENTION: a wasm
    /// heap never shrinks, so an experiment that frees memory and re-reads the figure can only
    /// ever return zero, which is exactly the mistake that made two earlier A/Bs worthless. The
    /// only valid experiment is to not allocate the thing in the first place and compare.
    ///
    /// Prevention normally means a build per hypothesis, and a build here is a quarter of an
    /// hour. Reading the flags from <c>Application.absoluteURL</c> turns that into a query
    /// string and a two-minute run:
    ///
    ///     index.html?pl_noaudio=1     do not load the audio catalogue at all
    ///     index.html?pl_nomusic=1     load it, but never start a track
    ///     index.html?pl_nocamera=1    disable the scene camera before the first frame
    ///     index.html?pl_noui=1        do not build the main menu's canvas
    ///
    /// <b>These are diagnostics, not features.</b> Every one of them breaks the game on
    /// purpose. They are harmless when absent — the URL simply does not contain them — and the
    /// whole class costs one string comparison per flag, once.
    /// </summary>
    public static class Diag
    {
        private static string s_source;

        /// <summary>
        /// Where the flags come from: the page URL on the web, the command line everywhere else.
        ///
        /// Both are read and concatenated rather than chosen between, because the point of these
        /// switches is now to run the SAME code path on two platforms and compare. A desktop
        /// player is the only place Unity will produce a native allocator breakdown — WebGL
        /// cannot even deliver a quit callback — so the switches have to survive the trip.
        /// </summary>
        private static string Source
        {
            get
            {
                if (s_source != null) return s_source;

                var text = (Application.absoluteURL ?? string.Empty);
                try
                {
                    foreach (var arg in System.Environment.GetCommandLineArgs())
                        text += " " + arg;
                }
                catch { /* some platforms refuse; the URL alone still works */ }

                s_source = text.ToLowerInvariant();
                return s_source;
            }
        }

        /// <summary>True when the page URL or the command line carries this flag.</summary>
        public static bool Has(string flag) => Source.Contains(flag);

        /// <summary>Skip loading the AudioClipCatalog, and with it all 153 clips it references.</summary>
        public static bool NoAudio => Has("pl_noaudio");

        /// <summary>Load audio but never start a track.</summary>
        public static bool NoMusic => Has("pl_nomusic");

        /// <summary>Disable the scene camera, so nothing is drawn.</summary>
        public static bool NoCamera => Has("pl_nocamera");

        /// <summary>Skip building the main menu's canvas.</summary>
        public static bool NoUi => Has("pl_noui");

        /// <summary>
        /// Build the canvas exactly as normal, then switch the Canvas component off before the
        /// first frame is drawn.
        ///
        /// This is the honest version of <see cref="NoUi"/>. Skipping construction removes the
        /// objects AND the draw, so a difference between the two runs cannot say which of them
        /// spent the memory. This one leaves every object in place and prevents only the
        /// rendering, which is the thing the frame-by-frame marks pointed at.
        /// </summary>
        public static bool NoDraw => Has("pl_nodraw");

        /// <summary>Draw the canvas without any text.</summary>
        public static bool NoText => Has("pl_notext");

        /// <summary>Draw the canvas without any images.</summary>
        public static bool NoImages => Has("pl_noimages");

        /// <summary>Draw the canvas without layout groups or content size fitters.</summary>
        public static bool NoLayout => Has("pl_nolayout");

        /// <summary>Draw the canvas without shadows, outlines or masks.</summary>
        public static bool NoEffects => Has("pl_noeffects");

        /// <summary>
        /// Go straight from the login screen to the title screen, signed out.
        ///
        /// For the desktop cross-check: the question is what the main menu's first draw costs on
        /// a platform whose allocator can be broken down, and typing credentials into a windowed
        /// player is not part of that question.
        /// </summary>
        public static bool SkipLogin => Has("pl_skiplogin");

        /// <summary>Dump the full profiler counter table on every scene load.</summary>
        public static bool AutoCounters => Has("pl_counters");

        /// <summary>
        /// Keep only this fraction of the canvas's CanvasRenderers alive: 1, 1/2, 1/4 or 1/8.
        ///
        /// The halving test. Disabling every Graphic changed nothing while disabling the Canvas
        /// itself saved 798.5 MB, which says the cost belongs to the canvas hierarchy rather
        /// than to what it draws. The menu carries 147 CanvasRenderers and 798.5/147 is 5.4 MB
        /// apiece — a suspiciously round story that deserves to be falsified rather than
        /// believed. If the cost is per renderer it halves with the count; if it is a fixed
        /// price for having a canvas at all, it does not move.
        /// </summary>
        public static float UiKeep =>
            Has("pl_ui8") ? 0.125f : Has("pl_ui4") ? 0.25f : Has("pl_ui2") ? 0.5f : 1f;

        /// <summary>One line at boot, so a run's log says which switches were on.</summary>
        public static string Summary =>
            (NoAudio ? "noaudio " : "") + (NoMusic ? "nomusic " : "") +
            (NoCamera ? "nocamera " : "") + (NoUi ? "noui " : "") +
            (NoDraw ? "nodraw " : "") + (NoText ? "notext " : "") +
            (NoImages ? "noimages " : "") + (NoLayout ? "nolayout " : "") +
            (NoEffects ? "noeffects " : "") +
            (UiKeep < 1f ? $"uikeep{UiKeep:0.000} " : "");
    }
}
