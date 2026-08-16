using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Builds the standalone player.
    ///
    /// A player build is the only test that answers questions the editor cannot. Almost every
    /// silent failure this project has had was of the same shape — something that resolved in
    /// the editor and would not have resolved in a build: dialogue read off disk instead of
    /// out of Resources, art found by <c>AssetDatabase</c>, a scene that was never in the
    /// build settings. Running the game outside the editor is what makes those visible, and
    /// there was no way to do it.
    ///
    /// The scene list is taken from the build settings rather than restated here, because
    /// <see cref="SceneSetup.AddToBuildSettings"/> already owns that list and two copies of it
    /// would disagree the first time a scene was added.
    /// </summary>
    public static class GameBuilder
    {
        private const string OutputRoot = "Build";

        [MenuItem("Tools/Poké Lab/Build/Windows Player", priority = 300)]
        public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64, development: false);

        /// <summary>
        /// A development build: profiler, deep stack traces, and the debug jumps still armed.
        ///
        /// Kept separate rather than made the default. A development player answers "why did
        /// that happen"; a release player answers "does it work at all", and those are
        /// different questions on different days.
        /// </summary>
        [MenuItem("Tools/Poké Lab/Build/Windows Player (development)", priority = 301)]
        public static void BuildWindowsDev() => Build(BuildTarget.StandaloneWindows64, development: true);

        private static void Build(BuildTarget target, bool development)
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s != null && s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .Distinct()          // the list has carried a duplicate Boot entry before now
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("[Build] No scenes are enabled in the build settings, so the " +
                               "player would start on an empty screen. Run " +
                               "Tools/Poké Lab/Rebuild/Add Scenes To Build Settings first.");
                return;
            }

            // The first scene is what the player opens on, and the build settings' order is
            // not something anyone has deliberately curated — so it is stated here.
            var start = scenes.FirstOrDefault(p => p.EndsWith("/Town.unity", StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(start) && scenes[0] != start)
            {
                scenes = new[] { start }.Concat(scenes.Where(p => p != start)).ToArray();
            }

            var folder = Path.Combine(Directory.GetCurrentDirectory(), OutputRoot,
                development ? "Windows-dev" : "Windows");
            Directory.CreateDirectory(folder);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(folder, PlayerSettings.productName + ".exe"),
                target = target,
                options = development
                    ? BuildOptions.Development | BuildOptions.AllowDebugging
                    : BuildOptions.None,
            };

            Debug.Log($"[Build] {scenes.Length} scene(s), starting on '{scenes[0]}' → {folder}");

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[Build] Succeeded in {summary.totalTime.TotalSeconds:0}s, " +
                          $"{summary.totalSize / 1048576f:0.0} MB → {options.locationPathName}");
                return;
            }

            // Named individually, because "build failed" with a count is the least useful thing
            // a build log can say and the errors scroll past in the editor console.
            Debug.LogError($"[Build] {summary.result} — {summary.totalErrors} error(s).");
            foreach (var step in report.steps)
                foreach (var message in step.messages)
                    if (message.type == LogType.Error || message.type == LogType.Exception)
                        Debug.LogError($"[Build] {step.name}: {message.content}");
        }
    }
}
