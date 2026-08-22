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
    /// <b>What this does.</b> Adds the character set the game actually uses — measured from its
    /// own JSON and source, 926 glyphs of which 798 are Hangul — to each font asset, then marks
    /// the asset Static so the player treats the baked atlas as final. One 2048x2048 Alpha8
    /// atlas holds all of it at the current 48pt sampling size: four megabytes in place of a
    /// gigabyte.
    ///
    /// <b>The bound this introduces, stated plainly.</b> Static means a glyph that was not baked
    /// does not render. The game's own text is fully covered by construction. Player-typed text
    /// — the trainer name — is not, and needs a deliberate policy: either a wider baked set or a
    /// small dynamic fallback whose cost is proportional to the handful of glyphs a name
    /// contains. That decision is not this tool's to make, and it is why the report below prints
    /// what was covered rather than claiming the job is finished.
    /// </summary>
    public static class StaticFontAtlasBaker
    {
        private const string CharsetPath = "Temp/pokelab_charset.txt";

        private static readonly string[] FontAssets =
        {
            "Assets/Game/Art/Fonts/Resources/Fonts/Pretendard SDF.asset",
            "Assets/Game/Art/Fonts/Resources/Fonts/Pretendard SemiBold SDF.asset",
            "Assets/Game/Art/Fonts/Resources/Fonts/Pretendard Bold SDF.asset",
            "Assets/Game/Art/Fonts/Resources/Fonts/NanumGothic SDF.asset",
        };

        /// <summary>One atlas, big enough for the whole set at 48pt with 5px padding.</summary>
        private const int AtlasDimension = 2048;

        [MenuItem("Tools/Poké Lab/Rebuild/Bake Static Font Atlases", priority = 15)]
        public static void Bake()
        {
            var charsetFile = Path.Combine(Directory.GetCurrentDirectory(), CharsetPath);
            if (!File.Exists(charsetFile))
            {
                Debug.LogError($"[FontBake] {CharsetPath} is missing. It is written by the " +
                               "charset scan; without it this tool would have to guess which " +
                               "glyphs the game needs, and a guess here is a missing glyph.");
                return;
            }

            var charset = File.ReadAllText(charsetFile, Encoding.UTF8).Trim();
            if (charset.Length == 0)
            {
                Debug.LogError("[FontBake] The charset file is empty; nothing would be baked.");
                return;
            }

            var report = new StringBuilder();
            report.Append("[FontBake] ").Append(charset.Length).Append(" glyphs requested\n");

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
                so.FindProperty("m_AtlasWidth").intValue = AtlasDimension;
                so.FindProperty("m_AtlasHeight").intValue = AtlasDimension;
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
                var added = font.TryAddCharacters(charset, out var missing);

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
    }
}
