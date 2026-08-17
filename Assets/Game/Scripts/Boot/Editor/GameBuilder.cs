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

        /// <summary>
        /// Where the web player is written, and the reason it is not <c>docs/</c>.
        ///
        /// GitHub Pages will serve either <c>docs/</c> on a branch or the root of a branch, and
        /// <c>docs/</c> is the obvious choice right up until you look at this repository. There
        /// is already a tracked <c>Docs/</c> here holding the design notes — GOAL, CONTRACTS,
        /// the art direction — and Pages looks for the name in lower case while Windows cannot
        /// tell the two apart. Building into <c>docs/</c> on this machine would write the player
        /// straight into <c>Docs/</c>, and the result is one of two silent failures: the
        /// internal notes get published alongside the game, or GitHub never finds a lower-case
        /// <c>docs</c> to serve and the page is a 404 with nothing anywhere saying why.
        ///
        /// So the player is built beside the Windows one, under the gitignored <c>Build/</c>,
        /// and published to the root of a <c>gh-pages</c> branch instead. That keeps the ~100 MB
        /// payload out of <c>main</c>'s history permanently, which matters on a repository
        /// already close to its LFS allowance: an orphan branch can be replaced wholesale on
        /// every deploy, where a commit on <c>main</c> is carried forever by every clone.
        /// </summary>
        private const string PagesRoot = "Build/WebGL";

        /// <summary>
        /// GitHub's hard limit on a single file in a repository. A push containing anything
        /// larger is rejected outright, so a web player with one oversized asset bundle is not
        /// a slow page — it is a page that can never be uploaded at all.
        /// </summary>
        private const long GitHubFileLimitBytes = 100L * 1024 * 1024;

        /// <summary>The published size of a Pages site that GitHub will still serve.</summary>
        private const long GitHubSiteLimitBytes = 1024L * 1024 * 1024;

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

        /// <summary>
        /// Builds the web player into <c>docs/</c>, ready for GitHub Pages to serve.
        ///
        /// Written to the served folder directly rather than to <c>Build/</c> and copied,
        /// because a copy step is a step that gets skipped: the page would then be whatever
        /// was built last time, and a web build that silently serves stale bytes is the same
        /// class of failure as a scene that was never added to the build settings.
        /// </summary>
        [MenuItem("Tools/Poké Lab/Build/WebGL", priority = 310)]
        public static void BuildWebGL()
        {
            var scenes = ResolveScenes();
            if (scenes == null) return;

            // Switched before the build rather than left to BuildPlayer, because the switch
            // reimports every texture in the project for the new target — several minutes of
            // work this project has a lot of. Doing it as its own step means a failure here
            // says "could not switch platform" instead of surfacing as a build error.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                Debug.Log("[Build] Switching the active platform to WebGL. Every texture is " +
                          "reimported for the new target, so this takes a while the first time.");

                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildTargetGroup.WebGL, BuildTarget.WebGL))
                {
                    Debug.LogError("[Build] Could not switch the active platform to WebGL. The " +
                                   "WebGL module is not installed for this editor version — add " +
                                   "it from Unity Hub under Installs → Add modules → WebGL Build " +
                                   "Support, then reopen the project.");
                    return;
                }
            }

            ConfigureForPages();

            var folder = Path.Combine(Directory.GetCurrentDirectory(), PagesRoot);
            ClearPreviousWebPlayer(folder);
            Directory.CreateDirectory(folder);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                // A folder, not a file: the web player is index.html plus Build and
                // TemplateData beside it, and Pages serves the folder as the site root.
                locationPathName = folder,
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
            };

            Debug.Log($"[Build] WebGL: {scenes.Length} scene(s), starting on '{scenes[0]}' → {folder}");

            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                ReportFailure(report);
                return;
            }

            WritePagesMetadata(folder);

            Debug.Log($"[Build] WebGL succeeded in {report.summary.totalTime.TotalSeconds:0}s → {folder}");
            ReportPagesFit(folder);
        }

        /// <summary>
        /// The compression and memory settings a GitHub Pages site can actually serve.
        ///
        /// This is the setting that decides whether the page loads at all. Unity's default
        /// compression writes Brotli or Gzip files and expects the web server to answer with a
        /// matching <c>Content-Encoding</c> header, so the browser knows to inflate them.
        /// GitHub Pages serves static files from a repository and offers no way to set that
        /// header — so with the fallback off, the browser receives compressed bytes labelled
        /// as plain ones, the loader fails to parse the wasm, and the page hangs on a progress
        /// bar with a console error and no other explanation. The project was carrying exactly
        /// that combination: Gzip with the fallback disabled.
        ///
        /// Enabling the decompression fallback makes the loader inflate the data in JavaScript
        /// when the header is absent, which is precisely the case here. It costs a few seconds
        /// of startup on a first visit and it is what makes the page work at all on a host that
        /// only serves files.
        ///
        /// Brotli over Gzip for two reasons: it is meaningfully smaller on wasm, and this
        /// repository's .gitattributes sends every <c>*.gz</c> to Git LFS — a Gzip build would
        /// put the whole player behind LFS pointers, and Pages serves the pointer text rather
        /// than the file it points at.
        /// </summary>
        private static void ConfigureForPages()
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.decompressionFallback = true;

            // Kept on: it caches the data file in IndexedDB so a return visit does not
            // re-download a payload this size, which matters far more here than usual.
            PlayerSettings.WebGL.dataCaching = true;
        }

        /// <summary>
        /// Removes the previous web player before writing a new one.
        ///
        /// A build does not delete what it does not overwrite, and the compression change makes
        /// that dangerous: an old <c>.data.gz</c> left beside a new <c>.data.br</c> is still
        /// served, still committed, and still counted against the site limit. Only Unity's own
        /// output is removed, so a CNAME or anything else placed in the served folder by hand
        /// survives a rebuild.
        /// </summary>
        private static void ClearPreviousWebPlayer(string folder)
        {
            foreach (var sub in new[] { "Build", "TemplateData" })
            {
                var path = Path.Combine(folder, sub);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }

            var index = Path.Combine(folder, "index.html");
            if (File.Exists(index)) File.Delete(index);
        }

        /// <summary>
        /// Writes the two files that make the output folder a working Pages site.
        ///
        /// <c>.nojekyll</c> stops Pages running the output through Jekyll, which silently drops
        /// files and folders whose names begin with an underscore. Unity names nothing that way
        /// today, but the failure it produces — one missing file, no error, a page that will not
        /// start — is not worth leaving to chance for an empty file.
        ///
        /// <c>.gitattributes</c> turns Git LFS off for everything beside it, and it is written
        /// here rather than assumed because of where these files are going. The player is
        /// published to the root of an orphan <c>gh-pages</c> branch, which does not inherit
        /// <c>main</c>'s <c>.gitattributes</c> — and <c>main</c>'s would be actively wrong here:
        /// it sends every <c>*.png</c> and <c>*.gz</c> to LFS, and GitHub Pages does not resolve
        /// LFS pointers. A tracked file arrives at the browser as the pointer text itself, so
        /// the page fetches a 130-byte string where the payload should be and fails to start.
        /// Shipping the rule with the build means the branch is correct however it is created.
        ///
        /// <c>-text</c> as well, because end-of-line conversion applied to a <c>.wasm</c> or
        /// <c>.data</c> file corrupts it into a load error with no other symptom.
        /// </summary>
        private static void WritePagesMetadata(string folder)
        {
            File.WriteAllText(Path.Combine(folder, ".nojekyll"), string.Empty);
            File.WriteAllText(Path.Combine(folder, ".gitattributes"),
                "# The published web player. GitHub Pages serves bytes and cannot resolve Git LFS\n" +
                "# pointers, so nothing on this branch may be tracked by LFS.\n" +
                "* -filter -diff -merge -text\n");
        }

        /// <summary>
        /// Says whether GitHub will actually host what was just built.
        ///
        /// Reported here rather than left to discover on push, because both limits fail late
        /// and badly: an oversized file is rejected by the push itself, after the build and
        /// after the commit, and an oversized site is accepted and then not served. The
        /// numbers are cheap to read off disk and expensive to learn the other way.
        /// </summary>
        private static void ReportPagesFit(string folder)
        {
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.Length)
                .ToArray();

            var total = files.Sum(f => f.Length);
            Debug.Log($"[Build] Web player is {total / 1048576f:0.0} MB across {files.Length} files. " +
                      "Largest: " + string.Join(", ", files.Take(3)
                          .Select(f => $"{f.Name} {f.Length / 1048576f:0.0} MB")));

            foreach (var file in files.Where(f => f.Length > GitHubFileLimitBytes))
            {
                Debug.LogError($"[Build] '{file.Name}' is {file.Length / 1048576f:0.0} MB, over " +
                               "GitHub's 100 MB per-file limit. The push will be rejected. Reduce " +
                               "texture sizes or move assets out of Resources before committing.");
            }

            if (total > GitHubSiteLimitBytes)
            {
                Debug.LogError($"[Build] The site is {total / 1048576f:0.0} MB, over GitHub Pages' " +
                               "1 GB limit. It will not be served.");
            }
        }

        /// <summary>
        /// The scenes to build, in the order the player will see them, or null if there are
        /// none. Shared by every target so a scene that reaches one player reaches them all.
        /// </summary>
        private static string[] ResolveScenes()
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
                return null;
            }

            // The first scene is what the player opens on, and the build settings' order is
            // not something anyone has deliberately curated — so it is stated here.
            var start = scenes.FirstOrDefault(p => p.EndsWith("/Town.unity", StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(start) && scenes[0] != start)
            {
                scenes = new[] { start }.Concat(scenes.Where(p => p != start)).ToArray();
            }

            return scenes;
        }

        private static void Build(BuildTarget target, bool development)
        {
            var scenes = ResolveScenes();
            if (scenes == null) return;

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

            ReportFailure(report);
        }

        /// <summary>
        /// Names every error the build produced.
        ///
        /// Individually, because "build failed" with a count is the least useful thing a build
        /// log can say and the errors scroll past in the editor console.
        /// </summary>
        private static void ReportFailure(BuildReport report)
        {
            var summary = report.summary;
            Debug.LogError($"[Build] {summary.result} — {summary.totalErrors} error(s).");
            foreach (var step in report.steps)
                foreach (var message in step.messages)
                    if (message.type == LogType.Error || message.type == LogType.Exception)
                        Debug.LogError($"[Build] {step.name}: {message.content}");
        }
    }
}
