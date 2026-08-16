using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Refuses to let a hand-edited Unity asset carry YAML comments.
    ///
    /// Unity's serializer writes YAML but its reader is not a general YAML reader, and one
    /// of the things it does not accept is a comment. A `#` line in a .mat produces
    ///
    ///   Unable to parse file ...: [Parser Failure at line 77: Expect ':' between key and
    ///   value within mapping]
    ///
    /// and the material does not load — nor does anything depending on it, which in
    /// practice meant the whole terrain went untextured for a reason the error message
    /// does not name.
    ///
    /// This exists because the comments were worth writing. The terrain layer scales are
    /// tied to the feature sizes their textures were authored at, and someone changing one
    /// needs to know that; documenting it beside the number was the right instinct and the
    /// wrong file. The rule is simple enough to enforce: reasoning about an asset goes in a
    /// sibling `.notes.md`, never inside the asset.
    /// </summary>
    public static class AssetYamlGuard
    {
        private static readonly string[] Extensions = { ".mat", ".asset", ".prefab", ".unity" };

        private static readonly Regex CommentLine = new Regex(@"^[ \t]*#", RegexOptions.Compiled);

        [MenuItem("Tools/Poké Lab/Validate/Check Assets For YAML Comments")]
        public static void CheckAll()
        {
            var offenders = Scan();
            if (offenders.Count == 0)
            {
                Debug.Log("[Yaml] No Unity asset carries YAML comments.");
                return;
            }

            Debug.LogError(
                $"[Yaml] {offenders.Count} asset(s) contain '#' comments and will fail to " +
                "parse. Move the text to a sibling .notes.md:\n  " +
                string.Join("\n  ", offenders));
        }

        private static List<string> Scan()
        {
            var offenders = new List<string>();
            foreach (var path in Directory.EnumerateFiles("Assets", "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (System.Array.IndexOf(Extensions, extension) < 0) continue;

                // Text assets only. Prefabs and scenes can be serialized as binary, and a
                // binary file is full of bytes that look like anything you care to grep for
                // — the navmesh asset matches '#' eighty-two times and is not a text file.
                string first;
                try
                {
                    using var reader = new StreamReader(path);
                    first = reader.ReadLine();
                }
                catch (IOException) { continue; }

                if (first == null || !first.StartsWith("%YAML")) continue;

                var line = 0;
                foreach (var text in File.ReadLines(path))
                {
                    line++;
                    if (!CommentLine.IsMatch(text)) continue;
                    offenders.Add($"{path}:{line}");
                    break;
                }
            }
            return offenders;
        }
    }
}
