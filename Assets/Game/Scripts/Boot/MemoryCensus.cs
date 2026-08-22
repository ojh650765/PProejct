using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Itemises every loaded engine object, so an OOM can be attributed instead of guessed at.
    ///
    /// <b>Why one number was not enough.</b> <see cref="MemoryRelief.Report"/> says how much
    /// Unity is holding, and that figure grew from 560 MB to 1942 MB between the main menu and
    /// the battle button — with no scene load anywhere in between. That fact rules nothing out.
    /// Two hypotheses fitted it equally well: one fat asset, or a few thousand copies of a small
    /// one. Reading the source distinguished neither, and the arithmetic on the shipped art says
    /// the whole sprite library is 365 MB even if every last file is resident — so the memory is
    /// not coming from the places the source makes obvious. This walks the actual object table.
    ///
    /// Three views, because each answers a different question. Totals per type say which
    /// subsystem. The biggest singles say whether one object is absurd. The biggest by repeated
    /// name says whether something is being minted per use instead of cached — which is the
    /// failure that looks like nothing at all in a diff.
    ///
    /// <b>On the sizes.</b> Texture bytes are computed from dimensions and format rather than
    /// read from <c>Profiler.GetRuntimeMemorySizeLong</c>, which is documented for the editor and
    /// can answer 0 in a release player. A silent zero would read as "no textures loaded", the
    /// most misleading answer available. The profiler's total is printed alongside so the two can
    /// be compared wherever both work.
    ///
    /// Costs a full object-table walk — tens of milliseconds at nine thousand objects. Call it at
    /// a transition the player is already waiting through, never during play.
    /// </summary>
    public static class MemoryCensus
    {
        /// <summary>How many rows each of the three views prints.</summary>
        private const int Rows = 12;

        public static void Dump(string where)
        {
            var all = Resources.FindObjectsOfTypeAll<Object>();

            var bytesByType = new Dictionary<string, long>();
            var countByType = new Dictionary<string, int>();
            var bytesByName = new Dictionary<string, long>();
            var countByName = new Dictionary<string, int>();
            var singles = new List<KeyValuePair<string, long>>();

            long profilerTotal = 0;

            foreach (var o in all)
            {
                if (o == null) continue;

                var type = o.GetType().Name;
                var bytes = Sizeof(o);

                // Whichever of the two knows more. The computed figure covers the classes this
                // models and is the only one that works in a release player; the profiler covers
                // the classes it does not -- shaders, fonts, animation -- and a shader variant
                // set is not a small thing in a web build. Taking the larger means an unmodelled
                // class still shows up wherever the profiler answers, instead of reading as zero.
                var reported = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(o);
                profilerTotal += reported;
                if (reported > bytes) bytes = reported;

                countByType.TryGetValue(type, out var typeCount);
                countByType[type] = typeCount + 1;
                bytesByType.TryGetValue(type, out var typeBytes);
                bytesByType[type] = typeBytes + bytes;

                if (bytes <= 0) continue;

                var key = type + " " + o.name;
                bytesByName.TryGetValue(key, out var nameBytes);
                bytesByName[key] = nameBytes + bytes;
                countByName.TryGetValue(key, out var nameCount);
                countByName[key] = nameCount + 1;

                singles.Add(new KeyValuePair<string, long>(key, bytes));
            }

            long total = 0;
            foreach (var kv in bytesByType) total += kv.Value;

            var text = new StringBuilder();
            text.Append("[Census] ").Append(where).Append(": ").Append(all.Length)
                .Append(" objects, accounted ").Append(Mb(total)).Append(" MB (profiler ")
                .Append(Mb(profilerTotal)).Append(" MB)\n");

            foreach (var kv in Top(bytesByType))
            {
                countByType.TryGetValue(kv.Key, out var n);
                text.Append("  ").Append(Mb(kv.Value)).Append(" MB  x").Append(n)
                    .Append("  ").Append(kv.Key).Append('\n');
            }

            text.Append("  -- biggest singles --\n");
            singles.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (var i = 0; i < singles.Count && i < Rows; i++)
                text.Append("  ").Append(Mb(singles[i].Value)).Append(" MB  ")
                    .Append(singles[i].Key).Append('\n');

            // Count, not bytes. The first run of this turned up 2,484 TextAssets in a project
            // that contains 32 text files, and they were invisible in every other view because
            // their size came back as zero -- a class this census cannot weigh, in a quantity
            // that says it should. Naming a few of them resolves that without another build.
            text.Append("  -- most numerous --\n");
            var counts = new List<KeyValuePair<string, int>>(countByType);
            counts.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (var i = 0; i < counts.Count && i < 6; i++)
            {
                text.Append("  x").Append(counts[i].Value).Append("  ").Append(counts[i].Key)
                    .Append("  e.g. ");
                var shown = 0;
                foreach (var o in all)
                {
                    if (o == null || o.GetType().Name != counts[i].Key) continue;
                    if (shown > 0) text.Append(", ");
                    text.Append(o.name);
                    if (++shown >= 3) break;
                }
                text.Append('\n');
            }

            text.Append("  -- biggest by repeated name --\n");
            foreach (var kv in Top(bytesByName))
            {
                countByName.TryGetValue(kv.Key, out var n);
                text.Append("  ").Append(Mb(kv.Value)).Append(" MB  x").Append(n)
                    .Append("  ").Append(kv.Key).Append('\n');
            }

            Debug.Log(text.ToString());
        }

        /// <summary>
        /// How the audio system is holding its clips, which the object table alone cannot say.
        ///
        /// A clip's size in the census is what its PCM WOULD cost. Whether that cost is being
        /// paid depends on the load type and on whether the data has actually been loaded, and
        /// those two facts are the difference between 152 MB of accounting and 152 MB of memory.
        /// Streaming in particular is worth naming out loud: the web player has no
        /// implementation of it, so a clip left on that setting does not stream — it takes the
        /// fallback, and the fallback is not the cheap one.
        /// </summary>
        public static void AudioBreakdown(string where)
        {
            var clips = Resources.FindObjectsOfTypeAll<AudioClip>();
            var bytesByLoadType = new Dictionary<string, long>();
            var countByLoadType = new Dictionary<string, int>();
            var loaded = 0;
            long loadedBytes = 0;

            foreach (var clip in clips)
            {
                if (clip == null) continue;
                var pcm = (long)clip.samples * clip.channels * 2;
                var key = clip.loadType + "/" + clip.loadState + (clip.preloadAudioData ? "/preload" : "");

                bytesByLoadType.TryGetValue(key, out var b);
                bytesByLoadType[key] = b + pcm;
                countByLoadType.TryGetValue(key, out var c);
                countByLoadType[key] = c + 1;

                if (clip.loadState == AudioDataLoadState.Loaded) { loaded++; loadedBytes += pcm; }
            }

            var text = new StringBuilder();
            text.Append("[Audio] ").Append(where).Append(": ").Append(clips.Length)
                .Append(" clips, ").Append(loaded).Append(" with data loaded = ")
                .Append(Mb(loadedBytes)).Append(" MB of 16-bit PCM (double it for float32)\n");
            foreach (var kv in Top(bytesByLoadType))
            {
                countByLoadType.TryGetValue(kv.Key, out var n);
                text.Append("  ").Append(Mb(kv.Value)).Append(" MB  x").Append(n)
                    .Append("  ").Append(kv.Key).Append('\n');
            }
            Debug.Log(text.ToString());
        }

        /// <summary>
        /// Throws every clip's decoded data away and reports what that was worth.
        ///
        /// This is the experiment, not a fix. The object table says the loaded assets come to
        /// 167 MB while the engine says it has 1343 MB allocated, and no amount of reading the
        /// source closes a gap that size — but one A/B does. If dropping the audio moves the
        /// engine's own figure by hundreds of megabytes, the audio system is where the memory
        /// went; if it moves it by ten, the audio is exonerated and the search continues
        /// somewhere else. Either answer is worth a run.
        ///
        /// Clips that are playing stop. That is acceptable in a diagnostic build and is why
        /// this is only ever reached deliberately, from the probe.
        /// </summary>
        public static void DropAudio(string where)
        {
            var clips = Resources.FindObjectsOfTypeAll<AudioClip>();
            MemoryRelief.Report(where + " before dropping audio");

            var dropped = 0;
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                if (clip.UnloadAudioData()) dropped++;
            }

            Resources.UnloadUnusedAssets();
            Debug.Log($"[Audio] dropped decoded data for {dropped} of {clips.Length} clips");
            MemoryRelief.Report(where + " after dropping audio");
        }

        private static List<KeyValuePair<string, long>> Top(Dictionary<string, long> source)
        {
            var list = new List<KeyValuePair<string, long>>(source);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (list.Count > Rows) list.RemoveRange(Rows, list.Count - Rows);
            return list;
        }

        /// <summary>
        /// What one object costs in native memory — near enough to rank by, which is all this
        /// needs to be. Textures get real arithmetic because they are the only class that has
        /// ever turned out to be the answer.
        /// </summary>
        private static long Sizeof(Object o)
        {
            switch (o)
            {
                case Texture2D t:
                    return TextureBytes(t.width, t.height, Bpp(t.format), t.mipmapCount);
                case Cubemap c:
                    return TextureBytes(c.width, c.height, Bpp(c.format), c.mipmapCount) * 6;
                case Texture2DArray a:
                    return TextureBytes(a.width, a.height, Bpp(a.format), a.mipmapCount) * a.depth;
                case RenderTexture r:
                    return (long)r.width * r.height * (4 + (r.depth > 0 ? 4 : 0))
                           * Mathf.Max(1, r.antiAliasing);
                case Mesh m:
                    // Rough: position, normal, tangent, uv and colour, plus 16-bit indices.
                    return (long)m.vertexCount * 44;
                case AudioClip clip:
                    return (long)clip.samples * clip.channels * 2;
                case TextAsset asset:
                    return asset.dataSize;
                default:
                    return 0;
            }
        }

        private static long TextureBytes(int width, int height, float bpp, int mips)
        {
            double bytes = 0;
            for (var i = 0; i < Mathf.Max(1, mips); i++)
                bytes += Mathf.Max(1, width >> i) * (double)Mathf.Max(1, height >> i) * bpp / 8.0;
            return (long)bytes;
        }

        /// <summary>Bits per pixel, for the formats this project can actually produce.</summary>
        private static float Bpp(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                    return 8f;
                case TextureFormat.RGB565:
                case TextureFormat.RGBA4444:
                case TextureFormat.ARGB4444:
                case TextureFormat.RG16:
                case TextureFormat.R16:
                    return 16f;
                case TextureFormat.RGB24:
                    return 24f;
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                case TextureFormat.RGB9e5Float:
                case TextureFormat.RFloat:
                    return 32f;
                case TextureFormat.RGHalf:
                case TextureFormat.RGBAHalf:
                    return 64f;
                case TextureFormat.RGBAFloat:
                    return 128f;
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.BC4:
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC2_RGB:
                    return 4f;
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ASTC_4x4:
                    return 8f;
                case TextureFormat.ASTC_6x6:
                    return 3.56f;
                case TextureFormat.ASTC_8x8:
                    return 2f;
                default:
                    // Unknown means assume the worst, which is what this is hunting.
                    return 32f;
            }
        }

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1");
    }
}
