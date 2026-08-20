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
    /// Builds the committed Korean font assets from the committed source faces.
    ///
    /// The fonts this generates are the only thing standing between a WebGL build and a screen
    /// of empty boxes. <see cref="UiType.EnsureFont"/> used to ask the operating system for
    /// "Malgun Gothic" through <c>TMP_FontAsset.CreateFontAsset(family, style)</c>; a browser
    /// has no OS font list, so every family returned null and TextMesh Pro fell back to
    /// Liberation Sans, which is Latin-only. Every Hangul character in the game — the authored
    /// dialogue lines, the battle log, the menus — drew as a missing-glyph box, with the layout
    /// still correct so nothing looked wrong until you read it.
    ///
    /// <b>Three weights, because a real bold is not optional here.</b> The face this project
    /// shipped first, Nanum Gothic, was committed as Regular only. <see cref="UiType.Apply"/>
    /// asks for <see cref="FontStyles.Bold"/> on five of its eight roles — Metric, Title,
    /// Heading, Overline, Numeric — which is nearly every heading and button in the game, so
    /// TMP synthesised the weight instead: it dilates the glyph's signed distance field by
    /// <c>boldStyle</c> (0.75) and opens the tracking by <c>boldSpacing</c> (7). Dilation is a
    /// blunt instrument on a script whose strokes are already close together. A Hangul syllable
    /// packs three letters into one em box, so pushing every contour outward by a fixed
    /// distance closes the counters — the enclosed whites inside ㅁ, ㅂ, ㅇ, ㅎ — and the
    /// syllable turns into a smudge. That was the "폰트가 별로야" the user was looking at.
    ///
    /// The fix is a font weight table. TMP checks <c>fontWeightTable[weight/100]</c> before it
    /// synthesises anything; when a real face is parked there, <c>isUsingAltTypeface</c> comes
    /// back true and <c>TextMeshProUGUI</c> zeroes both the dilation and the extra tracking
    /// (see the <c>Handle Style Padding</c> region of TextMeshProUGUI.GenerateTextMesh). So
    /// Regular, SemiBold and Bold are each built into their own asset and cross-linked here.
    ///
    /// Source of the faces: Pretendard, from https://github.com/orioncactus/pretendard,
    /// fetched 2026-08-20. SIL Open Font License 1.1; the licence text travels with them in
    /// Assets/Game/Art/Fonts/Pretendard-OFL.txt, which is what the OFL requires of a
    /// redistribution. Malgun Gothic was deliberately not used: it is a licensed Microsoft face
    /// and this repository is pushed to GitHub. Nanum Gothic is left committed beside these as
    /// a fallback until Pretendard has been proven on screen; nothing references it any more.
    ///
    /// Generating rather than committing an opaque blob means the assets can be rebuilt from
    /// the source faces after a TMP upgrade, and means the atlas parameters below are
    /// reviewable.
    /// </summary>
    public static class KoreanFontAssetBuilder
    {
        private const string FontsFolder = "Assets/Game/Art/Fonts/";
        private const string GeneratedFolder = FontsFolder + "Resources/";

        /// <summary>Committed source faces. Not under Resources — the font assets reference them.</summary>
        public const string RegularSourcePath = FontsFolder + "Pretendard-Regular.otf";
        public const string SemiBoldSourcePath = FontsFolder + "Pretendard-SemiBold.otf";
        public const string BoldSourcePath = FontsFolder + "Pretendard-Bold.otf";

        /// <summary>
        /// Where the generated assets land. The "Resources" segment is load-bearing: a player
        /// has no AssetDatabase, so <see cref="Resources.Load"/> is the only lookup that
        /// survives a build, and it only sees what is under a Resources folder.
        /// </summary>
        public const string FontAssetPath = GeneratedFolder + UiType.KoreanFontResourcePath + ".asset";
        public const string SemiBoldFontAssetPath = GeneratedFolder + UiType.KoreanSemiBoldFontResourcePath + ".asset";
        public const string BoldFontAssetPath = GeneratedFolder + UiType.KoreanBoldFontResourcePath + ".asset";

        /// <summary>
        /// Slots in <c>TMP_FontAsset.fontWeightTable</c>. TMP indexes it by weight/100, so 4 is
        /// Regular (400), 6 SemiBold (600) and 7 Bold (700) — the slot
        /// <see cref="FontStyles.Bold"/> resolves to.
        /// </summary>
        private const int WeightRegular = 4;
        private const int WeightSemiBold = 6;
        private const int WeightBold = 7;

        /// <summary>
        /// Sampling size and padding for the signed-distance field.
        ///
        /// TMP's default is 90/9, which is wasteful here. A Hangul syllable fills its em box —
        /// three letters stacked into one glyph — so at 90pt each one claims roughly a
        /// 108x108 cell and only ~90 fit a 1024 atlas page.
        ///
        /// 48pt sampling puts a syllable in a ~63px cell, ~256 to a page. The quality floor is
        /// set by the largest role that ever renders Korean — Title at 50pt — which is
        /// effectively 1:1 here; Metric and Numeric go larger but only ever draw digits, whose
        /// simple shapes survive the 2x SDF upscale. Padding is held at ~10% of the sampling
        /// size, matching Liberation Sans SDF's 9/90, because <see cref="UiType.ApplyShadow"/>'s
        /// underlay offsets are expressed against that ratio and would change weight if it
        /// moved.
        ///
        /// Three weights means three atlases rather than one, which is the price of a real
        /// bold. It is paid in runtime texture, not in the repository: every asset is dynamic
        /// and is cleared back to a 1x1 page before it is saved (see <see cref="Verify"/>).
        /// </summary>
        private const int SamplingPointSize = 48;
        private const int AtlasPadding = 5;
        private const int AtlasDimension = 1024;

        /// <summary>A real authored line, used to prove the face actually carries Hangul.</summary>
        private const string ProbeLine = "안 오면 벌금 100만원!";

        /// <summary>
        /// The button labels the user complained about, plus the wordmark. These are the exact
        /// strings that render bold on the title screen, so they are the ones the bold face has
        /// to be able to rasterise.
        /// </summary>
        private const string BoldProbeLine = "포켓랩 지우고 다시시작 취소 확인";

        /// <summary>
        /// Regenerates the assets when any of them is missing.
        ///
        /// A clone that skipped the .asset files would compile clean, run clean in the editor —
        /// the OS fallback in <see cref="UiType"/> covers Windows — and ship a WebGL build with
        /// no Korean in it. That is the exact silent failure this whole file exists to remove,
        /// so the absence is repaired rather than reported.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void EnsureBuiltOnLoad()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath) != null
                    && AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(BoldFontAssetPath) != null) return;
                if (AssetDatabase.LoadAssetAtPath<Font>(RegularSourcePath) == null) return;
                Build();
            };
        }

        [MenuItem("PokeLab/UI/Rebuild Korean Font Asset")]
        public static void Build()
        {
            FontEngine.InitializeFontEngine();

            if (!BuildOne(RegularSourcePath, FontAssetPath)) return;
            BuildOne(SemiBoldSourcePath, SemiBoldFontAssetPath);
            BuildOne(BoldSourcePath, BoldFontAssetPath);

            // Re-fetched rather than carried over from BuildOne: ImportAsset(ForceUpdate)
            // re-reads the file, and the reference held before it can be the discarded
            // instance. A weight table wired onto a discarded instance serialises nowhere.
            AssetDatabase.Refresh();
            var regular = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            var semiBold = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(SemiBoldFontAssetPath);
            var bold = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(BoldFontAssetPath);

            LinkWeights(regular, semiBold, bold);
            RegisterAsGlobalFallback(regular);

            // CreateFontAsset copies this off TMP_Settings, and the asset's own flag is
            // internal to TMP, so the project setting is the only place it can be enforced.
            // With it off, every glyph rasterised while playing in the editor is serialised
            // into the asset and then into git and the build — megabytes of baked atlas for
            // no gain, since a dynamic asset re-rasterises them anyway.
            if (!TMP_Settings.clearDynamicDataOnBuild)
                Debug.LogWarning("[KoreanFont] TMP Settings > Clear Dynamic Data On Build is off, "
                                 + "so runtime-rasterised glyphs will be committed into the "
                                 + "generated font assets and shipped in the build.");

            Debug.Log("[KoreanFont] Built Regular/SemiBold/Bold from " + FontsFolder
                      + " (dynamic, " + SamplingPointSize + "pt sampling, padding " + AtlasPadding
                      + ", " + AtlasDimension + "x" + AtlasDimension + " pages).");

            Verify();
        }

        /// <summary>
        /// Builds one weight into one asset. Returns false only when the failure is fatal for
        /// that weight — a missing SemiBold costs a rich-text weight, a missing Regular costs
        /// the whole game its script.
        /// </summary>
        private static bool BuildOne(string sourcePath, string assetPath)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(sourcePath);
            if (font == null)
            {
                Debug.LogError("[KoreanFont] Source face missing at " + sourcePath
                               + ". Without it a build renders every Korean line as boxes.");
                return false;
            }

            // Dynamic, not static. Two reasons, and the first is decisive: the name prompt
            // (NameEntryPresenter) accepts free text, and Josa then splices that name into
            // sentences, so the set of syllables the game can be asked to draw is not the set
            // it was authored with — a static atlas cut to the authored set would render the
            // player's own name as boxes, which is this same bug wearing a smaller mask.
            // Second, a static atlas covering those needs several 1024x1024 Alpha8 pages baked
            // into the .asset. Measured: one such page costs 2.1 MB of hex-encoded YAML, so
            // ~6 MB committed and shipped per weight, against 1.5 MB for the OTF that covers
            // all 11,172 syllables. Dynamic rasterises on demand from that OTF; the cost is a
            // one-frame hitch the first time a glyph appears.
            var asset = TMP_FontAsset.CreateFontAsset(
                font, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
                AtlasDimension, AtlasDimension, AtlasPopulationMode.Dynamic,
                // Multi-atlas on: without it the first page fills and every later glyph fails
                // silently to a box, reintroducing the failure partway through the game.
                enableMultiAtlasSupport: true);

            if (asset == null)
            {
                Debug.LogError("[KoreanFont] TMP could not load a face from " + sourcePath
                               + ". Check that Include Font Data is enabled on the importer.");
                return false;
            }

            var directory = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(asset, assetPath);

            // The atlas texture and material are created in memory by CreateFontAsset; without
            // this they are not serialised and the asset reloads with a null material.
            var assetName = Path.GetFileNameWithoutExtension(assetPath);
            if (asset.atlasTextures != null && asset.atlasTextures.Length > 0 && asset.atlasTextures[0] != null)
            {
                asset.atlasTextures[0].name = assetName + " Atlas";
                AssetDatabase.AddObjectToAsset(asset.atlasTextures[0], asset);
            }
            if (asset.material != null)
            {
                asset.material.name = assetName + " Atlas Material";
                AssetDatabase.AddObjectToAsset(asset.material, asset);
            }

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return true;
        }

        /// <summary>
        /// Cross-links the three weights through TMP's font weight table.
        ///
        /// <b>What this buys.</b> With slot 7 filled, a label styled <c>FontStyles.Bold</c> —
        /// which is what <see cref="UiType.Apply"/> asks for on five roles, and what a dozen
        /// battle and dialogue views set by hand — draws Pretendard Bold's own outlines instead
        /// of Pretendard Regular dilated by 0.75 of a distance-field unit. It also covers rich
        /// text: <c>&lt;b&gt;</c> and <c>&lt;font-weight=600&gt;</c> route through the same
        /// table.
        ///
        /// <b>Why every asset points back at every other one.</b> A label whose primary asset is
        /// Bold — which is exactly what <see cref="UiType.Apply"/> now hands the display roles —
        /// can still be told <c>fontStyle = Bold</c> afterwards by a view that does not know
        /// that (TypeBadge and StatusBadge both do). Without slot 7 on the Bold asset that
        /// request finds nothing and TMP falls back to synthesising, dilating a face that is
        /// already bold. Pointing Bold's own slot 7 at itself makes the request resolve to the
        /// glyphs already in use, at zero cost and with the dilation switched off.
        ///
        /// The italic slot takes the same face. TMP shears italics in the vertex pass rather
        /// than swapping outlines, so a real weight there costs nothing and stops
        /// bold-italic — DialogueView's control slabs — from falling back to synthesis.
        ///
        /// <b>Why SerializedObject.</b> <c>TMP_FontAsset.fontWeightTable</c> has an internal
        /// setter, so an editor script outside TMP's own assembly cannot assign it. The backing
        /// field is serialised, which makes SerializedProperty the supported route — and it
        /// writes through the same path the inspector does, so the change survives a domain
        /// reload rather than living in a reflected copy.
        /// </summary>
        private static void LinkWeights(TMP_FontAsset regular, TMP_FontAsset semiBold, TMP_FontAsset bold)
        {
            SetWeight(regular, WeightSemiBold, semiBold);
            SetWeight(regular, WeightBold, bold);

            SetWeight(semiBold, WeightRegular, regular);
            SetWeight(semiBold, WeightSemiBold, semiBold);
            SetWeight(semiBold, WeightBold, bold);

            SetWeight(bold, WeightRegular, regular);
            SetWeight(bold, WeightSemiBold, semiBold);
            SetWeight(bold, WeightBold, bold);

            AssetDatabase.SaveAssets();
        }

        private static void SetWeight(TMP_FontAsset host, int weightIndex, TMP_FontAsset face)
        {
            if (host == null || face == null) return;

            var serialized = new SerializedObject(host);
            var table = serialized.FindProperty("m_FontWeightTable");
            if (table == null || !table.isArray || table.arraySize <= weightIndex)
            {
                Debug.LogError("[KoreanFont] " + host.name + " has no serialised m_FontWeightTable"
                               + " slot " + weightIndex + ". This TMP build cannot carry a real "
                               + "bold on the asset; UiType.Apply's per-role face is then the "
                               + "only thing keeping the display roles off synthesised bold.");
                return;
            }

            var pair = table.GetArrayElementAtIndex(weightIndex);
            pair.FindPropertyRelative("regularTypeface").objectReferenceValue = face;
            pair.FindPropertyRelative("italicTypeface").objectReferenceValue = face;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(host);
        }

        /// <summary>
        /// Puts the regular asset in TMP's global fallback list.
        ///
        /// <see cref="UiType.Apply"/> is the seam every label is meant to pass through, but
        /// not every label does — NameEntryPresenter builds its input field's text and
        /// placeholder with a bare AddComponent, so they take TMP's default asset, Liberation
        /// Sans, and drew the prefilled Korean name as boxes. A global fallback covers those
        /// and anything written later that forgets the seam.
        ///
        /// Only the regular weight goes in. A fallback list is consulted for glyphs the primary
        /// face is missing, so a bold entry there would answer for body copy too and quietly
        /// bold random characters. Bold reaches labels through the weight table above, which is
        /// keyed on the weight actually asked for.
        ///
        /// Done in code rather than by hand-editing TMP Settings because rebuilding the asset
        /// gives it a new GUID, which would leave a hand-written reference silently pointing
        /// at nothing — the same invisible breakage in a new place.
        /// </summary>
        private static void RegisterAsGlobalFallback(TMP_FontAsset asset)
        {
            if (asset == null) return;

            var fallbacks = TMP_Settings.fallbackFontAssets ?? new List<TMP_FontAsset>();

            // Drop nulls left by an earlier rebuild's discarded GUID, and any stale copy of
            // this same asset, before putting the current one back at the head of the list.
            fallbacks.RemoveAll(f => f == null || f.name == asset.name);
            fallbacks.Insert(0, asset);

            TMP_Settings.fallbackFontAssets = fallbacks;
            EditorUtility.SetDirty(TMP_Settings.instance);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Proves the assets can actually rasterise Hangul, that the bold is real rather than
        /// synthesised, and that the atlas is a distance field rather than a smooth bitmap.
        ///
        /// Each check drives the real runtime path rather than reading a field and trusting it:
        /// face load and FreeType raster via TryAddCharacters, weight resolution via the same
        /// <see cref="TMP_FontAssetUtilities"/> call TMP_Text makes, and the distance ramp read
        /// straight off the rasterised atlas pixels.
        /// </summary>
        [MenuItem("PokeLab/UI/Verify Korean Font Asset")]
        public static void Verify()
        {
            // Verify is a menu item in its own right, so it cannot assume Build just ran.
            FontEngine.InitializeFontEngine();

            var regular = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (regular == null)
            {
                Debug.LogError("[KoreanFont] No font asset at " + FontAssetPath + ".");
                return;
            }
            var semiBold = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(SemiBoldFontAssetPath);
            var bold = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(BoldFontAssetPath);

            // The asset existing at a path proves nothing about a build. Resources.Load is the
            // lookup UiType uses and the only one a player has, so it is checked here rather
            // than discovered as boxes on a web page.
            var viaResources = Resources.Load<TMP_FontAsset>(UiType.KoreanFontResourcePath);
            var boldViaResources = Resources.Load<TMP_FontAsset>(UiType.KoreanBoldFontResourcePath);
            var resolved = UiType.EnsureFont();
            var resolvedBold = UiType.EnsureBoldFont();

            var report = new StringBuilder();
            report.Append("[KoreanFont] Resources.Load(\"").Append(UiType.KoreanFontResourcePath)
                  .Append("\") -> ").Append(viaResources != null ? viaResources.name : "NULL")
                  .Append("; Resources.Load(\"").Append(UiType.KoreanBoldFontResourcePath)
                  .Append("\") -> ").Append(boldViaResources != null ? boldViaResources.name : "NULL")
                  .Append("\n[KoreanFont] UiType.EnsureFont() -> ")
                  .Append(resolved != null ? resolved.name : "NULL")
                  .Append("; UiType.EnsureBoldFont() -> ")
                  .Append(resolvedBold != null ? resolvedBold.name : "NULL");

            DescribeFace(report, regular, ProbeLine);
            DescribeFace(report, semiBold, BoldProbeLine);
            DescribeFace(report, bold, BoldProbeLine);

            ReportWeightTable(report, regular, bold, semiBold);
            ReportDistanceField(report, regular);
            ReportCoverage(report, RegularSourcePath);
            ReportCoverage(report, BoldSourcePath);
            ReportGlobalFallback(report, regular);

            Debug.Log(report.ToString());

            // Verification must not leave its glyphs in the committed assets, and the atlases
            // must go back to 1x1 rather than stay at the working 1024x1024: an Alpha8 page
            // that size hex-encodes to ~2 MB of YAML, which would be committed to git and
            // shipped inside the build for nothing. TryAddCharacterInternal re-expands a 1x1
            // page to atlasWidth/atlasHeight on the first glyph it rasterises at runtime.
            ClearWorkingAtlas(regular);
            ClearWorkingAtlas(semiBold);
            ClearWorkingAtlas(bold);
            AssetDatabase.SaveAssets();
        }

        private static void DescribeFace(StringBuilder report, TMP_FontAsset asset, string probe)
        {
            if (asset == null)
            {
                report.Append("\n[KoreanFont] MISSING one of the three weights — check the .otf "
                              + "importers and rerun PokeLab > UI > Rebuild Korean Font Asset.");
                return;
            }

            report.Append("\n[KoreanFont] ").Append(asset.name)
                  .Append(" face=").Append(asset.faceInfo.familyName)
                  .Append(' ').Append(asset.faceInfo.styleName)
                  .Append(" mode=").Append(asset.atlasPopulationMode)
                  .Append(" render=").Append(asset.atlasRenderMode)
                  .Append(' ').Append(asset.atlasWidth).Append('x').Append(asset.atlasHeight)
                  .Append(" pad=").Append(asset.atlasPadding)
                  .Append(" sourceFontFile=")
                  .Append(asset.sourceFontFile != null ? asset.sourceFontFile.name : "NULL (build will have no Korean)");

            if (!asset.TryAddCharacters(probe, out var missing))
                report.Append("\n  FAIL probe \"").Append(probe)
                      .Append("\" is missing: ").Append(missing);
            else
                report.Append("\n  OK probe \"").Append(probe).Append("\" rasterised, ")
                      .Append(asset.characterTable.Count).Append(" glyphs across ")
                      .Append(asset.atlasTextures != null ? asset.atlasTextures.Length : 0)
                      .Append(" atlas page(s).");
        }

        /// <summary>
        /// Asks for a bold character exactly the way TMP_Text does, and reports which asset
        /// answered.
        ///
        /// <c>isAlternativeTypeface</c> is not a cosmetic flag: it is the same bool that
        /// TextMeshProUGUI reads to decide whether to dilate the distance field. True here
        /// means the label gets Bold's own outlines and no synthesis; false means the game is
        /// still smearing Regular and the weight table did not take.
        /// </summary>
        private static void ReportWeightTable(StringBuilder report, TMP_FontAsset regular,
                                              TMP_FontAsset bold, TMP_FontAsset semiBold)
        {
            if (regular == null) return;

            var slots = regular.fontWeightTable;
            var wiredBold = slots != null && slots.Length > WeightBold ? slots[WeightBold].regularTypeface : null;
            var wiredSemi = slots != null && slots.Length > WeightSemiBold ? slots[WeightSemiBold].regularTypeface : null;

            report.Append("\n[KoreanFont] weight table on ").Append(regular.name)
                  .Append(": 600 -> ").Append(wiredSemi != null ? wiredSemi.name : "NULL")
                  .Append(", 700 -> ").Append(wiredBold != null ? wiredBold.name : "NULL");

            if (wiredBold != bold || bold == null)
            {
                report.Append("\n  FAIL slot 700 does not hold the bold asset, so FontStyles.Bold "
                              + "still synthesises weight by dilating the regular SDF.");
                return;
            }

            // 랩 — the last syllable of the wordmark, and one whose ㅂ has counters that close
            // first under dilation. If anything is going to be resolved from the wrong face it
            // is this.
            var answered = TMP_FontAssetUtilities.GetCharacterFromFontAsset(
                '랩', regular, includeFallbacks: false,
                FontStyles.Bold, FontWeight.Bold, out var isAlternativeTypeface);

            var answeredBy = answered != null && answered.textAsset != null ? answered.textAsset.name : "NULL";
            if (isAlternativeTypeface && answered != null && answered.textAsset == bold)
                report.Append("\n  OK '랩' with FontStyles.Bold resolves to ").Append(answeredBy)
                      .Append(" as an alternative typeface — TextMeshProUGUI's style-padding "
                              + "block skips the boldStyle dilation and the boldSpacing tracking "
                              + "for it. Real bold.");
            else
                report.Append("\n  FAIL '랩' with FontStyles.Bold was answered by ").Append(answeredBy)
                      .Append(" (isAlternativeTypeface=").Append(isAlternativeTypeface)
                      .Append("), so the dilation is still on.");

            if (semiBold != null && wiredSemi != semiBold)
                report.Append("\n  WARN slot 600 does not hold the semibold asset; "
                              + "<font-weight=600> will synthesise.");
        }

        /// <summary>
        /// Measures the width of the alpha ramp at a glyph edge, straight off the rasterised
        /// atlas.
        ///
        /// This settles an argument the project has had with itself. UiInkText was written
        /// around the claim that "this project's Korean font asset is generated with a smooth
        /// atlas, not a distance-field one — the ramp from inside to outside a glyph is about
        /// one pixel wide", and rims labels with eight offset copies because of it. A real
        /// SDFAA atlas ramps over roughly the padding, so the number this prints decides
        /// whether the material route (<c>_OutlineWidth</c>, <see cref="UiType.ApplyShadow"/>'s
        /// underlay) can work at all — and whether those eight extra text meshes per label can
        /// be deleted.
        /// </summary>
        private static void ReportDistanceField(StringBuilder report, TMP_FontAsset asset)
        {
            if (asset == null || asset.atlasTextures == null || asset.atlasTextures.Length == 0) return;
            var texture = asset.atlasTextures[0];
            if (texture == null || texture.width < 2) return;

            int stride, offset;
            switch (texture.format)
            {
                case TextureFormat.Alpha8:
                case TextureFormat.R8: stride = 1; offset = 0; break;
                case TextureFormat.RGBA32: stride = 4; offset = 3; break;
                case TextureFormat.ARGB32: stride = 4; offset = 0; break;
                default:
                    report.Append("\n[KoreanFont] atlas is ").Append(texture.format)
                          .Append("; ramp not measured.");
                    return;
            }

            byte[] raw;
            try { raw = texture.GetRawTextureData(); }
            catch (System.Exception e)
            {
                report.Append("\n[KoreanFont] atlas pixels unreadable (").Append(e.Message).Append(").");
                return;
            }

            // A ramp is a run of pixels that are neither solidly inside nor solidly outside.
            // Counting run lengths across every row of the page and taking the median is
            // robust against the odd hairline stroke that is all ramp.
            var histogram = new int[64];
            var samples = 0;
            var run = 0;
            for (var y = 0; y < texture.height; y++)
            {
                run = 0;
                for (var x = 0; x < texture.width; x++)
                {
                    var value = raw[(y * texture.width + x) * stride + offset];
                    if (value > 16 && value < 239) { run++; continue; }
                    if (run > 0 && run < histogram.Length) { histogram[run]++; samples++; }
                    run = 0;
                }
            }

            if (samples == 0)
            {
                report.Append("\n[KoreanFont] atlas has no partial-coverage pixels at all — "
                              + "that would be a hard 1-bit raster, not a distance field.");
                return;
            }

            var half = samples / 2;
            var running = 0;
            var median = 0;
            for (var i = 1; i < histogram.Length; i++)
            {
                running += histogram[i];
                if (running < half) continue;
                median = i;
                break;
            }

            report.Append("\n[KoreanFont] ").Append(asset.name).Append(" atlas ")
                  .Append(texture.width).Append('x').Append(texture.height).Append(' ')
                  .Append(texture.format).Append(": median edge ramp ").Append(median)
                  .Append(" px over ").Append(samples).Append(" runs (padding is ")
                  .Append(asset.atlasPadding).Append(" px).");

            if (median >= 3)
                report.Append("\n  OK that is a genuine signed distance field. TMP's material "
                              + "outline and underlay have a real gradient to read, so "
                              + "_OutlineWidth and UiType.ApplyShadow can work without "
                              + "UiInkText's eight stacked copies.");
            else
                report.Append("\n  WARN a ramp this narrow is a smooth bitmap, not a distance "
                              + "field. Material outlines will draw close to nothing and "
                              + "UiInkText's stacked copies are still load-bearing.");
        }

        /// <summary>
        /// Counts how many of the 11,172 modern Hangul syllables the source face actually
        /// carries.
        ///
        /// Coverage is a property of the face, not of the atlas — every asset here is dynamic
        /// with multi-atlas paging on, so a page filling up costs another page rather than a
        /// box. What can still ship a hole is a face that only covers the 2,350 syllables of
        /// KS X 1001, which is most of the free Korean web fonts; a player whose own name uses
        /// one of the other 8,822 would see it as boxes. So the whole block is rasterised into
        /// a throwaway asset built at 12pt in a plain smooth mode, which is fast and costs a
        /// few megabytes of scratch texture that is destroyed on the next line.
        /// </summary>
        private static void ReportCoverage(StringBuilder report, string sourcePath)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(sourcePath);
            if (font == null) return;

            var probe = TMP_FontAsset.CreateFontAsset(font, 12, 1, GlyphRenderMode.SMOOTH,
                1024, 1024, AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);
            if (probe == null)
            {
                report.Append("\n[KoreanFont] coverage of ").Append(Path.GetFileName(sourcePath))
                      .Append(" not measured (face would not load).");
                return;
            }

            var missing = 0;
            var syllables = new StringBuilder(1024);
            for (var c = 0xAC00; c <= 0xD7A3; c++)
            {
                syllables.Append((char)c);
                if (syllables.Length < 1024 && c != 0xD7A3) continue;
                if (!probe.TryAddCharacters(syllables.ToString(), out string gap))
                    missing += gap != null ? gap.Length : 0;
                syllables.Clear();
            }

            var pages = probe.atlasTextures != null ? probe.atlasTextures.Length : 0;

            // The pages are separate objects created in memory by the font asset; destroying
            // only the asset would leave several megabytes of scratch texture behind on every
            // verify, which over a working session is how an editor ends up out of memory.
            if (probe.atlasTextures != null)
                for (var i = 0; i < probe.atlasTextures.Length; i++)
                    if (probe.atlasTextures[i] != null) Object.DestroyImmediate(probe.atlasTextures[i]);
            if (probe.material != null) Object.DestroyImmediate(probe.material);
            Object.DestroyImmediate(probe);

            report.Append("\n[KoreanFont] ").Append(Path.GetFileName(sourcePath))
                  .Append(": ").Append(11172 - missing).Append('/').Append(11172)
                  .Append(" modern Hangul syllables (U+AC00..U+D7A3), ")
                  .Append(missing == 0 ? "complete." : missing + " missing.");

            // What the same set would cost the shipped 48pt atlas, so the page count is a
            // number rather than a guess. 12pt cells are ~1/4 the linear size of 48pt ones.
            report.Append(" At the probe's 12pt that filled ").Append(pages)
                  .Append(" page(s); the shipped 48pt atlas needs roughly ")
                  .Append(pages * 16).Append(" for the same set, but only pages for syllables "
                          + "the game actually draws are ever allocated.");
        }

        private static void ReportGlobalFallback(StringBuilder report, TMP_FontAsset asset)
        {
            // UiType.Apply covers every label that goes through it, but not every label does:
            // NameEntryPresenter builds its input field's text and placeholder with a bare
            // AddComponent<TextMeshProUGUI>, so those two get TMP's default asset — Liberation
            // Sans — and would still have drawn the prefilled Korean name as boxes. Registering
            // this asset as a global TMP fallback closes that class of hole for good, including
            // for any label written later that forgets the seam.
            var registered = false;
            var fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks != null)
                for (var i = 0; i < fallbacks.Count; i++)
                    if (fallbacks[i] == asset) registered = true;

            if (!registered)
                report.Append("\n[KoreanFont] WARN not in TMP Settings > Fallback Font Assets; any "
                              + "label built outside UiType.Apply will still render Korean as boxes.");
            else if (TMP_Settings.defaultFontAsset != null
                     && TMP_Settings.defaultFontAsset.HasCharacters(ProbeLine, out uint[] _,
                         searchFallbacks: true, tryAddCharacter: true))
                report.Append("\n[KoreanFont] OK probe line also resolves from the default font "
                              + "asset (").Append(TMP_Settings.defaultFontAsset.name)
                      .Append(") through the global fallback.");
            else
                report.Append("\n[KoreanFont] FAIL registered as a global fallback but the default "
                              + "font asset still cannot resolve the probe line.");
        }

        /// <summary>
        /// Renders one glyph three times and counts the coloured pixels, to settle whether
        /// TMP's material outline and underlay actually draw against this font asset.
        ///
        /// <b>Why a render rather than an argument.</b> UiInkText rims display type with eight
        /// offset copies of the label because <c>_OutlineWidth = 0.24</c> once came back
        /// invisible on the title screen, and it attributes that to the atlas being a smooth
        /// bitmap. <see cref="ReportDistanceField"/> measures the atlas and disagrees. Neither
        /// of those is a picture, and the question is about a picture, so this draws one: a
        /// throwaway <see cref="TextMeshPro"/> parked far outside the scene, photographed by a
        /// throwaway camera into a RenderTexture, with the rim colour counted in the readback.
        ///
        /// Everything it makes is <see cref="HideFlags.HideAndDontSave"/> and destroyed on the
        /// way out, so it neither dirties the open scene nor leaves anything behind in it.
        /// </summary>
        [MenuItem("PokeLab/UI/Verify Font Outline")]
        public static void VerifyOutline()
        {
            var face = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(BoldFontAssetPath)
                       ?? AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (face == null)
            {
                Debug.LogError("[KoreanFont] No font asset to test an outline against.");
                return;
            }

            // Far enough out that no scene geometry can wander into frame; the camera clears to
            // solid black rather than the skybox for the same reason.
            var origin = new Vector3(0f, 100000f, 0f);

            var textObject = new GameObject("~FontOutlineProbe") { hideFlags = HideFlags.HideAndDontSave };
            textObject.transform.position = origin;
            var label = textObject.AddComponent<TextMeshPro>();
            label.font = face;
            label.text = "랩";
            label.fontSize = 36f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.rectTransform.sizeDelta = new Vector2(10f, 10f);

            var cameraObject = new GameObject("~FontOutlineCamera") { hideFlags = HideFlags.HideAndDontSave };
            cameraObject.transform.position = origin + new Vector3(0f, 0f, -10f);
            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 4f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 40f;

            var report = new StringBuilder("[KoreanFont] outline probe on ").Append(face.name);
            try
            {
                var material = label.fontMaterial;
                var plain = Photograph(label, camera);

                // A frame with no white in it means the probe never drew the glyph — under a
                // scriptable render pipeline Camera.Render can decline to do anything — and a
                // rim count of zero would then be a fact about the probe, not about the font.
                // Say so rather than report a failure that was never tested.
                if (plain.face < 50)
                {
                    Debug.LogWarning("[KoreanFont] outline probe rendered no glyph at all ("
                                     + plain.face + " face pixels), so nothing was tested. This "
                                     + "is a limitation of the probe under the active render "
                                     + "pipeline, not a verdict on the font asset.");
                    return;
                }

                // The material route, driven the way a caller would: set the width, then let
                // the label rebuild. The rebuild is not optional — TMP sizes each glyph's quad
                // from the material's outline and underlay settings at generation time, so a
                // width raised afterwards draws outside the quad it is clipped to and is
                // invisible for a reason that has nothing to do with the atlas.
                material.SetFloat(Shader.PropertyToID("_OutlineWidth"), 0.3f);
                material.SetColor(Shader.PropertyToID("_OutlineColor"), Color.red);
                var widthOnly = Photograph(label, camera);

                // The same width again, with the keyword the shader gates the outline behind.
                // TMP_SDF-Mobile — which is the shader every asset this builder generates gets —
                // wraps its outline blend in "#ifdef OUTLINE_ON" and declares it as a
                // shader_feature, so on a material without the keyword the outline branch is
                // not merely disabled, it is not compiled. _OutlineWidth then moves a number
                // nothing reads. This is what "the wordmark came back with no outline on it"
                // was; it is the shader's opt-in, not the atlas.
                material.EnableKeyword("OUTLINE_ON");
                var outlined = Photograph(label, camera);
                material.DisableKeyword("OUTLINE_ON");
                material.SetFloat(Shader.PropertyToID("_OutlineWidth"), 0f);

                ApplyShadowForProbe(label);
                var underlaid = Photograph(label, camera);

                report.Append("\n  face/rim pixels: plain ").Append(plain.face).Append('/').Append(plain.rim)
                      .Append(", _OutlineWidth=0.3 alone ").Append(widthOnly.face).Append('/').Append(widthOnly.rim)
                      .Append(", + OUTLINE_ON ").Append(outlined.face).Append('/').Append(outlined.rim)
                      .Append(", ApplyShadow underlay ").Append(underlaid.face).Append('/').Append(underlaid.rim);

                if (outlined.rim > plain.rim + 50 && widthOnly.rim <= plain.rim + 50)
                    report.Append("\n  OK the material outline draws, but only once OUTLINE_ON is "
                                  + "enabled on the material. Setting _OutlineWidth on its own "
                                  + "does nothing on TMP_SDF-Mobile — which is what UiInkText "
                                  + "hit, and it is a missing keyword rather than a smooth atlas.");
                else if (outlined.rim > plain.rim + 50)
                    report.Append("\n  OK the material outline draws.");
                else
                    report.Append("\n  FAIL the material outline draws nothing even with "
                                  + "OUTLINE_ON; UiInkText's stacked copies are still the only rim.");

                report.Append(underlaid.rim > plain.rim + 50
                    ? "\n  OK UiType.ApplyShadow's underlay draws — it enables UNDERLAY_ON, which "
                      + "is the same opt-in one step over, and it has been working all along."
                    : "\n  FAIL UiType.ApplyShadow's underlay draws nothing.");
            }
            catch (System.Exception e)
            {
                report.Append("\n  probe threw: ").Append(e);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(textObject);

                // Drawing the probe glyph re-expanded the asset's atlas from the 1x1 page it is
                // committed with to a working 1024x1024, and left the glyph in it. Left alone,
                // the next save would write ~2 MB of hex-encoded page into a file that is
                // otherwise 7 KB — a diagnostic is not allowed to cost the repository that.
                ClearWorkingAtlas(face);
                AssetDatabase.SaveAssets();
            }

            Debug.Log(report.ToString());
        }

        /// <summary>Runs the shipped shadow path against the probe label, in the shipped colour.</summary>
        private static void ApplyShadowForProbe(TMP_Text label)
        {
            UiType.ApplyShadow(label, Color.red, offsetX: 1f, offsetY: -1f, softness: 0f, dilate: 0.3f);
        }

        /// <summary>How much white and how much red came back in one frame.</summary>
        private struct Shot
        {
            public int face;
            public int rim;
        }

        /// <summary>
        /// Photographs the label and counts white pixels and red pixels separately.
        ///
        /// Red is the rim colour in every pass above and white is the face, against a black
        /// ground, so the two counts cannot contaminate each other. The face count is what
        /// separates "the outline did not draw" from "nothing drew".
        /// </summary>
        private static Shot Photograph(TMP_Text label, Camera camera)
        {
            label.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);

            const int size = 256;
            var target = RenderTexture.GetTemporary(size, size, 24);
            var previous = RenderTexture.active;
            var readback = new Texture2D(size, size, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                camera.targetTexture = null;

                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
                readback.Apply();

                var pixels = readback.GetPixels32();
                var shot = new Shot();
                for (var i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    if (p.r > 90 && p.g < 70 && p.b < 70) shot.rim++;
                    else if (p.r > 150 && p.g > 150 && p.b > 150) shot.face++;
                }
                return shot;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(readback);
            }
        }

        private static void ClearWorkingAtlas(TMP_FontAsset asset)
        {
            if (asset == null) return;
            asset.ClearFontAssetData(setAtlasSizeToZero: true);
            EditorUtility.SetDirty(asset);
        }
    }
}
