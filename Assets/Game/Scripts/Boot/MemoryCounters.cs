using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Enumerates every profiler counter the player exposes and reports the ones that MOVED.
    ///
    /// <b>Why enumeration rather than a hand-picked list.</b> The first version of this asked
    /// twelve counters chosen by hand, and the answer was that Total Used Memory rose 796.9 MB
    /// between the login screen and the main menu while Gfx, Texture, Mesh, Audio and GC all
    /// stayed flat or fell. That is a useful exclusion and a bad conclusion: "none of the twelve
    /// I asked about" is not "none", and a curated list can only ever return something already
    /// suspected. <see cref="ProfilerRecorderHandle.GetAvailable"/> returns what the player
    /// actually has, so the table cannot be curated into missing the culprit.
    ///
    /// <b>Sorted by delta, because the level is not the question.</b> A counter sitting at
    /// 300 MB in both snapshots explains nothing; one that goes 40 → 690 MB explains everything.
    /// Two snapshots are taken — login and main menu — and rows are ordered by how far they moved.
    ///
    /// <b>The timing rule that already caught this class out once.</b> A ProfilerRecorder reports
    /// the frame AFTER it is created, so recorders started and read in the same breath return
    /// zeroes, which reads exactly like a set of innocent subsystems. They are created at boot
    /// by <c>AvPresenterHost.Awake</c> and only read later.
    ///
    /// <b>What a null result would mean.</b> If the full enumeration still leaves Total Used
    /// Memory up ~797 MB while every other byte counter sums to a few tens of MB, then the
    /// allocation is inside Unity's total but attributed to no counter the player exposes — and
    /// the next step is a development desktop player, where the native allocator breakdown is
    /// available, cross-checked against WebGL on the same code path. Chasing WebGL's shutdown
    /// report is not an option: Unity cannot deliver a quit callback on WebGL at all.
    /// </summary>
    public static class MemoryCounters
    {
        private struct Tracked
        {
            public string Name;
            public string Category;
            public bool IsBytes;
            public ProfilerRecorder Recorder;
        }

        private static List<Tracked> s_tracked;
        private static int s_available;

        /// <summary>The previous snapshot, so the next one can be reported as a delta.</summary>
        private static Dictionary<string, long> s_previous;
        private static string s_previousLabel;

        /// <summary>
        /// Enumerates and starts a recorder for every counter the player offers. Must run at
        /// least one frame before <see cref="Dump"/>.
        /// </summary>
        public static void Start()
        {
            if (s_tracked != null) return;
            s_tracked = new List<Tracked>(128);

            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            s_available = handles.Count;

            foreach (var handle in handles)
            {
                ProfilerRecorderDescription description;
                try { description = ProfilerRecorderHandle.GetDescription(handle); }
                catch { continue; }

                // Bytes and counts only. Timing counters cannot answer this question and there
                // are a great many of them.
                var isBytes = description.UnitType == ProfilerMarkerDataUnit.Bytes;
                var isCount = description.UnitType == ProfilerMarkerDataUnit.Count;
                if (!isBytes && !isCount) continue;

                // StartNew has no handle overload; the handle goes through the constructor and
                // the recorder is started separately.
                ProfilerRecorder recorder;
                try
                {
                    recorder = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default);
                    recorder.Start();
                }
                catch { continue; }
                if (!recorder.Valid) continue;

                s_tracked.Add(new Tracked
                {
                    Name = description.Name,
                    Category = description.Category.Name,
                    IsBytes = isBytes,
                    Recorder = recorder,
                });
            }

            Debug.Log($"[Counters] enumerated {s_available} available handles, " +
                      $"tracking {s_tracked.Count} byte/count counters");
        }

        public static void Dump(string where)
        {
            Start();

            var now = new Dictionary<string, long>(s_tracked.Count);
            foreach (var tracked in s_tracked)
            {
                if (!tracked.Recorder.Valid) continue;
                var key = tracked.Category + " / " + tracked.Name + (tracked.IsBytes ? "" : " (count)");
                now[key] = tracked.Recorder.LastValue;
            }

            var text = new StringBuilder();
            text.Append("[Counters] ").Append(where).Append(": ").Append(now.Count)
                .Append(" counters read\n");

            if (s_previous == null)
            {
                // First snapshot: levels, biggest first, so the next one has something to
                // subtract from.
                var rows = new List<KeyValuePair<string, long>>(now);
                rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (var i = 0; i < rows.Count && i < 26; i++)
                    if (rows[i].Value != 0)
                        text.Append("  ").Append(Fmt(rows[i].Value)).Append("  ")
                            .Append(rows[i].Key).Append('\n');
            }
            else
            {
                text.Append("  delta against '").Append(s_previousLabel).Append("', largest first\n");

                var deltas = new List<KeyValuePair<string, long>>();
                long moved = 0;
                foreach (var kv in now)
                {
                    s_previous.TryGetValue(kv.Key, out var before);
                    var delta = kv.Value - before;
                    if (delta == 0) continue;
                    deltas.Add(new KeyValuePair<string, long>(kv.Key, delta));
                    moved += delta;
                }

                deltas.Sort((a, b) => System.Math.Abs(b.Value).CompareTo(System.Math.Abs(a.Value)));
                for (var i = 0; i < deltas.Count && i < 26; i++)
                {
                    s_previous.TryGetValue(deltas[i].Key, out var before);
                    now.TryGetValue(deltas[i].Key, out var after);
                    text.Append("  ").Append(Fmt(before)).Append(" -> ").Append(Fmt(after))
                        .Append("   ").Append(Fmt(deltas[i].Value)).Append("   ")
                        .Append(deltas[i].Key).Append('\n');
                }

                text.Append("  -- ").Append(deltas.Count).Append(" counters moved, summing ")
                    .Append(Fmt(moved)).Append('\n');
            }

            s_previous = now;
            s_previousLabel = where;
            Debug.Log(text.ToString());
        }

        private static string Fmt(long value)
        {
            var mb = value / 1048576.0;
            return System.Math.Abs(mb) >= 0.05
                ? (mb >= 0 ? " " : "") + mb.ToString("F1") + " MB"
                : (value >= 0 ? " " : "") + value + " B";
        }
    }
}
