using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PokeLab.UI.Editor
{
    /// <summary>
    /// Bakes the Korean font atlases once, in the editor, so the player never rasterises a glyph.
    ///
    /// <b>What this fixes, measured.</b> A development Windows player of this game, driven to the
    /// main menu, exits holding 1,036 MB — of which <b>1,035.4 MB is under the single native
    /// allocator label <c>FontEngine</c></b>. Every other label in that report totals half a
    /// megabyte. The web build shows the same shape (+797 MB the moment the menu's canvas first
    /// draws) and the same figure on a real GPU, so this is the application's cost, not the
    /// browser's.
    ///
    /// <b>Why it was invisible for so long.</b> FontEngine is not exposed by any profiler
    /// counter: not <c>Texture Memory</c>, not <c>Gfx</c>, not <c>GC</c>, and not by any of the
    /// 145 byte counters a full <c>ProfilerRecorderHandle.GetAvailable</c> enumeration turns up.
    /// It is not a UnityEngine.Object either, so an object census cannot see it. It only appears
    /// in the native allocator breakdown a desktop player writes on shutdown — which WebGL never
    /// produces, because Unity cannot deliver a quit callback there at all.
    ///
    /// <b>Why disabling the text components did not help.</b> Turning off every TMP_Text on the
    /// canvas changed nothing, because the rasterisation belongs to the FONT ASSET, not to the
    /// components: with <c>atlasPopulationMode = Dynamic</c> and <c>clearDynamicDataOnBuild</c>
    /// set, the shipped asset carries an empty atlas and TMP rebuilds every glyph through
    /// FreeType on first draw. Switching the whole Canvas off was the only prevention that
    /// worked, which is exactly the signature of a cost owned by the font rather than the label.
    ///
    /// <b>What this does.</b> Scans the game for every character it can display, adds that set to
    /// each font asset, then marks the asset Static so the player treats the baked atlas as
    /// final. One 2048x2048 atlas holds the current set at 48pt: a few megabytes in place of a
    /// gigabyte.
    ///
    /// <b>Why the scan lives here and not in a file.</b> It used to read a character list written
    /// out beforehand, and that is precisely how the atlas went stale: 648 Korean move names were
    /// added to moves.json AFTER a bake, nothing rescanned, and 88 syllables — 뚫, 뿜 and 휘 among
    /// them — reached the skill menu as empty boxes. Static means an unbaked glyph does not
    /// render at all, so the character set has to be derived from the data at bake time and
    /// re-checked at build time. <see cref="VerifyCoverage"/> is that check, and the deploy path
    /// calls it, so data added after a bake fails the build instead of shipping as tofu.
    ///
    /// <b>The bound this introduces, stated plainly.</b> The game's own text is covered by
    /// construction. Player-typed text — the trainer name — is not, and needs a deliberate
    /// policy: either a wider baked set or a small dynamic fallback whose cost is proportional to
    /// the handful of glyphs a name contains. That decision is not this tool's to make.
    /// </summary>
    public static class StaticFontAtlasBaker
    {
        private static readonly string[] FontAssets =
        {
            "Assets/Game/Art/Fonts/Resources/Fonts/Pretendard SDF.asset",
            "Assets/Game/Art/Fonts/Resources/Fonts/Pretendard SemiBold SDF.asset",
            "Assets/Game/Art/Fonts/Resources/Fonts/Pretendard Bold SDF.asset",
            "Assets/Game/Art/Fonts/Resources/Fonts/NanumGothic SDF.asset",
        };

        /// <summary>
        /// Where displayable text comes from, and nowhere else.
        ///
        /// The server's sources are in this list because its replies are shown verbatim — a
        /// rejected login prints the Worker's own Korean message. <c>.asset</c> is deliberately
        /// NOT in it: the NavMesh blobs under Assets/Game/Data/Navigation hold binary that
        /// decodes as Hangul (좂 쳫 츹 쿅 쿙 쿮) and would fill the atlas with glyphs nothing will
        /// ever display. Scenes and prefabs are absent for the opposite reason — they were
        /// checked and author no Korean at all.
        /// </summary>
        private static readonly string[][] Sources =
        {
            new[] { "Assets/Game/Data", "*.json" },
            new[] { "Assets/StreamingAssets", "*.json" },
            new[] { "Assets/Game/Scripts", "*.cs" },
            new[] { "Server/pokelab-online/src", "*.ts" },
        };

        [MenuItem("Tools/Poké Lab/Rebuild/Bake Static Font Atlases", priority = 15)]
        public static void Bake()
        {
            int scanned;
            var charset = ScanCharset(out scanned);
            if (charset.Length == 0)
            {
                Debug.LogError("[FontBake] The scan found no characters. Nothing would be baked, " +
                               "and baking nothing over a working atlas would blank the game.");
                return;
            }

            // 2048 holds roughly 1,225 glyphs at 48pt with padding. Stepping up is cheap next to
            // what this replaces — 16 MB against a gigabyte — and far cheaper than a glyph that
            // does not render, so headroom wins over tightness.
            var dimension = charset.Length <= 1100 ? 2048 : 4096;

            var report = new StringBuilder();
            report.Append("[FontBake] ").Append(charset.Length).Append(" glyphs from ")
                  .Append(scanned).Append(" files -> ").Append(dimension).Append(" atlas\n");

            foreach (var path in FontAssets)
            {
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font == null)
                {
                    report.Append("  MISSING  ").Append(path).Append('\n');
                    continue;
                }

                // atlasWidth, atlasHeight and clearDynamicDataOnBuild are read-only or private
                // on TMP_FontAsset, so the serialised fields are the only way in.
                var so = new SerializedObject(font);
                so.FindProperty("m_AtlasWidth").intValue = dimension;
                so.FindProperty("m_AtlasHeight").intValue = dimension;
                // One atlas, deliberately. Multi-atlas is what lets a dynamic font grow without
                // limit at runtime, and an asset that can still grow is one that can still
                // rasterise.
                so.FindProperty("m_IsMultiAtlasTexturesEnabled").boolValue = false;
                // The setting that made the shipped atlas empty in the first place.
                so.FindProperty("m_ClearDynamicDataOnBuild").boolValue = false;
                // Dynamic while filling: Static refuses to add anything, by design.
                so.FindProperty("m_AtlasPopulationMode").intValue = (int)AtlasPopulationMode.Dynamic;
                so.ApplyModifiedPropertiesWithoutUndo();

                font.ClearFontAssetData(setAtlasSizeToZero: true);
                string missing;
                var added = font.TryAddCharacters(charset, out missing);

                // Static from here on: the player treats the baked atlas as the whole truth and
                // never opens the source face.
                so.Update();
                so.FindProperty("m_AtlasPopulationMode").intValue = (int)AtlasPopulationMode.Static;
                so.ApplyModifiedPropertiesWithoutUndo();

                EditorUtility.SetDirty(font);

                var missingCount = string.IsNullOrEmpty(missing) ? 0 : missing.Length;
                report.Append(added ? "  baked    " : "  PARTIAL  ")
                      .Append(Path.GetFileNameWithoutExtension(path))
                      .Append("  atlas ").Append(font.atlasWidth).Append('x').Append(font.atlasHeight)
                      .Append("  characters ").Append(font.characterTable.Count)
                      .Append("  glyphs ").Append(font.glyphTable.Count);
                if (missingCount > 0)
                    report.Append("  MISSING ").Append(missingCount)
                          .Append(" (").Append(missing.Substring(0, Mathf.Min(24, missingCount)))
                          .Append(')');
                report.Append('\n');
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(report.ToString());
        }

        [MenuItem("Tools/Poké Lab/Rebuild/Verify Font Coverage", priority = 16)]
        public static void VerifyCoverageMenu()
        {
            bool ok;
            var report = VerifyCoverage(out ok);
            if (ok) Debug.Log(report);
            else Debug.LogError(report + "  Run Tools/Poké Lab/Rebuild/Bake Static Font Atlases.");
        }

        /// <summary>
        /// Reports characters the game can display that no baked atlas carries — the exact
        /// failure that put 뚫 and 뿜 on screen as empty boxes. <paramref name="ok"/> comes back
        /// false only for a glyph the source face CAN draw but the atlas lacks, because that is
        /// the one a rebake would fix; a character absent from the face itself is a font choice,
        /// not a stale bake, and failing a build over it would be crying wolf.
        /// </summary>
        public static string VerifyCoverage(out bool ok)
        {
            int scanned;
            var charset = ScanCharset(out scanned);
            var report = new StringBuilder();
            report.Append("[FontCoverage] ").Append(charset.Length)
                  .Append(" glyphs from ").Append(scanned).Append(" files\n");

            ok = true;
            foreach (var path in FontAssets)
            {
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font == null) continue;

                var absent = new StringBuilder();
                foreach (var ch in charset)
                    if (!font.HasCharacter(ch)) absent.Append(ch);

                var name = Path.GetFileNameWithoutExtension(path);
                if (absent.Length == 0)
                {
                    report.Append("  ok       ").Append(name).Append('\n');
                    continue;
                }

                var stale = new StringBuilder();
                foreach (var ch in absent.ToString())
                    if (FaceHasGlyph(font, ch)) stale.Append(ch);

                if (stale.Length > 0)
                {
                    ok = false;
                    report.Append("  STALE    ").Append(name).Append("  ").Append(stale.Length)
                          .Append(" glyph(s) the face has but the atlas does not: ")
                          .Append(stale.ToString().Substring(0, Mathf.Min(40, stale.Length)))
                          .Append('\n');
                }
                else
                {
                    report.Append("  ok       ").Append(name).Append("  (")
                          .Append(absent.Length).Append(" absent from the face itself: ")
                          .Append(absent.ToString().Substring(0, Mathf.Min(12, absent.Length)))
                          .Append(")\n");
                }
            }

            return report.ToString();
        }

        /// <summary>
        /// Whether the source face can draw a character, independent of what is baked. Answered
        /// by briefly opening the face — a Static asset never touches it at runtime, so this is
        /// an editor-only question that costs the player nothing.
        /// </summary>
        private static bool FaceHasGlyph(TMP_FontAsset font, char ch)
        {
            var face = font.sourceFontFile;
            if (face == null) return false;
            if (FontEngine.LoadFontFace(face, 48) != FontEngineError.Success) return false;

            uint index;
            var has = FontEngine.TryGetGlyphIndex(ch, out index) && index != 0;
            FontEngine.UnloadFontFace();
            return has;
        }

        /// <summary>
        /// Every character the game can put on screen, gathered from the data and code that
        /// produce its text. Sorted, so the same inputs always give the same atlas and a rebake
        /// that changed nothing stays out of the diff.
        /// </summary>
        private static string ScanCharset(out int filesScanned)
        {
            var ascii = new SortedSet<char>();
            var hangul = new SortedSet<char>();
            var other = new SortedSet<char>();

            filesScanned = 0;
            var root = Directory.GetCurrentDirectory();

            foreach (var source in Sources)
            {
                var full = Path.Combine(root, source[0]);
                if (!Directory.Exists(full)) continue;

                foreach (var file in Directory.GetFiles(full, source[1], SearchOption.AllDirectories))
                {
                    string text;
                    try { text = File.ReadAllText(file, Encoding.UTF8); }
                    catch (IOException) { continue; }

                    filesScanned++;
                    foreach (var ch in text)
                    {
                        if (ch >= 32 && ch < 127) ascii.Add(ch);
                        else if (ch >= 0xAC00 && ch <= 0xD7A3) hangul.Add(ch);
                        else if (ch > 127 && !char.IsControl(ch) && !char.IsWhiteSpace(ch)
                                 && !char.IsSurrogate(ch)) other.Add(ch);
                    }
                }
            }

            var sb = new StringBuilder();
            foreach (var ch in ascii) sb.Append(ch);
            foreach (var ch in hangul) sb.Append(ch);
            foreach (var ch in other) sb.Append(ch);
            return sb.ToString();
        }
    }
}
