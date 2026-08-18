using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Executes a named menu item from a request file, the way
    /// <see cref="LevelWalkthroughCapture"/> renders frames from one.
    ///
    /// Same reasoning as that tool: an integration sweep has to be scriptable end to end,
    /// and a menu item is the one shape every builder in this project already has. The
    /// request lives in Temp/ deliberately — a file dropped inside Assets/ would start an
    /// import, reload the domain and cancel the very request being served.
    ///
    /// The request file is one menu path per line. Each line is executed in order; the
    /// result file records what ran and what failed. As with the capture tool, Unity ticks
    /// the editor update loop slowly when its window is not focused — if a request seems to
    /// hang, click the Unity window once.
    /// </summary>
    [InitializeOnLoad]
    public static class MenuRequestRunner
    {
        private static readonly string RequestPath =
            Path.Combine(Directory.GetCurrentDirectory(), "Temp", "pokelab_menu.request");
        private static readonly string ResultPath =
            Path.Combine(Directory.GetCurrentDirectory(), "Temp", "pokelab_menu.result");

        static MenuRequestRunner()
        {
            EditorApplication.update += Poll;
        }

        /// <summary>Which request the pipeline was already asked to catch up for — survives
        /// the domain reload a recompile causes, which a static field would not.</summary>
        private const string RefreshedKey = "PokeLab.MenuRunner.RefreshedFor";

        private static void Poll()
        {
            if (!File.Exists(RequestPath)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            // Serve the request with current code, not with whatever was compiled last —
            // the same discipline the capture tool documents.
            var stamp = File.GetLastWriteTimeUtc(RequestPath).Ticks.ToString();
            if (SessionState.GetString(RefreshedKey, "") != stamp)
            {
                SessionState.SetString(RefreshedKey, stamp);
                AssetDatabase.Refresh(ImportAssetOptions.Default);
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(RequestPath);
            }
            catch (IOException)
            {
                return; // Still being written; next tick.
            }

            File.Delete(RequestPath);

            var report = new System.Text.StringBuilder();
            foreach (var raw in lines)
            {
                var menuPath = raw.Trim();
                if (menuPath.Length == 0 || menuPath.StartsWith("#")) continue;

                try
                {
                    var ran = EditorApplication.ExecuteMenuItem(menuPath);
                    report.AppendLine((ran ? "ok  " : "MISS") + "\t" + menuPath);
                    if (!ran)
                        Debug.LogWarning($"[MenuRunner] No menu item at '{menuPath}'.");
                }
                catch (Exception e)
                {
                    report.AppendLine("FAIL\t" + menuPath + "\t" + e.Message);
                    Debug.LogError($"[MenuRunner] '{menuPath}' threw: {e}");
                }
            }

            File.WriteAllText(ResultPath, report.ToString());
        }
    }
}
