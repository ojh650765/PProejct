using System.IO;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Cinematics.EditorTools
{
    /// <summary>
    /// Builds the <see cref="StarterBallKit"/> asset that lets the starter case reach the
    /// capture-ball models at runtime.
    ///
    /// The models are ordinary FBX files in <c>Assets/Game/Art/Props</c>. Nothing outside
    /// Resources or Addressables survives into a build, and the component that needs them is
    /// added to the scene by the rig setup with <c>AddComponent</c>, so there is no inspector
    /// anywhere to drag a reference into. One serialized asset inside a Resources folder
    /// solves both at once.
    ///
    /// It builds itself on editor load when the asset is missing rather than waiting for
    /// somebody to find the menu item: the scene is regenerated constantly, and a beat that
    /// silently degrades to grey spheres because nobody clicked a menu is the kind of failure
    /// that gets reported as "the balls look wrong" three rounds later.
    /// </summary>
    public static class StarterBallKitBuilder
    {
        private const string KitDirectory = "Assets/Game/Data/Starter/Resources";
        private const string KitPath = KitDirectory + "/" + StarterBallKit.ResourceName + ".asset";

        private const string ClosedModel = "Assets/Game/Art/Props/Env_Prop_CaptureBall.fbx";
        private const string OpenModel = "Assets/Game/Art/Props/Env_Prop_CaptureBall_Open.fbx";

        [MenuItem("Tools/Poké Lab/Art/Rebuild Starter Ball Kit")]
        public static void Rebuild() => Build(true);

        [InitializeOnLoadMethod]
        private static void EnsureOnLoad()
        {
            // Deferred: this runs during the editor's own load, when the asset database is
            // still being brought up and writing to it either throws or is discarded.
            EditorApplication.delayCall += () => Build(false);
        }

        private static void Build(bool verbose)
        {
            var kit = AssetDatabase.LoadAssetAtPath<StarterBallKit>(KitPath);
            if (kit != null && !verbose && kit.Closed != null && kit.Open != null) return;

            var closed = AssetDatabase.LoadAssetAtPath<GameObject>(ClosedModel);
            var open = AssetDatabase.LoadAssetAtPath<GameObject>(OpenModel);

            if (closed == null)
            {
                Debug.LogWarning($"[Starter] '{ClosedModel}' is missing, so the case has no " +
                                 "balls to hold. The environment kit builds it; until it is " +
                                 "back the stage draws primitive spheres.");
                return;
            }

            if (kit == null)
            {
                Directory.CreateDirectory(KitDirectory);
                AssetDatabase.Refresh();
                kit = ScriptableObject.CreateInstance<StarterBallKit>();
                AssetDatabase.CreateAsset(kit, KitPath);
            }

            kit.Closed = closed;
            // The open livery is optional in the sense that the beat survives without it: the
            // ball simply stays shut while the creature comes out of it, which reads as a bug
            // rather than as missing art, so it is worth saying so out loud.
            kit.Open = open;
            if (open == null)
                Debug.LogWarning($"[Starter] '{OpenModel}' is missing, so the chosen ball will " +
                                 "not visibly open on the reveal.");

            EditorUtility.SetDirty(kit);
            AssetDatabase.SaveAssets();

            if (verbose) Debug.Log($"[Starter] Ball kit written to {KitPath}.");
        }
    }
}
