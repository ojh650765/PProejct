using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// One place to rebuild the level and the art without leaving Unity.
    ///
    /// The level is generated, not authored in the scene: <c>build_layout.py</c> designs
    /// it, <c>audit_placement.py</c> seats everything against the terrain, and
    /// <c>emit_unity_layout.py</c> flattens the result into meshes. Running those by hand
    /// in the right order and then remembering to rebuild the scene is the kind of thing
    /// that gets half-done, and a half-done regeneration looks exactly like a bug in
    /// whatever you were actually working on.
    ///
    /// Each step reports what the script printed rather than swallowing it, because the
    /// counts are the only evidence the pass did anything.
    /// </summary>
    public static class PokeLabRebuildMenu
    {
        private const string Root = "Tools/Poké Lab/Rebuild/";

        [MenuItem(Root + "Everything (layout, audit, emit, scene)", priority = 0)]
        public static void RebuildEverything()
        {
            if (!RunLayout()) return;
            if (!RunAudit()) return;
            if (!RunEmit()) return;
            LevelLayoutBuilder.Build();
        }

        [MenuItem(Root + "Level Scene Only (from the emitted layout)", priority = 20)]
        public static void RebuildScene() => LevelLayoutBuilder.Build();

        [MenuItem(Root + "Regenerate Layout Data (design + audit + emit)", priority = 21)]
        public static void RebuildData()
        {
            if (!RunLayout()) return;
            if (!RunAudit()) return;
            RunEmit();
        }

        [MenuItem(Root + "Reimport All Art", priority = 40)]
        public static void ReimportArt() => CreatureImportSettings.ReimportAll();

        [MenuItem(Root + "Rebuild Asset Bounds (after art changes)", priority = 41)]
        public static void RebuildBounds()
        {
            // Written to a temp file first: the script prints the JSON on stdout, and
            // redirecting straight over the real file would truncate it if the run failed.
            var target = Path.Combine(ProjectRoot, "Tools", "Level", "asset_bounds.json");
            if (!RunPython("Tools/Level/fbx_bounds.py", out var stdout, out _)) return;
            if (stdout.Length < 64)
            {
                Debug.LogError("[Rebuild] fbx_bounds produced almost nothing; asset_bounds.json left alone.");
                return;
            }
            File.WriteAllText(target, stdout);
            Debug.Log($"[Rebuild] asset_bounds.json rewritten ({stdout.Length} bytes). " +
                      "Regenerate the layout next — placement reads these dimensions.");
        }

        // --- steps ------------------------------------------------------------------

        private static bool RunLayout() =>
            Step("design", "Tools/Level/build_layout.py");

        private static bool RunEmit() =>
            Step("emit", "Tools/Level/emit_unity_layout.py");

        /// <summary>
        /// Seats everything against the terrain the design just produced.
        ///
        /// Run repeatedly on purpose. Separating two overlapping props can push one into
        /// a third, and lowering an object onto a slope changes what is under its
        /// neighbours, so a single pass converges only in the easy cases.
        /// </summary>
        private static bool RunAudit()
        {
            for (var pass = 0; pass < 3; pass++)
                if (!Step($"audit pass {pass + 1}", "Tools/Level/audit_placement.py",
                          "--gap 0.06 --fix"))
                    return false;
            return true;
        }

        private static bool Step(string label, string script, string args = "")
        {
            EditorUtility.DisplayProgressBar("Rebuilding", label, 0.5f);
            try
            {
                if (!RunPython(script + (args.Length > 0 ? " " + args : ""), out var stdout, out var stderr))
                {
                    Debug.LogError($"[Rebuild] {label} failed:\n{stderr}\n{stdout}");
                    return false;
                }
                Debug.Log($"[Rebuild] {label}:\n{Tail(stdout, 24)}");
                return true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)!.FullName;

        private static bool RunPython(string scriptAndArgs, out string stdout, out string stderr)
        {
            var info = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = scriptAndArgs,
                WorkingDirectory = ProjectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                using var process = Process.Start(info);
                if (process == null)
                {
                    stdout = stderr = "could not start python";
                    return false;
                }
                stdout = process.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch (Exception e)
            {
                stdout = "";
                stderr = $"{e.GetType().Name}: {e.Message}. Is python on PATH?";
                return false;
            }
        }

        private static string Tail(string text, int lines)
        {
            var all = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            var from = Mathf.Max(0, all.Length - lines);
            return string.Join("\n", all, from, all.Length - from);
        }
    }
}
