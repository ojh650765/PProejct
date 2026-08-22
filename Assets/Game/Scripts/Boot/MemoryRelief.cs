using System;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Gives back the memory the game has stopped using.
    ///
    /// <b>Why this has to exist.</b> Unity frees a <c>Resources.Load</c>ed asset at exactly one
    /// moment: when somebody calls <see cref="Resources.UnloadUnusedAssets"/>. Loading a new
    /// scene does not do it. Destroying every object that referenced the asset does not do it.
    /// Before this file, the whole project called it zero times — so every creature atlas the
    /// game had ever drawn stayed resident for the life of the session, and the web build's
    /// wasm heap only ever went up.
    ///
    /// That is what the OOM reports were. A five-pull gacha loads six species per pull and
    /// releases none of them, which is why the third pull was fine and the fifth aborted; and
    /// once the heap is near the ceiling, the next allocation to need a
    /// <c>WebAssembly.Memory.grow()</c> is the one that dies — which is how opening the settings
    /// panel or pressing 로그아웃 became a crash.
    ///
    /// <b>Order matters.</b> The managed collection runs first. UnloadUnusedAssets only frees
    /// assets nothing references, and a C# object that is garbage but not yet collected still
    /// counts as a reference — so calling the unload first quietly does far less than it looks
    /// like it did.
    ///
    /// <b>Where it is safe to call.</b> Not on a whim: the unload walks every loaded object and
    /// costs tens to hundreds of milliseconds. Every call site is a moment the player is already
    /// waiting — a scene change, a battle ending, a full-screen panel closing — so the hitch
    /// lands under a transition instead of in the middle of play.
    /// </summary>
    public static class MemoryRelief
    {
        /// <summary>
        /// Logs what each pass reclaimed. On by default because the number is the only evidence
        /// that any of this works, and it is one line per scene change.
        /// </summary>
        public static bool Verbose = true;

        /// <summary>Guards against two passes overlapping across a re-entrant scene load.</summary>
        private static bool s_running;

        /// <summary>
        /// Samples memory on a timer, for working out what a scene load costs.
        ///
        /// Set from a probe or the console; off in a normal session because the answer is a
        /// wall of log lines. Read by <see cref="AvPresenterHost"/>, which owns an Update the
        /// whole game already pays for.
        /// </summary>
        public static bool Trace =
            System.IO.File.Exists(System.IO.Path.Combine(
                UnityEngine.Application.persistentDataPath, "pokelab_memtrace"))
            || System.Environment.GetEnvironmentVariable("POKELAB_MEMTRACE") == "1";

        /// <summary>
        /// Prints what the engine thinks it is holding, tagged with where we were when asked.
        ///
        /// Exists because the web build aborts with OOM and leaves nothing behind to look at:
        /// the browser reports a failed heap resize and the stack is three frames of emscripten.
        /// From outside the page only the ALLOCATED heap can be read (see
        /// Tools/probe_web_memory.py), which says how big the box is and nothing about how full
        /// it is. These two figures are the inside view, and printing them on the way into a
        /// battle is the difference between knowing which allocation is too big and guessing.
        ///
        /// Reserved is what Unity has taken from the system; allocated is what is live inside
        /// it. A reserved figure climbing toward the heap size with allocated far below it is
        /// fragmentation, and a large single jump in allocated is one fat object -- they want
        /// different fixes, and this is how to tell them apart.
        /// </summary>
        public static void Report(string where)
        {
            var reserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
            var allocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            var mono = GC.GetTotalMemory(false);

            // The managed heap's RESERVED size, which GC.GetTotalMemory does not report -- it
            // answers what is in use. A collector holding a gigabyte to store fourteen
            // megabytes looks identical to no leak at all through the used figure, and on a
            // heap that can never shrink that distinction decides whether the page survives.
            var monoHeap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();

            Debug.Log($"[Memory] {where}: reserved {Mb(reserved)} MB, " +
                      $"allocated {Mb(allocated)} MB, mono {Mb(mono)} MB, " +
                      $"mono heap {Mb(monoHeap)} MB");
        }

        /// <summary>
        /// One number, one line, for bisecting a screen's cost.
        ///
        /// <see cref="Report"/> is three figures and a sentence; sprinkling twenty of those
        /// through a startup path buries the sequence in its own text. This is the same
        /// reserved figure and nothing else, which under a LINEAR growth policy tracks the wasm
        /// heap to within one 16 MB step — so a run of these reads as a profile of where the
        /// memory actually went.
        /// </summary>
        public static void Mark(string label)
        {
            Debug.Log($"[Mark] {label}: {Mb(UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong())} MB");
        }

        /// <summary>
        /// Drops what can be dropped and reports it.
        ///
        /// <paramref name="dropCreatureArt"/> also clears <see cref="CreatureThumbnail"/>'s
        /// cache, and without it the unload is largely theatre: that cache holds a Sprite per
        /// species, each Sprite holds its source texture open, and a held texture is by
        /// definition not unused. Pass true only where nothing on screen is still drawing one —
        /// a single-mode scene load tears down every canvas, so that is the safe case. Passing
        /// it while a menu is up would blank the pictures on it.
        /// </summary>
        public static void Reclaim(string reason, bool dropCreatureArt)
        {
            if (s_running) return;
            s_running = true;

            try
            {
                var before = Verbose ? UsedBytes() : 0L;

                if (dropCreatureArt) CreatureThumbnail.Clear();

                // Managed first; see the class comment.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Resources.UnloadUnusedAssets();

                if (!Verbose) return;

                var after = UsedBytes();
                Debug.Log($"[Memory] Reclaimed after {reason}: " +
                          $"{Mb(before)} MB -> {Mb(after)} MB (freed {Mb(before - after)} MB)" +
                          (dropCreatureArt ? ", creature art dropped" : ""));
            }
            finally
            {
                s_running = false;
            }
        }

        /// <summary>
        /// Mono's view of the heap. Not the wasm heap the browser aborts on — nothing inside the
        /// player can read that — but it moves with it, and it is the only figure available from
        /// in here. <c>Tools/probe_web_memory.py</c> reads the real one from outside.
        /// </summary>
        private static long UsedBytes() => GC.GetTotalMemory(false);

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1");
    }
}
