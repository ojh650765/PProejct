using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace PokeLab.Audio.Editor
{
    /// <summary>
    /// Checks that GameMixer.mixer loaded and exposes what AudioDirector expects.
    ///
    /// The mixer asset was hand-authored as YAML rather than clicked together in the
    /// editor, so this exists to make any problem with it obvious and cheap. Nothing at
    /// runtime hard-depends on the mixer -- AudioDirector falls back to per-source gain
    /// when the field is empty -- so if this reports a failure the fix is a three minute
    /// rebuild, and the log below spells out exactly what to rebuild.
    /// </summary>
    public static class AudioMixerValidator
    {
        public const string MixerPath = "Assets/Game/Audio/GameMixer.mixer";

        private static readonly (string group, string param)[] Expected =
        {
            (AudioDirector.GroupMaster, AudioDirector.ParamMaster),
            (AudioDirector.GroupMusic, AudioDirector.ParamMusic),
            (AudioDirector.GroupSfx, AudioDirector.ParamSfx),
            (AudioDirector.GroupAmbience, AudioDirector.ParamAmbience),
            (AudioDirector.GroupUi, AudioDirector.ParamUi),
        };

        [MenuItem("Tools/Poke Lab/Audio/Validate Mixer")]
        public static void Validate()
        {
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
            if (mixer == null)
            {
                Debug.LogError($"[PokeLab.Audio] {MixerPath} did not load as an AudioMixer.\n{RebuildInstructions()}");
                return;
            }

            var problems = new List<string>();

            foreach (var (group, param) in Expected)
            {
                var found = mixer.FindMatchingGroups(group);
                if (found == null || found.Length == 0)
                    problems.Add($"missing group '{group}'");

                // SetFloat only succeeds on a parameter that is actually exposed, which
                // makes it the cheapest reliable existence check there is.
                if (!mixer.SetFloat(param, 0f))
                    problems.Add($"parameter '{param}' is not exposed");
                else
                    mixer.ClearFloat(param);
            }

            if (problems.Count == 0)
                Debug.Log("[PokeLab.Audio] Mixer valid: Master/Music/SFX/Ambience/UI groups " +
                          "present and all five volume parameters exposed.");
            else
                Debug.LogError($"[PokeLab.Audio] Mixer has {problems.Count} problem(s):\n" +
                               string.Join("\n", problems) + "\n\n" + RebuildInstructions());
        }

        private static string RebuildInstructions() =>
            "To rebuild by hand (nothing depends on the asset's GUID):\n" +
            "  1. Delete Assets/Game/Audio/GameMixer.mixer and its .meta.\n" +
            "  2. Assets > Create > Audio > Audio Mixer, name it GameMixer, in Assets/Game/Audio.\n" +
            "  3. Add four child groups of Master, named exactly: Music, SFX, Ambience, UI.\n" +
            "  4. For each of the five groups, right-click its Volume in the inspector and\n" +
            "     'Expose ... to script', then rename the exposed parameters to exactly:\n" +
            "     MasterVolume, MusicVolume, SfxVolume, AmbienceVolume, UiVolume.\n" +
            "  5. Assign the mixer to AudioDirector.mixer and re-run this validator.\n" +
            "Until then AudioDirector runs mixer-less and applies bus volume per source, so\n" +
            "the game is audible and correctly balanced either way.";
    }
}
