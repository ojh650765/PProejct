using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Applies the import settings the creature FBX files require, automatically and on every
    /// reimport.
    ///
    /// This is a postprocessor rather than a one-off pass because the art is regenerated:
    /// the Blender pipeline rebuilds any subset on demand, and hand-set importer values would
    /// be silently lost each time. Getting these wrong is not a visible error — it is a model
    /// that arrives at 1/100 scale, or one whose anchor empties have been optimised away, which
    /// then fails much later as "the hit VFX spawns at the origin".
    /// </summary>
    public sealed class CreatureImportSettings : AssetPostprocessor
    {
        private const string CreatureRoot = "Assets/Game/Art/Creatures/";
        private const string EnvironmentRoot = "Assets/Game/Art/Environment/";
        private const string PropRoot = "Assets/Game/Art/Props/";
        private const string FoliageRoot = "Assets/Game/Art/Environment/Foliage/";
        private const string TownRoot = "Assets/Game/Art/Environment/Town/";
        private const string TerrainRoot = "Assets/Game/Art/Environment/Terrain/";
        private const string SpriteRoot = "Assets/Game/Art/Sprites/";

        /// <summary>Portraits and their backdrops: loaded as Sprites, not sliced in code.</summary>
        private const string PortraitRoot = "Assets/Game/Art/Sprites/Resources/Portraits/";

        /// <summary>
        /// Which hand-authored material each art family is bound to.
        ///
        /// Every one of these families was arriving on URP/Lit, because that is what the
        /// FBX importer generates and nothing overrode it. The project has four shaders
        /// written and verified for exactly this art — and not one of them was reaching
        /// the meshes. The visible cost was not subtle: props and rocks rendered almost
        /// black next to a bright ground, because our shaders read the ambient the
        /// LightingDirector publishes in <c>_PL_Ambient*</c> while URP/Lit reads Unity's
        /// own, which this project never populates.
        /// </summary>
        private static readonly (string Root, string Material)[] MaterialBindings =
        {
            (FoliageRoot, "Assets/Game/Art/Environment/Foliage/Materials/M_Env_Foliage.mat"),
            (TownRoot, "Assets/Game/Art/Environment/Town/Materials/M_Env_Town.mat"),
            (TerrainRoot, "Assets/Game/Art/Environment/Terrain/Materials/M_Env_Terrain.mat"),
            (PropRoot, "Assets/Game/Art/Props/Materials/M_Env_Props.mat"),
        };

        /// <summary>
        /// Materials named by an asset's own manifest entry, keyed by the shader it says it
        /// was authored for.
        ///
        /// A folder is not a material family. Assets/Game/Art/Environment/Terrain holds two
        /// kinds of asset: rocks and cliffs UV-packed into a sixteen-cell atlas, and the
        /// ground, ramp and ledge modules, which carry no atlas UV at all because their
        /// vertex colours are TerrainBlend's layer weights. Binding the folder put the
        /// second kind on the atlas material, and a mesh whose UVs span 0-1 against a 4x4
        /// atlas draws all sixteen cells — which is exactly the checkerboard that appeared
        /// on every ledge, ramp and ground module in the level.
        /// </summary>
        private static readonly (string Shader, string Material)[] ShaderBindings =
        {
            ("PokeLab/TerrainBlend",
             "Assets/Game/Art/Environment/Terrain/Materials/M_Ground_TerrainBlend.mat"),
            ("PokeLab/Water",
             "Assets/Game/Shaders/Materials/M_Water_Lake.mat"),
        };

        private const string ManifestPath =
            "Assets/Game/Art/Environment/environment_manifest.json";

        private static Dictionary<string, string> s_manifestShaders;

        /// <summary>
        /// Asset path to the shader its manifest entry declares. Built once and cached,
        /// because a full art reimport asks this question 134 times.
        /// </summary>
        private static Dictionary<string, string> ManifestShaders()
        {
            if (s_manifestShaders != null) return s_manifestShaders;

            s_manifestShaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
            if (text == null) return s_manifestShaders;

            var manifest = JsonUtility.FromJson<EnvironmentManifest>(text.text);
            foreach (var entry in manifest?.assets ?? Array.Empty<ManifestAsset>())
            {
                if (string.IsNullOrEmpty(entry?.path) || string.IsNullOrEmpty(entry.unityMaterial))
                    continue;
                s_manifestShaders[entry.path] = entry.unityMaterial;
            }
            return s_manifestShaders;
        }

        [Serializable] private sealed class EnvironmentManifest { public ManifestAsset[] assets; }

        [Serializable]
        private sealed class ManifestAsset
        {
            public string path;
            public string unityMaterial;
        }

        private static string BindingFor(string path)
        {
            // What the asset says it is beats where it happens to live.
            if (ManifestShaders().TryGetValue(path, out var declared) && declared != null)
            {
                foreach (var (shader, material) in ShaderBindings)
                    if (declared.StartsWith(shader, StringComparison.Ordinal))
                        return material;
            }

            foreach (var (root, material) in MaterialBindings)
                if (path.StartsWith(root, StringComparison.Ordinal))
                    return material;
            return null;
        }

        /// <summary>Clips that must loop. Everything else is a one-shot.</summary>
        private static readonly string[] LoopingClips =
        {
            "Idle", "IdleBattle", "Walk", "Run", "Sleep",
        };

        private static bool IsGameArt(string path) =>
            path.StartsWith(CreatureRoot, StringComparison.Ordinal)
            || path.StartsWith(EnvironmentRoot, StringComparison.Ordinal)
            || path.StartsWith(PropRoot, StringComparison.Ordinal)
            || path.StartsWith(SpriteRoot, StringComparison.Ordinal);

        private void OnPreprocessModel()
        {
            if (!IsGameArt(assetPath)) return;
            var importer = (ModelImporter)assetImporter;

            // Both pipelines now export real metres with a declared axis convention, so
            // Unity's own unit conversion is correct and no compensation is needed. This
            // briefly carried globalScale = 100 and a baked axis conversion for the
            // environment art while its export was wrong; leaving those in place after the
            // export was fixed would correct the same error twice.
            importer.globalScale = 1f;
            importer.useFileUnits = true;
            importer.bakeAxisConversion = false;

            // Custom split normals are exported; recalculating them would flatten the
            // smooth-with-sharp-edges shading the models were authored for.
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.importBlendShapes = true;
            importer.weldVertices = false;

            if (BindingFor(assetPath) != null)
            {
                // Bound families get one shared hand-authored material in
                // OnPostprocessModel. Importing materials as well would keep
                // regenerating the URP/Lit asset that culled the back of every grass
                // clump, and leave it in the project as a second, wrong answer to the
                // question of what shades this art.
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
            }
            else
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                importer.materialLocation = ModelImporterMaterialLocation.External;
            }

            var isCreature = assetPath.StartsWith(CreatureRoot, StringComparison.Ordinal);
            var isLod = assetPath.EndsWith("_LOD1.fbx", StringComparison.OrdinalIgnoreCase)
                        || assetPath.EndsWith("_LOD2.fbx", StringComparison.OrdinalIgnoreCase);

            if (isCreature && !isLod)
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.motionNodeName = "Root";
                importer.importAnimation = true;
                importer.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;

                // Must stay off. Optimising the hierarchy strips the Anchor_Head/Body/Muzzle
                // empties, and every battle camera and hit effect resolves through those.
                importer.optimizeGameObjects = false;
            }
            else
            {
                // LODs and scenery carry no rig; importing one just adds an Animator to skin.
                importer.animationType = ModelImporterAnimationType.None;
                importer.importAnimation = false;
            }
        }

        /// <summary>
        /// Binds an art family to its hand-authored material instead of the one Unity
        /// generates from the FBX.
        ///
        /// For foliage this is not a preference. Every grass blade in the kit is a single
        /// sided ribbon — six triangles instead of twelve — on the explicit understanding
        /// that the shader is <c>Cull Off</c> (see the comment in
        /// <c>Tools/Blender/environment/gen_foliage.py</c>, `tall_grass`). Unity's imported
        /// material is URP/Lit with <c>_Cull = Back</c>, so half of every clump vanished
        /// when the camera crossed to the far side of it. Nothing about that is visible in
        /// a mesh inspection: the geometry is exactly as designed, and the material is the
        /// thing that is wrong.
        ///
        /// Assignment happens here rather than through the importer's own material slots
        /// so it does not depend on the material's name inside the FBX. Routing the whole
        /// family to one shared material also collapses it to a single draw call per
        /// instanced batch, which is what makes dense grass affordable.
        /// </summary>
        private void AssignBoundMaterial(GameObject root, string materialPath)
        {
            var shared = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (shared == null)
            {
                Debug.LogWarning(
                    $"[EnvImport] {materialPath} is missing, so " +
                    $"{System.IO.Path.GetFileName(assetPath)} has no material at all. " +
                    "It will render magenta rather than fall back to URP/Lit, which is " +
                    "deliberate — a silent fallback is what hid this for so long.");
                return;
            }

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var slots = renderer.sharedMaterials;
                for (var i = 0; i < slots.Length; i++) slots[i] = shared;
                renderer.sharedMaterials = slots;
            }
        }

        private void OnPostprocessModel(GameObject root)
        {
            var binding = BindingFor(assetPath);
            if (binding != null) AssignBoundMaterial(root, binding);

            if (!assetPath.StartsWith(CreatureRoot, StringComparison.Ordinal)) return;
            if (assetPath.EndsWith("_LOD1.fbx", StringComparison.OrdinalIgnoreCase)) return;

            var importer = (ModelImporter)assetImporter;
            var clips = importer.defaultClipAnimations;
            if (clips == null || clips.Length == 0) return;

            for (var i = 0; i < clips.Length; i++)
            {
                var shouldLoop = LoopingClips.Contains(clips[i].name);
                clips[i].loopTime = shouldLoop;
                clips[i].loopPose = shouldLoop;
            }
            importer.clipAnimations = clips;

            WarnAboutMissingAnchors(root);
        }

        /// <summary>
        /// The anchors are a contract, not a nicety: health bars, hit VFX and projectile
        /// origins all attach to them. A missing one produces effects at the model origin,
        /// which reads as a bug in the VFX layer rather than in the art, so say it loudly here.
        /// </summary>
        private void WarnAboutMissingAnchors(GameObject root)
        {
            foreach (var required in new[] { "Anchor_Head", "Anchor_Body", "Anchor_Muzzle" })
            {
                var found = root.GetComponentsInChildren<Transform>(true)
                    .Any(t => t.name == required);
                if (!found)
                {
                    Debug.LogWarning(
                        $"[CreatureImport] {System.IO.Path.GetFileName(assetPath)} is missing " +
                        $"'{required}'. Battle framing and impact effects will fall back to the " +
                        "model origin. Regenerate it from Tools/Blender.");
                }
            }
        }

        /// <summary>
        /// Forces every art asset through the importer again.
        ///
        /// Touching files is not enough: Unity keys reimport on a content hash, so changing
        /// only the postprocessor leaves existing assets on whatever settings they were first
        /// imported with. That is silent — the meta keeps the old values and nothing warns.
        /// </summary>
        [MenuItem("Tools/Poké Lab/Art/Reimport All Art")]
        public static void ReimportAll()
        {
            var roots = new[] { CreatureRoot, EnvironmentRoot, PropRoot, SpriteRoot };
            var reimported = 0;

            foreach (var root in roots)
            {
                if (!AssetDatabase.IsValidFolder(root.TrimEnd('/'))) continue;
                // Textures as well as models. This only reimported t:Model, so every rule in
                // OnPreprocessTexture — including the pixel-art rules that decide whether the
                // creature sheets are blurry — could be changed and then never applied to
                // anything, because touching the postprocessor does not change a texture's
                // content hash.
                foreach (var guid in AssetDatabase.FindAssets("t:Model t:Texture",
                             new[] { root.TrimEnd('/') }))
                {
                    AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid),
                        ImportAssetOptions.ForceUpdate);
                    reimported++;
                }
            }

            AssetDatabase.Refresh();
            Debug.Log($"[Art] Reimported {reimported} model(s).");
        }

        /// <summary>
        /// Forces the portraits through the importer again.
        ///
        /// Touching a file is not enough: the asset pipeline keys its cache on content and
        /// importer settings, not on the modification time, so a rule that changed in code
        /// does not reach art that has not itself changed. This is how a new rule is applied
        /// to files that already exist.
        /// </summary>
        [MenuItem("Tools/Poké Lab/Rebuild/Reimport Portraits", priority = 12)]
        public static void ReimportPortraits()
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { PortraitRoot.TrimEnd('/') });
            foreach (var guid in guids)
                AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid),
                    ImportAssetOptions.ForceUpdate);

            AssetDatabase.Refresh();
            Debug.Log($"[Art] Reimported {guids.Length} portrait(s) as sprites.");
        }

        private void OnPreprocessTexture()
        {
            if (!IsGameArt(assetPath)) return;
            var importer = (TextureImporter)assetImporter;

            if (assetPath.EndsWith("_Normal.png", StringComparison.OrdinalIgnoreCase))
            {
                importer.textureType = TextureImporterType.NormalMap;
                return;
            }

            if (assetPath.EndsWith("_Portrait.png", StringComparison.OrdinalIgnoreCase))
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                return;
            }

            // Dialogue portraits are the one thing under the sprite root that is loaded as a
            // Sprite rather than sliced out of a sheet in code.
            //
            // ApplyPixelArtRules sets textureType to Default, which is right for the walk
            // sheets — nothing loads those as sprites, they are cut up with Sprite.Create at
            // runtime — and silently wrong here: Resources.Load<Sprite> returned null for a
            // file sitting in the folder, the box fell back to the walking sprite, and a
            // hand-written .meta could not fix it because the importer rewrites the meta from
            // its own settings after every import. The rule has to change, not the file.
            if (assetPath.StartsWith(PortraitRoot, StringComparison.Ordinal))
            {
                ApplyPixelArtRules(importer);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100f;
                return;
            }

            if (assetPath.StartsWith(SpriteRoot, StringComparison.Ordinal))
            {
                ApplyPixelArtRules(importer);
                return;
            }

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;

            // Terrain layers are sampled triplanar at a grazing angle up a cliff, which is
            // the worst case for filtering: the gradient is long in one axis and near zero
            // in the other. With aniso at 1 the hardware answers a heavily anisotropic
            // footprint with an isotropic mip, and the mip steps read as horizontal bands
            // across a distant wall.
            if (assetPath.StartsWith(TerrainRoot, StringComparison.Ordinal)
                && assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                importer.anisoLevel = 8;
                importer.mipmapEnabled = true;
                importer.filterMode = FilterMode.Trilinear;
            }
        }

        /// <summary>
        /// Import rules for hand-drawn pixel sheets.
        ///
        /// This root was outside <see cref="IsGameArt"/>, so 106 of the 124 creature and
        /// person sheets shipped on Unity's defaults, and the two defaults that matter are
        /// both wrong for pixel art:
        ///
        /// <b>Mipmaps.</b> A 768x672 sheet drawn small on screen samples a downscaled mip
        /// rather than the pixels that were drawn — which is exactly "everything looks low
        /// resolution and blurry". Point filtering cannot save it, because the blur is baked
        /// into the mip before filtering happens.
        ///
        /// <b>Compression.</b> The standalone override was set to compress. Block compression
        /// on hard-edged art with flat colour fields bleeds colour across the block and
        /// stipples the outlines, which is the second half of the same complaint.
        ///
        /// Anisotropy is dropped for the same reason as mipmaps, and the sheets are read as
        /// Default rather than Sprite because they are sampled through a material by the
        /// billboards, not through the sprite renderer.
        /// </summary>
        private static void ApplyPixelArtRules(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.alphaIsTransparency = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;

            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Point;
            importer.anisoLevel = 0;
            importer.wrapMode = TextureWrapMode.Clamp;

            // Uncompressed on every platform, not just the default one. A per-platform
            // override is what was compressing these while the default said otherwise, so
            // setting the default alone would have looked fixed and changed nothing.
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            foreach (var platform in new[] { "Standalone", "WebGL", "Android", "iPhone" })
            {
                var settings = importer.GetPlatformTextureSettings(platform);
                settings.overridden = true;
                settings.textureCompression = TextureImporterCompression.Uncompressed;
                settings.format = TextureImporterFormat.RGBA32;
                settings.maxTextureSize = 2048;
                importer.SetPlatformTextureSettings(settings);
            }
        }
    }
}
