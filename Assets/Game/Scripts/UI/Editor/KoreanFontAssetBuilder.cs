using System.IO;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PokeLab.UI.Editor
{
    /// <summary>
    /// Builds the committed Korean font asset from the committed TTF.
    ///
    /// The font this generates is the only thing standing between a WebGL build and a screen
    /// of empty boxes. <see cref="UiType.EnsureFont"/> used to ask the operating system for
    /// "Malgun Gothic" through <c>TMP_FontAsset.CreateFontAsset(family, style)</c>; a browser
    /// has no OS font list, so every family returned null and TextMesh Pro fell back to
    /// Liberation Sans, which is Latin-only. Every Hangul character in the game — the 107
    /// authored dialogue lines, the battle log, the menus — drew as a missing-glyph box, with
    /// the layout still correct so nothing looked wrong until you read it.
    ///
    /// Source of the face: Nanum Gothic Regular, from the Google Fonts repository at
    /// https://github.com/google/fonts/blob/main/ofl/nanumgothic/NanumGothic-Regular.ttf,
    /// fetched 2026-08-17. SIL Open Font License 1.1; the licence text travels with it in
    /// Assets/Game/Art/Fonts/OFL.txt, which is what the OFL requires of a redistribution.
    /// Malgun Gothic was deliberately not used: it is a licensed Microsoft face and this
    /// repository is pushed to GitHub.
    ///
    /// Generating rather than committing an opaque blob means the asset can be rebuilt from
    /// the TTF after a TMP upgrade, and means the atlas parameters below are reviewable.
    /// </summary>
    public static class KoreanFontAssetBuilder
    {
        /// <summary>Committed source face. Not under Resources — the font asset references it.</summary>
        public const string SourceTtfPath = "Assets/Game/Art/Fonts/NanumGothic-Regular.ttf";

        /// <summary>
        /// Where the generated asset lands. The "Resources" segment is load-bearing: a player
        /// has no AssetDatabase, so <see cref="Resources.Load"/> is the only lookup that
        /// survives a build, and it only sees what is under a Resources folder.
        /// </summary>
        public const string FontAssetPath = "Assets/Game/Art/Fonts/Resources/" + UiType.KoreanFontResourcePath + ".asset";

        /// <summary>
        /// Sampling size and padding for the signed-distance field.
        ///
        /// TMP's default is 90/9, which is wasteful here. A Hangul syllable fills its em box —
        /// three letters stacked into one glyph — so at 90pt each one claims roughly a
        /// 108x108 cell and only ~90 fit a 1024 atlas page. The game's authored text uses 728
        /// distinct syllables, so the default would spend eight 1 MB atlas pages of runtime
        /// texture before the player had read everything.
        ///
        /// 48pt sampling puts a syllable in a ~63px cell, ~256 to a page, so the whole
        /// authored set costs about three pages. The quality floor is set by the largest role
        /// that ever renders Korean — Title at 50pt — which is effectively 1:1 here; Metric
        /// and Numeric go larger but only ever draw digits, whose simple shapes survive the
        /// 2x SDF upscale. Padding is held at ~10% of the sampling size, matching Liberation
        /// Sans SDF's 9/90, because <see cref="UiType.ApplyShadow"/>'s underlay offsets are
        /// expressed against that ratio and would change weight if it moved.
        /// </summary>
        private const int SamplingPointSize = 48;
        private const int AtlasPadding = 5;
        private const int AtlasDimension = 1024;

        /// <summary>A real authored line, used to prove the face actually carries Hangul.</summary>
        private const string ProbeLine = "안 오면 벌금 100만원!";

        /// <summary>
        /// Regenerates the asset when it is missing.
        ///
        /// A clone that skipped the .asset would compile clean, run clean in the editor — the
        /// OS fallback in <see cref="UiType"/> covers Windows — and ship a WebGL build with no
        /// Korean in it. That is the exact silent failure this whole change exists to remove,
        /// so the absence is repaired rather than reported.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void EnsureBuiltOnLoad()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath) != null) return;
                if (AssetDatabase.LoadAssetAtPath<Font>(SourceTtfPath) == null) return;
                Build();
            };
        }

        [MenuItem("PokeLab/UI/Rebuild Korean Font Asset")]
        public static void Build()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(SourceTtfPath);
            if (font == null)
            {
                Debug.LogError("[KoreanFont] Source face missing at " + SourceTtfPath
                               + ". Without it a build renders every Korean line as boxes.");
                return;
            }

            FontEngine.InitializeFontEngine();

            // Dynamic, not static. Two reasons, and the first is decisive: the name prompt
            // (NameEntryPresenter) accepts free text, and Josa then splices that name into
            // sentences, so the set of syllables the game can be asked to draw is not the set
            // it was authored with — a static atlas cut to the authored 728 would render the
            // player's own name as boxes, which is this same bug wearing a smaller mask.
            // Second, a static atlas covering those 728 needs three 1024x1024 Alpha8 pages
            // baked into the .asset. Measured: one such page costs 2.1 MB of hex-encoded YAML,
            // so ~6 MB committed and shipped, against 1.96 MB for the TTF that covers all
            // 11,172 syllables. Dynamic rasterises on demand from that TTF; the cost is a
            // one-frame hitch the first time a glyph appears.
            var asset = TMP_FontAsset.CreateFontAsset(
                font, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
                AtlasDimension, AtlasDimension, AtlasPopulationMode.Dynamic,
                // Multi-atlas on: without it the first page fills and every later glyph fails
                // silently to a box, reintroducing the failure partway through the game.
                enableMultiAtlasSupport: true);

            if (asset == null)
            {
                Debug.LogError("[KoreanFont] TMP could not load a face from " + SourceTtfPath
                               + ". Check that Include Font Data is enabled on the importer.");
                return;
            }

            var directory = Path.GetDirectoryName(FontAssetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            AssetDatabase.DeleteAsset(FontAssetPath);
            AssetDatabase.CreateAsset(asset, FontAssetPath);

            // The atlas texture and material are created in memory by CreateFontAsset; without
            // this they are not serialised and the asset reloads with a null material.
            var assetName = Path.GetFileNameWithoutExtension(FontAssetPath);
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

            // CreateFontAsset copies this off TMP_Settings, and the asset's own flag is
            // internal to TMP, so the project setting is the only place it can be enforced.
            // With it off, every glyph rasterised while playing in the editor is serialised
            // into the asset and then into git and the build — megabytes of baked atlas for
            // no gain, since a dynamic asset re-rasterises them anyway.
            if (!TMP_Settings.clearDynamicDataOnBuild)
                Debug.LogWarning("[KoreanFont] TMP Settings > Clear Dynamic Data On Build is off, "
                                 + "so runtime-rasterised glyphs will be committed into "
                                 + FontAssetPath + " and shipped in the build.");

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(FontAssetPath, ImportAssetOptions.ForceUpdate);

            RegisterAsGlobalFallback(asset);

            Debug.Log("[KoreanFont] Built " + FontAssetPath + " from " + SourceTtfPath
                      + " (dynamic, " + SamplingPointSize + "pt sampling, "
                      + AtlasDimension + "x" + AtlasDimension + " pages).");

            Verify();
        }

        /// <summary>
        /// Puts the asset in TMP's global fallback list.
        ///
        /// <see cref="UiType.Apply"/> is the seam every label is meant to pass through, but
        /// not every label does — NameEntryPresenter builds its input field's text and
        /// placeholder with a bare AddComponent, so they take TMP's default asset, Liberation
        /// Sans, and drew the prefilled Korean name as boxes. A global fallback covers those
        /// and anything written later that forgets the seam.
        ///
        /// Done in code rather than by hand-editing TMP Settings because rebuilding the asset
        /// gives it a new GUID, which would leave a hand-written reference silently pointing
        /// at nothing — the same invisible breakage in a new place.
        /// </summary>
        private static void RegisterAsGlobalFallback(TMP_FontAsset asset)
        {
            var fallbacks = TMP_Settings.fallbackFontAssets ?? new System.Collections.Generic.List<TMP_FontAsset>();

            // Drop nulls left by an earlier rebuild's discarded GUID, and any stale copy of
            // this same asset, before putting the current one back at the head of the list.
            fallbacks.RemoveAll(f => f == null || f.name == asset.name);
            fallbacks.Insert(0, asset);

            TMP_Settings.fallbackFontAssets = fallbacks;
            EditorUtility.SetDirty(TMP_Settings.instance);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Proves the asset can actually rasterise Hangul, rather than trusting a filename.
        ///
        /// This drives the real runtime path — face load, FreeType raster, atlas packing —
        /// via TryAddCharacters, so a face that merely claims Korean in its name, or a TTF
        /// that imported without font data, fails here instead of in the browser.
        /// </summary>
        [MenuItem("PokeLab/UI/Verify Korean Font Asset")]
        public static void Verify()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (asset == null)
            {
                Debug.LogError("[KoreanFont] No font asset at " + FontAssetPath + ".");
                return;
            }

            // The asset existing at a path proves nothing about a build. Resources.Load is the
            // lookup UiType uses and the only one a player has, so it is checked here rather
            // than discovered as boxes on a web page.
            var viaResources = Resources.Load<TMP_FontAsset>(UiType.KoreanFontResourcePath);
            var resolved = UiType.EnsureFont();

            var report = new StringBuilder();
            report.Append("[KoreanFont] Resources.Load(\"").Append(UiType.KoreanFontResourcePath)
                  .Append("\") -> ").Append(viaResources != null ? viaResources.name : "NULL")
                  .Append("; UiType.EnsureFont() -> ")
                  .Append(resolved != null ? resolved.name : "NULL")
                  .Append('\n');
            report.Append("[KoreanFont] ").Append(asset.name)
                  .Append(" face=").Append(asset.faceInfo.familyName)
                  .Append(' ').Append(asset.faceInfo.styleName)
                  .Append(" mode=").Append(asset.atlasPopulationMode)
                  .Append(" sourceFontFile=")
                  .Append(asset.sourceFontFile != null ? asset.sourceFontFile.name : "NULL (build will have no Korean)");

            if (!asset.TryAddCharacters(ProbeLine, out var missingFromLine))
                report.Append("\n  FAIL probe line \"").Append(ProbeLine)
                      .Append("\" is missing: ").Append(missingFromLine);
            else
                report.Append("\n  OK probe line \"").Append(ProbeLine).Append("\" rasterised, ")
                      .Append(asset.characterTable.Count).Append(" glyphs now in the atlas.");

            // A spot check across the block, since the probe line only exercises 9 syllables.
            var sweep = new StringBuilder();
            for (var c = 0xAC00; c <= 0xD7A3; c += 137) sweep.Append((char)c);
            if (!asset.TryAddCharacters(sweep.ToString(), out var missingFromSweep))
                report.Append("\n  FAIL Hangul block sweep missing: ").Append(missingFromSweep);
            else
                report.Append("\n  OK Hangul block sweep (").Append(sweep.Length)
                      .Append(" syllables spread over U+AC00..U+D7A3) all resolved across ")
                      .Append(asset.atlasTextures.Length).Append(" atlas page(s).");

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
                report.Append("\n  WARN not in TMP Settings > Fallback Font Assets; any label "
                              + "built outside UiType.Apply will still render Korean as boxes.");
            else if (TMP_Settings.defaultFontAsset != null
                     && TMP_Settings.defaultFontAsset.HasCharacters(ProbeLine, out uint[] _,
                         searchFallbacks: true, tryAddCharacter: true))
                report.Append("\n  OK probe line also resolves from the default font asset (")
                      .Append(TMP_Settings.defaultFontAsset.name)
                      .Append(") through the global fallback.");
            else
                report.Append("\n  FAIL registered as a global fallback but the default font "
                              + "asset still cannot resolve the probe line.");

            Debug.Log(report.ToString());

            // Verification must not leave its glyphs in the committed asset, and the atlas
            // must go back to 1x1 rather than stay at the working 1024x1024: an Alpha8 page
            // that size hex-encodes to ~2 MB of YAML, which would be committed to git and
            // shipped inside the build for nothing. TryAddCharacterInternal re-expands a 1x1
            // page to atlasWidth/atlasHeight on the first glyph it rasterises at runtime.
            asset.ClearFontAssetData(setAtlasSizeToZero: true);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }
    }
}
