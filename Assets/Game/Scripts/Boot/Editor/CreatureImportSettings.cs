using System;
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

        /// <summary>Clips that must loop. Everything else is a one-shot.</summary>
        private static readonly string[] LoopingClips =
        {
            "Idle", "IdleBattle", "Walk", "Run", "Sleep",
        };

        private static bool IsGameArt(string path) =>
            path.StartsWith(CreatureRoot, StringComparison.Ordinal)
            || path.StartsWith(EnvironmentRoot, StringComparison.Ordinal)
            || path.StartsWith(PropRoot, StringComparison.Ordinal);

        private void OnPreprocessModel()
        {
            if (!IsGameArt(assetPath)) return;
            var importer = (ModelImporter)assetImporter;

            // Unit handling differs by family because the two pipelines export differently,
            // and getting it wrong is invisible until something is measured: the environment
            // FBX were arriving at 1/100, so a cottage was 7 cm tall and simply vanished
            // against the ground rather than looking wrong.
            //
            // Creatures are authored in true metres and want the file taken verbatim.
            // Environment and props declare their units in the file, so Unity must honour that
            // declaration and convert.
            // The environment FBX are authored at 1/100 — a cottage arrives 4.9 cm tall, which
            // does not look wrong so much as invisible: it vanishes into the ground and the
            // level reads as empty. The export is what is actually wrong and is being fixed at
            // source, but compensating here means the existing 89 assets are usable now and
            // stay usable either way, because a corrected export will declare its units and
            // useFileUnits will then do the work instead.
            var isCreatureArt = assetPath.StartsWith(CreatureRoot, StringComparison.Ordinal);
            importer.globalScale = isCreatureArt ? 1f : 100f;
            importer.useFileUnits = false;

            // The environment export writes Z-up without declaring the conversion, so the
            // models arrive lying on their backs — a cottage reads as a slab on the ground.
            // Baking the axis conversion at import rotates the mesh data itself rather than
            // leaving a -90 degree rotation on every instance, which would fight every
            // authored rotation in the layout.
            importer.bakeAxisConversion = !isCreatureArt;

            // Custom split normals are exported; recalculating them would flatten the
            // smooth-with-sharp-edges shading the models were authored for.
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.importBlendShapes = true;
            importer.weldVertices = false;

            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.External;

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

        private void OnPostprocessModel(GameObject root)
        {
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
            var roots = new[] { CreatureRoot, EnvironmentRoot, PropRoot };
            var reimported = 0;

            foreach (var root in roots)
            {
                if (!AssetDatabase.IsValidFolder(root.TrimEnd('/'))) continue;
                foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { root.TrimEnd('/') }))
                {
                    AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid),
                        ImportAssetOptions.ForceUpdate);
                    reimported++;
                }
            }

            AssetDatabase.Refresh();
            Debug.Log($"[Art] Reimported {reimported} model(s).");
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

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
        }
    }
}
