using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Imports TextMesh Pro's essential resources if the project has never had them.
    ///
    /// Without them there is no <c>TMP_Settings</c> asset and no default font, and
    /// <c>TMP_Text.SetText</c> throws a NullReferenceException on the first character of
    /// the first label it is asked to draw. That is not a dialogue problem, it is every
    /// piece of text in the game — the battle HUD, the menus, the Poké Lab readout — and
    /// it fails at the point of use rather than at startup, which is why it survived this
    /// long unnoticed behind UI that had not been exercised yet.
    ///
    /// Unity normally offers this as a modal on first use of TMP. A project that is built
    /// and reviewed from scripts never sees that dialog, so it is done here instead.
    /// </summary>
    [InitializeOnLoad]
    public static class TextMeshProBootstrap
    {
        private const string SettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

        static TextMeshProBootstrap()
        {
            // Deferred: the asset database is not necessarily ready during a static
            // constructor, and importing a package mid-reload is a good way to lose it.
            EditorApplication.delayCall += EnsureImported;
        }

        [MenuItem("Tools/Poké Lab/Setup/Import TextMesh Pro Essentials")]
        public static void EnsureImported()
        {
            if (File.Exists(SettingsPath) || TMP_Settings.instance != null) return;

            Debug.Log("[TMP] Essential resources are missing; importing them. Without " +
                      "these every TextMeshPro label in the project throws on its first " +
                      "SetText call.");
            TMP_PackageResourceImporter.ImportResources(true, false, false);
        }
    }
}
